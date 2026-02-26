using BaseMacro.Platform;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using static BaseMacro.SkyHookSystem;

#nullable enable

namespace BaseMacro
{
    /// <summary>
    /// 异步输入管理器（完全模拟 AsyncInputManager）
    /// </summary>
    public class AsyncInputManagerUtils
    {
        // 常量定义
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;
        private const uint KEYEVENTF_SCANCODE = 0x0008;

        // 事件队列 - 使用 ConcurrentQueue 避免锁竞争
        private static readonly System.Collections.Concurrent.ConcurrentQueue<SkyHookEvent> eventQueue = new();

        // 处理线程
        private static Thread? processingThread;
        private static volatile bool isRunning = false;
        private static readonly ManualResetEventSlim eventSignal = new(false);

        // 批处理大小，避免单次处理过多
        private const int MAX_BATCH_SIZE = 64;

        // Windows API 声明
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // 性能计数器
        private static long totalEventsProcessed = 0;
        private static long totalEventsDropped = 0;
        private static readonly long maxQueueSize = 1024;

        /// <summary>
        /// 启动 SkyHook 输入系统
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Start()
        {
            if (isRunning)
            {
                Macro.Log("[SkyHook] 输入系统已经在运行中");
                return;
            }

            isRunning = true;
            totalEventsProcessed = 0;
            totalEventsDropped = 0;

            Macro.Log("[SkyHook] 正在启动输入系统...");

            // 启动高优先级处理线程
            processingThread = new Thread(ProcessEventQueue)
            {
                Name = "SkyHookProcessor",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            processingThread.Start();

            Macro.Log("[SkyHook] 处理线程已启动");
        }

        /// <summary>
        /// 停止 SkyHook 输入系统
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Stop()
        {
            Macro.Log("[SkyHook] 正在停止输入系统...");
            isRunning = false;

            // 唤醒线程让其退出
            eventSignal.Set();

            // 等待线程结束
            if (processingThread != null && processingThread.IsAlive)
            {
                if (!processingThread.Join(1000))
                {
                    processingThread.Interrupt();
                }
            }

            // 清空队列
            while (eventQueue.TryDequeue(out _)) { }

            Macro.Log($"[SkyHook] 输入系统已停止，共处理 {totalEventsProcessed} 个事件，丢弃 {totalEventsDropped} 个");
            processingThread = null;
        }

        /// <summary>
        /// 添加事件到队列
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvent(SkyHookEvent evt)
        {
            // 队列保护：如果队列太长，丢弃旧事件
            if (eventQueue.Count > maxQueueSize)
            {
                // 尝试丢弃最旧的10个事件
                for (int i = 0; i < 10; i++)
                {
                    if (eventQueue.TryDequeue(out _))
                        Interlocked.Increment(ref totalEventsDropped);
                    else
                        break;
                }
                Macro.Log($"[SkyHook] 警告：队列积压 {eventQueue.Count}，已丢弃部分事件");
            }

            eventQueue.Enqueue(evt);
            eventSignal.Set();
        }

        /// <summary>
        /// 处理事件队列的线程
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProcessEventQueue()
        {
            Macro.Log("[SkyHook] 处理线程开始运行");

            int processedCount = 0;
            DateTime lastLogTime = DateTime.UtcNow;

            // 预分配批处理数组，减少GC
            var batchEvents = new SkyHookEvent[MAX_BATCH_SIZE];

            while (isRunning)
            {
                try
                {
                    // 等待事件信号（最多100ms）
                    eventSignal.Wait(100);

                    if (!isRunning) break;

                    int batchProcessed = 0;

                    // 批量取出事件
                    while (batchProcessed < MAX_BATCH_SIZE && isRunning)
                    {
                        if (eventQueue.TryDequeue(out SkyHookEvent evt))
                        {
                            batchEvents[batchProcessed] = evt;
                            batchProcessed++;
                        }
                        else
                        {
                            break;
                        }
                    }

                    // 批量处理事件
                    if (batchProcessed > 0)
                    {
                        // 预处理：合并相同按键的连续事件
                        batchProcessed = MergeEvents(batchEvents, batchProcessed);

                        // 发送事件
                        for (int i = 0; i < batchProcessed; i++)
                        {
                            SendInputFromSkyHook(batchEvents[i]);
                        }

                        Interlocked.Add(ref totalEventsProcessed, batchProcessed);
                        processedCount += batchProcessed;
                    }

                    // 每秒输出一次统计信息
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastLogTime).TotalSeconds >= 1)
                    {
                        if (processedCount > 0 || totalEventsDropped > 0)
                        {
                            Macro.Log($"[SkyHook] 处理线程: 速率={processedCount}/s, 队列={eventQueue.Count}, 丢弃={totalEventsDropped}");
                            processedCount = 0;
                        }
                        lastLogTime = now;
                    }

                    // 如果这一批处理了很多事件，让出CPU
                    if (batchProcessed > 32)
                    {
                        Thread.Sleep(1);
                    }
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Macro.Log($"[SkyHook] 处理线程异常: {ex.Message}");
                    Thread.Sleep(10);
                }
            }

            Macro.Log("[SkyHook] 处理线程结束");
        }

        /// <summary>
        /// 合并相同按键的连续事件（减少SendInput调用）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int MergeEvents(SkyHookEvent[] events, int count)
        {
            if (count < 2) return count;

            List<SkyHookEvent> result = new(count)
            {
                events[0]
            };

            for (int i = 1; i < count; i++)
            {
                SkyHookEvent last = result[result.Count - 1];
                SkyHookEvent current = events[i];

                // 如果是同一个按键的相反操作，且时间差小于1ms
                if (last.Key == current.Key &&
                    last.Type != current.Type)
                {
                    long timeDiff = Math.Abs(current.TimeSec * 1000000000L + current.TimeSubsecNano -
                                            (last.TimeSec * 1000000000L + last.TimeSubsecNano));

                    if (timeDiff < 1000000) // 1ms
                    {
                        // 合并：移除上一个（相当于不添加当前）
                        result.RemoveAt(result.Count - 1);
                        continue;
                    }
                }

                result.Add(current);
            }

            // 复制回原数组
            for (int i = 0; i < result.Count; i++)
            {
                events[i] = result[i];
            }
            return result.Count;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void SendInputFromSkyHook(SkyHookEvent evt)
        {
            try
            {
                bool isDown = evt.Type == EventType.KeyPressed;

                // 获取扫描码
                ushort scanCode = (ushort)MapVirtualKey(evt.Key, 0);

                INPUT input = new()
                {
                    type = INPUT_KEYBOARD,
                    u = new InputUnion
                    {
                        ki = new KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scanCode,
                            dwFlags = (isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP) | KEYEVENTF_SCANCODE,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };

                uint result = SendInput(1, [input], Marshal.SizeOf(typeof(INPUT)));

                if (result != 1)
                {
                    int error = Marshal.GetLastWin32Error();
                    // 只记录重要错误，避免刷屏
                    if (error != 0)
                    {
                        Macro.Log($"[SkyHook] SendInput 失败: Key=0x{evt.Key:X2} 错误码={error}");
                    }
                }
            }
            catch (Exception ex)
            {
                Macro.Log($"[SkyHook] SendInput 异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取队列状态
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int queueSize, long processed, long dropped) GetStats()
        {
            return (eventQueue.Count, totalEventsProcessed, totalEventsDropped);
        }
    }
}