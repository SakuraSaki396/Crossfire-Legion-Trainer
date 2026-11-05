using System;
using Carbon.Scripting.Lua;
using Carbon.Simulation;
using HarmonyLib;

namespace CrossfireTrainer
{
    [HarmonyPatch(typeof(SimScriptProcessor), "OnInitialize")]
    internal static class PatchOnInitialize
    {
        internal static readonly AccessTools.FieldRef<SimScriptProcessor, LuaEnv> LuaField =
            AccessTools.FieldRefAccess<SimScriptProcessor, LuaEnv>("mLuaEnv");

        internal static readonly AccessTools.FieldRef<SimScriptProcessor, object> FrameCtrl =
            AccessTools.FieldRefAccess<SimScriptProcessor, object>("mFrameController");

        [HarmonyPostfix]
        private static void Postfix(SimScriptProcessor __instance)
        {
            try
            {
                TrainerPlugin.Instance?.OnSessionStart(__instance);
            }
            catch (Exception e)
            {
                TrainerPlugin.Log?.LogError("OnInitialize postfix: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(SimScriptProcessor), "OnShutdown")]
    internal static class PatchOnShutdown
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                TrainerPlugin.Instance?.OnSessionEnd();
            }
            catch (Exception e)
            {
                TrainerPlugin.Log?.LogError("OnShutdown postfix: " + e);
            }
        }
    }

    /// <summary>Runs on the simulation thread every tick: safe place to touch LuaEnv.</summary>
    [HarmonyPatch(typeof(SimScriptProcessor), "DoProcess")]
    internal static class PatchDoProcess
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                TrainerPlugin.Instance?.Drain();
            }
            catch (Exception e)
            {
                TrainerPlugin.Log?.LogError("DoProcess postfix: " + e);
            }
        }
    }
}
