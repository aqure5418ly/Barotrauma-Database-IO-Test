using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using DatabaseIOTest.Models;

namespace DatabaseIOTest.Services
{
    public static class DatabaseStore
    {
        private static readonly Dictionary<string, DatabaseData> _store = new Dictionary<string, DatabaseData>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, int> _activeTerminals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>> _terminals =
            new Dictionary<string, List<WeakReference<DatabaseTerminalComponent>>>(StringComparer.OrdinalIgnoreCase);

        public static string Normalize(string databaseId)
        {
            return string.IsNullOrWhiteSpace(databaseId) ? Constants.DefaultDatabaseId : databaseId.Trim();
        }

        public static void Clear()
        {
            _store.Clear();
            _activeTerminals.Clear();
            _terminals.Clear();
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
            single.StolenFlags = SliceBool(source.StolenFlags, 0);
            single.OriginalOutposts = SliceString(source.OriginalOutposts, 0);
            single.SlotIndices = SliceInt(source.SlotIndices, 0);

            source.StackSize = Math.Max(0, source.StackSize - 1);
            RemoveAtSafe(source.StolenFlags, 0);
            RemoveAtSafe(source.OriginalOutposts, 0);
            RemoveAtSafe(source.SlotIndices, 0);

            return single;
        }

        private static List<bool> SliceBool(List<bool> source, int index)
        {
            var result = new List<bool>(1);
            bool value = (source != null && index >= 0 && index < source.Count) ? source[index] : false;
            result.Add(value);
            return result;
        }

        private static List<string> SliceString(List<string> source, int index)
        {
            var result = new List<string>(1);
            string value = (source != null && index >= 0 && index < source.Count) ? (source[index] ?? "") : "";
            result.Add(value);
            return result;
        }

        private static List<int> SliceInt(List<int> source, int index)
        {
            var result = new List<int>(1);
            int value = (source != null && index >= 0 && index < source.Count) ? source[index] : -1;
            result.Add(value);
            return result;
        }

        private static void RemoveAtSafe<T>(List<T> list, int index)
        {
            if (list == null) { return; }
            if (index < 0 || index >= list.Count) { return; }
            list.RemoveAt(index);
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
