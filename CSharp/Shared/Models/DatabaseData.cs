using System;
using System.Collections.Generic;

namespace DatabaseIOTest.Models
{
    [Serializable]
    public class DatabaseData
    {
        public string DatabaseId { get; set; } = Constants.DefaultDatabaseId;
        public int Version { get; set; }
        public List<ItemData> Items { get; set; } = new List<ItemData>();

        public int ItemCount
        {
            get
            {
                int count = 0;
                foreach (var item in Items)
                {
                    count += CountRecursive(item);
                }
                return count;
            }
        }

        private static int CountRecursive(ItemData item)
        {
            if (item == null) { return 0; }
            int childCount = 0;
            foreach (var child in item.ContainedItems)
            {
                childCount += CountRecursive(child);
            }
            return item.StackSize * (1 + childCount);
        }

        public DatabaseData Clone()
        {
            var clone = new DatabaseData
            {
                DatabaseId = DatabaseId,
                Version = Version,
                Items = new List<ItemData>()
            };

            foreach (var item in Items)
            {
                clone.Items.Add(item?.Clone());
            }
            return clone;
        }
    }
}
