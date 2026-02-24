using System;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;
using HarmonyLib;

namespace DatabaseIOTest.Patches
{
    [HarmonyPatch(typeof(ItemComponent), "AddToGUIUpdateList")]
    internal static class DatabaseAutoRestockerPreviewPatch
    {
        private static void Postfix(ItemComponent __instance)
        {
#if CLIENT
            try
            {
                if (__instance is DatabaseAutoRestockerComponent restocker)
                {
                    restocker.DrawClientPreviewFromGuiHook("harmony:ItemComponent.AddToGUIUpdateList:self");
                    return;
                }

                if (__instance is not CustomInterface customInterface) { return; }
                var owner = customInterface.Item;
                if (owner == null || owner.Removed) { return; }
                var ownerRestocker = owner.GetComponent<DatabaseAutoRestockerComponent>();
                if (ownerRestocker == null) { return; }
                ownerRestocker.DrawClientPreviewFromGuiHook("harmony:ItemComponent.AddToGUIUpdateList:custom_interface");
            }
            catch (Exception ex)
            {
                if (ModFileLog.IsDebugEnabled)
                {
                    ModFileLog.WriteDebug(
                        "Restocker",
                        $"{Constants.LogPrefix} AutoRestockerPreviewPatch failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
#endif
        }
    }
}
