using System;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch(typeof(Item), "UpdateHUD")]
    internal static class DatabaseTerminalFixedHudPatch
    {
#if CLIENT
        private static void Postfix(Item __instance, Character character)
        {
            try
            {
                if (__instance == null || __instance.Removed) { return; }
                if (character != Character.Controlled) { return; }

                var terminal = __instance.GetComponent<DatabaseTerminalComponent>();
                if (terminal == null || !terminal.ShouldHijackFixedTerminalUi()) { return; }

                foreach (var component in __instance.GetComponents<ItemContainer>())
                {
                    if (!__instance.activeHUDs.Contains(component))
                    {
                        __instance.activeHUDs.Add(component);
                    }
                }
            }
            catch (Exception ex)
            {
                ModFileLog.Write("Panel", $"{Constants.LogPrefix} FixedHudPatch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
#endif
    }
}
