using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;

public partial class DatabaseAutoRestockerComponent : ItemComponent
{
    private enum RestockerAction : byte
    {
        None = 0,
        PrevSupply = 1,
        NextSupply = 2
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

    private string _resolvedDatabaseId = DatabaseIOTest.Constants.DefaultDatabaseId;
    private double _lastPollTime;
    private double _lastDescriptionUpdateTime;
    private double _nextNoPowerLogTime;

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

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

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
    }

    public override void Update(float deltaTime, Camera cam)
    {
        if (!IsServerAuthority) { return; }

        RefreshParsedConfig();
        ConsumeXmlActionRequest();

        double now = Timing.TotalTime;
        if (now - _lastPollTime >= Math.Max(PollInterval, 0.05f))
        {
            _lastPollTime = now;
            TryRestock(now);
        }

        if (now - _lastDescriptionUpdateTime >= DescriptionUpdateInterval)
        {
            _lastDescriptionUpdateTime = now;
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
