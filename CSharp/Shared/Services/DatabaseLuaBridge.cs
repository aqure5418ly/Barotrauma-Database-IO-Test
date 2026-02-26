using System;
using System.Collections.Generic;
using Barotrauma;

namespace DatabaseIOTest.Services
{
    public static class DatabaseLuaBridge
    {
        public static bool IsTerminalSessionOpen(int terminalEntityId)
        {
            var terminal = DatabaseStore.FindRegisteredTerminal(terminalEntityId);
            return terminal != null && terminal.IsVirtualSessionOpenForUi();
        }

        public static string GetTerminalDatabaseId(int terminalEntityId)
        {
            var terminal = DatabaseStore.FindRegisteredTerminal(terminalEntityId);
            return terminal?.DatabaseId ?? Constants.DefaultDatabaseId;
        }

        public static List<DatabaseTerminalComponent.TerminalVirtualEntry> GetTerminalVirtualSnapshot(
            int terminalEntityId,
            bool refreshCurrentPage = true)
        {
            var terminal = DatabaseStore.FindRegisteredTerminal(terminalEntityId);
            if (terminal == null)
            {
                return new List<DatabaseTerminalComponent.TerminalVirtualEntry>();
            }

            var rows = terminal.GetVirtualViewSnapshot(refreshCurrentPage);
            return rows ?? new List<DatabaseTerminalComponent.TerminalVirtualEntry>();
        }

        public static string TryTakeOneByIdentifierFromTerminalSession(
            int terminalEntityId,
            string identifier,
            Character actor)
        {
            var terminal = DatabaseStore.FindRegisteredTerminal(terminalEntityId);
            if (terminal == null)
            {
                return "terminal_missing";
            }

            return terminal.TryTakeOneByIdentifierFromVirtualSession(identifier, actor) ?? "";
        }
    }
}
