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
        public string VariantKey { get; set; } = "";
        public bool HasContainedItems { get; set; }
        public int VariantQuality { get; set; }
        public float VariantCondition { get; set; } = 100f;
        public int CategoryInt { get; set; }
        public int Amount { get; set; }
        public int BestQuality { get; set; }
        public float AverageCondition { get; set; } = 100f;
    }

    [Editable, Serialize(Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = Constants.DefaultDatabaseId;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Require incoming electrical power to operate.")]
    public bool RequirePower { get; set; } = false;

    [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10f), Serialize(0.5f, IsPropertySaveable.Yes, description: "Minimum voltage required when RequirePower=true.")]
    public float MinRequiredVoltage { get; set; } = 0.5f;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Keep session in-place without swapping item identifier.")]
    public bool UseInPlaceSession { get; set; } = false;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Persisted shared database encoded string.")]
    public string SerializedDatabase { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Persisted database version.")]
    public int DatabaseVersion { get; set; } = 0;

    [Serialize(0, IsPropertySaveable.No, description: "XML button action request (1=Prev,2=Next,8=SortMode,9=SortOrder).")]
    public int XmlActionRequest { get; set; } = 0;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Search keyword for page jump by identifier.")]
    public string SearchKeyword { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Sort mode (0=Identifier,1=Condition,2=Quality,3=StackSize).")]
    public int SortModeIndex { get; set; } = 0;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Sort descending when true.")]
    public bool SortDescending { get; set; } = false;

    [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Enable C# client terminal panel overlay for testing.")]
    public bool EnableCsPanelOverlay { get; set; } = true;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "When true, terminal panel is read-only (view only, no take actions).")]
    public bool ReadOnlyView { get; set; } = false;

    [Editable(MinValueInt = 0, MaxValueInt = 16), Serialize(0, IsPropertySaveable.Yes, description: "Which ItemContainer index is used as terminal buffer inventory.")]
    public int TerminalBufferContainerIndex { get; set; } = 0;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Use compact left-side panel layout for fabricator-linked terminals.")]
    public bool CompactLeftPanel { get; set; } = false;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Filter panel entries by currently selected fabricator recipe requirements.")]
    public bool FollowFabricatorSelection { get; set; } = false;

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
    private readonly double _creationTime;
    private double _lastTickTime;

    private int _cachedItemCount;
    private bool _cachedLocked;
    private bool _cachedSessionOpen;
    private int _cachedPageIndex;
    private int _cachedPageTotal;
    private int _cachedRemainingPageItems;

    private bool _pendingSummarySync;
    private double _nextPendingSummarySyncAt;

    private string _lastSyncedDatabaseId;
    private int _lastSyncedItemCount = -1;
    private bool _lastSyncedLocked;
    private bool _lastSyncedSessionOpen;
    private int _lastSyncedPageIndex = -1;
    private int _lastSyncedPageTotal = -1;
    private int _lastSyncedRemainingPageItems = -1;
    private int _lastAppliedStoreVersion = -1;
    private ItemContainer[] _cachedTerminalItemContainers;
    private ItemContainer _cachedTerminalBufferContainer;
    private int _cachedTerminalBufferRequestedIndex = int.MinValue;
    private int _cachedTerminalBufferResolvedIndex = -1;

    private byte _pendingClientAction;
    private string _pendingClientTakeIdentifier = "";
    private int _pendingClientTakeCount = 1;
    private string _pendingClientTakeVariantKey = "";
    private int _luaTakeRequestNonce;
    private int _lastProcessedLuaTakeRequestNonce;
    private bool _processingLuaTakeRequest;
    private TerminalPanelAction _lastXmlAction = TerminalPanelAction.None;
    private double _lastXmlActionAt;
    private double _nextLuaBridgeDiagAt;
    private const double LuaBridgeDiagCooldownSeconds = 1.0;
    private const double XmlActionDebounceSeconds = 0.75;
    private const char LuaRowSeparator = (char)0x1E;
    private const char LuaFieldSeparator = (char)0x1F;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
    internal bool IsFixedTerminal => UseInPlaceSession;
    private const double PanelActionCooldownSeconds = 0.4;
    private const double PendingSummarySyncRetrySeconds = 0.25;
    private const double TerminalUpdatePerfWarnMs = 8.0;
    private const double TerminalUpdatePerfLogCooldownSeconds = 0.8;
    private double _nextUpdatePerfLogAt;
    private const double VirtualViewDiagCooldownSeconds = 0.85;
    private double _nextVirtualViewDiagAt;

    private static readonly PropertyInfo ItemFullyInitializedProperty =
        typeof(Item).GetProperty("FullyInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo ItemFullyInitializedField =
        typeof(Item).GetField("fullyInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

#if CLIENT
    private const double HandheldUseToggleCooldownSeconds = 0.75;
    private enum CellSizeMode : byte
    {
        Large = 0,
        Medium = 1,
        Small = 2
    }

    private static CellSizeMode _runtimeCellSizeMode = CellSizeMode.Medium;
    private GUIFrame _panelFrame;
    private GUILayoutGroup _panelMainLayout;
    private GUILayoutGroup _panelToolbarLayout;
    private GUILayoutGroup _panelContentLayout;
    private GUILayoutGroup _panelCategoryLayout;
    private GUIListBox _panelIconGridList;
    private GUITextBlock _panelTitle;
    private GUITextBlock _panelSummaryInfo;
    private GUITextBlock _panelStatusText;
    private GUITextBlock _panelBufferInfo;
    private GUIFrame _panelBufferFrame;
    private GUICustomComponent _panelBufferDrawer;
    private GUIButton _panelCloseButton;
    private GUITextBox _panelSearchBox;
    private GUIButton _panelSortButton;
    private GUIButton _panelCellSizeButton;
    private GUIButton _panelDbFillButton;
    private readonly List<GUIButton> _panelCategoryButtons = new List<GUIButton>();
    private readonly List<TerminalVirtualEntry> _panelEntrySnapshot = new List<TerminalVirtualEntry>();
    private readonly Dictionary<string, int> _recipeRequiredByIdentifier = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private CustomInterface _fixedXmlControlPanel;
    private double _nextPanelEntryRefreshAt;
    private double _nextClientPanelActionAllowedTime;
    private double _panelManualHideUntil;
    private string _currentSearchText = "";
    private int _selectedCategoryFlag = -1;
    private int _localSortMode;
    private bool _localSortDescending;
    private CellSizeMode _cellSizeMode = CellSizeMode.Medium;
    private string _selectedRecipeIdentifier = "";
    private int _selectedRecipeAmount = 1;
    private int _selectedRecipeRequirementsHash;
    private double _nextRecipeRequirementsRefreshAt;
    private bool _handheldPanelArmedByUse;
    private double _nextHandheldUseToggleAt;

    private string _lastIconGridRenderSignature = "";
    private const float PanelInteractionRange = 340f;
    // Panel trace logs were used to debug fixed-terminal flicker.
    // Keep them disabled by default to avoid high-frequency log noise in normal runs.
    private const bool EnablePanelDebugLog = false;
    private const double PanelEvalLogCooldown = 1.0;
    private const double PanelQueueLogCooldown = 2.0;
    private const double PanelEntryRefreshInterval = 0.25;
    private const double RecipeRequirementsRefreshInterval = 0.5;
    private const double PanelPerfDiagLogInterval = 1.0;
    private const double PanelPerfSlowLogThresholdMs = 6.0;
    private const double PanelPerfSlowLogCooldown = 0.75;
    private const double PanelFocusStickySeconds = 1.25;
    private static long _panelTraceSeq;
    private bool _panelLastVisible;
    private string _panelLastHiddenReason = "";
    private double _nextNoCanvasLogAllowedTime;
    private double _nextPanelQueueLogAllowedTime;
    private double _nextPanelQueueWarnLogAllowedTime;
    private string _lastPanelEvalSignature = "";
    private double _nextPanelEvalLogAllowedTime;
    private double _nextHandheldDiagLogAt;
    private static int _clientPanelFocusItemId = -1;
    private static double _clientPanelFocusUntil;
    private static string _clientPanelFocusReason = "";
    private double _nextPanelPerfDiagLogAt;
    private int _panelPerfDiagSamples;
    private int _panelPerfDiagGridRebuilds;
    private double _panelPerfDiagTotalMs;
    private double _panelPerfDiagMaxMs;
    private double _panelPerfDiagSnapshotMs;
    private double _panelPerfDiagRecipeMs;
    private double _panelPerfDiagVisualsMs;
    private double _panelPerfDiagBufferMs;
    private double _panelPerfDiagGridMs;
    private double _nextPanelPerfSlowLogAt;
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

#endif

    private bool _terminalBufferIndexFallbackWarned;
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
}
