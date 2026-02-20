using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Barotrauma;
using DatabaseIOTest.Models;

namespace DatabaseIOTest.Services
{
    public static class DatabaseStore
    {
        public enum TakePolicy
        {
            Fifo = 0,
            HighestConditionFirst = 1,
            LowestConditionFirst = 2
        }

        public readonly struct DeltaChange
        {
            public DeltaChange(string key, int oldAmount, int newAmount)
            {
                Key = key ?? "";
                OldAmount = Math.Max(0, oldAmount);
                NewAmount = Math.Max(0, newAmount);
            }

            public string Key { get; }
            public int OldAmount { get; }
            public int NewAmount { get; }
        }

        public sealed class DeltaPacket
        {
            public DeltaPacket(
                string databaseId,
                int previousVersion,
                int version,
                string source,
                bool isSnapshot,
                int totalAmount,
                List<DeltaChange> changes,
                Dictionary<string, int> snapshotAmounts = null)
            {
                DatabaseId = Normalize(databaseId);
                PreviousVersion = Math.Max(0, previousVersion);
                Version = Math.Max(0, version);
                Source = source ?? "";
                IsSnapshot = isSnapshot;
                TotalAmount = Math.Max(0, totalAmount);
                Changes = changes ?? new List<DeltaChange>();
                SnapshotAmounts = snapshotAmounts ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            }

            public string DatabaseId { get; }
            public int PreviousVersion { get; }
            public int Version { get; }
            public string Source { get; }
            public bool IsSnapshot { get; }
            public int TotalAmount { get; }
            public IReadOnlyList<DeltaChange> Changes { get; }
            public IReadOnlyDictionary<string, int> SnapshotAmounts { get; }
        }

        private enum StateMutationKind
        {
            Ensure = 0,
            Set = 1,
            Clear = 2,
            Merge = 3,
            Extract = 4,
            Rebuild = 5,
            Restore = 6,
            Compact = 7
        }

        private sealed class StateMutation
        {
            public StateMutation(
                long sequence,
                string databaseId,
                StateMutationKind kind,
                string source,
                int beforeVersion,
                int beforeCount,
                int afterVersion,
                int afterCount)
            {
                Sequence = sequence;
                DatabaseId = databaseId;
                Kind = kind;
                Source = source ?? "";
                BeforeVersion = beforeVersion;
                BeforeCount = beforeCount;
                AfterVersion = afterVersion;
                AfterCount = afterCount;
                TimestampUtc = DateTime.UtcNow;
            }

            public long Sequence { get; }
            public string DatabaseId { get; }
            public StateMutationKind Kind { get; }
            public string Source { get; }
            public int BeforeVersion { get; }
            public int BeforeCount { get; }
            public int AfterVersion { get; }
            public int AfterCount { get; }
            public DateTime TimestampUtc { get; }
        }

        private sealed class StateEntry
        {
            public StateEntry(string databaseId, DatabaseData data)
            {
                DatabaseId = databaseId;
                Data = data;
            }

            public string DatabaseId { get; }
            public DatabaseData Data { get; set; }
            public StateMutation LastMutation { get; set; }
        }

        private sealed class WatchSubscription
        {
            public string DatabaseId { get; set; }
            public string WatcherId { get; set; }
            public Action<DeltaPacket> Callback { get; set; }
            public HashSet<string> Keys { get; set; }
            public int LastSeenVersion { get; set; } = -1;
        }

        private sealed class DatabaseState
        {
            private readonly Dictionary<string, StateEntry> _entries =
                new Dictionary<string, StateEntry>(StringComparer.OrdinalIgnoreCase);
            private long _nextMutationSequence = 1;

            public int Count => _entries.Count;

            public IEnumerable<string> Keys => _entries.Keys;

            public IEnumerable<KeyValuePair<string, StateEntry>> Entries => _entries;

            public IEnumerable<DatabaseData> Values => _entries.Values
                .Where(entry => entry?.Data != null)
                .Select(entry => entry.Data);

            public void Clear(string source)
            {
                if (_entries.Count <= 0)
                {
                    return;
                }

                foreach (var pair in _entries)
                {
                    if (pair.Value?.Data == null) { continue; }
                    pair.Value.LastMutation = BuildMutation(
                        pair.Key,
                        StateMutationKind.Clear,
                        source,
                        pair.Value.Data.Version,
                        pair.Value.Data.ItemCount,
                        0,
                        0);
                }

                _entries.Clear();
            }

            public bool TryGetData(string databaseId, out DatabaseData data)
            {
                if (_entries.TryGetValue(databaseId, out var entry) && entry?.Data != null)
                {
                    data = entry.Data;
                    return true;
                }

                data = null;
                return false;
            }

            public DatabaseData GetOrCreateData(string databaseId, string source)
            {
                if (!_entries.TryGetValue(databaseId, out var entry) || entry == null || entry.Data == null)
                {
                    var created = new DatabaseData
                    {
                        DatabaseId = databaseId,
                        Version = 0,
                        Items = new List<ItemData>()
                    };

                    entry = new StateEntry(databaseId, created)
                    {
                        LastMutation = BuildMutation(databaseId, StateMutationKind.Ensure, source, 0, 0, created.Version, created.ItemCount)
                    };
                    _entries[databaseId] = entry;
                }

                if (entry.Data.Items == null)
                {
                    entry.Data.Items = new List<ItemData>();
                }

                return entry.Data;
            }

            public void SetData(string databaseId, DatabaseData data, StateMutationKind kind, string source)
            {
                if (!_entries.TryGetValue(databaseId, out var entry) || entry == null)
                {
                    entry = new StateEntry(databaseId, null);
                    _entries[databaseId] = entry;
                }

                int beforeVersion = entry.Data?.Version ?? 0;
                int beforeCount = entry.Data?.ItemCount ?? 0;

                var next = data ?? new DatabaseData();
                next.DatabaseId = databaseId;
                if (next.Items == null)
                {
                    next.Items = new List<ItemData>();
                }

                entry.Data = next;
                entry.LastMutation = BuildMutation(
                    databaseId,
                    kind,
                    source,
                    beforeVersion,
                    beforeCount,
                    next.Version,
                    next.ItemCount);
            }

            public void RecordMutation(string databaseId, StateMutationKind kind, string source, int beforeVersion, int beforeCount)
            {
                if (!_entries.TryGetValue(databaseId, out var entry) || entry?.Data == null)
                {
                    return;
                }

                entry.LastMutation = BuildMutation(
                    databaseId,
                    kind,
                    source,
                    beforeVersion,
                    beforeCount,
                    entry.Data.Version,
                    entry.Data.ItemCount);
            }

            private StateMutation BuildMutation(
                string databaseId,
                StateMutationKind kind,
                string source,
                int beforeVersion,
                int beforeCount,
                int afterVersion,
                int afterCount)
            {
                return new StateMutation(
                    _nextMutationSequence++,
                    databaseId,
                    kind,
                    source,
                    beforeVersion,
                    beforeCount,
                    afterVersion,
                    afterCount);
            }
        }

        private static readonly DatabaseState _workingState = new DatabaseState();
        private static readonly DatabaseState _committedState = new DatabaseState();
        private static readonly Dictionary<string, int> _activeTerminals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>> _terminals =
            new Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<WeakReference<DatabaseStorageAnchorComponent>>> _anchors =
            new Dictionary<string, List<WeakReference<DatabaseStorageAnchorComponent>>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DatabaseData> _pendingCommittedSnapshot =
            new Dictionary<string, DatabaseData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, WatchSubscription>> _watchAll =
            new Dictionary<string, Dictionary<string, WatchSubscription>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Dictionary<string, WatchSubscription>> _watchByKey =
            new Dictionary<string, Dictionary<string, WatchSubscription>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DeltaPacket> _latestDeltaByDatabase =
            new Dictionary<string, DeltaPacket>(StringComparer.OrdinalIgnoreCase);
        private static bool _roundDecisionApplied;
        private static string _roundDecisionSource = "";
        private static int _roundDecisionPriority;
        private static bool _saveDecisionBindingEnabled;
        private static bool _pendingRestoreArmed;
        private static double _nextSyncPerfLogAt;

        public static string Normalize(string databaseId)
        {
            return string.IsNullOrWhiteSpace(databaseId) ? Constants.DefaultDatabaseId : databaseId.Trim();
        }

        public static void WatchAll(
            string databaseId,
            string watcherId,
            Action<DeltaPacket> callback,
            bool sendInitialSnapshot = true)
        {
            RegisterWatcher(databaseId, watcherId, callback, keys: null, sendInitialSnapshot: sendInitialSnapshot);
        }

        public static void WatchByKey(
            string databaseId,
            string watcherId,
            IEnumerable<string> keys,
            Action<DeltaPacket> callback,
            bool sendInitialSnapshot = true)
        {
            RegisterWatcher(databaseId, watcherId, callback, keys: keys, sendInitialSnapshot: sendInitialSnapshot);
        }

