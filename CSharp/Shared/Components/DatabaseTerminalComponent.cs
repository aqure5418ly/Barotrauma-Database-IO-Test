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
    private enum TerminalPanelAction : byte
    {
        None = 0,
        PrevPage = 1,
        NextPage = 2,
        CloseSession = 3,
        OpenSession = 4,
        ForceOpenSession = 5,
        PrevMatch = 6,
        NextMatch = 7,
        CycleSortMode = 8,
        ToggleSortOrder = 9,
        CompactItems = 10
    }

    private enum TerminalSortMode : int
    {
        Identifier = 0,
        Condition = 1,
        Quality = 2,
        StackSize = 3
    }

    private readonly struct SummaryEventData : IEventData
    {
        public readonly string DatabaseId;
        public readonly int ItemCount;
        public readonly bool Locked;
        public readonly bool SessionOpen;
        public readonly int PageIndex;
        public readonly int PageTotal;
        public readonly int RemainingPageItems;

        public SummaryEventData(
            string databaseId,
            int itemCount,
            bool locked,
            bool sessionOpen,
            int pageIndex,
            int pageTotal,
            int remainingPageItems)
        {
            DatabaseId = databaseId;
            ItemCount = itemCount;
            Locked = locked;
            SessionOpen = sessionOpen;
            PageIndex = pageIndex;
            PageTotal = pageTotal;
            RemainingPageItems = remainingPageItems;
        }
    }

    [Editable, Serialize(Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = Constants.DefaultDatabaseId;

    [Editable(MinValueFloat = 5f, MaxValueFloat = 3600f), Serialize(Constants.DefaultTerminalSessionTimeout, IsPropertySaveable.Yes, description: "Session timeout in seconds.")]
    public float TerminalSessionTimeout { get; set; } = Constants.DefaultTerminalSessionTimeout;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Require incoming electrical power to operate.")]
    public bool RequirePower { get; set; } = false;

    [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10f), Serialize(0.5f, IsPropertySaveable.Yes, description: "Minimum voltage required when RequirePower=true.")]
    public float MinRequiredVoltage { get; set; } = 0.5f;

    [Editable(MinValueInt = 8, MaxValueInt = 512), Serialize(20, IsPropertySaveable.Yes, description: "Max top-level entries loaded per page.")]
    public int TerminalPageSize { get; set; } = 20;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Whether this item is the open session variant.")]
    public bool SessionVariant { get; set; } = false;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Keep session in-place without swapping item identifier.")]
    public bool UseInPlaceSession { get; set; } = false;

    [Editable, Serialize("DatabaseTerminalSession", IsPropertySaveable.Yes, description: "Open session terminal identifier.")]
    public string OpenSessionIdentifier { get; set; } = "DatabaseTerminalSession";

    [Editable, Serialize("DatabaseTerminal", IsPropertySaveable.Yes, description: "Closed terminal identifier.")]
    public string ClosedTerminalIdentifier { get; set; } = "DatabaseTerminal";

    [Serialize("", IsPropertySaveable.Yes, description: "Persisted shared database encoded string.")]
    public string SerializedDatabase { get; set; } = "";

    [Serialize(0, IsPropertySaveable.Yes, description: "Persisted database version.")]
    public int DatabaseVersion { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "XML button action request (1=Prev,2=Next,3=Close,4=Open,5=ForceOpen,6=PrevMatch,7=NextMatch,8=SortMode,9=SortOrder,10=Compact).")]
    public int XmlActionRequest { get; set; } = 0;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Search keyword for page jump by identifier.")]
    public string SearchKeyword { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Sort mode (0=Identifier,1=Condition,2=Quality,3=StackSize).")]
    public int SortModeIndex { get; set; } = 0;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Sort descending when true.")]
    public bool SortDescending { get; set; } = false;

    private string _resolvedDatabaseId = Constants.DefaultDatabaseId;
    private Character _sessionOwner;
    private double _sessionOpenedAt;
    private readonly double _creationTime;
    private double _lastTickTime;
    private double _nextToggleAllowedTime;
    private double _nextPanelActionAllowedTime;
    private double _nextNoPowerLogTime;
    private bool _inPlaceSessionActive;
    private int _pendingTakeoverRequesterId = -1;
    private double _pendingTakeoverUntil;
    private double _currentPageLoadedAt;

    private readonly List<List<ItemData>> _sessionPages = new List<List<ItemData>>();
    private int _sessionCurrentPageIndex = -1;
    private int _sessionTotalEntryCount;
    private int _pageLoadGeneration;
    private int _sessionAutomationConsumedCount;

    private int _cachedItemCount;
    private bool _cachedLocked;
    private bool _cachedSessionOpen;
    private int _cachedPageIndex;
    private int _cachedPageTotal;
    private int _cachedRemainingPageItems;

    private string _lastSyncedDatabaseId;
    private int _lastSyncedItemCount = -1;
    private bool _lastSyncedLocked;
    private bool _lastSyncedSessionOpen;
    private int _lastSyncedPageIndex = -1;
    private int _lastSyncedPageTotal = -1;
    private int _lastSyncedRemainingPageItems = -1;

    private byte _pendingClientAction;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
    private const double ToggleCooldownSeconds = 0.6;
    private const double MinSessionDurationBeforeClose = 0.9;
    private const double PanelActionCooldownSeconds = 0.4;
    private const double TakeoverConfirmWindowSeconds = 4.0;
    private const double PageActionSafetySeconds = 0.55;

#if CLIENT
    private GUIFrame _panelFrame;
    private GUITextBlock _panelTitle;
    private GUITextBlock _panelPageInfo;
    private GUIButton _panelPrevButton;
    private GUIButton _panelNextButton;
    private GUIButton _panelCloseButton;
    private double _nextClientPanelActionAllowedTime;
    private const float PanelInteractionRange = 220f;
    private const bool EnableCsPanelOverlay = false;
    private const bool EnablePanelDebugLog = false;
    private const double PanelDebugLogCooldown = 0.35;
    private bool _panelLastVisible;
    private string _panelLastHiddenReason = "";
    private double _nextPanelStateLogAllowedTime;
    private double _nextNoCanvasLogAllowedTime;
#endif

    public DatabaseTerminalComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
        _creationTime = Timing.TotalTime;
    }

    public int TerminalEntityId => item?.ID ?? -1;

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);

        if (IsServerAuthority)
        {
            DatabaseStore.RegisterTerminal(this);

            if (SessionVariant)
            {
                DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID);
                if (_sessionOpenedAt <= 0)
                {
                    _sessionOpenedAt = Timing.TotalTime;
                }

                // Recovery path when a session item exists after load.
                BuildPagesFromCurrentInventory();
            }

            UpdateSummaryFromStore();
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
        }
        else
        {
            LoadSummaryFromSerialized();
            UpdateDescriptionLocal();
        }
    }

    public override void Update(float deltaTime, Camera cam)
    {
#if CLIENT
        if (EnableCsPanelOverlay)
        {
            UpdateClientPanel();
        }
#endif

        if (!IsServerAuthority) { return; }

        if (Timing.TotalTime - _lastTickTime < 0.25)
        {
            return;
        }
        _lastTickTime = Timing.TotalTime;

        if (IsSessionActive() && ShouldAutoClose())
        {
            if (SessionVariant)
            {
                CloseSessionInternal("timeout or invalid owner", true, _sessionOwner);
            }
            else
            {
                CloseSessionInPlace("timeout or invalid owner");
            }
        }

        // In-place terminals remain as one item. If someone inserts items while session is closed,
        // immediately return those items so they cannot be silently cleared on open.
        FlushIdleInventoryItems();

        ConsumeXmlActionRequest();

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary();
    }

    public override bool SecondaryUse(float deltaTime, Character character = null)
    {
        if (character == null) { return false; }
        if (Timing.TotalTime < _nextToggleAllowedTime) { return true; }
        if (Timing.TotalTime - _creationTime < 0.35) { return true; }
        if (!IsServerAuthority) { return false; }

        if (!SessionVariant && !HasRequiredPower())
        {
            if (Timing.TotalTime >= _nextNoPowerLogTime)
            {
                _nextNoPowerLogTime = Timing.TotalTime + 3.0;
                DebugConsole.NewMessage(
                    $"{Constants.LogPrefix} Terminal '{_resolvedDatabaseId}' has no power (need {Math.Max(0f, MinRequiredVoltage):0.##}V).",
                    Microsoft.Xna.Framework.Color.Orange);
            }
            return true;
        }

        _nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;

        if (SessionVariant || _inPlaceSessionActive)
        {
            if (Timing.TotalTime - _sessionOpenedAt < MinSessionDurationBeforeClose)
            {
                return true;
            }

            if (SessionVariant)
            {
                CloseSessionInternal("manual close", true, character);
            }
            else
            {
                CloseSessionInPlace("manual close");
            }
        }
        else
        {
            OpenSessionInternal(character);
        }

        return true;
    }

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
                $"summary update id={item?.ID} db='{_resolvedDatabaseId}' sessionOpen={_cachedSessionOpen} " +
                $"page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} count={_cachedItemCount} locked={_cachedLocked}");
            UpdateClientPanelVisuals();
        }
