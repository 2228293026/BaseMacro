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
        private static List<scrFloor>? cachedFloors;
        private static bool initialized = false;
        private static string lastKeysSetting = "";
        private static readonly List<byte> keyCodes = new(4);

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
        //        工作线程只做 (QPC_now - qpcSnapshot) / freq，零系统调用，~20ns。
        //
        //  层3 - anchor 双缓冲（发布协议）
        //        主线程写 inactive anchor，Volatile.Write 发布，工作线程读。
        //
        //  公式：
        //    audioNow = songPosRef
        //             + (dspSnapshot + (QPC_now - qpcSnapshot) / freq - dspTimeRef) * pitch
        //
        //  好处：DSP 长期校准 + QPC 帧内精度 + 零竞争
        // ─────────────────────────────────────────────
        private sealed class TimeAnchor
        {
            public double songPosRef;    // 参考点歌曲位置（秒），初始化时确定
            public double dspTimeRef;    // 参考点 DSP 时间（秒），初始化时确定
            public double dspSnapshot;   // 本帧主线程读取的 DSP 时间（工作线程不调用 GetDSPTime）
            public long qpcSnapshot;   // 与 dspSnapshot 同时采样的 QPC 原始值
            public double pitch;
            public double timeOffset;
            public bool simulateKeyPress;
            public double[]? triggerTimes;
            public List<scrFloor>? floors;
            public byte[] keyCodesSnapshot;
            public bool valid;
            public TimeAnchor() { keyCodesSnapshot = []; }
        }

        private static readonly TimeAnchor _anchorA = new();
        private static readonly TimeAnchor _anchorB = new();
        private static volatile TimeAnchor _currentAnchor = _anchorA; // ← 必须在 A/B 之后

        // 参考点（主线程独占写，仅在 Initialize 和 pitch 变化时更新）
        // 复制到每帧的 anchor 中，保证工作线程读到 (songPosRef, dspTimeRef, pitch) 一致快照
        private static double _songPosRef;
        private static double _dspTimeRef;
        private static float _lastPitch;

        // ─────────────────────────────────────────────
        //  工作线程 → 主线程反馈
        // ─────────────────────────────────────────────
        private static volatile int _workerLastTriggeredFloor = -1;
        private static volatile int _workerKeyIndex = 0;
        private static volatile int _workerNeedsHit = 0;

        // Bug3 修复：独立的 Reset 版本号，避免 -1 被工作线程的 triggered 写覆盖
        // 主线程每次 Reset 递增，工作线程检测版本变化来同步
        private static volatile int _resetVersion = 0;

        // ─────────────────────────────────────────────
        //  工作线程控制
        // ─────────────────────────────────────────────
        private static Thread? _workerThread;
        private static volatile bool _workerRunning = false;
        private static readonly SemaphoreSlim _startSignal = new(0, 1);

        // _pendingKey 始终与 _isKeyDown 同步（worker 线程独占），用 byte 代替 byte?
        // 消除 Nullable<byte>.HasValue 检查（冗余）和潜在装箱开销
        private static byte _pendingKey;
        private static bool _isKeyDown;

        // SkyHookMode 缓存：避免 SendKey 热路径每次读 Main.Settings（跨程序集属性访问）
        // 由 SwitchMode 和 StopWorkerIfNeeded 负责同步更新
        private static volatile bool _cachedSkyHookMode = false;


        // Unix epoch 基准（100ns 单位），用于将 DateTime.UtcNow.Ticks 转换为 SkyHookEvent 格式。
        // SkyHookEvent.TimeSec 是从1970-01-01开始的秒数，与 QPC 相对基准不兼容。
        // DateTime.UtcNow.Ticks - _unixEpochTicks = 从1970起的100ns滴答数，
        // 与游戏内 SkyHookEvent.GetTimeInTicks() 的重建逻辑完全对称，零误差。
        private static readonly long _unixEpochTicks =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
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

            int hitCount = Interlocked.Exchange(ref _workerNeedsHit, 0);
            for (int h = 0; h < hitCount; h++) controller!.Hit(false);

            // 仅 DEBUG 构建用于日志；Release 下 Log 被 [Conditional] 裁剪，volatile read 也随之省去
