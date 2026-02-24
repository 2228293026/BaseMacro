using UnityEngine;
using System;

namespace BaseMacro
{
    public class ShowText : MonoBehaviour
    {
        private bool _showMacroText;
        private readonly string _cachedText = "Macro is enabled!";
        private Rect _rect;

        void Awake()
        {
            _rect = new Rect(
                0,
                0,
                200, 20
            );
            // 初始化状态
            _showMacroText = Main.Settings.Macro;
        }

        void OnEnable()
        {
            // 订阅事件
            Main.Settings?.OnMacroChanged += OnMacroChanged;
        }

        void OnDisable()
        {
            // 取消订阅
            Main.Settings?.OnMacroChanged -= OnMacroChanged;
        }

        private void OnMacroChanged(bool newValue)
        {
            _showMacroText = newValue;
        }

        void OnGUI()
        {
            if (_showMacroText)
            {
                var originalColor = GUI.color;
                GUI.color = Color.green;
                GUI.Label(_rect, _cachedText);
                GUI.color = originalColor;
            }
        }
    }
}