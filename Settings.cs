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
                _keyCache = []; // 清除缓存
            }
        }

        // 缓存解析后的键码
        private byte[] _keyCache = [];
        public byte[] GetKeyCodes()
        {
            if (_keyCache.Length > 0) return _keyCache;
            _keyCache = ParseKeyCodes(MacroKeys);
            return _keyCache;
        }

        public bool SimulateKeyPress = false;
        public bool EnableKeyAdjust = true;
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

        // 预编译键名映射
        private static readonly System.Collections.Generic.Dictionary<string, byte> KeyNameToCode = new()
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
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D
        };

        private static byte[] ParseKeyCodes(string keysSetting)
        {
            if (string.IsNullOrEmpty(keysSetting)) return new byte[] { 0x4A };

            var parts = keysSetting.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var codes = new System.Collections.Generic.List<byte>(parts.Length);

            foreach (var part in parts)
            {
                var keyName = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(keyName)) continue;

                if (KeyNameToCode.TryGetValue(keyName, out byte code))
                {
                    codes.Add(code);
                }
            }

            return codes.Count > 0 ? codes.ToArray() : new byte[] { 0x4A };
        }

        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UIUtils.InitializeStyles();

            GUILayout.Label("Macro | 宏", UIUtils.HeaderStyle);
            GUILayout.BeginHorizontal();

            // 主开关
            GUILayout.BeginVertical();
            GUILayout.BeginVertical(UIUtils.CardStyle, GUILayout.Width(450));
            bool newMacro = UIUtils.M3Switch(Macro, "Enable Macro | 启用宏");
            if (newMacro != Macro) Macro = newMacro;
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
                SimulateKeyPress = UIUtils.M3Switch(SimulateKeyPress, "Input key press (using WinAPI) | 输入按键 (使用Windows API)");
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