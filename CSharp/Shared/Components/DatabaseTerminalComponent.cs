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
        CompactItems = 10,
        TakeByIdentifier = 11
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

    public sealed class TerminalVirtualEntry
    {
        public string Identifier { get; set; } = "";
        public string PrefabIdentifier { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Amount { get; set; }
        public int BestQuality { get; set; }
        public float AverageCondition { get; set; } = 100f;
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

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Persisted shared database encoded string.")]
    public string SerializedDatabase { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Persisted database version.")]
    public int DatabaseVersion { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "XML button action request (1=Prev,2=Next,3=Close,4=Open,5=ForceOpen,6=PrevMatch,7=NextMatch,8=SortMode,9=SortOrder,10=Compact).")]
    public int XmlActionRequest { get; set; } = 0;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Search keyword for page jump by identifier.")]
    public string SearchKeyword { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Sort mode (0=Identifier,1=Condition,2=Quality,3=StackSize).")]
    public int SortModeIndex { get; set; } = 0;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Sort descending when true.")]
    public bool SortDescending { get; set; } = false;

    [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Enable C# client terminal panel overlay for testing.")]
    public bool EnableCsPanelOverlay { get; set; } = true;

    [Serialize(false, IsPropertySaveable.No, description: "Lua B1: session open state.")]
    public bool LuaB1SessionOpen { get; set; } = false;

    [Serialize(Constants.DefaultDatabaseId, IsPropertySaveable.No, description: "Lua B1: normalized database id.")]
    public string LuaB1DatabaseId { get; set; } = Constants.DefaultDatabaseId;

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: snapshot serial.")]
    public int LuaB1RowsSerial { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: snapshot total entry rows.")]
    public int LuaB1TotalEntries { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: snapshot total item amount.")]
    public int LuaB1TotalAmount { get; set; } = 0;

    [Serialize("", IsPropertySaveable.No, description: "Lua B1: row payload (RS=0x1E, FS=0x1F).")]
    public string LuaB1RowsPayload { get; set; } = "";

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: client take request nonce.")]
    public int LuaTakeRequestNonce
    {
        get => _luaTakeRequestNonce;
        set
        {
            _luaTakeRequestNonce = value;
            TryProcessLuaTakeRequestFromBridge();
        }
    }

    [Serialize("", IsPropertySaveable.No, description: "Lua B1: client take request identifier.")]
    public string LuaTakeRequestIdentifier { get; set; } = "";

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: actor entity id for take request.")]
    public int LuaTakeRequestActorId { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "Lua B1: last processed take request nonce.")]
    public int LuaTakeResultNonce { get; set; } = 0;

    [Serialize("", IsPropertySaveable.No, description: "Lua B1: take request result code.")]
    public string LuaTakeResultCode { get; set; } = "";

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

    // Source-of-truth entries for this terminal session.
    private readonly List<ItemData> _sessionEntries = new List<ItemData>();
    // View indices into _sessionEntries used for sorting/paging.
    private readonly List<int> _viewIndices = new List<int>();
    // Materialized pages for UI/container load.
    private readonly List<List<ItemData>> _sessionPages = new List<List<ItemData>>();
    // For each materialized page, track corresponding source indices in _sessionEntries.
    private readonly List<List<int>> _sessionPageSourceIndices = new List<List<int>>();
    // identifier -> searchable text (identifier + localized names)
    private readonly Dictionary<string, string> _searchTextCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private int _sessionCurrentPageIndex = -1;
    private int _sessionTotalEntryCount;
    private int _pageLoadGeneration;
    private int _sessionAutomationConsumedCount;
    private bool _sessionWritebackCommitted;

    private int _cachedItemCount;
    private bool _cachedLocked;
    private bool _cachedSessionOpen;
    private int _cachedPageIndex;
    private int _cachedPageTotal;
    private int _cachedRemainingPageItems;

    private bool _pendingSummarySync;
    private double _nextPendingSummarySyncAt;

    private int _pendingPageFillCheckGeneration = -1;
    private double _pendingPageFillCheckAt;
    private int _pendingPageFillCheckExpectedCount;
    private int _pendingPageFillCheckPageIndex = -1;
    private int _pendingPageFillCheckRetries;
    private bool _currentPageFillVerified;

    private string _lastSyncedDatabaseId;
    private int _lastSyncedItemCount = -1;
    private bool _lastSyncedLocked;
    private bool _lastSyncedSessionOpen;
    private int _lastSyncedPageIndex = -1;
    private int _lastSyncedPageTotal = -1;
    private int _lastSyncedRemainingPageItems = -1;
    private int _lastAppliedStoreVersion = -1;

    private byte _pendingClientAction;
    private string _pendingClientTakeIdentifier = "";
    private int _luaTakeRequestNonce;
    private int _lastProcessedLuaTakeRequestNonce;
    private bool _processingLuaTakeRequest;
    private TerminalPanelAction _lastXmlAction = TerminalPanelAction.None;
    private double _lastXmlActionAt;
    private int _lastXmlRawRequest;
    private double _nextXmlRawLogAt;
    private double _nextLuaBridgeDiagAt;
    private const double LuaBridgeDiagCooldownSeconds = 1.0;
    private const double XmlActionDebounceSeconds = 0.75;
    private const double XmlRawLogCooldownSeconds = 0.2;
    private const char LuaRowSeparator = (char)0x1E;
    private const char LuaFieldSeparator = (char)0x1F;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
    private const double ToggleCooldownSeconds = 0.6;
    private const double MinSessionDurationBeforeClose = 0.9;
    private const double PanelActionCooldownSeconds = 0.4;
    private const double TakeoverConfirmWindowSeconds = 4.0;
    private const double PageActionSafetySeconds = 0.55;
    private const double PendingSummarySyncRetrySeconds = 0.25;
    private const double PageFillCheckDelaySeconds = 0.35;
    private const int MaxPageFillCheckRetries = 1;
    private const double TerminalUpdatePerfWarnMs = 8.0;
    private const double TerminalUpdatePerfLogCooldownSeconds = 0.8;
    private double _nextUpdatePerfLogAt;
    private const double TerminalUpdateStageWarnMs = 12.0;
    private const double TerminalUpdateStageLogCooldownSeconds = 0.8;
    private double _nextUpdateStageLogAt;
    private const double VirtualViewDiagCooldownSeconds = 0.85;
    private double _nextVirtualViewDiagAt;

    private static readonly PropertyInfo ItemFullyInitializedProperty =
        typeof(Item).GetProperty("FullyInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo ItemFullyInitializedField =
        typeof(Item).GetField("fullyInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

#if CLIENT
    private GUIFrame _panelFrame;
    private GUITextBlock _panelTitle;
    private GUITextBlock _panelPageInfo;
    private GUITextBlock _panelStatusText;
    private GUIButton _panelPrevButton;
    private GUIButton _panelNextButton;
    private GUIButton _panelCloseButton;
    private readonly List<GUIButton> _panelEntryButtons = new List<GUIButton>();
    private readonly List<TerminalVirtualEntry> _panelEntrySnapshot = new List<TerminalVirtualEntry>();
    private CustomInterface _fixedXmlControlPanel;
    private int _panelEntryPageIndex;
    private double _nextPanelEntryRefreshAt;
    private double _nextClientPanelActionAllowedTime;
    private const float PanelInteractionRange = 340f;
    private const bool EnablePanelDebugLog = true;
    private const double PanelDebugLogCooldown = 0.35;
    private const double PanelEvalLogCooldown = 0.25;
    private const double PanelQueueLogCooldown = 2.0;
    private const double PanelEntryRefreshInterval = 0.25;
    private const int PanelEntryButtonCount = 12;
    private const int PanelEntryColumns = 4;
    private const double PanelFocusStickySeconds = 1.25;
    private bool _panelLastVisible;
    private string _panelLastHiddenReason = "";
    private double _nextPanelStateLogAllowedTime;
    private double _nextNoCanvasLogAllowedTime;
    private double _nextPanelQueueLogAllowedTime;
    private double _nextPanelQueueWarnLogAllowedTime;
    private string _lastPanelEvalSignature = "";
    private double _nextPanelEvalLogAllowedTime;
    private static int _clientPanelFocusItemId = -1;
    private static double _clientPanelFocusUntil;
    private static string _clientPanelFocusReason = "";
    private static readonly MethodInfo AddToGuiUpdateListMethodWithOrder =
        typeof(GUIComponent).GetMethod(
            "AddToGUIUpdateList",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new[] { typeof(bool), typeof(int) },
            null);
    private static readonly MethodInfo AddToGuiUpdateListMethodNoArgs =
        typeof(GUIComponent).GetMethod(
            "AddToGUIUpdateList",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            Type.EmptyTypes,
            null);

    private void ClaimClientPanelFocus(string reason)
    {
        if (item == null || item.Removed) { return; }
        _clientPanelFocusItemId = item.ID;
        _clientPanelFocusUntil = Timing.TotalTime + PanelFocusStickySeconds;
        _clientPanelFocusReason = reason ?? "";
    }

    private void RefreshClientPanelFocusLeaseIfOwned()
    {
        if (item == null || item.Removed) { return; }
        if (_clientPanelFocusItemId != item.ID) { return; }
        _clientPanelFocusUntil = Timing.TotalTime + PanelFocusStickySeconds;
    }

    private void ReleaseClientPanelFocusIfOwned(string reason)
    {
        if (item == null || item.Removed) { return; }
        if (_clientPanelFocusItemId != item.ID) { return; }
        _clientPanelFocusItemId = -1;
        _clientPanelFocusUntil = 0;
        _clientPanelFocusReason = reason ?? "";
    }
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
            RefreshLuaB1BridgeState(force: true);
        }
        else
        {
            LoadSummaryFromSerialized();
            UpdateDescriptionLocal();
        }
    }

    public override void Update(float deltaTime, Camera cam)
    {
        long perfStartTicks = 0;
        if (ModFileLog.IsDebugEnabled)
        {
            perfStartTicks = Stopwatch.GetTimestamp();
        }

        double autoCloseMs = 0;
        double flushIdleMs = 0;
        double xmlActionMs = 0;
        double pageFillMs = 0;
        double pendingSyncMs = 0;
        double summaryMs = 0;
        double descMs = 0;
        double syncMs = 0;
        long stageStartTicks = perfStartTicks;

#if CLIENT
        UpdateFixedXmlControlPanelState();
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
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            autoCloseMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        // In-place terminals remain as one item. If someone inserts items while session is closed,
        // immediately return those items so they cannot be silently cleared on open.
        FlushIdleInventoryItems();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            flushIdleMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        ConsumeXmlActionRequest();
        TryProcessLuaTakeRequestFromBridge();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            xmlActionMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        TryRunPendingPageFillCheck();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            pageFillMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        if (_pendingSummarySync && Timing.TotalTime >= _nextPendingSummarySyncAt)
        {
            TrySyncSummary(force: true);
        }
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            pendingSyncMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        UpdateSummaryFromStore();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            summaryMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        UpdateDescriptionLocal();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            descMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        TrySyncSummary();
        RefreshLuaB1BridgeState();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            syncMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        if (perfStartTicks != 0)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - perfStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= TerminalUpdatePerfWarnMs && Timing.TotalTime >= _nextUpdatePerfLogAt)
            {
                _nextUpdatePerfLogAt = Timing.TotalTime + TerminalUpdatePerfLogCooldownSeconds;
                ModFileLog.Write(
                    "Perf",
                    $"{Constants.LogPrefix} TerminalUpdateSlow id={item?.ID} db='{_resolvedDatabaseId}' ms={elapsedMs:0.###} " +
                    $"session={IsSessionActive()} inPlace={_inPlaceSessionActive} owner={(_sessionOwner != null ? _sessionOwner.Name : "none")} " +
                    $"entries={_sessionEntries.Count} page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
                    $"pendingSummary={_pendingSummarySync}");
            }

            if (elapsedMs >= TerminalUpdateStageWarnMs && Timing.TotalTime >= _nextUpdateStageLogAt)
            {
                _nextUpdateStageLogAt = Timing.TotalTime + TerminalUpdateStageLogCooldownSeconds;
                ModFileLog.Write(
                    "Perf",
                    $"{Constants.LogPrefix} TerminalUpdateStage id={item?.ID} db='{_resolvedDatabaseId}' totalMs={elapsedMs:0.###} " +
                    $"autoCloseMs={autoCloseMs:0.###} flushIdleMs={flushIdleMs:0.###} xmlActionMs={xmlActionMs:0.###} " +
                    $"pageFillMs={pageFillMs:0.###} pendingSyncMs={pendingSyncMs:0.###} summaryMs={summaryMs:0.###} " +
                    $"descMs={descMs:0.###} syncMs={syncMs:0.###} session={IsSessionActive()} inPlace={_inPlaceSessionActive} " +
                    $"owner={(_sessionOwner != null ? _sessionOwner.Name : "none")}");
            }
        }
    }

    public override bool SecondaryUse(float deltaTime, Character character = null)
    {
        if (character == null) { return false; }
        if (Timing.TotalTime < _nextToggleAllowedTime) { return true; }
        if (Timing.TotalTime - _creationTime < 0.35) { return true; }
        if (!IsServerAuthority) { return false; }

        // In fixed in-place terminal mode, opening/closing should be driven by panel/UI actions only.
        // Blocking secondary-use toggle here avoids open/close oscillation while interacting with UI.
        if (UseInPlaceSession && EnableCsPanelOverlay)
        {
            return true;
        }

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
                ModFileLog.Write(
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
            ModFileLog.Write(
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
            ModFileLog.Write(
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

    public int CountTakeableForAutomation(Func<ItemData, bool> predicate)
    {
        if (!IsServerAuthority || predicate == null || !IsSessionActive())
        {
            return 0;
        }

        var protectedIndices = GetCurrentPageSourceIndexSet();
        int count = 0;
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (protectedIndices.Contains(i)) { continue; }
            var entry = _sessionEntries[i];
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
            if (!TryFindAutomationCandidate(predicate, policy, out int sourceIndex))
            {
                break;
            }

            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count)
            {
                break;
            }

            var entry = _sessionEntries[sourceIndex];
            if (entry == null)
            {
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
                continue;
            }

            if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
            {
                taken.Add(entry.Clone());
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
                remaining--;
                continue;
            }

            int stackSize = Math.Max(1, entry.StackSize);
            if (stackSize <= remaining)
            {
                taken.Add(entry.Clone());
                remaining -= stackSize;
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
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
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
            }
        }

        if (remaining > 0)
        {
            return false;
        }

        RebuildViewIndices();
        _sessionAutomationConsumedCount += CountFlatItems(taken);
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryFindAutomationCandidate(
        Func<ItemData, bool> predicate,
        DatabaseStore.TakePolicy policy,
        out int sourceIndex)
    {
        sourceIndex = -1;
        if (predicate == null) { return false; }
        var protectedIndices = GetCurrentPageSourceIndexSet();

        if (policy == DatabaseStore.TakePolicy.Fifo)
        {
            for (int i = 0; i < _sessionEntries.Count; i++)
            {
                if (protectedIndices.Contains(i)) { continue; }
                var entry = _sessionEntries[i];
                if (entry == null || !predicate(entry)) { continue; }
                sourceIndex = i;
                return true;
            }

            return false;
        }

        float bestCondition = 0f;
        int bestQuality = 0;
        const float eps = 0.0001f;

        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (protectedIndices.Contains(i)) { continue; }
            var entry = _sessionEntries[i];
            if (entry == null || !predicate(entry)) { continue; }

            if (sourceIndex < 0)
            {
                sourceIndex = i;
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
                sourceIndex = i;
                bestCondition = entry.Condition;
                bestQuality = entry.Quality;
            }
        }

        return sourceIndex >= 0;
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
                    terminal.RefreshLuaB1BridgeState(force: true);
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
        ModFileLog.Write(
            "FixedTerminal",
            $"{Constants.LogPrefix} in-place open committed id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"actor='{character?.Name ?? "none"}' force={forceTakeover} items={allData.Count} pageSize={TerminalPageSize}");
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
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
                        terminal.RefreshLuaB1BridgeState(force: true);
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

    private bool HandlePanelActionServer(TerminalPanelAction action, Character actor, string source = "unknown")
    {
        if (ModFileLog.IsDebugEnabled)
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} action dispatch source={source} action={action} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"actor='{actor?.Name ?? "none"}' owner='{_sessionOwner?.Name ?? "none"}' " +
                $"sessionActive={IsSessionActive()} cachedOpen={_cachedSessionOpen} inPlace={UseInPlaceSession} sessionVariant={SessionVariant}");
        }

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

        if (action != TerminalPanelAction.CloseSession && !HasRequiredPower())
        {
            LogFixedTerminal($"{action} rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
            return false;
        }
        if (Timing.TotalTime < _nextPanelActionAllowedTime)
        {
            LogFixedTerminal($"{action} rejected: cooldown id={item?.ID}");
            return false;
        }

        if (!IsSessionActive())
        {
            bool closedApplied = action switch
            {
                TerminalPanelAction.CycleSortMode => TryCycleSortModeWhenClosed(),
                TerminalPanelAction.ToggleSortOrder => TryToggleSortOrderWhenClosed(),
                TerminalPanelAction.CompactItems => TryCompactStoreWhenClosed(),
                _ => false
            };

            if (closedApplied)
            {
                _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            }
            else if (action == TerminalPanelAction.CloseSession)
            {
                LogFixedTerminal($"close rejected: no active session id={item?.ID}");
            }
            return closedApplied;
        }

        if (!CanCharacterControlSession(actor))
        {
            LogFixedTerminal(
                $"{action} rejected: owner mismatch id={item?.ID} owner={_sessionOwner?.Name ?? "none"} actor={actor?.Name ?? "none"}");
            return false;
        }

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
                if (!UseInPlaceSession && Timing.TotalTime - _sessionOpenedAt < MinSessionDurationBeforeClose)
                {
                    LogFixedTerminal(
                        $"close rejected: min duration id={item?.ID} openFor={(Timing.TotalTime - _sessionOpenedAt):0.###}");
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
        else if (action == TerminalPanelAction.CloseSession)
        {
            LogFixedTerminal($"close rejected: generic id={item?.ID} session={IsSessionActive()} owner={_sessionOwner?.Name ?? "none"}");
        }

        return applied;
    }

    private void ConsumeXmlActionRequest()
    {
        int request = XmlActionRequest;
        if (request == 0) { return; }
        XmlActionRequest = 0;

        if (ModFileLog.IsDebugEnabled && (request != _lastXmlRawRequest || Timing.TotalTime >= _nextXmlRawLogAt))
        {
            _lastXmlRawRequest = request;
            _nextXmlRawLogAt = Timing.TotalTime + XmlRawLogCooldownSeconds;
            Character controlled = Character.Controlled;
            int selectedId = controlled?.SelectedItem?.ID ?? -1;
            int selectedSecondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML raw request={request} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"sessionActive={IsSessionActive()} cachedOpen={_cachedSessionOpen} inPlace={UseInPlaceSession} sessionVariant={SessionVariant} " +
                $"owner='{_sessionOwner?.Name ?? "none"}' controlled='{controlled?.Name ?? "none"}' selected={selectedId}/{selectedSecondaryId}");
        }

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

        if (action == _lastXmlAction && Timing.TotalTime - _lastXmlActionAt < XmlActionDebounceSeconds)
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action ignored by debounce: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }
        _lastXmlAction = action;
        _lastXmlActionAt = Timing.TotalTime;

        if (EnableCsPanelOverlay && UseInPlaceSession)
        {
            // In mixed fixed-terminal mode, XML is used only for "Open/Force".
            // Keep "Close" on the C# panel path to avoid oscillation from repeated XML writes.
            bool sessionActive = IsSessionActive();
            bool allowXmlOpenWhenClosed =
                !sessionActive &&
                (action == TerminalPanelAction.OpenSession || action == TerminalPanelAction.ForceOpenSession);
            if (!allowXmlOpenWhenClosed)
            {
                ModFileLog.Write(
                    "Panel",
                    $"{Constants.LogPrefix} XML action ignored in CS panel in-place mode: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
                return;
            }
        }

        bool allowWhenClosed = action == TerminalPanelAction.OpenSession ||
                               action == TerminalPanelAction.ForceOpenSession ||
                               action == TerminalPanelAction.CycleSortMode ||
                               action == TerminalPanelAction.ToggleSortOrder ||
                               action == TerminalPanelAction.CompactItems;
        if (!allowWhenClosed && !IsSessionActive())
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action dropped while closed: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        Character actor = _sessionOwner;
        if (actor != null && (actor.Removed || actor.IsDead))
        {
            DebugConsole.NewMessage(
                $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} for '{_resolvedDatabaseId}'.",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Panel", $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        if (actor == null || actor.Removed || actor.IsDead)
        {
            actor = Character.Controlled;
        }
        if (actor == null || actor.Removed || actor.IsDead)
        {
            actor = item?.ParentInventory?.Owner as Character;
        }
        if (action != TerminalPanelAction.OpenSession &&
            action != TerminalPanelAction.ForceOpenSession &&
            _sessionOwner == null &&
            actor != null &&
            !actor.Removed &&
            !actor.IsDead)
        {
            _sessionOwner = actor;
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action adopted session owner '{actor.Name}' db='{_resolvedDatabaseId}' itemId={item?.ID}");
        }

        bool applied = HandlePanelActionServer(action, actor, "xml");
        ModFileLog.Write(
            "Panel",
            $"{Constants.LogPrefix} XML action consumed: {action} applied={applied} actor='{actor?.Name ?? "none"}' owner='{_sessionOwner?.Name ?? "none"}' " +
            $"db='{_resolvedDatabaseId}' page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} itemId={item?.ID}");
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
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        // Capture first, then fully rebuild page mapping from source to avoid stale page/index state.
        CaptureCurrentPageFromInventory();

        if (targetIndex < 0 || targetIndex >= _sessionPages.Count)
        {
            LoadCurrentPageIntoInventory(actor);
            return false;
        }

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

        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

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

    private bool TryCycleSortModeWhenClosed()
    {
        if (IsSessionActive()) { return false; }
        SortModeIndex = (NormalizeSortModeIndex(SortModeIndex) + 1) % 4;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryToggleSortOrderWhenClosed()
    {
        if (IsSessionActive()) { return false; }
        SortDescending = !SortDescending;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryCompactStoreWhenClosed()
    {
        if (IsSessionActive()) { return false; }

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            string blockedLine = $"{Constants.LogPrefix} Compact blocked: '{_resolvedDatabaseId}' is locked by another terminal session.";
            DebugConsole.NewMessage(blockedLine, Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Terminal", blockedLine);
            return false;
        }

        try
        {
            var allEntries = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);
            int beforeEntries = allEntries.Count;
            int beforeItems = CountFlatItems(allEntries);
            var beforeDiag = GetCompactionDiagnostics(allEntries);

            var compacted = DatabaseStore.CompactSnapshot(allEntries);
            compacted.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
            int afterEntries = compacted.Count;
            int afterItems = CountFlatItems(compacted);
            int mergedEntries = Math.Max(0, beforeEntries - afterEntries);

            if (!DatabaseStore.WriteBackFromTerminalContainer(_resolvedDatabaseId, compacted, item.ID))
            {
                DatabaseStore.AppendItems(_resolvedDatabaseId, compacted);
            }

            string compactLine =
                $"{Constants.LogPrefix} Closed compact db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"beforeEntries={beforeEntries} beforeItems={beforeItems} afterEntries={afterEntries} afterItems={afterItems} mergedEntries={mergedEntries} " +
                $"eligible={beforeDiag.eligible} blockedCond={beforeDiag.blockedCondition} blockedContained={beforeDiag.blockedContained} " +
                $"uniqueEligibleKeys={beforeDiag.uniqueKeys} potentialMergeEntries={beforeDiag.potentialMergeEntries} " +
                $"sort={GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} desc={SortDescending}";
            DebugConsole.NewMessage(
                compactLine,
                mergedEntries > 0 ? Microsoft.Xna.Framework.Color.LightGreen : Microsoft.Xna.Framework.Color.Yellow);
            ModFileLog.Write("Terminal", compactLine);

            UpdateSummaryFromStore();
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
            return true;
        }
        finally
        {
            DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        }
    }

    private bool TryCompactSessionItems(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        int preferredPage = _sessionCurrentPageIndex;
        CaptureCurrentPageFromInventory();
        var beforeData = FlattenAllPages();
        int beforeEntries = beforeData.Count;
        int beforeItems = CountFlatItems(beforeData);
        var beforeDiag = GetCompactionDiagnostics(beforeData);

        var compacted = DatabaseStore.CompactSnapshot(beforeData);
        compacted.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
        int afterEntries = compacted.Count;
        int afterItems = CountFlatItems(compacted);
        int mergedEntries = Math.Max(0, beforeEntries - afterEntries);

        BuildPagesBySlotUsage(compacted, inventory, preferredPage);
        LoadCurrentPageIntoInventory(actor ?? _sessionOwner);

        string compactLine =
            $"{Constants.LogPrefix} Session compact db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"beforeEntries={beforeEntries} beforeItems={beforeItems} afterEntries={afterEntries} afterItems={afterItems} mergedEntries={mergedEntries} " +
            $"eligible={beforeDiag.eligible} blockedCond={beforeDiag.blockedCondition} blockedContained={beforeDiag.blockedContained} " +
            $"uniqueEligibleKeys={beforeDiag.uniqueKeys} potentialMergeEntries={beforeDiag.potentialMergeEntries} " +
            $"sort={GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} desc={SortDescending} " +
            $"search='{(SearchKeyword ?? "").Trim()}' page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)}";
        DebugConsole.NewMessage(
            compactLine,
            mergedEntries > 0 ? Microsoft.Xna.Framework.Color.LightGreen : Microsoft.Xna.Framework.Color.Yellow);
        ModFileLog.Write("Terminal", compactLine);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryRebuildPagesAfterResort(Character actor)
    {
        if (!CanRunPageMutatingAction()) { return false; }
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        int preferredPage = _sessionCurrentPageIndex;
        CaptureCurrentPageFromInventory();
        // Sort mode/order only reorders the view; do not rewrite storage order here.
        BuildPagesBySlotUsage(_sessionEntries, inventory, preferredPage);
        LoadCurrentPageIntoInventory(actor ?? _sessionOwner);

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

    private bool EntryMatchesKeyword(ItemData entry, string keyword)
    {
        if (entry == null) { return false; }
        string needle = (keyword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(needle)) { return true; }

        string haystack = GetSearchTextForIdentifier(entry.Identifier);
        if (string.IsNullOrWhiteSpace(haystack)) { return false; }
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string GetSearchTextForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        if (_searchTextCache.TryGetValue(id, out string cached))
        {
            return cached ?? "";
        }

        var terms = new List<string>();
        AddSearchTerm(terms, id);

        string entityNameKey = $"entityname.{id}";
        AddSearchTerm(terms, TextManager.Get(entityNameKey)?.Value);
        AddSearchTerm(terms, TextManager.Get(entityNameKey.ToLowerInvariant())?.Value);

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab != null)
        {
            AddSearchTerm(terms, TryReadPrefabName(prefab, "Name"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "name"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "OriginalName"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "originalName"));
        }

        string combined = string.Join("\n", terms.Distinct(StringComparer.OrdinalIgnoreCase));
        _searchTextCache[id] = combined;
        return combined;
    }

    private static string TryReadPrefabName(ItemPrefab prefab, string memberName)
    {
        if (prefab == null || string.IsNullOrWhiteSpace(memberName)) { return ""; }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var prop = prefab.GetType().GetProperty(memberName, flags);
        if (prop != null)
        {
            return ExtractTextValue(prop.GetValue(prefab));
        }

        var field = prefab.GetType().GetField(memberName, flags);
        if (field != null)
        {
            return ExtractTextValue(field.GetValue(prefab));
        }

        return "";
    }

    private static string ExtractTextValue(object value)
    {
        if (value == null) { return ""; }
        if (value is string text) { return text; }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var valueProp = value.GetType().GetProperty("Value", flags);
        if (valueProp != null && valueProp.PropertyType == typeof(string))
        {
            return valueProp.GetValue(value) as string ?? "";
        }

        return value.ToString() ?? "";
    }

    private static void AddSearchTerm(List<string> terms, string value)
    {
        if (terms == null) { return; }
        string trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) { return; }
        terms.Add(trimmed);
    }

    private string ResolveDisplayNameForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab != null)
        {
            string name = TryReadPrefabName(prefab, "Name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            name = TryReadPrefabName(prefab, "OriginalName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }

        string entityName = TextManager.Get($"entityname.{id}")?.Value;
        if (!string.IsNullOrWhiteSpace(entityName))
        {
            return entityName.Trim();
        }

        return id;
    }

    private bool PageMatchesKeyword(int pageIndex, string keyword)
    {
        if (pageIndex < 0 || pageIndex >= _sessionPageSourceIndices.Count) { return false; }
        var sourceIndices = _sessionPageSourceIndices[pageIndex];
        if (sourceIndices == null || sourceIndices.Count == 0) { return false; }

        foreach (int sourceIndex in sourceIndices)
        {
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            var entry = _sessionEntries[sourceIndex];
            if (EntryMatchesKeyword(entry, keyword))
            {
                return true;
            }
        }

        return false;
    }

    private int FindPageIndexByIdentifier(string identifier)
    {
        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return -1; }

        for (int pageIndex = 0; pageIndex < _sessionPageSourceIndices.Count; pageIndex++)
        {
            var sourceIndices = _sessionPageSourceIndices[pageIndex];
            if (sourceIndices == null || sourceIndices.Count <= 0) { continue; }

            foreach (int sourceIndex in sourceIndices)
            {
                if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
                var entry = _sessionEntries[sourceIndex];
                if (entry == null) { continue; }
                if (string.Equals(entry.Identifier ?? "", wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return pageIndex;
                }
            }
        }

        return -1;
    }

    private bool HasAnyEmptyBufferSlot(Inventory inventory)
    {
        if (inventory == null) { return false; }
        for (int slot = 0; slot < inventory.Capacity; slot++)
        {
            if (inventory.GetItemAt(slot) == null)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryExtractOneVirtualItemData(string identifier, out ItemData extracted)
    {
        extracted = null;
        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return false; }

        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            var entry = _sessionEntries[i];
            if (entry == null) { continue; }
            if (!string.Equals(entry.Identifier ?? "", wanted, StringComparison.OrdinalIgnoreCase)) { continue; }

            if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
            {
                extracted = entry.Clone();
                extracted.StackSize = 1;
                _sessionEntries.RemoveAt(i);
                return true;
            }

            int stackSize = Math.Max(1, entry.StackSize);
            if (stackSize <= 1)
            {
                extracted = entry.Clone();
                extracted.StackSize = 1;
                _sessionEntries.RemoveAt(i);
                return true;
            }

            extracted = ExtractStackPart(entry, 1);
            if (entry.StackSize <= 0)
            {
                _sessionEntries.RemoveAt(i);
            }
            return extracted != null;
        }

        return false;
    }

    private Item FindInventoryItemByIdentifier(Inventory inventory, string identifier)
    {
        if (inventory == null) { return null; }
        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return null; }

        foreach (var candidate in inventory.AllItemsMod)
        {
            if (candidate == null || candidate.Removed || candidate.Prefab == null) { continue; }
            string id = candidate.Prefab.Identifier.Value ?? "";
            if (string.Equals(id, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
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

        string ownerName = _sessionOwner?.Name ?? "none";
        int sessionEntryCount = _sessionEntries.Count;
        int pendingPageItemCount = CountPendingPageItems();
        CommitSessionInventoryToStore();
        _inPlaceSessionActive = false;
        _sessionOwner = null;
        _sessionOpenedAt = 0;
        ModFileLog.Write(
            "FixedTerminal",
            $"{Constants.LogPrefix} in-place close committed id={item?.ID} db='{_resolvedDatabaseId}' reason='{reason}' " +
            $"owner='{ownerName}' sessionEntries={sessionEntryCount} pendingItems={pendingPageItemCount}");

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
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
        _sessionWritebackCommitted = false;
        _currentPageFillVerified = false;
        var inventory = GetTerminalInventory();
        BuildPagesBySlotUsage(sourceItems, inventory);
        if (inventory != null)
        {
            SpawnService.ClearInventory(inventory);
        }
    }

    private void BuildPagesFromCurrentInventory()
    {
        if (!SessionVariant) { return; }

        _sessionWritebackCommitted = false;
        _currentPageFillVerified = false;
        var inventory = GetTerminalInventory();
        var currentItems = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        BuildPagesBySlotUsage(currentItems, inventory);
    }

    private bool LoadCurrentPageIntoInventory(Character actor)
    {
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return false; }

        // In C# panel mode, keep the terminal buffer empty until user explicitly takes one entry.
        // This avoids auto-spawning full pages into the container.
        if (EnableCsPanelOverlay)
        {
            SpawnService.ClearInventory(inventory);
            _currentPageLoadedAt = 0;
            _pendingPageFillCheckGeneration = -1;
            _pendingPageFillCheckExpectedCount = 0;
            _pendingPageFillCheckPageIndex = -1;
            _pendingPageFillCheckRetries = 0;
            _currentPageFillVerified = true;
            return true;
        }

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
        _pendingPageFillCheckGeneration = loadGeneration;
        _pendingPageFillCheckAt = Timing.TotalTime + PageFillCheckDelaySeconds;
        _pendingPageFillCheckExpectedCount = CountFlatItems(pageItems);
        _pendingPageFillCheckPageIndex = _sessionCurrentPageIndex;
        _pendingPageFillCheckRetries = 0;
        _currentPageFillVerified = _pendingPageFillCheckExpectedCount <= 0;

        return true;
    }

    private void TryRunPendingPageFillCheck()
    {
        if (!IsSessionActive())
        {
            _pendingPageFillCheckGeneration = -1;
            _currentPageFillVerified = false;
            return;
        }

        if (_pendingPageFillCheckGeneration != _pageLoadGeneration) { return; }
        if (Timing.TotalTime < _pendingPageFillCheckAt) { return; }
        if (_pendingPageFillCheckPageIndex != _sessionCurrentPageIndex)
        {
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        var inventory = GetTerminalInventory();
        if (inventory == null)
        {
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        int actualCount = CountFlatItems(ItemSerializer.SerializeInventory(_sessionOwner, inventory));
        if (actualCount >= _pendingPageFillCheckExpectedCount)
        {
            _currentPageFillVerified = true;
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        if (_pendingPageFillCheckRetries >= MaxPageFillCheckRetries)
        {
            string warnLine =
                $"{Constants.LogPrefix} Page fill under target db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
                $"expected={_pendingPageFillCheckExpectedCount} actual={actualCount} retries={_pendingPageFillCheckRetries}";
            DebugConsole.NewMessage(warnLine, Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Terminal", warnLine);
            _currentPageFillVerified = false;
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        int nextRetry = _pendingPageFillCheckRetries + 1;
        string retryLine =
            $"{Constants.LogPrefix} Page fill retry db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
            $"expected={_pendingPageFillCheckExpectedCount} actual={actualCount} retry={nextRetry}";
        DebugConsole.NewMessage(retryLine, Microsoft.Xna.Framework.Color.Yellow);
        ModFileLog.Write("Terminal", retryLine);

        if (LoadCurrentPageIntoInventory(_sessionOwner))
        {
            _pendingPageFillCheckRetries = nextRetry;
        }
        else
        {
            _pendingPageFillCheckGeneration = -1;
        }
    }

    private void CaptureCurrentPageFromInventory()
    {
        CaptureCurrentPageFromInventory(clearInventoryAfterCapture: true);
    }

    private void SnapshotCurrentPageFromInventory()
    {
        CaptureCurrentPageFromInventory(clearInventoryAfterCapture: false);
    }

    private void CaptureCurrentPageFromInventory(bool clearInventoryAfterCapture)
    {
        if (!IsSessionActive()) { return; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return; }

        var serialized = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        int expectedCurrentPageCount = CountFlatItems(_sessionPages[_sessionCurrentPageIndex]);
        int actualCurrentPageCount = CountFlatItems(serialized);
        if (expectedCurrentPageCount > 0 && actualCurrentPageCount == 0 && !_currentPageFillVerified)
        {
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} Preserve source page on capture db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"expected={expectedCurrentPageCount} actual={actualCurrentPageCount} verified={_currentPageFillVerified}.");
            serialized = CloneItems(_sessionPages[_sessionCurrentPageIndex]);
        }

        var sourceIndices = _sessionPageSourceIndices.Count > _sessionCurrentPageIndex
            ? _sessionPageSourceIndices[_sessionCurrentPageIndex]
            : new List<int>();
        ReplaceSessionEntriesBySourceIndices(sourceIndices, serialized);

        if (clearInventoryAfterCapture)
        {
            SpawnService.ClearInventory(inventory);
        }

        _pendingPageFillCheckGeneration = -1;
        _currentPageFillVerified = false;

        // Keep current page index stable after source mutation.
        BuildPagesBySlotUsage(_sessionEntries, inventory, _sessionCurrentPageIndex);
    }

    private List<ItemData> FlattenAllPages()
    {
        return CloneItems(_sessionEntries);
    }

    private int CountPendingPageItems()
    {
        if (!IsSessionActive() || _sessionEntries.Count == 0)
        {
            return 0;
        }

        var protectedIndices = GetCurrentPageSourceIndexSet();
        int count = 0;
        foreach (int sourceIndex in _viewIndices)
        {
            if (protectedIndices.Contains(sourceIndex)) { continue; }
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            count += Math.Max(1, _sessionEntries[sourceIndex]?.StackSize ?? 1);
        }
        return count;
    }

    private int ResolvePageSize(Inventory inventory)
    {
        int invCapacity = Math.Max(1, inventory?.Capacity ?? 1);
        return Math.Max(1, Math.Min(invCapacity, TerminalPageSize));
    }

    private void BuildPagesBySlotUsage(List<ItemData> sourceItems, Inventory inventory, int preferredPageIndex = 0)
    {
        var clonedSource = CloneItems(sourceItems ?? new List<ItemData>());
        _sessionEntries.Clear();
        _sessionEntries.AddRange(clonedSource);
        RebuildViewIndices();
        MaterializePagesFromViewIndices(inventory, preferredPageIndex);
    }

    private void RebuildViewIndices()
    {
        _viewIndices.Clear();
        string keyword = (SearchKeyword ?? "").Trim();
        bool useKeyword = !string.IsNullOrWhiteSpace(keyword);
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            var entry = _sessionEntries[i];
            if (entry == null) { continue; }
            if (useKeyword && !EntryMatchesKeyword(entry, keyword)) { continue; }
            _viewIndices.Add(i);
        }

        _viewIndices.Sort((leftIndex, rightIndex) =>
            CompareBySortMode(
                leftIndex >= 0 && leftIndex < _sessionEntries.Count ? _sessionEntries[leftIndex] : null,
                rightIndex >= 0 && rightIndex < _sessionEntries.Count ? _sessionEntries[rightIndex] : null,
                (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex),
                SortDescending));
    }

    private void MaterializePagesFromViewIndices(Inventory inventory, int preferredPageIndex)
    {
        _sessionPages.Clear();
        _sessionPageSourceIndices.Clear();
        _sessionCurrentPageIndex = -1;
        _sessionTotalEntryCount = _sessionEntries.Sum(entry => Math.Max(1, entry?.StackSize ?? 1));
        int pageSlotBudget = ResolvePageSize(inventory);
        int pageEntryBudget = Math.Max(1, TerminalPageSize);

        if (_viewIndices.Count == 0)
        {
            _sessionPages.Add(new List<ItemData>());
            _sessionPageSourceIndices.Add(new List<int>());
            _sessionCurrentPageIndex = 0;
            return;
        }

        var currentPage = new List<ItemData>();
        var currentSourceIndices = new List<int>();
        int usedSlots = 0;
        int usedEntries = 0;

        foreach (int sourceIndex in _viewIndices)
        {
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            var entry = _sessionEntries[sourceIndex];
            if (entry == null) { continue; }

            int neededSlots = EstimateSlotUsage(entry, inventory);
            bool wouldOverflowSlots = currentPage.Count > 0 && usedSlots + neededSlots > pageSlotBudget;
            bool wouldOverflowEntries = usedEntries >= pageEntryBudget;
            if (wouldOverflowSlots || wouldOverflowEntries)
            {
                _sessionPages.Add(currentPage);
                _sessionPageSourceIndices.Add(currentSourceIndices);
                currentPage = new List<ItemData>();
                currentSourceIndices = new List<int>();
                usedSlots = 0;
                usedEntries = 0;
            }

            currentPage.Add(entry.Clone());
            currentSourceIndices.Add(sourceIndex);
            usedSlots += neededSlots;
            usedEntries++;

            if (usedSlots >= pageSlotBudget || usedEntries >= pageEntryBudget)
            {
                _sessionPages.Add(currentPage);
                _sessionPageSourceIndices.Add(currentSourceIndices);
                currentPage = new List<ItemData>();
                currentSourceIndices = new List<int>();
                usedSlots = 0;
                usedEntries = 0;
            }
        }

        if (currentPage.Count > 0 || _sessionPages.Count == 0)
        {
            _sessionPages.Add(currentPage);
            _sessionPageSourceIndices.Add(currentSourceIndices);
        }

        if (_sessionPages.Count <= 0)
        {
            _sessionPages.Add(new List<ItemData>());
            _sessionPageSourceIndices.Add(new List<int>());
        }

        if (preferredPageIndex < 0) { preferredPageIndex = 0; }
        if (preferredPageIndex >= _sessionPages.Count) { preferredPageIndex = _sessionPages.Count - 1; }
        _sessionCurrentPageIndex = preferredPageIndex;
    }

    private HashSet<int> GetCurrentPageSourceIndexSet()
    {
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPageSourceIndices.Count)
        {
            return new HashSet<int>();
        }

        return new HashSet<int>(_sessionPageSourceIndices[_sessionCurrentPageIndex].Where(idx => idx >= 0));
    }

    private void ShiftPageSourceIndicesAfterRemoval(int removedSourceIndex)
    {
        if (removedSourceIndex < 0) { return; }
        foreach (var page in _sessionPageSourceIndices)
        {
            if (page == null) { continue; }
            for (int i = 0; i < page.Count; i++)
            {
                int idx = page[i];
                if (idx == removedSourceIndex)
                {
                    // Should not happen because current page is protected, but keep safe.
                    page[i] = -1;
                }
                else if (idx > removedSourceIndex)
                {
                    page[i] = idx - 1;
                }
            }

            page.RemoveAll(idx => idx < 0);
        }
    }

    private void ReplaceSessionEntriesBySourceIndices(List<int> sourceIndices, List<ItemData> replacement)
    {
        var removeSet = new HashSet<int>((sourceIndices ?? new List<int>())
            .Where(idx => idx >= 0 && idx < _sessionEntries.Count));

        var replacementItems = CloneItems(replacement ?? new List<ItemData>());
        if (removeSet.Count == 0)
        {
            _sessionEntries.AddRange(replacementItems);
            return;
        }

        int insertAt = removeSet.Min();
        var rebuilt = new List<ItemData>(_sessionEntries.Count - removeSet.Count + replacementItems.Count);
        bool inserted = false;
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (!inserted && i == insertAt)
            {
                rebuilt.AddRange(CloneItems(replacementItems));
                inserted = true;
            }

            if (removeSet.Contains(i)) { continue; }
            rebuilt.Add(_sessionEntries[i]?.Clone());
        }

        if (!inserted)
        {
            rebuilt.AddRange(CloneItems(replacementItems));
        }

        _sessionEntries.Clear();
        _sessionEntries.AddRange(rebuilt);
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
        if (_sessionWritebackCommitted)
        {
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} Session writeback skipped db='{_resolvedDatabaseId}' terminal={item?.ID} reason='already committed'.");
            return;
        }

        var inventory = GetTerminalInventory();
        var bufferData = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        var remainingData = CloneItems(_sessionEntries);
        remainingData.AddRange(CloneItems(bufferData));

        int capturedTopLevelEntries = remainingData.Count;
        int capturedFlatItems = CountFlatItems(remainingData);

        var compactedData = DatabaseStore.CompactSnapshot(remainingData);
        int compactedTopLevelEntries = compactedData.Count;
        int compactedFlatItems = CountFlatItems(compactedData);

        bool wroteBackToLockedStore = DatabaseStore.WriteBackFromTerminalContainer(_resolvedDatabaseId, compactedData, item.ID);
        if (!wroteBackToLockedStore)
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, compactedData);
        }

        ModFileLog.Write(
            "Terminal",
            $"{Constants.LogPrefix} Session writeback db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"capturedEntries={capturedTopLevelEntries} capturedItems={capturedFlatItems} " +
            $"bufferEntries={bufferData.Count} bufferItems={CountFlatItems(bufferData)} " +
            $"compactedEntries={compactedTopLevelEntries} compactedItems={compactedFlatItems} " +
            $"writtenBackItems={compactedFlatItems} lockedWrite={wroteBackToLockedStore}");

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

        _sessionEntries.Clear();
        _viewIndices.Clear();
        _sessionPages.Clear();
        _sessionPageSourceIndices.Clear();
        _sessionCurrentPageIndex = -1;
        _sessionTotalEntryCount = 0;
        _sessionAutomationConsumedCount = 0;
        _pendingPageFillCheckGeneration = -1;
        _sessionWritebackCommitted = true;
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

    private static (int eligible, int blockedCondition, int blockedContained, int uniqueKeys, int potentialMergeEntries)
        GetCompactionDiagnostics(IEnumerable<ItemData> items)
    {
        int eligible = 0;
        int blockedCondition = 0;
        int blockedContained = 0;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (items != null)
        {
            foreach (var entry in items)
            {
                if (entry == null) { continue; }
                bool hasContained = entry.ContainedItems != null && entry.ContainedItems.Count > 0;
                if (hasContained)
                {
                    blockedContained++;
                    continue;
                }

                if (entry.Condition < 99.9f)
                {
                    blockedCondition++;
                    continue;
                }

                eligible++;
                string key = $"{entry.Identifier}_{entry.Quality}";
                keys.Add(key);
            }
        }

        int uniqueKeys = keys.Count;
        int potentialMergeEntries = Math.Max(0, eligible - uniqueKeys);
        return (eligible, blockedCondition, blockedContained, uniqueKeys, potentialMergeEntries);
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

    private void UpdateFixedXmlControlPanelState()
    {
        if (!EnableCsPanelOverlay || !UseInPlaceSession || SessionVariant || item == null || item.Removed)
        {
            return;
        }

        _fixedXmlControlPanel ??= item.GetComponent<CustomInterface>();
        if (_fixedXmlControlPanel == null)
        {
            return;
        }

        bool shouldEnableXmlPanel = !_cachedSessionOpen;
        if (_fixedXmlControlPanel.IsActive != shouldEnableXmlPanel)
        {
            _fixedXmlControlPanel.IsActive = shouldEnableXmlPanel;
            LogPanelDebug(
                $"fixed xml panel {(shouldEnableXmlPanel ? "enabled" : "disabled")} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"cachedOpen={_cachedSessionOpen} sessionActive={IsSessionActive()}");
        }
    }

    private void LogPanelEval(
        string phase,
        Character controlled,
        bool isSelected,
        bool isInControlledInventory,
        bool isNearby,
        bool shouldShow,
        float distance)
    {
        if (!EnablePanelDebugLog || !ModFileLog.IsDebugEnabled) { return; }

        int controlledId = controlled?.ID ?? -1;
        int selectedId = controlled?.SelectedItem?.ID ?? -1;
        int selectedSecondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
        int focusOwner = _clientPanelFocusItemId;
        double focusRemaining = Math.Max(0, _clientPanelFocusUntil - Timing.TotalTime);
        bool panelVisible = _panelFrame?.Visible ?? false;
        bool panelEnabled = _panelFrame?.Enabled ?? false;
        bool sessionActive = IsSessionActive();
        string signature =
            $"id={item?.ID}|{phase}|ctrl={controlledId}|sel={isSelected}|inv={isInControlledInventory}|near={isNearby}|show={shouldShow}|dist={distance:0.0}" +
            $"|cachedOpen={_cachedSessionOpen}|sessionActive={sessionActive}|inPlace={UseInPlaceSession}|sessionVariant={SessionVariant}" +
            $"|focus={focusOwner}|focusRemain={focusRemaining:0.00}|panel={panelVisible}/{panelEnabled}|sid={selectedId}|ssid={selectedSecondaryId}";
        if (signature == _lastPanelEvalSignature && Timing.TotalTime < _nextPanelEvalLogAllowedTime) { return; }

        _lastPanelEvalSignature = signature;
        _nextPanelEvalLogAllowedTime = Timing.TotalTime + PanelEvalLogCooldown;
        LogPanelDebug($"eval {signature}");
    }

    private static string TrimEntryLabel(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) { return ""; }
        string text = value.Trim();
        if (text.Length <= maxChars) { return text; }
        if (maxChars <= 1) { return text.Substring(0, 1); }
        return text.Substring(0, maxChars - 1) + "…";
    }

    private int GetPanelEntryPageCount()
    {
        if (_panelEntrySnapshot.Count <= 0) { return 1; }
        return Math.Max(1, (int)Math.Ceiling((double)_panelEntrySnapshot.Count / Math.Max(1, PanelEntryButtonCount)));
    }

    private int GetPanelEntryPageStartIndex()
    {
        return Math.Max(0, _panelEntryPageIndex) * Math.Max(1, PanelEntryButtonCount);
    }

    private List<TerminalVirtualEntry> ParsePanelEntriesFromLuaPayload()
    {
        var rows = new List<TerminalVirtualEntry>();
        string payload = LuaB1RowsPayload ?? "";
        if (string.IsNullOrEmpty(payload)) { return rows; }

        string[] rowParts = payload.Split(LuaRowSeparator);
        for (int i = 0; i < rowParts.Length; i++)
        {
            string row = rowParts[i] ?? "";
            if (string.IsNullOrWhiteSpace(row)) { continue; }
            string[] fields = row.Split(LuaFieldSeparator);
            if (fields.Length <= 0) { continue; }

            string id = (fields[0] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) { continue; }
            string prefabId = fields.Length > 1 ? (fields[1] ?? "").Trim() : id;
            string displayName = fields.Length > 2 ? (fields[2] ?? "").Trim() : id;

            int amount = 0;
            if (fields.Length > 3)
            {
                int.TryParse(fields[3] ?? "0", out amount);
            }
            int quality = 0;
            if (fields.Length > 4)
            {
                int.TryParse(fields[4] ?? "0", out quality);
            }
            float condition = 100f;
            if (fields.Length > 5)
            {
                float.TryParse(
                    fields[5] ?? "100",
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out condition);
            }

            rows.Add(new TerminalVirtualEntry
            {
                Identifier = id,
                PrefabIdentifier = string.IsNullOrWhiteSpace(prefabId) ? id : prefabId,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? id : displayName,
                Amount = Math.Max(0, amount),
                BestQuality = Math.Max(0, quality),
                AverageCondition = Math.Max(0f, condition)
            });
        }

        return rows;
    }

    private void RefreshPanelEntrySnapshot(bool force = false)
    {
        if (!force && Timing.TotalTime < _nextPanelEntryRefreshAt) { return; }
        _nextPanelEntryRefreshAt = Timing.TotalTime + PanelEntryRefreshInterval;

        _panelEntrySnapshot.Clear();
        List<TerminalVirtualEntry> source;
        if (IsServerAuthority)
        {
            source = GetVirtualViewSnapshot(refreshCurrentPage: false);
        }
        else
        {
            source = ParsePanelEntriesFromLuaPayload();
        }

        if (source != null && source.Count > 0)
        {
            _panelEntrySnapshot.AddRange(source
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Identifier))
                .OrderBy(entry => entry.DisplayName ?? entry.Identifier ?? "", StringComparer.OrdinalIgnoreCase));
        }

        int pageCount = GetPanelEntryPageCount();
        if (_panelEntryPageIndex >= pageCount)
        {
            _panelEntryPageIndex = Math.Max(0, pageCount - 1);
        }
    }

    private bool TryApplyEntryIconToButton(GUIButton button, string identifier)
    {
        if (button == null || string.IsNullOrWhiteSpace(identifier)) { return false; }

        var prefab = ItemPrefab.FindByIdentifier(identifier.ToIdentifier()) as ItemPrefab;
        Sprite icon = prefab?.InventoryIcon ?? prefab?.Sprite;
        if (icon == null) { return false; }

        var buttonType = button.GetType();
        string[] propertyCandidates = { "Sprite", "Icon", "Image", "OverrideSprite" };
        for (int i = 0; i < propertyCandidates.Length; i++)
        {
            string name = propertyCandidates[i];
            try
            {
                var prop = buttonType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanWrite) { continue; }
                if (!prop.PropertyType.IsAssignableFrom(icon.GetType())) { continue; }
                prop.SetValue(button, icon);
                return true;
            }
            catch
            {
                // Best-effort only.
            }
        }

        return false;
    }

    private void ApplyPanelEntriesToButtons()
    {
        if (_panelEntryButtons.Count <= 0) { return; }

        int start = GetPanelEntryPageStartIndex();
        int available = _panelEntrySnapshot.Count;
        for (int i = 0; i < _panelEntryButtons.Count; i++)
        {
            var button = _panelEntryButtons[i];
            if (button == null) { continue; }

            int idx = start + i;
            if (idx >= 0 && idx < available)
            {
                var entry = _panelEntrySnapshot[idx];
                string shortName = TrimEntryLabel(entry?.DisplayName ?? entry?.Identifier ?? "", 9);
                int amount = Math.Max(0, entry?.Amount ?? 0);
                string amountText = $"x{amount}";
                bool iconApplied = TryApplyEntryIconToButton(button, entry?.PrefabIdentifier ?? entry?.Identifier ?? "");

                button.Visible = true;
                button.Enabled = _cachedSessionOpen && amount > 0;
                button.Text = iconApplied ? amountText : $"{shortName} {amountText}";
                button.ToolTip =
                    $"{entry?.DisplayName ?? entry?.Identifier ?? ""}\n" +
                    $"{T("dbiotest.terminal.amount", "Amount")}: {amount}\n" +
                    $"{T("dbiotest.terminal.leftclicktake", "Left click to move 1 item to buffer.")}";
            }
            else
            {
                button.Visible = true;
                button.Enabled = false;
                button.Text = "";
                button.ToolTip = "";
            }
        }
    }

    private void HandlePanelEntryButtonClicked(int localIndex)
    {
        if (localIndex < 0 || localIndex >= _panelEntryButtons.Count) { return; }
        if (!_cachedSessionOpen) { return; }

        int idx = GetPanelEntryPageStartIndex() + localIndex;
        if (idx < 0 || idx >= _panelEntrySnapshot.Count) { return; }
        var entry = _panelEntrySnapshot[idx];
        string identifier = entry?.Identifier ?? "";
        if (string.IsNullOrWhiteSpace(identifier)) { return; }
        LogPanelDebug(
            $"entry click slot={localIndex} idx={idx} identifier='{identifier}' amount={Math.Max(0, entry?.Amount ?? 0)}");

        RequestPanelTakeByIdentifierClient(identifier);
    }

    private void RequestPanelTakeByIdentifierClient(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) { return; }
        if (Timing.TotalTime < _nextClientPanelActionAllowedTime)
        {
            LogPanelDebug($"take blocked by cooldown identifier='{identifier}'");
            return;
        }
        _nextClientPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;

        if (IsServerAuthority)
        {
            Character actor = Character.Controlled ?? _sessionOwner;
            string result = TryTakeOneByIdentifierFromVirtualSession(identifier, actor);
            if (string.IsNullOrEmpty(result))
            {
                LogPanelDebug($"take local success identifier='{identifier}'");
            }
            else
            {
                LogPanelDebug($"take local failed identifier='{identifier}' reason='{result}'");
            }
            RefreshPanelEntrySnapshot(force: true);
            return;
        }

        _pendingClientTakeIdentifier = identifier;
        _pendingClientAction = (byte)TerminalPanelAction.TakeByIdentifier;
        LogPanelDebug($"take sent to server identifier='{identifier}'");
        item.CreateClientEvent(this);
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
        LogPanelDebug(
            $"panel draw queue methods id={item?.ID} withOrder={(AddToGuiUpdateListMethodWithOrder != null)} noArgs={(AddToGuiUpdateListMethodNoArgs != null)}");

        var content = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.90f), _panelFrame.RectTransform, Anchor.Center));

        _panelTitle = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.17f), content.RectTransform),
            T("dbiotest.panel.title", "Database Terminal"),
            textAlignment: Alignment.Center);

        _panelPageInfo = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.10f), content.RectTransform),
            "",
            textAlignment: Alignment.Center);

        var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.18f), content.RectTransform), isHorizontal: true);

        _panelPrevButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.prev", "Prev"));
        _panelPrevButton.OnClicked = (_, __) =>
        {
            if (_panelEntryPageIndex > 0)
            {
                _panelEntryPageIndex--;
                RefreshPanelEntrySnapshot(force: true);
                UpdateClientPanelVisuals();
            }
            return true;
        };

        _panelNextButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.next", "Next"));
        _panelNextButton.OnClicked = (_, __) =>
        {
            int pageCount = GetPanelEntryPageCount();
            if (_panelEntryPageIndex + 1 < pageCount)
            {
                _panelEntryPageIndex++;
                RefreshPanelEntrySnapshot(force: true);
                UpdateClientPanelVisuals();
            }
            return true;
        };

        _panelCloseButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1f), row.RectTransform), T("dbiotest.panel.close", "Close"));
        _panelCloseButton.OnClicked = (_, __) =>
        {
            RequestPanelActionClient(TerminalPanelAction.CloseSession);
            return true;
        };

        int rowCount = Math.Max(1, PanelEntryButtonCount / Math.Max(1, PanelEntryColumns));
        var entryGrid = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.42f), content.RectTransform), isHorizontal: false);
        _panelEntryButtons.Clear();
        for (int r = 0; r < rowCount; r++)
        {
            var rowGroup = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 1f / rowCount), entryGrid.RectTransform),
                isHorizontal: true);
            for (int c = 0; c < PanelEntryColumns; c++)
            {
                int slot = r * PanelEntryColumns + c;
                var entryButton = new GUIButton(
                    new RectTransform(new Vector2(1f / PanelEntryColumns, 1f), rowGroup.RectTransform),
                    "");
                entryButton.OnClicked = (_, __) =>
                {
                    HandlePanelEntryButtonClicked(slot);
                    return true;
                };
                entryButton.Visible = true;
                entryButton.Enabled = false;
                _panelEntryButtons.Add(entryButton);
            }
        }

        _panelStatusText = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.13f), content.RectTransform),
            "",
            textAlignment: Alignment.Left);

        UpdateClientPanelVisuals();
    }

    private void QueuePanelForGuiUpdate()
    {
        if (_panelFrame == null) { return; }

        MethodInfo method = AddToGuiUpdateListMethodWithOrder ?? AddToGuiUpdateListMethodNoArgs;
        if (method == null)
        {
            if (Timing.TotalTime >= _nextPanelQueueWarnLogAllowedTime)
            {
                LogPanelDebug($"panel queue failed id={item?.ID}: AddToGUIUpdateList method not found");
                _nextPanelQueueWarnLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
            return;
        }

        try
        {
            if (ReferenceEquals(method, AddToGuiUpdateListMethodWithOrder))
            {
                method.Invoke(_panelFrame, new object[] { false, 1 });
            }
            else
            {
                method.Invoke(_panelFrame, null);
            }

            if (Timing.TotalTime >= _nextPanelQueueLogAllowedTime)
            {
                int argCount = method.GetParameters().Length;
                LogPanelDebug(
                    $"panel queued id={item?.ID} methodArgs={argCount} visible={_panelFrame.Visible} enabled={_panelFrame.Enabled}");
                _nextPanelQueueLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
        }
        catch (Exception ex)
        {
            if (Timing.TotalTime >= _nextPanelQueueWarnLogAllowedTime)
            {
                LogPanelDebug($"panel queue exception id={item?.ID}: {ex.GetType().Name}: {ex.Message}");
                _nextPanelQueueWarnLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
        }
    }

    private void UpdateClientPanel()
    {
        Character controlled = null;
        bool isSelected = false;
        bool isInControlledInventory = false;
        bool isNearby = false;
        bool shouldShow = false;
        float distance = -1f;

        bool panelCandidate = SessionVariant || UseInPlaceSession;
        if (item == null || item.Removed || !panelCandidate)
        {
            LogPanelEval("skip:candidate", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("invalid item or unsupported panel mode");
            SetPanelVisible(false, "invalid item or unsupported panel mode");
            return;
        }

        if (UseInPlaceSession && !SessionVariant && !_cachedSessionOpen)
        {
            controlled = Character.Controlled;
            LogPanelEval("hide:closed_inplace", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("in-place session closed");
            SetPanelVisible(false, "in-place session closed");
            return;
        }

        controlled = Character.Controlled;
        if (controlled == null)
        {
            LogPanelEval("hide:no_controlled", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("no controlled character");
            SetPanelVisible(false, "no controlled character");
            return;
        }

        isSelected = controlled.SelectedItem == item || controlled.SelectedSecondaryItem == item;
        isInControlledInventory = item.ParentInventory?.Owner == controlled;
        float distanceSq = Vector2.DistanceSquared(controlled.WorldPosition, item.WorldPosition);
        distance = (float)Math.Sqrt(Math.Max(0f, distanceSq));
        isNearby = distanceSq <= PanelInteractionRange * PanelInteractionRange;
        if (UseInPlaceSession)
        {
            shouldShow = _cachedSessionOpen && (isSelected || isNearby);
        }
        else if (SessionVariant)
        {
            shouldShow = _cachedSessionOpen && (isSelected || isInControlledInventory);
        }
        else
        {
            shouldShow = isSelected;
        }
        if (!shouldShow)
        {
            LogPanelEval("hide:outside", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("outside visibility conditions");
            SetPanelVisible(false, "outside visibility conditions");
            return;
        }

        if (isSelected)
        {
            ClaimClientPanelFocus("selected");
        }
        else
        {
            RefreshClientPanelFocusLeaseIfOwned();
            bool focusExpired = _clientPanelFocusItemId <= 0 || Timing.TotalTime > _clientPanelFocusUntil;
            if (focusExpired)
            {
                if (UseInPlaceSession && isNearby)
                {
                    ClaimClientPanelFocus("in-place-nearby");
                }
            }
        }

        if (_clientPanelFocusItemId > 0 && _clientPanelFocusItemId != item.ID && Timing.TotalTime <= _clientPanelFocusUntil)
        {
            LogPanelEval("hide:focus_owner", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, $"focus owner={_clientPanelFocusItemId} reason={_clientPanelFocusReason}");
            return;
        }

        if (_clientPanelFocusItemId <= 0 || Timing.TotalTime > _clientPanelFocusUntil)
        {
            ClaimClientPanelFocus("fallback-claim");
        }

        if (_clientPanelFocusItemId != item.ID)
        {
            LogPanelEval("hide:focus_mismatch", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, $"focus mismatch owner={_clientPanelFocusItemId}");
            return;
        }

        EnsurePanelCreated();
        if (_panelFrame == null)
        {
            LogPanelEval("hide:panel_not_created", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, "panel not created");
            return;
        }

        LogPanelEval("show:eligible", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
        SetPanelVisible(true, "eligible");
        QueuePanelForGuiUpdate();
        UpdateClientPanelVisuals();
    }

    private void UpdateClientPanelVisuals()
    {
        if (_panelFrame == null) { return; }
        RefreshPanelEntrySnapshot();

        int pageCount = GetPanelEntryPageCount();
        int safePage = Math.Max(0, Math.Min(_panelEntryPageIndex, Math.Max(0, pageCount - 1)));
        _panelEntryPageIndex = safePage;
        int totalAmount = 0;
        for (int i = 0; i < _panelEntrySnapshot.Count; i++)
        {
            totalAmount += Math.Max(0, _panelEntrySnapshot[i]?.Amount ?? 0);
        }

        if (_panelTitle != null)
        {
            _panelTitle.Text = $"{T("dbiotest.panel.title", "Database Terminal")} [{_resolvedDatabaseId}]";
        }

        if (_panelPageInfo != null)
        {
            _panelPageInfo.Text =
                $"{T("dbiotest.terminal.page", "Page")}: {safePage + 1}/{Math.Max(1, pageCount)} | " +
                $"{T("dbiotest.terminal.entries", "Entries")}: {_panelEntrySnapshot.Count} | " +
                $"{T("dbiotest.terminal.amount", "Amount")}: {totalAmount}";
        }

        if (_panelPrevButton != null)
        {
            _panelPrevButton.Enabled = _cachedSessionOpen && safePage > 0;
        }

        if (_panelNextButton != null)
        {
            _panelNextButton.Enabled = _cachedSessionOpen && (safePage + 1) < pageCount;
        }

        if (_panelCloseButton != null)
        {
            _panelCloseButton.Enabled = _cachedSessionOpen;
        }

        if (_panelStatusText != null)
        {
            _panelStatusText.Text = _cachedSessionOpen
                ? T("dbiotest.panel.takehint", "Click an icon to move 1 item to buffer.")
                : T("dbiotest.panel.closedhint", "Session closed. Use Open button on terminal.");
        }

        ApplyPanelEntriesToButtons();
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
        LogPanelDebug(
            $"action requested: {action} source=cs_panel id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"cachedOpen={_cachedSessionOpen} sessionActive={IsSessionActive()} inPlace={UseInPlaceSession} sessionVariant={SessionVariant}");

        if (IsServerAuthority)
        {
            LogPanelDebug($"action handled locally as server: {action} source=cs_panel");
            HandlePanelActionServer(action, Character.Controlled, "cs_panel_local");
            return;
        }

        _pendingClientAction = (byte)action;
        LogPanelDebug($"action sent to server event: {action} source=cs_panel");
        item.CreateClientEvent(this);
    }
#endif

    public override void RemoveComponentSpecific()
    {
#if CLIENT
        ReleaseClientPanelFocusIfOwned("component removed");
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

    public bool RequestForceCloseForTakeover(string reason, Character requester, bool convertToClosedItem = true)
    {
        if (!IsServerAuthority) { return false; }

        if (SessionVariant)
        {
            CloseSessionInternal(reason, convertToClosedItem, requester ?? _sessionOwner);
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
