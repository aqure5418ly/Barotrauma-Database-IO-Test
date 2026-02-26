using System;
using System.Reflection;
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
    public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
    {
        var data = new SummaryEventData(
            _resolvedDatabaseId,
            _cachedItemCount,
            _cachedLocked,
            true,
            1,
            1,
            0);

        msg.WriteString(data.DatabaseId ?? _resolvedDatabaseId);
        msg.WriteInt32(data.ItemCount);
        msg.WriteBoolean(data.Locked);
        msg.WriteBoolean(data.SessionOpen);
        msg.WriteInt32(data.PageIndex);
        msg.WriteInt32(data.PageTotal);
        msg.WriteInt32(data.RemainingPageItems);
        msg.WriteString(LuaB1RowsPayload ?? "");
    }

    public void ClientEventRead(IReadMessage msg, float sendingTime)
    {
        bool prevOpen = _cachedSessionOpen;
        int prevPage = _cachedPageIndex;
        int prevTotal = _cachedPageTotal;
        int prevCount = _cachedItemCount;

        _resolvedDatabaseId = DatabaseStore.Normalize(msg.ReadString());
        _cachedItemCount = msg.ReadInt32();
        _cachedLocked = msg.ReadBoolean();
        _cachedSessionOpen = msg.ReadBoolean();
        _cachedPageIndex = msg.ReadInt32();
        _cachedPageTotal = msg.ReadInt32();
        _cachedRemainingPageItems = msg.ReadInt32();
        LuaB1RowsPayload = msg.ReadString() ?? "";
        UpdateDescriptionLocal();
#if CLIENT
        if (EnableCsPanelOverlay)
        {
            LogPanelDebug(
                $"summary update id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"open={prevOpen}->{_cachedSessionOpen} page={Math.Max(1, prevPage)}/{Math.Max(1, prevTotal)}->{Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} " +
                $"count={prevCount}->{_cachedItemCount}");
            UpdateClientPanelVisuals();
        }
#endif
    }

    public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
    {
        var action = (TerminalPanelAction)_pendingClientAction;
        msg.WriteByte(_pendingClientAction);
        if (action == TerminalPanelAction.TakeByIdentifier)
        {
            msg.WriteString(_pendingClientTakeIdentifier ?? "");
            msg.WriteByte((byte)Math.Clamp(_pendingClientTakeCount, 1, byte.MaxValue));
            msg.WriteString(_pendingClientTakeVariantKey ?? "");
        }

        _pendingClientAction = (byte)TerminalPanelAction.None;
        _pendingClientTakeIdentifier = "";
        _pendingClientTakeCount = 1;
        _pendingClientTakeVariantKey = "";
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        var action = (TerminalPanelAction)msg.ReadByte();
        string takeIdentifier = "";
        int takeCount = 1;
        string takeVariantKey = "";
        if (action == TerminalPanelAction.TakeByIdentifier)
        {
            takeIdentifier = (msg.ReadString() ?? "").Trim();
            takeCount = Math.Max(1, (int)msg.ReadByte());
            try
            {
                takeVariantKey = (msg.ReadString() ?? "").Trim();
            }
            catch
            {
                takeVariantKey = "";
            }
        }

        if (action == TerminalPanelAction.None) { return; }

        Character actor = c?.Character;
        if (actor == null || actor.Removed || actor.IsDead)
        {
            return;
        }

        if (action == TerminalPanelAction.TakeByIdentifier)
        {
            if (ReadOnlyView)
            {
                ModFileLog.WriteDebug(
                    "Panel",
                    $"{Constants.LogPrefix} take request ignored (read-only) id={item?.ID} db='{_resolvedDatabaseId}' actor='{actor?.Name ?? "none"}' " +
                    $"identifier='{takeIdentifier}' variant='{takeVariantKey}' count={takeCount}");
                return;
            }

            string result = TryTakeByVariantKeyCountFromVirtualSession(takeIdentifier, takeVariantKey, takeCount, actor);
            if (!string.IsNullOrEmpty(result))
            {
                ModFileLog.Write(
                    "Panel",
                    $"{Constants.LogPrefix} take request denied id={item?.ID} db='{_resolvedDatabaseId}' actor='{actor?.Name ?? "none"}' " +
                    $"identifier='{takeIdentifier}' variant='{takeVariantKey}' count={takeCount} reason='{result}'");
            }
            return;
        }

        HandlePanelActionServer(action, actor, "net_event");
    }

    public void ResolveDatabaseId(string normalized)
    {
        _resolvedDatabaseId = DatabaseStore.Normalize(normalized);
        DatabaseId = _resolvedDatabaseId;
    }

    public void ApplyStoreSnapshot(DatabaseData data, bool persistSerializedState = false)
    {
        if (!IsServerAuthority || data == null) { return; }

        ResolveDatabaseId(data.DatabaseId);
        if (persistSerializedState)
        {
            DatabaseVersion = data.Version;
            SerializedDatabase = DatabaseStore.SerializeData(data);
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} PersistTerminalState db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"version={DatabaseVersion} items={data.ItemCount} serializedLen={(SerializedDatabase?.Length ?? 0)}");
        }

        _cachedItemCount = data.ItemCount;
        _cachedLocked = false;
        _cachedSessionOpen = true;
        _cachedPageIndex = 1;
        _cachedPageTotal = 1;
        _cachedRemainingPageItems = 0;
        _lastAppliedStoreVersion = Math.Max(0, data.Version);

        UpdateDescriptionLocal();
        TrySyncSummary();
    }

    public bool ApplyStoreDelta(DatabaseStore.DeltaPacket delta)
    {
        if (!IsServerAuthority || delta == null) { return false; }

        string id = DatabaseStore.Normalize(delta.DatabaseId);
        if (!string.Equals(id, _resolvedDatabaseId, StringComparison.OrdinalIgnoreCase))
        {
            ResolveDatabaseId(id);
        }

        if (!delta.IsSnapshot &&
            _lastAppliedStoreVersion >= 0 &&
            delta.PreviousVersion != _lastAppliedStoreVersion)
        {
            ModFileLog.WriteDebug(
                "Terminal",
                $"{Constants.LogPrefix} Delta gap fallback db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"lastVersion={_lastAppliedStoreVersion} previous={delta.PreviousVersion} incoming={delta.Version} source='{delta.Source}'.");
            return false;
        }

        _cachedItemCount = Math.Max(0, delta.TotalAmount);
        _cachedLocked = false;
        _cachedSessionOpen = true;
        _cachedPageIndex = 1;
        _cachedPageTotal = 1;
        _cachedRemainingPageItems = 0;
        _lastAppliedStoreVersion = Math.Max(0, delta.Version);

        UpdateDescriptionLocal();
        TrySyncSummary();
        return true;
    }

    private void UpdateSummaryFromStore()
    {
        if (!IsServerAuthority) { return; }

        _cachedItemCount = DatabaseStore.GetItemCount(_resolvedDatabaseId);
        _cachedLocked = false;
        _cachedSessionOpen = true;
        _cachedPageIndex = 1;
        _cachedPageTotal = 1;
        _cachedRemainingPageItems = 0;
    }

    private void LoadSummaryFromSerialized()
    {
        var data = DatabaseStore.DeserializeData(SerializedDatabase, DatabaseId);
        _resolvedDatabaseId = data.DatabaseId;
        _cachedItemCount = data.ItemCount;
        _cachedLocked = false;
        _cachedSessionOpen = true;
        _cachedPageIndex = 1;
        _cachedPageTotal = 1;
        _cachedRemainingPageItems = 0;
    }

    private bool IsSessionActive()
    {
        return true;
    }

    private void TrySyncSummary(bool force = false)
    {
        if (GameMain.NetworkMember?.IsServer != true) { return; }
        if (!CanCreateServerSummaryEvent())
        {
            _pendingSummarySync = true;
            _nextPendingSummarySyncAt = Timing.TotalTime + PendingSummarySyncRetrySeconds;
            return;
        }

        bool changed = force ||
                       _lastSyncedDatabaseId != _resolvedDatabaseId ||
                       _lastSyncedItemCount != _cachedItemCount ||
                       _lastSyncedLocked != _cachedLocked ||
                       _lastSyncedSessionOpen != _cachedSessionOpen ||
                       _lastSyncedPageIndex != _cachedPageIndex ||
                       _lastSyncedPageTotal != _cachedPageTotal ||
                       _lastSyncedRemainingPageItems != _cachedRemainingPageItems;
        if (!changed) { return; }

        _lastSyncedDatabaseId = _resolvedDatabaseId;
        _lastSyncedItemCount = _cachedItemCount;
        _lastSyncedLocked = _cachedLocked;
        _lastSyncedSessionOpen = _cachedSessionOpen;
        _lastSyncedPageIndex = _cachedPageIndex;
        _lastSyncedPageTotal = _cachedPageTotal;
        _lastSyncedRemainingPageItems = _cachedRemainingPageItems;

#if SERVER
        try
        {
            item.CreateServerEvent(this, new SummaryEventData(
                _resolvedDatabaseId,
                _cachedItemCount,
                _cachedLocked,
                _cachedSessionOpen,
                _cachedPageIndex,
                _cachedPageTotal,
                _cachedRemainingPageItems));
            _pendingSummarySync = false;
        }
        catch (Exception ex)
        {
            _pendingSummarySync = true;
            _nextPendingSummarySyncAt = Timing.TotalTime + PendingSummarySyncRetrySeconds;
            ModFileLog.Write("Terminal", $"{Constants.LogPrefix} Summary sync deferred db='{_resolvedDatabaseId}' terminal={item?.ID}: {ex.Message}");
        }
#endif
    }

    private bool CanCreateServerSummaryEvent()
    {
        if (item == null || item.Removed) { return false; }
        if (item.ID <= 0) { return false; }

        try
        {
            if (ItemFullyInitializedProperty != null && ItemFullyInitializedProperty.PropertyType == typeof(bool))
            {
                object value = ItemFullyInitializedProperty.GetValue(item);
                if (value is bool ready && !ready) { return false; }
            }

            if (ItemFullyInitializedField != null && ItemFullyInitializedField.FieldType == typeof(bool))
            {
                object value = ItemFullyInitializedField.GetValue(item);
                if (value is bool ready && !ready) { return false; }
            }
        }
        catch
        {
        }

        return Timing.TotalTime - _creationTime >= 0.15;
    }

    private Inventory GetTerminalInventory()
    {
        return GetTerminalBufferContainerComponent()?.Inventory;
    }

    private void InvalidateTerminalContainerCache()
    {
        _cachedTerminalItemContainers = null;
        _cachedTerminalBufferContainer = null;
        _cachedTerminalBufferRequestedIndex = int.MinValue;
        _cachedTerminalBufferResolvedIndex = -1;
    }

    internal ItemContainer GetTerminalBufferContainerComponent()
    {
        if (item == null || item.Removed)
        {
            InvalidateTerminalContainerCache();
            return null;
        }

        if (_cachedTerminalItemContainers == null || _cachedTerminalItemContainers.Length == 0)
        {
            _cachedTerminalItemContainers = item.GetComponents<ItemContainer>()?.ToArray() ?? Array.Empty<ItemContainer>();
            _cachedTerminalBufferContainer = null;
            _cachedTerminalBufferRequestedIndex = int.MinValue;
            _cachedTerminalBufferResolvedIndex = -1;
        }

        if (_cachedTerminalItemContainers == null || _cachedTerminalItemContainers.Length == 0) { return null; }

        int requestedIndex = TerminalBufferContainerIndex;
        int clampedIndex = Math.Clamp(requestedIndex, 0, _cachedTerminalItemContainers.Length - 1);

        if (_cachedTerminalBufferContainer != null &&
            _cachedTerminalBufferRequestedIndex == requestedIndex &&
            _cachedTerminalBufferResolvedIndex == clampedIndex &&
            clampedIndex >= 0 &&
            clampedIndex < _cachedTerminalItemContainers.Length &&
            ReferenceEquals(_cachedTerminalItemContainers[clampedIndex], _cachedTerminalBufferContainer))
        {
            return _cachedTerminalBufferContainer;
        }

        if (requestedIndex != clampedIndex && !_terminalBufferIndexFallbackWarned)
        {
            _terminalBufferIndexFallbackWarned = true;
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} terminalBufferContainerIndex out of range id={item?.ID} requested={requestedIndex} " +
                $"available={_cachedTerminalItemContainers.Length} fallback={clampedIndex}");
        }

        _cachedTerminalBufferRequestedIndex = requestedIndex;
        _cachedTerminalBufferResolvedIndex = clampedIndex;
        _cachedTerminalBufferContainer = _cachedTerminalItemContainers[clampedIndex];
        return _cachedTerminalBufferContainer;
    }

    internal bool IsTerminalBufferContainer(ItemContainer container)
    {
        if (container == null) { return false; }
        if (_cachedTerminalBufferContainer != null && ReferenceEquals(container, _cachedTerminalBufferContainer))
        {
            return true;
        }
        return ReferenceEquals(container, GetTerminalBufferContainerComponent());
    }

    private void UpdateDescriptionLocal()
    {
        string dbLabel = T("dbiotest.terminal.dbid", "Database ID");
        string countLabel = T("dbiotest.terminal.count", "Stored Item Count");
        string hint = T("dbiotest.terminal.closedhint", "Use terminal panel to browse and take items.");

        string sortLabel = T("dbiotest.terminal.sort", "Sort");
        string directionLabel = SortDescending ? T("dbiotest.terminal.sortdesc", "Desc") : T("dbiotest.terminal.sortasc", "Asc");
        string searchLabel = T("dbiotest.terminal.search", "Search");
        string searchValue = string.IsNullOrWhiteSpace(SearchKeyword) ? "-" : SearchKeyword;
        string readOnlyLine = ReadOnlyView ? $"\n{T("dbiotest.panel.readonly", "Read-only view: take actions are disabled.")}" : "";

        string powerLine = "";
        if (RequirePower)
        {
            string powerLabel = T("dbiotest.power.status", "Power");
            string online = T("dbiotest.power.online", "Online");
            string offline = T("dbiotest.power.offline", "Offline");
            string state = HasRequiredPower() ? online : offline;
            powerLine = $"\n{powerLabel}: {state} ({GetCurrentVoltage():0.##}/{Math.Max(0f, MinRequiredVoltage):0.##}V)";
        }

        item.Description =
            $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{countLabel}: {_cachedItemCount}" +
            $"\n{sortLabel}: {GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} ({directionLabel})" +
            $"\n{searchLabel}: {searchValue}{powerLine}{readOnlyLine}";
    }

    private static string GetSortModeLabel(TerminalSortMode mode)
    {
        return mode switch
        {
            TerminalSortMode.Condition => T("dbiotest.terminal.sort.condition", "Condition"),
            TerminalSortMode.Quality => T("dbiotest.terminal.sort.quality", "Quality"),
            TerminalSortMode.StackSize => T("dbiotest.terminal.sort.stacksize", "StackSize"),
            _ => T("dbiotest.terminal.sort.identifier", "Identifier")
        };
    }

    private float GetCurrentVoltage()
    {
        var powered = item.GetComponent<Powered>();
        return powered?.Voltage ?? 0f;
    }

    private bool HasRequiredPower()
    {
        if (!RequirePower) { return true; }
        float minVoltage = Math.Max(0f, MinRequiredVoltage);
        return GetCurrentVoltage() >= minVoltage;
    }
}
