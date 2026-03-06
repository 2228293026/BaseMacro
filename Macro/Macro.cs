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
        //  旧架构：主线程发布"触发窗口 [now, now+frameDelta]"
        //           工作线程在帧开始时把所有在窗口内的按键一次性全发出去
        //           → 本该均匀分布的按键全部堆在帧开始，时序错误
        //
        //  新架构：主线程发布"此刻音频时间 + 此刻系统时间 + 播放速率"
        //           工作线程用锚点推算任意时刻的精确音频时间
        //           → 工作线程自己掐表，在每个按键该触发的精确时刻发出
        //           → 触发精度从"帧级（16ms）"提升到"QPC级（~100ns）"
        // ─────────────────────────────────────────────
        private sealed class TimeAnchor
        {
            public double audioTime;       // 锚点时刻的音频时间（秒）
            public long systemTicks;     // 锚点时刻的 QPC 原始值
            public double pitch;           // 播放速率
            public double timeOffset;      // 用户偏移（秒）
            public bool simulateKeyPress;
            public double[]? triggerTimes;
            public List<scrFloor>? floors;
            public byte[] keyCodesSnapshot;
            // keyIndexSnapshot 已移除：工作线程用本地 localKeyIndex 维护，此字段从未被读取
            public bool valid;
            public TimeAnchor() { keyCodesSnapshot = []; }
        }

        private static TimeAnchor _anchorA = new();
        private static TimeAnchor _anchorB = new();
        private static volatile TimeAnchor _currentAnchor = new();

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

        // ─────────────────────────────────────────────
        //  按键状态（仅工作线程访问）
        // ─────────────────────────────────────────────
        private static byte? _pendingKey;
        private static bool _isKeyDown;

        // SkyHookMode 缓存：避免 SendKey 热路径每次读 Main.Settings（跨程序集属性访问）
        // 由 SwitchMode 和 StopWorkerIfNeeded 负责同步更新
        private static volatile bool _cachedSkyHookMode = false;


        // 注意：C# 不允许 volatile long（CS0677）。
        // 用 Interlocked.Read / Interlocked.Exchange 保证 64-bit 原子性（兼容 32-bit Mono）。
        private static long startTimeTicks;
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

            if (Interlocked.Exchange(ref _workerNeedsHit, 0) != 0)
            {
                controller!.Hit(false);
                Log("[Macro-Main] controller.Hit() 已调用");
            }

            // audioTime 先读，nowTicks 紧跟其后
            // 锚点含义：「在 systemTicks 时刻，音频位置是 audioTime」
            // 先读 audioTime 再立即采样 ticks，使 systemTicks 尽量贴近 audioTime 的读取时刻
            double audioTime = conductor!.songposition_minusi;
            long nowTicks = GetRawTicks();
            float pitch = conductor.song.pitch;

            // 仅 DEBUG 构建用于日志；Release 下 Log 被 [Conditional] 裁剪，volatile read 也随之省去
#if DEBUG
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
#endif

            var anchor = ReferenceEquals(_currentAnchor, _anchorA) ? _anchorB : _anchorA;
            anchor.audioTime = audioTime;
            anchor.systemTicks = nowTicks;
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
            Log($"[Macro-Main] 锚点已发布 audioTime={audioTime:F6}s lastFloor={lastFloor}");
