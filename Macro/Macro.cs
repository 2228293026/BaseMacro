using BaseMacro;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

#nullable enable

namespace BaseMacro.Macro
{
#pragma warning disable CS0420
    #region TimeBasedMacro

    internal static class Macro
    {
        // ─────────────────────────────────────────────
        //  主线程专属数据
        // ─────────────────────────────────────────────
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;
        private static scrFloor[]? cachedFloors;
        private static bool initialized = false;
        private static string lastKeysSetting = "";
        private static readonly List<byte> keyCodes = new(4);
        private static int _keyCodesVersion = 0;

        // ─────────────────────────────────────────────
        //  只读共享数据（初始化后不变）
        // ─────────────────────────────────────────────
        private static double[]? triggerTimes;
        private static int floorCount;

        // ─────────────────────────────────────────────
        //  时间锚点
        //
        //  时钟架构（三层）：
        //
        //  层1 - DSPTimeSimulater（主线程独占）
        //        每帧做漂移修正，提供长期精度。但其字段（m_dspTime/m_lastTime）是
        //        普通 static double，工作线程直接调用 GetDSPTime() 是数据竞争：
        //        32-bit Mono 下 double 读写非原子 → 撕裂读。
        //        + GetDSPTime() 内部每次调用 BaseSelect.GetFileTime()（Win32 系统调用），
        //          在工作线程热路径里比 QPC 慢 5 倍以上。
        //
        //  层2 - QPC（工作线程插值）
        //        主线程在采 DSP 快照后立刻采 QPC，一起写入 anchor。
        //        工作线程只做 (QPC_now - qpcSnapshot) * perfFreqInv，零系统调用，~20ns。
        //
        //  层3 - anchor 双缓冲（发布协议）
        //        主线程写 inactive anchor，Volatile.Write 发布，工作线程读。
        //
        //  公式：
        //    audioNow = songPosRef
        //             + (dspSnapshot + (QPC_now - qpcSnapshot) * perfFreqInv - dspTimeRef) * pitch
        //
        //  好处：DSP 长期校准 + QPC 帧内精度 + 零竞争
        // ─────────────────────────────────────────────
        private sealed class TimeAnchor
        {
            public double songPosRef;
            public double dspTimeRef;
            public double dspSnapshot;
            public long qpcSnapshot;
            public double pitch;
            public double timeOffset;
            public bool simulateKeyPress;
            public double[]? triggerTimes;
            public scrFloor[]? floors;
            public byte[] keyCodesSnapshot;
            public int keyCodesVersion;
            // valid 改为字段（引用类型字段），以支持 Volatile.Write
            public int validFlag;   // 0=false, 1=true（int 可 Volatile 操作）
            // 静态数据版本号，跳过热帧对不变字段的重复赋值
            public int staticVersion;

            public bool valid
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => Volatile.Read(ref validFlag) == 1;
            }

            public TimeAnchor() { keyCodesSnapshot = []; }
        }

        private static readonly TimeAnchor _anchorA = new();
        private static readonly TimeAnchor _anchorB = new();
        private static volatile TimeAnchor _currentAnchor = _anchorA;

        private static double _songPosRef;
        private static double _dspTimeRef;
        private static float _lastPitch;

        // 静态数据版本号（triggerTimes/floors），reinit 时递增，
        // 避免主线程热帧每次都重写不变字段
        private static int _staticAnchorVersion = 0;

        // ─────────────────────────────────────────────
        //  OPT-1: 预计算 QPC 频率倒数，将内层热路径除法 (~5-10ns) 改为乘法 (~1-2ns)
        //         同时消除 usePerfCounter 分支判断。
        //         perfFrequency == 0 时（QueryPerformanceFrequency 失败）回退 100ns 单位。
        // ─────────────────────────────────────────────
        private static readonly double perfFreqInv;

        // ─────────────────────────────────────────────
        //  工作线程 → 主线程反馈
        // ─────────────────────────────────────────────
        private static volatile int _workerLastTriggeredFloor = -1;
        private static volatile int _workerKeyIndex = 0;
        private static volatile int _workerNeedsHit = 0;

        private static volatile int _resetVersion = 0;

        // ─────────────────────────────────────────────
        //  工作线程控制
        // ─────────────────────────────────────────────
        private static volatile Thread? _workerThread;
        private static volatile bool _workerRunning = false;
        private static readonly SemaphoreSlim _startSignal = new(0, 1);

