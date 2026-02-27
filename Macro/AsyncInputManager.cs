using BaseMacro.Platform;
using System;
using System.Runtime.CompilerServices;
using static BaseMacro.Macro.SkyHookSystem;

#nullable enable

namespace BaseMacro.Macro
{
    /// <summary>
    /// 异步输入管理器 - 使用 C++ DLL 实现高性能输入
    /// </summary>
    public static class AsyncInputManager
    {
        private static bool _isInitialized = false;

        // 性能计数器
        private static long _totalEventsProcessed = 0;
        private static long _totalEventsDropped = 0;

        /// <summary>
        /// 启动输入系统
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Start()
        {
            if (_isInitialized)
            {
                Macro.Log("[InputSystem] 输入系统已经在运行中");
                return;
            }

            try
            {
                // 重置计数器
                _totalEventsProcessed = 0;
                _totalEventsDropped = 0;
                _isInitialized = true;

                // 启动处理
                InputSystem.StartProcessing();

                Macro.Log("[InputSystem] 输入系统启动成功");
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 启动失败: {ex.Message}");
                _isInitialized = false;
            }
        }

        /// <summary>
        /// 停止输入系统
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Stop()
        {
            if (!_isInitialized) return;

            Macro.Log("[InputSystem] 正在停止输入系统...");

            try
            {
                // 紧急停止，清空队列
                InputSystem.EmergencyStop();

                // 停止处理
                InputSystem.StopProcessing();

                var status = InputSystem.GetStatus();
                Macro.Log($"[InputSystem] 已停止，队列剩余: {status.queueSize}, 已处理: {status.processedCount}");
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 停止失败: {ex.Message}");
            }
            finally
            {
                _isInitialized = false;
            }
        }

        /// <summary>
        /// 添加事件到队列
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvent(SkyHookEvent evt)
        {
            if (!_isInitialized) return;

            try
            {
                bool isDown = evt.Type == EventType.KeyPressed;

                // 直接调用 DLL 的 PushKeyEvent
                int result = InputSystem.PushKeyEvent((byte)evt.Key, isDown, 0);

                if (result == 0)
                {
                    System.Threading.Interlocked.Increment(ref _totalEventsProcessed);
                }
                else if (result == -2) // 队列满
                {
                    System.Threading.Interlocked.Increment(ref _totalEventsDropped);
                    Macro.Log($"[InputSystem] 警告：队列满，丢弃事件 {evt.Key}");
                }
                else
                {
                    Macro.Log($"[InputSystem] 推送事件失败: {result}");
                }
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 推送事件异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 批量添加事件 - 通过循环单个推送实现
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void EnqueueEvents(SkyHookEvent[] events)
        {
            if (!_isInitialized || events == null || events.Length == 0) return;

            try
            {
                int successCount = 0;
                for (int i = 0; i < events.Length; i++)
                {
                    bool isDown = events[i].Type == EventType.KeyPressed;
                    int result = InputSystem.PushKeyEvent((byte)events[i].Key, isDown, 0);

                    if (result == 0)
                    {
                        successCount++;
                    }
                    else if (result == -2)
                    {
                        System.Threading.Interlocked.Increment(ref _totalEventsDropped);
                    }
                }

                if (successCount > 0)
                {
                    System.Threading.Interlocked.Add(ref _totalEventsProcessed, successCount);
                }

                if (successCount < events.Length)
                {
                    Macro.Log($"[InputSystem] 批量推送部分失败: {successCount}/{events.Length}");
                }
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 批量推送异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 获取队列状态
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (int queueSize, long processed, long dropped) GetStats()
        {
            try
            {
                var status = InputSystem.GetStatus();
                return (status.queueSize, _totalEventsProcessed, _totalEventsDropped);
            }
            catch
            {
                return (0, _totalEventsProcessed, _totalEventsDropped);
            }
        }

        /// <summary>
        /// 清空队列
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void ClearQueue()
        {
            if (!_isInitialized) return;

            try
            {
                InputSystem.ClearQueue();
                Macro.Log("[InputSystem] 队列已清空");
            }
            catch (Exception ex)
            {
                Macro.Log($"[InputSystem] 清空队列失败: {ex.Message}");
            }
        }
    }
}