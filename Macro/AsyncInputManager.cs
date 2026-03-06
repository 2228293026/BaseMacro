using BaseMacro.Platform;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static BaseMacro.Macro.SkyHookSystem;

#nullable enable

namespace BaseMacro.Macro
{
    public static class AsyncInputManager
    {
        // ══════════════════════════════════════════════════════
        //  Win32
        // ══════════════════════════════════════════════════════
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint p);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint p);

        // ══════════════════════════════════════════════════════
        //  环形缓冲区
        //  8192 而非 4096：更大的缓冲区在突发场景下减少丢弃
        // ══════════════════════════════════════════════════════
        private const int BUFFER_SIZE = 8192;
        private const int BUFFER_MASK = BUFFER_SIZE - 1;

        // SkyHookEvent 数组：结构体数组，内存连续，缓存友好
        private static readonly SkyHookEvent[] _ring = new SkyHookEvent[BUFFER_SIZE];

        // 读写指针：volatile 保证可见性
        // 不用 PaddedIndices 结构体（Mono FieldOffset 访问比直接字段慢）
        private static volatile int _writeIndex = 0;
        private static volatile int _readIndex = 0;

        private static volatile bool _isInitialized = false;
        private static volatile bool _isRunning = false;
        private static Thread? _workerThread;

        // 统计（工作线程独占写，无需 Interlocked）
        private static long _totalProcessed = 0;
        private static long _totalDropped = 0;

        public static bool IsInitialized => _isInitialized;

        // ══════════════════════════════════════════════════════
        //  启动
        // ══════════════════════════════════════════════════════
        public static void Start()
        {
            if (_isInitialized)
            {
                Macro.Log("[InputSystem] 已在运行中");
                return;
            }

            try
            {
                // ① 时钟精度 15.6ms → 1ms
                //    SpinWait 退化到 Sleep(1) 时，精度从 15.6ms 变成 1ms
                //    这是 4000/s 稳定性的基础保障
                timeBeginPeriod(1);

                // ② GC 低延迟模式
                //    SustainedLowLatency：允许 Gen0/1 GC，但抑制 Gen2 阻塞式 GC
                //    避免 50-200ms 的 Stop-the-World 打断工作线程
                System.Runtime.GCSettings.LatencyMode =
                    System.Runtime.GCLatencyMode.SustainedLowLatency;

                // ③ 提前触发一次完整 GC，清理堆，减少运行期 GC 概率
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, blocking: true);

                _writeIndex = 0;
                _readIndex = 0;
                _totalProcessed = 0;
                _totalDropped = 0;

                InputSystem.StartProcessing();

                _isRunning = true;
                _isInitialized = true;

                _workerThread = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "InputSystem-Worker"
                };
                _workerThread.Start();

                Macro.Log("[InputSystem] 启动成功");
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 启动失败: {ex.Message}");
                _isRunning = false;
                _isInitialized = false;
                timeEndPeriod(1);
            }
        }

        // ══════════════════════════════════════════════════════
        //  停止
        // ══════════════════════════════════════════════════════
        public static void Stop()
        {
            if (!_isInitialized) return;

            _isRunning = false;
            _workerThread?.Join(TimeSpan.FromSeconds(2));
            _workerThread = null;

            InputSystem.EmergencyStop();
            InputSystem.StopProcessing();

            System.Runtime.GCSettings.LatencyMode =
                System.Runtime.GCLatencyMode.Interactive;
            timeEndPeriod(1);

            _isInitialized = false;
            Macro.Log($"[InputSystem] 已停止 | 处理: {_totalProcessed} | 丢弃: {_totalDropped}");
        }

        // ══════════════════════════════════════════════════════
        //  工作线程
        // ══════════════════════════════════════════════════════
        private static void WorkerLoop()
        {
            Macro.Log("[InputSystem] 工作线程启动");

            // ④ 锁定 CLR 不迁移到其他 OS 线程
            //    防止线程迁移导致 CPU 缓存失效，Unity 安全，无需 P/Invoke
            Thread.BeginThreadAffinity();

            // ⑤ JIT 预热：让热路径在正式运行前完成编译
            //    避免第一批事件触发 JIT 编译导致的初始抖动
            WarmUp();

            try
            {
                ConsumeLoop();
            }
            finally
            {
                Thread.EndThreadAffinity();
                Macro.Log("[InputSystem] 工作线程退出");
            }
        }

        // ⑤ JIT 预热：空跑一次所有热路径代码，触发 JIT 编译
        [MethodImpl(MethodImplOptions.NoInlining)] // 不内联，确保独立 JIT
        private static void WarmUp()
        {
            // 空跑 PushKeyEvent（用不存在的 key，DLL 会忽略或报错，但 JIT 已完成）
            InputSystem.PushKeyEvent(0xFF, false, 0);

            // 空跑环形缓冲区读取逻辑
            int r = _readIndex;
            int w = _writeIndex;
            if (r != w) // 一定为 false（刚启动），但 JIT 会编译这段代码
            {
                ref readonly SkyHookEvent e = ref _ring[r & BUFFER_MASK];
                InputSystem.PushKeyEvent((byte)e.Key, e.Type == EventType.KeyPressed, 0);
            }
        }

        // ⑥ 消费主循环：单独提取为方法，让 JIT 对其独立优化
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ConsumeLoop()
        {
            var spinner = new SpinWait();

            // 用局部变量缓存读指针，避免每次循环重复读 volatile 字段
            int localRead = _readIndex;

            while (_isRunning)
            {
                int localWrite = _writeIndex; // volatile 读，感知生产者写入

                if (localRead == localWrite)
                {
                    // 队列空：SpinWait 自适应等待
                    // 在 timeBeginPeriod(1) 下，即使退化到 Sleep(1) 也只有 1ms
                    spinner.SpinOnce();
                    continue;
                }

                // 有数据：重置自旋计数，进入消费模式
                spinner.Reset();

                // ⑦ 批量消费：内层循环不做任何 volatile/Interlocked 操作
                //    全程用局部变量 localRead/localWrite，最后一次性提交
                while (localRead != localWrite)
                {
                    ref readonly SkyHookEvent evt = ref _ring[localRead & BUFFER_MASK];

                    int result = InputSystem.PushKeyEvent(
                        (byte)evt.Key,
                        evt.Type == EventType.KeyPressed,
                        0);

                    // ⑧ 统计用局部变量累加，退出循环后一次性写回
                    //    避免每次 Interlocked 操作
                    if (result == 0) _totalProcessed++;
                    else if (result == -2) _totalDropped++;

                    localRead++;

                    // 每消费 16 个事件刷新一次 localWrite
                    // 平衡"感知新事件"和"volatile 读开销"
                    if ((localRead & 15) == 0)
                        localWrite = _writeIndex;
                }

                // ⑦ 一次性提交读指针（整批只有这一次 Interlocked）
                Interlocked.Exchange(ref _readIndex, localRead);
            }
        }

        // ══════════════════════════════════════════════════════
        //  生产者入队
        // ══════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvent(SkyHookEvent evt)
        {
            if (!_isInitialized) return;

            // 生产者私有：_writeIndex 只有生产者写，读不需要 Volatile
            int write = _writeIndex;
            int read = _readIndex; // volatile 读，感知消费者进度

            if ((write - read) >= BUFFER_SIZE)
            {
                _totalDropped++;
                return;
            }

            _ring[write & BUFFER_MASK] = evt;

            // 写屏障：保证数据写入对消费者可见后再移动指针
            Interlocked.Increment(ref _writeIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvents(SkyHookEvent[] events)
        {
            if (!_isInitialized || events == null || events.Length == 0) return;

            // 批量入队：一次检查空间，批量写入，一次提交指针
            int write = _writeIndex;
            int read = _readIndex;
            int space = BUFFER_SIZE - (write - read);
            int count = Math.Min(events.Length, space);

            _totalDropped += events.Length - count;

            for (int i = 0; i < count; i++)
                _ring[(write + i) & BUFFER_MASK] = events[i];

            Interlocked.Add(ref _writeIndex, count);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearQueue()
        {
            Interlocked.Exchange(ref _readIndex, _writeIndex);
            Macro.Log("[InputSystem] 队列已清空");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int queueSize, long processed, long dropped) GetStats() =>
            (_writeIndex - _readIndex, _totalProcessed, _totalDropped);
    }
}