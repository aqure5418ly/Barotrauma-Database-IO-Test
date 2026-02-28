using Barotrauma;
using DatabaseIOTest;
using DatabaseIOTest.Services;

public partial class DatabaseTerminalComponent
{
    private static readonly Identifier ScrewdriverIdentifier = "screwdriver".ToIdentifier();

    // Keep both paths available for fast A/B tests:
    // UpdateOnly: Update() drives fixed UI, hooks only silence native container GUI.
    // HookOnly: hooks drive fixed UI (legacy path, kept for rollback).
    // Hybrid: update + hook drive together (debug only, not recommended for normal play).
    private enum FixedUiRenderStrategy : byte
    {
        UpdateOnly = 0,
        HookOnly = 1,
        Hybrid = 2
    }

    private const FixedUiRenderStrategy ActiveFixedUiRenderStrategy = FixedUiRenderStrategy.HookOnly;

    internal bool ShouldHijackFixedTerminalUi()
    {
#if CLIENT
        return IsFixedTerminal &&
               EnableCsPanelOverlay &&
               HasRequiredPower() &&
               !IsCompactRecipePanelMode() &&
               item != null &&
               !item.Removed;
#else
        return false;
#endif
    }

    internal bool IsCompactRecipeTerminalUiMode()
    {
#if CLIENT
        return IsFixedTerminal &&
               EnableCsPanelOverlay &&
               HasRequiredPower() &&
               item != null &&
               !item.Removed &&
               IsCompactRecipePanelMode();
#else
        return false;
#endif
    }

    internal bool ShouldSuppressNativeTerminalGui()
    {
#if CLIENT
        if (ShouldYieldToConnectionPanelUi()) { return false; }
        return ShouldHijackFixedTerminalUi() || IsCompactRecipeTerminalUiMode();
#else
        return false;
#endif
    }

    internal bool ShouldSilenceFixedContainerGui()
    {
#if CLIENT
        if (ShouldYieldToConnectionPanelUi()) { return false; }
        return ShouldHijackFixedTerminalUi() && IsVirtualSessionOpenForUi();
#else
        return false;
#endif
    }

    internal bool ShouldYieldToConnectionPanelUi()
    {
#if CLIENT
        if (!IsFixedTerminal || item == null || item.Removed) { return false; }

        Character controlled = Character.Controlled;
        if (controlled == null || controlled.Removed || controlled.IsDead) { return false; }

        bool selectedThis = controlled.SelectedItem == item || controlled.SelectedSecondaryItem == item;
        if (!selectedThis) { return false; }

        var equippedItems = controlled.Inventory?.AllItemsMod;
        if (equippedItems == null) { return false; }
        foreach (var equipped in equippedItems)
        {
            if (equipped == null || equipped.Removed) { continue; }
            if (equipped.Prefab == null || equipped.Prefab.Identifier != ScrewdriverIdentifier) { continue; }
            if (controlled.HasEquippedItem(equipped)) { return true; }
        }

        return false;
#else
        return false;
#endif
    }

    internal bool ShouldDriveFixedUiFromUpdate()
    {
#if CLIENT
        if (!ShouldHijackFixedTerminalUi()) { return false; }
        return ActiveFixedUiRenderStrategy == FixedUiRenderStrategy.UpdateOnly ||
               ActiveFixedUiRenderStrategy == FixedUiRenderStrategy.Hybrid;
#else
        return false;
#endif
    }

    internal bool ShouldDriveFixedUiFromHook()
    {
#if CLIENT
        if (!ShouldHijackFixedTerminalUi()) { return false; }
        return ActiveFixedUiRenderStrategy == FixedUiRenderStrategy.HookOnly ||
               ActiveFixedUiRenderStrategy == FixedUiRenderStrategy.Hybrid;
#else
        return false;
#endif
    }

    internal bool DrawFixedTerminalUiFromGuiHook(string source)
    {
#if CLIENT
        if (!ShouldDriveFixedUiFromHook()) { return false; }

        if (ModFileLog.IsDebugEnabled && Timing.TotalTime >= _nextFixedUiHookLogAt)
        {
            _nextFixedUiHookLogAt = Timing.TotalTime + 1.0;
            LogPanelDebug(
                $"fixed hook draw source={source} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"cachedOpen={_cachedSessionOpen} sessionActive={IsVirtualSessionOpenForUi()}");
        }

        UpdateFixedXmlControlPanelState();
        UpdateClientPanel();
        return _panelFrame != null && _panelFrame.Visible && _panelFrame.Enabled;
#else
        return false;
#endif
    }

    internal void TraceFixedContainerSilenced(string source)
    {
#if CLIENT
        if (!ModFileLog.IsDebugEnabled || Timing.TotalTime < _nextFixedContainerSilenceLogAt) { return; }
        _nextFixedContainerSilenceLogAt = Timing.TotalTime + 1.0;
        LogPanelDebug(
            $"fixed container silenced source={source} id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"cachedOpen={_cachedSessionOpen} sessionActive={IsVirtualSessionOpenForUi()}");
#endif
    }

#if CLIENT
    private double _nextFixedUiHookLogAt;
    private double _nextFixedContainerSilenceLogAt;
#endif
}