        public static void UpdateWatchKeys(string databaseId, string watcherId, IEnumerable<string> keys)
        {
            string id = Normalize(databaseId);
            string normalizedWatcherId = NormalizeWatcherId(watcherId);
            var normalizedKeys = NormalizeWatchKeys(keys);

            if (_watchByKey.TryGetValue(id, out var byKeyMap) &&
                byKeyMap.TryGetValue(normalizedWatcherId, out var existing))
            {
                existing.Keys = normalizedKeys;
                return;
            }

            RegisterWatcher(id, normalizedWatcherId, callback: null, keys: normalizedKeys, sendInitialSnapshot: false, requireExistingCallback: true);
        }

        public static void Unwatch(string databaseId, string watcherId)
        {
            string id = Normalize(databaseId);
            string normalizedWatcherId = NormalizeWatcherId(watcherId);

            if (_watchAll.TryGetValue(id, out var watchAllMap))
            {
                watchAllMap.Remove(normalizedWatcherId);
                if (watchAllMap.Count <= 0)
                {
                    _watchAll.Remove(id);
                }
            }

            if (_watchByKey.TryGetValue(id, out var watchByKeyMap))
            {
                watchByKeyMap.Remove(normalizedWatcherId);
                if (watchByKeyMap.Count <= 0)
                {
                    _watchByKey.Remove(id);
                }
            }
        }

        public static void Clear()
        {
            _workingState.Clear("store:clear");
            _committedState.Clear("store:clear");
            _activeTerminals.Clear();
            _terminals.Clear();
            _anchors.Clear();
            _pendingCommittedSnapshot.Clear();
            _watchAll.Clear();
            _watchByKey.Clear();
            _latestDeltaByDatabase.Clear();
            _roundDecisionApplied = false;
            _roundDecisionSource = "";
            _roundDecisionPriority = 0;
            _saveDecisionBindingEnabled = false;
            _pendingRestoreArmed = false;
        }

        public static void ClearVolatile()
        {
            _workingState.Clear("store:clearVolatile");
            _activeTerminals.Clear();
            _latestDeltaByDatabase.Clear();
        }

        private static string NormalizeWatcherId(string watcherId)
        {
            return string.IsNullOrWhiteSpace(watcherId) ? Guid.NewGuid().ToString("N") : watcherId.Trim();
        }