#if DEBUG
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
#endif
            float pitch = conductor!.song.pitch;

            // 主线程安全读 DSP，紧跟采 QPC 快照
            // 工作线程用 (QPC_now - qpcSnapshot) 做帧内插值，不直接访问 DSPTimeSimulater 的字段
            double dspSnap = DSPTimeSimulater.GetDSPTime();
            long qpcSnap = GetRawTicks();

            // 检测 pitch 变化（速率变化时重置参考点，防止歌曲位置公式漂移）
            if (pitch != _lastPitch)
            {
                _dspTimeRef = dspSnap;
                _songPosRef = conductor!.songposition_minusi;
                _lastPitch = pitch;
            }

            var anchor = ReferenceEquals(_currentAnchor, _anchorA) ? _anchorB : _anchorA;
            anchor.songPosRef = _songPosRef;
            anchor.dspTimeRef = _dspTimeRef;
            anchor.dspSnapshot = dspSnap;
            anchor.qpcSnapshot = qpcSnap;
            anchor.pitch = pitch;
            anchor.timeOffset = settings.TimeOffset * 0.001;
            anchor.simulateKeyPress = settings.SimulateKeyPress;
            anchor.triggerTimes = triggerTimes;
            anchor.floors = cachedFloors;
            anchor.valid = true;

            if (anchor.keyCodesSnapshot.Length != keyCodes.Count)
                anchor.keyCodesSnapshot = new byte[keyCodes.Count];
            keyCodes.CopyTo(anchor.keyCodesSnapshot, 0);

            Volatile.Write(ref _currentAnchor, anchor);

            if (_startSignal.CurrentCount == 0)
                _startSignal.Release();

#if DEBUG
            Log($"[Macro-Main] 锚点已发布 pitch={pitch} lastFloor={lastFloor}");
