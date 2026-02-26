using BaseMacro.Platform;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static BaseMacro.SkyHookSystem;

#nullable enable

namespace BaseMacro
{
    /// <summary>
    /// 异步输入管理器（完全模拟 AsyncInputManager）
    /// </summary>
    public class AsyncInputManager
    {
        // 常量定义
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        // 事件队列
        private static readonly Queue<SkyHookEvent> eventQueue = new(256);
        private static readonly object queueLock = new();

        // 处理线程
        private static Thread? processingThread;
        private static bool isRunning = false;
        private static AutoResetEvent? eventSignal = new(false);

        // Windows API 声明
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // 性能计数器
        private static long totalEventsProcessed = 0;

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

            Macro.Log("[SkyHook] 正在启动输入系统...");

            // 确保事件信号被创建
            eventSignal = new AutoResetEvent(false);

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
            try
            {
                eventSignal?.Set();
            }
            catch { }

            // 等待线程结束
            if (processingThread != null && processingThread.IsAlive)
            {
                if (!processingThread.Join(1000))
                {
                    processingThread.Interrupt();
                }
            }

            // 清空队列
            lock (queueLock)
            {
                eventQueue.Clear();
            }

            Macro.Log($"[SkyHook] 输入系统已停止，共处理 {totalEventsProcessed} 个事件");
            processingThread = null;
        }

        /// <summary>
        /// 添加事件到队列
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvent(SkyHookEvent evt)
        {
            lock (queueLock)
            {
                eventQueue.Enqueue(evt);
            }

            // 唤醒处理线程
            try
            {
                eventSignal?.Set();
            }
            catch { }
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

            while (isRunning)
            {
                try
                {
                    // 等待事件或超时
                    bool signaled = eventSignal?.WaitOne(100) ?? false;

                    if (!isRunning) break;

                    // 处理所有待处理的事件
                    int batchProcessed = 0;

                    while (isRunning)
                    {
                        SkyHookEvent evt = default;
                        bool hasEvent = false;

                        lock (queueLock)
                        {
                            if (eventQueue.Count > 0)
                            {
                                evt = eventQueue.Dequeue();
                                hasEvent = true;
                            }
                        }

                        if (!hasEvent) break;

                        // 发送事件
                        SendInputFromSkyHook(evt);
                        totalEventsProcessed++;
                        batchProcessed++;
                        processedCount++;
                    }

                    // 每秒输出一次统计信息
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastLogTime).TotalSeconds >= 1)
                    {
                        if (processedCount > 0)
                        {
                            Macro.Log($"[SkyHook] 处理线程: 速率={processedCount}/s");
                            processedCount = 0;
                        }
                        lastLogTime = now;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe static void SendInputFromSkyHook(SkyHookEvent evt)
        {
            try
            {
                bool isDown = evt.Type == EventType.KeyPressed;

                // 使用高精度时间
                long currentTime = (long)BaseSelect.GetFileTime;

                // 获取扫描码
                ushort scanCode = (ushort)MapVirtualKey(evt.Key, 0);

                const uint KEYEVENTF_SCANCODE = 0x0008;

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
                    Macro.Log($"[SkyHook] SendInput 失败: Key=0x{evt.Key:X2} 错误码={error}");
                }
            }
            catch (Exception ex)
            {
                Macro.Log($"[SkyHook] SendInput 异常: {ex.Message}");
            }
        }
    }
}
