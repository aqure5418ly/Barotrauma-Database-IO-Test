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
        private static readonly Identifier DatabaseTerminalTag = "database_terminal".ToIdentifier();

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
                if (!item.HasTag(DatabaseTerminalTag)) { return true; }

                var terminal = item.GetComponent<DatabaseTerminalComponent>();
                if (terminal == null || !terminal.ShouldSuppressNativeTerminalGui()) { return true; }

                // HookOnly fixed UI requires the XML Terminal component on DatabaseTerminalFixed.
                // This patch is triggered by Terminal.AddToGUIUpdateList, so removing that XML node
                // will remove the render entrypoint for fixed terminal UI.
                if (terminal.ShouldDriveFixedUiFromHook())
                {
                    terminal.DrawFixedTerminalUiFromGuiHook("harmony:Terminal.AddToGUIUpdateList");
                }

                // Always suppress native Terminal GUI for DB terminals.
                // Compact craft terminal runs Update-only custom UI, so this path intentionally
                // suppresses native Terminal while skipping hook-driven drawing.
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
