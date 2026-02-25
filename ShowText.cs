using UnityEngine;
using System;

namespace BaseMacro
{
    public class ShowText : MonoBehaviour
    {
        private bool _showMacroText;
        private readonly string _cachedText = "<color=green>Macro is enabled!</color>";
        private Rect _rect;

        public void Awake()
        {
            _rect = new Rect(
                0,
                0,
                200, 20
            );
            // 初始化状态
            _showMacroText = Main.Settings.Macro;
        }

        public void OnEnable()
        {
            // 订阅事件
            Main.Settings?.OnMacroChanged += OnMacroChanged;
        }

        public void OnDisable()
        {
            // 取消订阅
            Main.Settings?.OnMacroChanged -= OnMacroChanged;
        }

        private void OnMacroChanged(bool newValue)
        {
            _showMacroText = newValue;
        }

        public void OnGUI()
        {
            if (_showMacroText)
            {
                GUI.Label(_rect, _cachedText);
            }
        }
    }
}