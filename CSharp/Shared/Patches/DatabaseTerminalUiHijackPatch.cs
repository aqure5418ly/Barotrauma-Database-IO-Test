using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch]
    internal static class DatabaseTerminalUiHijackPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(Terminal).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == "AddToGUIUpdateList")
                {
                    yield return method;
                }
            }
        }

        private static bool Prefix(Terminal __instance)
        {
#if CLIENT
            try
            {
                Item item = __instance?.Item;
                if (item == null || item.Removed) { return true; }

                var terminal = item.GetComponent<DatabaseTerminalComponent>();
                if (terminal == null || !terminal.ShouldHijackFixedTerminalUi()) { return true; }

                if (!terminal.ShouldDriveFixedUiFromHook()) { return true; }

                terminal.DrawFixedTerminalUiFromGuiHook("harmony:Terminal.AddToGUIUpdateList");
                return false;
            }
            catch (Exception ex)
            {
                ModFileLog.Write("Panel", $"{Constants.LogPrefix} UIHijackPatch failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
#else
            return true;
#endif
        }
    }
}
