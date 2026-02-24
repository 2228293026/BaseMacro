using HarmonyLib;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityModManagerNet;

namespace BaseMacro
{
    /// <summary>
    /// Mod settings class
    /// Mod 设置类
    /// </summary>
    public class Settings : UnityModManager.ModSettings
    {
        public bool Macro = false;

        public string MacroKeys = "J";                // 按键序列，逗号分隔，如 "Q,W,E,R"
        public bool SimulateKeyPress = false;         // 是否模拟按键输入（若 false 则仅调用 Hit）

        public bool EnableKeyAdjust = true;   // 允许方向键调整偏移
        public float AdjustStep = 1f;          // 每次调整的步长（毫秒）

        public bool UseFramePrediction = true;   // 启用帧预测提升精度

        public float TimeOffset = 0;

        public bool EnableArrowTimeAdjust = true; // 默认开启


        private string adjustStepInput = "";
        private string timeOffsetInput = "";
        private bool adjustStepFocused = false;
        private bool timeOffsetFocused = false;

        /// <summary>
        /// Draw mod GUI / 绘制 Mod GUI
        /// </summary>
        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("Macro Settings | 宏设置", UnityModManager.UI.bold, GUILayout.Width(200));
            Macro = GUILayout.Toggle(Macro, "Enable Macro | 启用宏");

            if (Macro)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("Keys (comma separated) | 按键序列 (逗号分隔):");
                MacroKeys = GUILayout.TextField(MacroKeys);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                SimulateKeyPress = GUILayout.Toggle(SimulateKeyPress, "Input key press (using WinAPI) | 输入按键 (使用Windows API)");
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                EnableKeyAdjust = GUILayout.Toggle(EnableKeyAdjust, "Enable Key Adjust | 允许方向键调整偏移");
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("调整步长:", GUILayout.Width(80));

                // 滑块
                AdjustStep = GUILayout.HorizontalSlider(AdjustStep, 0.1f, 10f, GUILayout.MinWidth(120));

                // 输入框
                GUI.SetNextControlName("AdjustStepField");
                adjustStepInput = GUILayout.TextField(adjustStepInput, GUILayout.Width(60));

                // 焦点管理
                if (GUI.GetNameOfFocusedControl() == "AdjustStepField")
                {
                    if (!adjustStepFocused)
                    {
                        // 获得焦点时，用当前值初始化输入框
                        adjustStepInput = AdjustStep.ToString("F2");
                        adjustStepFocused = true;
                    }
                }
                else
                {
                    if (adjustStepFocused)
                    {
                        // 失去焦点时，尝试解析输入并更新设置
                        if (float.TryParse(adjustStepInput, out float newStep))
                            AdjustStep = Mathf.Clamp(newStep, 0.1f, 10f);
                        adjustStepFocused = false;
                    }
                    // 未获得焦点时，保持输入框显示当前设置值
                    adjustStepInput = AdjustStep.ToString("F2");
                }
                GUILayout.EndHorizontal();

                // 延迟设置（滑块 + 输入框）
                GUILayout.BeginHorizontal();
                GUILayout.Label("延迟 (ms):", GUILayout.Width(80));

                // 滑块（直接操作 TimeOffset 字段）
                TimeOffset = GUILayout.HorizontalSlider(TimeOffset, -100f, 100f, GUILayout.MinWidth(120));

                // 输入框
                GUI.SetNextControlName("TimeOffsetField");
                timeOffsetInput = GUILayout.TextField(timeOffsetInput, GUILayout.Width(60));

                // 焦点管理
                if (GUI.GetNameOfFocusedControl() == "TimeOffsetField")
                {
                    if (!timeOffsetFocused)
                    {
                        timeOffsetInput = TimeOffset.ToString("F2");
                        timeOffsetFocused = true;
                    }
                }
                else
                {
                    if (timeOffsetFocused)
                    {
                        if (float.TryParse(timeOffsetInput, out float newOffset))
                            TimeOffset = Mathf.Clamp(newOffset, -100f, 100f);
                        timeOffsetFocused = false;
                    }
                    timeOffsetInput = TimeOffset.ToString("F2");
                }
                GUILayout.EndHorizontal();

                // 左右键调整开关
                Main.Settings.EnableArrowTimeAdjust = GUILayout.Toggle(Main.Settings.EnableArrowTimeAdjust, "允许左右键调整延迟(游戏中)");
                UseFramePrediction = GUILayout.Toggle(UseFramePrediction, "Use Frame Prediction (improves accuracy) | 使用帧预测（提高精度）");
            }
        }

        /// <summary>
        /// Called when saving GUI / 保存设置时调用
        /// </summary>
        public void OnSaveGUI(UnityModManager.ModEntry modEntry)
        {
            Save(modEntry);
        }

        /// <summary>
        /// Save settings / 保存设置
        /// </summary>
        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }

        /// <summary>
        /// Load settings / 加载设置
        /// </summary>
        public static Settings Load(UnityModManager.ModEntry modEntry)
        {
            return Load<Settings>(modEntry);
        }
    }
}