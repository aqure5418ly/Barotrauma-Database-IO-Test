using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;

public partial class DatabaseAutoRestockerComponent : ItemComponent, IClientSerializable, IServerSerializable
{
    private enum RestockerAction : byte
    {
        None = 0,
        PrevSupply = 1,
        NextSupply = 2
    }

    private enum PreviewNetOp : byte
    {
        None = 0,
        Subscribe = 1,
        Unsubscribe = 2,
        Snapshot = 3
    }

    [Editable, Serialize(DatabaseIOTest.Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = DatabaseIOTest.Constants.DefaultDatabaseId;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Comma-separated supply identifiers.")]
    public string SupplyIdentifiers { get; set; } = "";

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Text filter for supply identifiers.")]
    public string SupplyFilter { get; set; } = "";

    [Serialize(0, IsPropertySaveable.Yes, description: "Selected supply index.")]
    public int SupplyIndex { get; set; } = 0;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Comma-separated target item identifiers (linked items).")]
    public string TargetIdentifiers { get; set; } = "";

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Comma-separated target item tags (linked items).")]
    public string TargetTags { get; set; } = "";

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Slot whitelist (comma-separated). Empty = all.")]
    public string SlotWhitelist { get; set; } = "";

    [Editable(MinValueInt = 1, MaxValueInt = 120), Serialize(3, IsPropertySaveable.Yes, description: "Empty ticks required before restock.")]
    public int EmptyTicksRequired { get; set; } = 3;

    [Editable(MinValueFloat = 0f, MaxValueFloat = 100f), Serialize(2.0f, IsPropertySaveable.Yes, description: "Condition threshold to treat slot as empty.")]
    public float LowConditionThreshold { get; set; } = 2.0f;

    [Editable(MinValueFloat = 0.05f, MaxValueFloat = 5f), Serialize(0.2f, IsPropertySaveable.Yes, description: "Polling interval in seconds.")]
    public float PollInterval { get; set; } = 0.2f;

    [Editable(MinValueFloat = 0.05f, MaxValueFloat = 10f), Serialize(0.3f, IsPropertySaveable.Yes, description: "Cooldown after a supply is spawned.")]
    public float SupplyCooldown { get; set; } = 0.3f;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Require incoming electrical power to operate.")]
    public bool RequirePower { get; set; } = false;

    [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10f), Serialize(0.5f, IsPropertySaveable.Yes, description: "Minimum voltage required when RequirePower=true.")]
    public float MinRequiredVoltage { get; set; } = 0.5f;

    [Serialize(0, IsPropertySaveable.No, description: "XML action request (1=Prev,2=Next).")]
    public int XmlActionRequest { get; set; } = 0;

    [Serialize("", IsPropertySaveable.No, description: "Selected supply identifier preview for client UI.")]
    public string SelectedSupplyIdentifierPreview { get; set; } = "";

    [Serialize(0, IsPropertySaveable.No, description: "Selected supply amount preview for client UI.")]
    public int SelectedSupplyAmountPreview { get; set; } = 0;

    private string _resolvedDatabaseId = DatabaseIOTest.Constants.DefaultDatabaseId;
    private double _lastPollTime;
    private double _lastDescriptionUpdateTime;
    private double _nextNoPowerLogTime;
    private double _nextPreviewSubscriberSweepTime;
    private string _lastPreviewBroadcastIdentifier = "";
    private int _lastPreviewBroadcastAmount = -1;

    private readonly Dictionary<Client, double> _previewSubscribers = new Dictionary<Client, double>();

    private string _lastSupplyIdentifiers = "";
    private readonly List<string> _supplyList = new List<string>();

    private string _lastSupplyFilter = "";
    private readonly List<string> _filteredSupplyList = new List<string>();
    private readonly Dictionary<string, string> _supplySearchTextCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private int _lastIdentifierSnapshotVersion = -1;
    private List<string> _cachedDatabaseIdentifiers = new List<string>();
    private string _storeWatcherId = "";
    private string _watchingDatabaseId = "";

    private string _lastTargetIdentifiers = "";
    private readonly List<string> _targetIdentifiers = new List<string>();

    private string _lastTargetTags = "";
    private readonly List<string> _targetTags = new List<string>();

    private string _lastSlotWhitelist = "";
    private readonly List<int> _slotWhitelist = new List<int>();

    private readonly Dictionary<ulong, int> _slotEmptyTicks = new Dictionary<ulong, int>();

    private static readonly Dictionary<ulong, double> SlotLockUntil = new Dictionary<ulong, double>();

    private const double DescriptionUpdateInterval = 0.75;
    private const double PreviewSubscriberTimeoutSeconds = 2.5;
    private const double PreviewSubscriberSweepInterval = 0.5;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

#if CLIENT
    private GUIFrame _previewFrame;
    private GUIFrame _previewIconFrame;
    private GUITextBlock _previewAmountText;
    private bool _previewVisible;
    private string _previewLastIdentifier = "__init__";
    private int _previewLastAmount = int.MinValue;
    private double _nextPreviewQueueWarnLogTime;
    private byte _pendingPreviewClientOp;
    private bool _previewSubscribed;
    private double _nextPreviewSubscribeHeartbeatAt;

    private const double PreviewSubscribeHeartbeatSeconds = 1.2;

    private static readonly MethodInfo PreviewAddToGuiUpdateListMethodWithOrder = typeof(GUIComponent).GetMethod(
        "AddToGUIUpdateList",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        new[] { typeof(bool), typeof(int) },
        null);

    private static readonly MethodInfo PreviewAddToGuiUpdateListMethodNoArgs = typeof(GUIComponent).GetMethod(
        "AddToGUIUpdateList",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        null,
        Type.EmptyTypes,
        null);
#endif

    private readonly struct TargetInventory
    {
        public readonly Inventory Inventory;
        public readonly int OwnerId;
        public readonly int ContainerIndex;

        public TargetInventory(Inventory inventory, int ownerId, int containerIndex)
        {
            Inventory = inventory;
            OwnerId = ownerId;
            ContainerIndex = containerIndex;
        }
    }

    public DatabaseAutoRestockerComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
    }

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);
        RefreshParsedConfig();
        if (IsServerAuthority)
        {
            EnsureStoreWatcherRegistered();
            RefreshSelectedSupplyPreviewData();
            UpdateDescription();
        }
    }

    public override void RemoveComponentSpecific()
    {
        if (IsServerAuthority &&
            !string.IsNullOrWhiteSpace(_watchingDatabaseId) &&
            !string.IsNullOrWhiteSpace(_storeWatcherId))
        {
            DatabaseStore.Unwatch(_watchingDatabaseId, _storeWatcherId);
        }

        if (IsServerAuthority)
        {
            _previewSubscribers.Clear();
        }
#if CLIENT
        _previewSubscribed = false;
        _pendingPreviewClientOp = (byte)PreviewNetOp.None;
#endif
    }

    public override void Update(float deltaTime, Camera cam)
    {
        if (!IsServerAuthority)
        {
#if CLIENT
            UpdateClientPreviewSubscriptionPassive();
#endif
            return;
        }

        RefreshParsedConfig();
        ConsumeXmlActionRequest();

        double now = Timing.TotalTime;
        SweepPreviewSubscribers(now);

        if (now - _lastPollTime >= Math.Max(PollInterval, 0.05f))
        {
            _lastPollTime = now;
            TryRestock(now);
        }

        if (now - _lastDescriptionUpdateTime >= DescriptionUpdateInterval)
        {
            _lastDescriptionUpdateTime = now;
            RefreshSelectedSupplyPreviewData();
            UpdateDescription();
        }
    }

    private void ConsumeXmlActionRequest()
    {
        int request = XmlActionRequest;
        if (request == 0) { return; }
        XmlActionRequest = 0;

        var action = request switch
        {
            1 => RestockerAction.PrevSupply,
            2 => RestockerAction.NextSupply,
            _ => RestockerAction.None
        };

        if (action == RestockerAction.None) { return; }
        var list = GetFilteredSupplyList();
        if (list.Count == 0) { return; }

        if (action == RestockerAction.PrevSupply)
        {
            SupplyIndex--;
        }
        else if (action == RestockerAction.NextSupply)
        {
            SupplyIndex++;
        }

        ClampSupplyIndex(list.Count);
        RefreshSelectedSupplyPreviewData();
        UpdateDescription();
    }

    private void RefreshParsedConfig()
    {
        bool supplyListChanged = false;
        if (!string.Equals(_lastSupplyIdentifiers, SupplyIdentifiers ?? "", StringComparison.Ordinal))
        {
            _lastSupplyIdentifiers = SupplyIdentifiers ?? "";
            _supplyList.Clear();
            foreach (var entry in SplitTokens(_lastSupplyIdentifiers))
            {
                if (string.IsNullOrWhiteSpace(entry)) { continue; }
                _supplyList.Add(entry.Trim());
            }
            supplyListChanged = true;
        }

        bool supplyFilterChanged = false;
        string filterValue = SupplyFilter ?? "";
        if (!string.Equals(_lastSupplyFilter, filterValue, StringComparison.Ordinal))
        {
            _lastSupplyFilter = filterValue;
            supplyFilterChanged = true;
        }

        bool baseListChanged = supplyListChanged;
        if (_supplyList.Count == 0)
        {
            EnsureStoreWatcherRegistered();
        }

        if (supplyListChanged || supplyFilterChanged || baseListChanged)
        {
            if (supplyListChanged || baseListChanged)
            {
                _supplySearchTextCache.Clear();
            }
            RebuildFilteredSupplyList();
            RefreshSelectedSupplyPreviewData();
        }

        if (!string.Equals(_lastTargetIdentifiers, TargetIdentifiers ?? "", StringComparison.Ordinal))
        {
            _lastTargetIdentifiers = TargetIdentifiers ?? "";
            _targetIdentifiers.Clear();
            foreach (var entry in SplitTokens(_lastTargetIdentifiers))
            {
                if (string.IsNullOrWhiteSpace(entry)) { continue; }
                _targetIdentifiers.Add(entry.Trim().ToLowerInvariant());
            }
        }

        if (!string.Equals(_lastTargetTags, TargetTags ?? "", StringComparison.Ordinal))
        {
            _lastTargetTags = TargetTags ?? "";
            _targetTags.Clear();
            foreach (var entry in SplitTokens(_lastTargetTags))
            {
                if (string.IsNullOrWhiteSpace(entry)) { continue; }
                _targetTags.Add(entry.Trim().ToLowerInvariant());
            }
        }

        if (!string.Equals(_lastSlotWhitelist, SlotWhitelist ?? "", StringComparison.Ordinal))
        {
            _lastSlotWhitelist = SlotWhitelist ?? "";
            _slotWhitelist.Clear();
            foreach (var entry in SplitTokens(_lastSlotWhitelist))
            {
                if (int.TryParse(entry, out int slot) && slot >= 0)
                {
                    _slotWhitelist.Add(slot);
                }
            }
            _slotWhitelist.Sort();
        }
    }

    private void ClampSupplyIndex(int count)
    {
        if (count <= 0)
        {
            SupplyIndex = 0;
            return;
        }

        if (SupplyIndex < 0)
        {
            SupplyIndex = count - 1;
        }
        else if (SupplyIndex >= count)
        {
            SupplyIndex = 0;
        }
    }

    private string GetSelectedSupplyIdentifier()
    {
        var list = GetFilteredSupplyList();
        if (list.Count == 0) { return ""; }
        int index = Math.Max(0, Math.Min(list.Count - 1, SupplyIndex));
        return list[index];
    }

    private void RefreshSelectedSupplyPreviewData()
    {
        if (!IsServerAuthority) { return; }

        string selectedIdentifier = (GetSelectedSupplyIdentifier() ?? "").Trim();
        int selectedAmount = 0;
        if (!string.IsNullOrWhiteSpace(selectedIdentifier))
        {
            selectedAmount = DatabaseStore.GetIdentifierAmount(_resolvedDatabaseId, selectedIdentifier);
        }

        selectedAmount = Math.Max(0, selectedAmount);
        if (string.Equals(SelectedSupplyIdentifierPreview ?? "", selectedIdentifier, StringComparison.OrdinalIgnoreCase) &&
            SelectedSupplyAmountPreview == selectedAmount)
        {
            return;
        }

        SelectedSupplyIdentifierPreview = selectedIdentifier;
        SelectedSupplyAmountPreview = selectedAmount;
        TrySyncPreviewSnapshot(force: false);
    }

    private void SweepPreviewSubscribers(double now)
    {
        if (!IsServerAuthority) { return; }
        if (now < _nextPreviewSubscriberSweepTime) { return; }
        _nextPreviewSubscriberSweepTime = now + PreviewSubscriberSweepInterval;
        if (_previewSubscribers.Count <= 0) { return; }

        var stale = new List<Client>();
        foreach (var pair in _previewSubscribers)
        {
            if (pair.Key == null || now - pair.Value > PreviewSubscriberTimeoutSeconds)
            {
                stale.Add(pair.Key);
            }
        }

        foreach (var client in stale)
        {
            _previewSubscribers.Remove(client);
        }
    }

    private void TouchPreviewSubscriber(Client c)
    {
        if (!IsServerAuthority || c == null) { return; }
        _previewSubscribers[c] = Timing.TotalTime;
    }

    private void RemovePreviewSubscriber(Client c)
    {
        if (!IsServerAuthority || c == null) { return; }
        _previewSubscribers.Remove(c);
    }

    private bool HasPreviewSubscribers()
    {
        return _previewSubscribers.Count > 0;
    }

    private void TrySyncPreviewSnapshot(bool force)
    {
        if (!IsServerAuthority) { return; }
        if (!HasPreviewSubscribers()) { return; }
        if (GameMain.NetworkMember?.IsServer != true) { return; }

        string identifier = (SelectedSupplyIdentifierPreview ?? "").Trim();
        int amount = Math.Max(0, SelectedSupplyAmountPreview);
        bool changed = force ||
                       !string.Equals(_lastPreviewBroadcastIdentifier ?? "", identifier, StringComparison.OrdinalIgnoreCase) ||
                       _lastPreviewBroadcastAmount != amount;
        if (!changed) { return; }

        _lastPreviewBroadcastIdentifier = identifier;
        _lastPreviewBroadcastAmount = amount;

#if SERVER
        try
        {
            item.CreateServerEvent(this);
        }
        catch (Exception ex)
        {
            if (ModFileLog.IsDebugEnabled)
            {
                ModFileLog.WriteDebug(
                    "Restocker",
                    $"{DatabaseIOTest.Constants.LogPrefix} preview snapshot sync deferred id={item?.ID}: {ex.GetType().Name}: {ex.Message}");
            }
        }
#endif
    }

