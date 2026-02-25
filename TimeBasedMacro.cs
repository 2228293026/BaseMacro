using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

#nullable enable

namespace BaseMacro
{
    internal static class TimeBasedMacro
    {
        // 使用数组替代List，减少开销
        private static double[]? triggerTimes;
        private static int lastTriggeredFloor = -1;
        private static int floorCount;
        private static bool initialized = false;

        // 缓存组件引用（使用属性减少null检查）
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;

        // 缓存常用属性访问
        private static List<scrFloor>? cachedFloors;

        // 使用ArrayPool或固定大小数组
        private static readonly List<byte> keyCodes = new(4); // 预设容量
        private static int keyIndex = 0;
        private static byte? pendingKey = null;
        private static bool isKeyDown = false;
        private static string lastKeysSetting = "";

        // 扫描码缓存 - 使用固定大小的数组替代Dictionary
        private static readonly byte[] scanCodeCache = new byte[256]; // 虚拟键码范围0-255

        // 优化的按键事件队列 - 使用数组+索引替代Queue
        private static KeyEvent[] pendingKeyEvents = new KeyEvent[32];
        private static int pendingKeyCount = 0;

        // 预分配的INPUT数组
        private static INPUT[] inputs = new INPUT[32];

        // Windows API 常量
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private struct KeyEvent
        {
            public byte keyCode;
            public bool isDown;

            // 复用方法
            public void Reset()
            {
                keyCode = 0;
                isDown = false;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // 优化的批量发送方法
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void FlushKeyEvents()
        {
            if (pendingKeyCount == 0) return;

            // 确保inputs数组足够大
            if (pendingKeyCount > inputs.Length)
                Array.Resize(ref inputs, Math.Max(pendingKeyCount, inputs.Length * 2));

            // 批量构建INPUT结构
            for (int i = 0; i < pendingKeyCount; i++)
            {
                ref var evt = ref pendingKeyEvents[i];
                byte scanCode = scanCodeCache[evt.keyCode];
                if (scanCode == 0)
                {
                    scanCode = (byte)MapVirtualKey(evt.keyCode, 0);
                    scanCodeCache[evt.keyCode] = scanCode;
                }

                inputs[i].type = INPUT_KEYBOARD;
                inputs[i].u.ki = new KEYBDINPUT
                {
                    wVk = evt.keyCode,
                    wScan = scanCode,
                    dwFlags = evt.isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                };
            }

            // 发送所有事件
            SendInput((uint)pendingKeyCount, inputs, Marshal.SizeOf(typeof(INPUT)));

            // 重置计数器
            pendingKeyCount = 0;
        }

        // 优化的队列化方法 - 使用数组索引避免Queue开销
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void QueueKeyEvent(byte keyCode, bool isDown)
        {
            if (pendingKeyCount >= pendingKeyEvents.Length)
                Array.Resize(ref pendingKeyEvents, pendingKeyEvents.Length * 2);

            pendingKeyEvents[pendingKeyCount].keyCode = keyCode;
            pendingKeyEvents[pendingKeyCount].isDown = isDown;
            pendingKeyCount++;
        }

        // 优化的按键状态更新
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateKeyState(byte? newKey)
        {
            if (isKeyDown && pendingKey.HasValue)
            {
                if (!newKey.HasValue || newKey.Value != pendingKey.Value)
                {
                    QueueKeyEvent(pendingKey.Value, false);
                    isKeyDown = false;
                    pendingKey = null;
                }
            }

            if (newKey.HasValue && (!isKeyDown || newKey.Value != pendingKey))
            {
                QueueKeyEvent(newKey.Value, true);
                pendingKey = newKey;
                isKeyDown = true;
            }
        }

        // 扩展的键名映射表 - 包含全键盘按键
        private static readonly Dictionary<string, byte> KeyNameToCode = new()
        {
            // 字母键
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

            // 数字键
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

            // 功能键
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

            // 方向键
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,

            // 控制键 - 添加 Ctrl 键
            ["CTRL"] = 0x11,           // 通用 Ctrl
            ["LCTRL"] = 0xA2,          // 左 Ctrl
            ["RCTRL"] = 0xA3,          // 右 Ctrl
            ["SHIFT"] = 0x10,          // 通用 Shift
            ["LSHIFT"] = 0xA0,         // 左 Shift
            ["RSHIFT"] = 0xA1,         // 右 Shift
            ["ALT"] = 0x12,            // 通用 Alt
            ["LALT"] = 0xA4,           // 左 Alt
            ["RALT"] = 0xA5,           // 右 Alt
            ["WIN"] = 0x5B,            // Windows 键
            ["LWIN"] = 0x5B,           // 左 Windows
            ["RWIN"] = 0x5C,           // 右 Windows

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
            ["PAGEDOWN"] = 0x22,

            // 标点符号
            ["MINUS"] = 0xBD,        // -
            ["EQUALS"] = 0xBB,       // =
            ["LBRACKET"] = 0xDB,     // [
            ["RBRACKET"] = 0xDD,     // ]
            ["BACKSLASH"] = 0xDC,    // \
            ["SEMICOLON"] = 0xBA,    // ;
            ["QUOTE"] = 0xDE,        // '
            ["COMMA"] = 0xBC,        // ,
            ["PERIOD"] = 0xBE,       // .
            ["SLASH"] = 0xBF,        // /

            // 数字小键盘
            ["NUMPAD0"] = 0x60,
            ["NUMPAD1"] = 0x61,
            ["NUMPAD2"] = 0x62,
            ["NUMPAD3"] = 0x63,
            ["NUMPAD4"] = 0x64,
            ["NUMPAD5"] = 0x65,
            ["NUMPAD6"] = 0x66,
            ["NUMPAD7"] = 0x67,
            ["NUMPAD8"] = 0x68,
            ["NUMPAD9"] = 0x69,
            ["MULTIPLY"] = 0x6A,     // *
            ["ADD"] = 0x6B,          // +
            ["SUBTRACT"] = 0x6D,     // -
            ["DECIMAL"] = 0x6E,      // .
            ["DIVIDE"] = 0x6F,       // /

            // 其他
            ["CAPSLOCK"] = 0x14,
            ["NUMLOCK"] = 0x90,
            ["SCROLLLOCK"] = 0x91,
            ["PAUSE"] = 0x13,
            ["PRINTSCREEN"] = 0x2C
        };

        // 修改 ParseKeyCodes 方法，支持数字和更多键名
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";

            if (keysSetting == lastKeysSetting && keyCodes.Count > 0)
                return;

            lastKeysSetting = keysSetting;
            keyCodes.Clear();

            string[] parts = keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                string keyName = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(keyName)) continue;

                // 处理单个字符（字母或数字）
                if (keyName.Length == 1)
                {
                    char c = keyName[0];
                    // 字母 A-Z
                    if (c >= 'A' && c <= 'Z')
                    {
                        keyCodes.Add((byte)c);
                        continue;
                    }
                    // 数字 0-9
                    if (c >= '0' && c <= '9')
                    {
                        keyCodes.Add((byte)c);
                        continue;
                    }
                }

                // 从映射表查找
                if (KeyNameToCode.TryGetValue(keyName, out byte code))
                {
                    keyCodes.Add(code);
                }
                else
                {
                    Debug.LogWarning($"[TimeBasedMacro] 未知键名: {keyName}，已忽略");
                }
            }

