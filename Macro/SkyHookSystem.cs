using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

#nullable enable

namespace BaseMacro.Macro
{
    #region SkyHook 系统独立实现

    /// <summary>
    /// SkyHook 兼容层：按键结构改为对齐游戏内置 SkyHook 类型。
    /// </summary>
    public static class SkyHookSystem
    {
        private static readonly long EpochTicks = new DateTime(1970, 1, 1).Ticks;

        /// <summary>
        /// SkyHook 事件结构（与游戏内结构对齐，Label/Key 使用 Union 共享内存）
        /// </summary>
        [StructLayout(LayoutKind.Explicit, Pack = 1)]
        public struct SkyHookEvent
        {
            [FieldOffset(0)]
            public long TimeSec;

            [FieldOffset(8)]
            public uint TimeSubsecNano;

            [FieldOffset(12)]
            public global::SkyHook.EventType Type;

            // Union：Label 与 Key 共享同一段内存
            [FieldOffset(16)]
            public global::SkyHook.KeyLabel Label;

            [FieldOffset(16)]
            public ushort Key;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public long GetTimeInTicks() =>
                TimeSec * 10000000L + (long)(TimeSubsecNano / 100U) + EpochTicks;

            /// <summary>
            /// 创建 SkyHook 事件。
            /// elapsedTicks 输入单位为 100ns（DateTime ticks）。
            /// </summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static SkyHookEvent Create(byte keyCode, bool isDown, long elapsedTicks)
            {
                var (sec, nano) = ConvertTicksToSkyHookTime(elapsedTicks);
                return new SkyHookEvent
                {
                    TimeSec = sec,
                    TimeSubsecNano = nano,
                    Type = isDown ? global::SkyHook.EventType.KeyPressed : global::SkyHook.EventType.KeyReleased,
                    Key = keyCode
                };
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static (long sec, uint nano) ConvertTicksToSkyHookTime(long ticks)
            {
                long sec = ticks / 10000000L;
                uint nano = (uint)((ticks % 10000000L) * 100);
                return (sec, nano);
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
