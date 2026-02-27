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
        foreach (var target in allItems)
        {
            if (target == null || target.Removed) { continue; }
            if (CanIngest(target))
            {
                ingestable.Add(target);
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

        item.Description = $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{countLabel}: {count}/{Math.Max(1, MaxStorageCount)}{powerLine}";
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