#endif
    }

    public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
    {
        msg.WriteByte(_pendingClientAction);
        _pendingClientAction = (byte)TerminalPanelAction.None;
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        var action = (TerminalPanelAction)msg.ReadByte();
        if (action == TerminalPanelAction.None) { return; }
        if (!SessionVariant) { return; }

        Character actor = c?.Character;
        if (actor == null || actor.Removed || actor.IsDead)
        {
            return;
        }

        HandlePanelActionServer(action, actor);
    }

    public void ResolveDatabaseId(string normalized)
    {
        _resolvedDatabaseId = DatabaseStore.Normalize(normalized);
        DatabaseId = _resolvedDatabaseId;
    }

    public void ApplyStoreSnapshot(DatabaseData data)
    {
        if (!IsServerAuthority || data == null) { return; }

        ResolveDatabaseId(data.DatabaseId);
        DatabaseVersion = data.Version;
        SerializedDatabase = DatabaseStore.SerializeData(data);

        _cachedItemCount = data.ItemCount;
        _cachedLocked = DatabaseStore.IsLocked(_resolvedDatabaseId);
        _cachedSessionOpen = IsSessionActive();
        _cachedPageIndex = _cachedSessionOpen ? Math.Max(1, _sessionCurrentPageIndex + 1) : 0;
        _cachedPageTotal = _cachedSessionOpen ? Math.Max(1, _sessionPages.Count) : 0;
        _cachedRemainingPageItems = _cachedSessionOpen ? CountPendingPageItems() : 0;

        UpdateDescriptionLocal();
        TrySyncSummary();
    }

    public int CountTakeableForAutomation(Func<ItemData, bool> predicate)
    {
        if (!IsServerAuthority || predicate == null || !IsSessionActive())
        {
            return 0;
        }

        int count = 0;
        for (int pageIndex = 0; pageIndex < _sessionPages.Count; pageIndex++)
        {
            if (pageIndex == _sessionCurrentPageIndex) { continue; }
            var page = _sessionPages[pageIndex];
            if (page == null) { continue; }
            foreach (var entry in page)
            {
                if (entry == null || !predicate(entry)) { continue; }
                count += Math.Max(1, entry.StackSize);
            }
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
        if (!IsServerAuthority || predicate == null || amount <= 0 || !IsSessionActive())
        {
            return false;
        }

        if (CountTakeableForAutomation(predicate) < amount)
        {
            return false;
        }

        int remaining = amount;
        while (remaining > 0)
        {
            if (!TryFindAutomationCandidate(predicate, policy, out int pageIndex, out int entryIndex))
            {
                break;
            }

            var page = _sessionPages[pageIndex];
            var entry = page[entryIndex];

            if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
            {
                taken.Add(entry.Clone());
                page.RemoveAt(entryIndex);
                remaining--;
                continue;
            }

            int stackSize = Math.Max(1, entry.StackSize);
            if (stackSize <= remaining)
            {
                taken.Add(entry.Clone());
                remaining -= stackSize;
                page.RemoveAt(entryIndex);
                continue;
            }

            var part = ExtractStackPart(entry, remaining);
            if (part != null)
            {
                taken.Add(part);
            }
            remaining = 0;

            if (entry.StackSize <= 0)
            {
                page.RemoveAt(entryIndex);
            }
        }

        if (remaining > 0)
        {
            return false;
        }

        _sessionAutomationConsumedCount += CountFlatItems(taken);
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryFindAutomationCandidate(
        Func<ItemData, bool> predicate,
        DatabaseStore.TakePolicy policy,
        out int pageIndex,
        out int entryIndex)
    {
        pageIndex = -1;
        entryIndex = -1;
        if (predicate == null) { return false; }

        if (policy == DatabaseStore.TakePolicy.Fifo)
        {
            for (int p = 0; p < _sessionPages.Count; p++)
            {
                if (p == _sessionCurrentPageIndex) { continue; }
                var page = _sessionPages[p];
                if (page == null) { continue; }
                for (int i = 0; i < page.Count; i++)
                {
                    var entry = page[i];
                    if (entry == null || !predicate(entry)) { continue; }
                    pageIndex = p;
                    entryIndex = i;
                    return true;
                }
            }

            return false;
        }

        float bestCondition = 0f;
        int bestQuality = 0;
        const float eps = 0.0001f;

        for (int p = 0; p < _sessionPages.Count; p++)
        {
            if (p == _sessionCurrentPageIndex) { continue; }
            var page = _sessionPages[p];
            if (page == null) { continue; }

            for (int i = 0; i < page.Count; i++)
            {
                var entry = page[i];
                if (entry == null || !predicate(entry)) { continue; }

                if (pageIndex < 0)
                {
                    pageIndex = p;
                    entryIndex = i;
                    bestCondition = entry.Condition;
                    bestQuality = entry.Quality;
                    continue;
                }

                bool replace;
                if (policy == DatabaseStore.TakePolicy.HighestConditionFirst)
                {
                    replace = entry.Condition > bestCondition + eps ||
                              (Math.Abs(entry.Condition - bestCondition) <= eps && entry.Quality > bestQuality);
                }
                else
                {
                    replace = entry.Condition < bestCondition - eps ||
                              (Math.Abs(entry.Condition - bestCondition) <= eps && entry.Quality < bestQuality);
                }

                if (replace)
                {
                    pageIndex = p;
                    entryIndex = i;
                    bestCondition = entry.Condition;
                    bestQuality = entry.Quality;
                }
            }
        }

        return pageIndex >= 0 && entryIndex >= 0;
    }

    private void OpenSessionInternal(Character character, bool forceTakeover = false)
    {
        if (UseInPlaceSession && !SessionVariant)
        {
            OpenSessionInPlace(character, forceTakeover);
            return;
        }

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            if (!TryHandleLockedOpen(character, forceTakeover))
            {
                return;
            }

            if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
            {
                DebugConsole.NewMessage(
                    $"{Constants.LogPrefix} Taking over '{_resolvedDatabaseId}'... retry in a moment.",
                    Microsoft.Xna.Framework.Color.Orange);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return;
            }
        }

        ClearTakeoverPrompt();
        var allData = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);

        bool started = SpawnReplacement(
            OpenSessionIdentifier,
            character,
            null,
            (spawned, actor) =>
            {
                var terminal = spawned.GetComponent<DatabaseTerminalComponent>();
                if (terminal != null)
                {
                    terminal.ResolveDatabaseId(_resolvedDatabaseId);
                    terminal.DatabaseVersion = DatabaseVersion;
                    terminal.SerializedDatabase = SerializedDatabase;
                    terminal._sessionOwner = character;
                    terminal._sessionOpenedAt = Timing.TotalTime;
                    terminal._sessionAutomationConsumedCount = 0;
                    terminal.InitializePages(allData, actor);
                    terminal._nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
                    terminal._nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
                    terminal.UpdateSummaryFromStore();
                    terminal.UpdateDescriptionLocal();
                    terminal.TrySyncSummary(force: true);
                }

                DatabaseStore.TransferTerminalLock(_resolvedDatabaseId, item.ID, spawned.ID);
            },
            () =>
            {
                DatabaseStore.AppendItems(_resolvedDatabaseId, allData);
                DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
                DebugConsole.NewMessage($"{Constants.LogPrefix} Failed to open session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
            });

        if (!started)
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, allData);
            DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        }
    }

    private void OpenSessionInPlace(Character character, bool forceTakeover = false)
    {
        if (_inPlaceSessionActive) { return; }

        FlushIdleInventoryItems();

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            if (!TryHandleLockedOpen(character, forceTakeover))
            {
                return;
            }

            if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
            {
                DebugConsole.NewMessage(
                    $"{Constants.LogPrefix} Taking over '{_resolvedDatabaseId}'... retry in a moment.",
                    Microsoft.Xna.Framework.Color.Orange);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return;
            }
        }

        ClearTakeoverPrompt();
        var allData = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);
        _sessionOwner = character;
        _sessionOpenedAt = Timing.TotalTime;
        _inPlaceSessionActive = true;
        _sessionAutomationConsumedCount = 0;
        InitializePages(allData, character);
        _nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
        _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        DebugConsole.NewMessage($"{Constants.LogPrefix} In-place terminal session opened for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private void FlushIdleInventoryItems()
    {
        if (!UseInPlaceSession) { return; }
        if (IsSessionActive()) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return; }

        var leakedItems = inventory.AllItems
            .Where(it => it != null && !it.Removed)
            .Distinct()
            .ToList();
        if (leakedItems.Count == 0) { return; }

        Character carrier = item.ParentInventory?.Owner as Character;
        int returnedCount = 0;
        int droppedCount = 0;

        foreach (Item leaked in leakedItems)
        {
            inventory.RemoveItem(leaked);

            bool returned = false;
            if (item.ParentInventory != null)
            {
                returned = item.ParentInventory.TryPutItem(leaked, carrier, CharacterInventory.AnySlot);
            }

            if (returned)
            {
                returnedCount++;
            }
            else
            {
                leaked.Drop(carrier);
                droppedCount++;
            }
        }

        if (returnedCount > 0 || droppedCount > 0)
        {
            DebugConsole.NewMessage(
                $"{Constants.LogPrefix} Cleared {leakedItems.Count} idle terminal item(s) for '{_resolvedDatabaseId}' (returned={returnedCount}, dropped={droppedCount}).",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} FlushIdleInventory db='{_resolvedDatabaseId}' itemId={item?.ID} total={leakedItems.Count} returned={returnedCount} dropped={droppedCount}");
        }
    }

    private void CloseSessionInternal(string reason, bool convertToClosedItem, Character requester)
    {
        bool committed = false;
        Action commitSession = () =>
        {
            if (committed) { return; }
            committed = true;
            CommitSessionInventoryToStore();
        };

        if (convertToClosedItem)
        {
            Character owner = requester ?? _sessionOwner;
            bool converted = SpawnReplacement(
                ClosedTerminalIdentifier,
                owner,
                commitSession,
                (spawned, actor) =>
                {
                    var terminal = spawned.GetComponent<DatabaseTerminalComponent>();
                    if (terminal != null)
                    {
                        terminal.ResolveDatabaseId(_resolvedDatabaseId);
                        terminal.DatabaseVersion = DatabaseVersion;
                        terminal.SerializedDatabase = SerializedDatabase;
                        terminal._nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
                        terminal.UpdateSummaryFromStore();
                        terminal.UpdateDescriptionLocal();
                        terminal.TrySyncSummary(force: true);
                    }
                },
                () =>
                {
                    commitSession();
                    DebugConsole.NewMessage($"{Constants.LogPrefix} Failed to close session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
                });

            if (!converted)
            {
                commitSession();
                DebugConsole.NewMessage($"{Constants.LogPrefix} Failed to close session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
            }
        }
        else
        {
            commitSession();
        }

        DebugConsole.NewMessage($"{Constants.LogPrefix} Terminal session closed ({reason}) for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private bool HandlePanelActionServer(TerminalPanelAction action, Character actor)
    {
        if (action == TerminalPanelAction.OpenSession)
        {
            if (SessionVariant || _inPlaceSessionActive)
            {
                LogFixedTerminal($"open rejected: already session id={item?.ID}");
                return false;
            }
            if (!HasRequiredPower())
            {
                LogFixedTerminal($"open rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
                return false;
            }
            if (Timing.TotalTime < _nextPanelActionAllowedTime)
            {
                LogFixedTerminal($"open rejected: cooldown id={item?.ID}");
                return false;
            }

            LogFixedTerminal($"open request id={item?.ID} actor={actor?.Name ?? "none"} db='{_resolvedDatabaseId}'");
            OpenSessionInternal(actor);
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            return true;
        }
        if (action == TerminalPanelAction.ForceOpenSession)
        {
            if (SessionVariant || _inPlaceSessionActive)
            {
                LogFixedTerminal($"force rejected: already session id={item?.ID}");
                return false;
            }
            if (!HasRequiredPower())
            {
                LogFixedTerminal($"force rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
                return false;
            }
            if (Timing.TotalTime < _nextPanelActionAllowedTime)
            {
                LogFixedTerminal($"force rejected: cooldown id={item?.ID}");
                return false;
            }

            LogFixedTerminal($"force request id={item?.ID} actor={actor?.Name ?? "none"} db='{_resolvedDatabaseId}'");
            OpenSessionInternal(actor, forceTakeover: true);
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            return true;
        }

        if (!IsSessionActive()) { return false; }
        if (action != TerminalPanelAction.CloseSession && !HasRequiredPower()) { return false; }
        if (!CanCharacterControlSession(actor)) { return false; }
        if (Timing.TotalTime < _nextPanelActionAllowedTime) { return false; }

        bool applied;
        switch (action)
        {
            case TerminalPanelAction.PrevPage:
                applied = TryChangePage(-1, actor);
                break;
            case TerminalPanelAction.NextPage:
                applied = TryChangePage(1, actor);
                break;
            case TerminalPanelAction.PrevMatch:
                applied = TryJumpToMatch(-1, actor);
                break;
            case TerminalPanelAction.NextMatch:
                applied = TryJumpToMatch(1, actor);
                break;
            case TerminalPanelAction.CycleSortMode:
                applied = TryCycleSortMode(actor);
                break;
            case TerminalPanelAction.ToggleSortOrder:
                applied = TryToggleSortOrder(actor);
                break;
            case TerminalPanelAction.CompactItems:
                applied = TryCompactSessionItems(actor);
                break;
            case TerminalPanelAction.CloseSession:
                if (Timing.TotalTime - _sessionOpenedAt < MinSessionDurationBeforeClose)
                {
                    applied = false;
                    break;
                }
                if (SessionVariant)
                {
                    CloseSessionInternal("panel close", true, actor);
                }
                else
                {
                    CloseSessionInPlace("panel close");
                }
                applied = true;
                break;
            default:
                applied = false;
                break;
        }

        if (applied)
        {
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        }

        return applied;
    }

    private void ConsumeXmlActionRequest()
    {
        int request = XmlActionRequest;
        if (request == 0) { return; }
        XmlActionRequest = 0;

        TerminalPanelAction action = request switch
        {
            1 => TerminalPanelAction.PrevPage,
            2 => TerminalPanelAction.NextPage,
            3 => TerminalPanelAction.CloseSession,
            4 => TerminalPanelAction.OpenSession,
            5 => TerminalPanelAction.ForceOpenSession,
            6 => TerminalPanelAction.PrevMatch,
            7 => TerminalPanelAction.NextMatch,
            8 => TerminalPanelAction.CycleSortMode,
            9 => TerminalPanelAction.ToggleSortOrder,
            10 => TerminalPanelAction.CompactItems,
            _ => TerminalPanelAction.None
        };
        if (action == TerminalPanelAction.None) { return; }
        bool isOpenAction = action == TerminalPanelAction.OpenSession || action == TerminalPanelAction.ForceOpenSession;
        if (!isOpenAction && !IsSessionActive()) { return; }

        Character actor = _sessionOwner;
        if (actor != null && (actor.Removed || actor.IsDead))
        {
            DebugConsole.NewMessage(
                $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} for '{_resolvedDatabaseId}'.",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Panel", $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        bool applied = HandlePanelActionServer(action, actor);
        ModFileLog.Write(
            "Panel",
            $"{Constants.LogPrefix} XML action consumed: {action} applied={applied} db='{_resolvedDatabaseId}' page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} itemId={item?.ID}");
    }

    private bool TryHandleLockedOpen(Character actor, bool forceTakeover)
    {
        if (forceTakeover)
        {
            bool forced = DatabaseStore.TryForceCloseActiveSession(_resolvedDatabaseId, item.ID, actor);
            if (forced)
            {
                DebugConsole.NewMessage(
                    $"{Constants.LogPrefix} Force takeover requested for '{_resolvedDatabaseId}'.",
                    Microsoft.Xna.Framework.Color.Orange);
                ModFileLog.Write(
                    "Terminal",
                    $"{Constants.LogPrefix} force takeover requested db='{_resolvedDatabaseId}' by terminal={item?.ID} actor={actor?.Name ?? "none"}");
            }
            else
            {
                LogFixedTerminal($"force takeover failed db='{_resolvedDatabaseId}' terminal={item?.ID}");
            }
            return forced;
        }

        if (actor == null)
        {
            DebugConsole.NewMessage($"{Constants.LogPrefix} {_resolvedDatabaseId} is already locked by another terminal.", Microsoft.Xna.Framework.Color.OrangeRed);
            return false;
        }

        if (_pendingTakeoverRequesterId == actor.ID && Timing.TotalTime <= _pendingTakeoverUntil)
        {
            ClearTakeoverPrompt();
            bool forced = DatabaseStore.TryForceCloseActiveSession(_resolvedDatabaseId, item.ID, actor);
            if (forced)
            {
                DebugConsole.NewMessage(
                    $"{Constants.LogPrefix} Force takeover requested for '{_resolvedDatabaseId}'.",
                    Microsoft.Xna.Framework.Color.Orange);
                ModFileLog.Write(
                    "Terminal",
                    $"{Constants.LogPrefix} force takeover requested db='{_resolvedDatabaseId}' by terminal={item?.ID} actor={actor?.Name ?? "none"}");
                return true;
            }
        }

        _pendingTakeoverRequesterId = actor.ID;
        _pendingTakeoverUntil = Timing.TotalTime + TakeoverConfirmWindowSeconds;
        DebugConsole.NewMessage(
            $"{Constants.LogPrefix} {_resolvedDatabaseId} is locked. Use again within {TakeoverConfirmWindowSeconds:0.#}s to force takeover.",
            Microsoft.Xna.Framework.Color.Orange);
        return false;
    }

    private void LogFixedTerminal(string message)
    {
        if (!UseInPlaceSession) { return; }
        ModFileLog.Write("FixedTerminal", $"{Constants.LogPrefix} {message}");
    }

    private void ClearTakeoverPrompt()
    {
        _pendingTakeoverRequesterId = -1;
        _pendingTakeoverUntil = 0;
    }

    private bool TryChangePage(int delta, Character actor)
    {
        if (!IsSessionActive() || _sessionPages.Count <= 0) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        int targetIndex = _sessionCurrentPageIndex + delta;
        if (targetIndex < 0 || targetIndex >= _sessionPages.Count)
        {
            return false;
        }

        CaptureCurrentPageFromInventory();
        _sessionCurrentPageIndex = targetIndex;
        LoadCurrentPageIntoInventory(actor);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);

        DebugConsole.NewMessage(
            $"{Constants.LogPrefix} Page switched to {_sessionCurrentPageIndex + 1}/{Math.Max(1, _sessionPages.Count)} for '{_resolvedDatabaseId}'.",
            Microsoft.Xna.Framework.Color.LightGray);

        return true;
    }

    private bool TryJumpToMatch(int direction, Character actor)
    {
        if (!IsSessionActive() || _sessionPages.Count <= 0) { return false; }
        if (direction == 0) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        string keyword = (SearchKeyword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(keyword)) { return false; }

        int originalIndex = _sessionCurrentPageIndex;
        CaptureCurrentPageFromInventory();

        int pageCount = _sessionPages.Count;
        int start = Math.Max(0, _sessionCurrentPageIndex);
        int stepDir = direction > 0 ? 1 : -1;
        for (int step = 1; step <= pageCount; step++)
        {
            int index = WrapPageIndex(start + (step * stepDir), pageCount);
            if (PageMatchesKeyword(index, keyword))
            {
                _sessionCurrentPageIndex = index;
                LoadCurrentPageIntoInventory(actor);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return true;
            }
        }

        _sessionCurrentPageIndex = originalIndex;
        LoadCurrentPageIntoInventory(actor);
        return false;
    }

    private bool TryCycleSortMode(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }
        SortModeIndex = (NormalizeSortModeIndex(SortModeIndex) + 1) % 4;
        return TryRebuildPagesAfterResort(actor);
    }

    private bool TryToggleSortOrder(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }
        SortDescending = !SortDescending;
        return TryRebuildPagesAfterResort(actor);
    }

    private bool TryCompactSessionItems(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        CaptureCurrentPageFromInventory();
        var allEntries = FlattenAllPages();
        allEntries = DatabaseStore.CompactSnapshot(allEntries);
        allEntries.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
        InitializePages(allEntries, actor ?? _sessionOwner);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryRebuildPagesAfterResort(Character actor)
    {
        if (!CanRunPageMutatingAction()) { return false; }
        CaptureCurrentPageFromInventory();
        var allEntries = FlattenAllPages();
        allEntries.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
        InitializePages(allEntries, actor ?? _sessionOwner);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private static int NormalizeSortModeIndex(int raw)
    {
        if (raw < 0 || raw > 3) { return 0; }
        return raw;
    }

    private static int CompareBySortMode(ItemData left, ItemData right, TerminalSortMode mode, bool descending)
    {
        left ??= new ItemData();
        right ??= new ItemData();

        int cmp;
        switch (mode)
        {
            case TerminalSortMode.Condition:
                cmp = left.Condition.CompareTo(right.Condition);
                break;
            case TerminalSortMode.Quality:
                cmp = left.Quality.CompareTo(right.Quality);
                break;
            case TerminalSortMode.StackSize:
                cmp = Math.Max(1, left.StackSize).CompareTo(Math.Max(1, right.StackSize));
                break;
            default:
                cmp = string.Compare(left.Identifier ?? "", right.Identifier ?? "", StringComparison.OrdinalIgnoreCase);
                break;
        }

        if (cmp == 0)
        {
            cmp = string.Compare(left.Identifier ?? "", right.Identifier ?? "", StringComparison.OrdinalIgnoreCase);
        }

        return descending ? -cmp : cmp;
    }

    private bool PageMatchesKeyword(int pageIndex, string keyword)
    {
        if (pageIndex < 0 || pageIndex >= _sessionPages.Count) { return false; }
        var page = _sessionPages[pageIndex];
        if (page == null || page.Count == 0) { return false; }

        foreach (var entry in page)
        {
            string id = entry?.Identifier ?? "";
            if (id.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int WrapPageIndex(int index, int count)
    {
        if (count <= 0) { return 0; }
        int wrapped = index % count;
        if (wrapped < 0) { wrapped += count; }
        return wrapped;
    }

    private bool CanRunPageMutatingAction()
    {
        if (!IsSessionActive()) { return false; }
        if (_currentPageLoadedAt <= 0) { return true; }
        if (Timing.TotalTime - _currentPageLoadedAt >= PageActionSafetySeconds) { return true; }
        return false;
    }

    private bool CanCharacterControlSession(Character actor)
    {
        if (_sessionOwner == null) { return true; }
        if (actor == null || actor.Removed || actor.IsDead) { return false; }
        return _sessionOwner == actor;
    }

    private bool ShouldAutoClose()
    {
        if (!IsSessionActive()) { return false; }
        if (item.Removed) { return true; }
        if (!HasRequiredPower()) { return true; }
        if (_sessionOwner != null && (_sessionOwner.Removed || _sessionOwner.IsDead)) { return true; }
        if (_sessionOpenedAt > 0 && Timing.TotalTime - _sessionOpenedAt > Math.Max(5f, TerminalSessionTimeout)) { return true; }
        return false;
    }

    private void CloseSessionInPlace(string reason)
    {
        if (!_inPlaceSessionActive) { return; }

        CommitSessionInventoryToStore();
        _inPlaceSessionActive = false;
        _sessionOwner = null;
        _sessionOpenedAt = 0;

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        DebugConsole.NewMessage($"{Constants.LogPrefix} In-place terminal session closed ({reason}) for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private bool SpawnReplacement(string targetIdentifier, Character actor, Action beforeSwap, Action<Item, Character> onSpawned, Action onSpawnFailed)
    {
        if (string.IsNullOrWhiteSpace(targetIdentifier))
        {
            return false;
        }
        if (Entity.Spawner == null)
        {
            return false;
        }

        var prefab = ItemPrefab.FindByIdentifier(targetIdentifier.ToIdentifier()) as ItemPrefab;
        if (prefab == null)
        {
            DebugConsole.NewMessage($"{Constants.LogPrefix} ItemPrefab not found: {targetIdentifier}", Microsoft.Xna.Framework.Color.OrangeRed);
            return false;
        }

        var parentInventory = item.ParentInventory;
        int slotIndex = parentInventory != null ? parentInventory.FindIndex(item) : -1;

        bool wasSelected = false;
        if (actor != null)
        {
            wasSelected = actor.SelectedItem == item || actor.SelectedSecondaryItem == item;
        }

        var position = item.WorldPosition;
        var submarine = item.Submarine;

        Entity.Spawner.AddItemToSpawnQueue(prefab, position, submarine, onSpawned: spawned =>
        {
            if (spawned == null)
            {
                onSpawnFailed?.Invoke();
                return;
            }

            bool placed = false;
            if (parentInventory != null)
            {
                beforeSwap?.Invoke();
                parentInventory.RemoveItem(item);

                if (slotIndex >= 0)
                {
                    placed = parentInventory.TryPutItem(spawned, slotIndex, false, false, actor, true, true);
                }

                if (!placed)
                {
                    placed = parentInventory.TryPutItem(spawned, actor, CharacterInventory.AnySlot);
                }
            }

            if (!placed)
            {
                spawned.Drop(actor);
            }

            if (wasSelected && actor != null)
            {
                actor.SelectedItem = spawned;
            }

            if (parentInventory == null)
            {
                beforeSwap?.Invoke();
            }

            onSpawned?.Invoke(spawned, actor);
            SpawnService.RemoveItem(item);
        });

        return true;
    }

    private void InitializePages(List<ItemData> sourceItems, Character actor)
    {
        var inventory = GetTerminalInventory();
        BuildPagesBySlotUsage(sourceItems, inventory);
        LoadCurrentPageIntoInventory(actor);
    }

    private void BuildPagesFromCurrentInventory()
    {
        if (!SessionVariant) { return; }

        var inventory = GetTerminalInventory();
        var currentItems = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        BuildPagesBySlotUsage(currentItems, inventory);
    }

    private bool LoadCurrentPageIntoInventory(Character actor)
    {
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return false; }

        SpawnService.ClearInventory(inventory);

        _pageLoadGeneration++;
        int loadGeneration = _pageLoadGeneration;

        var pageItems = CloneItems(_sessionPages[_sessionCurrentPageIndex]);
        if (pageItems.Count > 0)
        {
            SpawnService.SpawnItemsIntoInventory(
                pageItems,
                inventory,
                actor ?? _sessionOwner,
                () => IsSessionActive() && loadGeneration == _pageLoadGeneration);
        }

        _currentPageLoadedAt = Timing.TotalTime;

        return true;
    }

    private void CaptureCurrentPageFromInventory()
    {
        if (!IsSessionActive()) { return; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return; }

        var serialized = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        _sessionPages[_sessionCurrentPageIndex] = CloneItems(serialized);
        SpawnService.ClearInventory(inventory);
    }

    private List<ItemData> FlattenAllPages()
    {
        var result = new List<ItemData>();
        foreach (var page in _sessionPages)
        {
            result.AddRange(CloneItems(page));
        }
        return result;
    }

    private int CountPendingPageItems()
    {
        if (!IsSessionActive() || _sessionPages.Count == 0 || _sessionCurrentPageIndex < 0)
        {
            return 0;
        }

        int count = 0;
        for (int i = 0; i < _sessionPages.Count; i++)
        {
            if (i == _sessionCurrentPageIndex) { continue; }
            count += _sessionPages[i]?.Sum(entry => Math.Max(1, entry?.StackSize ?? 1)) ?? 0;
        }
        return count;
    }

    private int ResolvePageSize(Inventory inventory)
    {
        int invCapacity = Math.Max(1, inventory?.Capacity ?? 1);
        return Math.Max(1, Math.Min(invCapacity, TerminalPageSize));
    }

    private void BuildPagesBySlotUsage(List<ItemData> sourceItems, Inventory inventory)
    {
        _sessionPages.Clear();
        _sessionCurrentPageIndex = -1;

        var source = CloneItems(sourceItems ?? new List<ItemData>());
        _sessionTotalEntryCount = source.Sum(entry => Math.Max(1, entry?.StackSize ?? 1));
        int pageSlotBudget = ResolvePageSize(inventory);

        if (source.Count == 0)
        {
            _sessionPages.Add(new List<ItemData>());
            _sessionCurrentPageIndex = 0;
            return;
        }

        var currentPage = new List<ItemData>();
        int usedSlots = 0;

        foreach (var entry in source.Where(e => e != null))
        {
            int neededSlots = EstimateSlotUsage(entry, inventory);
            if (currentPage.Count > 0 && usedSlots + neededSlots > pageSlotBudget)
            {
                _sessionPages.Add(currentPage);
                currentPage = new List<ItemData>();
                usedSlots = 0;
            }

            currentPage.Add(entry.Clone());
            usedSlots += neededSlots;

            if (usedSlots >= pageSlotBudget)
            {
                _sessionPages.Add(currentPage);
                currentPage = new List<ItemData>();
                usedSlots = 0;
            }
        }

        if (currentPage.Count > 0 || _sessionPages.Count == 0)
        {
            _sessionPages.Add(currentPage);
        }

        _sessionCurrentPageIndex = 0;
    }

    private int EstimateSlotUsage(ItemData entry, Inventory inventory)
    {
        if (entry == null) { return 1; }

        int stackCount = Math.Max(1, entry.StackSize);
        int stackPerSlot = ResolveStackPerSlot(entry, inventory);
        int slots = (int)Math.Ceiling((double)stackCount / Math.Max(1, stackPerSlot));
        return Math.Max(1, slots);
    }

    private int ResolveStackPerSlot(ItemData entry, Inventory inventory)
    {
        if (entry == null || inventory == null) { return 1; }

        var prefab = ItemPrefab.FindByIdentifier(entry.Identifier.ToIdentifier()) as ItemPrefab;
        if (prefab == null) { return 1; }

        int prefabLimit = Math.Max(1, prefab.GetMaxStackSize(inventory));
        int containerLimit = int.MaxValue;

        var container = item.GetComponent<ItemContainer>();
        if (container != null && inventory != null)
        {
            for (int slot = 0; slot < inventory.Capacity; slot++)
            {
                int slotLimit = Math.Max(1, container.GetMaxStackSize(slot));
                containerLimit = Math.Min(containerLimit, slotLimit);
            }
        }

        int resolved = containerLimit == int.MaxValue
            ? prefabLimit
            : Math.Min(prefabLimit, containerLimit);

        resolved = Math.Min(resolved, Inventory.MaxPossibleStackSize);
        return Math.Max(1, resolved);
    }

    private void CommitSessionInventoryToStore()
    {
        CaptureCurrentPageFromInventory();

        var remainingData = FlattenAllPages();
        if (!DatabaseStore.WriteBackFromTerminalContainer(_resolvedDatabaseId, remainingData, item.ID))
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, remainingData);
        }

        var inventory = GetTerminalInventory();
        if (inventory != null)
        {
            SpawnService.ClearInventory(inventory);
        }

        DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);

        if (_sessionAutomationConsumedCount > 0)
        {
            string line = $"{Constants.LogPrefix} Session '{_resolvedDatabaseId}' consumed {_sessionAutomationConsumedCount} item(s) by automation.";
            DebugConsole.NewMessage(line, Microsoft.Xna.Framework.Color.LightGray);
            ModFileLog.Write("Terminal", line);
        }

        _sessionPages.Clear();
        _sessionCurrentPageIndex = -1;
        _sessionTotalEntryCount = 0;
        _sessionAutomationConsumedCount = 0;
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
        item.CreateServerEvent(this, new SummaryEventData(
            _resolvedDatabaseId,
            _cachedItemCount,
            _cachedLocked,
            _cachedSessionOpen,
            _cachedPageIndex,
            _cachedPageTotal,
            _cachedRemainingPageItems));
#endif
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
        var powered = item.GetComponent<Powered>();
        if (powered == null) { return false; }
        return powered.Voltage >= Math.Max(0f, MinRequiredVoltage);
    }

    private static string T(string key, string fallback)
    {
        var value = TextManager.Get(key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static List<ItemData> CloneItems(List<ItemData> source)
    {
        var list = new List<ItemData>();
        if (source == null) { return list; }
        foreach (var itemData in source)
        {
            list.Add(itemData?.Clone());
        }
        return list;
    }

    private static int CountFlatItems(IEnumerable<ItemData> items)
    {
        int count = 0;
        if (items == null) { return 0; }
        foreach (var item in items)
        {
            if (item == null) { continue; }
            count += Math.Max(1, item.StackSize);
        }
        return count;
    }

    private static ItemData ExtractStackPart(ItemData source, int count)
    {
        if (source == null) { return null; }
        int take = Math.Max(1, count);
        var part = source.Clone();
        part.StackSize = take;
        part.StolenFlags = SliceBool(source.StolenFlags, 0, take, false);
        part.OriginalOutposts = SliceString(source.OriginalOutposts, 0, take, "");
        part.SlotIndices = SliceInt(source.SlotIndices, 0, take, -1);

        source.StackSize = Math.Max(0, source.StackSize - take);
        RemoveRangeSafe(source.StolenFlags, take);
        RemoveRangeSafe(source.OriginalOutposts, take);
        RemoveRangeSafe(source.SlotIndices, take);

        return part;
    }

    private static List<bool> SliceBool(List<bool> source, int start, int count, bool fallback)
    {
        var result = new List<bool>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            bool value = (source != null && idx >= 0 && idx < source.Count) ? source[idx] : fallback;
            result.Add(value);
        }
        return result;
    }

    private static List<string> SliceString(List<string> source, int start, int count, string fallback)
    {
        var result = new List<string>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            string value = (source != null && idx >= 0 && idx < source.Count) ? (source[idx] ?? fallback) : fallback;
            result.Add(value);
        }
        return result;
    }

    private static List<int> SliceInt(List<int> source, int start, int count, int fallback)
    {
        var result = new List<int>(Math.Max(0, count));
        for (int i = 0; i < count; i++)
        {
            int idx = start + i;
            int value = (source != null && idx >= 0 && idx < source.Count) ? source[idx] : fallback;
            result.Add(value);
        }
        return result;
    }

    private static void RemoveRangeSafe<T>(List<T> list, int count)
    {
        if (list == null || count <= 0) { return; }
        int remove = Math.Min(count, list.Count);
        if (remove <= 0) { return; }
        list.RemoveRange(0, remove);
    }

#if CLIENT
    private void LogPanelDebug(string message)
    {
        if (!EnablePanelDebugLog) { return; }
        string line = $"{Constants.LogPrefix} [Panel] {message}";
        DebugConsole.NewMessage(line, Color.LightSkyBlue);
        ModFileLog.Write("Panel", line);
    }

    private void SetPanelVisible(bool visible, string reason)
    {
        if (_panelFrame != null)
        {
            _panelFrame.Visible = visible;
            _panelFrame.Enabled = visible;
        }

        bool stateChanged = _panelLastVisible != visible;
        bool hideReasonChanged = !visible && _panelLastHiddenReason != reason;
        if ((stateChanged || hideReasonChanged) && Timing.TotalTime >= _nextPanelStateLogAllowedTime)
        {
            string rectInfo = _panelFrame == null
                ? "rect=(null)"
                : $"rect=({_panelFrame.Rect.X},{_panelFrame.Rect.Y},{_panelFrame.Rect.Width},{_panelFrame.Rect.Height})";
            LogPanelDebug(
                $"panel {(visible ? "show" : "hide")} id={item?.ID} reason={reason} sessionVariant={SessionVariant} " +
                $"summaryOpen={_cachedSessionOpen} page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} {rectInfo}");
            _nextPanelStateLogAllowedTime = Timing.TotalTime + PanelDebugLogCooldown;
        }

        _panelLastVisible = visible;
        if (!visible) { _panelLastHiddenReason = reason; }
    }

    private void EnsurePanelCreated()
    {
        if (_panelFrame != null) { return; }
        if (GUI.Canvas == null)
        {
            if (Timing.TotalTime >= _nextNoCanvasLogAllowedTime)
            {
                LogPanelDebug($"skip create id={item?.ID}: GUI.Canvas is null");
                _nextNoCanvasLogAllowedTime = Timing.TotalTime + 1.0;
            }
            return;
        }

        _panelFrame = new GUIFrame(new RectTransform(new Vector2(0.30f, 0.22f), GUI.Canvas, Anchor.TopLeft));
        _panelFrame.RectTransform.AbsoluteOffset = new Point(36, 92);
        _panelFrame.Visible = false;
        _panelFrame.Enabled = false;
        LogPanelDebug(
            $"panel created id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"rect=({_panelFrame.Rect.X},{_panelFrame.Rect.Y},{_panelFrame.Rect.Width},{_panelFrame.Rect.Height}) " +
            $"canvas=({GUI.Canvas.Rect.X},{GUI.Canvas.Rect.Y},{GUI.Canvas.Rect.Width},{GUI.Canvas.Rect.Height})");

        var content = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.90f), _panelFrame.RectTransform, Anchor.Center));

        _panelTitle = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.28f), content.RectTransform),
            T("dbiotest.panel.title", "Database Page Controls"),
            textAlignment: Alignment.Center);

        _panelPageInfo = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.20f), content.RectTransform),
            "",
            textAlignment: Alignment.Center);

        var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.42f), content.RectTransform), isHorizontal: true);

        _panelPrevButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.prev", "Prev"));
        _panelPrevButton.OnClicked = (_, __) =>
        {
            RequestPanelActionClient(TerminalPanelAction.PrevPage);
            return true;
        };

        _panelNextButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.next", "Next"));
        _panelNextButton.OnClicked = (_, __) =>
        {
            RequestPanelActionClient(TerminalPanelAction.NextPage);
            return true;
        };

        _panelCloseButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1f), row.RectTransform), T("dbiotest.panel.close", "Close"));
        _panelCloseButton.OnClicked = (_, __) =>
        {
            RequestPanelActionClient(TerminalPanelAction.CloseSession);
            return true;
        };

        UpdateClientPanelVisuals();
    }

    private void UpdateClientPanel()
    {
        if (item == null || item.Removed || !SessionVariant)
        {
            SetPanelVisible(false, "invalid item or not session variant");
            return;
        }

        Character controlled = Character.Controlled;
        if (controlled == null)
        {
            SetPanelVisible(false, "no controlled character");
            return;
        }

        bool shouldShow = controlled.SelectedItem == item ||
                          controlled.SelectedSecondaryItem == item ||
                          item.ParentInventory?.Owner == controlled ||
                          Vector2.DistanceSquared(controlled.WorldPosition, item.WorldPosition) <= PanelInteractionRange * PanelInteractionRange;
        if (!shouldShow)
        {
            SetPanelVisible(false, "outside visibility conditions");
            return;
        }

        EnsurePanelCreated();
        if (_panelFrame == null)
        {
            SetPanelVisible(false, "panel not created");
            return;
        }

        SetPanelVisible(true, "eligible");
        UpdateClientPanelVisuals();
    }

    private void UpdateClientPanelVisuals()
    {
        if (_panelFrame == null) { return; }
        if (_panelTitle != null)
        {
            _panelTitle.Text = $"{T("dbiotest.panel.title", "Database Page Controls")} [{_resolvedDatabaseId}]";
        }

        if (_panelPageInfo != null)
        {
            _panelPageInfo.Text = $"{T("dbiotest.terminal.page", "Page")}: {Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)}";
        }

        if (_panelPrevButton != null)
        {
            _panelPrevButton.Enabled = _cachedSessionOpen && _cachedPageIndex > 1;
        }

        if (_panelNextButton != null)
        {
            _panelNextButton.Enabled = _cachedSessionOpen && _cachedPageIndex < _cachedPageTotal;
        }

        if (_panelCloseButton != null)
        {
            _panelCloseButton.Enabled = _cachedSessionOpen;
        }
    }

    private void RequestPanelActionClient(TerminalPanelAction action)
    {
        if (action == TerminalPanelAction.None) { return; }
        if (Timing.TotalTime < _nextClientPanelActionAllowedTime)
        {
            LogPanelDebug($"action blocked by cooldown: {action}");
            return;
        }
        _nextClientPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        LogPanelDebug($"action requested: {action}");

        if (IsServerAuthority)
        {
            LogPanelDebug($"action handled locally as server: {action}");
            HandlePanelActionServer(action, Character.Controlled);
            return;
        }

        _pendingClientAction = (byte)action;
        LogPanelDebug($"action sent to server event: {action}");
        item.CreateClientEvent(this);
    }
#endif

    public override void RemoveComponentSpecific()
    {
#if CLIENT
        if (_panelFrame != null)
        {
            _panelFrame.Visible = false;
            _panelFrame.Enabled = false;
        }
        _panelFrame = null;
#endif

        if (IsServerAuthority)
        {
            if (SessionVariant)
            {
                CloseSessionInternal("terminal removed", false, _sessionOwner);
            }
            else if (_inPlaceSessionActive)
            {
                CloseSessionInPlace("terminal removed");
            }
            else
            {
                DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
            }

            DatabaseStore.UnregisterTerminal(this);
        }
    }

    public bool RequestForceCloseForTakeover(string reason, Character requester)
    {
        if (!IsServerAuthority) { return false; }

        if (SessionVariant)
        {
            CloseSessionInternal(reason, true, requester ?? _sessionOwner);
            return true;
        }

        if (_inPlaceSessionActive)
        {
            CloseSessionInPlace(reason);
            return true;
        }

        DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        return true;
    }
}