            if (keyCodes.Count == 0)
                keyCodes.Add(0x4A); // 默认 J

            keyIndex = 0;
            Log($"[TimeBasedMacro] 键码解析完成: {string.Join(", ", keyCodes.Select(k => $"0x{k:X2}"))}");
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
        // 优化的初始化方法
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Initialize()
        {
            levelMaker = scrLevelMaker.instance;
            if (levelMaker?.listFloors == null || levelMaker.listFloors.Count == 0)
                return;

            cachedFloors = levelMaker.listFloors;
            floorCount = cachedFloors.Count;

            // 预分配数组，避免List开销
            triggerTimes = new double[floorCount];

            // 批量填充触发时间
            for (int i = 0; i < floorCount - 1; i++)
            {
                triggerTimes[i] = cachedFloors[i + 1]?.entryTime ?? double.MaxValue;
            }
            triggerTimes[floorCount - 1] = double.MaxValue;

            conductor = scrConductor.instance;

            // === 在初始化阶段解析键码 ===
            ParseKeyCodes();
            initialized = true;
            if (conductor != null)
            {
                SyncLastTriggeredFloor(conductor.songposition_minusi);
            }

            Log($"[TimeBasedMacro] 初始化完成，共 {floorCount} 个触发点");
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
                FlushKeyEvents();
            }

            pendingKey = null;
            isKeyDown = false;
            pendingKeyCount = 0;

            ApplyHoldBehavior(controller);

