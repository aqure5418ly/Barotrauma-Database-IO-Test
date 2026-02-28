using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
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
    // Keep the optional refreshCurrentPage parameter for Lua bridge compatibility.
    // Current implementation does not use this flag but external callers rely on this signature.
    public List<TerminalVirtualEntry> GetVirtualViewSnapshot(bool refreshCurrentPage = true)
    {
        var snapshot = new List<TerminalVirtualEntry>();
        var items = DatabaseStore.GetItemsSnapshot(_resolvedDatabaseId, out _);
        if (items == null || items.Count <= 0) { return snapshot; }

        var signatureOrdinal = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var entry in items)
        {
            if (entry == null) { continue; }
            string id = (entry.Identifier ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) { continue; }

            int amount = Math.Max(1, entry.StackSize);
            string baseSignature = BuildVariantBaseSignature(entry);
            int ordinal = 0;
            if (signatureOrdinal.TryGetValue(baseSignature, out int currentOrdinal))
            {
                ordinal = currentOrdinal;
            }
            signatureOrdinal[baseSignature] = ordinal + 1;
            bool hasContained = entry.ContainedItems != null && entry.ContainedItems.Count > 0;

            snapshot.Add(new TerminalVirtualEntry
            {
                Identifier = id,
                PrefabIdentifier = id,
                DisplayName = ResolveDisplayNameForIdentifier(id),
                VariantKey = BuildVariantKey(baseSignature, ordinal),
                HasContainedItems = hasContained,
                VariantQuality = Math.Max(0, entry.Quality),
                VariantCondition = Math.Max(0f, entry.Condition),
                Amount = amount,
                BestQuality = Math.Max(0, entry.Quality),
                AverageCondition = Math.Max(0f, entry.Condition)
            });
        }

        snapshot = snapshot
            .OrderBy(v => v.DisplayName ?? v.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.VariantQuality)
            .ThenBy(v => v.VariantCondition)
            .ThenBy(v => v.VariantKey ?? "", StringComparer.Ordinal)
            .ToList();
        return snapshot;
    }

    public bool IsVirtualSessionOpenForUi()
    {
        return true;
    }

    public string TryTakeOneByIdentifierFromVirtualSession(string identifier, Character actor)
    {
        if (ReadOnlyView) { return "read_only"; }
        if (!HasRequiredPower()) { return "no_power"; }
        if (!IsServerAuthority) { return "not_authority"; }
        if (actor == null || actor.Removed || actor.IsDead || actor.Inventory == null) { return "invalid_actor"; }

        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return "invalid_identifier"; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return "inventory_unavailable"; }
        if (!TryFindEmptyOutputSlot(inventory, out int outputSlot)) { return "inventory_full"; }

        if (!DatabaseStore.TryTakeOneByIdentifier(_resolvedDatabaseId, wanted, out var extracted) || extracted == null)
        {
            return "not_found";
        }
        extracted.StackSize = 1;
        extracted.SlotIndices = new List<int> { outputSlot };

        SpawnService.SpawnItemsIntoInventory(
            new List<ItemData> { extracted },
            inventory,
            actor);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        return "";
    }

    public string TryTakeByIdentifierCountFromVirtualSession(string identifier, int count, Character actor)
    {
        if (ReadOnlyView) { return "read_only"; }
        int remaining = Math.Clamp(count, 1, byte.MaxValue);
        string lastFailure = "not_found";
        int taken = 0;
        while (remaining > 0)
        {
            string result = TryTakeOneByIdentifierFromVirtualSession(identifier, actor);
            if (!string.IsNullOrEmpty(result))
            {
                lastFailure = result;
                break;
            }

            taken++;
            remaining--;
        }

        return taken > 0 ? "" : lastFailure;
    }

    public string TryTakeByVariantKeyCountFromVirtualSession(string identifier, string variantKey, int count, Character actor)
    {
        if (ReadOnlyView) { return "read_only"; }
        if (string.IsNullOrWhiteSpace(variantKey))
        {
            return TryTakeByIdentifierCountFromVirtualSession(identifier, count, actor);
        }

        int remaining = Math.Clamp(count, 1, byte.MaxValue);
        string lastFailure = "not_found";
        int taken = 0;
        while (remaining > 0)
        {
            string result = TryTakeOneByVariantKeyFromVirtualSession(identifier, variantKey, actor);
            if (!string.IsNullOrEmpty(result))
            {
                lastFailure = result;
                break;
            }

            taken++;
            remaining--;
        }

        return taken > 0 ? "" : lastFailure;
    }

    private string TryTakeOneByVariantKeyFromVirtualSession(string identifier, string variantKey, Character actor)
    {
        if (ReadOnlyView) { return "read_only"; }
        if (!HasRequiredPower()) { return "no_power"; }
        if (!IsServerAuthority) { return "not_authority"; }
        if (actor == null || actor.Removed || actor.IsDead || actor.Inventory == null) { return "invalid_actor"; }

        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return "invalid_identifier"; }
        string wantedVariant = (variantKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wantedVariant)) { return "invalid_variant"; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return "inventory_unavailable"; }
        if (!TryFindEmptyOutputSlot(inventory, out int outputSlot)) { return "inventory_full"; }

        if (!DatabaseStore.TryTakeOneByVariantKey(_resolvedDatabaseId, wanted, wantedVariant, out var extracted) || extracted == null)
        {
            return "not_found";
        }
        extracted.StackSize = 1;
        extracted.SlotIndices = new List<int> { outputSlot };

        SpawnService.SpawnItemsIntoInventory(
            new List<ItemData> { extracted },
            inventory,
            actor);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        return "";
    }

    private static int GetOutputSlotStart(Inventory inventory)
    {
        if (inventory == null) { return 0; }
        int capacity = Math.Max(0, inventory.Capacity);
        if (capacity <= 1) { return 0; }
        return capacity / 2;
    }

    private static bool TryFindEmptyOutputSlot(Inventory inventory, out int slot)
    {
        slot = -1;
        if (inventory == null) { return false; }

        int start = Math.Max(0, GetOutputSlotStart(inventory));
        for (int i = start; i < inventory.Capacity; i++)
        {
            if (inventory.GetItemAt(i) == null)
            {
                slot = i;
                return true;
            }
        }

        return false;
    }

    private static Character FindCharacterByEntityId(int entityId)
    {
        if (entityId <= 0) { return null; }
        foreach (var character in Character.CharacterList)
        {
            if (character == null || character.Removed) { continue; }
            if (character.ID == entityId) { return character; }
        }
        return null;
    }

    private static string SanitizeLuaBridgeField(string value)
    {
        if (string.IsNullOrEmpty(value)) { return ""; }
        return value
            .Replace(LuaFieldSeparator, ' ')
            .Replace(LuaRowSeparator, ' ')
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string ResolveDisplayNameForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab == null) { return id; }

        string localized = prefab.Name?.ToString();
        return string.IsNullOrWhiteSpace(localized) ? id : localized.Trim();
    }

    private static string BuildVariantKey(string baseSignature, int ordinal)
    {
        return $"{baseSignature}#{Math.Max(0, ordinal)}";
    }

    private static string BuildVariantBaseSignature(ItemData item)
    {
        if (item == null) { return "null"; }
        string id = ((item.Identifier ?? "").Trim()).ToLowerInvariant();
        int quality = Math.Max(0, item.Quality);
        string condition = Math.Max(0f, item.Condition).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string contained = BuildContainedSignature(item);
        // Keep variant key stable while amount changes during repeated take actions.
        return $"{id}|q={quality}|c={condition}|sub={contained}";
    }

    private static string BuildContainedSignature(ItemData item)
    {
        if (item?.ContainedItems == null || item.ContainedItems.Count <= 0)
        {
            return "none";
        }

        var childSignatures = new List<string>(item.ContainedItems.Count);
        foreach (var child in item.ContainedItems)
        {
            if (child == null) { continue; }
            childSignatures.Add(BuildVariantBaseSignature(child));
        }

        childSignatures.Sort(StringComparer.Ordinal);
        return string.Join(";", childSignatures);
    }

    private static string EncodeLuaB1Rows(IReadOnlyList<TerminalVirtualEntry> rows)
    {
        if (rows == null || rows.Count <= 0) { return ""; }

        var builder = new StringBuilder(rows.Count * 36);
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row == null) { continue; }
            if (builder.Length > 0) { builder.Append(LuaRowSeparator); }

            builder.Append(SanitizeLuaBridgeField(row.Identifier ?? ""));
            builder.Append(LuaFieldSeparator);
            builder.Append(SanitizeLuaBridgeField(row.PrefabIdentifier ?? row.Identifier ?? ""));
            builder.Append(LuaFieldSeparator);
            builder.Append(SanitizeLuaBridgeField(row.DisplayName ?? row.Identifier ?? ""));
            builder.Append(LuaFieldSeparator);
            builder.Append(Math.Max(0, row.Amount));
            builder.Append(LuaFieldSeparator);
            builder.Append(Math.Max(0, row.BestQuality));
            builder.Append(LuaFieldSeparator);
            builder.Append(row.AverageCondition.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
            builder.Append(LuaFieldSeparator);
            builder.Append(SanitizeLuaBridgeField(row.VariantKey ?? ""));
            builder.Append(LuaFieldSeparator);
            builder.Append(row.HasContainedItems ? "1" : "0");
            builder.Append(LuaFieldSeparator);
            builder.Append(Math.Max(0, row.VariantQuality));
            builder.Append(LuaFieldSeparator);
            builder.Append(Math.Max(0f, row.VariantCondition).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    private List<TerminalVirtualEntry> BuildLuaB1Rows()
    {
        var rows = GetVirtualViewSnapshot(refreshCurrentPage: false) ?? new List<TerminalVirtualEntry>();
        foreach (var row in rows)
        {
            if (row == null) { continue; }
            // Keep payload locale-agnostic; clients resolve localized names locally.
            row.DisplayName = row.Identifier ?? "";
        }
        return rows;
    }

    private void RefreshLuaB1BridgeState(bool force = false)
    {
        if (!IsServerAuthority) { return; }

        bool sessionOpen = true;
        string dbId = DatabaseStore.Normalize(_resolvedDatabaseId);
        int totalEntries = 0;
        int totalAmount = 0;
        string payload = "";

        if (sessionOpen)
        {
            var rows = BuildLuaB1Rows();
            totalEntries = rows.Count;
            totalAmount = rows.Sum(row => Math.Max(0, row?.Amount ?? 0));
            payload = EncodeLuaB1Rows(rows);
        }

        bool changed = force ||
                       LuaB1SessionOpen != sessionOpen ||
                       !string.Equals(LuaB1DatabaseId ?? "", dbId, StringComparison.OrdinalIgnoreCase) ||
                       LuaB1TotalEntries != totalEntries ||
                       LuaB1TotalAmount != totalAmount ||
                       !string.Equals(LuaB1RowsPayload ?? "", payload ?? "", StringComparison.Ordinal);
        if (!changed) { return; }

        LuaB1SessionOpen = sessionOpen;
        LuaB1DatabaseId = dbId;
        LuaB1TotalEntries = totalEntries;
        LuaB1TotalAmount = totalAmount;
        LuaB1RowsPayload = payload ?? "";
        LuaB1RowsSerial = unchecked(LuaB1RowsSerial + 1);
        if (LuaB1RowsSerial <= 0) { LuaB1RowsSerial = 1; }

        if (ModFileLog.IsDebugEnabled &&
            Timing.TotalTime >= _nextLuaBridgeDiagAt)
        {
            _nextLuaBridgeDiagAt = Timing.TotalTime + LuaBridgeDiagCooldownSeconds;
            ModFileLog.WriteDebug(
                "Terminal",
                $"{Constants.LogPrefix} LuaB1State id={item?.ID} db='{LuaB1DatabaseId}' open={LuaB1SessionOpen} " +
                $"serial={LuaB1RowsSerial} entries={LuaB1TotalEntries} amount={LuaB1TotalAmount} payloadLen={(LuaB1RowsPayload?.Length ?? 0)}");
        }
    }

    private void TryProcessLuaTakeRequestFromBridge()
    {
        if (!IsServerAuthority) { return; }
        if (_processingLuaTakeRequest) { return; }

        int nonce = _luaTakeRequestNonce;
        if (nonce <= 0 || nonce == _lastProcessedLuaTakeRequestNonce) { return; }

        _processingLuaTakeRequest = true;
        try
        {
            _lastProcessedLuaTakeRequestNonce = nonce;
            Character actor = FindCharacterByEntityId(LuaTakeRequestActorId);
            string reason = TryTakeOneByIdentifierFromVirtualSession(LuaTakeRequestIdentifier, actor);
            LuaTakeResultNonce = nonce;
            LuaTakeResultCode = reason ?? "";
            RefreshLuaB1BridgeState(force: true);

            if (ModFileLog.IsDebugEnabled)
            {
                ModFileLog.WriteDebug(
                    "Terminal",
                    $"{Constants.LogPrefix} LuaTakeRequest processed id={item?.ID} db='{_resolvedDatabaseId}' nonce={nonce} " +
                    $"identifier='{LuaTakeRequestIdentifier ?? ""}' actor={LuaTakeRequestActorId} result='{LuaTakeResultCode ?? ""}'");
            }
        }
        catch (Exception ex)
        {
            LuaTakeResultNonce = nonce;
            LuaTakeResultCode = "exception";
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} LuaTakeRequest failed id={item?.ID} db='{_resolvedDatabaseId}' nonce={nonce}: {ex.Message}");
        }
        finally
        {
            _processingLuaTakeRequest = false;
        }
    }
}
