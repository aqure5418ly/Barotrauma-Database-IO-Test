using System;
using Barotrauma;
using Barotrauma.Items.Components;
using Barotrauma.Networking;
using DatabaseIOTest;
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

        TerminalPanelAction action = request switch
        {
            1 => TerminalPanelAction.PrevPage,
            2 => TerminalPanelAction.NextPage,
            8 => TerminalPanelAction.CycleSortMode,
            9 => TerminalPanelAction.ToggleSortOrder,
            _ => TerminalPanelAction.None
        };

        if (action == TerminalPanelAction.None)
        {
            if (ModFileLog.IsDebugEnabled)
            {
                ModFileLog.WriteDebug(
                    "Panel",
                    $"{Constants.LogPrefix} XML action ignored id={item?.ID} db='{_resolvedDatabaseId}' request={request} (session actions removed)");
            }
            return;
        }

        if (action == _lastXmlAction && Timing.TotalTime - _lastXmlActionAt < XmlActionDebounceSeconds)
        {
            return;
        }
        _lastXmlAction = action;
        _lastXmlActionAt = Timing.TotalTime;

        Character actor = Character.Controlled;
        bool applied = HandlePanelActionServer(action, actor, "xml");

        if (ModFileLog.IsDebugEnabled)
        {
            ModFileLog.WriteDebug(
                "Panel",
                $"{Constants.LogPrefix} XML action consumed: {action} applied={applied} actor='{actor?.Name ?? "none"}' db='{_resolvedDatabaseId}' itemId={item?.ID}");
        }
    }
}
