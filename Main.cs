using HarmonyLib;
using SA.GoogleDoc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;

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
                }
            }
            else
            {
                IsEnabled = false;
                Harmony?.UnpatchAll();
            }
            return true;
        }

            public static bool IsEnabled { get; internal set; }
    }
}
