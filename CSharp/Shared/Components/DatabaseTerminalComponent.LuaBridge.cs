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
        bool sessionActive = IsSessionActive();
        if (!sessionActive)
        {
            if (ModFileLog.IsDebugEnabled &&
                Timing.TotalTime >= _nextVirtualViewDiagAt)
            {
                _nextVirtualViewDiagAt = Timing.TotalTime + VirtualViewDiagCooldownSeconds;
                ModFileLog.WriteDebug(
                    "Terminal",
                    $"{Constants.LogPrefix} VirtualViewSnapshot empty id={item?.ID} db='{_resolvedDatabaseId}' " +
                    $"sessionActive={sessionActive} cachedOpen={_cachedSessionOpen} " +
                    $"inPlace={_inPlaceSessionActive} sessionVariant={SessionVariant} sessionEntries={_sessionEntries.Count}");
            }
            return snapshot;
        }

        var grouped = new Dictionary<string, TerminalVirtualEntry>(StringComparer.OrdinalIgnoreCase);
        var conditionWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _sessionEntries)
        {
            if (entry == null) { continue; }
            string id = (entry.Identifier ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) { continue; }

            int amount = Math.Max(1, entry.StackSize);
            if (!grouped.TryGetValue(id, out var row))
            {
                row = new TerminalVirtualEntry
                {
                    Identifier = id,
                    PrefabIdentifier = id,
                    DisplayName = ResolveDisplayNameForIdentifier(id),
                    Amount = 0,
                    BestQuality = 0,
                    AverageCondition = 100f
                };
                grouped[id] = row;
                conditionWeight[id] = 0;
            }

            row.Amount += amount;
            row.BestQuality = Math.Max(row.BestQuality, entry.Quality);
            int weight = conditionWeight[id] + amount;
            float weighted = row.AverageCondition * conditionWeight[id] + entry.Condition * amount;
            row.AverageCondition = weight <= 0 ? 100f : weighted / weight;
            conditionWeight[id] = weight;
        }

        snapshot.AddRange(grouped.Values
            .OrderBy(v => v.Identifier ?? "", StringComparer.OrdinalIgnoreCase));
        if (ModFileLog.IsDebugEnabled && Timing.TotalTime >= _nextVirtualViewDiagAt)
        {
            _nextVirtualViewDiagAt = Timing.TotalTime + VirtualViewDiagCooldownSeconds;
            ModFileLog.WriteDebug(
                "Terminal",
                $"{Constants.LogPrefix} VirtualViewSnapshot id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"snapshotEntries={snapshot.Count} sessionEntries={_sessionEntries.Count} " +
                $"sessionActive={sessionActive} cachedOpen={_cachedSessionOpen} " +
                $"inPlace={_inPlaceSessionActive} sessionVariant={SessionVariant}");
        }
        return snapshot;
    }

    public bool IsVirtualSessionOpenForUi()
    {
        bool open = IsSessionActive() || _cachedSessionOpen;
        if (ModFileLog.IsDebugEnabled &&
            Timing.TotalTime >= _nextVirtualViewDiagAt)
        {
            _nextVirtualViewDiagAt = Timing.TotalTime + VirtualViewDiagCooldownSeconds;
            ModFileLog.WriteDebug(
                "Terminal",
                $"{Constants.LogPrefix} VirtualUiOpenState id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"open={open} sessionActive={IsSessionActive()} cachedOpen={_cachedSessionOpen} " +
                $"inPlace={_inPlaceSessionActive} sessionVariant={SessionVariant}");
        }
        return open;
    }

    public string TryTakeOneByIdentifierFromVirtualSession(string identifier, Character actor)
    {
        if (!IsServerAuthority) { return "not_authority"; }
        if (!IsSessionActive()) { return "session_closed"; }
        if (actor == null || actor.Removed || actor.IsDead || actor.Inventory == null) { return "invalid_actor"; }

        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return "invalid_identifier"; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return "inventory_unavailable"; }
        if (!HasAnyEmptyBufferSlot(inventory)) { return "inventory_full"; }

        if (!TryExtractOneVirtualItemData(wanted, out var extracted) || extracted == null)
        {
            return "not_found";
        }

        SpawnService.SpawnItemsIntoInventory(
            new List<ItemData> { extracted },
            inventory,
            actor,
            guard: () => IsSessionActive());

        int preferredPage = _sessionCurrentPageIndex < 0 ? 0 : _sessionCurrentPageIndex;
        BuildPagesBySlotUsage(_sessionEntries, inventory, preferredPage);
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        return "";
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
        }

        return builder.ToString();
    }

    private List<TerminalVirtualEntry> BuildLuaB1Rows()
    {
        var grouped = new Dictionary<string, TerminalVirtualEntry>(StringComparer.OrdinalIgnoreCase);
        var conditionWeight = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in _sessionEntries)
        {
            if (entry == null) { continue; }
            string id = (entry.Identifier ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) { continue; }

            int amount = Math.Max(1, entry.StackSize);
            if (!grouped.TryGetValue(id, out var row))
            {
                row = new TerminalVirtualEntry
                {
                    Identifier = id,
                    PrefabIdentifier = id,
                    DisplayName = id,
                    Amount = 0,
                    BestQuality = 0,
                    AverageCondition = 100f
                };
                grouped[id] = row;
                conditionWeight[id] = 0;
            }

            row.Amount += amount;
            row.BestQuality = Math.Max(row.BestQuality, entry.Quality);
            int weight = conditionWeight[id] + amount;
            float weighted = row.AverageCondition * conditionWeight[id] + entry.Condition * amount;
            row.AverageCondition = weight <= 0 ? 100f : weighted / weight;
            conditionWeight[id] = weight;
        }

        return grouped.Values
            .OrderBy(v => v.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void RefreshLuaB1BridgeState(bool force = false)
    {
        if (!IsServerAuthority) { return; }

        bool sessionOpen = IsSessionActive();
        string dbId = DatabaseStore.Normalize(_resolvedDatabaseId);
        int totalEntries = 0;
        int totalAmount = 0;
        string payload = "";

        if (sessionOpen && _sessionEntries.Count > 0)
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
            Character actor = FindCharacterByEntityId(LuaTakeRequestActorId) ?? _sessionOwner;
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
