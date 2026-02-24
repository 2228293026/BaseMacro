using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

#nullable enable

namespace BaseMacro
{
    internal static class TimeBasedMacro
    {
        private static List<double>? triggerTimes;
        private static int lastTriggeredFloor = -1;
        private static int floorCount;
        private static bool initialized = false;

        // 缓存组件
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;

        private static readonly List<byte> keyCodes = [];
        private static int keyIndex = 0;

        private static byte? pendingKey = null;
        private static bool isKeyDown = false; // 跟踪按键状态

        private static string lastKeysSetting = "";

        // Windows API - 使用 SendInput 替代 keybd_event
        [DllImport("user32.dll")]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

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

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        // 缓存虚拟键码到扫描码的映射
        private static readonly Dictionary<byte, ushort> scanCodeCache = new(10);

        // 按键事件队列，用于批量处理
        private static readonly Queue<KeyEvent> pendingKeyEvents = new(16);

        private static INPUT[] inputs = new INPUT[16]; // 预设最大事件数

        private struct KeyEvent
        {
            public byte keyCode;
            public bool isDown;
        }

        // 批量发送按键事件
        private static void FlushKeyEvents()
        {
            int count = pendingKeyEvents.Count;
            if (count == 0) return; if (count > inputs.Length)
                Array.Resize(ref inputs, count);
            int index = 0;
            foreach (var evt in pendingKeyEvents)
            {
                // 获取扫描码（缓存结果）
                if (!scanCodeCache.TryGetValue(evt.keyCode, out ushort scanCode))
                {
                    scanCode = (ushort)MapVirtualKey(evt.keyCode, 0);
                    scanCodeCache[evt.keyCode] = scanCode;
                }

                inputs[index].type = INPUT_KEYBOARD;
                inputs[index].u.ki = new KEYBDINPUT
                {
                    wVk = evt.keyCode,
                    wScan = scanCode,
                    dwFlags = evt.isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                };
                index++;
            }

            if (index > 0)
            {
                SendInput((uint)index, inputs, Marshal.SizeOf(typeof(INPUT)));
            }

            pendingKeyEvents.Clear();
        }

        // 队列化按键事件
        private static void QueueKeyEvent(byte keyCode, bool isDown)
        {
            pendingKeyEvents.Enqueue(new KeyEvent { keyCode = keyCode, isDown = isDown });
        }

        // 更新按键状态（智能去重）
        private static void UpdateKeyState(byte? newKey)
        {
            // 如果需要释放当前按键
            if (isKeyDown && pendingKey.HasValue)
            {
                if (!newKey.HasValue || newKey.Value != pendingKey.Value)
                {
                    QueueKeyEvent(pendingKey.Value, false);
                    isKeyDown = false;
                    pendingKey = null;
                }
            }

            // 如果需要按下新按键
            if (newKey.HasValue && (!isKeyDown || newKey.Value != pendingKey))
            {
                QueueKeyEvent(newKey.Value, true);
                pendingKey = newKey;
                isKeyDown = true;
            }
        }

        private static void Initialize()
        {
            levelMaker = scrLevelMaker.instance;
            if (levelMaker?.listFloors == null || levelMaker.listFloors.Count == 0) return;

            floorCount = levelMaker.listFloors.Count;
            triggerTimes = new List<double>(floorCount);
            for (int i = 0; i < floorCount - 1; i++)
                triggerTimes.Add(levelMaker.listFloors[i + 1].entryTime);
            triggerTimes.Add(double.MaxValue);

            conductor = scrConductor.instance;
            initialized = true;
            if (conductor != null)
            {
                SyncLastTriggeredFloor(conductor.songposition_minusi);
            }
            Log($"[TimeBasedMacro] 初始化完成，共 {floorCount} 个触发点");
        }

        public static void Reset()
        {
            lastTriggeredFloor = -1;
            initialized = false;
            triggerTimes = null;
            levelMaker = null;
            conductor = null;

            // 释放可能残留的按键（使用新方法）
            if (isKeyDown && pendingKey.HasValue)
            {
                UpdateKeyState(null);
                FlushKeyEvents();
            }

            pendingKey = null;
            isKeyDown = false;
            pendingKeyEvents.Clear();

            Log("[TimeBasedMacro] 状态已重置");
        }

        // 解析按键设置字符串，转换为虚拟键码列表
        private static void UpdateKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && keyCodes.Count > 0)
                return;

            lastKeysSetting = keysSetting;
            keyCodes.Clear();

