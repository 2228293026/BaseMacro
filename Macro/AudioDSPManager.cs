/*
 * 本文件基于 [AsyncInputOptimize] 的代码修改
 * 原始项目: [https://github.com/adofaiex/AsyncInputOptimize]
 * 原始许可证: GPL-3.0
 * 
 * 修复内容:
 * 1. 优化初始同步 - 直接使用真实dspTime作为起点
 * 2. 增加误差死区 - 微小误差忽略不计
 * 3. 平滑校正 - 避免硬重置
 */
using BaseMacro.Platform;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BaseMacro.Macro
{
    public static unsafe class AudioDSPManager
    {
        public const double HIGH_PRECISE = 1.0 / 750.0;
        public const double MID_PRECISE = 1.0 / 150.0;
        public const double LOW_PRECISE = 1.0 / 30.0;
        public const double SEC_2_TICK = 10000000.0;
        public const int MAX_ERROR_COUNT = 8;
        public const int SINGLE_ROUND = 750;

        public static double lastTime;
        public static double dspTime;
        public static double dspErrorCounter;
        public static double dspDeltaTime;
        public static int dspDeltaTimeCount;
        public static bool safe;
        public static double cpy_dspTime;

        // 新增：启动标志
        private static bool _isInitialized = false;
        // 新增：误差死区阈值
        private const double ERROR_DEADZONE = 0.00000001; // 0.00001ms 以下的误差忽略
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double GetPreciseTime()
        {
            double res;
            long l = BaseSelect.GetFileTime();

            if (Time.captureFramerate > 0)
            {
                res = 1.0 / Time.captureFramerate;
            }
            else
            {
                double timeScaleFactor = ((long)(Time.timeScale * 1E6 + 0.1)) * 1E-6;
                res = (l - lastTime) / SEC_2_TICK * timeScaleFactor;
            }

            lastTime = l;
            return res;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Update()
        {
            // 首次运行：直接同步到真实dspTime
            if (!_isInitialized)
            {
                dspTime = AudioSettings.dspTime;
                lastTime = BaseSelect.GetFileTime();
                _isInitialized = true;
                return;
            }

            double realDsp = AudioSettings.dspTime;

            // 累加模拟时间
            dspTime += GetPreciseTime();

            // 计算误差
            double error = realDsp - dspTime;

            // 误差死区：微小误差忽略
            if (Math.Abs(error) < ERROR_DEADZONE)
            {
                cpy_dspTime = dspTime;
                return;
            }

            // 原有的误差累积逻辑
            dspDeltaTime += error;
            dspDeltaTimeCount++;
            double avg = dspDeltaTime / dspDeltaTimeCount;
            cpy_dspTime = dspTime + avg;

            // 大误差：平滑校正而不是硬重置
            if (Math.Abs(avg) > LOW_PRECISE)
            {
                safe = false;
                // 只校正一半的误差，避免跳变
                dspTime += error * 0.5;
                dspDeltaTime = 0;
                dspDeltaTimeCount = 0;
                dspErrorCounter = 0;
            }
            else if (Math.Abs(avg) > MID_PRECISE)
            {
                dspErrorCounter++;
                safe = false;
                if (dspErrorCounter >= MAX_ERROR_COUNT)
                {
                    // 累积多次后，平滑校正
                    dspTime += error * 0.3;
                    dspDeltaTime = 0;
                    dspDeltaTimeCount = 0;
                    dspErrorCounter = 0;
                }
            }
            else if (dspDeltaTimeCount >= SINGLE_ROUND)
            {
                if (!safe && Math.Abs(avg) > HIGH_PRECISE)
                {
                    // 微调
                    dspTime += avg * 0.2;
                }
                else
                {
                    safe = true;
                }
                dspErrorCounter = 0;
                dspDeltaTime = 0;
                dspDeltaTimeCount = 0;
            }
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double GetDSPTime()
        {
            if (!_isInitialized)
            {
                return AudioSettings.dspTime;
            }

            long l = BaseSelect.GetFileTime();
            return dspTime + (l - lastTime) / SEC_2_TICK;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static long GetDSPTimeAsFileTime()
        {
            double time = GetDSPTime();
            return (long)(time * SEC_2_TICK);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset()
        {
            _isInitialized = false;
            dspErrorCounter = 0;
            dspDeltaTime = 0;
            dspDeltaTimeCount = 0;
            safe = false;
        }
    }
}