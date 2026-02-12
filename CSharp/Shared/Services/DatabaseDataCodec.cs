using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml.Linq;
using DatabaseIOTest.Models;

namespace DatabaseIOTest.Services
{
    public static class DatabaseDataCodec
    {
        public static string Serialize(DatabaseData data)
        {
            if (data == null)
            {
                data = new DatabaseData();
            }

            var root = new XElement("db",
                new XAttribute("id", data.DatabaseId ?? Constants.DefaultDatabaseId),
                new XAttribute("version", data.Version));

            var items = new XElement("items");
            if (data.Items != null)
            {
                foreach (var item in data.Items)
                {
                    if (item != null)
                    {
                        items.Add(SerializeItem(item));
                    }
                }
            }
            root.Add(items);

            var doc = new XDocument(root);
            return doc.ToString(SaveOptions.DisableFormatting);
        }

        public static DatabaseData Deserialize(string encoded, string fallbackDatabaseId)
        {
            string fallback = string.IsNullOrWhiteSpace(fallbackDatabaseId) ? Constants.DefaultDatabaseId : fallbackDatabaseId;
            if (string.IsNullOrWhiteSpace(encoded))
            {
                return new DatabaseData { DatabaseId = fallback };
            }

            try
            {
                var doc = XDocument.Parse(encoded);
                var root = doc.Root;
                if (root == null || root.Name != "db")
                {
                    return new DatabaseData { DatabaseId = fallback };
                }

                var data = new DatabaseData
                {
                    DatabaseId = (string)root.Attribute("id") ?? fallback,
                    Version = ParseInt((string)root.Attribute("version"), 0),
                    Items = new List<ItemData>()
                };

                var itemsNode = root.Element("items");
                if (itemsNode != null)
                {
                    foreach (var itemElem in itemsNode.Elements("item"))
                    {
                        var parsed = DeserializeItem(itemElem);
                        if (parsed != null)
                        {
                            data.Items.Add(parsed);
                        }
                    }
                }

                return data;
            }
            catch
            {
                return new DatabaseData { DatabaseId = fallback };
            }
        }

        private static XElement SerializeItem(ItemData item)
        {
            var node = new XElement("item",
                new XAttribute("id", item.Identifier ?? ""),
                new XAttribute("condition", item.Condition.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("quality", item.Quality),
                new XAttribute("stack", item.StackSize));

            var stolenNode = new XElement("stolenflags");
            if (item.StolenFlags != null)
            {
                foreach (var value in item.StolenFlags)
                {
                    stolenNode.Add(new XElement("v", value ? "1" : "0"));
                }
            }
            node.Add(stolenNode);

            var outpostNode = new XElement("outposts");
            if (item.OriginalOutposts != null)
            {
                foreach (var value in item.OriginalOutposts)
                {
                    outpostNode.Add(new XElement("v", value ?? ""));
                }
            }
            node.Add(outpostNode);

            var slotNode = new XElement("slots");
            if (item.SlotIndices != null)
            {
                foreach (var value in item.SlotIndices)
                {
                    slotNode.Add(new XElement("v", value));
                }
            }
            node.Add(slotNode);

            var containedNode = new XElement("contained");
            if (item.ContainedItems != null)
            {
                foreach (var child in item.ContainedItems)
                {
                    if (child != null)
                    {
                        containedNode.Add(SerializeItem(child));
                    }
                }
            }
            node.Add(containedNode);

            return node;
        }

        private static ItemData DeserializeItem(XElement elem)
        {
            if (elem == null || elem.Name != "item")
            {
                return null;
            }

            var item = new ItemData
            {
                Identifier = (string)elem.Attribute("id") ?? "",
                Condition = ParseFloat((string)elem.Attribute("condition"), 100f),
                Quality = ParseInt((string)elem.Attribute("quality"), 0),
                StackSize = Math.Max(1, ParseInt((string)elem.Attribute("stack"), 1)),
                StolenFlags = new List<bool>(),
                OriginalOutposts = new List<string>(),
                SlotIndices = new List<int>(),
                ContainedItems = new List<ItemData>()
            };

            var stolenNode = elem.Element("stolenflags");
            if (stolenNode != null)
            {
                foreach (var value in stolenNode.Elements("v"))
                {
                    item.StolenFlags.Add((value.Value ?? "").Trim() == "1");
                }
            }

            var outpostNode = elem.Element("outposts");
            if (outpostNode != null)
            {
                foreach (var value in outpostNode.Elements("v"))
                {
                    item.OriginalOutposts.Add(value.Value ?? "");
                }
            }

            var slotNode = elem.Element("slots");
            if (slotNode != null)
            {
                foreach (var value in slotNode.Elements("v"))
                {
                    item.SlotIndices.Add(ParseInt(value.Value, -1));
                }
            }

            var containedNode = elem.Element("contained");
            if (containedNode != null)
            {
                foreach (var child in containedNode.Elements("item"))
                {
                    var parsed = DeserializeItem(child);
                    if (parsed != null)
                    {
                        item.ContainedItems.Add(parsed);
                    }
                }
            }

            return item;
        }

        private static int ParseInt(string input, int fallback)
        {
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
            return fallback;
        }

        private static float ParseFloat(string input, float fallback)
        {
            if (float.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
            return fallback;
        }
    }
}
