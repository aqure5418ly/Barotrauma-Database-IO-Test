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
    internal static class DatabaseTerminalContainerSilencePatch
    {
        private static readonly Identifier DatabaseTerminalTag = "database_terminal".ToIdentifier();
#if CLIENT
        private static double _nextPerfLogAt;
        private static int _prefixCalls;
        private static int _componentSuppressed;
        private static int _containerCandidates;
        private static int _tagFilteredOut;
        private static int _terminalResolved;
        private static int _notSilenced;
        private static int _containerSuppressed;

        private static void FlushPerfIfDue()
        {
            if (!ModFileLog.IsDebugEnabled) { return; }
            if (Timing.TotalTime < _nextPerfLogAt) { return; }
            _nextPerfLogAt = Timing.TotalTime + 1.0;
            if (_prefixCalls <= 0) { return; }

            ModFileLog.WriteDebug(
                "Perf",
                $"{Constants.LogPrefix} ContainerPatchPerf calls={_prefixCalls} componentSuppressed={_componentSuppressed} " +
                $"containerCandidates={_containerCandidates} tagFiltered={_tagFilteredOut} terminalResolved={_terminalResolved} " +
                $"notSilenced={_notSilenced} containerSuppressed={_containerSuppressed}");

            _prefixCalls = 0;
            _componentSuppressed = 0;
            _containerCandidates = 0;
            _tagFilteredOut = 0;
            _terminalResolved = 0;
            _notSilenced = 0;
            _containerSuppressed = 0;
        }
#endif

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
                if (ModFileLog.IsDebugEnabled)
                {
                    _prefixCalls++;
                    FlushPerfIfDue();
                }

                if (__instance is DatabaseTerminalComponent terminal &&
                    terminal.ShouldHijackFixedTerminalUi())
                {
                    // Only suppress native component GUI; Terminal hook drives UI.
                    if (ModFileLog.IsDebugEnabled) { _componentSuppressed++; }
                    return false;
                }

                if (__instance is not ItemContainer container) { return true; }
                if (ModFileLog.IsDebugEnabled) { _containerCandidates++; }
                var owner = container.Item;
                if (owner == null || owner.Removed) { return true; }
                if (!owner.HasTag(DatabaseTerminalTag))
                {
                    if (ModFileLog.IsDebugEnabled) { _tagFilteredOut++; }
                    return true;
                }

                var ownerTerminal = owner.GetComponent<DatabaseTerminalComponent>();
                if (ownerTerminal == null) { return true; }
                if (ModFileLog.IsDebugEnabled) { _terminalResolved++; }
                if (!ownerTerminal.ShouldHijackFixedTerminalUi()) { return true; }

                // For fixed terminals, silence all ItemContainer GUI layers.
                // Fabricator UI uses its own GUI pipeline; it does not depend on ItemContainer.AddToGUIUpdateList.
                if (!ownerTerminal.ShouldSilenceFixedContainerGui())
                {
                    if (ModFileLog.IsDebugEnabled) { _notSilenced++; }
                    return true;
                }

                ownerTerminal.TraceFixedContainerSilenced("harmony:ItemContainer.AddToGUIUpdateList");
                if (ModFileLog.IsDebugEnabled) { _containerSuppressed++; }
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