        private static HashSet<string> NormalizeWatchKeys(IEnumerable<string> keys)
        {
            if (keys == null)
            {
                return null;
            }

            var normalized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in keys)
            {
                string key = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key)) { continue; }
                normalized.Add(key);
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static void RegisterWatcher(
            string databaseId,
            string watcherId,
            Action<DeltaPacket> callback,
            IEnumerable<string> keys,
            bool sendInitialSnapshot,
            bool requireExistingCallback = false)
        {
            string id = Normalize(databaseId);
            string normalizedWatcherId = NormalizeWatcherId(watcherId);
            var normalizedKeys = NormalizeWatchKeys(keys);

            Action<DeltaPacket> effectiveCallback = callback;
            if (effectiveCallback == null)
            {
                if (_watchAll.TryGetValue(id, out var watchAllMap) &&
                    watchAllMap.TryGetValue(normalizedWatcherId, out var existingWatchAll) &&
                    existingWatchAll?.Callback != null)
                {
                    effectiveCallback = existingWatchAll.Callback;
                }
                else if (_watchByKey.TryGetValue(id, out var watchByKeyMap) &&
                         watchByKeyMap.TryGetValue(normalizedWatcherId, out var existingWatchByKey) &&
                         existingWatchByKey?.Callback != null)
                {
                    effectiveCallback = existingWatchByKey.Callback;
                }
            }

            if (effectiveCallback == null && requireExistingCallback)
            {
                return;
            }

            WatchSubscription subscription;
            if (normalizedKeys == null)
            {
                if (!_watchAll.TryGetValue(id, out var map))
                {
                    map = new Dictionary<string, WatchSubscription>(StringComparer.OrdinalIgnoreCase);
                    _watchAll[id] = map;
                }

                subscription = new WatchSubscription
                {
                    DatabaseId = id,
                    WatcherId = normalizedWatcherId,
                    Callback = effectiveCallback,
                    Keys = null
                };
                map[normalizedWatcherId] = subscription;

                if (_watchByKey.TryGetValue(id, out var oldByKey))
                {
                    oldByKey.Remove(normalizedWatcherId);
                    if (oldByKey.Count <= 0)
                    {
                        _watchByKey.Remove(id);
                    }
                }
            }
            else
            {
                if (!_watchByKey.TryGetValue(id, out var map))
                {
                    map = new Dictionary<string, WatchSubscription>(StringComparer.OrdinalIgnoreCase);
                    _watchByKey[id] = map;
                }

                subscription = new WatchSubscription
                {
                    DatabaseId = id,
                    WatcherId = normalizedWatcherId,
                    Callback = effectiveCallback,
                    Keys = normalizedKeys
                };
                map[normalizedWatcherId] = subscription;

                if (_watchAll.TryGetValue(id, out var oldAll))
                {
                    oldAll.Remove(normalizedWatcherId);
                    if (oldAll.Count <= 0)
                    {
                        _watchAll.Remove(id);
                    }
                }
            }

            if (sendInitialSnapshot && effectiveCallback != null)
            {
                if (TryBuildSnapshotPacket(id, "watch:initial", -1, normalizedKeys, out var snapshot))
                {
                    try
                    {
                        effectiveCallback(snapshot);
                        subscription.LastSeenVersion = snapshot.Version;
                        ModFileLog.WriteDebug(
                            "Store",
                            $"{Constants.LogPrefix} WatchRegister db='{id}' watcher='{normalizedWatcherId}' " +
                            $"mode='{(normalizedKeys == null ? "all" : "byKey")}' snapshotVersion={snapshot.Version} " +
                            $"snapshotKeys={snapshot.Changes.Count}");
                    }
                    catch (Exception ex)
                    {
                        ModFileLog.Write(
                            "Store",
                            $"{Constants.LogPrefix} Watch callback failed watcher='{normalizedWatcherId}' db='{id}' on initial snapshot: {ex.Message}");
                    }
                }
                return;
            }

            if (TryGetWorkingData(id, out var existingData) && existingData != null)
            {
                subscription.LastSeenVersion = Math.Max(0, existingData.Version);
            }

            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} WatchRegister db='{id}' watcher='{normalizedWatcherId}' " +
                $"mode='{(normalizedKeys == null ? "all" : "byKey")}' snapshotVersion={subscription.LastSeenVersion}");
        }

        public static void BeginRound(string source = "roundStart")
        {
            _roundDecisionApplied = false;
            _roundDecisionSource = "";
            _roundDecisionPriority = 0;
            ClearVolatile();
            RebuildFromPersistedTerminals();
            TryRestoreFromPendingCommitSnapshot();
            int totalItems = GetWorkingTotalItems();
            int committedItems = GetCommittedTotalItems();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} BeginRound source='{source}' workingDbCount={_workingState.Count} " +
                $"workingItems={totalItems} committedDbCount={_committedState.Count} committedItems={committedItems}");
            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} BeginRound debug source='{source}' pendingRestoreArmed={_pendingRestoreArmed} " +
                $"pendingSnapshotDbCount={_pendingCommittedSnapshot.Count} saveBinding={_saveDecisionBindingEnabled}");
        }

        public static void CommitRound(string source = "unknown")
        {
            if (!TryMarkRoundDecision("commit", source))
            {
                return;
            }

            // Use non-converting close to force deterministic writeback before persistence.
            int closed = ForceCloseAllActiveSessions($"commit:{source}", convertToClosedItem: false);
            PromoteWorkingToCommitted(source);
            PersistCommittedStateToAnchors();
            CapturePendingCommitSnapshot();
            int totalItems = GetWorkingTotalItems();
            int committedItems = GetCommittedTotalItems();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} CommitRound source='{source}' forcedClosed={closed} workingDbCount={_workingState.Count} " +
                $"workingItems={totalItems} committedDbCount={_committedState.Count} committedItems={committedItems}");
            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} CommitRound debug source='{source}' pendingSnapshotDbCount={_pendingCommittedSnapshot.Count} " +
                $"pendingRestoreArmed={_pendingRestoreArmed}");
        }

        public static void RollbackRound(string reason = "unknown")
        {
            if (!TryMarkRoundDecision("rollback", reason))
            {
                return;
            }

            int closed = ForceCloseAllActiveSessions($"rollback:{reason}", convertToClosedItem: false);
            if (_committedState.Count <= 0)
            {
                RebuildFromPersistedTerminals();
            }

            TryRestoreFromPendingCommitSnapshot();
            RestoreWorkingFromCommitted($"rollback:{reason}");
            SyncAllKnownDatabases();

            _pendingRestoreArmed = _pendingCommittedSnapshot.Count > 0;
            int totalItems = GetWorkingTotalItems();
            int committedItems = GetCommittedTotalItems();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} RollbackRound reason='{reason}' forcedClosed={closed} workingDbCount={_workingState.Count} " +
                $"workingItems={totalItems} committedDbCount={_committedState.Count} committedItems={committedItems}");
            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} RollbackRound debug reason='{reason}' pendingRestoreArmed={_pendingRestoreArmed} " +
                $"pendingSnapshotDbCount={_pendingCommittedSnapshot.Count}");
        }

        public static void OnRoundEndObserved(string source = "roundEnd")
        {
            if (_roundDecisionApplied)
            {
                return;
            }

            ModFileLog.Write("Store", $"{Constants.LogPrefix} RoundEnd observed without explicit save decision source='{source}'. Waiting for roundStart fallback rebuild.");
        }

        public static void SetSaveDecisionBindingEnabled(bool enabled)
        {
            _saveDecisionBindingEnabled = enabled;
            ModFileLog.Write("Store", $"{Constants.LogPrefix} Save decision binding enabled={enabled}");
        }

        public static void RebuildFromPersistedTerminals()
        {
            _workingState.Clear("store:rebuild:working");
            _committedState.Clear("store:rebuild:committed");
            _activeTerminals.Clear();

            int seenAnchors = 0;
            int rebuiltDatabases = 0;
            var ids = _anchors.Keys
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string id in ids)
            {
                DatabaseData best = null;

                if (_anchors.TryGetValue(id, out var registeredAnchors))
                {
                    CleanupDeadReferences(registeredAnchors);
                    foreach (var weak in registeredAnchors)
                    {
                        if (!weak.TryGetTarget(out var anchor) || anchor == null)
                        {
                            continue;
                        }

                        seenAnchors++;
                        var candidate = anchor.ReadPersistedData();
                        candidate.Items = CompactItems(CloneItemList(candidate.Items));
                        if (IsBetterCandidate(candidate, best))
                        {
                            best = candidate;
                        }
                    }
                }

                if (best == null)
                {
                    best = new DatabaseData
                    {
                        DatabaseId = id,
                        Version = 0,
                        Items = new List<ItemData>()
                    };
                }

                best.DatabaseId = id;
                if (best.Items == null)
                {
                    best.Items = new List<ItemData>();
                }
                _committedState.SetData(id, CloneDatabaseData(best), StateMutationKind.Rebuild, "store:rebuild:committed");
                rebuiltDatabases++;
            }

            RestoreWorkingFromCommitted("store:rebuild");
            SyncAllKnownDatabases();

            int totalItems = GetWorkingTotalItems();
            int committedItems = GetCommittedTotalItems();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} RebuildFromPersistedTerminals anchors={seenAnchors} " +
                $"committedDbCount={rebuiltDatabases} committedItems={committedItems} workingItems={totalItems}");
        }

        public static string SerializeData(DatabaseData data)
        {
            return DatabaseDataCodec.Serialize(data);
        }

        public static DatabaseData DeserializeData(string json, string fallbackDatabaseId)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new DatabaseData { DatabaseId = Normalize(fallbackDatabaseId) };
            }

            try
            {
                var data = DatabaseDataCodec.Deserialize(json, fallbackDatabaseId) ?? new DatabaseData();
                data.DatabaseId = Normalize(string.IsNullOrWhiteSpace(data.DatabaseId) ? fallbackDatabaseId : data.DatabaseId);
                if (data.Items == null)
                {
                    data.Items = new List<ItemData>();
                }
                return data;
            }
            catch
            {
                return new DatabaseData { DatabaseId = Normalize(fallbackDatabaseId) };
            }
        }

        private static bool TryMarkRoundDecision(string decision, string source)
        {
            string normalizedDecision = (decision ?? "").Trim().ToLowerInvariant();
            string normalizedSource = string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim();
            int incomingPriority = GetRoundDecisionPriority(normalizedDecision, normalizedSource);
            bool incomingRollback = normalizedDecision == "rollback";
            bool incomingCommit = normalizedDecision == "commit";
            bool existingRollback = _roundDecisionApplied &&
                                    _roundDecisionSource.StartsWith("rollback:", StringComparison.OrdinalIgnoreCase);
            bool existingCommit = _roundDecisionApplied &&
                                  _roundDecisionSource.StartsWith("commit:", StringComparison.OrdinalIgnoreCase);

            if (incomingRollback)
            {
                if (existingRollback)
                {
                    if (incomingPriority > _roundDecisionPriority)
                    {
                        string previous = _roundDecisionSource;
                        int previousPriority = _roundDecisionPriority;
                        _roundDecisionSource = $"{decision}:{normalizedSource}";
                        _roundDecisionPriority = incomingPriority;
                        ModFileLog.Write(
                            "Store",
                            $"{Constants.LogPrefix} Upgraded rollback decision source='{normalizedSource}' " +
                            $"priority={incomingPriority} previous='{previous}' previousPriority={previousPriority}.");
                    }
                    else
                    {
                        ModFileLog.Write(
                            "Store",
                            $"{Constants.LogPrefix} Ignored duplicate rollback decision source='{normalizedSource}' " +
                            $"priority={incomingPriority}, already='{_roundDecisionSource}' priority={_roundDecisionPriority}.");
                    }

                    return false;
                }

                if (existingCommit)
                {
                    ModFileLog.Write(
                        "Store",
                        $"{Constants.LogPrefix} Rollback overrides prior commit source='{normalizedSource}' " +
                        $"priority={incomingPriority}, previous='{_roundDecisionSource}' priority={_roundDecisionPriority}.");
                }

                // Rollback is authoritative over a prior commit when explicit no-save is observed.
                _roundDecisionApplied = true;
                _roundDecisionSource = $"{decision}:{normalizedSource}";
                _roundDecisionPriority = incomingPriority;
                return true;
            }

            if (incomingCommit)
            {
                if (existingRollback)
                {
                    ModFileLog.Write(
                        "Store",
                        $"{Constants.LogPrefix} Ignored late commit source='{normalizedSource}' priority={incomingPriority} " +
                        $"after rollback='{_roundDecisionSource}' priority={_roundDecisionPriority}.");
                    return false;
                }

                if (existingCommit)
                {
                    if (incomingPriority > _roundDecisionPriority)
                    {
                        string previous = _roundDecisionSource;
                        int previousPriority = _roundDecisionPriority;
                        _roundDecisionSource = $"{decision}:{normalizedSource}";
                        _roundDecisionPriority = incomingPriority;
                        ModFileLog.Write(
                            "Store",
                            $"{Constants.LogPrefix} Upgraded commit decision source='{normalizedSource}' " +
                            $"priority={incomingPriority} previous='{previous}' previousPriority={previousPriority}; dedup skip re-commit.");
                    }
                    else
                    {
                        ModFileLog.Write(
                            "Store",
                            $"{Constants.LogPrefix} Ignored duplicate commit decision source='{normalizedSource}' " +
                            $"priority={incomingPriority}, already='{_roundDecisionSource}' priority={_roundDecisionPriority}.");
                    }

                    return false;
                }

                _roundDecisionApplied = true;
                _roundDecisionSource = $"{decision}:{normalizedSource}";
                _roundDecisionPriority = incomingPriority;
                return true;
            }

            if (_roundDecisionApplied)
            {
                ModFileLog.Write(
                    "Store",
                    $"{Constants.LogPrefix} Ignored unknown round decision '{normalizedDecision}' source='{normalizedSource}', " +
                    $"already='{_roundDecisionSource}' priority={_roundDecisionPriority}.");
                return false;
            }

            _roundDecisionApplied = true;
            _roundDecisionSource = $"{normalizedDecision}:{normalizedSource}";
            _roundDecisionPriority = incomingPriority;
            return true;
        }

        private static int GetRoundDecisionPriority(string normalizedDecision, string source)
        {
            string key = (source ?? "").Trim();
            if (normalizedDecision == "rollback")
            {
                if (key.IndexOf("gameserver.endgame(wassaved=false)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 320;
                }
                if (key.IndexOf("hook:roundend", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 160;
                }

                return 120;
            }

            if (normalizedDecision == "commit")
            {
                if (key.IndexOf("gameserver.endgame(wassaved=true)", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 300;
                }
                if (key.IndexOf("saveutil.savegame", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 220;
                }
                if (key.IndexOf("gamesession.save", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return 180;
                }

                return 100;
            }

            return 10;
        }

        private static int ForceCloseAllActiveSessions(string reason, bool convertToClosedItem)
        {
            if (_activeTerminals.Count <= 0)
            {
                return 0;
            }

            int closed = 0;
            var touchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeSnapshot = _activeTerminals.ToList();

            foreach (var pair in activeSnapshot)
            {
                string id = Normalize(pair.Key);
                int terminalEntityId = pair.Value;
                touchedIds.Add(id);

                if (TryGetTerminalByEntityId(id, terminalEntityId, out var terminal) && terminal != null)
                {
                    try
                    {
                        bool ok = terminal.RequestForceCloseForTakeover(reason, requester: null, convertToClosedItem: convertToClosedItem);
                        if (ok)
                        {
                            closed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        ModFileLog.Write("Store", $"{Constants.LogPrefix} ForceClose session failed db='{id}' terminal={terminalEntityId}: {ex.Message}");
                    }
                }

                _activeTerminals.Remove(id);
            }

            foreach (string id in touchedIds)
            {
                SyncTerminals(id);
            }

            return closed;
        }

        private static void PromoteWorkingToCommitted(string source)
        {
            var previousCommitted = SnapshotState(_committedState);
            _committedState.Clear($"commit:promote:{source}:clear");

            foreach (var pair in _workingState.Entries)
            {
                if (pair.Value?.Data == null) { continue; }

                string id = Normalize(pair.Key);
                var committedData = CloneDatabaseData(pair.Value.Data);
                committedData.Items = CompactItems(CloneItemList(committedData.Items));

                previousCommitted.TryGetValue(id, out var previousCommittedData);
                committedData.Version = ComputeCommitVersion(previousCommittedData, committedData);

                _committedState.SetData(
                    id,
                    committedData,
                    StateMutationKind.Set,
                    $"commit:promote:{source}");

                ModFileLog.WriteDebug(
                    "Store",
                    $"{Constants.LogPrefix} PromoteWorkingToCommitted db='{id}' source='{source}' " +
                    $"workingVersion={pair.Value.Data.Version} workingItems={pair.Value.Data.ItemCount} " +
                    $"previousCommittedVersion={(previousCommittedData?.Version ?? 0)} previousCommittedItems={(previousCommittedData?.ItemCount ?? 0)} " +
                    $"nextCommittedVersion={committedData.Version} nextCommittedItems={committedData.ItemCount}");
            }
        }

        private static void PersistCommittedStateToAnchors()
        {
            var ids = _anchors.Keys
                .Concat(_committedState.Keys)
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} PersistCommittedStateToAnchors ids={ids.Count} committedDbCount={_committedState.Count} " +
                $"terminals={_terminals.Count} anchors={_anchors.Count} saveBinding={_saveDecisionBindingEnabled}");

            foreach (string id in ids)
            {
                SyncTerminals(id, persistCommittedState: true);
                if (TryGetCommittedData(id, out var committed))
                {
                    ModFileLog.WriteDebug(
                        "Store",
                        $"{Constants.LogPrefix} PersistCommittedToAnchor db='{id}' committedVersion={committed?.Version ?? 0} " +
                        $"committedItems={committed?.ItemCount ?? 0}");
                }
            }
        }

        public static void RegisterTerminal(DatabaseTerminalComponent terminal)
        {
            if (terminal == null) { return; }
            string id = Normalize(terminal.DatabaseId);
            terminal.ResolveDatabaseId(id);

            if (!_terminals.TryGetValue(id, out var registered))
            {
                registered = new List<WeakReference<DatabaseTerminalComponent>>();
                _terminals[id] = registered;
            }

            CleanupDeadReferences(registered);
            if (!registered.Any(r => r.TryGetTarget(out var t) && t == terminal))
            {
                registered.Add(new WeakReference<DatabaseTerminalComponent>(terminal));
            }

            SyncTerminals(id);

            int storeItems = TryGetWorkingData(id, out var merged) ? merged?.ItemCount ?? 0 : 0;
            int storeVersion = TryGetWorkingData(id, out var mergedVersion) ? mergedVersion?.Version ?? 0 : 0;
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} RegisterTerminal db='{id}' terminal={terminal.TerminalEntityId} " +
                $"storeVersion={storeVersion} storeItems={storeItems}");
        }

        public static void RegisterPersistenceAnchor(DatabaseStorageAnchorComponent anchor)
        {
            if (anchor == null) { return; }
            string id = Normalize(anchor.DatabaseId);
            anchor.ResolveDatabaseId(id);

            if (!_anchors.TryGetValue(id, out var registered))
            {
                registered = new List<WeakReference<DatabaseStorageAnchorComponent>>();
                _anchors[id] = registered;
            }

            CleanupDeadReferences(registered);
            if (!registered.Any(r => r.TryGetTarget(out var a) && a == anchor))
            {
                registered.Add(new WeakReference<DatabaseStorageAnchorComponent>(anchor));
            }

            var dataFromAnchor = anchor.ReadPersistedData();
            dataFromAnchor.Items = CompactItems(CloneItemList(dataFromAnchor.Items));

            if (!TryGetCommittedData(id, out var committedData))
            {
                _committedState.SetData(
                    id,
                    CloneDatabaseData(dataFromAnchor),
                    StateMutationKind.Set,
                    "anchor:register:committed:first");
            }
            else if (IsBetterCandidate(dataFromAnchor, committedData))
            {
                _committedState.SetData(
                    id,
                    CloneDatabaseData(dataFromAnchor),
                    StateMutationKind.Set,
                    "anchor:register:committed:replaceBetter");
            }

            if (!TryGetWorkingData(id, out var workingData))
            {
                _workingState.SetData(
                    id,
                    CloneDatabaseData(dataFromAnchor),
                    StateMutationKind.Set,
                    "anchor:register:working:first");
            }
            else if (IsBetterCandidate(dataFromAnchor, workingData))
            {
                _workingState.SetData(
                    id,
                    CloneDatabaseData(dataFromAnchor),
                    StateMutationKind.Set,
                    "anchor:register:working:replaceBetter");
            }

            if (_pendingRestoreArmed &&
                dataFromAnchor.ItemCount <= 0 &&
                TryGetCommittedData(id, out var restoredData) &&
                (restoredData?.ItemCount ?? 0) > 0)
            {
                anchor.ApplyStoreSnapshot(restoredData, persistSerializedState: true);
                dataFromAnchor = CloneDatabaseData(restoredData);

                ModFileLog.Write(
                    "Store",
                    $"{Constants.LogPrefix} RegisterAnchor applied pending snapshot db='{id}' anchor={anchor.AnchorEntityId} " +
                    $"version={dataFromAnchor.Version} items={dataFromAnchor.ItemCount}");
            }

            SyncTerminals(id);

            int workingItems = TryGetWorkingData(id, out var mergedWorking) ? mergedWorking?.ItemCount ?? 0 : 0;
            int committedItems = TryGetCommittedData(id, out var mergedCommitted) ? mergedCommitted?.ItemCount ?? 0 : 0;
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} RegisterAnchor db='{id}' anchor={anchor.AnchorEntityId} " +
                $"serializedVersion={dataFromAnchor.Version} serializedItems={dataFromAnchor.ItemCount} " +
                $"workingItems={workingItems} committedItems={committedItems}");
        }

        public static void UnregisterTerminal(DatabaseTerminalComponent terminal)
        {
            if (terminal == null) { return; }
            string id = Normalize(terminal.DatabaseId);
            if (!_terminals.TryGetValue(id, out var registered)) { return; }

            registered.RemoveAll(r => !r.TryGetTarget(out var t) || t == terminal);
            if (registered.Count == 0)
            {
                _terminals.Remove(id);
            }
        }

        public static void UnregisterPersistenceAnchor(DatabaseStorageAnchorComponent anchor)
        {
            if (anchor == null) { return; }
            string id = Normalize(anchor.DatabaseId);
            if (!_anchors.TryGetValue(id, out var registered)) { return; }

            registered.RemoveAll(r => !r.TryGetTarget(out var a) || a == anchor);
            if (registered.Count == 0)
            {
                _anchors.Remove(id);
            }
        }

        public static bool TryAcquireTerminal(string databaseId, int terminalEntityId)
        {
            string id = Normalize(databaseId);
            if (_activeTerminals.TryGetValue(id, out int active))
            {
                if (!IsKnownTerminalId(id, active))
                {
                    _activeTerminals[id] = terminalEntityId;
                    SyncTerminals(id);
                    return true;
                }
                return active == terminalEntityId;
            }
            _activeTerminals[id] = terminalEntityId;
            return true;
        }

        public static bool ReleaseTerminal(string databaseId, int terminalEntityId)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int active)) { return true; }
            if (active != terminalEntityId) { return false; }
            _activeTerminals.Remove(id);
            SyncTerminals(id);
            return true;
        }

        public static bool TransferTerminalLock(string databaseId, int fromTerminalEntityId, int toTerminalEntityId)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int active)) { return false; }
            if (active != fromTerminalEntityId) { return false; }
            _activeTerminals[id] = toTerminalEntityId;
            SyncTerminals(id);
            return true;
        }

        public static bool TryForceCloseActiveSession(string databaseId, int requesterTerminalEntityId, Character requester)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int activeEntityId))
            {
                return true;
            }

            if (activeEntityId == requesterTerminalEntityId)
            {
                return true;
            }

            if (!TryGetTerminalByEntityId(id, activeEntityId, out var activeTerminal))
            {
                _activeTerminals.Remove(id);
                SyncTerminals(id);
                return true;
            }

            return activeTerminal.RequestForceCloseForTakeover(
                $"takeover requested by terminal {requesterTerminalEntityId}",
                requester);
        }

        public static bool IsLocked(string databaseId)
        {
            string id = Normalize(databaseId);
            return _activeTerminals.ContainsKey(id);
        }

        public static int GetItemCount(string databaseId)
        {
            string id = Normalize(databaseId);
            return GetOrCreate(id).ItemCount;
        }

        public static void AppendItems(string databaseId, List<ItemData> items)
        {
            if (items == null || items.Count == 0) { return; }
            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            var merged = CloneItemList(db.Items);
            merged.AddRange(CloneItemList(items));
            db.Items = CompactItems(merged);
            db.Version++;
            _workingState.RecordMutation(id, StateMutationKind.Merge, "api:appendItems", beforeVersion, beforeCount);
            if (TryBuildMutationDelta(id, "api:appendItems", beforeVersion, beforeAmounts, db, out var delta))
            {
                PublishDelta(delta);
            }
            SyncTerminals(id, preferDelta: true);
        }

        public static List<ItemData> TakeAllForTerminalSession(string databaseId, int terminalEntityId)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int active) || active != terminalEntityId)
            {
                return new List<ItemData>();
            }

            var db = GetOrCreate(id);
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            var copy = CloneItemList(db.Items);
            db.Items.Clear();
            db.Version++;
            _workingState.RecordMutation(id, StateMutationKind.Extract, "api:takeAllForSession", beforeVersion, beforeCount);
            if (TryBuildMutationDelta(id, "api:takeAllForSession", beforeVersion, beforeAmounts, db, out var delta))
            {
                PublishDelta(delta);
            }
            SyncTerminals(id, preferDelta: true);
            return copy;
        }

        public static bool WriteBackFromTerminalContainer(string databaseId, List<ItemData> items, int terminalEntityId)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int active) || active != terminalEntityId)
            {
                return false;
            }

            var db = GetOrCreate(id);
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            // Merge instead of overwrite:
            // while a terminal session is open, interfaces can still ingest into the same database.
            // those ingests live in db.Items and must not be lost when the session writes back.
            var merged = CloneItemList(db.Items);
            var writeBack = CloneItemList(items ?? new List<ItemData>());
            merged.AddRange(writeBack);
            db.Items = CompactItems(merged);
            db.Version++;
            _workingState.RecordMutation(id, StateMutationKind.Merge, "api:writeBackFromTerminal", beforeVersion, beforeCount);
            if (TryBuildMutationDelta(id, "api:writeBackFromTerminal", beforeVersion, beforeAmounts, db, out var delta))
            {
                PublishDelta(delta);
            }
            SyncTerminals(id, preferDelta: true);
            return true;
        }

        public static bool TryTakeOneByIdentifier(string databaseId, string identifier, out ItemData taken)
        {
            taken = null;
            if (string.IsNullOrWhiteSpace(identifier)) { return false; }
            string target = identifier.Trim();
            return TryTakeBestMatching(databaseId, data =>
                data != null &&
                string.Equals(data.Identifier, target, StringComparison.OrdinalIgnoreCase),
                out taken);
        }

        public static bool TryTakeOneByIdentifierForAutomation(
            string databaseId,
            string identifier,
            out ItemData taken,
            TakePolicy policy = TakePolicy.HighestConditionFirst)
        {
            taken = null;
            if (string.IsNullOrWhiteSpace(identifier)) { return false; }
            string target = identifier.Trim();

            bool ok = TryTakeItemsForAutomation(
                databaseId,
                data => data != null && string.Equals(data.Identifier, target, StringComparison.OrdinalIgnoreCase),
                1,
                out var items,
                policy);

            if (!ok || items.Count == 0) { return false; }
            taken = items[0];
            return true;
        }

        public static bool TryTakeOneMatching(string databaseId, Func<ItemData, bool> predicate, out ItemData taken)
        {
            return TryTakeBestMatching(databaseId, predicate, out taken);
        }

        public static bool TryTakeBestMatching(string databaseId, Func<ItemData, bool> predicate, out ItemData taken)
        {
            taken = null;
            if (predicate == null) { return false; }
            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            if (db.Items == null || db.Items.Count == 0) { return false; }

            int bestIndex = -1;
            float bestCondition = float.MinValue;
            int bestQuality = int.MinValue;

            for (int i = 0; i < db.Items.Count; i++)
            {
                var entry = db.Items[i];
                if (entry == null) { continue; }
                if (!predicate(entry)) { continue; }

                float condition = entry.Condition;
                int quality = entry.Quality;
                if (condition > bestCondition || (System.Math.Abs(condition - bestCondition) < 0.001f && quality > bestQuality))
                {
                    bestIndex = i;
                    bestCondition = condition;
                    bestQuality = quality;
                }
            }

            if (bestIndex < 0) { return false; }

            var bestEntry = db.Items[bestIndex];
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            if (bestEntry.ContainedItems != null && bestEntry.ContainedItems.Count > 0)
            {
                taken = bestEntry.Clone();
                db.Items.RemoveAt(bestIndex);
            }
            else if (bestEntry.StackSize <= 1)
            {
                taken = bestEntry.Clone();
                db.Items.RemoveAt(bestIndex);
            }
            else
            {
                taken = ExtractSingleFromStack(bestEntry);
                if (bestEntry.StackSize <= 0)
                {
                    db.Items.RemoveAt(bestIndex);
                }
            }

            db.Version++;
            _workingState.RecordMutation(id, StateMutationKind.Extract, "api:takeBestMatching", beforeVersion, beforeCount);
            if (TryBuildMutationDelta(id, "api:takeBestMatching", beforeVersion, beforeAmounts, db, out var delta))
            {
                PublishDelta(delta);
            }
            SyncTerminals(id, preferDelta: true);
            return true;
        }

        public static bool TryTakeItems(string databaseId, Func<ItemData, bool> predicate, int amount, out List<ItemData> taken)
        {
            taken = new List<ItemData>();
            if (predicate == null) { return false; }
            if (amount <= 0) { return true; }

            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            if (db.Items == null || db.Items.Count == 0) { return false; }

            int available = CountMatching(db.Items, predicate);
            if (available < amount) { return false; }
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            int remaining = TakeMatchingFromList(db.Items, predicate, amount, taken);
            if (remaining > 0) { return false; }

            db.Version++;
            _workingState.RecordMutation(id, StateMutationKind.Extract, "api:takeItems", beforeVersion, beforeCount);
            if (TryBuildMutationDelta(id, "api:takeItems", beforeVersion, beforeAmounts, db, out var delta))
            {
                PublishDelta(delta);
            }
            SyncTerminals(id, preferDelta: true);
            return true;
        }

        public static bool TryTakeItemsForAutomation(
            string databaseId,
            Func<ItemData, bool> predicate,
            int amount,
            out List<ItemData> taken,
            TakePolicy policy = TakePolicy.Fifo)
        {
            taken = new List<ItemData>();
            if (predicate == null) { return false; }
            if (amount <= 0) { return true; }

            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            int availableStore = CountMatching(db.Items, predicate);
            int availableSession = 0;
            if (TryGetActiveTerminal(id, out var activeTerminal))
            {
                availableSession = activeTerminal.CountTakeableForAutomation(predicate);
            }

            if (availableStore + availableSession < amount)
            {
                return false;
            }

            int remaining = amount;
            bool storeChanged = false;
            int beforeVersion = db.Version;
            int beforeCount = db.ItemCount;
            var beforeAmounts = BuildIdentifierAmountMap(db.Items);
            if (availableStore > 0)
            {
                int fromStore = Math.Min(availableStore, remaining);
                remaining = TakeMatchingFromList(db.Items, predicate, fromStore, taken, policy);
                storeChanged = fromStore > 0;
            }

            if (remaining > 0)
            {
                if (!TryGetActiveTerminal(id, out var activeTerminalNow) ||
                    !activeTerminalNow.TryTakeItemsFromNonCurrentPagesForAutomation(predicate, remaining, policy, out var sessionTaken))
                {
                    if (taken.Count > 0)
                    {
                        var rollback = CloneItemList(db.Items);
                        rollback.AddRange(CloneItemList(taken));
                        db.Items = CompactItems(rollback);
                    }
                    return false;
                }

                taken.AddRange(sessionTaken);
                remaining -= CountFlatItems(sessionTaken);
            }

            if (remaining > 0)
            {
                if (taken.Count > 0)
                {
                    var rollback = CloneItemList(db.Items);
                    rollback.AddRange(CloneItemList(taken));
                    db.Items = CompactItems(rollback);
                }
                return false;
            }

            if (storeChanged)
            {
                db.Version++;
                _workingState.RecordMutation(id, StateMutationKind.Extract, "api:takeItemsForAutomation", beforeVersion, beforeCount);
                if (TryBuildMutationDelta(id, "api:takeItemsForAutomation", beforeVersion, beforeAmounts, db, out var delta))
                {
                    PublishDelta(delta);
                }
                SyncTerminals(id, preferDelta: true);
            }

            return true;
        }

        public static List<string> GetIdentifierSnapshot(string databaseId, out int version)
        {
            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            version = db.Version;
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (db.Items != null)
            {
                foreach (var item in db.Items)
                {
                    if (item == null) { continue; }
                    if (string.IsNullOrWhiteSpace(item.Identifier)) { continue; }
                    set.Add(item.Identifier);
                }
            }

            var list = set.ToList();
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        public static List<ItemData> CompactSnapshot(List<ItemData> items)
        {
            return CompactItems(CloneItemList(items ?? new List<ItemData>()));
        }

        public static DatabaseTerminalComponent FindRegisteredTerminal(int terminalEntityId)
        {
            if (terminalEntityId <= 0) { return null; }

            foreach (var pair in _terminals)
            {
                if (TryGetTerminalByEntityId(pair.Key, terminalEntityId, out var terminal) &&
                    terminal != null)
                {
                    return terminal;
                }
            }

            return null;
        }

        // LuaCs cannot reliably call instance methods on custom component userdata in this runtime.
        // These static helpers expose terminal session operations through DatabaseStore for Lua B1.
        public static bool IsTerminalSessionOpenForLua(int terminalEntityId)
        {
            var terminal = FindRegisteredTerminal(terminalEntityId);
            return terminal != null && terminal.IsVirtualSessionOpenForUi();
        }

        public static string GetTerminalDatabaseIdForLua(int terminalEntityId)
        {
            var terminal = FindRegisteredTerminal(terminalEntityId);
            return Normalize(terminal?.DatabaseId ?? Constants.DefaultDatabaseId);
        }

        public static List<DatabaseTerminalComponent.TerminalVirtualEntry> GetTerminalVirtualSnapshotForLua(
            int terminalEntityId,
            bool refreshCurrentPage = true)
        {
            var terminal = FindRegisteredTerminal(terminalEntityId);
            if (terminal == null)
            {
                return new List<DatabaseTerminalComponent.TerminalVirtualEntry>();
            }

            var rows = terminal.GetVirtualViewSnapshot(refreshCurrentPage);
            return rows ?? new List<DatabaseTerminalComponent.TerminalVirtualEntry>();
        }

        public static string TryTakeOneByIdentifierFromTerminalSessionForLua(
            int terminalEntityId,
            string identifier,
            Character actor)
        {
            var terminal = FindRegisteredTerminal(terminalEntityId);
            if (terminal == null)
            {
                return "terminal_missing";
            }

            return terminal.TryTakeOneByIdentifierFromVirtualSession(identifier, actor) ?? "";
        }

        private static bool IsBetterCandidate(DatabaseData candidate, DatabaseData baseline)
        {
            if (candidate == null) { return false; }
            if (baseline == null) { return true; }

            if (candidate.Version != baseline.Version)
            {
                return candidate.Version > baseline.Version;
            }

            return candidate.ItemCount > baseline.ItemCount;
        }

        private static bool TryGetWorkingData(string databaseId, out DatabaseData data)
        {
            return _workingState.TryGetData(databaseId, out data);
        }

        private static bool TryGetCommittedData(string databaseId, out DatabaseData data)
        {
            return _committedState.TryGetData(databaseId, out data);
        }

        private static int GetWorkingTotalItems()
        {
            return _workingState.Values.Sum(db => db?.ItemCount ?? 0);
        }

        private static int GetCommittedTotalItems()
        {
            return _committedState.Values.Sum(db => db?.ItemCount ?? 0);
        }

        private static bool TryBuildMutationDelta(
            string databaseId,
            string source,
            int beforeVersion,
            Dictionary<string, int> beforeAmounts,
            DatabaseData afterData,
            out DeltaPacket delta)
        {
            delta = null;
            if (afterData == null)
            {
                return false;
            }

            string id = Normalize(databaseId);
            var afterAmounts = BuildIdentifierAmountMap(afterData.Items);
            var changes = BuildDeltaChanges(beforeAmounts, afterAmounts);
            if (changes.Count <= 0 && afterData.Version == beforeVersion)
            {
                return false;
            }

            delta = new DeltaPacket(
                id,
                beforeVersion,
                afterData.Version,
                source,
                isSnapshot: false,
                totalAmount: afterData.ItemCount,
                changes: changes);
            return true;
        }

        private static void PublishDelta(DeltaPacket delta)
        {
            if (delta == null) { return; }
            string id = Normalize(delta.DatabaseId);
            _latestDeltaByDatabase[id] = delta;
            int watchAllCount = _watchAll.TryGetValue(id, out var allMap) ? allMap.Count : 0;
            int watchByKeyCount = _watchByKey.TryGetValue(id, out var byKeyMap) ? byKeyMap.Count : 0;
            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} DeltaEmit db='{id}' source='{delta.Source}' prevVersion={delta.PreviousVersion} " +
                $"version={delta.Version} total={delta.TotalAmount} changedKeys={delta.Changes.Count} " +
                $"watchAll={watchAllCount} watchByKey={watchByKeyCount}");
            DispatchDeltaToWatchers(id, delta);
        }

        private static void DispatchDeltaToWatchers(string databaseId, DeltaPacket delta)
        {
            if (delta == null) { return; }
            string id = Normalize(databaseId);

            if (_watchAll.TryGetValue(id, out var watchAllMap))
            {
                foreach (var sub in watchAllMap.Values.ToList())
                {
                    DispatchDeltaToWatcher(sub, delta, keyScoped: false);
                }
            }

            if (_watchByKey.TryGetValue(id, out var watchByKeyMap))
            {
                foreach (var sub in watchByKeyMap.Values.ToList())
                {
                    DispatchDeltaToWatcher(sub, delta, keyScoped: true);
                }
            }
        }

        private static void DispatchDeltaToWatcher(WatchSubscription sub, DeltaPacket delta, bool keyScoped)
        {
            if (sub?.Callback == null || delta == null)
            {
                return;
            }

            bool hasGap = sub.LastSeenVersion >= 0 && delta.PreviousVersion != sub.LastSeenVersion;
            if (hasGap)
            {
                ModFileLog.WriteDebug(
                    "Store",
                    $"{Constants.LogPrefix} DeltaGap watcher='{sub.WatcherId}' db='{sub.DatabaseId}' " +
                    $"lastSeen={sub.LastSeenVersion} deltaPrev={delta.PreviousVersion} deltaVersion={delta.Version} source='{delta.Source}'");
                if (TryBuildSnapshotPacket(
                        delta.DatabaseId,
                        $"watch:gap:{delta.Source}",
                        sub.LastSeenVersion,
                        keyScoped ? sub.Keys : null,
                        out var snapshot))
                {
                    TryInvokeWatcher(sub, snapshot);
                }
                return;
            }

            if (!keyScoped)
            {
                TryInvokeWatcher(sub, delta);
                return;
            }

            var keySet = sub.Keys;
            if (keySet == null || keySet.Count <= 0)
            {
                sub.LastSeenVersion = delta.Version;
                return;
            }

            var filteredChanges = new List<DeltaChange>();
            foreach (var change in delta.Changes)
            {
                if (string.IsNullOrWhiteSpace(change.Key)) { continue; }
                if (!keySet.Contains(change.Key)) { continue; }
                filteredChanges.Add(change);
            }

            if (filteredChanges.Count <= 0)
            {
                sub.LastSeenVersion = delta.Version;
                return;
            }

            var filtered = new DeltaPacket(
                delta.DatabaseId,
                delta.PreviousVersion,
                delta.Version,
                delta.Source,
                isSnapshot: false,
                totalAmount: delta.TotalAmount,
                changes: filteredChanges);
            TryInvokeWatcher(sub, filtered);
        }

        private static void TryInvokeWatcher(WatchSubscription sub, DeltaPacket packet)
        {
            if (sub?.Callback == null || packet == null)
            {
                return;
            }

            try
            {
                sub.Callback(packet);
            }
            catch (Exception ex)
            {
                ModFileLog.Write(
                    "Store",
                    $"{Constants.LogPrefix} Watch callback failed watcher='{sub.WatcherId}' db='{sub.DatabaseId}': {ex.Message}");
            }
            finally
            {
                sub.LastSeenVersion = packet.Version;
            }
        }

        private static bool TryBuildSnapshotPacket(
            string databaseId,
            string source,
            int previousVersion,
            HashSet<string> keyFilter,
            out DeltaPacket snapshot)
        {
            snapshot = null;
            string id = Normalize(databaseId);
            var db = GetOrCreate(id);
            var allAmounts = BuildIdentifierAmountMap(db.Items);
            var filtered = FilterIdentifierAmounts(allAmounts, keyFilter);
            if (keyFilter != null && filtered.Count <= 0)
            {
                snapshot = new DeltaPacket(
                    id,
                    previousVersion < 0 ? db.Version : previousVersion,
                    db.Version,
                    source,
                    isSnapshot: true,
                    totalAmount: 0,
                    changes: new List<DeltaChange>(),
                    snapshotAmounts: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
                return true;
            }

            var changes = filtered
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new DeltaChange(pair.Key, 0, pair.Value))
                .ToList();

            int totalAmount = keyFilter == null ? db.ItemCount : filtered.Values.Sum();
            snapshot = new DeltaPacket(
                id,
                previousVersion < 0 ? db.Version : previousVersion,
                db.Version,
                source,
                isSnapshot: true,
                totalAmount: totalAmount,
                changes: changes,
                snapshotAmounts: filtered);
            return true;
        }

        private static bool TryGetLatestDelta(string databaseId, int expectedVersion, out DeltaPacket delta)
        {
            string id = Normalize(databaseId);
            if (_latestDeltaByDatabase.TryGetValue(id, out delta) &&
                delta != null &&
                delta.Version == expectedVersion)
            {
                return true;
            }

            delta = null;
            return false;
        }

        private static Dictionary<string, int> BuildIdentifierAmountMap(List<ItemData> items)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (items == null)
            {
                return map;
            }

            foreach (var item in items)
            {
                AccumulateIdentifierAmount(map, item, parentMultiplier: 1);
            }

            return map;
        }

        private static void AccumulateIdentifierAmount(Dictionary<string, int> map, ItemData item, int parentMultiplier)
        {
            if (map == null || item == null) { return; }

            int stack = Math.Max(1, item.StackSize);
            int multiplier = Math.Max(1, parentMultiplier);
            int amount = stack * multiplier;

            string key = (item.Identifier ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(key))
            {
                map[key] = map.TryGetValue(key, out int existing)
                    ? existing + amount
                    : amount;
            }

            if (item.ContainedItems == null || item.ContainedItems.Count <= 0)
            {
                return;
            }

            foreach (var child in item.ContainedItems)
            {
                AccumulateIdentifierAmount(map, child, amount);
            }
        }

        private static List<DeltaChange> BuildDeltaChanges(
            Dictionary<string, int> before,
            Dictionary<string, int> after)
        {
            before ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            after ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var keys = new HashSet<string>(before.Keys, StringComparer.OrdinalIgnoreCase);
            keys.UnionWith(after.Keys);

            var changes = new List<DeltaChange>();
            foreach (string key in keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
            {
                int oldAmount = before.TryGetValue(key, out int oldValue) ? oldValue : 0;
                int newAmount = after.TryGetValue(key, out int newValue) ? newValue : 0;
                if (oldAmount == newAmount) { continue; }
                changes.Add(new DeltaChange(key, oldAmount, newAmount));
            }

            return changes;
        }

        private static Dictionary<string, int> FilterIdentifierAmounts(
            Dictionary<string, int> source,
            HashSet<string> keyFilter)
        {
            source ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (keyFilter == null || keyFilter.Count <= 0)
            {
                return new Dictionary<string, int>(source, StringComparer.OrdinalIgnoreCase);
            }

            var filtered = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in keyFilter)
            {
                if (string.IsNullOrWhiteSpace(key)) { continue; }
                if (!source.TryGetValue(key, out int value)) { continue; }
                if (value <= 0) { continue; }
                filtered[key] = value;
            }

            return filtered;
        }

        private static DatabaseData GetOrCreate(string databaseId)
        {
            string id = Normalize(databaseId);
            return _workingState.GetOrCreateData(id, "store:getOrCreate:working");
        }

        private static Dictionary<string, DatabaseData> SnapshotState(DatabaseState state)
        {
            var snapshot = new Dictionary<string, DatabaseData>(StringComparer.OrdinalIgnoreCase);
            if (state == null) { return snapshot; }

            foreach (var pair in state.Entries)
            {
                if (pair.Value?.Data == null) { continue; }
                snapshot[pair.Key] = CloneDatabaseData(pair.Value.Data);
            }

            return snapshot;
        }

        private static void RestoreWorkingFromCommitted(string source)
        {
            _workingState.Clear($"working:restore:{source}:clear");
            foreach (var pair in _committedState.Entries)
            {
                if (pair.Value?.Data == null) { continue; }
                _workingState.SetData(
                    pair.Key,
                    CloneDatabaseData(pair.Value.Data),
                    StateMutationKind.Restore,
                    $"working:restore:{source}");
            }

            ModFileLog.WriteDebug(
                "Store",
                $"{Constants.LogPrefix} RestoreWorkingFromCommitted source='{source}' committedDbCount={_committedState.Count} " +
                $"committedItems={GetCommittedTotalItems()} workingDbCount={_workingState.Count} workingItems={GetWorkingTotalItems()}");
        }

        private static void SyncAllKnownDatabases(bool persistCommittedState = false)
        {
            var ids = _workingState.Keys
                .Concat(_committedState.Keys)
                .Concat(_anchors.Keys)
                .Concat(_terminals.Keys)
                .Select(Normalize)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string id in ids)
            {
                SyncTerminals(id, persistCommittedState);
            }
        }

        private static int ComputeCommitVersion(DatabaseData previousCommitted, DatabaseData candidate)
        {
            int previousVersion = Math.Max(0, previousCommitted?.Version ?? 0);
            int candidateVersion = Math.Max(0, candidate?.Version ?? 0);
            if (previousCommitted == null)
            {
                return candidateVersion;
            }

            bool payloadChanged = !IsPayloadEquivalent(previousCommitted, candidate);
            if (!payloadChanged)
            {
                return Math.Max(previousVersion, candidateVersion);
            }

            return Math.Max(candidateVersion, previousVersion + 1);
        }

        private static bool IsPayloadEquivalent(DatabaseData left, DatabaseData right)
        {
            if (left == null && right == null) { return true; }
            if (left == null || right == null) { return false; }

            var leftSnapshot = CloneDatabaseData(left);
            var rightSnapshot = CloneDatabaseData(right);
            leftSnapshot.Version = 0;
            rightSnapshot.Version = 0;
            string leftEncoded = SerializeData(leftSnapshot);
            string rightEncoded = SerializeData(rightSnapshot);
            return string.Equals(leftEncoded, rightEncoded, StringComparison.Ordinal);
        }

        private static void CapturePendingCommitSnapshot()
        {
            _pendingCommittedSnapshot.Clear();
            foreach (var pair in _committedState.Entries)
            {
                if (pair.Value?.Data == null) { continue; }
                _pendingCommittedSnapshot[pair.Key] = CloneDatabaseData(pair.Value.Data);
            }

            _pendingRestoreArmed = _pendingCommittedSnapshot.Count > 0;
            int pendingItems = _pendingCommittedSnapshot.Values.Sum(db => db?.ItemCount ?? 0);
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} Captured pending commit snapshot dbCount={_pendingCommittedSnapshot.Count} totalItems={pendingItems}");
        }

        private static void TryRestoreFromPendingCommitSnapshot()
        {
            if (!_pendingRestoreArmed || _pendingCommittedSnapshot.Count <= 0)
            {
                return;
            }

            int currentCommittedItems = GetCommittedTotalItems();
            if (currentCommittedItems > 0)
            {
                // Keep last committed snapshot available for future no-save rollback.
                return;
            }

            _committedState.Clear("snapshot:restore:committed:clear");
            foreach (var pair in _pendingCommittedSnapshot)
            {
                _committedState.SetData(
                    pair.Key,
                    CloneDatabaseData(pair.Value),
                    StateMutationKind.Restore,
                    "snapshot:restore:committed:apply");
            }

            RestoreWorkingFromCommitted("snapshot:restore");
            SyncAllKnownDatabases(persistCommittedState: true);

            int restoredItems = GetWorkingTotalItems();
            int restoredCommittedItems = GetCommittedTotalItems();
            ModFileLog.Write(
                "Store",
                $"{Constants.LogPrefix} Restored pending commit snapshot workingDbCount={_workingState.Count} " +
                $"workingItems={restoredItems} committedDbCount={_committedState.Count} committedItems={restoredCommittedItems}");
        }

        private static void SyncTerminals(string databaseId, bool persistCommittedState = false, bool preferDelta = false)
        {
            long perfStartTicks = 0;
            if (ModFileLog.IsDebugEnabled)
            {
                perfStartTicks = Stopwatch.GetTimestamp();
            }

            string id = Normalize(databaseId);
            var workingData = GetOrCreate(id);
            DeltaPacket liveDelta = null;
            bool hasLiveDelta = !persistCommittedState &&
                                preferDelta &&
                                TryGetLatestDelta(id, workingData.Version, out liveDelta);
            int terminalApplyCount = 0;
            int anchorApplyCount = 0;

            if (_terminals.TryGetValue(id, out var registered))
            {
                CleanupDeadReferences(registered);
                foreach (var weak in registered)
                {
                    if (weak.TryGetTarget(out var terminal))
                    {
                        if (hasLiveDelta && terminal.ApplyStoreDelta(liveDelta))
                        {
                            terminalApplyCount++;
                            continue;
                        }

                        // Terminal serialization is no longer used for persistence.
                        terminal.ApplyStoreSnapshot(workingData, persistSerializedState: false);
                        terminalApplyCount++;
                    }
                }
            }

            if (_anchors.TryGetValue(id, out var anchors))
            {
                DatabaseData anchorData = workingData;
                bool persistSerialized = false;
                if (persistCommittedState && TryGetCommittedData(id, out var committedData) && committedData != null)
                {
                    anchorData = committedData;
                    persistSerialized = true;
                }

                if (persistCommittedState)
                {
                    ModFileLog.WriteDebug(
                        "Store",
                        $"{Constants.LogPrefix} SyncTerminals persistCommitted db='{id}' workingVersion={workingData.Version} " +
                        $"workingItems={workingData.ItemCount} committedVersion={anchorData.Version} committedItems={anchorData.ItemCount}");
                }

                CleanupDeadReferences(anchors);
                foreach (var weak in anchors)
                {
                    if (weak.TryGetTarget(out var anchor))
                    {
                        anchor.ApplyStoreSnapshot(anchorData, persistSerializedState: persistSerialized);
                        anchorApplyCount++;
                    }
                }
            }

            if (perfStartTicks != 0)
            {
                double elapsedMs = (Stopwatch.GetTimestamp() - perfStartTicks) * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs >= 6.0 && Timing.TotalTime >= _nextSyncPerfLogAt)
                {
                    _nextSyncPerfLogAt = Timing.TotalTime + 0.8;
                    int watchAllCount = _watchAll.TryGetValue(id, out var watchAllMap) ? watchAllMap.Count : 0;
                    int watchByKeyCount = _watchByKey.TryGetValue(id, out var watchByKeyMap) ? watchByKeyMap.Count : 0;
                    ModFileLog.Write(
                        "Perf",
                        $"{Constants.LogPrefix} SyncTerminalsSlow db='{id}' ms={elapsedMs:0.###} " +
                        $"terminalApply={terminalApplyCount} anchorApply={anchorApplyCount} " +
                        $"preferDelta={preferDelta} persistCommitted={persistCommittedState} hasLiveDelta={hasLiveDelta} " +
                        $"workingVersion={workingData.Version} workingItems={workingData.ItemCount} " +
                        $"activeLocks={_activeTerminals.Count} watchAll={watchAllCount} watchByKey={watchByKeyCount}");
                }
            }
        }

        private static bool TryGetActiveTerminal(string databaseId, out DatabaseTerminalComponent terminal)
        {
            terminal = null;
            if (!_activeTerminals.TryGetValue(databaseId, out int terminalEntityId))
            {
                return false;
            }
            return TryGetTerminalByEntityId(databaseId, terminalEntityId, out terminal);
        }

        private static void CleanupDeadReferences(List<WeakReference<DatabaseTerminalComponent>> refsList)
        {
            refsList.RemoveAll(r => !r.TryGetTarget(out _));
        }

        private static void CleanupDeadReferences(List<WeakReference<DatabaseStorageAnchorComponent>> refsList)
        {
            refsList.RemoveAll(r => !r.TryGetTarget(out _));
        }

        private static bool IsKnownTerminalId(string databaseId, int terminalEntityId)
        {
            if (!_terminals.TryGetValue(databaseId, out var registered))
            {
                return false;
            }

            CleanupDeadReferences(registered);
            foreach (var weak in registered)
            {
                if (weak.TryGetTarget(out var terminal) && terminal?.TerminalEntityId == terminalEntityId)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetTerminalByEntityId(string databaseId, int terminalEntityId, out DatabaseTerminalComponent terminal)
        {
            terminal = null;
            if (!_terminals.TryGetValue(databaseId, out var registered))
            {
                return false;
            }

            CleanupDeadReferences(registered);
            foreach (var weak in registered)
            {
                if (weak.TryGetTarget(out var candidate) && candidate?.TerminalEntityId == terminalEntityId)
                {
                    terminal = candidate;
                    return true;
                }
            }

            return false;
        }

        private static List<ItemData> CloneItemList(List<ItemData> items)
        {
            var list = new List<ItemData>();
            if (items == null) { return list; }
            foreach (var item in items)
            {
                list.Add(item?.Clone());
            }
            return list;
        }

        private static DatabaseData CloneDatabaseData(DatabaseData source)
        {
            if (source == null)
            {
                return new DatabaseData
                {
                    DatabaseId = Constants.DefaultDatabaseId,
                    Version = 0,
                    Items = new List<ItemData>()
                };
            }

            return new DatabaseData
            {
                DatabaseId = Normalize(source.DatabaseId),
                Version = source.Version,
                Items = CloneItemList(source.Items)
            };
        }

        private static List<ItemData> CompactItems(List<ItemData> items)
        {
            var result = new List<ItemData>();
            var stackableItems = new Dictionary<string, ItemData>(StringComparer.OrdinalIgnoreCase);
            if (items == null) { return result; }

            foreach (var item in items.Where(i => i != null))
            {
                if (CanStack(item))
                {
                    string key = $"{item.Identifier}_{item.Quality}";
                    if (stackableItems.TryGetValue(key, out var existing))
                    {
                        existing.StackSize += Math.Max(1, item.StackSize);
                        if (item.StolenFlags != null && item.StolenFlags.Count > 0)
                        {
                            existing.StolenFlags.AddRange(item.StolenFlags);
                        }
                        if (item.OriginalOutposts != null && item.OriginalOutposts.Count > 0)
                        {
                            existing.OriginalOutposts.AddRange(item.OriginalOutposts);
                        }
                        if (item.SlotIndices != null && item.SlotIndices.Count > 0)
                        {
                            existing.SlotIndices.AddRange(item.SlotIndices);
                        }
                    }
                    else
                    {
                        stackableItems[key] = item.Clone();
                    }
                }
                else
                {
                    result.Add(item.Clone());
                }
            }

            result.AddRange(stackableItems.Values);
            return ItemSerializer.NormalizeStackEntries(result);
        }

        private static ItemData ExtractSingleFromStack(ItemData source)
        {
            if (source == null) { return null; }
            var single = source.Clone();
            single.StackSize = 1;
            single.StolenFlags = SliceBool(source.StolenFlags, 0, 1, false);
            single.OriginalOutposts = SliceString(source.OriginalOutposts, 0, 1, "");
            single.SlotIndices = SliceInt(source.SlotIndices, 0, 1, -1);

            source.StackSize = Math.Max(0, source.StackSize - 1);
            RemoveRangeSafe(source.StolenFlags, 1);
            RemoveRangeSafe(source.OriginalOutposts, 1);
            RemoveRangeSafe(source.SlotIndices, 1);

            return single;
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

        private static int CountMatching(IEnumerable<ItemData> items, Func<ItemData, bool> predicate)
        {
            int count = 0;
            if (items == null) { return 0; }
            foreach (var item in items)
            {
                if (item == null) { continue; }
                if (!predicate(item)) { continue; }
                count += Math.Max(1, item.StackSize);
            }
            return count;
        }

        private static int TakeMatchingFromList(List<ItemData> items, Func<ItemData, bool> predicate, int amount, List<ItemData> taken)
        {
            return TakeMatchingFromList(items, predicate, amount, taken, TakePolicy.Fifo);
        }

        private static int TakeMatchingFromList(
            List<ItemData> items,
            Func<ItemData, bool> predicate,
            int amount,
            List<ItemData> taken,
            TakePolicy policy)
        {
            int remaining = Math.Max(0, amount);
            if (items == null || predicate == null || remaining <= 0)
            {
                return remaining;
            }

            while (remaining > 0)
            {
                int index = FindCandidateIndex(items, predicate, policy);
                if (index < 0) { break; }

                var entry = items[index];

                if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
                {
                    taken?.Add(entry.Clone());
                    items.RemoveAt(index);
                    remaining--;
                    continue;
                }

                int stackSize = Math.Max(1, entry.StackSize);
                if (stackSize <= remaining)
                {
                    taken?.Add(entry.Clone());
                    remaining -= stackSize;
                    items.RemoveAt(index);
                    continue;
                }

                var part = ExtractStackPart(entry, remaining);
                if (part != null)
                {
                    taken?.Add(part);
                }
                remaining = 0;
                if (entry.StackSize <= 0)
                {
                    items.RemoveAt(index);
                }
            }

            return remaining;
        }

        private static int FindCandidateIndex(List<ItemData> items, Func<ItemData, bool> predicate, TakePolicy policy)
        {
            if (items == null || predicate == null || items.Count == 0) { return -1; }

            if (policy == TakePolicy.Fifo)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var entry = items[i];
                    if (entry == null) { continue; }
                    if (!predicate(entry)) { continue; }
                    return i;
                }
                return -1;
            }

            int bestIndex = -1;
            float bestCondition = 0f;
            int bestQuality = 0;
            const float eps = 0.0001f;

            for (int i = 0; i < items.Count; i++)
            {
                var entry = items[i];
                if (entry == null) { continue; }
                if (!predicate(entry)) { continue; }

                if (bestIndex < 0)
                {
                    bestIndex = i;
                    bestCondition = entry.Condition;
                    bestQuality = entry.Quality;
                    continue;
                }

                bool replace;
                if (policy == TakePolicy.HighestConditionFirst)
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
                    bestIndex = i;
                    bestCondition = entry.Condition;
                    bestQuality = entry.Quality;
                }
            }

            return bestIndex;
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

        private static void RemoveAtSafe<T>(List<T> list, int index)
        {
            if (list == null) { return; }
            if (index < 0 || index >= list.Count) { return; }
            list.RemoveAt(index);
        }

        private static void RemoveRangeSafe<T>(List<T> list, int count)
        {
            if (list == null) { return; }
            if (count <= 0) { return; }
            int remove = Math.Min(count, list.Count);
            if (remove <= 0) { return; }
            list.RemoveRange(0, remove);
        }

        private static bool CanStack(ItemData item)
        {
            if (item == null) { return false; }
            if (item.Condition < 99.9f) { return false; }
            if (item.ContainedItems != null && item.ContainedItems.Count > 0) { return false; }
            return true;
        }
    }
}
