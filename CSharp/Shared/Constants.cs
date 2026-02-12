using System.Collections.Generic;

namespace DatabaseIOTest
{
    public static class Constants
    {
        public const string LogPrefix = "[Database IO Test]";

        public const string DefaultDatabaseId = "default";
        public const int DefaultMaxStorageCount = 2048;
        public const float DefaultIngestInterval = 0.15f;
        public const float DefaultTerminalSessionTimeout = 180f;
        public const int MaxSerializedStackSize = 63;

        public static readonly HashSet<string> BlockedTags = new HashSet<string>
        {
            "io_box",
            "data_card",
            "item_storage_box",
            "database_interface",
            "database_terminal"
        };

        public static readonly HashSet<string> BlockedIdentifiers = new HashSet<string>
        {
            "stackbox",
            "datacard",
            "itemstoragebox",
            "databaseinterface",
            "databaseterminal"
        };
    }
}
