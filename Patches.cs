using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BaseMacro
{
    internal class Patches
    {
        [HarmonyPatch(typeof(scrController), "PlayerControl_Update")]
        private static class Patch_PlayerControl_Update
        {
            private static void Postfix(scrController __instance)
            {
                TimeBasedMacro.Update(__instance);
                TimeBasedMacro.HandleInput();
            }
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Awake_Rewind))]
        private static class Patch_Awake_Rewind
        {
            private static void Postfix() => TimeBasedMacro.Reset();
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Restart))]
        private static class Patch_Restart
        {
            private static void Prefix() => TimeBasedMacro.Reset();
        }
    }
}
