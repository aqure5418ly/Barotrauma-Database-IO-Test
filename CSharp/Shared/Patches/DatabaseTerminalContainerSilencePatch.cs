using System;
using System.Collections.Generic;
using System.Reflection;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch]
    internal static class DatabaseTerminalContainerSilencePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var method in typeof(ItemComponent).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == "AddToGUIUpdateList")
                {
                    yield return method;
                }
            }
        }

        private static bool Prefix(ItemComponent __instance)
        {
#if CLIENT
            try
            {
                if (__instance is DatabaseTerminalComponent terminal &&
                    terminal.ShouldHijackFixedTerminalUi())
                {
                    // Only suppress native component GUI; Terminal hook drives UI.
                    return false;
                }

                if (__instance is not ItemContainer container) { return true; }
                var owner = container.Item;
                if (owner == null || owner.Removed) { return true; }

                var ownerTerminal = owner.GetComponent<DatabaseTerminalComponent>();
                if (ownerTerminal == null) { return true; }
                if (!ownerTerminal.ShouldHijackFixedTerminalUi()) { return true; }

                if (!ownerTerminal.ShouldSilenceFixedContainerGui()) { return true; }

                ownerTerminal.TraceFixedContainerSilenced("harmony:ItemContainer.AddToGUIUpdateList");
                return false;
            }
            catch (Exception ex)
            {
                ModFileLog.Write("Panel", $"{Constants.LogPrefix} ContainerSilencePatch failed: {ex.GetType().Name}: {ex.Message}");
                return true;
            }
#else
            return true;
#endif
        }
    }
}
