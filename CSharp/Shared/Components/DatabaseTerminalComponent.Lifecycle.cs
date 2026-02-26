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

    private void ToggleHandheldPanelByUse(Character character, string source)
    {
#if CLIENT
        if (IsFixedTerminal || !EnableCsPanelOverlay) { return; }
        if (character == null || character != Character.Controlled) { return; }
        LogHandheldDiag($"toggle_enter:{source}", Character.Controlled, character);

        bool currentlyVisible = _panelFrame?.Visible ?? false;
        if (currentlyVisible)
        {
            _handheldPanelArmedByUse = false;
            _panelManualHideUntil = Timing.TotalTime + 0.2;
            ReleaseClientPanelFocusIfOwned($"handheld {source} close");
            SetPanelVisible(false, $"handheld {source} close");
        }
        else
        {
            _handheldPanelArmedByUse = true;
            _panelManualHideUntil = 0;
            ClaimClientPanelFocus($"handheld {source} open");
        }
        LogHandheldDiag($"toggle_exit:{source}", Character.Controlled, character);
#else
        _ = character;
        _ = source;
#endif
    }

    public override bool Use(float deltaTime, Character character = null)
    {
        // Handheld panel trigger is handled by SecondaryUse (native right+left path).
        return false;
    }

    public override bool SecondaryUse(float deltaTime, Character character = null)
    {
        // Native interaction path for handheld terminal: right-click hold + left click.
#if CLIENT
        if (!IsFixedTerminal && EnableCsPanelOverlay)
        {
            LogHandheldDiag("secondary_called", Character.Controlled, character);
        }

        if (!IsFixedTerminal &&
            EnableCsPanelOverlay &&
            character != null &&
            character == Character.Controlled)
        {
            if (Timing.TotalTime < _nextHandheldUseToggleAt)
            {
                LogHandheldDiag("secondary_debounced", Character.Controlled, character);
                return true;
            }

            _nextHandheldUseToggleAt = Timing.TotalTime + HandheldUseToggleCooldownSeconds;
            ToggleHandheldPanelByUse(character, "secondary use");
            LogHandheldDiag("secondary_handled", Character.Controlled, character);
            return true;
        }

        if (!IsFixedTerminal && EnableCsPanelOverlay)
        {
            LogHandheldDiag("secondary_ignored", Character.Controlled, character);
        }
#endif
        return false;
    }

    public override void RemoveComponentSpecific()
    {
#if CLIENT
        _handheldPanelArmedByUse = false;
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
