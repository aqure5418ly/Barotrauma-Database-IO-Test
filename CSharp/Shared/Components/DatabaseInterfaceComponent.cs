using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Services;

public partial class DatabaseInterfaceComponent : ItemComponent
{
    [Editable, Serialize(DatabaseIOTest.Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = DatabaseIOTest.Constants.DefaultDatabaseId;

    [Editable(MinValueInt = 1, MaxValueInt = 100000), Serialize(DatabaseIOTest.Constants.DefaultMaxStorageCount, IsPropertySaveable.Yes, description: "Maximum storable item entities.")]
    public int MaxStorageCount { get; set; } = DatabaseIOTest.Constants.DefaultMaxStorageCount;

    [Editable(MinValueFloat = 0.05f, MaxValueFloat = 10f), Serialize(DatabaseIOTest.Constants.DefaultIngestInterval, IsPropertySaveable.Yes, description: "Ingest check interval in seconds.")]
    public float IngestInterval { get; set; } = DatabaseIOTest.Constants.DefaultIngestInterval;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Require incoming electrical power to ingest.")]
    public bool RequirePower { get; set; } = false;

    [Editable(MinValueFloat = 0.0f, MaxValueFloat = 10f), Serialize(0.5f, IsPropertySaveable.Yes, description: "Minimum voltage required when RequirePower=true.")]
    public float MinRequiredVoltage { get; set; } = 0.5f;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "Unpack container contents before ingesting.")]
    public bool AutoUnpackContainers { get; set; } = false;

    [Editable, Serialize(false, IsPropertySaveable.Yes, description: "When unpacking, only ingest unpacked contents and skip top-level items.")]
    public bool UnpackContainersOnly { get; set; } = false;

    [Editable, Serialize(true, IsPropertySaveable.Yes, description: "Keep container item in interface after unpacking its contents.")]
    public bool KeepContainerAfterUnpack { get; set; } = true;

    private string _resolvedDatabaseId = DatabaseIOTest.Constants.DefaultDatabaseId;
    private double _lastIngestCheckTime;
    private double _lastDescriptionUpdateTime;
    private double _nextNoPowerLogTime;

    private const double DescriptionUpdateInterval = 0.5;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;