#endif
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程：自旋 + 精确计时触发
        // ═══════════════════════════════════════════════════════════════
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
                }

                var times = anchor.triggerTimes;
                var floors = anchor.floors;
                int triggerCount = times.Length;
                byte[] keys = anchor.keyCodesSnapshot;
                bool simulateKey = anchor.simulateKeyPress;
                double timeOffset = anchor.timeOffset;
                double pitch = anchor.pitch;
                // Bug修复：将 audioTime 和 systemTicks 复制到局部变量
                // 双缓冲只保证"切换时不写当前缓冲"，但工作线程持有 anchor 引用跨越 Sleep(1) 时，
                // 主线程可能在 2 帧后轮转回来覆写同一个对象的这两个字段（每帧都变化）。
                // 其余字段（triggerTimes/floors/keys）帧间不变或已为独立引用，无此问题。
                double localAudioTime = anchor.audioTime;
                long localSystemTicks = anchor.systemTicks;
                bool hitNeeded = false;
                bool triggered = false;

                long nowTicks = GetRawTicks();
                double elapsed = usePerfCounter
                    ? (double)(nowTicks - localSystemTicks) / perfFrequency
                    : (double)(nowTicks - localSystemTicks) * 1e-7;
                double audioNow = localAudioTime + elapsed * pitch;

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
                        // Bug3 修复：pitch==0 时（暂停瞬间）除零崩溃
                        if (pitch <= 0f) { Thread.Sleep(1); break; }
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
                    else if (keys.Length > 0)
                    {
                        byte key = keys[localKeyIndex % keys.Length];
                        WorkerPressKey(key);
                        localKeyIndex = (localKeyIndex + 1) % keys.Length;
                        localLastFloor = i;
                        Log($"[Macro-Worker] 按下 0x{key:X2} FloorIndex={i} audioNow={audioNow:F6}");
                    }
                    else
                    {
                        localLastFloor = i;
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
                    Volatile.Write(ref _workerNeedsHit, 1); // 只需 release fence，无需 Interlocked 全栅
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
            if (_isKeyDown && _pendingKey.HasValue && _pendingKey.Value != keyCode)
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
            if (_isKeyDown && _pendingKey.HasValue)
            {
                SendKey(_pendingKey.Value, isDown: false);
                _pendingKey = null;
                _isKeyDown = false;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void SendKey(byte keyCode, bool isDown)
        {
            if (_cachedSkyHookMode)
            {
                // Bug2 修复：SkyHookEvent.Create 期望 100ns 单位（FileTime格式）
                // 使用 GetAs100Ns() 保证单位正确
                long elapsed = GetAs100Ns() - Interlocked.Read(ref startTimeTicks);
                AsyncInputManager.EnqueueEvent(
                    SkyHookSystem.SkyHookEvent.Create(keyCode, isDown, elapsed));
                Log($"[Macro-Worker] SkyHook key=0x{keyCode:X2} down={isDown}");
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

            int syncFloor = SyncFloor(conductor!.songposition_minusi);
            Volatile.Write(ref _workerLastTriggeredFloor, syncFloor);
            Volatile.Write(ref _workerKeyIndex, 0);

            if (Main.Settings.SkyHookMode)
                Interlocked.Exchange(ref startTimeTicks, GetAudioSyncTicks());

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
            var lm = levelMaker ?? scrLevelMaker.instance;
            return lm?.listFloors == null || lm.listFloors.Count != floorCount;
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
            controller.requireHolding = Main.Settings.SimulateKeyPress &&
                                        Persistence.holdBehavior < HoldBehavior.NoHoldNeeded;
            if (!Main.Settings.SimulateKeyPress)
                controller.requireHolding = false;
        }

        // ═══════════════════════════════════════════════════════════════
        //  生命周期
        // ═══════════════════════════════════════════════════════════════
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

            // Bug3 修复：递增版本号通知工作线程 Reset
            // 工作线程检测版本变化后读取新的 _workerLastTriggeredFloor(-1) 和 _workerKeyIndex(0)
            // 不会被工作线程的 triggered 写覆盖（版本号只主线程写）
            Interlocked.Increment(ref _resetVersion);

            var anchor = Volatile.Read(ref _currentAnchor);
            Volatile.Write(ref anchor.valid, false); // 确保工作线程在读取 _resetVersion 之后能看到 valid=false

            AsyncInputManager.ClearQueue();

            if (Main.Settings.SkyHookMode)
                Interlocked.Exchange(ref startTimeTicks, GetAudioSyncTicks());

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
                Priority = System.Threading.ThreadPriority.AboveNormal,
                Name = "MacroWorkerThread"
            };
            _workerThread.Start();
            Log("[Macro-Main] 工作线程已启动");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void StopWorkerIfNeeded()
        {
            if (!_workerRunning) return;
            _workerRunning = false;

            // Bug4 修复：Release 前检查 CurrentCount，避免 SemaphoreFullException
            if (_startSignal.CurrentCount == 0)
                _startSignal.Release();

            // Bug2 修复：等旧线程真正退出后再返回，防止新线程启动时两个线程同时
            // 访问 _isKeyDown/_pendingKey（无同步的静态字段），造成双重 keydown 或漏 keyup。
            // 超时 50ms：正常情况工作线程在下一个 Sleep(1) 醒来后立即退出，几 ms 内完成。
            _workerThread?.Join(50);

            if (skyHookInitialized)
            {
                AsyncInputManager.Stop();
                skyHookInitialized = false;
                _cachedSkyHookMode = false;
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
                Interlocked.Exchange(ref startTimeTicks, GetAudioSyncTicks());
                Main.Settings.SkyHookMode = true;
            }
            else
            {
                AsyncInputManager.Stop();
                skyHookInitialized = false;
                _cachedSkyHookMode = false;
                Main.Settings.SkyHookMode = false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        //  计时器
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetAudioSyncTicks() =>
            Main.Settings.HighPrecisionTime
                ? DSPTimeSimulater.GetDSPTimeAsFileTime()
                : GetAs100Ns(); // 返回 100ns 单位，与 FileTime 格式一致

        // 返回 100ns 单位（FileTime 格式），用于 SkyHook elapsed 计算
        // Fix: 先除再乘，避免 counter * 10_000_000L 在 TSC-based QPC（~3GHz）下
        //      约 30 分钟后 long 溢出（3e9 * 10_000_000 * 1800 ≈ 5.4e19 > long.MaxValue）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetAs100Ns()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long counter))
                return (counter / perfFrequency) * 10_000_000L
                     + (counter % perfFrequency) * 10_000_000L / perfFrequency;
            return DateTime.UtcNow.Ticks;
        }

        // 返回 QPC 原始值（不做单位转换），用于锚点时间推算（精度最高）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetRawTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long counter))
                return counter;
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