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
    private readonly struct XmlActionActorResolution
    {
        public XmlActionActorResolution(Character actor, string source, int candidateCount)
        {
            Actor = actor;
            Source = source ?? "none";
            CandidateCount = Math.Max(0, candidateCount);
        }

        public Character Actor { get; }
        public string Source { get; }
        public int CandidateCount { get; }
    }

    private static bool IsValidXmlActionActor(Character candidate)
    {
        return candidate != null && !candidate.Removed && !candidate.IsDead;
    }

    private XmlActionActorResolution ResolveXmlActionActor()
    {
        if (IsValidXmlActionActor(_sessionOwner))
        {
            return new XmlActionActorResolution(_sessionOwner, "session_owner", 1);
        }

        Character controlled = Character.Controlled;
        if (IsValidXmlActionActor(controlled))
        {
            return new XmlActionActorResolution(controlled, "character_controlled", 1);
        }

        Character selectedCandidate = null;
        int selectedCandidateCount = 0;
        foreach (Character candidate in Character.CharacterList)
        {
            if (!IsValidXmlActionActor(candidate)) { continue; }
            bool selectedThisTerminal = candidate.SelectedItem == item || candidate.SelectedSecondaryItem == item;
            if (!selectedThisTerminal) { continue; }

            selectedCandidateCount++;
            if (selectedCandidate == null || candidate.ID < selectedCandidate.ID)
            {
                selectedCandidate = candidate;
            }
        }

        if (IsValidXmlActionActor(selectedCandidate))
        {
            if (selectedCandidateCount > 1 && ModFileLog.IsDebugEnabled)
            {
                ModFileLog.WriteDebug(
                    "Panel",
                    $"{Constants.LogPrefix} XML actor ambiguity db='{_resolvedDatabaseId}' itemId={item?.ID} " +
                    $"candidates={selectedCandidateCount} chosen='{selectedCandidate.Name}' chosenId={selectedCandidate.ID} source='selected_item_scan'");
            }

            return new XmlActionActorResolution(selectedCandidate, "selected_item_scan", selectedCandidateCount);
        }

        Character inventoryOwner = item?.ParentInventory?.Owner as Character;
        if (IsValidXmlActionActor(inventoryOwner))
        {
            return new XmlActionActorResolution(inventoryOwner, "parent_inventory_owner", 1);
        }

        return new XmlActionActorResolution(null, "none", selectedCandidateCount);
    }

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

        if (!IsValidXmlActionActor(_sessionOwner) && _sessionOwner != null)
        {
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                $"{Constants.LogPrefix} XML action owner reset (invalid session owner): {action} for '{_resolvedDatabaseId}'.",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} XML action owner reset (invalid session owner): {action} db='{_resolvedDatabaseId}' itemId={item?.ID}");
            _sessionOwner = null;
        }

        XmlActionActorResolution actorResolution = ResolveXmlActionActor();
        Character actor = actorResolution.Actor;

        if (action != TerminalPanelAction.OpenSession &&
            action != TerminalPanelAction.ForceOpenSession &&
            _sessionOwner == null &&
            IsValidXmlActionActor(actor))
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
            $"actorSource='{actorResolution.Source}' candidateCount={actorResolution.CandidateCount} " +
            $"db='{_resolvedDatabaseId}' page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} itemId={item?.ID}");
    }
}

