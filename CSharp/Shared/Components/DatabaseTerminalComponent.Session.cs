using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;
#if CLIENT
using Microsoft.Xna.Framework;
#endif

public partial class DatabaseTerminalComponent : ItemComponent, IServerSerializable, IClientSerializable
{
    public int CountTakeableForAutomation(Func<ItemData, bool> predicate)
    {
        if (!IsServerAuthority || predicate == null) { return 0; }

        var snapshot = DatabaseStore.GetItemsSnapshot(_resolvedDatabaseId, out _);
        if (snapshot == null || snapshot.Count <= 0) { return 0; }

        int count = 0;
        foreach (var entry in snapshot)
        {
            if (entry == null || !predicate(entry)) { continue; }
            count += Math.Max(1, entry.StackSize);
        }

        return count;
    }

    public bool TryTakeItemsFromNonCurrentPagesForAutomation(
        Func<ItemData, bool> predicate,
        int amount,
        DatabaseStore.TakePolicy policy,
        out List<ItemData> taken)
    {
        taken = new List<ItemData>();
        if (!IsServerAuthority || predicate == null || amount <= 0) { return false; }

        bool ok = DatabaseStore.TryTakeItemsForAutomation(_resolvedDatabaseId, predicate, amount, out var extracted, policy);
        if (!ok || extracted == null || extracted.Count <= 0) { return false; }

        taken = extracted;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        return true;
    }

    private bool HandlePanelActionServer(TerminalPanelAction action, Character actor, string source = "unknown")
    {
        if (!IsServerAuthority) { return false; }

        if (action == TerminalPanelAction.CycleSortMode)
        {
            SortModeIndex = (NormalizeSortModeIndex(SortModeIndex) + 1) % 4;
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
            return true;
        }

        if (action == TerminalPanelAction.ToggleSortOrder)
        {
            SortDescending = !SortDescending;
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
            return true;
        }

        // Session actions are removed in atomic mode.
        return false;
    }

    private void FlushIdleInventoryItems()
    {
        if (!IsServerAuthority || item == null || item.Removed) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null || inventory.Capacity <= 0) { return; }

        int outputStart = Math.Max(0, Math.Min(inventory.Capacity, GetOutputSlotStart(inventory)));
        if (outputStart <= 0)
        {
            // No input partition configured.
            return;
        }

        var inputItems = new List<Item>();
        for (int slot = 0; slot < outputStart; slot++)
        {
            var entry = inventory.GetItemAt(slot);
            if (entry == null || entry.Removed) { continue; }
            inputItems.Add(entry);
        }

        if (inputItems.Count <= 0) { return; }

        var serialized = ItemSerializer.SerializeItems(null, inputItems);
        if (serialized.Count <= 0) { return; }

        DatabaseStore.AppendItems(_resolvedDatabaseId, serialized);

        foreach (var input in inputItems)
        {
            inventory.RemoveItem(input);
            SpawnService.RemoveItem(input);
        }

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);

        if (ModFileLog.IsDebugEnabled)
        {
            ModFileLog.WriteDebug(
                "Terminal",
                $"{Constants.LogPrefix} InputBufferIngest db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"inputItems={inputItems.Count} serializedEntries={serialized.Count} serializedAmount={CountFlatItems(serialized)}");
        }
    }

    private void TryRunPendingPageFillCheck()
    {
        // Legacy session page fill checks are removed in atomic mode.
    }

    private int CountPendingPageItems()
    {
        return 0;
    }

    public bool RequestForceCloseForTakeover(string reason, Character requester, bool convertToClosedItem = true)
    {
        // Session locks are removed. Keep this compatibility hook as a no-op success.
        return true;
    }

    private static int NormalizeSortModeIndex(int raw)
    {
        if (raw < 0 || raw > 3) { return 0; }
        return raw;
    }
}
