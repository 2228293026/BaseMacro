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
            UIUtils.InitializeStyles();

            // 总标题
            GUILayout.Label("Macro Settings | 宏设置", UIUtils.HeaderStyle);
            GUILayout.BeginHorizontal();

            // 主开关卡片（宽度紧凑，与下方卡片自然衔接）
            GUILayout.BeginVertical(); // 开始外层垂直组
            GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450)); // 开始内层卡片
            Macro = UIUtils.M3Switch(Macro, "Enable Macro | 启用宏");
            GUILayout.EndVertical(); // 结束内层卡片

            if (Macro)
            {
                // 左侧：按键设置
                GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
                GUILayout.Label("按键设置", UIUtils.HeaderStyle);
                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Keys (comma separated) | 按键序列 (逗号分隔):", UIUtils.LabelStyle);
                MacroKeys = GUILayout.TextField(MacroKeys, UIUtils.TextFieldStyle);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                SimulateKeyPress = UIUtils.M3Switch(SimulateKeyPress, "Input key press (using WinAPI) | 输入按键 (使用Windows API)");
                GUILayout.EndVertical(); // 结束按键设置卡片
            }
            GUILayout.EndVertical(); // 结束外层垂直组（主开关和按键设置的容器）

            if (Macro)
            {
                // 右侧：延迟设置
                GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
                GUILayout.Label("延迟设置", UIUtils.HeaderStyle);
                GUILayout.Space(2);
                EnableKeyAdjust = UIUtils.M3Switch(EnableKeyAdjust, "Enable Key Adjust | 允许方向键调整偏移");
                GUILayout.Space(2);
                GUILayout.Label("调整步长", UIUtils.LabelStyle);
                GUILayout.BeginHorizontal();
                AdjustStep = UIUtils.M3HorizontalSliderWithLabelAndInput("AdjustStepField", AdjustStep, 0.1f, 10f,
                    ref adjustStepInput, ref adjustStepFocused, "F2", 120, 240, 60);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                GUILayout.Label("延迟 (ms)", UIUtils.LabelStyle);
                GUILayout.BeginHorizontal();
                TimeOffset = UIUtils.M3HorizontalSliderWithLabelAndInput("TimeOffsetField", TimeOffset, -100f, 100f,
                    ref timeOffsetInput, ref timeOffsetFocused, "F2", 120, 240, 60);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                EnableArrowTimeAdjust = UIUtils.M3Switch(EnableArrowTimeAdjust, "允许左右键调整延迟(游戏中)");
                GUILayout.Space(2);
                UseFramePrediction = UIUtils.M3Switch(UseFramePrediction, "Use Frame Prediction (improves accuracy) | 使用帧预测（提高精度）");
                GUILayout.EndVertical(); // 结束延迟设置卡片
            }

            GUILayout.EndHorizontal(); // 结束外层水平布局
        }
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