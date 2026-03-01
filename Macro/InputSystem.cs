using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BaseMacro.Macro
{
    public static class InputSystem
    {
        private static IntPtr _hModule = IntPtr.Zero;
        private static bool _isInitialized = false;

        // Windows API
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);

        // 委托定义 - 确保调用约定和参数类型完全匹配
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


        // 函数指针
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

                Console.WriteLine($"[InputSystem] 加载 DLL: {dllPath}");

                _hModule = LoadLibrary(dllPath);
                if (_hModule == IntPtr.Zero)
                {
                    int error = Marshal.GetLastWin32Error();
                    Console.WriteLine($"[InputSystem] LoadLibrary 失败，错误码: {error}");
                    return false;
                }

                Console.WriteLine("[InputSystem] LoadLibrary 成功");

                // 获取所有函数地址
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

                // 检查必要的函数
                if (InitializeFunc == null || PushKeyEventFunc == null)
                {
                    Console.WriteLine("[InputSystem] 缺少必要的导出函数");
                    FreeLibrary(_hModule);
                    _hModule = IntPtr.Zero;
                    return false;
                }

                // 测试调用
                int result = InitializeFunc(2048);
                Console.WriteLine($"[InputSystem] 初始化结果: {result}");

                _isInitialized = (result == 0);

                if (_isInitialized)
                {
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
                }

                return _isInitialized;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[InputSystem] 初始化异常: {ex.Message}");
                return false;
            }
        }

        private static T GetDelegate<T>(string functionName) where T : Delegate
        {
            IntPtr pFunc = GetProcAddress(_hModule, functionName);
            if (pFunc == IntPtr.Zero)
            {
                Console.WriteLine($"[InputSystem] 找不到函数: {functionName}");
                return null;
            }
            Console.WriteLine($"[InputSystem] 找到函数: {functionName}");
            return Marshal.GetDelegateForFunctionPointer<T>(pFunc);
        }

        // 包装函数
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
            if (!_isInitialized || SendKeyCombinationFunc == null || keys == null || keys.Length == 0)
                return -1;

            fixed (byte* pKeys = keys)
            {
                return SendKeyCombinationFunc(pKeys, keys.Length, delayMs);
            }
        }

        public static int SendText(string text)
        {
            if (!_isInitialized || SendTextFunc == null || string.IsNullOrEmpty(text))
                return -1;
            return SendTextFunc(text);
        }

        public static int StartProcessing()
        {
            if (!_isInitialized || StartProcessingFunc == null) return -1;
            return StartProcessingFunc();
        }

        public static int StopProcessing()
        {
            if (!_isInitialized || StopProcessingFunc == null) return -1;
            return StopProcessingFunc();
        }

        public static void ClearQueue()
        {
            if (!_isInitialized || ClearQueueFunc == null) return;
            ClearQueueFunc();
        }

        public static int GetInputQueueStatus(out int queueSize, out int processedCount)
        {
            queueSize = 0;
            processedCount = 0;
            if (!_isInitialized || GetInputQueueStatusFunc == null) return -1;
            return GetInputQueueStatusFunc(out queueSize, out processedCount);
        }

        public static void Shutdown()
        {
            if (ShutdownFunc != null)
            {
                try { ShutdownFunc(); } catch { }
            }
            if (_hModule != IntPtr.Zero)
            {
                FreeLibrary(_hModule);
                _hModule = IntPtr.Zero;
            }
            _isInitialized = false;
        }

        public static void EmergencyStop()
        {
            if (!_isInitialized || EmergencyStopFunc == null) return;
            EmergencyStopFunc();
        }

        public static bool IsUsingNtFunctions()
        {
            if (!_isInitialized || IsUsingNtFunctionsFunc == null) return false;
            try
            {
                return IsUsingNtFunctionsFunc();
            }
            catch
            {
                return false;
            }
        }

        // 辅助方法
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
            GetInputQueueStatus(out int queueSize, out int processedCount);
            return (queueSize, processedCount);
        }

        public static bool IsInitialized => _isInitialized;
    }
}