            string[] parts = keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries);
            foreach (string part in parts)
            {
                string keyName = part.Trim().ToUpper();
                if (string.IsNullOrEmpty(keyName)) continue;

                // 尝试将键名转换为虚拟键码
                if (keyName.Length == 1)
                {
                    char c = keyName[0];
                    if (c >= 'A' && c <= 'Z')
                    {
                        keyCodes.Add((byte)c);
                        continue;
                    }
                }

                switch (keyName)
                {
                    case "J": keyCodes.Add(0x4A); break;
                    case "SPACE": keyCodes.Add(0x20); break;
                    case "ENTER": keyCodes.Add(0x0D); break;
                    default:
                        Debug.LogWarning($"[TimeBasedMacro] 未知键名: {keyName}，已忽略");
                        break;
                }
            }

            if (keyCodes.Count == 0)
                keyCodes.Add(0x4A); // 默认 J

            keyIndex = 0; // 重置索引
        }
        public static void Update(scrController controller)
        {
            if (!Main.Settings.Macro) return;
            if (controller?.paused != false) return;
            if (ADOBase.sceneName == GCNS.sceneLevelSelect) return;

            var lm = levelMaker;
            var cond = conductor;
            var floors = lm?.listFloors;

            if (!initialized || NeedReinitialize())
            {
                Reset();
                Initialize();
                if (!initialized) return;
            }

            double currentTime = cond!.songposition_minusi;
            double nextFrameTime = currentTime + Time.unscaledDeltaTime; // 预测的下一帧时间

            // 从上一个触发的地板之后开始检查
            int startFloor = lastTriggeredFloor + 1;
            bool keyStateChanged = false;

            UpdateKeyCodes();
            int triggerCount = triggerTimes!.Count;
            for (int i = startFloor; i < triggerCount; i++)
            {
                var floor = floors![i];
                if (floor == null) continue;

                if (floor.nextfloor != null && floor.nextfloor.auto)
                {
                    lastTriggeredFloor = i;
                    continue;
                }
                if (floor.midSpin)
                {
                    lastTriggeredFloor = i;
                    continue;
                }

                // 判断是否需要只释放按键而不按下新键
                bool releaseOnly = false;

                // 如果当前地板是长按，且下一个地板不是长按
                if (Main.Settings.SimulateKeyPress)
                {
                    if (floor.holdLength > -1 && i + 1 < triggerCount)
                    {
                        var nextFloor = floors[i + 1];
                        if (nextFloor != null && nextFloor.holdLength == -1)
                        {
                            releaseOnly = true;
                        }
                    }
                }

                double triggerTime = triggerTimes[i];
                double adjustedTrigger = triggerTime + Main.Settings.TimeOffset * 0.001;

                if (adjustedTrigger <= nextFrameTime + 1e-15)
                {
                    if (i <= lastTriggeredFloor) continue;

                    if (releaseOnly)
                    {
                        // 情况1: 只释放按键，不按下新键
                        if (Main.Settings.SimulateKeyPress && isKeyDown)
                        {
                            UpdateKeyState(null);
                            keyStateChanged = true;
                            Log($"[TimeBasedMacro] 地板 {i} 释放所有按键（下一个不是长按）");
                        }

                        // 跳过下一个地板（将其标记为已处理）
                        if (i + 1 > lastTriggeredFloor)
                        {
                            lastTriggeredFloor = i + 1;
                            Log($"[TimeBasedMacro] 跳过普通地板 {i + 1}");
                        }
                    }
                    else
                    {
                        // 触发点击
                        if (!Main.Settings.SimulateKeyPress)
                            controller.Hit(false);

                        // 模拟按键
                        if (Main.Settings.SimulateKeyPress && keyCodes.Count > 0)
                        {
                            byte key = keyCodes[keyIndex];
                            keyIndex = (keyIndex + 1) % keyCodes.Count;

                            UpdateKeyState(key);
                            keyStateChanged = true;
                        }
                    }

                    lastTriggeredFloor = i;

                    Log($"[TimeBasedMacro] 触发地板 {i}，时间 {currentTime:F6}s，理论 {triggerTime:F6}s，偏移 {Main.Settings.TimeOffset}ms，releaseOnly={releaseOnly}");
                }
                else
                {
                    break;
                }
            }

            // 每帧只发送一次批量按键事件
            if (keyStateChanged || pendingKeyEvents.Count > 0)
            {
                FlushKeyEvents();
            }
        }

        private static bool NeedReinitialize()
        {
            var lm = levelMaker ?? scrLevelMaker.instance;
            return lm?.listFloors == null || lm.listFloors.Count != floorCount;
        }

        public static void HandleInput()
        {
            if (!Main.Settings.Macro) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

            if (ctrl)
            {
                // Ctrl + Left/Right 调整 AdjustStep
                if (Main.Settings.EnableKeyAdjust)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow))
                    {
                        Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                        Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep}");
                    }
                    if (Input.GetKeyDown(KeyCode.RightArrow))
                    {
                        Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                        Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep}");
                    }
                }
            }
            else
            {
                // 无 Ctrl：左右方向键调整 TimeOffset
                if (Main.Settings.EnableArrowTimeAdjust)
                {
                    if (Input.GetKeyDown(KeyCode.LeftArrow))
                    {
                        Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                        Log($"[TimeBasedMacro] 偏移调整为 {Main.Settings.TimeOffset}ms");
                    }
                    if (Input.GetKeyDown(KeyCode.RightArrow))
                    {
                        Main.Settings.TimeOffset += Main.Settings.AdjustStep;
                        Log($"[TimeBasedMacro] 偏移调整为 {Main.Settings.TimeOffset}ms");
                    }
                }
            }
        }

        private static void SyncLastTriggeredFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Count == 0) return;

            // 二分查找第一个触发时间 > currentTime 的索引
            int index = triggerTimes.BinarySearch(currentTime);
            if (index < 0) index = ~index; // 获取大于 currentTime 的第一个索引

            // 如果所有触发时间都 <= currentTime，则 index = triggerTimes.Count
            // 此时 lastTriggeredFloor 应为最后一个索引
            lastTriggeredFloor = index - 1;

            Log($"[TimeBasedMacro] 同步到当前时间 {currentTime:F6}s，lastTriggeredFloor = {lastTriggeredFloor}");
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private static void Log(string message)
        {
            Main.Mod?.Logger.Log(message);
        }
    }
}