#if CLIENT
    internal bool DrawClientPreviewFromGuiHook(string source)
    {
        UpdateClientPreviewUi();
        return _previewVisible;
    }

    private void UpdateClientPreviewUi()
    {
        EnsurePreviewUiCreated();
        UpdateClientPreviewVisibility();
        SyncPreviewClientSubscription(_previewVisible);
        if (!_previewVisible || _previewFrame == null) { return; }

        UpdateClientPreviewContent();
        QueuePreviewUi();
    }

    private void UpdateClientPreviewSubscriptionPassive()
    {
        if (!_previewSubscribed) { return; }

        var controlled = Character.Controlled;
        bool shouldShow = controlled != null &&
                          (controlled.SelectedItem == item || controlled.SelectedSecondaryItem == item);
        if (shouldShow) { return; }

        SyncPreviewClientSubscription(false);
    }

    private void EnsurePreviewUiCreated()
    {
        if (_previewFrame != null) { return; }
        if (GUI.Canvas == null) { return; }

        _previewFrame = new GUIFrame(
            new RectTransform(new Microsoft.Xna.Framework.Vector2(0.11f, 0.18f), GUI.Canvas, Anchor.BottomLeft)
            {
                RelativeOffset = new Microsoft.Xna.Framework.Vector2(0.39f, 0.08f)
            },
            style: "ItemUI");
        _previewFrame.Visible = false;
        _previewFrame.Enabled = false;
        _previewFrame.CanBeFocused = false;

        var layout = new GUILayoutGroup(
            new RectTransform(new Microsoft.Xna.Framework.Vector2(0.92f, 0.92f), _previewFrame.RectTransform, Anchor.Center),
            isHorizontal: false)
        {
            AbsoluteSpacing = 2
        };

        _previewIconFrame = new GUIFrame(
            new RectTransform(new Microsoft.Xna.Framework.Vector2(1f, 0.72f), layout.RectTransform),
            style: "InnerFrameDark");
        _previewIconFrame.CanBeFocused = false;

        _previewAmountText = new GUITextBlock(
            new RectTransform(new Microsoft.Xna.Framework.Vector2(1f, 0.28f), layout.RectTransform),
            "x0",
            textAlignment: Alignment.CenterRight);
        _previewAmountText.CanBeFocused = false;
    }

    private void SyncPreviewClientSubscription(bool shouldShow)
    {
        if (item == null || item.Removed)
        {
            _previewSubscribed = false;
            _nextPreviewSubscribeHeartbeatAt = 0;
            return;
        }

        if (GameMain.NetworkMember == null || !GameMain.NetworkMember.IsClient)
        {
            _previewSubscribed = false;
            _nextPreviewSubscribeHeartbeatAt = 0;
            return;
        }

        double now = Timing.TotalTime;
        if (shouldShow)
        {
            if (!_previewSubscribed || now >= _nextPreviewSubscribeHeartbeatAt)
            {
                SendPreviewClientOp(PreviewNetOp.Subscribe);
                _previewSubscribed = true;
                _nextPreviewSubscribeHeartbeatAt = now + PreviewSubscribeHeartbeatSeconds;
            }
            return;
        }

        if (_previewSubscribed)
        {
            SendPreviewClientOp(PreviewNetOp.Unsubscribe);
            _previewSubscribed = false;
            _nextPreviewSubscribeHeartbeatAt = 0;
        }
    }

    private void SendPreviewClientOp(PreviewNetOp op)
    {
        if (op == PreviewNetOp.None || item == null || item.Removed) { return; }
        _pendingPreviewClientOp = (byte)op;
        item.CreateClientEvent(this);
    }

    private void UpdateClientPreviewVisibility()
    {
        bool shouldShow = false;
        if (item != null && !item.Removed)
        {
            var controlled = Character.Controlled;
            if (controlled != null)
            {
                shouldShow = controlled.SelectedItem == item || controlled.SelectedSecondaryItem == item;
            }
        }

        if (_previewVisible == shouldShow && _previewFrame != null) { return; }
        _previewVisible = shouldShow;

        if (_previewFrame != null)
        {
            _previewFrame.Visible = shouldShow;
            _previewFrame.Enabled = shouldShow;
        }
    }

    private static Sprite ResolvePreviewSprite(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return null; }

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        return prefab?.InventoryIcon ?? prefab?.Sprite;
    }

    private void UpdateClientPreviewContent()
    {
        if (_previewFrame == null || _previewIconFrame == null || _previewAmountText == null) { return; }

        string identifier = (SelectedSupplyIdentifierPreview ?? "").Trim();
        int amount = Math.Max(0, SelectedSupplyAmountPreview);
        if (string.IsNullOrWhiteSpace(identifier))
        {
            amount = 0;
        }

        if (string.Equals(_previewLastIdentifier, identifier, StringComparison.OrdinalIgnoreCase) &&
            _previewLastAmount == amount)
        {
            return;
        }

        _previewLastIdentifier = identifier;
        _previewLastAmount = amount;

        _previewIconFrame.ClearChildren();
        Sprite icon = ResolvePreviewSprite(identifier);
        if (icon != null)
        {
            var iconImage = new GUIImage(
                new RectTransform(new Microsoft.Xna.Framework.Vector2(0.86f, 0.86f), _previewIconFrame.RectTransform, Anchor.Center),
                icon,
                scaleToFit: true);
            iconImage.CanBeFocused = false;
        }

        _previewAmountText.Text = $"x{amount}";
        _previewFrame.ToolTip = string.IsNullOrWhiteSpace(identifier)
            ? T("dbiotest.restocker.nosupply", "Not configured")
            : $"{identifier}\n{T("dbiotest.restocker.amount", "Amount")}: {amount}";
    }

    private void QueuePreviewUi()
    {
        if (_previewFrame == null) { return; }

        MethodInfo method = PreviewAddToGuiUpdateListMethodWithOrder ?? PreviewAddToGuiUpdateListMethodNoArgs;
        if (method == null) { return; }

        try
        {
            if (ReferenceEquals(method, PreviewAddToGuiUpdateListMethodWithOrder))
            {
                method.Invoke(_previewFrame, new object[] { false, 2 });
            }
            else
            {
                method.Invoke(_previewFrame, null);
            }
        }
        catch (Exception ex)
        {
            if (ModFileLog.IsDebugEnabled && Timing.TotalTime >= _nextPreviewQueueWarnLogTime)
            {
                _nextPreviewQueueWarnLogTime = Timing.TotalTime + 5.0;
                ModFileLog.WriteDebug(
                    "Restocker",
                    $"{DatabaseIOTest.Constants.LogPrefix} restocker preview queue failed id={item?.ID}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
#endif

    public void ClientEventWrite(IWriteMessage msg, NetEntityEvent.IData extraData = null)
    {
#if CLIENT
        msg.WriteByte(_pendingPreviewClientOp);
        _pendingPreviewClientOp = (byte)PreviewNetOp.None;
#else
        msg.WriteByte((byte)PreviewNetOp.None);
#endif
    }

    public void ServerEventRead(IReadMessage msg, Client c)
    {
        if (!IsServerAuthority) { return; }
        if (c == null) { return; }

        var op = (PreviewNetOp)msg.ReadByte();
        switch (op)
        {
            case PreviewNetOp.Subscribe:
                bool isNewSubscriber = !_previewSubscribers.ContainsKey(c);
                TouchPreviewSubscriber(c);
                if (isNewSubscriber)
                {
                    TrySyncPreviewSnapshot(force: true);
                }
                break;
            case PreviewNetOp.Unsubscribe:
                RemovePreviewSubscriber(c);
                break;
            default:
                break;
        }
    }

    public void ServerEventWrite(IWriteMessage msg, Client c, NetEntityEvent.IData extraData = null)
    {
        msg.WriteByte((byte)PreviewNetOp.Snapshot);
        msg.WriteString((SelectedSupplyIdentifierPreview ?? "").Trim());
        msg.WriteInt32(Math.Max(0, SelectedSupplyAmountPreview));
    }

    public void ClientEventRead(IReadMessage msg, float sendingTime)
    {
        var op = (PreviewNetOp)msg.ReadByte();
        if (op != PreviewNetOp.Snapshot) { return; }

        SelectedSupplyIdentifierPreview = (msg.ReadString() ?? "").Trim();
        SelectedSupplyAmountPreview = Math.Max(0, msg.ReadInt32());
#if CLIENT
        if (_previewVisible)
        {
            UpdateClientPreviewContent();
        }
#endif
    }

    private void RebuildFilteredSupplyList()
    {
        _filteredSupplyList.Clear();
        var baseList = GetBaseSupplyList();
        if (baseList.Count == 0)
        {
            SupplyIndex = 0;
            return;
        }

        string filter = (_lastSupplyFilter ?? "").Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            _filteredSupplyList.AddRange(baseList);
            ClampSupplyIndex(_filteredSupplyList.Count);
            return;
        }

        string lowered = filter.ToLowerInvariant();
        foreach (var entry in baseList)
        {
            if (string.IsNullOrWhiteSpace(entry)) { continue; }
            if (IdentifierMatchesFilter(entry, lowered))
            {
                _filteredSupplyList.Add(entry);
            }
        }

        SupplyIndex = 0;
        ClampSupplyIndex(_filteredSupplyList.Count);
    }

    private List<string> GetFilteredSupplyList()
    {
        if (_filteredSupplyList.Count > 0) { return _filteredSupplyList; }
        RebuildFilteredSupplyList();
        return _filteredSupplyList;
    }

    private List<string> GetBaseSupplyList()
    {
        if (_supplyList.Count > 0)
        {
            return _supplyList;
        }
        if (_cachedDatabaseIdentifiers == null)
        {
            _cachedDatabaseIdentifiers = new List<string>();
        }
        return _cachedDatabaseIdentifiers;
    }

    private void EnsureStoreWatcherRegistered()
    {
        if (!IsServerAuthority) { return; }

        string id = DatabaseStore.Normalize(_resolvedDatabaseId);
        if (string.IsNullOrWhiteSpace(_storeWatcherId))
        {
            _storeWatcherId = $"restocker:{item?.ID ?? -1}";
        }

        if (string.Equals(_watchingDatabaseId, id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_watchingDatabaseId))
        {
            DatabaseStore.Unwatch(_watchingDatabaseId, _storeWatcherId);
        }

        _watchingDatabaseId = id;
        DatabaseStore.WatchAll(
            _watchingDatabaseId,
            _storeWatcherId,
            OnDatabaseDelta,
            sendInitialSnapshot: true);
    }

    private void OnDatabaseDelta(DatabaseStore.DeltaPacket delta)
    {
        if (!IsServerAuthority || delta == null) { return; }
        if (_supplyList.Count > 0) { return; }

        bool changed = false;
        var current = new HashSet<string>(_cachedDatabaseIdentifiers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        if (delta.IsSnapshot)
        {
            current.Clear();
            if (delta.SnapshotAmounts != null)
            {
                foreach (var pair in delta.SnapshotAmounts)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) { continue; }
                    if (pair.Value <= 0) { continue; }
                    current.Add(pair.Key);
                }
            }

            changed = true;
        }
        else
        {
            foreach (var change in delta.Changes)
            {
                if (string.IsNullOrWhiteSpace(change.Key)) { continue; }
                if (change.NewAmount > 0)
                {
                    changed |= current.Add(change.Key);
                }
                else
                {
                    changed |= current.Remove(change.Key);
                }
            }
        }

        _lastIdentifierSnapshotVersion = delta.Version;
        RefreshSelectedSupplyPreviewData();
        if (!changed) { return; }

        _cachedDatabaseIdentifiers = current
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _supplySearchTextCache.Clear();
        RebuildFilteredSupplyList();
    }

    private bool IdentifierMatchesFilter(string identifier, string loweredFilter)
    {
        if (string.IsNullOrWhiteSpace(identifier)) { return false; }
        if (string.IsNullOrWhiteSpace(loweredFilter)) { return true; }

        string searchText = GetSearchTextForIdentifier(identifier);
        if (string.IsNullOrWhiteSpace(searchText)) { return false; }
        return searchText.IndexOf(loweredFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string GetSearchTextForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        if (_supplySearchTextCache.TryGetValue(id, out var cached))
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
        _supplySearchTextCache[id] = combined;
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

    private void TryRestock(double now)
    {
        if (!HasRequiredPower())
        {
            if (Timing.TotalTime >= _nextNoPowerLogTime)
            {
                _nextNoPowerLogTime = Timing.TotalTime + 3.0;
                DebugConsole.NewMessage(
                    $"{DatabaseIOTest.Constants.LogPrefix} Restocker '{_resolvedDatabaseId}' has no power (need {Math.Max(0f, MinRequiredVoltage):0.##}V).",
                    Microsoft.Xna.Framework.Color.Orange);
            }
            return;
        }

        string supplyIdentifier = GetSelectedSupplyIdentifier();
        if (string.IsNullOrWhiteSpace(supplyIdentifier)) { return; }

        foreach (var target in GetTargetInventories())
        {
            var inventory = target.Inventory;
            if (inventory == null) { continue; }

            var slots = GetSlotsForInventory(inventory);
            foreach (int slot in slots)
            {
                if (slot < 0 || slot >= inventory.Capacity) { continue; }
                ulong slotKey = MakeSlotKey(target.OwnerId, target.ContainerIndex, slot);
                if (IsSlotLocked(slotKey, now)) { continue; }

                var current = inventory.GetItemAt(slot);
                bool shouldEjectCurrent = false;
                bool needsSupply = EvaluateSlotNeedsSupply(current, out shouldEjectCurrent);
                UpdateEmptyTick(slotKey, needsSupply);
                if (!needsSupply) { continue; }
                if (_slotEmptyTicks.TryGetValue(slotKey, out int ticks) && ticks < Math.Max(1, EmptyTicksRequired))
                {
                    continue;
                }

                if (!DatabaseStore.TryTakeOneByIdentifierForAutomation(
                        _resolvedDatabaseId,
                        supplyIdentifier,
                        out var supplyData,
                        DatabaseStore.TakePolicy.HighestConditionFirst))
                {
                    continue;
                }

                _slotEmptyTicks[slotKey] = 0;
                LockSlot(slotKey, now + Math.Max(0.05f, SupplyCooldown));

                if (current != null && shouldEjectCurrent)
                {
                    EjectItemFromSlot(current, inventory);
                }

                SpawnSupplyToSlot(supplyData, inventory, slot);
            }
        }
    }

    private void SpawnSupplyToSlot(ItemData itemData, Inventory inventory, int slot)
    {
        if (itemData == null || inventory == null) { return; }
        var prefab = ItemPrefab.FindByIdentifier(itemData.Identifier.ToIdentifier()) as ItemPrefab;
        if (prefab == null)
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, new List<ItemData> { itemData });
            return;
        }

        var (spawnPos, submarine) = ResolveSpawnPoint(inventory);
        Entity.Spawner?.AddItemToSpawnQueue(prefab, spawnPos, submarine, quality: itemData.Quality, onSpawned: spawned =>
        {
            if (spawned == null || spawned.Removed)
            {
                DatabaseStore.AppendItems(_resolvedDatabaseId, new List<ItemData> { itemData });
                return;
            }

            spawned.Condition = itemData.Condition;
            RestoreStolenState(spawned, itemData, 0);

            bool placed = inventory.TryPutItem(spawned, slot, allowSwapping: false, allowCombine: true, user: null, createNetworkEvent: true);
            if (!placed)
            {
                spawned.Drop(null);
                DatabaseStore.AppendItems(_resolvedDatabaseId, new List<ItemData> { itemData });
                return;
            }

            if (itemData.ContainedItems != null && itemData.ContainedItems.Count > 0)
            {
                var containers = spawned.GetComponents<ItemContainer>().Where(c => c?.Inventory != null).ToList();
                if (containers.Count > 0)
                {
                    SpawnService.SpawnItemsIntoInventory(itemData.ContainedItems, containers[0].Inventory, null);
                }
            }
        });
    }

    private IEnumerable<TargetInventory> GetTargetInventories()
    {
        if (item?.linkedTo == null) { yield break; }

        foreach (var linked in item.linkedTo)
        {
            if (!(linked is Item linkedItem)) { continue; }
            if (linkedItem == item) { continue; }
            if (!MatchesTarget(linkedItem)) { continue; }

            var containers = linkedItem.GetComponents<ItemContainer>().Where(c => c?.Inventory != null).ToList();
            for (int i = 0; i < containers.Count; i++)
            {
                yield return new TargetInventory(containers[i].Inventory, linkedItem.ID, i);
            }
        }
    }

    private bool MatchesTarget(Item linkedItem)
    {
        if (linkedItem?.Prefab == null) { return false; }
        if (_targetIdentifiers.Count > 0)
        {
            string id = linkedItem.Prefab.Identifier.Value.ToLowerInvariant();
            if (!_targetIdentifiers.Contains(id)) { return false; }
        }
        if (_targetTags.Count > 0)
        {
            bool hasTag = _targetTags.Any(tag => linkedItem.HasTag(tag));
            if (!hasTag) { return false; }
        }
        return true;
    }

    private IEnumerable<int> GetSlotsForInventory(Inventory inventory)
    {
        if (inventory == null) { yield break; }
        if (_slotWhitelist.Count == 0)
        {
            for (int i = 0; i < inventory.Capacity; i++)
            {
                yield return i;
            }
            yield break;
        }

        foreach (int slot in _slotWhitelist)
        {
            if (slot >= 0 && slot < inventory.Capacity)
            {
                yield return slot;
            }
        }
    }

    private void UpdateEmptyTick(ulong slotKey, bool needsSupply)
    {
        if (!needsSupply)
        {
            if (_slotEmptyTicks.ContainsKey(slotKey))
            {
                _slotEmptyTicks[slotKey] = 0;
            }
            return;
        }

        if (_slotEmptyTicks.TryGetValue(slotKey, out int ticks))
        {
            _slotEmptyTicks[slotKey] = ticks + 1;
        }
        else
        {
            _slotEmptyTicks[slotKey] = 1;
        }
    }

    private static bool IsSlotLocked(ulong slotKey, double now)
    {
        if (SlotLockUntil.TryGetValue(slotKey, out double until))
        {
            if (now < until)
            {
                return true;
            }
            SlotLockUntil.Remove(slotKey);
        }
        return false;
    }

    private static void LockSlot(ulong slotKey, double until)
    {
        SlotLockUntil[slotKey] = until;
    }

    private bool EvaluateSlotNeedsSupply(Item current, out bool shouldEjectCurrent)
    {
        shouldEjectCurrent = false;
        if (current == null || current.Removed)
        {
            return true;
        }

        bool lowCondition = current.Condition <= LowConditionThreshold;
        if (lowCondition)
        {
            shouldEjectCurrent = true;
            return true;
        }

        return false;
    }

    private static void EjectItemFromSlot(Item current, Inventory expectedInventory)
    {
        if (current == null || current.Removed) { return; }

        if (expectedInventory != null && current.ParentInventory == expectedInventory)
        {
            expectedInventory.RemoveItem(current);
        }
        else
        {
            current.ParentInventory?.RemoveItem(current);
        }

        current.Drop(null);
    }

    private void UpdateDescription()
    {
        string dbLabel = T("dbiotest.restocker.dbid", "Database ID");
        string supplyLabel = T("dbiotest.restocker.supply", "Supply");
        string targetLabel = T("dbiotest.restocker.targets", "Targets");
        string slotLabel = T("dbiotest.restocker.slots", "Slots");
        string filterLabel = T("dbiotest.restocker.filter", "Filter");
        string hint = T("dbiotest.restocker.hint", "Auto restock linked containers.");
        string supply = GetSelectedSupplyIdentifier();
        if (string.IsNullOrWhiteSpace(supply))
        {
            supply = T("dbiotest.restocker.nosupply", "Not configured");
        }

        int targetCount = GetTargetInventories().Count();
        string slotText = _slotWhitelist.Count > 0 ? string.Join(",", _slotWhitelist) : T("dbiotest.restocker.slots.all", "All");

        string powerLine = "";
        if (RequirePower)
        {
            string powerLabel = T("dbiotest.power.status", "Power");
            string online = T("dbiotest.power.online", "Online");
            string offline = T("dbiotest.power.offline", "Offline");
            string state = HasRequiredPower() ? online : offline;
            powerLine = $"\n{powerLabel}: {state} ({GetCurrentVoltage():0.##}/{Math.Max(0f, MinRequiredVoltage):0.##}V)";
        }

        string filterInfo = "";
        if (!string.IsNullOrWhiteSpace(SupplyFilter))
        {
            filterInfo = $"\n{filterLabel}: {SupplyFilter}";
        }

        item.Description = $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{supplyLabel}: {supply}{filterInfo}\n{targetLabel}: {targetCount}\n{slotLabel}: {slotText}{powerLine}";
    }

    private float GetCurrentVoltage()
    {
        var powered = item.GetComponent<Powered>();
        return powered?.Voltage ?? 0f;
    }

    private bool HasRequiredPower()
    {
        // Temporary: power gating fully disabled for troubleshooting.
        return true;
    }

    private static string T(string key, string fallback)
    {
        var value = TextManager.Get(key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static IEnumerable<string> SplitTokens(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) { yield break; }
        var parts = raw.Split(new[] { ',', ';', '\n', '\r', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            yield return part;
        }
    }

    private static ulong MakeSlotKey(int itemId, int containerIndex, int slot)
    {
        return ((ulong)(uint)itemId << 32) | ((ulong)(ushort)containerIndex << 16) | (ushort)slot;
    }

    private static void RestoreStolenState(Item item, ItemData itemData, int stackIndex)
    {
        int idx = stackIndex >= 0 ? stackIndex : 0;
        if (itemData.OriginalOutposts != null &&
            idx < itemData.OriginalOutposts.Count &&
            !string.IsNullOrEmpty(itemData.OriginalOutposts[idx]))
        {
            item.OriginalOutpost = itemData.OriginalOutposts[idx];
            item.AllowStealing = false;
        }

        if (itemData.StolenFlags != null &&
            idx < itemData.StolenFlags.Count &&
            itemData.StolenFlags[idx])
        {
            item.StolenDuringRound = true;
        }
    }

    private static (Microsoft.Xna.Framework.Vector2 pos, Submarine sub) ResolveSpawnPoint(Inventory targetInventory)
    {
        if (targetInventory?.Owner is Item ownerItem)
        {
            return (ownerItem.WorldPosition, ownerItem.Submarine);
        }
        if (targetInventory?.Owner is Character ownerChar)
        {
            return (ownerChar.WorldPosition, ownerChar.Submarine);
        }
        return (Microsoft.Xna.Framework.Vector2.Zero, null);
    }
}


