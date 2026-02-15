using System;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;

public partial class DatabaseStorageAnchorComponent : ItemComponent
{
    [Editable, Serialize(Constants.DefaultDatabaseId, IsPropertySaveable.Yes, description: "Shared database id.")]
    public string DatabaseId { get; set; } = Constants.DefaultDatabaseId;

    [Editable, Serialize("", IsPropertySaveable.Yes, description: "Persisted shared database encoded string.")]
    public string SerializedDatabase { get; set; } = "";

    [Editable, Serialize(0, IsPropertySaveable.Yes, description: "Persisted database version.")]
    public int DatabaseVersion { get; set; } = 0;

    private string _resolvedDatabaseId = Constants.DefaultDatabaseId;
    private int _cachedPersistedCount;
    private double _lastDescriptionUpdateTime;
    private const double DescriptionUpdateInterval = 0.5;

    private bool IsServerAuthority => GameMain.NetworkMember == null || GameMain.NetworkMember.IsServer;
    public int AnchorEntityId => item?.ID ?? -1;

    public DatabaseStorageAnchorComponent(Item item, ContentXElement element) : base(item, element)
    {
        IsActive = true;
    }

    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);

        if (IsServerAuthority)
        {
            DatabaseStore.RegisterPersistenceAnchor(this);
            UpdateDescription();
            return;
        }

        LoadSummaryFromSerialized();
        UpdateDescription();
    }

    public override void Update(float deltaTime, Camera cam)
    {
        if (Timing.TotalTime - _lastDescriptionUpdateTime < DescriptionUpdateInterval)
        {
            return;
        }

        _lastDescriptionUpdateTime = Timing.TotalTime;
        UpdateDescription();
    }

    public override void RemoveComponentSpecific()
    {
        if (IsServerAuthority)
        {
            DatabaseStore.UnregisterPersistenceAnchor(this);
        }
    }

    public void ResolveDatabaseId(string normalized)
    {
        _resolvedDatabaseId = DatabaseStore.Normalize(normalized);
        DatabaseId = _resolvedDatabaseId;
    }

    public DatabaseData ReadPersistedData()
    {
        ResolveDatabaseId(DatabaseId);
        var data = DatabaseStore.DeserializeData(SerializedDatabase, _resolvedDatabaseId);
        data.DatabaseId = _resolvedDatabaseId;
        data.Version = Math.Max(DatabaseVersion, data.Version);
        data.Items = DatabaseStore.CompactSnapshot(data.Items);
        _cachedPersistedCount = data.ItemCount;
        return data;
    }

    public void ApplyStoreSnapshot(DatabaseData data, bool persistSerializedState = false)
    {
        if (!IsServerAuthority || data == null) { return; }

        ResolveDatabaseId(data.DatabaseId);
        _cachedPersistedCount = data.ItemCount;

        if (persistSerializedState)
        {
            DatabaseVersion = data.Version;
            SerializedDatabase = DatabaseStore.SerializeData(data);
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} PersistAnchorState db='{_resolvedDatabaseId}' anchor={AnchorEntityId} " +
                $"version={DatabaseVersion} items={_cachedPersistedCount} serializedLen={(SerializedDatabase?.Length ?? 0)}");
        }

        UpdateDescription();
    }

    private void LoadSummaryFromSerialized()
    {
        var data = DatabaseStore.DeserializeData(SerializedDatabase, DatabaseId);
        _resolvedDatabaseId = data.DatabaseId;
        DatabaseId = _resolvedDatabaseId;
        DatabaseVersion = Math.Max(DatabaseVersion, data.Version);
        _cachedPersistedCount = data.ItemCount;
    }

    private void UpdateDescription()
    {
        int displayCount = _cachedPersistedCount;
        if (IsServerAuthority)
        {
            displayCount = DatabaseStore.GetItemCount(_resolvedDatabaseId);
            _cachedPersistedCount = displayCount;
        }

        string hint = T("dbiotest.anchor.hint", "Persistent in-save storage anchor for database data.");
        string dbLabel = T("dbiotest.anchor.dbid", "Database ID");
        string countLabel = T("dbiotest.anchor.count", "Persisted Item Count");

        item.Description = $"{hint}\n\n{dbLabel}: {_resolvedDatabaseId}\n{countLabel}: {Math.Max(0, displayCount)}";
    }

    private static string T(string key, string fallback)
    {
        var value = TextManager.Get(key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
