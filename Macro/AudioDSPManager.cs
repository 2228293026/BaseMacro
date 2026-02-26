/*
 * 本文件基于 [AsyncInputOptimize] 的代码修改
 * 原始项目: [https://github.com/adofaiex/AsyncInputOptimize]
 * 原始许可证: GPL-3.0
 * 
 */
using BaseMacro.Platform;
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace BaseMacro
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
        private static double GetPreciseTime()
        {
            double res;

            long l = BaseSelect.GetFileTime();
            res = Time.captureFramerate > 0
                ? 1.0 / Time.captureFramerate
                : (l - lastTime) / SEC_2_TICK * (((long)(Time.timeScale * 1E6 + 0.1)) * 1E-6);
            lastTime = l;
            return res;
        }
        internal static void Update()
        {
            dspTime += GetPreciseTime();
            dspDeltaTime += AudioSettings.dspTime - dspTime;
            dspDeltaTimeCount++;
            double avg = dspDeltaTime / dspDeltaTimeCount;
            cpy_dspTime = dspTime + avg;
            if (avg > LOW_PRECISE || avg < (-LOW_PRECISE))
            {
                safe = false;
                dspTime = AudioSettings.dspTime - MID_PRECISE / 2;
                dspDeltaTime = 0;
                dspDeltaTimeCount = 0;
                dspErrorCounter = 0;
            }
            else if (avg > MID_PRECISE || avg < (-MID_PRECISE))
            {
                dspErrorCounter++;
                safe = false;
                if (dspErrorCounter >= MAX_ERROR_COUNT)
                {
                    dspTime = AudioSettings.dspTime - MID_PRECISE / 2;
                    dspDeltaTime = 0;
                    dspDeltaTimeCount = 0;
                    dspErrorCounter = 0;
                }
            }
            else if (dspDeltaTimeCount >= SINGLE_ROUND)
            {
                if (!safe && (avg > HIGH_PRECISE || avg< (-HIGH_PRECISE)))
                {
                    dspTime += avg;
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
        public static double GetDSPTime()
        {
            long l = BaseSelect.GetFileTime();
            return dspTime + (l - lastTime) / SEC_2_TICK;
        }
        public static long GetDSPTimeAsFileTime()
        {
            long l = BaseSelect.GetFileTime();
            double curr_dsp = dspTime + (l - lastTime) / SEC_2_TICK;
            return (long)(curr_dsp * SEC_2_TICK);
        }
    }
}
