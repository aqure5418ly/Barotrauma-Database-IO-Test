using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest;
using DatabaseIOTest.Models;
using DatabaseIOTest.Services;
#if CLIENT
using Microsoft.Xna.Framework;
#endif

public partial class DatabaseTerminalComponent : ItemComponent, IServerSerializable, IClientSerializable
{
    private void ConsumeXmlActionRequest()
    {
        int request = XmlActionRequest;
        if (request == 0) { return; }
        XmlActionRequest = 0;

#if CLIENT
        if (ModFileLog.IsDebugEnabled)
        {
            Character controlled = Character.Controlled;
            int selectedId = controlled?.SelectedItem?.ID ?? -1;
            int selectedSecondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
            LogPanelDebug(
                $"xml raw request={request} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"sessionActive={IsSessionActive()} cachedOpen={_cachedSessionOpen} inPlace={UseInPlaceSession} sessionVariant={SessionVariant} " +
                $"owner='{_sessionOwner?.Name ?? "none"}' controlled='{controlled?.Name ?? "none"}' selected={selectedId}/{selectedSecondaryId}");
        }
#endif

        TerminalPanelAction action = request switch
        {
            1 => TerminalPanelAction.PrevPage,
            2 => TerminalPanelAction.NextPage,
            3 => TerminalPanelAction.CloseSession,
            4 => TerminalPanelAction.OpenSession,
            5 => TerminalPanelAction.ForceOpenSession,
            6 => TerminalPanelAction.PrevMatch,
            7 => TerminalPanelAction.NextMatch,
            8 => TerminalPanelAction.CycleSortMode,
            9 => TerminalPanelAction.ToggleSortOrder,
            10 => TerminalPanelAction.CompactItems,
            _ => TerminalPanelAction.None
        };
        if (action == TerminalPanelAction.None) { return; }

        if (action == _lastXmlAction && Timing.TotalTime - _lastXmlActionAt < XmlActionDebounceSeconds)
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action ignored by debounce: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }
        _lastXmlAction = action;
        _lastXmlActionAt = Timing.TotalTime;

        if (EnableCsPanelOverlay && UseInPlaceSession)
        {
            // In mixed fixed-terminal mode, XML is used only for "Open/Force".
            // Keep "Close" as XML fallback so sessions can always be exited if panel rendering fails.
            bool sessionActive = IsSessionActive();
            bool allowXmlOpenWhenClosed =
                !sessionActive &&
                (action == TerminalPanelAction.OpenSession || action == TerminalPanelAction.ForceOpenSession);
            bool allowXmlCloseWhenOpen =
                sessionActive &&
                action == TerminalPanelAction.CloseSession;
            if (!allowXmlOpenWhenClosed && !allowXmlCloseWhenOpen)
            {
                ModFileLog.Write(
                    "Panel",
                    $"{Constants.LogPrefix} XML action ignored in CS panel in-place mode: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
                return;
            }
        }

        bool allowWhenClosed = action == TerminalPanelAction.OpenSession ||
                               action == TerminalPanelAction.ForceOpenSession ||
                               action == TerminalPanelAction.CycleSortMode ||
                               action == TerminalPanelAction.ToggleSortOrder ||
                               action == TerminalPanelAction.CompactItems;
        if (!allowWhenClosed && !IsSessionActive())
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action dropped while closed: {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        Character actor = _sessionOwner;
        if (actor != null && (actor.Removed || actor.IsDead))
        {
            DebugConsole.NewMessage(
                $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} for '{_resolvedDatabaseId}'.",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Panel", $"{Constants.LogPrefix} XML action ignored (invalid session owner): {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            return;
        }

        if (actor == null || actor.Removed || actor.IsDead)
        {
            actor = Character.Controlled;
        }
        if (actor == null || actor.Removed || actor.IsDead)
        {
            actor = item?.ParentInventory?.Owner as Character;
        }
        if (action != TerminalPanelAction.OpenSession &&
            action != TerminalPanelAction.ForceOpenSession &&
            _sessionOwner == null &&
            actor != null &&
            !actor.Removed &&
            !actor.IsDead)
        {
            _sessionOwner = actor;
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action adopted session owner '{actor.Name}' db='{_resolvedDatabaseId}' itemId={item?.ID}");
        }

        bool applied = HandlePanelActionServer(action, actor, "xml");
        ModFileLog.Write(
            "Panel",
            $"{Constants.LogPrefix} XML action consumed: {action} applied={applied} actor='{actor?.Name ?? "none"}' owner='{_sessionOwner?.Name ?? "none"}' " +
            $"db='{_resolvedDatabaseId}' page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} itemId={item?.ID}");
    }
}
