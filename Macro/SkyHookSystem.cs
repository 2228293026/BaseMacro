using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

#nullable enable

namespace BaseMacro.Macro
{
    #region SkyHook 系统独立实现

    /// <summary>
    /// SkyHook 输入系统实现
    /// </summary>
    public static class SkyHookSystem
    {
        /// <summary>
        /// SkyHook 事件类型
        /// </summary>
        public enum EventType : uint
        {
            KeyPressed = 0x100,
            KeyReleased = 0x101,
            MouseMoved = 0x200,
            MouseButton = 0x201
        }

        /// <summary>
        /// SkyHook 按键标签
        /// </summary>
        public enum KeyLabel : uint
        {
            None = 0,
            Escape = 0x1B,
            Alpha0 = 0x30, Alpha1, Alpha2, Alpha3, Alpha4, Alpha5, Alpha6, Alpha7, Alpha8, Alpha9,
            A = 0x41, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
            F1 = 0x70, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
            Left = 0x25, Up, Right, Down,
            Space = 0x20, Return = 0x0D, Tab = 0x09,
            LControl = 0xA2, RControl = 0xA3,
            LShift = 0xA0, RShift = 0xA1,
            LAlt = 0xA4, RAlt = 0xA5
        }

        /// <summary>
        /// SkyHook 事件结构（完整版）
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SkyHookEvent
        {
            public long TimeSec;           // 秒
            public uint TimeSubsecNano;     // 纳秒
            public EventType Type;           // 事件类型
            public KeyLabel Label;           // 按键标签
            public ushort Key;               // 按键码
            public uint Flags;                // 标志位
            public uint ExtraInfo;            // 额外信息

            /// <summary>
            /// 创建 SkyHook 事件
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static SkyHookEvent Create(byte keyCode, bool isDown, long elapsedTicks)
            {
                var (sec, nano) = ConvertTicksToSkyHookTime(elapsedTicks);

                return new SkyHookEvent
                {
                    TimeSec = sec,
                    TimeSubsecNano = nano,
                    Type = isDown ? EventType.KeyPressed : EventType.KeyReleased,
                    Label = GetKeyLabel(keyCode),
                    Key = keyCode,
                    Flags = 0,
                    ExtraInfo = 0
                };
            }

            /// <summary>
            /// 将 100ns ticks 转换为 SkyHook 时间格式
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static (long sec, uint nano) ConvertTicksToSkyHookTime(long ticks)
            {
                // Windows Epoch (1601-01-01) 到 Unix Epoch (1970-01-01) 的差值
                const long EPOCH_DIFFERENCE = 116444736000000000; // 100-ns 间隔数

                // 转换为从1601开始的100-ns间隔数
                long windowsTicks = ticks + EPOCH_DIFFERENCE;

                // 转换为秒和纳秒
                long totalNano = windowsTicks * 100; // 100-ns -> ns
                long sec = totalNano / 1000000000;
                uint nano = (uint)(totalNano % 1000000000);

                return (sec, nano);
            }

            /// <summary>
            /// 获取按键对应的标签
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static KeyLabel GetKeyLabel(byte keyCode)
            {
                if (keyCode >= 0x30 && keyCode <= 0x39)
                    return (KeyLabel)((int)KeyLabel.Alpha0 + (keyCode - 0x30));

                if (keyCode >= 0x41 && keyCode <= 0x5A)
                    return (KeyLabel)((int)KeyLabel.A + (keyCode - 0x41));

                if (keyCode >= 0x70 && keyCode <= 0x7B)
                    return (KeyLabel)((int)KeyLabel.F1 + (keyCode - 0x70));

                return keyCode switch
                {
                    0x20 => KeyLabel.Space,
                    0x0D => KeyLabel.Return,
                    0x09 => KeyLabel.Tab,
                    0x25 => KeyLabel.Left,
                    0x26 => KeyLabel.Up,
                    0x27 => KeyLabel.Right,
                    0x28 => KeyLabel.Down,
                    0xA2 => KeyLabel.LControl,
                    0xA3 => KeyLabel.RControl,
                    0xA0 => KeyLabel.LShift,
                    0xA1 => KeyLabel.RShift,
                    0xA4 => KeyLabel.LAlt,
                    0xA5 => KeyLabel.RAlt,
                    _ => KeyLabel.None
                };
            }
        }

        /// <summary>
        /// Windows INPUT 结构定义
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }
    }
    #endregion
}
