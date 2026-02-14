using System;
using System.Collections.Generic;
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

        private static readonly Dictionary<string, DatabaseData> _store = new Dictionary<string, DatabaseData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, DatabaseData> _committedStore = new Dictionary<string, DatabaseData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _activeTerminals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>> _terminals =
            new Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>>(StringComparer.OrdinalIgnoreCase);
        private static int _lastSessionToken = int.MinValue;
        private static bool _roundInitialized;

        public static string Normalize(string databaseId)
        {
            return string.IsNullOrWhiteSpace(databaseId) ? Constants.DefaultDatabaseId : databaseId.Trim();
        }

        public static void Clear()
        {
            _store.Clear();
            _committedStore.Clear();
            _activeTerminals.Clear();
            _terminals.Clear();
            _roundInitialized = false;
            _lastSessionToken = int.MinValue;
        }

        public static void BeginRound(string reason = "")
        {
            ForceCloseAllActiveSessions($"begin-round:{reason}");
            ClearVolatile();
            RebuildFromPersistedTerminals();

            _committedStore.Clear();
            CopyDictionary(_store, _committedStore);
            _roundInitialized = true;

            ModFileLog.Write("Store", $"{Constants.LogPrefix} BeginRound reason='{reason}' dbs={_store.Count}");
        }

        public static void CommitRound(string reason = "")
        {
            ForceCloseAllActiveSessions($"commit-round:{reason}");

            _committedStore.Clear();
            CopyDictionary(_store, _committedStore);
            PersistCommittedToTerminals();

            ModFileLog.Write("Store", $"{Constants.LogPrefix} CommitRound reason='{reason}' dbs={_committedStore.Count}");
        }

        public static void RollbackRound(string reason = "")
        {
            ForceCloseAllActiveSessions($"rollback-round:{reason}");

            _store.Clear();
            CopyDictionary(_committedStore, _store);
            _activeTerminals.Clear();
            SyncAllTerminals();

            ModFileLog.Write("Store", $"{Constants.LogPrefix} RollbackRound reason='{reason}' dbs={_store.Count}");
        }

        public static void ClearVolatile()
        {
            _store.Clear();
            _activeTerminals.Clear();
        }

        public static void RebuildFromPersistedTerminals()
        {
            foreach (var pair in _terminals)
            {
                var id = Normalize(pair.Key);
                var refsList = pair.Value;
                if (refsList == null) { continue; }

                CleanupDeadReferences(refsList);
                foreach (var weak in refsList)
                {
                    if (!weak.TryGetTarget(out var terminal) || terminal == null)
                    {
                        continue;
                    }

                    var data = DeserializeData(terminal.SerializedDatabase, id);
                    data.Items = CompactItems(CloneItemList(data.Items));
                    MergeRebuildData(id, data);
                }
            }

            SyncAllTerminals();
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

        public static void RegisterTerminal(DatabaseTerminalComponent terminal)
        {
            if (terminal == null) { return; }

            EnsureRoundInitialized();

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

            var dataFromTerminal = DeserializeData(terminal.SerializedDatabase, id);
            dataFromTerminal.Items = CompactItems(CloneItemList(dataFromTerminal.Items));
            if (!_store.TryGetValue(id, out var storeData))
            {
                _store[id] = dataFromTerminal;
            }
            else if (dataFromTerminal.Version > storeData.Version && dataFromTerminal.Items.Count > 0)
            {
                _store[id] = dataFromTerminal;
            }

            SyncTerminals(id);
        }

        private static void EnsureRoundInitialized()
        {
            int sessionToken = GameMain.GameSession?.GetHashCode() ?? 0;
            if (!_roundInitialized || sessionToken != _lastSessionToken)
            {
                _lastSessionToken = sessionToken;
                BeginRound($"session-token:{sessionToken}");
            }
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
            var merged = CloneItemList(db.Items);
            merged.AddRange(CloneItemList(items));
            db.Items = CompactItems(merged);
            db.Version++;
            SyncTerminals(id);
        }

        public static List<ItemData> TakeAllForTerminalSession(string databaseId, int terminalEntityId)
        {
            string id = Normalize(databaseId);
            if (!_activeTerminals.TryGetValue(id, out int active) || active != terminalEntityId)
            {
                return new List<ItemData>();
            }

            var db = GetOrCreate(id);
            var copy = CloneItemList(db.Items);
            db.Items.Clear();
            db.Version++;
            SyncTerminals(id);
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
            // Merge instead of overwrite:
            // while a terminal session is open, interfaces can still ingest into the same database.
            // those ingests live in db.Items and must not be lost when the session writes back.
            var merged = CloneItemList(db.Items);
            var writeBack = CloneItemList(items ?? new List<ItemData>());
            merged.AddRange(writeBack);
            db.Items = CompactItems(merged);
            db.Version++;
            SyncTerminals(id);
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
            SyncTerminals(id);
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
            int remaining = TakeMatchingFromList(db.Items, predicate, amount, taken);
            if (remaining > 0) { return false; }

            db.Version++;
            SyncTerminals(id);
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
                SyncTerminals(id);
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

        private static DatabaseData GetOrCreate(string databaseId)
        {
            string id = Normalize(databaseId);
            if (!_store.TryGetValue(id, out var data))
            {
                data = new DatabaseData { DatabaseId = id, Version = 0, Items = new List<ItemData>() };
                _store[id] = data;
            }
            if (data.Items == null)
            {
                data.Items = new List<ItemData>();
            }
            return data;
        }

        private static void SyncAllTerminals()
        {
            foreach (var key in _terminals.Keys.ToList())
            {
                SyncTerminals(key);
            }
        }

        private static void PersistCommittedToTerminals()
        {
            foreach (var pair in _committedStore)
            {
                if (!_store.ContainsKey(pair.Key))
                {
                    _store[pair.Key] = CloneDatabaseData(pair.Value);
                }
            }

            SyncAllTerminals();
        }

        private static void ForceCloseAllActiveSessions(string reason)
        {
            foreach (var kv in _activeTerminals.ToList())
            {
                if (TryGetTerminalByEntityId(kv.Key, kv.Value, out var terminal) && terminal != null)
                {
                    terminal.RequestForceCloseForTakeover(reason, null);
                }
            }

            _activeTerminals.Clear();
        }

        private static void MergeRebuildData(string databaseId, DatabaseData candidate)
        {
            if (candidate == null) { return; }

            if (!_store.TryGetValue(databaseId, out var existing))
            {
                _store[databaseId] = CloneDatabaseData(candidate);
                return;
            }

            int existingItems = existing.Items?.Count ?? 0;
            int candidateItems = candidate.Items?.Count ?? 0;
            if (candidate.Version > existing.Version ||
                (candidate.Version == existing.Version && candidateItems > existingItems))
            {
                _store[databaseId] = CloneDatabaseData(candidate);
            }
        }

        private static void CopyDictionary(Dictionary<string, DatabaseData> source, Dictionary<string, DatabaseData> target)
        {
            foreach (var pair in source)
            {
                target[pair.Key] = CloneDatabaseData(pair.Value);
            }
        }

        private static DatabaseData CloneDatabaseData(DatabaseData source)
        {
            if (source == null)
            {
                return new DatabaseData { DatabaseId = Constants.DefaultDatabaseId, Version = 0, Items = new List<ItemData>() };
            }

            return new DatabaseData
            {
                DatabaseId = Normalize(source.DatabaseId),
                Version = source.Version,
                Items = CloneItemList(source.Items)
            };
        }

        private static void SyncTerminals(string databaseId)
        {
            string id = Normalize(databaseId);
            var data = GetOrCreate(id);
            if (!_terminals.TryGetValue(id, out var registered)) { return; }

            CleanupDeadReferences(registered);
            foreach (var weak in registered)
            {
                if (weak.TryGetTarget(out var terminal))
                {
                    terminal.ApplyStoreSnapshot(data);
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
