using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BaseMacro.Macro
{
    /// <summary>
    /// 与 C++ DLL 对应的输入模式枚举
    /// </summary>
    public enum InputMode : int
    {
        Auto = 0,  // 自动选择最底层可用方式
        NtUserInjectKeyboard = 1,  // NtUserInjectKeyboardInput（最底层）
        NtUserSendInput = 2,  // NtUserSendInput
        SendInput = 3,  // 标准 SendInput（兜底）
    }

    /// <summary>
    /// GetAvailableModes() 返回值的位掩码解析辅助
    /// </summary>
    [Flags]
    public enum AvailableModeMask : int
    {
        Auto = 1 << 0,
        NtUserInjectKeyboard = 1 << 1,
        NtUserSendInput = 1 << 2,
        SendInput = 1 << 3,
    }

    public static class InputSystem
    {
        private static IntPtr _hModule = IntPtr.Zero;
        private static bool _isInitialized = false;

        // ── Win32 ──────────────────────────────────────────────────
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);

        // ── 委托 ───────────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeDelegate(int maxQueueSize);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PushKeyEventDelegate(byte keyCode, [MarshalAs(UnmanagedType.Bool)] bool isDown, uint delayMs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendKeyDirectDelegate(byte keyCode, [MarshalAs(UnmanagedType.Bool)] bool isDown);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private unsafe delegate int SendKeyCombinationDelegate(byte* keys, int keyCount, uint delayMs);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SendTextDelegate([MarshalAs(UnmanagedType.LPStr)] string text);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartProcessingDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StopProcessingDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ClearQueueDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInputQueueStatusDelegate(out int queueSize, out int processedCount);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void ShutdownDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void EmergencyStopDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate bool IsUsingNtFunctionsDelegate();

        // ── 模式控制委托 ───────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SetInputModeDelegate(int mode);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetInputModeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetAvailableModesDelegate();

        // ── 函数指针 ───────────────────────────────────────────────
        private static InitializeDelegate InitializeFunc;
        private static PushKeyEventDelegate PushKeyEventFunc;
        private static SendKeyDirectDelegate SendKeyDirectFunc;
        private static SendKeyCombinationDelegate SendKeyCombinationFunc;
        private static SendTextDelegate SendTextFunc;
        private static StartProcessingDelegate StartProcessingFunc;
        private static StopProcessingDelegate StopProcessingFunc;
        private static ClearQueueDelegate ClearQueueFunc;
        private static GetInputQueueStatusDelegate GetInputQueueStatusFunc;
        private static ShutdownDelegate ShutdownFunc;
        private static EmergencyStopDelegate EmergencyStopFunc;
        private static IsUsingNtFunctionsDelegate IsUsingNtFunctionsFunc;
        private static SetInputModeDelegate SetInputModeFunc;
        private static GetInputModeDelegate GetInputModeFunc;
        private static GetAvailableModesDelegate GetAvailableModesFunc;

        // ── 初始化 ─────────────────────────────────────────────────
        public static bool Initialize()
        {
            if (_isInitialized) return true;

            try
            {
                string modPath = Main.Mod?.Path ?? Path.GetDirectoryName(typeof(InputSystem).Assembly.Location);
                string dllPath = Path.Combine(modPath, "InputSystem.dll");

                if (!File.Exists(dllPath))
                {
                    Console.WriteLine($"[InputSystem] 文件不存在: {dllPath}");
                    return false;
                }

                _hModule = LoadLibrary(dllPath);
                if (_hModule == IntPtr.Zero)
                {
                    Console.WriteLine($"[InputSystem] LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                InitializeFunc = GetDelegate<InitializeDelegate>("Initialize");
                PushKeyEventFunc = GetDelegate<PushKeyEventDelegate>("PushKeyEvent");
                SendKeyDirectFunc = GetDelegate<SendKeyDirectDelegate>("SendKeyDirect");
                SendKeyCombinationFunc = GetDelegate<SendKeyCombinationDelegate>("SendKeyCombination");
                SendTextFunc = GetDelegate<SendTextDelegate>("SendText");
                StartProcessingFunc = GetDelegate<StartProcessingDelegate>("StartProcessing");
                StopProcessingFunc = GetDelegate<StopProcessingDelegate>("StopProcessing");
                ClearQueueFunc = GetDelegate<ClearQueueDelegate>("ClearQueue");
                GetInputQueueStatusFunc = GetDelegate<GetInputQueueStatusDelegate>("GetInputQueueStatus");
                ShutdownFunc = GetDelegate<ShutdownDelegate>("Shutdown");
                EmergencyStopFunc = GetDelegate<EmergencyStopDelegate>("EmergencyStop");
                IsUsingNtFunctionsFunc = GetDelegate<IsUsingNtFunctionsDelegate>("IsUsingNtFunctions");
                SetInputModeFunc = GetDelegate<SetInputModeDelegate>("SetInputMode");
                GetInputModeFunc = GetDelegate<GetInputModeDelegate>("GetInputMode");
                GetAvailableModesFunc = GetDelegate<GetAvailableModesDelegate>("GetAvailableModes");

                if (InitializeFunc == null || PushKeyEventFunc == null)
                {
                    Console.WriteLine("[InputSystem] 缺少必要的导出函数");
                    FreeLibrary(_hModule);
                    _hModule = IntPtr.Zero;
                    return false;
                }

                int result = InitializeFunc(2048);
                Console.WriteLine($"[InputSystem] 初始化结果: {result}");

                _isInitialized = (result == 0);
                if (_isInitialized)
                {
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();

                    SyncModeFromSettings();
                }

                return _isInitialized;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InputSystem] 初始化异常: {ex.Message}");
                return false;
            }
        }

        private static T GetDelegate<T>(string name) where T : Delegate
        {
            IntPtr ptr = GetProcAddress(_hModule, name);
            if (ptr == IntPtr.Zero) { Console.WriteLine($"[InputSystem] 找不到函数: {name}"); return null; }
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        private static void SyncModeFromSettings()
        {
            try
            {
                // 获取当前 Mod 的 Settings 实例
                if (Main.Mod != null && Main.Settings is Settings settings)
                {
                    Main.Mod.Logger.Log($"[InputSystem] 从设置同步模式: {settings.InputMode}");
                    SetInputMode((InputMode)settings.InputMode);
                }
            }
            catch (Exception ex)
            {
                Main.Mod.Logger.Log($"[InputSystem] 同步模式失败: {ex.Message}");
            }
        }

        // ── 模式控制 API ───────────────────────────────────────────

        /// <summary>
        /// 设置输入模式。返回实际生效的模式（Auto 时返回解析后的真实模式）。
        /// 返回 -1 表示参数无效。
        /// </summary>
        public static InputMode SetInputMode(InputMode mode)
        {
            if (!_isInitialized || SetInputModeFunc == null) return InputMode.SendInput;
            int result = SetInputModeFunc((int)mode);
            if (result < 0)
            {
                Console.WriteLine($"[InputSystem] SetInputMode 失败: {result}");
                return GetInputMode();
            }
            Console.WriteLine($"[InputSystem] 模式已切换 -> 请求={mode}, 实际生效={((InputMode)result)}");
            return (InputMode)result;
        }

        /// <summary>
        /// 获取当前实际生效的输入模式（Auto 时返回解析后的模式）
        /// </summary>
        public static InputMode GetInputMode()
        {
            if (!_isInitialized || GetInputModeFunc == null) return InputMode.SendInput;
            return (InputMode)GetInputModeFunc();
        }

        /// <summary>
        /// 获取当前系统支持的输入模式（位掩码）
        /// </summary>
        public static AvailableModeMask GetAvailableModes()
        {
            if (!_isInitialized || GetAvailableModesFunc == null) return AvailableModeMask.SendInput;
            return (AvailableModeMask)GetAvailableModesFunc();
        }

        /// <summary>
        /// 检查某个模式在当前系统上是否可用
        /// </summary>
        public static bool IsModeAvailable(InputMode mode)
        {
            var mask = GetAvailableModes();
            return mode switch
            {
                InputMode.Auto => mask.HasFlag(AvailableModeMask.Auto),
                InputMode.NtUserInjectKeyboard => mask.HasFlag(AvailableModeMask.NtUserInjectKeyboard),
                InputMode.NtUserSendInput => mask.HasFlag(AvailableModeMask.NtUserSendInput),
                InputMode.SendInput => mask.HasFlag(AvailableModeMask.SendInput),
                _ => false
            };
        }

        // ── 输入 API ───────────────────────────────────────────────

        public static int PushKeyEvent(byte keyCode, bool isDown, uint delayMs = 0)
        {
            if (!_isInitialized || PushKeyEventFunc == null) return -1;
            return PushKeyEventFunc(keyCode, isDown, delayMs);
        }

        public static int SendKeyDirect(byte keyCode, bool isDown)
        {
            if (!_isInitialized || SendKeyDirectFunc == null) return -1;
            return SendKeyDirectFunc(keyCode, isDown);
        }

        public static unsafe int SendKeyCombination(byte[] keys, uint delayMs = 50)
        {
            if (!_isInitialized || SendKeyCombinationFunc == null || keys == null || keys.Length == 0) return -1;
            fixed (byte* pKeys = keys)
                return SendKeyCombinationFunc(pKeys, keys.Length, delayMs);
        }

        public static int SendText(string text)
        {
            if (!_isInitialized || SendTextFunc == null || string.IsNullOrEmpty(text)) return -1;
            return SendTextFunc(text);
        }

        public static int StartProcessing() => _isInitialized && StartProcessingFunc != null ? StartProcessingFunc() : -1;
        public static int StopProcessing() => _isInitialized && StopProcessingFunc != null ? StopProcessingFunc() : -1;
        public static void ClearQueue() { if (_isInitialized && ClearQueueFunc != null) ClearQueueFunc(); }

        public static int GetInputQueueStatus(out int queueSize, out int processedCount)
        {
            queueSize = 0; processedCount = 0;
            if (!_isInitialized || GetInputQueueStatusFunc == null) return -1;
            return GetInputQueueStatusFunc(out queueSize, out processedCount);
        }

        public static void Shutdown()
        {
            try { ShutdownFunc?.Invoke(); } catch { }
            if (_hModule != IntPtr.Zero) { FreeLibrary(_hModule); _hModule = IntPtr.Zero; }
            _isInitialized = false;
        }

        public static void EmergencyStop() { if (_isInitialized && EmergencyStopFunc != null) EmergencyStopFunc(); }

        public static bool IsUsingNtFunctions()
        {
            if (!_isInitialized || IsUsingNtFunctionsFunc == null) return false;
            try { return IsUsingNtFunctionsFunc(); } catch { return false; }
        }

        // ── 便捷方法 ───────────────────────────────────────────────
        public static void KeyDown(byte keyCode) => PushKeyEvent(keyCode, true);
        public static void KeyUp(byte keyCode) => PushKeyEvent(keyCode, false);

        public static void KeyPress(byte keyCode, uint durationMs = 50)
        {
            PushKeyEvent(keyCode, true, durationMs);
            PushKeyEvent(keyCode, false);
        }

        public static void KeyDownDirect(byte keyCode) => SendKeyDirect(keyCode, true);
        public static void KeyUpDirect(byte keyCode) => SendKeyDirect(keyCode, false);

        public static (int queueSize, int processedCount) GetStatus()
        {
            GetInputQueueStatus(out int q, out int p);
            return (q, p);
        }

        public static bool IsInitialized => _isInitialized;
    }
}