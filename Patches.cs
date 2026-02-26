using BaseMacro.Platform;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;

namespace BaseMacro
{
    public class Patches
    {
        [HarmonyPatch(typeof(scrController), "PlayerControl_Update")]
        public static class Patch_PlayerControl_Update
        {
            [HarmonyPostfix]
            public static void Postfix(scrController __instance)
            {
                Macro.Update(__instance);
                Macro.HandleInput();
            }
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Awake_Rewind))]
        public static class Patch_Awake_Rewind
        {
            [HarmonyPostfix]
            public static void Postfix(scrController __instance) => Macro.Reset(__instance);
        }

        [HarmonyPatch(typeof(scrController), nameof(scrController.Restart))]
        public static class Patch_Restart
        {
            [HarmonyPrefix]
            public static void Prefix(scrController __instance) => Macro.Reset(__instance);
        }
        [HarmonyPatch(typeof(scrConductor), "Update")]
        public static class __scrConductor
        {

            public static unsafe long Update_1()
            {
                long l = BaseSelect.GetFileTime();
                return DateTime.Now.Ticks - DateTime.UtcNow.Ticks + l;
            }

            public static double Update_2()
            {
                return AudioDSPManager.GetDSPTime();
            }

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler_Update(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
            {
                // 如果开关关闭，直接返回原始指令，不进行任何修改
                if (!Main.Settings.HighPrecisionTime)
                {
                    return instructions;
                }

                /* 以下为原有的 IL 修改代码 */
                bool patch = false;
                int skip = 0;
                List<CodeInstruction> result = [];

                foreach (CodeInstruction ci in instructions)
                {
                    if (patch)
                    {
                        patch = false;
                        result.Add(new CodeInstruction(OpCodes.Call, typeof(__scrConductor).GetMethod(nameof(Update_1), AccessTools.all)));
                    }
                    if (skip > 0)
                    {
                        skip--;
                        continue;
                    }
                    if (ci.opcode == OpCodes.Call && ((MethodInfo)ci.operand).Name == "get_Now")
                    {
                        skip = 3;
                        patch = true;
                        continue;
                    }
                    if (ci.opcode == OpCodes.Call && ((MethodInfo)ci.operand).Name == "get_dspTime")
                    {
                        ci.operand = typeof(__scrConductor).GetMethod(nameof(Update_2), AccessTools.all);
                    }

                    result.Add(ci);
                }

                return result.AsEnumerable();
            }
        }
    }
}