            Log("[TimeBasedMacro] 状态已重置");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            // 快速失败检查
            if (!Main.Settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
                return;

            // 延迟初始化
            if (!initialized || NeedReinitialize())
            {
                Reset(controller);
                Initialize();
                if (!initialized) return;
            }

            // 缓存本地引用
            var cond = conductor;
            var floors = cachedFloors;
            var times = triggerTimes;

            if (cond == null || floors == null || times == null)
                return;

            double currentTime = cond.songposition_minusi;
            double nextFrameTime = currentTime + Time.unscaledDeltaTime;
            double timeOffsetMs = Main.Settings.TimeOffset * 0.001;

            int startFloor = lastTriggeredFloor + 1;
            bool simulateKeyPress = Main.Settings.SimulateKeyPress;
            bool keyStateChanged = false;

            // 批量处理触发点
            int triggerCount = times.Length;
            for (int i = startFloor; i < triggerCount; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;

                // 合并快速检查
                if (floor.nextfloor?.auto == true || floor.midSpin)
                {
                    lastTriggeredFloor = i;
                    continue;
                }

                // 预计算触发时间
                double adjustedTrigger = times[i] + timeOffsetMs;
                if (adjustedTrigger > nextFrameTime)
                    break;

                if (i <= lastTriggeredFloor)
                    continue;

                // 处理释放逻辑
                bool releaseOnly = false;
                if (simulateKeyPress && floor.holdLength > -1 && i + 1 < triggerCount)
                {
                    var nextFloor = floors[i + 1];
                    if (nextFloor != null && nextFloor.holdLength == -1)
                    {
                        releaseOnly = true;
                    }
                }

                // 触发点击
                if (!simulateKeyPress)
                {
                    controller.Hit(false);
                }

                if (releaseOnly)
                {
                    if (simulateKeyPress && isKeyDown)
                    {
                        UpdateKeyState(null);
                        keyStateChanged = true;
                        Log($"[TimeBasedMacro] 地板 {i} 释放所有按键");
                    }

                    // 跳过下一个地板
                    if (i + 1 > lastTriggeredFloor)
                    {
                        lastTriggeredFloor = i + 1;
                        Log($"[TimeBasedMacro] 跳过普通地板 {i + 1}");
                    }
                }
                else if (simulateKeyPress && keyCodes.Count > 0)
                {
                    byte key = keyCodes[keyIndex];
                    keyIndex = (keyIndex + 1) % keyCodes.Count;
                    UpdateKeyState(key);
                    keyStateChanged = true;
                }

                lastTriggeredFloor = i;
                Log($"[TimeBasedMacro] 触发地板 {i}");
            }

            // 批量发送按键事件
            if (keyStateChanged || pendingKeyCount > 0)
            {
                FlushKeyEvents();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize()
        {
            var lm = levelMaker ?? scrLevelMaker.instance;
            return lm?.listFloors == null || lm.listFloors.Count != floorCount;
        }

        // 优化的输入处理
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HandleInput()
        {
            if (!Main.Settings.Macro) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl && Main.Settings.EnableKeyAdjust)
            {
                // 批量处理方向键输入
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                    Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep:F1}");
                }
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                    Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep:F1}");
                }
            }
            else if (!ctrl && Main.Settings.EnableArrowTimeAdjust)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                    Log($"[TimeBasedMacro] 偏移调整为 {Main.Settings.TimeOffset:F1}ms");
                }
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    Main.Settings.TimeOffset += Main.Settings.AdjustStep;
                    Log($"[TimeBasedMacro] 偏移调整为 {Main.Settings.TimeOffset:F1}ms");
                }
            }
        }

        // 优化的同步方法 - 使用二分查找
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SyncLastTriggeredFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Length == 0)
                return;

            int left = 0;
            int right = triggerTimes.Length - 1;

            while (left <= right)
            {
                int mid = (left + right) >> 1; // 位运算优化
                if (triggerTimes[mid] < currentTime)
                {
                    left = mid + 1;
                }
                else if (triggerTimes[mid] > currentTime)
                {
                    right = mid - 1;
                }
                else
                {
                    lastTriggeredFloor = mid;
                    Log($"[TimeBasedMacro] 同步到时间 {currentTime:F6}s，地板 {lastTriggeredFloor}");
                    return;
                }
            }

            lastTriggeredFloor = left - 1;
            Log($"[TimeBasedMacro] 同步到时间 {currentTime:F6}s，地板 {lastTriggeredFloor}");
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Log(string message)
        {
            Main.Mod?.Logger.Log(message);
        }
    }
}