using HarmonyLib;
using System;
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
        public event Action<bool> OnMacroChanged;

        private bool _macro;
        public bool Macro
        {
            get => _macro;
            set
            {
                if (_macro == value) return;
                _macro = value;
                OnMacroChanged?.Invoke(value);
            }
        }

        // 修复属性语法
        private string _macroKeys = "D,F,J,K";
        public string MacroKeys
        {
            get => _macroKeys;
            set
            {
                if (_macroKeys == value) return;
                _macroKeys = value;
            }
        }

        private bool _simulateKeyPress = false;
        public bool SimulateKeyPress
        {
            get => _simulateKeyPress;
            set
            {
                if (_simulateKeyPress == value) return;
                _simulateKeyPress = value;
            }
        }        public bool EnableKeyAdjust = true;
        public float AdjustStep = 1f;
        public bool UseFramePrediction = true;

        private float _timeOffset;
        public float TimeOffset
        {
            get => _timeOffset;
            set => _timeOffset = Mathf.Clamp(value, -100f, 100f);
        }

        public bool EnableArrowTimeAdjust = true;

        // 使用ValueTuple减少GC
        private (string input, bool focused) _adjustStepState = (string.Empty, false);
        private (string input, bool focused) _timeOffsetState = (string.Empty, false);

        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UIUtils.InitializeStyles();

            GUILayout.Label("Macro | 宏", UIUtils.HeaderStyle);
            GUILayout.BeginHorizontal();

            // 主开关
            GUILayout.BeginVertical();
            GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
            bool newMacro = UIUtils.M3Switch(Macro, "Enable Macro | 启用宏");
            if (newMacro != Macro)
            {
                Macro = newMacro;
                ADOBase.controller.Restart();
            }
            GUILayout.EndVertical();

            if (Macro)
            {
                // 按键设置
                GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
                GUILayout.Label("Key Settings | 按键设置", UIUtils.HeaderStyle);
                GUILayout.Space(2);
                GUILayout.BeginHorizontal();
                GUILayout.Label("Keys (comma separated) | 按键序列 (逗号分隔):", UIUtils.LabelStyle);
                MacroKeys = GUILayout.TextField(MacroKeys, UIUtils.TextFieldStyle);
                GUILayout.EndHorizontal();
                GUILayout.Space(2);
                bool newSimulateKeyPress = UIUtils.M3Switch(SimulateKeyPress, "Input key press (using WinAPI) | 输入按键 (使用Windows API)");
                if (newSimulateKeyPress != SimulateKeyPress)
                {
                    SimulateKeyPress = newSimulateKeyPress; // 通过属性设置，会触发事件
                    ADOBase.controller.Restart();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndVertical();

            if (Macro)
            {
                // 延迟设置
                GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
                GUILayout.Label("Offset Settings | 延迟设置", UIUtils.HeaderStyle);
                GUILayout.Space(2);

                EnableKeyAdjust = UIUtils.M3Switch(EnableKeyAdjust, "Allow adjusting step offset using Ctrl and arrow keys | 允许Ctrl+方向键调整步长偏移");
                GUILayout.Space(2);

                GUILayout.Label("调整步长", UIUtils.LabelStyle);
                GUILayout.BeginHorizontal();
                AdjustStep = UIUtils.M3HorizontalSliderWithLabelAndInput("AdjustStep", AdjustStep, 0.1f, 10f,
                    ref _adjustStepState.input, ref _adjustStepState.focused, "F2", 120, 240, 60);
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUILayout.Label("延迟 (ms)", UIUtils.LabelStyle);
                GUILayout.BeginHorizontal();
                TimeOffset = UIUtils.M3HorizontalSliderWithLabelAndInput("TimeOffset", TimeOffset, -100f, 100f,
                    ref _timeOffsetState.input, ref _timeOffsetState.focused, "F2", 120, 240, 60);
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                EnableArrowTimeAdjust = UIUtils.M3Switch(EnableArrowTimeAdjust, "Allow adjustment of delay using left and right keys (in-game) | 允许左右键调整延迟(游戏中)");
                GUILayout.Space(2);
                UseFramePrediction = UIUtils.M3Switch(UseFramePrediction, "Use Frame Prediction (improves accuracy) | 使用帧预测（提高精度）");
                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();
        }

        public void OnSaveGUI(UnityModManager.ModEntry modEntry) => Save(modEntry);
        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
        public static Settings Load(UnityModManager.ModEntry modEntry) => Load<Settings>(modEntry);
    }
}