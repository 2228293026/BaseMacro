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
#pragma warning disable CS0420 // 对可变字段的引用不被视为可变字段
    #region TimeBasedMacro

    /// <summary>
    /// 多线程版本：主线程仅更新 Unity 组件，工作线程处理触发逻辑与按键发送。
    /// </summary>
    internal static class Macro
    {
        // ─────────────────────────────────────────────
        //  主线程专属数据（只在主线程读写）
        // ─────────────────────────────────────────────
        private static scrLevelMaker? levelMaker;
        private static scrConductor? conductor;
        private static List<scrFloor>? cachedFloors;
        private static bool initialized = false;
        private static string lastKeysSetting = "";
        private static readonly List<byte> keyCodes = new(4);

        // ─────────────────────────────────────────────
        //  只读共享数据（初始化后不可变，无需锁）
        // ─────────────────────────────────────────────
        private static double[]? triggerTimes;
        private static int floorCount;

        // ─────────────────────────────────────────────
        //  帧快照（主线程写，工作线程读）
        // ─────────────────────────────────────────────
        /// <summary>
        /// 主线程每帧写入，工作线程消费。使用 volatile 保证可见性。
        /// struct 整体通过 Interlocked 交换，避免撕裂读写。
        /// </summary>
        private sealed class FrameSnapshot
        {
            public double currentTime;
            public double nextFrameTime;
            public double timeOffset;
            public bool simulateKeyPress;
            public int lastTriggeredFloor;   // 主线程上一帧的值，用于工作线程初始定位
            public double[]? triggerTimes;    // 共享只读引用
            public List<scrFloor>? floors;    // 共享只读引用（帧内不变）
            public byte[] keyCodesSnapshot;   // keyCodes 快照（避免并发修改）
            public int keyIndexSnapshot;
            public bool valid;
            public FrameSnapshot() { keyCodesSnapshot = []; }
        }

        // 双缓冲快照：主线程写 _writing，工作线程交换后读
        private static FrameSnapshot _snapshotA = new();
        private static FrameSnapshot _snapshotB = new();
        private static volatile FrameSnapshot _pendingSnapshot = new(); // 工作线程读这个

        // ─────────────────────────────────────────────
        //  工作线程 → 主线程的结果反馈
        // ─────────────────────────────────────────────
        private static volatile int _workerLastTriggeredFloor = -1;
        private static volatile int _workerKeyIndex = 0;
        private static volatile bool _workerNeedsHit = false; // 非模拟按键模式下通知主线程调用 Hit()

        // ─────────────────────────────────────────────
        //  工作线程控制
        // ─────────────────────────────────────────────
        private static Thread? _workerThread;
        private static volatile bool _workerRunning = false;
        private static readonly SemaphoreSlim _frameSignal = new(0, 1);

        // ─────────────────────────────────────────────
        //  按键状态（仅工作线程访问）
        // ─────────────────────────────────────────────
        private static byte? _pendingKey;
        private static bool _isKeyDown;

        // ─────────────────────────────────────────────
        //  SkyHook 相关
        // ─────────────────────────────────────────────
        private static long startTimeTicks;
        private static volatile bool skyHookInitialized = false;

        // ─────────────────────────────────────────────
        //  Win32 / SendInput
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
            ["PAGEDOWN"] = 0x22
        };

        // ─────────────────────────────────────────────
        //  静态构造
        // ─────────────────────────────────────────────
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Macro()
        {
            usePerfCounter = QueryPerformanceFrequency(out perfFrequency);
            for (int i = 0; i < 256; i++)
                scanCodeCache[i] = (byte)MapVirtualKey((uint)i, 0);
        }

        // ═══════════════════════════════════════════════════════════════
        //  主线程入口
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// [主线程] 每帧由 Unity Update 调用。只访问 Unity 组件。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Update(scrController controller)
        {
            var settings = Main.Settings;

            // ── 快速失败 ────────────────────────────────────────
            if (!settings.Macro || controller?.paused != false ||
                ADOBase.sceneName == GCNS.sceneLevelSelect)
            {
                StopWorkerIfNeeded();
                return;
            }

            // ── 工作线程启动 ────────────────────────────────────
            EnsureWorkerRunning();

            // ── 模式切换 ────────────────────────────────────────
            if (settings.SkyHookMode != skyHookInitialized)
                SwitchMode(settings.SkyHookMode);

            // ── 初始化 / 重初始化 ───────────────────────────────
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

            // ── 读取主线程上一帧工作线程的反馈 ─────────────────
            int lastFloor = Volatile.Read(ref _workerLastTriggeredFloor);
            int keyIdx = Volatile.Read(ref _workerKeyIndex);

            // 非模拟按键模式：工作线程通知主线程调用 Hit()
            if (Interlocked.Exchange(ref Unsafe.As<bool, int>(ref _workerNeedsHit), 0) != 0)
            {
                controller!.Hit(false);
                Log("[Macro-Main] controller.Hit() 已调用");
            }

            // ── 构造帧快照并发布给工作线程 ──────────────────────
            double currentTime = conductor!.songposition_minusi;
            float pitch = conductor.song.pitch;
            double nextFrameTime = currentTime + Time.unscaledDeltaTime * pitch;

            // 轮流使用 A/B 两个快照对象，避免 GC
            var snap = ReferenceEquals(_pendingSnapshot, _snapshotA) ? _snapshotB : _snapshotA;
            snap.currentTime = currentTime;
            snap.nextFrameTime = nextFrameTime;
            snap.timeOffset = settings.TimeOffset * 0.001;
            snap.simulateKeyPress = settings.SimulateKeyPress;
            snap.lastTriggeredFloor = lastFloor;
            snap.triggerTimes = triggerTimes;
            snap.floors = cachedFloors;
            snap.keyIndexSnapshot = keyIdx;
            snap.valid = true;

            // keyCodes 快照（长度一般 ≤4，廉价）
            if (snap.keyCodesSnapshot.Length != keyCodes.Count)
                snap.keyCodesSnapshot = new byte[keyCodes.Count];
            keyCodes.CopyTo(snap.keyCodesSnapshot, 0);

            // 发布快照（volatile 写，工作线程 volatile 读）
            Volatile.Write(ref _pendingSnapshot, snap);

            // 信号工作线程开始处理（最多持有 1 个信号，不阻塞主线程）
            if (_frameSignal.CurrentCount == 0)
                _frameSignal.Release();

            Log($"[Macro-Main] 快照已发布 time={currentTime:F6}s lastFloor={lastFloor}");
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerLoop()
        {
            Log("[Macro-Worker] 工作线程启动");

            while (_workerRunning)
            {
                // 等待主线程信号（最多 100ms，避免永久阻塞）
                bool got = _frameSignal.Wait(100);
                if (!got || !_workerRunning) continue;

                var snap = Volatile.Read(ref _pendingSnapshot);
                if (!snap.valid) continue;

                ProcessSnapshot(snap);
            }

            // 退出前释放按键
            WorkerReleaseKey();
            Log("[Macro-Worker] 工作线程退出");
        }

        /// <summary>
        /// [工作线程] 根据快照判断需要触发哪些地板，发送按键事件。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ProcessSnapshot(FrameSnapshot snap)
        {
            var times = snap.triggerTimes;
            var floors = snap.floors;
            if (times == null || floors == null) return;

            double nextFrameTime = snap.nextFrameTime;
            double timeOffset = snap.timeOffset;
            bool simulateKey = snap.simulateKeyPress;
            int triggerCount = times.Length;
            byte[] keys = snap.keyCodesSnapshot;
            int ki = snap.keyIndexSnapshot;

            int lastFloor = snap.lastTriggeredFloor;
            int startFloor = lastFloor + 1;
            bool hitNeeded = false;

            for (int i = startFloor; i < triggerCount; i++)
            {
                var floor = floors[i];
                if (floor == null) continue;

                // 跳过自动/中旋
                if (floor.nextfloor?.auto == true || floor.midSpin)
                {
                    lastFloor = i;
                    continue;
                }

                double adjustedTrigger = times[i] + timeOffset;
                if (adjustedTrigger > nextFrameTime) break;
                if (i <= lastFloor) continue;

                // 检测 hold 释放模式
                bool releaseOnly = false;
                if (simulateKey && floor.holdLength > -1 && i + 1 < triggerCount)
                {
                    var nextFloor = floors[i + 1];
                    if (nextFloor != null && nextFloor.holdLength == -1)
                        releaseOnly = true;
                }

                if (!simulateKey)
                {
                    // 通知主线程调用 controller.Hit()（Unity API 必须在主线程）
                    hitNeeded = true;
                    Log($"[Macro-Worker] 请求 Hit() FloorIndex={i}");
                }
                else if (releaseOnly)
                {
                    WorkerReleaseKey();
                    if (i + 1 > lastFloor) lastFloor = i + 1;
                }
                else if (keys.Length > 0)
                {
                    byte key = keys[ki % keys.Length];
                    WorkerPressKey(key);
                    ki = (ki + 1) % keys.Length;
                    Log($"[Macro-Worker] 按下 0x{key:X2} FloorIndex={i}");
                }

                lastFloor = i;
            }

            // 末尾强制释放
            if (_isKeyDown && lastFloor >= triggerCount - 1)
                WorkerReleaseKey();

            // 回写结果给主线程
            Volatile.Write(ref _workerLastTriggeredFloor, lastFloor);
            Volatile.Write(ref _workerKeyIndex, ki);
            if (hitNeeded)
                Volatile.Write(ref Unsafe.As<bool, int>(ref _workerNeedsHit), 1);
        }

        // ═══════════════════════════════════════════════════════════════
        //  工作线程 - 按键操作
        // ═══════════════════════════════════════════════════════════════

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WorkerPressKey(byte keyCode)
        {
            // 先松开旧键（如有）
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
            if (Main.Settings.SkyHookMode)
            {
                long now = GetAudioSyncTicks();
                long elapsed = now - startTimeTicks;
                var evt = SkyHookSystem.SkyHookEvent.Create(keyCode, isDown, elapsed);
                AsyncInputManager.EnqueueEvent(evt);
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
        //  主线程辅助方法（只调用 Unity API）
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

            // 同步工作线程状态
            int syncFloor = SyncFloor(conductor!.songposition_minusi);
            Volatile.Write(ref _workerLastTriggeredFloor, syncFloor);
            Volatile.Write(ref _workerKeyIndex, 0);
            if (Main.Settings.SkyHookMode)
                startTimeTicks = GetAudioSyncTicks();

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
        //  生命周期管理
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
            // 工作线程在 ProcessSnapshot 时会自行处理释放

            AsyncInputManager.ClearQueue();

            if (Main.Settings.SkyHookMode)
                startTimeTicks = GetAudioSyncTicks();

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
            _frameSignal.Release(); // 唤醒线程让它退出

            if (skyHookInitialized)
            {
                AsyncInputManager.Stop();
                skyHookInitialized = false;
            }
            Log("[Macro-Main] 工作线程停止请求已发送");
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SwitchMode(bool useSkyHook)
        {
            if (useSkyHook == skyHookInitialized) return;
            Log($"[Macro-Main] 切换模式: {(useSkyHook ? "SkyHook" : "SendInput")}");

            if (useSkyHook)
            {
                AsyncInputManager.Start();
                skyHookInitialized = true;
                startTimeTicks = GetAudioSyncTicks();
                Main.Settings.SkyHookMode = true;
            }
            else
            {
                AsyncInputManager.Stop();
                skyHookInitialized = false;
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
                : GetPreciseTicks();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetPreciseTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long counter))
                return (counter * 10000000) / perfFrequency;
            return DateTime.UtcNow.Ticks;
        }

        // ═══════════════════════════════════════════════════════════════
        //  输入调整（主线程 HandleInput，Unity Input API）
        // ═══════════════════════════════════════════════════════════════
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void HandleInput()
        {
            if (!Main.Settings.Macro || ADOBase.sceneName == GCNS.sceneLevelSelect || ADOBase.controller.paused) return;
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)]

        [System.Diagnostics.Conditional("DEBUG")]
        public static void Log(string message) => Main.Mod?.Logger.Log(message);
    }

    #endregion
#pragma warning restore CS0420 // 对可变字段的引用不被视为可变字段
}