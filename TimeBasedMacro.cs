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
        public static float TimeOffset = 0.0f;

        private static List<double>? triggerTimes;
        private static int lastTriggeredFloor = -1;
        private static int floorCount;
        private static bool initialized = false;

        // 缓存组件
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;

        private static readonly bool enableLog = false;

        private static readonly List<byte> keyCodes = [];
        private static int keyIndex = 0;

        private static byte? pendingKey = null;

        private static string lastKeysSetting = "";

        // Windows API
        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

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
            if (enableLog) Debug.Log($"[TimeBasedMacro] 初始化完成，共 {floorCount} 个触发点");
        }

        public static void Reset()
        {
            lastTriggeredFloor = -1;
            initialized = false;
            triggerTimes = null;
            levelMaker = null;
            conductor = null;
            // 释放可能残留的按键
            if (pendingKey.HasValue)
            {
                keybd_event(pendingKey.Value, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                pendingKey = null;
            }
            if (enableLog) Debug.Log("[TimeBasedMacro] 状态已重置");
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
                // 对于字母键，可以直接取 ASCII 码
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

            if (!initialized || NeedReinitialize())
            {
                Reset();
                Initialize();
                if (!initialized) return;
            }

            double currentTime = conductor!.songposition_minusi;
            double nextFrameTime = currentTime + Time.unscaledDeltaTime; // 预测的下一帧时间

            // 从上一个触发的地板之后开始检查
            int startFloor = lastTriggeredFloor + 1;
            for (int i = startFloor; i < triggerTimes!.Count; i++)
            {
                var floor = levelMaker?.listFloors[i];
                if (floor != null && floor.midSpin)
                {
                    lastTriggeredFloor = i; // 标记为已处理，但不触发
                    continue;
                }
                double triggerTime = triggerTimes[i];
                double adjustedTrigger = triggerTime + TimeOffset * 0.001;

                // 如果触发时间在当前帧或下一帧内（允许微小容差）
                if (adjustedTrigger <= nextFrameTime + 1e-15) // 1飞秒容差
                {
                    // 避免重复触发（理论上不会）
                    if (i <= lastTriggeredFloor) continue;

                    UpdateKeyCodes();

                    // 触发点击
                    if (!Main.Settings.SimulateKeyPress)
                        controller.Hit(false);

                    // 模拟按键
                    if (Main.Settings.SimulateKeyPress && keyCodes.Count > 0)
                    {
                        if (pendingKey.HasValue)
                        {
                            keybd_event(pendingKey.Value, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                            pendingKey = null;
                        }

                        byte key = keyCodes[keyIndex];
                        keyIndex = (keyIndex + 1) % keyCodes.Count;
                        keybd_event(key, 0, KEYEVENTF_KEYDOWN, UIntPtr.Zero);
                        pendingKey = key;
                        // 不设置自动释放，由下次触发或重置时释放
                    }

                    lastTriggeredFloor = i;

                    if (enableLog)
                        Debug.Log($"[TimeBasedMacro] 触发地板 {i}，时间 {currentTime:F6}s，理论 {triggerTime:F6}s，偏移 {TimeOffset}ms");
                }
                else
                {
                    // 由于触发时间是递增的，遇到第一个超出下一帧时间的就停止
                    break;
                }
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
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                    if (enableLog) Debug.Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep}");
                }
                if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
                    if (enableLog) Debug.Log($"[TimeBasedMacro] AdjustStep 调整为 {Main.Settings.AdjustStep}");
                }
                // Ctrl + A 切换 EnableKeyAdjust
                if (Input.GetKeyDown(KeyCode.A))
                {
                    Main.Settings.EnableKeyAdjust = !Main.Settings.EnableKeyAdjust;
                    if (enableLog) Debug.Log($"[TimeBasedMacro] EnableKeyAdjust 切换为 {Main.Settings.EnableKeyAdjust}");
                }
            }
            else
            {
                // 无 Ctrl：左右方向键调整 TimeOffset
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                {
                    TimeOffset -= Main.Settings.AdjustStep;
                    if (enableLog) Debug.Log($"[TimeBasedMacro] 偏移调整为 {TimeOffset}ms");
                }
                if (Input.GetKeyDown(KeyCode.RightArrow))
                {
                    TimeOffset += Main.Settings.AdjustStep;
                    if (enableLog) Debug.Log($"[TimeBasedMacro] 偏移调整为 {TimeOffset}ms");
                }
            }
        }
        private static void SyncLastTriggeredFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Count == 0) return;

            // 分查找第一个触发时间 > currentTime 的索引
            int index = triggerTimes.BinarySearch(currentTime);
            if (index < 0) index = ~index; // 获取大于 currentTime 的第一个索引

            // 如果所有触发时间都 <= currentTime，则 index = triggerTimes.Count
            // 此时 lastTriggeredFloor 应为最后一个索引
            lastTriggeredFloor = index - 1;

            if (enableLog)
                Debug.Log($"[TimeBasedMacro] 同步到当前时间 {currentTime:F6}s，lastTriggeredFloor = {lastTriggeredFloor}");
        }
    }
}