using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BaseMacro
{
    public class Patches
    {
        [HarmonyPatch(typeof(scrController), "PlayerControl_Update")]
        public static class Patch_PlayerControl_Update
        {
            private static void Postfix(scrController __instance)
            {
                TimeBasedMacro.Update(__instance);
                TimeBasedMacro.HandleInput();
            }
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Awake_Rewind))]
        public static class Patch_Awake_Rewind
        {
            private static void Postfix() => TimeBasedMacro.Reset();
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Restart))]
        public static class Patch_Restart
        {
            private static void Prefix() => TimeBasedMacro.Reset();
        }
    }
}
