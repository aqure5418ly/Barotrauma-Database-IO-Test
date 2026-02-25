using System;
using System.Diagnostics;
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
    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);

        if (IsServerAuthority)
        {
            DatabaseStore.RegisterTerminal(this);
            UpdateSummaryFromStore();
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
            RefreshLuaB1BridgeState(force: true);
        }
        else
        {
            LoadSummaryFromSerialized();
            UpdateDescriptionLocal();
        }
    }

    public override void Update(float deltaTime, Camera cam)
    {
        long perfStartTicks = 0;
        if (ModFileLog.IsDebugEnabled)
        {
            perfStartTicks = Stopwatch.GetTimestamp();
        }

#if CLIENT
        UpdateFixedXmlControlPanelState();
        if (EnableCsPanelOverlay)
        {
            if (!IsFixedTerminal || ShouldDriveFixedUiFromUpdate())
            {
                UpdateClientPanel();
            }
        }
#endif

        if (!IsServerAuthority) { return; }

        if (Timing.TotalTime - _lastTickTime < 0.25)
        {
            return;
        }
        _lastTickTime = Timing.TotalTime;

        FlushIdleInventoryItems();
        ConsumeXmlActionRequest();
        TryProcessLuaTakeRequestFromBridge();

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary();
        RefreshLuaB1BridgeState();

        if (perfStartTicks != 0)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - perfStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= TerminalUpdatePerfWarnMs && Timing.TotalTime >= _nextUpdatePerfLogAt)
            {
                _nextUpdatePerfLogAt = Timing.TotalTime + TerminalUpdatePerfLogCooldownSeconds;
                ModFileLog.Write(
                    "Perf",
                    $"{Constants.LogPrefix} TerminalUpdateSlow id={item?.ID} db='{_resolvedDatabaseId}' ms={elapsedMs:0.###} " +
                    $"items={_cachedItemCount} panel={EnableCsPanelOverlay} fixed={IsFixedTerminal}");
            }
        }
    }

    public override bool SecondaryUse(float deltaTime, Character character = null)
    {
        // Session open/close semantics are removed in atomic mode.
        // Keep this input consumed so legacy toggle behavior cannot be triggered.
        return true;
    }

    public override void RemoveComponentSpecific()
    {
#if CLIENT
        ReleaseClientPanelFocusIfOwned("component removed");
        if (_panelFrame != null)
        {
            _panelFrame.Visible = false;
            _panelFrame.Enabled = false;
        }
        _panelBufferDrawer = null;
        _panelBufferFrame = null;
        _panelBufferInfo = null;
        _panelFrame = null;
#endif

        if (IsServerAuthority)
        {
            DatabaseStore.UnregisterTerminal(this);
        }
    }
}
