/*
 * 本文件基于 [AsyncInputOptimize] 的代码修改
 * 原始项目: [https://github.com/adofaiex/AsyncInputOptimize]
 * 原始许可证: GPL-3.0
 * 
 */
using System;

namespace BaseMacro.Platform
{
    public static class BaseSelect
    {
        static unsafe BaseSelect()
        {
            if (UnityEngine.Application.platform is UnityEngine.RuntimePlatform.WindowsPlayer or UnityEngine.RuntimePlatform.WindowsServer or UnityEngine.RuntimePlatform.WindowsEditor)
                GetFileTime = &Windows.GetFileTime;
            else if (UnityEngine.Application.platform is UnityEngine.RuntimePlatform.LinuxPlayer or UnityEngine.RuntimePlatform.LinuxServer or UnityEngine.RuntimePlatform.LinuxEditor)
                GetFileTime = &Linux.GetFileTime;
            else
                GetFileTime = &Base;
        }
        public static long Base() => DateTime.Now.Ticks;
        public static unsafe delegate* managed<long> GetFileTime;
    }
}
