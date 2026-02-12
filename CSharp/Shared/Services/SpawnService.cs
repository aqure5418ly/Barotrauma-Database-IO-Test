using System;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using DatabaseIOTest.Models;
using Microsoft.Xna.Framework;

namespace DatabaseIOTest.Services
{
    public static class SpawnService
    {
        public static void SpawnItemsIntoInventory(List<ItemData> items, Inventory targetInventory, Character actor = null)
        {
            if (items == null || targetInventory == null) { return; }
            foreach (var itemData in items)
            {
                SpawnItemData(itemData, targetInventory, actor, stackIndex: 0);
            }
        }

        public static void RemoveItems(IEnumerable<Item> items)
        {
            if (items == null) { return; }
            foreach (var item in items)
            {
                RemoveItem(item);
            }
        }

        public static void RemoveItem(Item item)
        {
            if (item == null || item.Removed) { return; }
            Entity.Spawner?.AddItemToRemoveQueue(item);
        }

        public static void ClearInventory(Inventory inventory)
        {
            if (inventory == null) { return; }
            var items = inventory.AllItemsMod?.Where(i => i != null).ToList() ?? new List<Item>();
            foreach (var item in items)
            {
                inventory.RemoveItem(item);
                RemoveItem(item);
            }
        }

        private static void SpawnItemData(ItemData itemData, Inventory targetInventory, Character actor, int stackIndex)
        {
            if (itemData == null || targetInventory == null) { return; }
            var prefab = ItemPrefab.FindByIdentifier(itemData.Identifier.ToIdentifier()) as ItemPrefab;
            if (prefab == null)
            {
                DebugConsole.NewMessage($"{Constants.LogPrefix} Prefab not found: {itemData.Identifier}", Color.Orange);
                return;
            }

            for (int i = 0; i < Math.Max(itemData.StackSize, 1); i++)
            {
                int index = i;
                SpawnSingle(prefab, itemData, targetInventory, actor, index);
            }
        }

        private static void SpawnSingle(ItemPrefab prefab, ItemData itemData, Inventory targetInventory, Character actor, int stackIndex)
        {
            var (spawnPos, submarine) = ResolveSpawnPoint(targetInventory, actor);
            Entity.Spawner?.AddItemToSpawnQueue(prefab, spawnPos, submarine, quality: itemData.Quality, onSpawned: item =>
            {
                if (item == null || item.Removed) { return; }

                item.Condition = itemData.Condition;
                RestoreStolenState(item, itemData, stackIndex);

                TryPutItemPreserveSlot(itemData, stackIndex, item, targetInventory, actor);
                SpawnContained(itemData, item, actor);
            });
        }

        private static void SpawnContained(ItemData itemData, Item parent, Character actor)
        {
            if (itemData?.ContainedItems == null || itemData.ContainedItems.Count == 0) { return; }

            var containers = parent.GetComponents<ItemContainer>().Where(c => c?.Inventory != null).ToList();
            if (containers.Count == 0) { return; }

            foreach (var child in itemData.ContainedItems)
            {
                var childTarget = containers[0].Inventory;
                SpawnItemData(child, childTarget, actor, 0);
            }
        }

        private static void TryPutItemPreserveSlot(ItemData itemData, int stackIndex, Item item, Inventory targetInventory, Character actor)
        {
            bool placed = false;
            if (itemData.SlotIndices != null && stackIndex < itemData.SlotIndices.Count)
            {
                int targetSlot = itemData.SlotIndices[stackIndex];
                if (targetSlot >= 0)
                {
                    // Allow combining so stacked entries can stay in fewer slots.
                    placed = targetInventory.TryPutItem(item, targetSlot, false, true, actor, true, true);
                }
            }

            if (!placed)
            {
                placed = TryPutItemAnywhere(targetInventory, item, actor);
            }

            if (!placed)
            {
                item.Drop(actor);
            }
        }

        private static bool TryPutItemAnywhere(Inventory targetInventory, Item item, Character actor)
        {
            if (targetInventory == null || item == null) { return false; }

            for (int slot = 0; slot < targetInventory.Capacity; slot++)
            {
                if (targetInventory.TryPutItem(item, slot, false, true, actor, true, true))
                {
                    return true;
                }
            }

            return targetInventory.TryPutItem(item, actor, CharacterInventory.AnySlot);
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

        private static (Vector2 pos, Submarine sub) ResolveSpawnPoint(Inventory targetInventory, Character actor)
        {
            if (targetInventory?.Owner is Item ownerItem)
            {
                return (ownerItem.WorldPosition, ownerItem.Submarine);
            }
            if (targetInventory?.Owner is Character ownerChar)
            {
                return (ownerChar.WorldPosition, ownerChar.Submarine);
            }
            if (actor != null)
            {
                return (actor.WorldPosition, actor.Submarine);
            }
            return (Vector2.Zero, null);
        }
    }
}
