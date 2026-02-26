/*
 * 本文件基于 [AsyncInputOptimize] 的代码修改
 * 原始项目: [https://github.com/adofaiex/AsyncInputOptimize]
 * 原始许可证: GPL-3.0
 * 
 */
using System.Runtime.InteropServices;

namespace BaseMacro.Platform
{
    public static class Windows
    {
        [DllImport("Kernel32.dll")]
        public static extern void GetSystemTimePreciseAsFileTime(out long val);

        public static long GetFileTime()
        {
            GetSystemTimePreciseAsFileTime(out long res);
            return res + 50491123200_0000000;
        }
    }
}