using BaseMacro;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#nullable enable

namespace BaseMacro.Macro
{

    #region TimeBasedMacro

    /// <summary>
    /// 支持 SkyHook - 性能优化版
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

        // 扫描码缓存 - 预填充
        private static readonly byte[] scanCodeCache = new byte[256];

        // 原始 SendInput 相关
        private struct KeyEvent
        {
            public byte keyCode;
            public bool isDown;
            public void Reset() { keyCode = 0; isDown = false; }
        }

        private static KeyEvent[] pendingKeyEvents = new KeyEvent[32];
        private static int pendingKeyCount = 0;

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        // 使用 IntPtr 版本的 SendInput 以获得更好的性能
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, IntPtr pInputs, int cbSize);

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

            // 预热扫描码缓存
            for (int i = 0; i < 256; i++)
            {
                scanCodeCache[i] = (byte)MapVirtualKey((uint)i, 0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetAudioSyncTicks()
        {
            if (Main.Settings.HighPrecisionTime)
            {
                return DSPTimeSimulater.GetDSPTimeAsFileTime();
            }
            return GetPreciseTicks();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetPreciseTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long counter))
            {
                return (counter * 10000000) / perfFrequency;
            }
            return DateTime.UtcNow.Ticks;
        }

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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize()
        {
            var lm = levelMaker ?? scrLevelMaker.instance;
            return lm?.listFloors == null || lm.listFloors.Count != floorCount;
        }

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
        /// 高性能 SendInput 发送（优化版）
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        private static unsafe void FlushSendInputEvents()
        {
            if (pendingKeyCount == 0) return;

            int inputSize = sizeof(SkyHookSystem.INPUT); // 使用 sizeof 替代 Marshal.SizeOf
            int totalSize = inputSize * pendingKeyCount;

            if (pendingKeyCount <= 32)
            {
                SkyHookSystem.INPUT* inputsPtr = stackalloc SkyHookSystem.INPUT[pendingKeyCount];
                var span = new Span<SkyHookSystem.INPUT>(inputsPtr, pendingKeyCount);

                for (int i = 0; i < pendingKeyCount; i++)
                {
                    ref var evt = ref pendingKeyEvents[i];
                    ref var input = ref span[i];

                    input.type = INPUT_KEYBOARD;
                    input.u.ki.wVk = evt.keyCode;
                    input.u.ki.wScan = scanCodeCache[evt.keyCode];
                    input.u.ki.dwFlags = evt.isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                }

                SendInput((uint)pendingKeyCount, (IntPtr)inputsPtr, inputSize);
            }
            else
            {
                IntPtr inputsPtr = Marshal.AllocHGlobal(totalSize);
                try
                {
                    var span = new Span<SkyHookSystem.INPUT>((void*)inputsPtr, pendingKeyCount);

                    for (int i = 0; i < pendingKeyCount; i++)
                    {
                        ref var evt = ref pendingKeyEvents[i];
                        ref var input = ref span[i];

                        input.type = INPUT_KEYBOARD;
                        input.u.ki.wVk = evt.keyCode;
                        input.u.ki.wScan = scanCodeCache[evt.keyCode];
                        input.u.ki.dwFlags = evt.isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                    }

                    SendInput((uint)pendingKeyCount, inputsPtr, inputSize);
                }
                finally
                {
                    Marshal.FreeHGlobal(inputsPtr);
                }
            }

            pendingKeyCount = 0;
        }

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
                        long now = GetAudioSyncTicks();
                        long elapsed = now - startTimeTicks;
                        var evt = SkyHookSystem.SkyHookEvent.Create(pendingKey.Value, false, elapsed);
                        AsyncInputManager.EnqueueEvent(evt);
                        Log($"[TimeBasedMacro] SkyHook松开已入队: Key=0x{pendingKey.Value:X2}");
                    }
                    else
                    {
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
                    long now = GetAudioSyncTicks();
                    long elapsed = now - startTimeTicks;
                    var evt = SkyHookSystem.SkyHookEvent.Create(newKey.Value, true, elapsed);
                    AsyncInputManager.EnqueueEvent(evt);
                    Log($"[TimeBasedMacro] SkyHook按下已入队: Key=0x{newKey.Value:X2}");
                }
                else
                {
                    QueueSendInputEvent(newKey.Value, true);
                }

                pendingKey = newKey;
                isKeyDown = true;
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
                if (!Main.Settings.SkyHookMode && pendingKeyCount > 0)
                {
                    FlushSendInputEvents();
                }
            }
            pendingKey = null;
            isKeyDown = false;
            keyIndex = 0;

            if (pendingKeyCount > 0)
            {
                Array.Clear(pendingKeyEvents, 0, pendingKeyCount);
                pendingKeyCount = 0;
            }

            AsyncInputManager.ClearQueue();

            if (Main.Settings.SkyHookMode)
            {
                startTimeTicks = GetAudioSyncTicks();
            }
            ApplyHoldBehavior(controller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            var settings = Main.Settings;

            // 快速失败检查
            if (!settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
            {
                if (skyHookInitialized)
                {
                    AsyncInputManager.Stop();
                    skyHookInitialized = false;
                }
                return;
            }

            // 模式切换检查
            if (settings.SkyHookMode != skyHookInitialized)
            {
                SwitchMode(settings.SkyHookMode);
            }

            // 初始化检查
            if (!initialized)
            {
                Initialize();
                if (!initialized) return;
            }
            else if (NeedReinitialize())
            {
                Reset(controller);
                Initialize();
                if (!initialized) return;
            }

            // 缓存局部变量
            var cond = conductor;
            var floors = cachedFloors;
            var times = triggerTimes;
            if (cond == null || floors == null || times == null) return;

            // 预计算常用值
            double currentTime = cond.songposition_minusi;
            double nextFrameTime = currentTime + (Time.unscaledDeltaTime * cond.song.pitch);
            double timeOffset = settings.TimeOffset * 0.001;
            bool simulateKeyPress = settings.SimulateKeyPress;

            int startFloor = lastTriggeredFloor + 1;
            int triggerCount = times.Length;

            // 主循环
            for (int i = startFloor; i < triggerCount; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;

                if (floor.nextfloor?.auto == true || floor.midSpin)
                {
                    lastTriggeredFloor = i;
                    continue;
                }

                double adjustedTrigger = times[i] + timeOffset;

                if (adjustedTrigger > nextFrameTime) break;
                if (i <= lastTriggeredFloor) continue;

                bool releaseOnly = false;
                if (simulateKeyPress && floor.holdLength > -1 && i + 1 < triggerCount)
                {
                    var nextFloor = floors[i + 1];
                    if (nextFloor != null && nextFloor.holdLength == -1)
                    {
                        releaseOnly = true;
                    }
                }

                if (!simulateKeyPress)
                {
                    controller.Hit(false);
                }
                else if (releaseOnly)
                {
                    if (isKeyDown && pendingKey.HasValue)
                    {
                        UpdateKeyState(null);
                    }
                    if (i + 1 > lastTriggeredFloor)
                    {
                        lastTriggeredFloor = i + 1;
                    }
                }
                else if (simulateKeyPress && keyCodes.Count > 0)
                {
                    byte key = keyCodes[keyIndex];
                    UpdateKeyState(key);

                    keyIndex = (keyIndex + 1) % keyCodes.Count;
                }

                lastTriggeredFloor = i;
            }

            // 使用高性能的 FlushSendInputEvents 发送事件
            if (!settings.SkyHookMode && pendingKeyCount > 0)
            {
                FlushSendInputEvents();
            }

            // 强制释放检查
            if (isKeyDown && pendingKey.HasValue && lastTriggeredFloor >= triggerCount - 1)
            {
                UpdateKeyState(null);
                if (!settings.SkyHookMode && pendingKeyCount > 0)
                {
                    FlushSendInputEvents();
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwitchMode(bool useSkyHook)
        {
            if (useSkyHook == skyHookInitialized) return;

            Log($"[TimeBasedMacro] 切换模式: {(useSkyHook ? "SkyHook" : "SendInput")}");

            if (useSkyHook)
            {
                if (!skyHookInitialized)
                {
                    AsyncInputManager.Start();
                    skyHookInitialized = true;
                }
                startTimeTicks = GetAudioSyncTicks();
                Main.Settings.SkyHookMode = true;
                Log("[TimeBasedMacro] 切换到 SkyHook 模式（时间精确模式）");
            }
            else
            {
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
                    FlushSendInputEvents(); // 使用高性能版本
                }
                isKeyDown = false;
                pendingKey = null;
            }
        }

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