    public DatabaseInterfaceComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
    }

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);
        if (IsServerAuthority)
        {
            UpdateDescription();
        }
    }

    public override void Update(float deltaTime, Camera cam)
    {
        if (!IsServerAuthority) { return; }

        double now = Timing.TotalTime;
        if (now - _lastIngestCheckTime >= Math.Max(IngestInterval, 0.05f))
        {
            _lastIngestCheckTime = now;
            TryIngestItems();
        }

        if (now - _lastDescriptionUpdateTime >= DescriptionUpdateInterval)
        {
            _lastDescriptionUpdateTime = now;
            UpdateDescription();
        }
    }

    private void TryIngestItems()
    {
        if (!HasRequiredPower())
        {
            if (Timing.TotalTime >= _nextNoPowerLogTime)
            {
                _nextNoPowerLogTime = Timing.TotalTime + 3.0;
                DebugConsole.NewMessage(
                    $"{DatabaseIOTest.Constants.LogPrefix} Interface '{_resolvedDatabaseId}' has no power (need {Math.Max(0f, MinRequiredVoltage):0.##}V).",
                    Microsoft.Xna.Framework.Color.Orange);
            }
            return;
        }

        var container = item.GetComponent<ItemContainer>();
        if (container?.Inventory == null) { return; }

        var allItems = container.Inventory.AllItemsMod?.Where(i => i != null).ToList() ?? new List<Item>();
        if (allItems.Count == 0) { return; }

        var ingestable = new List<Item>();
        var seenIngestIds = new HashSet<int>();
        foreach (var target in allItems)
        {
            if (target == null || target.Removed) { continue; }

            if (AutoUnpackContainers && TryCollectUnpackContents(target, out var unpackContents))
            {
                foreach (var unpacked in unpackContents)
                {
                    if (CanIngest(unpacked))
                    {
                        AddUniqueIngestTarget(ingestable, seenIngestIds, unpacked);
                    }
                }

                if (!KeepContainerAfterUnpack && !UnpackContainersOnly && CanIngest(target))
                {
                    AddUniqueIngestTarget(ingestable, seenIngestIds, target);
                }
                continue;
            }

            if (UnpackContainersOnly) { continue; }

            if (CanIngest(target))
            {
                AddUniqueIngestTarget(ingestable, seenIngestIds, target);
            }
        }

        if (ingestable.Count == 0) { return; }

        var serialized = ItemSerializer.SerializeItems(null, ingestable);
        int incomingCount = ItemSerializer.CountItems(serialized);
        int existingCount = DatabaseStore.GetItemCount(_resolvedDatabaseId);

        if (existingCount + incomingCount > Math.Max(1, MaxStorageCount))
        {
            DebugConsole.NewMessage(
                $"{DatabaseIOTest.Constants.LogPrefix} Ingest denied: capacity exceeded for '{_resolvedDatabaseId}' ({existingCount + incomingCount}>{MaxStorageCount})",
                Microsoft.Xna.Framework.Color.OrangeRed);
            return;
        }

        DatabaseStore.AppendItems(_resolvedDatabaseId, serialized);

        foreach (var target in ingestable)
        {
            if (target.Removed) { continue; }
            target.ParentInventory?.RemoveItem(target);
            SpawnService.RemoveItem(target);
        }
    }

    private bool CanIngest(Item target)
    {
        if (target == null || target.Prefab == null) { return false; }
        if (target == item) { return false; }
        if (target.Illegitimate) { return false; }

        string identifier = target.Prefab.Identifier.Value.ToLowerInvariant();
        if (DatabaseIOTest.Constants.BlockedIdentifiers.Contains(identifier)) { return false; }

        foreach (var tag in DatabaseIOTest.Constants.BlockedTags)
        {
            if (target.HasTag(tag)) { return false; }
        }

        return true;
    }

    private static void AddUniqueIngestTarget(List<Item> ingestable, HashSet<int> seenIds, Item target)
    {
        if (target == null || target.Removed) { return; }
        if (!seenIds.Add(target.ID)) { return; }
        ingestable.Add(target);
    }

    private static bool TryCollectUnpackContents(Item containerItem, out List<Item> contents)
    {
        contents = null;
        if (containerItem == null || containerItem.Removed) { return false; }

        var containerComponents = containerItem.GetComponents<ItemContainer>()?.Where(c => c?.Inventory != null).ToList();
        if (containerComponents == null || containerComponents.Count == 0) { return false; }

        var collected = new List<Item>();
        var seenIds = new HashSet<int>();
        foreach (var component in containerComponents)
        {
            var nestedItems = component.Inventory?.AllItemsMod;
            if (nestedItems == null) { continue; }

            foreach (var nested in nestedItems)
            {
                if (nested == null || nested.Removed) { continue; }
                if (nested == containerItem) { continue; }
                if (!seenIds.Add(nested.ID)) { continue; }
                collected.Add(nested);
            }
        }

        if (collected.Count == 0) { return false; }
        contents = collected;
        return true;
    }

    private void UpdateDescription()
    {
        int count = DatabaseStore.GetItemCount(_resolvedDatabaseId);
        string dbLabel = T("dbiotest.interface.dbid", "Database ID");
        string countLabel = T("dbiotest.interface.count", "Stored Item Count");
        string hint = T("dbiotest.interface.hint", "Insert items to ingest into database.");
        string powerLine = "";
        if (RequirePower)
        {
            string powerLabel = T("dbiotest.power.status", "Power");
            string online = T("dbiotest.power.online", "Online");
            string offline = T("dbiotest.power.offline", "Offline");
            string state = HasRequiredPower() ? online : offline;
            powerLine = $"\n{powerLabel}: {state} ({GetCurrentVoltage():0.##}/{Math.Max(0f, MinRequiredVoltage):0.##}V)";
        }

        string modeLine = "";
        if (AutoUnpackContainers)
        {
            string unpackMode = UnpackContainersOnly
                ? T("dbiotest.interface.mode.unpackonly", "Mode: unpack container contents only")
                : T("dbiotest.interface.mode.unpackmixed", "Mode: ingest items and unpack container contents");
            string keepMode = KeepContainerAfterUnpack
                ? T("dbiotest.interface.mode.keepcontainer", "Container: keep in interface")
                : T("dbiotest.interface.mode.storecontainer", "Container: ingest after unpack");
            modeLine = $"\n{unpackMode}\n{keepMode}";
        }

        item.Description = $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{countLabel}: {count}/{Math.Max(1, MaxStorageCount)}{powerLine}{modeLine}";
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
}
