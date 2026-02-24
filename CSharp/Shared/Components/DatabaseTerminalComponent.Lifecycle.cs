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
    public override void OnItemLoaded()
    {
        base.OnItemLoaded();
        _resolvedDatabaseId = DatabaseStore.Normalize(DatabaseId);

        if (IsServerAuthority)
        {
            DatabaseStore.RegisterTerminal(this);

            if (SessionVariant)
            {
                DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID);
                if (_sessionOpenedAt <= 0)
                {
                    _sessionOpenedAt = Timing.TotalTime;
                }

                // Recovery path when a session item exists after load.
                BuildPagesFromCurrentInventory();
            }

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

        double autoCloseMs = 0;
        double flushIdleMs = 0;
        double xmlActionMs = 0;
        double pageFillMs = 0;
        double pendingSyncMs = 0;
        double summaryMs = 0;
        double descMs = 0;
        double syncMs = 0;
        long stageStartTicks = perfStartTicks;

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

        if (IsSessionActive() && ShouldAutoClose())
        {
            if (SessionVariant)
            {
                CloseSessionInternal("timeout or invalid owner", true, _sessionOwner);
            }
            else
            {
                CloseSessionInPlace("timeout or invalid owner");
            }
        }
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            autoCloseMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        // In-place terminals remain as one item. If someone inserts items while session is closed,
        // immediately return those items so they cannot be silently cleared on open.
        FlushIdleInventoryItems();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            flushIdleMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        ConsumeXmlActionRequest();
        TryProcessLuaTakeRequestFromBridge();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            xmlActionMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        TryRunPendingPageFillCheck();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            pageFillMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        if (_pendingSummarySync && Timing.TotalTime >= _nextPendingSummarySyncAt)
        {
            TrySyncSummary(force: true);
        }
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            pendingSyncMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        UpdateSummaryFromStore();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            summaryMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        UpdateDescriptionLocal();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            descMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }
        TrySyncSummary();
        RefreshLuaB1BridgeState();
        if (stageStartTicks != 0)
        {
            long nowTicks = Stopwatch.GetTimestamp();
            syncMs = (nowTicks - stageStartTicks) * 1000.0 / Stopwatch.Frequency;
            stageStartTicks = nowTicks;
        }

        if (perfStartTicks != 0)
        {
            double elapsedMs = (Stopwatch.GetTimestamp() - perfStartTicks) * 1000.0 / Stopwatch.Frequency;
            if (elapsedMs >= TerminalUpdatePerfWarnMs && Timing.TotalTime >= _nextUpdatePerfLogAt)
            {
                _nextUpdatePerfLogAt = Timing.TotalTime + TerminalUpdatePerfLogCooldownSeconds;
                ModFileLog.Write(
                    "Perf",
                    $"{Constants.LogPrefix} TerminalUpdateSlow id={item?.ID} db='{_resolvedDatabaseId}' ms={elapsedMs:0.###} " +
                    $"session={IsSessionActive()} inPlace={_inPlaceSessionActive} owner={(_sessionOwner != null ? _sessionOwner.Name : "none")} " +
                    $"entries={_sessionEntries.Count} page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
                    $"pendingSummary={_pendingSummarySync}");
            }

            if (elapsedMs >= TerminalUpdateStageWarnMs && Timing.TotalTime >= _nextUpdateStageLogAt)
            {
                _nextUpdateStageLogAt = Timing.TotalTime + TerminalUpdateStageLogCooldownSeconds;
                ModFileLog.Write(
                    "Perf",
                    $"{Constants.LogPrefix} TerminalUpdateStage id={item?.ID} db='{_resolvedDatabaseId}' totalMs={elapsedMs:0.###} " +
                    $"autoCloseMs={autoCloseMs:0.###} flushIdleMs={flushIdleMs:0.###} xmlActionMs={xmlActionMs:0.###} " +
                    $"pageFillMs={pageFillMs:0.###} pendingSyncMs={pendingSyncMs:0.###} summaryMs={summaryMs:0.###} " +
                    $"descMs={descMs:0.###} syncMs={syncMs:0.###} session={IsSessionActive()} inPlace={_inPlaceSessionActive} " +
                    $"owner={(_sessionOwner != null ? _sessionOwner.Name : "none")}");
            }
        }
    }

    public override bool SecondaryUse(float deltaTime, Character character = null)
    {
        if (character == null) { return false; }
        if (Timing.TotalTime < _nextToggleAllowedTime) { return true; }
        if (Timing.TotalTime - _creationTime < 0.35) { return true; }
        if (!IsServerAuthority) { return false; }

        // In fixed in-place terminal mode, opening/closing should be driven by panel/UI actions only.
        // Blocking secondary-use toggle here avoids open/close oscillation while interacting with UI.
        if (UseInPlaceSession && EnableCsPanelOverlay)
        {
            return true;
        }

        if (!SessionVariant && !HasRequiredPower())
        {
            if (Timing.TotalTime >= _nextNoPowerLogTime)
            {
                _nextNoPowerLogTime = Timing.TotalTime + 3.0;
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                    $"{Constants.LogPrefix} Terminal '{_resolvedDatabaseId}' has no power (need {Math.Max(0f, MinRequiredVoltage):0.##}V).",
                    Microsoft.Xna.Framework.Color.Orange);
            }
            return true;
        }

        _nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;

        if (SessionVariant || _inPlaceSessionActive)
        {
            if (Timing.TotalTime - _sessionOpenedAt < MinSessionDurationBeforeClose)
            {
                return true;
            }

            if (SessionVariant)
            {
                CloseSessionInternal("manual close", true, character);
            }
            else
            {
                CloseSessionInPlace("manual close");
            }
        }
        else
        {
            OpenSessionInternal(character);
        }

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
            if (SessionVariant)
            {
                CloseSessionInternal("terminal removed", false, _sessionOwner);
            }
            else if (_inPlaceSessionActive)
            {
                CloseSessionInPlace("terminal removed");
            }
            else
            {
                DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
            }

            DatabaseStore.UnregisterTerminal(this);
        }
    }
}

