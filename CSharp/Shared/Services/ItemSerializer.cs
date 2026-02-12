using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Models;

namespace DatabaseIOTest.Services
{
    public static class ItemSerializer
    {
        public static List<ItemData> SerializeItems(Character owner, List<Item> items)
        {
            if (items == null) { return new List<ItemData>(); }

            var result = new List<ItemData>();
            var stackableItems = new Dictionary<string, ItemData>();

            foreach (var item in items.Where(i => i?.Prefab != null))
            {
                var itemData = SerializeItem(item);
                if (CanStack(itemData))
                {
                    string key = $"{itemData.Identifier}_{itemData.Quality}";
                    if (stackableItems.TryGetValue(key, out var existing))
                    {
                        existing.StackSize += itemData.StackSize;
                        if (itemData.StolenFlags != null && itemData.StolenFlags.Count > 0)
                        {
                            existing.StolenFlags.AddRange(itemData.StolenFlags);
                        }
                        if (itemData.OriginalOutposts != null && itemData.OriginalOutposts.Count > 0)
                        {
                            existing.OriginalOutposts.AddRange(itemData.OriginalOutposts);
                        }
                        if (itemData.SlotIndices != null && itemData.SlotIndices.Count > 0)
                        {
                            existing.SlotIndices.AddRange(itemData.SlotIndices);
                        }
                    }
                    else
                    {
                        stackableItems[key] = itemData;
                    }
                }
                else
                {
                    result.Add(itemData);
                }
            }

            result.AddRange(stackableItems.Values);
            return NormalizeStackEntries(result);
        }

        public static List<ItemData> SerializeInventory(Character owner, Inventory inventory)
        {
            if (inventory == null)
            {
                return new List<ItemData>();
            }

            var items = inventory.AllItemsMod?.Where(i => i != null).ToList() ?? new List<Item>();
            return SerializeItems(owner, items);
        }

        public static int CountItems(IEnumerable<ItemData> items)
        {
            int total = 0;
            if (items == null) { return 0; }
            foreach (var item in items)
            {
                total += CountRecursive(item);
            }
            return total;
        }

        private static int CountRecursive(ItemData item)
        {
            if (item == null) { return 0; }
            int childCount = 0;
            if (item.ContainedItems != null)
            {
                foreach (var sub in item.ContainedItems)
                {
                    childCount += CountRecursive(sub);
                }
            }
            return item.StackSize * (1 + childCount);
        }

        private static ItemData SerializeItem(Item item)
        {
            var data = new ItemData
            {
                Identifier = item.Prefab.Identifier.Value,
                Condition = item.Condition,
                Quality = item.Quality,
                // LuaCs Item type in this game version does not expose runtime StackSize.
                // Stacking is reconstructed by merging equivalent serialized entries.
                StackSize = 1
            };

            bool isStolen = IsStolen(item);
            string originalOutpost = isStolen ? (item.OriginalOutpost ?? "") : "";

            int slot = -1;
            if (item.ParentInventory != null)
            {
                slot = item.ParentInventory.FindIndex(item);
            }

            for (int i = 0; i < data.StackSize; i++)
            {
                data.StolenFlags.Add(isStolen);
                data.OriginalOutposts.Add(originalOutpost);
                if (slot >= 0)
                {
                    data.SlotIndices.Add(slot);
                }
            }

            foreach (var container in item.GetComponents<ItemContainer>())
            {
                if (container?.Inventory == null) { continue; }
                foreach (var child in container.Inventory.AllItemsMod)
                {
                    if (child?.Prefab == null) { continue; }
                    if (child.Prefab.HideInMenus) { continue; }
                    data.ContainedItems.Add(SerializeItem(child));
                }
            }

            return data;
        }

        private static bool CanStack(ItemData itemData)
        {
            if (itemData == null) { return false; }
            if (itemData.Condition < 99.9f) { return false; }
            if (itemData.ContainedItems != null && itemData.ContainedItems.Count > 0) { return false; }
            return true;
        }

        public static List<ItemData> NormalizeStackEntries(IEnumerable<ItemData> items)
        {
            var normalized = new List<ItemData>();
            if (items == null) { return normalized; }

            foreach (var item in items)
            {
                normalized.AddRange(SplitOversizedStack(item));
            }

            return normalized;
        }

        private static IEnumerable<ItemData> SplitOversizedStack(ItemData item)
        {
            if (item == null) { yield break; }

            int stackSize = System.Math.Max(1, item.StackSize);
            bool hasContained = item.ContainedItems != null && item.ContainedItems.Count > 0;

            if (hasContained || stackSize <= Constants.MaxSerializedStackSize)
            {
                var copy = item.Clone();
                copy.StackSize = stackSize;
                EnsureMetadataLength(copy, stackSize);
                yield return copy;
                yield break;
            }

            int offset = 0;
            int remaining = stackSize;
            while (remaining > 0)
            {
                int chunk = System.Math.Min(Constants.MaxSerializedStackSize, remaining);
                var part = item.Clone();
                part.StackSize = chunk;
                part.StolenFlags = SliceBool(item.StolenFlags, offset, chunk, false);
                part.OriginalOutposts = SliceString(item.OriginalOutposts, offset, chunk, "");
                part.SlotIndices = SliceInt(item.SlotIndices, offset, chunk, -1);
                yield return part;

                offset += chunk;
                remaining -= chunk;
            }
        }

        private static void EnsureMetadataLength(ItemData item, int stackSize)
        {
            item.StolenFlags = SliceBool(item.StolenFlags, 0, stackSize, false);
            item.OriginalOutposts = SliceString(item.OriginalOutposts, 0, stackSize, "");
            item.SlotIndices = SliceInt(item.SlotIndices, 0, stackSize, -1);
        }

        private static List<bool> SliceBool(List<bool> source, int start, int count, bool fallback)
        {
            var result = new List<bool>(count);
            for (int i = 0; i < count; i++)
            {
                int index = start + i;
                bool value = (source != null && index >= 0 && index < source.Count) ? source[index] : fallback;
                result.Add(value);
            }
            return result;
        }

        private static List<string> SliceString(List<string> source, int start, int count, string fallback)
        {
            var result = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                int index = start + i;
                string value = (source != null && index >= 0 && index < source.Count) ? (source[index] ?? fallback) : fallback;
                result.Add(value);
            }
            return result;
        }

        private static List<int> SliceInt(List<int> source, int start, int count, int fallback)
        {
            var result = new List<int>(count);
            for (int i = 0; i < count; i++)
            {
                int index = start + i;
                int value = (source != null && index >= 0 && index < source.Count) ? source[index] : fallback;
                result.Add(value);
            }
            return result;
        }

        private static bool IsStolen(Item item)
        {
            if (!item.AllowStealing) { return true; }
            if (item.StolenDuringRound) { return true; }
            if (!string.IsNullOrEmpty(item.OriginalOutpost)) { return true; }
            return false;
        }
    }
}