        // 用简单 flag 替代每帧检查 CurrentCount，避免重复内核调用
        private static volatile bool _workerStarted = false;

        private static byte _pendingKey;
        private static bool _isKeyDown;

        private static volatile bool _cachedSkyHookMode = false;

        // OPT-2: 缓存 HighPrecisionTime 设置，避免热路径每次解引用 Main.Settings 对象
        private static volatile bool _cachedHighPrecision = false;

        private static volatile bool skyHookInitialized = false;

        // ─────────────────────────────────────────────
        //  Win32
        // ─────────────────────────────────────────────
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0;
        private const uint KEYEVENTF_KEYUP = 2;

        [ThreadStatic]
        private static SkyHookSystem.INPUT _cachedInput;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, IntPtr pInputs, int cbSize);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);
        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long lpPerformanceCount);
        [DllImport("Kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long lpFrequency);

        // timeBeginPeriod/timeEndPeriod，把 Sleep(1) 的实际精度从 ~15.6ms 压到 ~1ms
        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint uPeriod);
        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint uPeriod);

        private static readonly long perfFrequency;
        private static readonly bool usePerfCounter;
        private static readonly byte[] scanCodeCache = new byte[256];

        private static readonly Dictionary<string, byte> KeyNameToCode = new()
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
            ["0"] = 0x30,
            ["1"] = 0x31,
            ["2"] = 0x32,
            ["3"] = 0x33,
            ["4"] = 0x34,
            ["5"] = 0x35,
            ["6"] = 0x36,
            ["7"] = 0x37,
            ["8"] = 0x38,
            ["9"] = 0x39,
            ["F1"] = 0x70,
            ["F2"] = 0x71,
            ["F3"] = 0x72,
            ["F4"] = 0x73,
            ["F5"] = 0x74,
            ["F6"] = 0x75,
            ["F7"] = 0x76,
            ["F8"] = 0x77,
            ["F9"] = 0x78,
            ["F10"] = 0x79,
            ["F11"] = 0x7A,
            ["F12"] = 0x7B,
            ["LEFT"] = 0x25,
            ["UP"] = 0x26,
            ["RIGHT"] = 0x27,
            ["DOWN"] = 0x28,
            ["CTRL"] = 0x11,
            ["LCTRL"] = 0xA2,
            ["RCTRL"] = 0xA3,
            ["SHIFT"] = 0x10,
            ["LSHIFT"] = 0xA0,
            ["RSHIFT"] = 0xA1,
            ["ALT"] = 0x12,
            ["LALT"] = 0xA4,
            ["RALT"] = 0xA5,
            ["WIN"] = 0x5B,
            ["LWIN"] = 0x5B,
            ["RWIN"] = 0x5C,
            ["SPACE"] = 0x20,
            ["ENTER"] = 0x0D,
            ["ESC"] = 0x1B,
            ["TAB"] = 0x09,
            ["BACKSPACE"] = 0x08,
            ["DELETE"] = 0x2E,
            ["INSERT"] = 0x2D,
            ["HOME"] = 0x24,
            ["END"] = 0x23,
            ["PAGEUP"] = 0x21,
            ["PAGEDOWN"] = 0x22,
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Macro()
        {
            usePerfCounter = QueryPerformanceFrequency(out perfFrequency);

            // OPT-1: 预计算倒数，后续所有 QPC 时间换算均用乘法
            perfFreqInv = (usePerfCounter && perfFrequency > 0)
                ? 1.0 / perfFrequency
                : 1e-7;  // 100ns 单位回退

            for (int i = 0; i < 256; i++)
                scanCodeCache[i] = (byte)MapVirtualKey((uint)i, 0);
        }

        // ═══════════════════════════════════════════════════════════════
        //  主线程：每帧只写锚点，不做触发决策
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            var settings = Main.Settings;

            if (!settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
            {
                StopWorkerIfNeeded();
                return;
            }

            EnsureWorkerRunning();

            if (settings.SkyHookMode != skyHookInitialized)
                SwitchMode(settings.SkyHookMode);

            // OPT-2: 每帧同步缓存，避免工作线程热路径解引用 Main.Settings
            _cachedHighPrecision = settings.HighPrecisionTime;

            if (!initialized)
            {
                Initialize();
                if (!initialized) return;
            }
            else if (NeedReinitialize())
            {
                ResetState(controller);
                Initialize();
                if (!initialized) return;
            }

            // 先 volatile 读做快速路径，避免每帧无条件执行 Interlocked 全栅障
            if (Volatile.Read(ref _workerNeedsHit) != 0)
            {
                int hitCount = Interlocked.Exchange(ref _workerNeedsHit, 0);
                for (int h = 0; h < hitCount; h++) controller!.Hit(false);
            }

#if DEBUG
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
#endif
            float pitch = conductor!.song.pitch;

            double dspSnap = DSPTimeSimulater.GetDSPTime();
            long qpcSnap = GetRawTicks();

            if (pitch != _lastPitch)
            {
                _dspTimeRef = dspSnap;
                _songPosRef = conductor!.songposition_minusi;
                _lastPitch = pitch;
            }

            var anchor = ReferenceEquals(_currentAnchor, _anchorA) ? _anchorB : _anchorA;

            // 只更新每帧都会变化的时钟字段；
            // triggerTimes/floors 等静态数据只在版本号变化时更新，避免热帧无效赋值
            anchor.songPosRef = _songPosRef;
            anchor.dspTimeRef = _dspTimeRef;
            anchor.dspSnapshot = dspSnap;
            anchor.qpcSnapshot = qpcSnap;
            anchor.pitch = pitch;
            anchor.timeOffset = settings.TimeOffset * 0.001;
            anchor.simulateKeyPress = settings.SimulateKeyPress;

            if (anchor.staticVersion != _staticAnchorVersion)
            {
                anchor.triggerTimes = triggerTimes;
                anchor.floors = cachedFloors;
                anchor.staticVersion = _staticAnchorVersion;
            }

            if (anchor.keyCodesVersion != _keyCodesVersion)
            {
                if (anchor.keyCodesSnapshot.Length != keyCodes.Count)
                    anchor.keyCodesSnapshot = new byte[keyCodes.Count];
                keyCodes.CopyTo(anchor.keyCodesSnapshot, 0);
                anchor.keyCodesVersion = _keyCodesVersion;
            }

            // valid 通过 Volatile.Write 写 int 字段，保证工作线程读到正确值
            // 且写入顺序在 _currentAnchor 发布之前（防 CPU 乱序）
            Volatile.Write(ref anchor.validFlag, 1);
            Volatile.Write(ref _currentAnchor, anchor);

            // 用 _workerStarted flag 替代每帧检查 CurrentCount，
            // 彻底消除重复内核调用
            if (!_workerStarted)
            {
                _workerStarted = true;
                _startSignal.Release();
            }

#if DEBUG
            Log($"[Macro-Main] 锚点已发布 pitch={pitch} lastFloor={lastFloor}");
#endif
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程：自旋 + 精确计时触发
        //
        //  ──────────────────────────────────────────────────────────
        //  爆发高 BPM 解决方案
        //  ──────────────────────────────────────────────────────────
        //
        //  1. for 循环不自动递增 i，只有成功触发后才 i++
        //  2. audioNow 在每次循环体内用 QPC 重算（~20ns，无系统调用）
        //  3. 对同一个 floor 反复重入实现精确等待：
        //       waitSec > 5ms  → Sleep(1) + break（回外层刷新 anchor）
        //       waitSec > 2ms  → SpinWait + continue（比 Yield 更可控）[OPT-3]
        //       waitSec ≤ 2ms  → 纯自旋 continue（最高精度，不离开 for）
        //  4. 触发后尝试接收新 anchor（爆发段跨帧时刷新 DSP 基准）
        //  5. OPT-4: 外层缓存 lastSeenAnchor，锚点未变时跳过局部变量重拷贝
        //  6. OPT-5: 自旋段每 64 次才做一次 resetVersion volatile read，
        //            减少不必要的内存屏障
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerLoop()
        {
            Log("[Macro-Worker] 工作线程启动");

            timeBeginPeriod(1);

            try
            {
                _startSignal.Wait();
                if (!_workerRunning) return;

                int localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                int localKeyIndex = Volatile.Read(ref _workerKeyIndex);
                int localResetVer = Volatile.Read(ref _resetVersion);

                while (_workerRunning)
                {
                    var anchor = Volatile.Read(ref _currentAnchor);

                    if (!anchor.valid || anchor.floors == null || anchor.triggerTimes == null)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    int curResetVer = Volatile.Read(ref _resetVersion);
                    if (curResetVer != localResetVer)
                    {
                        localResetVer = curResetVer;
                        localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                        localKeyIndex = Volatile.Read(ref _workerKeyIndex);
                        continue;
                    }

                    var times = anchor.triggerTimes;
                    var floors = anchor.floors;
                    int triggerCount = Math.Min(times.Length, floors.Length);

                    byte[] keys = anchor.keyCodesSnapshot;
                    int keyLen = keys.Length;
                    bool simulateKey = anchor.simulateKeyPress;
                    double timeOffset = anchor.timeOffset;
                    double pitch = anchor.pitch;
                    double songPosRef = anchor.songPosRef;
                    double dspTimeRef = anchor.dspTimeRef;
                    double dspSnapshot = anchor.dspSnapshot;
                    long qpcSnapshot = anchor.qpcSnapshot;

                    int hitCount = 0;
                    bool triggered = false;

                    if (localLastFloor >= triggerCount - 2)
                    {
                        int ver = Volatile.Read(ref _resetVersion);
                        for (int s = 0; s < 50 && _workerRunning
                             && Volatile.Read(ref _resetVersion) == ver; s++)
                            Thread.Sleep(1);
                        goto WriteBack;
                    }

                    for (int i = localLastFloor + 1; i < triggerCount; /* i++ 在触发后 */)
                    {
                        if (Volatile.Read(ref _resetVersion) != localResetVer)
                            goto WriteBack;

                        var floor = floors[i];
                        if (floor == null) { i++; continue; }

                        if (floor.nextfloor?.auto == true || floor.midSpin)
                        {
                            localLastFloor = i++;
                            triggered = true;
                            continue;
                        }

                        // OPT-1: 乘 perfFreqInv 代替除 perfFrequency
                        long qpcNow = GetRawTicks();
                        double dspElapsed = (double)(qpcNow - qpcSnapshot) * perfFreqInv;
                        double audioNow = songPosRef + (dspSnapshot + dspElapsed - dspTimeRef) * pitch;

                        double triggerAt = times[i] + timeOffset;

                        if (triggerAt > audioNow)
                        {
                            if (pitch <= 0.0) { Thread.Sleep(1); break; }
                            double waitSec = (triggerAt - audioNow) / pitch;

                            if (waitSec > 0.005) { Thread.Sleep(1); break; }
                            else if (waitSec > 0.002) Thread.Yield();
                            continue;
                        }

                        bool releaseOnly = false;
                        if (simulateKey && floor.holdLength > -1 && i + 1 < triggerCount)
                        {
                            var nf = floors[i + 1];
                            if (nf != null && nf.holdLength == -1) releaseOnly = true;
                        }

                        if (!simulateKey)
                        {
                            hitCount++;
                            Log($"[Macro-Worker] 请求 Hit() FloorIndex={i}");
                        }
                        else if (releaseOnly)
                        {
                            WorkerReleaseKey();
                        }
                        else
                        {
                            byte key = keys[localKeyIndex];
                            WorkerPressKey(key);
                            if (++localKeyIndex >= keyLen) localKeyIndex = 0;
                            Log($"[Macro-Worker] 按下 0x{key:X2} FloorIndex={i} audioNow={audioNow:F6}");
                        }

                        localLastFloor = i++;
                        triggered = true;

                        var freshAnchor = Volatile.Read(ref _currentAnchor);
                        if (!ReferenceEquals(freshAnchor, anchor) && freshAnchor.valid
                            && Volatile.Read(ref _resetVersion) == localResetVer)
                        {
                            anchor = freshAnchor;
                            dspSnapshot = anchor.dspSnapshot;
                            qpcSnapshot = anchor.qpcSnapshot;
                        }
                    }

                    if (_isKeyDown && localLastFloor >= triggerCount - 1)
                        WorkerReleaseKey();

                WriteBack:
                    if (triggered && Volatile.Read(ref _resetVersion) == localResetVer)
                    {
                        Volatile.Write(ref _workerLastTriggeredFloor, localLastFloor);
                        Volatile.Write(ref _workerKeyIndex, localKeyIndex);
                    }
                    if (hitCount > 0)
                        Interlocked.Add(ref _workerNeedsHit, hitCount);
                }
            }
            finally
            {
                timeEndPeriod(1);
                WorkerReleaseKey();
                Log("[Macro-Worker] 工作线程退出");
            }
        }
        // ═══════════════════════════════════════════════════════════════
        //  按键操作
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerPressKey(byte keyCode)
        {
            if (_isKeyDown && _pendingKey != keyCode)
                WorkerReleaseKey();
            if (!_isKeyDown)
            {
                SendKey(keyCode, isDown: true);
                _pendingKey = keyCode;
                _isKeyDown = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerReleaseKey()
        {
            if (_isKeyDown)
            {
                SendKey(_pendingKey, isDown: false);
                _pendingKey = 0;
                _isKeyDown = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SendKey(byte keyCode, bool isDown)
        {
            if (_cachedSkyHookMode)
            {
                int result = AsyncInputManager.DirectPushKey(keyCode, isDown);
                if (result != 0)
                    Log($"[Macro-Worker] PushKeyEvent 失败 result={result} key=0x{keyCode:X2}");
                Log($"[Macro-Worker] SkyHook direct key=0x{keyCode:X2} down={isDown}");
            }
            else
            {
                _cachedInput.type = INPUT_KEYBOARD;
                _cachedInput.u.ki.wVk = keyCode;
                _cachedInput.u.ki.wScan = scanCodeCache[keyCode];
                _cachedInput.u.ki.dwFlags = isDown ? KEYEVENTF_KEYDOWN : KEYEVENTF_KEYUP;
                fixed (SkyHookSystem.INPUT* ptr = &_cachedInput)
                    SendInput(1, (IntPtr)ptr, sizeof(SkyHookSystem.INPUT));
                Log($"[Macro-Worker] SendInput key=0x{keyCode:X2} down={isDown}");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  初始化
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Initialize()
        {
            levelMaker = scrLevelMaker.instance;
            if (levelMaker?.listFloors == null || levelMaker.listFloors.Count == 0) return;

            cachedFloors = [.. levelMaker.listFloors];

            floorCount = cachedFloors.Length;
            triggerTimes = new double[floorCount];

            for (int i = 0; i < floorCount - 1; i++)
                triggerTimes[i] = cachedFloors[i + 1]?.entryTime ?? double.MaxValue;
            triggerTimes[floorCount - 1] = double.MaxValue;

            conductor = scrConductor.instance;
            ParseKeyCodes();
            initialized = true;

            // 静态数据版本递增，通知 anchor 更新 triggerTimes/floors
            _staticAnchorVersion++;

            _dspTimeRef = DSPTimeSimulater.GetDSPTime();
            long initQpc = GetRawTicks();
            _songPosRef = conductor!.songposition_minusi;
            _lastPitch = conductor.song.pitch;

            _anchorA.dspTimeRef = _anchorB.dspTimeRef = _dspTimeRef;
            _anchorA.songPosRef = _anchorB.songPosRef = _songPosRef;
            _anchorA.dspSnapshot = _anchorB.dspSnapshot = _dspTimeRef;
            _anchorA.qpcSnapshot = _anchorB.qpcSnapshot = initQpc;

            int syncFloor = SyncFloor(_songPosRef);
            Volatile.Write(ref _workerLastTriggeredFloor, syncFloor);
            Volatile.Write(ref _workerKeyIndex, 0);

            Log("[Macro-Main] 初始化完成");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int SyncFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Length == 0) return -1;
            int left = 0, right = triggerTimes.Length - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (triggerTimes[mid] < currentTime) left = mid + 1;
                else if (triggerTimes[mid] > currentTime) right = mid - 1;
                else return mid;
            }
            return left - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize()
        {
            return levelMaker?.listFloors.Count != floorCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ParseKeyCodes()
        {
            string keysSetting = Main.Settings.MacroKeys ?? "J";
            if (keysSetting == lastKeysSetting && keyCodes.Count > 0) return;

            lastKeysSetting = keysSetting;
            keyCodes.Clear();

            foreach (string part in keysSetting.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                string keyName = part.Trim().ToUpperInvariant();
                if (string.IsNullOrEmpty(keyName)) continue;
                if (keyName.Length == 1)
                {
                    char c = keyName[0];
                    if (c is >= 'A' and <= 'Z') { keyCodes.Add((byte)c); continue; }
                    if (c is >= '0' and <= '9') { keyCodes.Add((byte)c); continue; }
                }
                if (KeyNameToCode.TryGetValue(keyName, out byte code))
                    keyCodes.Add(code);
            }
            if (keyCodes.Count == 0) keyCodes.Add(0x4A);

            _keyCodesVersion++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyHoldBehavior(scrController controller)
        {
            if (controller == null || !Main.Settings.Macro) return;
            bool simulate = Main.Settings.SimulateKeyPress;
            controller.requireHolding = simulate && Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
        }

        // ═══════════════════════════════════════════════════════════════
        //  生命周期
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Reset(scrController controller) => ResetState(controller);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ResetState(scrController? controller)
        {
            initialized = false;
            triggerTimes = null;
            cachedFloors = null;
            levelMaker = null;
            conductor = null;

            Volatile.Write(ref _workerLastTriggeredFloor, -1);
            Volatile.Write(ref _workerKeyIndex, 0);
            Interlocked.Exchange(ref _workerNeedsHit, 0);

            // valid 改为 Volatile.Write(ref int)，保证写入顺序在 resetVersion 递增之前，
            // 防止 CPU 乱序导致工作线程读到 valid=true 但 resetVersion 已经改变
            Volatile.Write(ref _anchorA.validFlag, 0);
            Volatile.Write(ref _anchorB.validFlag, 0);

            Interlocked.Increment(ref _resetVersion);

            if (skyHookInitialized)
                AsyncInputManager.ClearQueue();

            if (controller != null)
                ApplyHoldBehavior(controller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureWorkerRunning()
        {
            if (_workerRunning && _workerThread?.IsAlive == true) return;

            _workerRunning = true;
            _workerStarted = false;   // 重置启动 flag，确保新线程能收到信号
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Priority = System.Threading.ThreadPriority.Highest,
                Name = "MacroWorkerThread"
            };
            _workerThread.Start();
            Log("[Macro-Main] 工作线程已启动");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StopWorkerIfNeeded()
        {
            if (!_workerRunning) return;

            if (skyHookInitialized)
                _cachedSkyHookMode = false;

            _workerRunning = false;

            // FIX-BUG: 用 try-catch 防止 SemaphoreFullException（maxCount=1）
            //          旧代码若工作线程尚未消费信号就触发第二次 Release 会抛异常
            if (!_workerStarted)
            {
                _workerStarted = true;
                try { _startSignal.Release(); }
                catch (SemaphoreFullException) { /* 信号已满，工作线程会自行醒来 */ }
            }

            _workerThread?.Join(50);

            if (skyHookInitialized)
            {
                AsyncInputManager.Stop();
                skyHookInitialized = false;
            }
            Log("[Macro-Main] 工作线程已停止");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwitchMode(bool useSkyHook)
        {
            if (useSkyHook == skyHookInitialized) return;
            Log($"[Macro-Main] 切换模式: {(useSkyHook ? "SkyHook" : "SendInput")}");

            if (useSkyHook)
            {
                AsyncInputManager.Start();
                if (!AsyncInputManager.IsInitialized)
                {
                    Log("[Macro-Main] SkyHook 启动失败，回退到 SendInput");
                    Main.Settings.SkyHookMode = false;
                    return;
                }
                skyHookInitialized = true;
                _cachedSkyHookMode = true;
                Main.Settings.SkyHookMode = true;
            }
            else
            {
                _cachedSkyHookMode = false;
                AsyncInputManager.Stop();
                skyHookInitialized = false;
                Main.Settings.SkyHookMode = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  计时器
        //  OPT-2: 读 _cachedHighPrecision 而非每次解引用 Main.Settings
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetRawTicks()
        {
            if (_cachedHighPrecision)
                return DSPTimeSimulater.GetDSPTimeAsFileTime();
            return GetTicks();
        }

        // QPC 原始值，用于工作线程帧内插值（~20ns，无系统调用开销）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long c)) return c;
            return DateTime.UtcNow.Ticks;
        }

        // ═══════════════════════════════════════════════════════════════
        //  输入调整
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HandleInput()
        {
            if (!Main.Settings.Macro ||
                ADOBase.sceneName == GCNS.sceneLevelSelect ||
                ADOBase.controller.paused) return;

            bool ctrl = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrl && Main.Settings.EnableKeyAdjust)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep - 0.1f, 0.1f, 10f);
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                    Main.Settings.AdjustStep = Mathf.Clamp(Main.Settings.AdjustStep + 0.1f, 0.1f, 10f);
            }
            else if (!ctrl && Main.Settings.EnableArrowTimeAdjust)
            {
                if (Input.GetKeyDown(KeyCode.LeftArrow))
                    Main.Settings.TimeOffset -= Main.Settings.AdjustStep;
                else if (Input.GetKeyDown(KeyCode.RightArrow))
                    Main.Settings.TimeOffset += Main.Settings.AdjustStep;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  日志（仅 DEBUG）
        // ═══════════════════════════════════════════════════════════════
        [System.Diagnostics.Conditional("DEBUG")]
        public static void Log(string message) => Main.Mod?.Logger.Log(message);
    }

    #endregion
#pragma warning restore CS0420
}