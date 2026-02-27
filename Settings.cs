using HarmonyLib;
using Newgrounds;
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

        private bool _useChinese = true;  // 默认中文
        public bool UseChinese
        {
            get => _useChinese;
            set
            {
                if (_useChinese == value) return;
                _useChinese = value;
            }
        }
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
        }
        public bool EnableKeyAdjust = true;
        public float AdjustStep = 1f;

        private float _timeOffset;
        public float TimeOffset
        {
            get => _timeOffset;
            set => _timeOffset = Mathf.Clamp(value, -100f, 100f);
        }

        public bool EnableArrowTimeAdjust = true;

        private bool _skyHookMode = false;
        public bool SkyHookMode
        {
            get => _skyHookMode;
            set
            {
                if (_skyHookMode == value) return;
                _skyHookMode = value;
                // 可以在这里添加模式切换的即时处理
                // OnSkyHookModeChanged?.Invoke(value);
            }
        }

        private bool _highPrecisionTime = false;
        public bool HighPrecisionTime
        {
            get => _highPrecisionTime;
            set
            {
                if (_highPrecisionTime == value) return;
                _highPrecisionTime = value;
            }
        }
        // 使用ValueTuple减少GC
        private (string input, bool focused) _adjustStepState = (string.Empty, false);
        private (string input, bool focused) _timeOffsetState = (string.Empty, false);

        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UIUtils.InitializeStyles();

            // 创建两列布局
            GUILayout.BeginHorizontal();

            // 左侧列
            GUILayout.BeginVertical(GUILayout.Width(425));

            DrawLanguageCard();

            GUILayout.Space(12);

            DrawMainSwitchCard();

            GUILayout.Space(12);

            if (Macro)
                DrawKeySettingsCard();

            GUILayout.EndVertical();

            GUILayout.Space(12);
            // 右侧列
            GUILayout.BeginVertical(GUILayout.Width(400));

            // 延迟设置卡片
            if (Macro)
            {
                DrawOffsetSettingsCard();
            }
            else
            {
                // 如果 Macro 未开启，显示一个提示卡片
                GUILayout.BeginVertical(UIUtils.CardStyle);
                GUILayout.Label(UseChinese ? "请先启用宏" : "Please enable Macro first",
                    UIUtils.LabelStyle);
                GUILayout.EndVertical();
            }

            GUILayout.Space(12);

            DrawAuthorCard();

            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawLanguageCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(UseChinese ? "语言" : "Language", UIUtils.HeaderStyle);
            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            string LanguageSwitchText = UseChinese ? "显示语言" : "Display Language";

            GUILayout.Label(LanguageSwitchText, UIUtils.LabelStyle, GUILayout.Width(150));

            string[] languages = { "中文", "English" };
            int selected = UseChinese ? 0 : 1;
            int newSelected = UIUtils.M3SelectionGrid(selected, languages, 2, GUILayout.Width(200));
            if (newSelected != selected)
            {
                UseChinese = newSelected == 0;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawMainSwitchCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(UseChinese ? "宏" : "Macro", UIUtils.HeaderStyle);

            string macroSwitchText = UseChinese ? "启用宏" : "Enable Macro";
            bool newMacro = UIUtils.M3Switch(Macro, macroSwitchText);
            if (newMacro != Macro)
            {
                Macro = newMacro;
                ADOBase.controller.Restart();
            }
            GUILayout.EndVertical();
        }

        private void DrawOffsetSettingsCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(UseChinese ? "延迟设置" : "Offset Settings", UIUtils.HeaderStyle);
            GUILayout.Space(2);

            string adjustText = UseChinese ? "允许Ctrl+左右键调整步长偏移(游戏中)" : "Allow adjusting step offset using Ctrl and arrow keys (in-game)";
            EnableKeyAdjust = UIUtils.M3Switch(EnableKeyAdjust, adjustText);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            AdjustStep = UIUtils.M3HorizontalSliderWithLabelAndInput(UseChinese ? "调整步长" : "Adjust Step", AdjustStep, 0.1f, 10f,
                ref _adjustStepState.input, ref _adjustStepState.focused, "F2", 120, 240, 60);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            TimeOffset = UIUtils.M3HorizontalSliderWithLabelAndInput(UseChinese ? "延迟 (ms)" : "Offset (ms)", TimeOffset, -100f, 100f,
                ref _timeOffsetState.input, ref _timeOffsetState.focused, "F2", 120, 240, 60);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            string arrowText = UseChinese ? "允许左右键调整延迟(游戏中)" : "Allow adjustment of delay using left and right keys (in-game)";
            EnableArrowTimeAdjust = UIUtils.M3Switch(EnableArrowTimeAdjust, arrowText);
            GUILayout.Space(2);
            string highPrecisionText = UseChinese ? "[实验性]启用高精度时间（提高同步精度）" : "[Experimental]Enable High Precision Time (improves sync accuracy)";
            HighPrecisionTime = UIUtils.M3Switch(HighPrecisionTime, highPrecisionText);
            GUILayout.EndVertical();
        }

        private void DrawKeySettingsCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(UseChinese ? "按键设置" : "Key Settings", UIUtils.HeaderStyle);
            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            string keysLabel = UseChinese ? "按键序列 (逗号分隔):" : "Keys (comma separated):";
            GUILayout.Label(keysLabel, UIUtils.LabelStyle, GUILayout.Width(180));
            MacroKeys = GUILayout.TextField(MacroKeys, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            string simulateText = UseChinese ? "按键模拟" : "Key simulation";
            bool newSimulateKeyPress = UIUtils.M3Switch(SimulateKeyPress, simulateText);
            if (newSimulateKeyPress != SimulateKeyPress)
            {
                SimulateKeyPress = newSimulateKeyPress;
                ADOBase.controller.Restart();
            }

            if (SimulateKeyPress)
            {
                GUILayout.Space(2);
                string skyHook = UseChinese ? "使用SkyHook输入(关闭则使用Win API)" : "Use SkyHook(If closed, use Win API)";
                SkyHookMode = UIUtils.M3Switch(SkyHookMode, skyHook);
            }

            GUILayout.EndVertical();
        }
        private void DrawAuthorCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);

            // 创建淡色样式
            GUIStyle authorStyle = new(UIUtils.LabelStyle);
            authorStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f, 0.8f);
            authorStyle.richText = true;
            authorStyle.alignment = TextAnchor.MiddleLeft;

            // 作者信息横排 - 紧凑版
            GUILayout.BeginHorizontal();
            GUILayout.Label($"👤 {Main.Mod.Info.Author}", authorStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"📦 {Main.Mod.Info.Version}", authorStyle);
            GUILayout.FlexibleSpace();
            GUILayout.Label($"📧 {(UseChinese ? "hitmargin@qq.com" : "hitmargin@Outlock.com")}", authorStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);

            // 分隔线
            Color originalColor = GUI.color;
            GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
            GUILayout.Box("", GUILayout.Height(10), GUILayout.ExpandWidth(true));
            GUI.color = originalColor;

            GUILayout.Space(4);

            // 感谢语 - 使用更淡的颜色
            GUIStyle thanksStyle = new(UIUtils.LabelStyle);
            thanksStyle.normal.textColor = new Color(0.7f, 0.7f, 0.7f, 0.4f);
            thanksStyle.fontSize = 9;
            thanksStyle.alignment = TextAnchor.MiddleCenter;

            GUILayout.Label(UseChinese ? "❤️ 感谢使用 BaseMacro" : "❤️ Thanks for using BaseMacro", thanksStyle);

            GUILayout.EndVertical();
        }
        public void OnSaveGUI(UnityModManager.ModEntry modEntry) => Save(modEntry);
        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
        public static Settings Load(UnityModManager.ModEntry modEntry) => Load<Settings>(modEntry);
    }
}