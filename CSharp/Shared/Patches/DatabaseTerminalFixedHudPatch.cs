using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch]
    internal static class DatabaseTerminalFixedHudPatch
    {
        private static bool _loggedMissingTarget;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var matches = new List<MethodBase>();

            Type itemType = null;
            try
            {
                itemType = AccessTools.TypeByName("Barotrauma.Item") ?? typeof(Item);
            }
            catch (Exception ex)
            {
                ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} FixedHudPatch type reflection failed: {ex.GetType().Name}: {ex.Message}");
                return Enumerable.Empty<MethodBase>();
            }

            if (itemType == null)
            {
                return Enumerable.Empty<MethodBase>();
            }

            MethodInfo[] methods;
            try
            {
                methods = itemType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            catch (Exception ex)
            {
                ModFileLog.Write(
                    "Core",
                    $"{Constants.LogPrefix} FixedHudPatch method reflection failed: {ex.GetType().Name}: {ex.Message}");
                return Enumerable.Empty<MethodBase>();
            }

            foreach (MethodInfo method in methods)
            {
                if (method == null) { continue; }

                try
                {
                    if (!string.Equals(method.Name, "UpdateHUD", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matches.Add(method);
                }
                catch (Exception ex)
                {
                    ModFileLog.WriteDebug(
                        "Core",
                        $"{Constants.LogPrefix} FixedHudPatch skip method by reflection error: {ex.GetType().Name}: {ex.Message}");
                }
            }

            matches = matches
                .Where(m => m != null)
                .Distinct()
                .ToList();

            if (matches.Count <= 0 && !_loggedMissingTarget)
            {
                _loggedMissingTarget = true;
                ModFileLog.WriteDebug("Core", $"{Constants.LogPrefix} FixedHudPatch target missing: Item.UpdateHUD");
            }

            return matches;
        }

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
