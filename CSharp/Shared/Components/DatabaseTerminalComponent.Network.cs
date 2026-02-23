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
    public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
    {
        var data = ExtractEventData<SummaryEventData>(extraData);
        if (string.IsNullOrEmpty(data.DatabaseId))
        {
            data = new SummaryEventData(
                _resolvedDatabaseId,
                _cachedItemCount,
                _cachedLocked,
                IsSessionActive(),
                _cachedPageIndex,
                _cachedPageTotal,
                _cachedRemainingPageItems);
        }

        msg.WriteString(data.DatabaseId ?? _resolvedDatabaseId);
        msg.WriteInt32(data.ItemCount);
        msg.WriteBoolean(data.Locked);
        msg.WriteBoolean(data.SessionOpen);
        msg.WriteInt32(data.PageIndex);
        msg.WriteInt32(data.PageTotal);
        msg.WriteInt32(data.RemainingPageItems);
    }

    public void ClientEventRead(IReadMessage msg, float sendingTime)
    {
        bool prevOpen = _cachedSessionOpen;
        int prevPage = _cachedPageIndex;
        int prevTotal = _cachedPageTotal;
        int prevCount = _cachedItemCount;
        bool prevLocked = _cachedLocked;

        _resolvedDatabaseId = DatabaseStore.Normalize(msg.ReadString());
        _cachedItemCount = msg.ReadInt32();
        _cachedLocked = msg.ReadBoolean();
        _cachedSessionOpen = msg.ReadBoolean();
        _cachedPageIndex = msg.ReadInt32();
        _cachedPageTotal = msg.ReadInt32();
        _cachedRemainingPageItems = msg.ReadInt32();
        UpdateDescriptionLocal();
#if CLIENT
        if (EnableCsPanelOverlay)
        {
            LogPanelDebug(
                $"summary update id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"open={prevOpen}->{_cachedSessionOpen} page={Math.Max(1, prevPage)}/{Math.Max(1, prevTotal)}->{Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} " +
                $"count={prevCount}->{_cachedItemCount} locked={prevLocked}->{_cachedLocked}");
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
        }
        _pendingClientAction = (byte)TerminalPanelAction.None;
        _pendingClientTakeIdentifier = "";
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        var action = (TerminalPanelAction)msg.ReadByte();
        string takeIdentifier = "";
        if (action == TerminalPanelAction.TakeByIdentifier)
        {
            takeIdentifier = (msg.ReadString() ?? "").Trim();
        }
        if (action == TerminalPanelAction.None) { return; }
        if (!SessionVariant && !_inPlaceSessionActive) { return; }

        Character actor = c?.Character;
        if (actor == null || actor.Removed || actor.IsDead)
        {
            return;
        }

        if (action == TerminalPanelAction.TakeByIdentifier)
        {
            string result = TryTakeOneByIdentifierFromVirtualSession(takeIdentifier, actor);
            if (!string.IsNullOrEmpty(result))
            {
                ModFileLog.Write(
                    "Panel",
                    $"{Constants.LogPrefix} take request denied id={item?.ID} db='{_resolvedDatabaseId}' actor='{actor?.Name ?? "none"}' " +
                    $"identifier='{takeIdentifier}' reason='{result}'");
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
        _cachedLocked = DatabaseStore.IsLocked(_resolvedDatabaseId);
        _cachedSessionOpen = IsSessionActive();
        _cachedPageIndex = _cachedSessionOpen ? Math.Max(1, _sessionCurrentPageIndex + 1) : 0;
        _cachedPageTotal = _cachedSessionOpen ? Math.Max(1, _sessionPages.Count) : 0;
        _cachedRemainingPageItems = _cachedSessionOpen ? CountPendingPageItems() : 0;
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
        _cachedLocked = DatabaseStore.IsLocked(_resolvedDatabaseId);
        _cachedSessionOpen = IsSessionActive();
        _cachedPageIndex = _cachedSessionOpen ? Math.Max(1, _sessionCurrentPageIndex + 1) : 0;
        _cachedPageTotal = _cachedSessionOpen ? Math.Max(1, _sessionPages.Count) : 0;
        _cachedRemainingPageItems = _cachedSessionOpen ? CountPendingPageItems() : 0;
        _lastAppliedStoreVersion = Math.Max(0, delta.Version);

        UpdateDescriptionLocal();
        TrySyncSummary();
        return true;
    }
    private void UpdateSummaryFromStore()
    {
        if (!IsServerAuthority) { return; }

        _cachedItemCount = DatabaseStore.GetItemCount(_resolvedDatabaseId);
        _cachedLocked = DatabaseStore.IsLocked(_resolvedDatabaseId);
        _cachedSessionOpen = IsSessionActive();
        _cachedPageIndex = _cachedSessionOpen ? Math.Max(1, _sessionCurrentPageIndex + 1) : 0;
        _cachedPageTotal = _cachedSessionOpen ? Math.Max(1, _sessionPages.Count) : 0;
        _cachedRemainingPageItems = _cachedSessionOpen ? CountPendingPageItems() : 0;
    }

    private void LoadSummaryFromSerialized()
    {
        var data = DatabaseStore.DeserializeData(SerializedDatabase, DatabaseId);
        _resolvedDatabaseId = data.DatabaseId;
        _cachedItemCount = data.ItemCount;
        _cachedLocked = false;
        _cachedSessionOpen = IsSessionActive();
        _cachedPageIndex = _cachedSessionOpen ? Math.Max(1, _sessionCurrentPageIndex + 1) : 0;
        _cachedPageTotal = _cachedSessionOpen ? Math.Max(1, _sessionPages.Count) : 0;
        _cachedRemainingPageItems = _cachedSessionOpen ? CountPendingPageItems() : 0;
    }

    private bool IsSessionActive()
    {
        return SessionVariant || _inPlaceSessionActive;
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

        // During entity spawn the item may exist but still be marked as not fully initialized.
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
            // Ignore reflection issues and fall back to age-based guard.
        }

        return Timing.TotalTime - _creationTime >= 0.15;
    }

    private Inventory GetTerminalInventory()
    {
        return item.GetComponent<ItemContainer>()?.Inventory;
    }

    private void UpdateDescriptionLocal()
    {
        string dbLabel = T("dbiotest.terminal.dbid", "Database ID");
        string countLabel = T("dbiotest.terminal.count", "Stored Item Count");
        string hint = _cachedSessionOpen
            ? T("dbiotest.terminal.openhint", "Session active. Use panel buttons to switch pages or close session.")
            : T("dbiotest.terminal.closedhint", "Use to open database session and populate container.");

        string lockLine = _cachedLocked && !_cachedSessionOpen
            ? "\n" + T("dbiotest.terminal.locked", "Locked by another terminal session.")
            : "";

        string pageLine = "";
        if (_cachedSessionOpen)
        {
            string pageLabel = T("dbiotest.terminal.page", "Page");
            string remainingLabel = T("dbiotest.terminal.remainingpages", "Pending Page Items");
            string sortLabel = T("dbiotest.terminal.sort", "Sort");
            string directionLabel = SortDescending ? T("dbiotest.terminal.sortdesc", "Desc") : T("dbiotest.terminal.sortasc", "Asc");
            string searchLabel = T("dbiotest.terminal.search", "Search");
            string searchValue = string.IsNullOrWhiteSpace(SearchKeyword) ? "-" : SearchKeyword;
            pageLine =
                $"\n{pageLabel}: {Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)}" +
                $"\n{remainingLabel}: {_cachedRemainingPageItems}" +
                $"\n{sortLabel}: {GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} ({directionLabel})" +
                $"\n{searchLabel}: {searchValue}";
        }

        string powerLine = "";
        if (RequirePower)
        {
            string powerLabel = T("dbiotest.power.status", "Power");
            string online = T("dbiotest.power.online", "Online");
            string offline = T("dbiotest.power.offline", "Offline");
            string state = HasRequiredPower() ? online : offline;
            powerLine = $"\n{powerLabel}: {state} ({GetCurrentVoltage():0.##}/{Math.Max(0f, MinRequiredVoltage):0.##}V)";
        }

        item.Description = $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{countLabel}: {_cachedItemCount}{pageLine}{powerLine}{lockLine}";
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
