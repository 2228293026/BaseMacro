using BaseMacro;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#nullable enable

namespace BaseMacro
{

    #region TimeBasedMacro

    /// <summary>
    /// 支持 SkyHook
    /// </summary>
    internal static class Macro
    {
        // 原始字段
        private static double[]? triggerTimes;
        private static int lastTriggeredFloor = -1;
        private static int floorCount;
        private static bool initialized = false;
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;
        private static List<scrFloor>? cachedFloors;
        private static readonly List<byte> keyCodes = new(4);
        private static int keyIndex = 0;
        private static byte? pendingKey = null;
        private static bool isKeyDown = false;
        private static string lastKeysSetting = "";
        private static readonly byte[] scanCodeCache = new byte[256];

        // 原始 SendInput 相关
        private struct KeyEvent { public byte keyCode; public bool isDown; public void Reset() { keyCode = 0; isDown = false; } }
        private static KeyEvent[] pendingKeyEvents = new KeyEvent[32];
        private static int pendingKeyCount = 0;
        private static readonly SkyHookSystem.INPUT[] inputs = new SkyHookSystem.INPUT[32];

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, SkyHookSystem.INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // SkyHook 模式相关
        private static long startTimeTicks;
        private static bool skyHookInitialized = false;

        // 高精度计时器
        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);

        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);

        private static readonly long perfFrequency;
        private static readonly bool usePerfCounter = false;

        // 键名映射表
        private static readonly Dictionary<string, byte> KeyNameToCode = new()
        {
            ["A"] = 0x41,
            ["B"] = 0x42,
            ["C"] = 0x43,
            ["D"] = 0x44,
            ["E"] = 0x45,
            ["F"] = 0x46,
            ["G"] = 0x47,
            ["H"] = 0x48,
            ["I"] = 0x49,
            ["J"] = 0x4A,
            ["K"] = 0x4B,
            ["L"] = 0x4C,
            ["M"] = 0x4D,
            ["N"] = 0x4E,
            ["O"] = 0x4F,
            ["P"] = 0x50,
            ["Q"] = 0x51,
            ["R"] = 0x52,
            ["S"] = 0x53,
            ["T"] = 0x54,
            ["U"] = 0x55,
            ["V"] = 0x56,
            ["W"] = 0x57,
            ["X"] = 0x58,
            ["Y"] = 0x59,
            ["Z"] = 0x5A,
            ["0"] = 0x30,
            ["1"] = 0x31,
            ["2"] = 0x32,
            ["3"] = 0x33,
            ["4"] = 0x34,
            ["5"] = 0x35,
            ["6"] = 0x36,
            ["7"] = 0x37,
            ["8"] = 0x38,
            ["9"] = 0x39,
            ["F1"] = 0x70,
            ["F2"] = 0x71,
            ["F3"] = 0x72,
            ["F4"] = 0x73,
            ["F5"] = 0x74,
            ["F6"] = 0x75,
            ["F7"] = 0x76,
            ["F8"] = 0x77,
            ["F9"] = 0x78,
            ["F10"] = 0x79,
            ["F11"] = 0x7A,
            ["F12"] = 0x7B,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["CTRL"] = 0x11,
            ["LCTRL"] = 0xA2,
            ["RCTRL"] = 0xA3,
            ["SHIFT"] = 0x10,
            ["LSHIFT"] = 0xA0,
            ["RSHIFT"] = 0xA1,
            ["ALT"] = 0x12,
            ["LALT"] = 0xA4,
            ["RALT"] = 0xA5,
            ["WIN"] = 0x5B,
            ["LWIN"] = 0x5B,
            ["RWIN"] = 0x5C,
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D,
            ["ESC"] = 0x1B,
            ["TAB"] = 0x09,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22
        };
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Macro()
        {
            usePerfCounter = QueryPerformanceFrequency(out perfFrequency);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetAudioSyncTicks()
        {
            if (Main.Settings.HighPrecisionTime)
            {
                // 使用 AudioDSPManager 获取音频同步的时间
                return AudioDSPManager.GetDSPTimeAsFileTime();
            }
            else
            {
                // 使用原来的方法
                return GetPreciseTicks();
            }
        }
        /// <summary>
        /// 获取高精度时间（100ns单位）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetPreciseTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long counter))
            {
                return (counter * 10000000) / perfFrequency;
            }
            return DateTime.UtcNow.Ticks;
        }

        /// <summary>
        /// 解析按键配置
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && keyCodes.Count > 0) return;

            lastKeysSetting = keysSetting;
            keyCodes.Clear();

            string[] parts = keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string keyName = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(keyName)) continue;

                if (keyName.Length == 1)
                {
                    char c = keyName[0];
                    if (c >= 'A' && c <= 'Z') { keyCodes.Add((byte)c); continue; }
                    if (c >= '0' && c <= '9') { keyCodes.Add((byte)c); continue; }
                }

                if (KeyNameToCode.TryGetValue(keyName, out byte code))
                {
                    keyCodes.Add(code);
                }
            }

            if (keyCodes.Count == 0) keyCodes.Add(0x4A);
            keyIndex = 0;
        }

        /// <summary>
        /// 初始化触发点
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Initialize()
        {
            levelMaker = scrLevelMaker.instance;
            if (levelMaker?.listFloors == null || levelMaker.listFloors.Count == 0) return;

            cachedFloors = levelMaker.listFloors;
            floorCount = cachedFloors.Count;
            triggerTimes = new double[floorCount];

            for (int i = 0; i < floorCount - 1; i++)
            {
                triggerTimes[i] = cachedFloors[i + 1]?.entryTime ?? double.MaxValue;
            }
            triggerTimes[floorCount - 1] = double.MaxValue;

            conductor = scrConductor.instance;
            initialized = true;

            ParseKeyCodes();

            if (conductor != null)
            {
                SyncLastTriggeredFloor(conductor.songposition_minusi);
            }
        }

        /// <summary>
        /// 同步当前触发点
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SyncLastTriggeredFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Length == 0) return;

            int left = 0, right = triggerTimes.Length - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (triggerTimes[mid] < currentTime) left = mid + 1;
                else if (triggerTimes[mid] > currentTime) right = mid - 1;
                else { lastTriggeredFloor = mid; return; }
            }
            lastTriggeredFloor = left - 1;
        }

        /// <summary>
        /// 检查是否需要重新初始化
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize()
        {
            var lm = levelMaker ?? scrLevelMaker.instance;
            return lm?.listFloors == null || lm.listFloors.Count != floorCount;
        }

        // ==================== 输入方法 ====================

        /// <summary>
        /// SendInput 模式：队列事件
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void QueueSendInputEvent(byte keyCode, bool isDown)
        {
            if (pendingKeyCount >= pendingKeyEvents.Length)
            {
                var newArray = new KeyEvent[pendingKeyEvents.Length * 2];
                Array.Copy(pendingKeyEvents, newArray, pendingKeyEvents.Length);
                pendingKeyEvents = newArray;
            }
            pendingKeyEvents[pendingKeyCount].keyCode = keyCode;
            pendingKeyEvents[pendingKeyCount].isDown = isDown;
            pendingKeyCount++;
        }

        /// <summary>
        /// 更新按键状态 - 修复版
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateKeyState(byte? newKey)
        {
            // 如果有按下的键，需要先松开
            if (isKeyDown && pendingKey.HasValue)
            {
                // 如果新按键为null（纯松开），或者切换到不同按键
                if (!newKey.HasValue || newKey.Value != pendingKey.Value)
                {
                    Log($"[TimeBasedMacro] UpdateKeyState: 松开按键 0x{pendingKey.Value:X2}");

                    if (Main.Settings.SkyHookMode)
                    {
                        // SkyHook模式：立即发送松开事件
                        long now = GetAudioSyncTicks();
                        long elapsed = now - startTimeTicks;
                        double elapsedSeconds = elapsed / 10000000.0;

                        Log($"[TimeDebug] UpdateKeyState: 松开按键 0x{pendingKey.Value:X2} 时间戳={elapsedSeconds:F6}s");

                        var evt = SkyHookSystem.SkyHookEvent.Create(pendingKey.Value, false, elapsed);
                        AsyncInputManager.EnqueueEvent(evt);
                        Log($"[TimeBasedMacro] SkyHook松开已入队: Key=0x{pendingKey.Value:X2}");
                    }
                    else
                    {
                        // SendInput模式：加入队列
                        QueueSendInputEvent(pendingKey.Value, false);
                    }

                    isKeyDown = false;
                    pendingKey = null;
                }
            }

            // 按下新按键
            if (newKey.HasValue && (!isKeyDown || newKey.Value != pendingKey))
            {
                Log($"[TimeBasedMacro] UpdateKeyState: 按下按键 0x{newKey.Value:X2}");

                if (Main.Settings.SkyHookMode)
                {
                    // SkyHook模式：立即发送按下事件
                    long now = GetAudioSyncTicks();
                    long elapsed = now - startTimeTicks;
                    double elapsedSeconds = elapsed / 10000000.0;

                    Log($"[TimeDebug] UpdateKeyState: 按下按键 0x{newKey.Value:X2} 时间戳={elapsedSeconds:F6}s");
                    var evt = SkyHookSystem.SkyHookEvent.Create(newKey.Value, true, elapsed);
                    AsyncInputManager.EnqueueEvent(evt);
                    Log($"[TimeBasedMacro] SkyHook按下已入队: Key=0x{newKey.Value:X2}");
                }
                else
                {
                    // SendInput模式：加入队列
                    QueueSendInputEvent(newKey.Value, true);
                }

                pendingKey = newKey;
                isKeyDown = true;
            }
        }

        /// <summary>
        /// 发送 SendInput 事件
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FlushSendInputEvents()
        {
            if (pendingKeyCount == 0) return;

            SkyHookSystem.INPUT[] inputsToSend;
            bool usePool = false;

            if (pendingKeyCount <= inputs.Length)
            {
                inputsToSend = inputs;
            }
            else
            {
                inputsToSend = ArrayPool<SkyHookSystem.INPUT>.Shared.Rent(pendingKeyCount);
                usePool = true;
            }

            for (int i = 0; i < pendingKeyCount; i++)
            {
                ref var evt = ref pendingKeyEvents[i];
                byte scanCode = scanCodeCache[evt.keyCode];
                if (scanCode == 0)
                {
                    scanCode = (byte)MapVirtualKey(evt.keyCode, 0);
                    scanCodeCache[evt.keyCode] = scanCode;
                }

                inputsToSend[i].type = INPUT_KEYBOARD;
                inputsToSend[i].u.ki = new SkyHookSystem.KEYBDINPUT
                {
                    wVk = evt.keyCode,
                    wScan = scanCode,
                    dwFlags = evt.isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                };
            }

            SendInput((uint)pendingKeyCount, inputsToSend, Marshal.SizeOf(typeof(SkyHookSystem.INPUT)));

            if (usePool)
            {
                ArrayPool<SkyHookSystem.INPUT>.Shared.Return(inputsToSend);
            }

            pendingKeyCount = 0;
        }

        /// <summary>
        /// 刷新所有事件
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FlushKeyEvents()
        {
            if (Main.Settings.SkyHookMode)
            {
                // SkyHook 模式不需要额外操作，事件已单独发送
                if (pendingKeyCount > 0) pendingKeyCount = 0;
            }
            else
            {
                FlushSendInputEvents();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyHoldBehavior(scrController controller)
        {
            if (controller == null) return;

            if (Main.Settings.Macro)
            {
                controller.requireHolding = Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
                if (!Main.Settings.SimulateKeyPress)
                {
                    controller.requireHolding = false;
                    Log($"[TimeBasedMacro] 强制设置 requireHolding = false");
                }
            }
        }

        // ==================== 公共接口 ====================

        /// <summary>
        /// 重置宏状态
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset(scrController controller)
        {
            lastTriggeredFloor = -1;
            initialized = false;
            triggerTimes = null;
            cachedFloors = null;
            levelMaker = null;
            conductor = null;

            if (isKeyDown && pendingKey.HasValue)
            {
                UpdateKeyState(null);
                FlushKeyEvents();
            }
            pendingKey = null;
            isKeyDown = false;
            keyIndex = 0;

            if (pendingKeyCount > 0)
            {
                Array.Clear(pendingKeyEvents, 0, pendingKeyCount);
                pendingKeyCount = 0;
            }

            if (Main.Settings.SkyHookMode)
            {
                startTimeTicks = GetAudioSyncTicks();
            }
            if (Main.Settings.HighPrecisionTime)
            {
                AudioDSPManager.Reset();
            }
            ApplyHoldBehavior(controller);
        }


        /// <summary>
        /// 更新宏 - 修复版
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            var settings = Main.Settings;

            // 检查模式切换
            if (settings.SkyHookMode != skyHookInitialized)
            {
                SwitchMode(settings.SkyHookMode);
            }

            if (!settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
            {
                // 如果宏关闭但 SkyHook 还在运行，停止它
                if (!settings.Macro && skyHookInitialized)
                {
                    AsyncInputManager.Stop();
                    skyHookInitialized = false;
                }
                return;
            }

            if (!initialized || NeedReinitialize())
            {
                Reset(controller);
                Initialize();
                if (!initialized) return;
            }

            var cond = conductor;
            var floors = cachedFloors;
            var times = triggerTimes;
            if (cond == null || floors == null || times == null) return;

            double currentTime = cond.songposition_minusi;
            double pitch = cond.song.pitch;
            double nextFrameTime = currentTime + (Time.unscaledDeltaTime * pitch);
            double timeOffsetMs = settings.TimeOffset * 0.001;

            int startFloor = lastTriggeredFloor + 1;
            bool simulateKeyPress = settings.SimulateKeyPress;

            int triggerCount = times.Length;
            for (int i = startFloor; i < triggerCount; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;

                if (floor.nextfloor?.auto == true || floor.midSpin)
                {
                    lastTriggeredFloor = i;
                    continue;
                }

                double adjustedTrigger = times[i] + timeOffsetMs;
                Log($"[TimeDebug] 地板 {i}: entryTime={times[i]:F6}s, 偏移={timeOffsetMs * 1000:F2}ms, 调整后={adjustedTrigger:F6}s");
                Log($"[TimeDebug] 当前时间={currentTime:F6}s, 下一帧时间={nextFrameTime:F6}s, 差值={adjustedTrigger - currentTime:F6}s");
                if (adjustedTrigger > nextFrameTime) break;
                if (i <= lastTriggeredFloor) continue;

                bool releaseOnly = false;
                if (simulateKeyPress && floor.holdLength > -1 && i + 1 < triggerCount)
                {
                    var nextFloor = floors[i + 1];
                    if (nextFloor != null && nextFloor.holdLength == -1)
                    {
                        releaseOnly = true;
                        Log($"[TimeBasedMacro] 地板 {i}: 长按结束，需要松开按键");
                    }
                }

                if (!simulateKeyPress)
                {
                    controller.Hit(false);
                }
                else if (releaseOnly)
                {
                    // 长按结束：只松开，不按下新键
                    if (isKeyDown && pendingKey.HasValue)
                    {
                        Log($"[TimeBasedMacro] 地板 {i}: 松开按键 0x{pendingKey.Value:X2}");
                        UpdateKeyState(null); // 松开当前按键
                    }
                    if (i + 1 > lastTriggeredFloor)
                    {
                        lastTriggeredFloor = i + 1;
                    }
                }
                else if (simulateKeyPress && keyCodes.Count > 0)
                {
                    byte key = keyCodes[keyIndex];
                    Log($"[TimeBasedMacro] 地板 {i}: 触发按键 0x{key:X2} (索引 {keyIndex})");

                    // 这里会自动处理：如果之前有按下的键，会先松开再按下新的
                    UpdateKeyState(key);

                    keyIndex = (keyIndex + 1) % keyCodes.Count;
                }

                lastTriggeredFloor = i;
            }

            // 发送所有待处理的事件
            if (!settings.SkyHookMode && pendingKeyCount > 0)
            {
                FlushSendInputEvents();
            }

            // 保险机制：如果宏结束但还有键按着，强制释放
            if (isKeyDown && pendingKey.HasValue && lastTriggeredFloor >= triggerCount - 1)
            {
                Log($"[TimeBasedMacro] 宏结束，强制释放按键 0x{pendingKey.Value:X2}");
                UpdateKeyState(null);
                if (!settings.SkyHookMode && pendingKeyCount > 0)
                {
                    FlushSendInputEvents();
                }
            }
        }
        private static void SwitchMode(bool useSkyHook)
        {
            // 如果已经是目标模式，直接返回
            if (useSkyHook == skyHookInitialized) return;

            Log($"[TimeBasedMacro] 切换模式: {(useSkyHook ? "SkyHook" : "SendInput")}");

            if (useSkyHook)
            {
                // 切换到 SkyHook 模式
                if (!skyHookInitialized)
                {
                    AsyncInputManager.Start();
                    skyHookInitialized = true;
                }
                // 记录开始时间基准
                startTimeTicks = GetAudioSyncTicks();
                Main.Settings.SkyHookMode = true;
                Log("[TimeBasedMacro] 切换到 SkyHook 模式（时间精确模式）");
            }
            else
            {
                // 切换到 SendInput 模式
                if (skyHookInitialized)
                {
                    AsyncInputManager.Stop();
                    skyHookInitialized = false;
                }
                Main.Settings.SkyHookMode = false;
                Log("[TimeBasedMacro] 切换到 SendInput 模式（即时模式）");
            }

            // 清理状态
            if (isKeyDown && pendingKey.HasValue)
            {
                // 立即松开按键
                if (useSkyHook)
                {
                    long now = GetAudioSyncTicks();
                    long elapsed = now - startTimeTicks;
                    var evt = SkyHookSystem.SkyHookEvent.Create(pendingKey.Value, false, elapsed);
                    AsyncInputManager.EnqueueEvent(evt);
                }
                else
                {
                    QueueSendInputEvent(pendingKey.Value, false);
                    FlushSendInputEvents();
                }
                isKeyDown = false;
                pendingKey = null;
            }
        }


        /// <summary>
        /// 处理输入调整
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HandleInput()
        {
            if (!Main.Settings.Macro) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl && Main.Settings.EnableKeyAdjust)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                }
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                }
            }
            else if (!ctrl && Main.Settings.EnableArrowTimeAdjust)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                }
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    Main.Settings.TimeOffset += Main.Settings.AdjustStep;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Log(string message)
        {
            Main.Mod?.Logger.Log(message);
        }
    }

    #endregion
}