#endif
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程：自旋 + 精确计时触发
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerLoop()
        {
            Log("[Macro-Worker] 工作线程启动");

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
                    Thread.SpinWait(10);
                    continue;
                }

                // Bug3 修复：通过版本号检测 Reset，不依赖 -1 哨兵值
                // 版本号只递增，不会被工作线程的 triggered 写覆盖
                int curResetVer = Volatile.Read(ref _resetVersion);
                if (curResetVer != localResetVer)
                {
                    localResetVer = curResetVer;
                    localLastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
                    localKeyIndex = Volatile.Read(ref _workerKeyIndex);
                    // continue 强制回到循环顶部重新读 anchor：
                    // 此时第 270 行读到的是 Reset 前的旧 anchor（旧歌数据），
                    // 但 localLastFloor 已经被重置为 -1（下一首歌的起点）。
                    // 若不 continue，下面会用旧歌的 triggerTimes/floors 配合 -1 索引
                    // 从旧歌 floor 0 开始触发，产生幻按；若旧 audioNow > 旧 times[0]，
                    // 会立即触发一次错误的按键或 Hit()。
                    // continue 后下次循环读到 Initialize 刚发布的新 anchor，数据一致。
                    continue;
                }

                var times = anchor.triggerTimes;
                var floors = anchor.floors;
                int triggerCount = times.Length;
                byte[] keys = anchor.keyCodesSnapshot;
                bool simulateKey = anchor.simulateKeyPress;
                double timeOffset = anchor.timeOffset;
                double pitch = anchor.pitch;
                double localSongPosRef = anchor.songPosRef;
                double localDspTimeRef = anchor.dspTimeRef;
                double localDspSnapshot = anchor.dspSnapshot;
                long localQpcSnapshot = anchor.qpcSnapshot;
                bool hitNeeded = false;
                bool triggered = false;

                // 时间公式：DSP 基准 + QPC 帧内插值（零系统调用，~20ns）
                // dspNow ≈ dspSnapshot + (QPC_now - qpcSnapshot) / freq
                // audioNow = songPosRef + (dspNow - dspTimeRef) * pitch
                long qpcNow = GetRawTicks();
                double dspElapsed = usePerfCounter
                    ? (double)(qpcNow - localQpcSnapshot) / perfFrequency
                    : (double)(qpcNow - localQpcSnapshot) * 1e-7;
                double dspNow = localDspSnapshot + dspElapsed;
                double audioNow = localSongPosRef + (dspNow - localDspTimeRef) * pitch;

                for (int i = localLastFloor + 1; i < triggerCount; i++)
                {
                    var floor = floors[i];
                    if (floor == null) continue;

                    if (floor.nextfloor?.auto == true || floor.midSpin)
                    {
                        localLastFloor = i;
                        triggered = true;
                        continue;
                    }

                    double triggerAt = times[i] + timeOffset;

                    if (triggerAt > audioNow)
                    {
                        // pitch==0 时（暂停瞬间）除零崩溃；pitch 是 double，用 0.0
                        if (pitch <= 0.0) { Thread.Sleep(1); break; }
                        double waitSec = (triggerAt - audioNow) / pitch;
                        // 分级等待：保证精度的同时不空烧 CPU
                        // > 5ms  → Sleep(1)：OS 调度，几乎零 CPU，timeBeginPeriod(1) 保证 ~1ms 唤醒
                        // > 2ms  → Yield()：让步一次时间片，立即返回
                        // ≤ 2ms  → 纯自旋，保证触发精度
                        if (waitSec > 0.005) Thread.Sleep(1);
                        else if (waitSec > 0.002) Thread.Yield();
                        break;
                    }

                    // 到时间，触发
                    bool releaseOnly = false;
                    if (simulateKey && floor.holdLength > -1 && i + 1 < triggerCount)
                    {
                        var nf = floors[i + 1];
                        if (nf != null && nf.holdLength == -1)
                            releaseOnly = true;
                    }

                    if (!simulateKey)
                    {
                        hitNeeded = true;
                        localLastFloor = i;
                        Log($"[Macro-Worker] 请求 Hit() FloorIndex={i}");
                    }
                    else if (releaseOnly)
                    {
                        WorkerReleaseKey();
                        // Fix: 设 i 而非 i+1
                        // 之前设 i+1 会导致 floor i+1 在 triggerAt>audioNow 时 break，
                        // 下次外层 while 从 i+2 开始，floor i+1 (正常 tap) 永远不会触发。
                        // 设 i：for 循环自然走到 i+1，时机到则触发，未到则 break 留到下次。
                        localLastFloor = i;
                    }
                    else // keys.Length >= 1 由 ParseKeyCodes 保证（fallback 0x4A）
                    {
                        byte key = keys[localKeyIndex % keys.Length];
                        WorkerPressKey(key);
                        localKeyIndex = (localKeyIndex + 1) % keys.Length;
                        localLastFloor = i;
                        Log($"[Macro-Worker] 按下 0x{key:X2} FloorIndex={i} audioNow={audioNow:F6}");
                    }

                    triggered = true;
                }

                if (_isKeyDown && localLastFloor >= triggerCount - 1)
                    WorkerReleaseKey();

                if (triggered)
                {
                    Volatile.Write(ref _workerLastTriggeredFloor, localLastFloor);
                    Volatile.Write(ref _workerKeyIndex, localKeyIndex);
                }
                if (hitNeeded)
                    Interlocked.Increment(ref _workerNeedsHit);
                //Volatile.Write(ref _workerNeedsHit, 1); // 只需 release fence，无需 Interlocked 全栅
            }

            WorkerReleaseKey();
            Log("[Macro-Worker] 工作线程退出");
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

            cachedFloors = levelMaker.listFloors;
            floorCount = cachedFloors.Count;
            triggerTimes = new double[floorCount];

            for (int i = 0; i < floorCount - 1; i++)
                triggerTimes[i] = cachedFloors[i + 1]?.entryTime ?? double.MaxValue;
            triggerTimes[floorCount - 1] = double.MaxValue;

            conductor = scrConductor.instance;
            ParseKeyCodes();
            initialized = true;

            // 初始化参考点（主线程安全读 DSP，紧跟采 QPC）
            _dspTimeRef = DSPTimeSimulater.GetDSPTime();
            long initQpc = GetRawTicks();
            _songPosRef = conductor!.songposition_minusi;
            _lastPitch = conductor.song.pitch;
            _anchorA.dspTimeRef = _anchorB.dspTimeRef = _dspTimeRef;
            _anchorA.songPosRef = _anchorB.songPosRef = _songPosRef;
            _anchorA.dspSnapshot = _anchorB.dspSnapshot = _dspTimeRef;
            _anchorA.qpcSnapshot = _anchorB.qpcSnapshot = initQpc;

            int syncFloor = SyncFloor(_songPosRef);
            //SyncLastTriggeredFloor(_songPosRef);
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
        private static void SyncLastTriggeredFloor(double currentTime)
        {
            if (triggerTimes == null || triggerTimes.Length == 0) return;

            int left = 0, right = triggerTimes.Length - 1;
            while (left <= right)
            {
                int mid = (left + right) >> 1;
                if (triggerTimes[mid] < currentTime) left = mid + 1;
                else if (triggerTimes[mid] > currentTime) right = mid - 1;
                else { _workerLastTriggeredFloor = mid; return; }
            }
            _workerLastTriggeredFloor = left - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool NeedReinitialize()
        {
            // levelMaker 在 initialized=true 时必然非 null（Initialize 负责赋值）
            // ResetState 会把 initialized 设为 false，所以 levelMaker==null 时走不到这里
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
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ApplyHoldBehavior(scrController controller)
        {
            if (controller == null || !Main.Settings.Macro) return;
            // SimulateKeyPress=false 时，表达式短路已令结果为 false，第二个 if 是死代码
            controller.requireHolding = Main.Settings.SimulateKeyPress &&
                                        Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
            if (!Main.Settings.SimulateKeyPress)
                controller.requireHolding = false;
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

            // valid=false 必须在 Interlocked.Increment 之前写入：
            // 工作线程用 Volatile.Read(ref _resetVersion) 做 acquire fence，
            // 该 fence 只保证看到 Increment 之前的所有写入。
            // 若 valid=false 在 Increment 之后写，工作线程检测到版本变化时
            // 仍可能看到 valid=true，继续执行一轮 floor-0 触发（幻按）。
            // 正确顺序：valid=false → Increment（full fence）→ 工作线程 acquire → 看到 valid=false
            _anchorA.valid = false;
            _anchorB.valid = false;

            Interlocked.Increment(ref _resetVersion);

            // Macro.cs — ResetState
            if (skyHookInitialized)
                AsyncInputManager.ClearQueue(); // 原来无条件调用，现在加守卫

            if (controller != null)
                ApplyHoldBehavior(controller);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void EnsureWorkerRunning()
        {
            if (_workerRunning && _workerThread?.IsAlive == true) return;

            _workerRunning = true;
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

            // _cachedSkyHookMode=false 必须在 _workerRunning=false 之前写入：
            // 原因与 SwitchMode(false) 相同——工作线程以 _cachedSkyHookMode 作为门控。
            // Join(50) 最多等 50ms，超时后工作线程可能仍存活再跑一次循环；
            // 若 _cachedSkyHookMode 此时仍为 true，那次循环的 SendKey 会调
            // AsyncInputManager.EnqueueEvent，而 Stop() 之后队列已销毁 → 行为未定义。
            // 先写 false（volatile release fence）→ 工作线程不再进入 SkyHook 路径 →
            // 再 Stop() 时队列无新写入者，安全。
            if (skyHookInitialized)
                _cachedSkyHookMode = false;

            _workerRunning = false;

            // Release 前检查 CurrentCount，避免 SemaphoreFullException
            if (_startSignal.CurrentCount == 0)
                _startSignal.Release();

            // 等旧线程真正退出后再返回，防止新线程启动时两个线程同时
            // 访问 _isKeyDown/_pendingKey（无同步的静态字段），造成双重 keydown 或漏 keyup。
            // 超时 50ms：正常情况工作线程在下一个 Sleep(1) 醒来后立即退出，几 ms 内完成。
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
                // SendKey 现在用 DateTime.UtcNow.Ticks - _unixEpochTicks 直接生成 Unix 时间戳，
                // 不依赖任何需要在 _cachedSkyHookMode=true 之前写入的基准字段，
                // 因此这里只需直接设置 _cachedSkyHookMode=true 即可。
                _cachedSkyHookMode = true;
                Main.Settings.SkyHookMode = true;
            }
            else
            {
                // _cachedSkyHookMode=false 必须在 AsyncInputManager.Stop() 之前写入：
                // 工作线程以 _cachedSkyHookMode 作为进入 SkyHook 分支的门控。
                // 若先调 Stop() 再写 false，存在窗口期：工作线程已读到 true、
                // 正在执行 SendKey SkyHook 路径时，Stop() 把队列销毁，
                // EnqueueEvent 操作一个已停止的队列，行为未定义。
                // 先写 false（volatile release fence）→ 工作线程下次读到 false 后不再入队 →
                // 再 Stop() 时队列已无新写入者，安全。
                _cachedSkyHookMode = false;
                AsyncInputManager.Stop();
                skyHookInitialized = false;
                Main.Settings.SkyHookMode = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  计时器
        // ═══════════════════════════════════════════════════════════════

        // QPC 原始值，用于工作线程帧内插值（~20ns，无系统调用开销）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetRawTicks()
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