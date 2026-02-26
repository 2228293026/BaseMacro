using HarmonyLib;
using SA.GoogleDoc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;
using static UnityModManagerNet.UnityModManager;

#nullable enable

namespace BaseMacro
{
    public static class Main
    {
        public static UnityModManager.ModEntry? Mod { get; private set; }
        public static Harmony? Harmony { get; private set; }
        public static Settings Settings { get; private set; } = null!;
        private static GameObject? _uiObject;
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            Settings = Settings.Load(modEntry);

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = Settings.OnGUI;
            modEntry.OnSaveGUI = Settings.OnSaveGUI;

            Harmony = new Harmony(modEntry.Info.Id);
            return true;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
                IsEnabled = true;
                Harmony?.PatchAll(Assembly.GetExecutingAssembly());
                if (_uiObject == null)
                {
                    _uiObject = new GameObject("MacroText");
                    _uiObject.AddComponent<ShowText>();
                    UnityEngine.Object.DontDestroyOnLoad(_uiObject);
                    TrySetWindowTitle($"{modEntry.Info.DisplayName}, {modEntry.Info.Version}, {modEntry.Info.Author}");
                }
            }
            else
            {
                IsEnabled = false;
                Harmony?.UnpatchAll();
                TrySetWindowTitle(null);

            }
            return true;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        private static void TrySetWindowTitle(string? title)
        {
            try
            {
                // 获取当前进程
                Process currentProcess = Process.GetCurrentProcess();

                // 等待主窗口句柄可用
                for (int i = 0; i < 10 && currentProcess.MainWindowHandle == IntPtr.Zero; i++)
                {
                    currentProcess.Refresh();
                    System.Threading.Thread.Sleep(100);
                }

                IntPtr hwnd = currentProcess.MainWindowHandle;

                if (hwnd != IntPtr.Zero)
                {
                    string newTitle = $"A Dance of Fire and Ice";
                    if (title != null)
                        newTitle = $"A Dance of Fire and Ice - {title}";

                    if (SetWindowText(hwnd, newTitle))
                    {
                        Mod?.Logger.Log($"成功设置窗口标题: {newTitle}");
                    }
                    else
                    {
                        Mod?.Logger.Log("设置窗口标题失败");
                    }
                }
                else
                {
                    Mod?.Logger.Log("未获取到主窗口句柄");
                }
            }
            catch (Exception ex)
            {
                Mod?.Logger.Log($"设置窗口标题异常: {ex.Message}");
            }
        }
        public static bool IsEnabled { get; internal set; }
    }
}
