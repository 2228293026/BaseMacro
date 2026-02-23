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
                GUILayout.Label("Adjust Step (ms) | 调整步长(毫秒):");
                string stepStr = GUILayout.TextField(AdjustStep.ToString(), GUILayout.Width(50));
                float.TryParse(stepStr, out AdjustStep);
                AdjustStep = Mathf.Clamp(AdjustStep, 0.1f, 10f);
                GUILayout.EndHorizontal();

                GUILayout.Label("Use Left/Right arrows to adjust TimeOffset | 使用左右方向键调整偏移量");
                GUILayout.Label($"Current TimeOffset: {TimeBasedMacro.TimeOffset} ms");
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