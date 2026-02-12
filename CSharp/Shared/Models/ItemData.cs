using System;
using System.Collections.Generic;

namespace DatabaseIOTest.Models
{
    [Serializable]
    public class ItemData
    {
        public string Identifier { get; set; } = "";
        public float Condition { get; set; } = 100f;
        public int Quality { get; set; }
        public int StackSize { get; set; } = 1;
        public List<bool> StolenFlags { get; set; } = new List<bool>();
        public List<string> OriginalOutposts { get; set; } = new List<string>();
        public List<int> SlotIndices { get; set; } = new List<int>();
        public List<ItemData> ContainedItems { get; set; } = new List<ItemData>();

        public ItemData Clone()
        {
            var clone = new ItemData
            {
                Identifier = Identifier,
                Condition = Condition,
                Quality = Quality,
                StackSize = StackSize,
                StolenFlags = new List<bool>(StolenFlags),
                OriginalOutposts = new List<string>(OriginalOutposts),
                SlotIndices = new List<int>(SlotIndices),
                ContainedItems = new List<ItemData>()
            };

            foreach (var sub in ContainedItems)
            {
                clone.ContainedItems.Add(sub?.Clone());
            }

            return clone;
        }
    }
}
