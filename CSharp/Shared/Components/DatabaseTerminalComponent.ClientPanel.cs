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
#if CLIENT
    private static long NextPanelTraceSeq()
    {
        unchecked
        {
            _panelTraceSeq++;
            if (_panelTraceSeq <= 0) { _panelTraceSeq = 1; }
            return _panelTraceSeq;
        }
    }

    private void ClaimClientPanelFocus(string reason)
    {
        if (item == null || item.Removed) { return; }
        _clientPanelFocusItemId = item.ID;
        _clientPanelFocusUntil = Timing.TotalTime + PanelFocusStickySeconds;
        _clientPanelFocusReason = reason ?? "";
    }

    private void RefreshClientPanelFocusLeaseIfOwned()
    {
        if (item == null || item.Removed) { return; }
        if (_clientPanelFocusItemId != item.ID) { return; }
        _clientPanelFocusUntil = Timing.TotalTime + PanelFocusStickySeconds;
    }

    private void ReleaseClientPanelFocusIfOwned(string reason)
    {
        if (item == null || item.Removed) { return; }
        if (_clientPanelFocusItemId != item.ID) { return; }
        _clientPanelFocusItemId = -1;
        _clientPanelFocusUntil = 0;
        _clientPanelFocusReason = reason ?? "";
    }
    private void LogPanelDebug(string message)
    {
        if (!EnablePanelDebugLog || !ModFileLog.IsDebugEnabled) { return; }
        long seq = NextPanelTraceSeq();
        string line = $"{Constants.LogPrefix} [Panel#{seq}] {message}";
        DebugConsole.NewMessage(line, Color.LightSkyBlue);
        ModFileLog.WriteDebug("Panel", line);
    }

    private void UpdateFixedXmlControlPanelState()
    {
        if (!EnableCsPanelOverlay || !IsFixedTerminal || item == null || item.Removed)
        {
            return;
        }

        _fixedXmlControlPanel ??= item.GetComponent<CustomInterface>();
        if (_fixedXmlControlPanel == null)
        {
            return;
        }

        bool shouldEnableXmlPanel = !_cachedSessionOpen;
        if (_fixedXmlControlPanel.IsActive != shouldEnableXmlPanel)
        {
            _fixedXmlControlPanel.IsActive = shouldEnableXmlPanel;
            LogPanelDebug(
                $"fixed xml panel {(shouldEnableXmlPanel ? "enabled" : "disabled")} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"cachedOpen={_cachedSessionOpen} sessionActive={IsSessionActive()}");
        }
    }

    private void LogPanelEval(
        string phase,
        Character controlled,
        bool isSelected,
        bool isInControlledInventory,
        bool isNearby,
        bool shouldShow,
        float distance)
    {
        if (!EnablePanelDebugLog || !ModFileLog.IsDebugEnabled) { return; }

        int controlledId = controlled?.ID ?? -1;
        int selectedId = controlled?.SelectedItem?.ID ?? -1;
        int selectedSecondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
        int focusOwner = _clientPanelFocusItemId;
        double focusRemaining = Math.Max(0, _clientPanelFocusUntil - Timing.TotalTime);
        bool panelVisible = _panelFrame?.Visible ?? false;
        bool panelEnabled = _panelFrame?.Enabled ?? false;
        bool sessionActive = IsSessionActive();
        string signature =
            $"id={item?.ID}|{phase}|ctrl={controlledId}|sel={isSelected}|inv={isInControlledInventory}|near={isNearby}|show={shouldShow}|dist={distance:0.0}" +
            $"|cachedOpen={_cachedSessionOpen}|sessionActive={sessionActive}|inPlace={UseInPlaceSession}|sessionVariant={SessionVariant}" +
            $"|focus={focusOwner}|focusRemain={focusRemaining:0.00}|panel={panelVisible}/{panelEnabled}|sid={selectedId}|ssid={selectedSecondaryId}";
        if (signature == _lastPanelEvalSignature && Timing.TotalTime < _nextPanelEvalLogAllowedTime) { return; }

        _lastPanelEvalSignature = signature;
        _nextPanelEvalLogAllowedTime = Timing.TotalTime + PanelEvalLogCooldown;
        LogPanelDebug($"eval {signature}");
    }

    private static string TrimEntryLabel(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value)) { return ""; }
        string text = value.Trim();
        if (text.Length <= maxChars) { return text; }
        if (maxChars <= 1) { return text.Substring(0, 1); }
        return text.Substring(0, maxChars - 1) + "…";
    }

    private int GetPanelEntryPageCount()
    {
        if (_panelEntrySnapshot.Count <= 0) { return 1; }
        return Math.Max(1, (int)Math.Ceiling((double)_panelEntrySnapshot.Count / Math.Max(1, PanelEntryButtonCount)));
    }

    private int GetPanelEntryPageStartIndex()
    {
        return Math.Max(0, _panelEntryPageIndex) * Math.Max(1, PanelEntryButtonCount);
    }

    private List<TerminalVirtualEntry> ParsePanelEntriesFromLuaPayload()
    {
        var rows = new List<TerminalVirtualEntry>();
        string payload = LuaB1RowsPayload ?? "";
        if (string.IsNullOrEmpty(payload)) { return rows; }

        string[] rowParts = payload.Split(LuaRowSeparator);
        for (int i = 0; i < rowParts.Length; i++)
        {
            string row = rowParts[i] ?? "";
            if (string.IsNullOrWhiteSpace(row)) { continue; }
            string[] fields = row.Split(LuaFieldSeparator);
            if (fields.Length <= 0) { continue; }

            string id = (fields[0] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(id)) { continue; }
            string prefabId = fields.Length > 1 ? (fields[1] ?? "").Trim() : id;
            string payloadDisplayName = fields.Length > 2 ? (fields[2] ?? "").Trim() : id;
            // Client resolves localized name from identifier so each locale sees its own language.
            string localizedDisplayName = ResolveDisplayNameForIdentifier(id);
            string displayName;
            if (!string.IsNullOrWhiteSpace(localizedDisplayName) &&
                (!localizedDisplayName.Equals(id, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payloadDisplayName)))
            {
                displayName = localizedDisplayName;
            }
            else
            {
                displayName = string.IsNullOrWhiteSpace(payloadDisplayName) ? id : payloadDisplayName;
            }

            int amount = 0;
            if (fields.Length > 3)
            {
                int.TryParse(fields[3] ?? "0", out amount);
            }
            int quality = 0;
            if (fields.Length > 4)
            {
                int.TryParse(fields[4] ?? "0", out quality);
            }
            float condition = 100f;
            if (fields.Length > 5)
            {
                float.TryParse(
                    fields[5] ?? "100",
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out condition);
            }

            rows.Add(new TerminalVirtualEntry
            {
                Identifier = id,
                PrefabIdentifier = string.IsNullOrWhiteSpace(prefabId) ? id : prefabId,
                DisplayName = displayName,
                Amount = Math.Max(0, amount),
                BestQuality = Math.Max(0, quality),
                AverageCondition = Math.Max(0f, condition)
            });
        }

        return rows;
    }

    private void RefreshPanelEntrySnapshot(bool force = false)
    {
        if (!force && Timing.TotalTime < _nextPanelEntryRefreshAt) { return; }
        _nextPanelEntryRefreshAt = Timing.TotalTime + PanelEntryRefreshInterval;

        _panelEntrySnapshot.Clear();
        List<TerminalVirtualEntry> source;
        if (IsServerAuthority)
        {
            source = GetVirtualViewSnapshot(refreshCurrentPage: false);
        }
        else
        {
            source = ParsePanelEntriesFromLuaPayload();
        }

        if (source != null && source.Count > 0)
        {
            _panelEntrySnapshot.AddRange(source
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Identifier))
                .OrderBy(entry => entry.DisplayName ?? entry.Identifier ?? "", StringComparer.OrdinalIgnoreCase));
        }

        int pageCount = GetPanelEntryPageCount();
        if (_panelEntryPageIndex >= pageCount)
        {
            _panelEntryPageIndex = Math.Max(0, pageCount - 1);
        }
    }

    private bool TryApplyEntryIconToButton(GUIButton button, string identifier)
    {
        if (button == null || string.IsNullOrWhiteSpace(identifier)) { return false; }

        var prefab = ItemPrefab.FindByIdentifier(identifier.ToIdentifier()) as ItemPrefab;
        Sprite icon = prefab?.InventoryIcon ?? prefab?.Sprite;
        if (icon == null) { return false; }

        var buttonType = button.GetType();
        string[] propertyCandidates = { "Sprite", "Icon", "Image", "OverrideSprite" };
        for (int i = 0; i < propertyCandidates.Length; i++)
        {
            string name = propertyCandidates[i];
            try
            {
                var prop = buttonType.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || !prop.CanWrite) { continue; }
                if (!prop.PropertyType.IsAssignableFrom(icon.GetType())) { continue; }
                prop.SetValue(button, icon);
                return true;
            }
            catch
            {
                // Best-effort only.
            }
        }

        return false;
    }

    private void ApplyPanelEntriesToButtons()
    {
        if (_panelEntryButtons.Count <= 0) { return; }

        int start = GetPanelEntryPageStartIndex();
        int available = _panelEntrySnapshot.Count;
        for (int i = 0; i < _panelEntryButtons.Count; i++)
        {
            var button = _panelEntryButtons[i];
            if (button == null) { continue; }

            int idx = start + i;
            if (idx >= 0 && idx < available)
            {
                var entry = _panelEntrySnapshot[idx];
                string shortName = TrimEntryLabel(entry?.DisplayName ?? entry?.Identifier ?? "", 9);
                int amount = Math.Max(0, entry?.Amount ?? 0);
                string amountText = $"x{amount}";
                bool iconApplied = TryApplyEntryIconToButton(button, entry?.PrefabIdentifier ?? entry?.Identifier ?? "");

                button.Visible = true;
                button.Enabled = _cachedSessionOpen && amount > 0;
                button.Text = iconApplied ? amountText : $"{shortName} {amountText}";
                button.ToolTip =
                    $"{entry?.DisplayName ?? entry?.Identifier ?? ""}\n" +
                    $"{T("dbiotest.terminal.amount", "Amount")}: {amount}\n" +
                    $"{T("dbiotest.terminal.leftclicktake", "Left click to move 1 item to buffer.")}";
            }
            else
            {
                button.Visible = true;
                button.Enabled = false;
                button.Text = "";
                button.ToolTip = "";
            }
        }
    }

    private void HandlePanelEntryButtonClicked(int localIndex)
    {
        if (localIndex < 0 || localIndex >= _panelEntryButtons.Count) { return; }
        if (!_cachedSessionOpen) { return; }

        int idx = GetPanelEntryPageStartIndex() + localIndex;
        if (idx < 0 || idx >= _panelEntrySnapshot.Count) { return; }
        var entry = _panelEntrySnapshot[idx];
        string identifier = entry?.Identifier ?? "";
        if (string.IsNullOrWhiteSpace(identifier)) { return; }
        LogPanelDebug(
            $"entry click slot={localIndex} idx={idx} identifier='{identifier}' amount={Math.Max(0, entry?.Amount ?? 0)}");

        RequestPanelTakeByIdentifierClient(identifier);
    }

    private void RequestPanelTakeByIdentifierClient(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier)) { return; }
        if (Timing.TotalTime < _nextClientPanelActionAllowedTime)
        {
            LogPanelDebug($"take blocked by cooldown identifier='{identifier}'");
            return;
        }
        _nextClientPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;

        if (IsServerAuthority)
        {
            Character actor = Character.Controlled ?? _sessionOwner;
            string result = TryTakeOneByIdentifierFromVirtualSession(identifier, actor);
            if (string.IsNullOrEmpty(result))
            {
                LogPanelDebug($"take local success identifier='{identifier}'");
            }
            else
            {
                LogPanelDebug($"take local failed identifier='{identifier}' reason='{result}'");
            }
            RefreshPanelEntrySnapshot(force: true);
            return;
        }

        _pendingClientTakeIdentifier = identifier;
        _pendingClientAction = (byte)TerminalPanelAction.TakeByIdentifier;
        LogPanelDebug($"take sent to server identifier='{identifier}'");
        item.CreateClientEvent(this);
    }

    private void SetPanelVisible(bool visible, string reason)
    {
        bool prevFrameVisible = _panelFrame?.Visible ?? false;
        bool prevFrameEnabled = _panelFrame?.Enabled ?? false;

        if (_panelFrame != null &&
            (_panelFrame.Visible != visible || _panelFrame.Enabled != visible))
        {
            _panelFrame.Visible = visible;
            _panelFrame.Enabled = visible;
        }

        bool stateChanged = _panelLastVisible != visible;
        bool hideReasonChanged = !visible && _panelLastHiddenReason != reason;
        bool currentFrameVisible = _panelFrame?.Visible ?? false;
        bool currentFrameEnabled = _panelFrame?.Enabled ?? false;
        Character controlled = Character.Controlled;
        int selectedId = controlled?.SelectedItem?.ID ?? -1;
        int selectedSecondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
        int focusOwner = _clientPanelFocusItemId;
        double focusRemaining = Math.Max(0, _clientPanelFocusUntil - Timing.TotalTime);

        if (stateChanged || hideReasonChanged)
        {
            string rectInfo = _panelFrame == null
                ? "rect=(null)"
                : $"rect=({_panelFrame.Rect.X},{_panelFrame.Rect.Y},{_panelFrame.Rect.Width},{_panelFrame.Rect.Height})";
            LogPanelDebug(
                $"panel {(visible ? "show" : "hide")} id={item?.ID} reason={reason} sessionVariant={SessionVariant} " +
                $"summaryOpen={_cachedSessionOpen} page={Math.Max(1, _cachedPageIndex)}/{Math.Max(1, _cachedPageTotal)} " +
                $"state={prevFrameVisible}/{prevFrameEnabled}->{currentFrameVisible}/{currentFrameEnabled} " +
                $"selected={selectedId}/{selectedSecondaryId} focus={focusOwner} focusRemain={focusRemaining:0.00} {rectInfo}");
        }

        _panelLastVisible = visible;
        if (!visible) { _panelLastHiddenReason = reason; }
    }

    private void EnsurePanelCreated()
    {
        if (_panelFrame != null) { return; }
        if (GUI.Canvas == null)
        {
            if (Timing.TotalTime >= _nextNoCanvasLogAllowedTime)
            {
                LogPanelDebug($"skip create id={item?.ID}: GUI.Canvas is null");
                _nextNoCanvasLogAllowedTime = Timing.TotalTime + 1.0;
            }
            return;
        }

        Vector2 panelSize = IsFixedTerminal ? new Vector2(0.36f, 0.34f) : new Vector2(0.30f, 0.22f);
        _panelFrame = new GUIFrame(new RectTransform(panelSize, GUI.Canvas, Anchor.TopLeft));
        _panelFrame.RectTransform.AbsoluteOffset = new Point(36, 92);
        _panelFrame.Visible = false;
        _panelFrame.Enabled = false;
        LogPanelDebug(
            $"panel created id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"rect=({_panelFrame.Rect.X},{_panelFrame.Rect.Y},{_panelFrame.Rect.Width},{_panelFrame.Rect.Height}) " +
            $"canvas=({GUI.Canvas.Rect.X},{GUI.Canvas.Rect.Y},{GUI.Canvas.Rect.Width},{GUI.Canvas.Rect.Height})");
        LogPanelDebug(
            $"panel draw queue methods id={item?.ID} withOrder={(AddToGuiUpdateListMethodWithOrder != null)} noArgs={(AddToGuiUpdateListMethodNoArgs != null)}");

        var content = new GUILayoutGroup(new RectTransform(new Vector2(0.94f, 0.92f), _panelFrame.RectTransform, Anchor.Center));

        _panelTitle = new GUITextBlock(
            new RectTransform(new Vector2(1f, IsFixedTerminal ? 0.12f : 0.17f), content.RectTransform),
            T("dbiotest.panel.title", "Database Terminal"),
            textAlignment: Alignment.Center);

        _panelPageInfo = new GUITextBlock(
            new RectTransform(new Vector2(1f, IsFixedTerminal ? 0.08f : 0.10f), content.RectTransform),
            "",
            textAlignment: Alignment.Center);

        var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, IsFixedTerminal ? 0.12f : 0.18f), content.RectTransform), isHorizontal: true);

        _panelPrevButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.prev", "Prev"));
        _panelPrevButton.OnClicked = (_, __) =>
        {
            if (_panelEntryPageIndex > 0)
            {
                _panelEntryPageIndex--;
                RefreshPanelEntrySnapshot(force: true);
                UpdateClientPanelVisuals();
            }
            return true;
        };

        _panelNextButton = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), T("dbiotest.panel.next", "Next"));
        _panelNextButton.OnClicked = (_, __) =>
        {
            int pageCount = GetPanelEntryPageCount();
            if (_panelEntryPageIndex + 1 < pageCount)
            {
                _panelEntryPageIndex++;
                RefreshPanelEntrySnapshot(force: true);
                UpdateClientPanelVisuals();
            }
            return true;
        };

        _panelCloseButton = new GUIButton(new RectTransform(new Vector2(0.34f, 1f), row.RectTransform), T("dbiotest.panel.close", "Close"));
        _panelCloseButton.OnClicked = (_, __) =>
        {
            RequestPanelActionClient(TerminalPanelAction.CloseSession);
            return true;
        };

        int rowCount = Math.Max(1, PanelEntryButtonCount / Math.Max(1, PanelEntryColumns));
        float entryGridHeight = IsFixedTerminal ? 0.28f : 0.42f;
        var entryGrid = new GUILayoutGroup(new RectTransform(new Vector2(1f, entryGridHeight), content.RectTransform), isHorizontal: false);
        _panelEntryButtons.Clear();
        for (int r = 0; r < rowCount; r++)
        {
            var rowGroup = new GUILayoutGroup(
                new RectTransform(new Vector2(1f, 1f / rowCount), entryGrid.RectTransform),
                isHorizontal: true);
            for (int c = 0; c < PanelEntryColumns; c++)
            {
                int slot = r * PanelEntryColumns + c;
                var entryButton = new GUIButton(
                    new RectTransform(new Vector2(1f / PanelEntryColumns, 1f), rowGroup.RectTransform),
                    "");
                entryButton.OnClicked = (_, __) =>
                {
                    HandlePanelEntryButtonClicked(slot);
                    return true;
                };
                entryButton.Visible = true;
                entryButton.Enabled = false;
                _panelEntryButtons.Add(entryButton);
            }
        }

        _panelBufferInfo = null;
        _panelBufferFrame = null;
        _panelBufferDrawer = null;
        if (IsFixedTerminal)
        {
            _panelBufferInfo = new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.08f), content.RectTransform),
                T("dbiotest.panel.bufferhint", "Buffer"),
                textAlignment: Alignment.Left);

            _panelBufferFrame = new GUIFrame(
                new RectTransform(new Vector2(1f, 0.20f), content.RectTransform),
                style: "InnerFrameDark");
            _panelBufferFrame.CanBeFocused = false;
            _panelBufferDrawer = new GUICustomComponent(
                new RectTransform(Vector2.One, _panelBufferFrame.RectTransform),
                (sb, _) =>
                {
                    var inventory = GetTerminalInventory();
                    if (inventory != null)
                    {
                        inventory.Draw(sb, false);
                    }
                },
                null);
        }

        _panelStatusText = new GUITextBlock(
            new RectTransform(new Vector2(1f, IsFixedTerminal ? 0.10f : 0.13f), content.RectTransform),
            "",
            textAlignment: Alignment.Left);

        UpdateClientPanelVisuals();
    }

    private void QueuePanelForGuiUpdate()
    {
        if (_panelFrame == null) { return; }

        MethodInfo method = AddToGuiUpdateListMethodWithOrder ?? AddToGuiUpdateListMethodNoArgs;
        if (method == null)
        {
            if (Timing.TotalTime >= _nextPanelQueueWarnLogAllowedTime)
            {
                LogPanelDebug($"panel queue failed id={item?.ID}: AddToGUIUpdateList method not found");
                _nextPanelQueueWarnLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
            return;
        }

        try
        {
            if (ReferenceEquals(method, AddToGuiUpdateListMethodWithOrder))
            {
                method.Invoke(_panelFrame, new object[] { false, 1 });
            }
            else
            {
                method.Invoke(_panelFrame, null);
            }

            if (Timing.TotalTime >= _nextPanelQueueLogAllowedTime)
            {
                int argCount = method.GetParameters().Length;
                LogPanelDebug(
                    $"panel queued id={item?.ID} methodArgs={argCount} visible={_panelFrame.Visible} enabled={_panelFrame.Enabled}");
                _nextPanelQueueLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
        }
        catch (Exception ex)
        {
            if (Timing.TotalTime >= _nextPanelQueueWarnLogAllowedTime)
            {
                LogPanelDebug($"panel queue exception id={item?.ID}: {ex.GetType().Name}: {ex.Message}");
                _nextPanelQueueWarnLogAllowedTime = Timing.TotalTime + PanelQueueLogCooldown;
            }
        }
    }

    private void UpdateClientPanel()
    {
        Character controlled = null;
        bool isSelected = false;
        bool isInControlledInventory = false;
        bool isNearby = false;
        bool shouldShow = false;
        float distance = -1f;

        bool panelCandidate = SessionVariant || UseInPlaceSession;
        if (item == null || item.Removed || !panelCandidate)
        {
            LogPanelEval("skip:candidate", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("invalid item or unsupported panel mode");
            SetPanelVisible(false, "invalid item or unsupported panel mode");
            return;
        }

        if (UseInPlaceSession && !SessionVariant && !_cachedSessionOpen)
        {
            controlled = Character.Controlled;
            LogPanelEval("hide:closed_inplace", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("in-place session closed");
            SetPanelVisible(false, "in-place session closed");
            return;
        }

        controlled = Character.Controlled;
        if (controlled == null)
        {
            LogPanelEval("hide:no_controlled", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("no controlled character");
            SetPanelVisible(false, "no controlled character");
            return;
        }

        isSelected = controlled.SelectedItem == item || controlled.SelectedSecondaryItem == item;
        isInControlledInventory = item.ParentInventory?.Owner == controlled;
        float distanceSq = Vector2.DistanceSquared(controlled.WorldPosition, item.WorldPosition);
        distance = (float)Math.Sqrt(Math.Max(0f, distanceSq));
        isNearby = distanceSq <= PanelInteractionRange * PanelInteractionRange;
        if (UseInPlaceSession)
        {
            shouldShow = _cachedSessionOpen && (isSelected || isNearby);
        }
        else if (SessionVariant)
        {
            shouldShow = _cachedSessionOpen && (isSelected || isInControlledInventory);
        }
        else
        {
            shouldShow = isSelected;
        }
        if (!shouldShow)
        {
            LogPanelEval("hide:outside", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("outside visibility conditions");
            SetPanelVisible(false, "outside visibility conditions");
            return;
        }

        if (isSelected)
        {
            ClaimClientPanelFocus("selected");
        }
        else
        {
            RefreshClientPanelFocusLeaseIfOwned();
            bool focusExpired = _clientPanelFocusItemId <= 0 || Timing.TotalTime > _clientPanelFocusUntil;
            if (focusExpired)
            {
                if (UseInPlaceSession && isNearby)
                {
                    ClaimClientPanelFocus("in-place-nearby");
                }
            }
        }

        if (_clientPanelFocusItemId > 0 && _clientPanelFocusItemId != item.ID && Timing.TotalTime <= _clientPanelFocusUntil)
        {
            LogPanelEval("hide:focus_owner", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, $"focus owner={_clientPanelFocusItemId} reason={_clientPanelFocusReason}");
            return;
        }

        if (_clientPanelFocusItemId <= 0 || Timing.TotalTime > _clientPanelFocusUntil)
        {
            ClaimClientPanelFocus("fallback-claim");
        }

        if (_clientPanelFocusItemId != item.ID)
        {
            LogPanelEval("hide:focus_mismatch", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, $"focus mismatch owner={_clientPanelFocusItemId}");
            return;
        }

        EnsurePanelCreated();
        if (_panelFrame == null)
        {
            LogPanelEval("hide:panel_not_created", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, "panel not created");
            return;
        }

        LogPanelEval("show:eligible", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
        SetPanelVisible(true, "eligible");
        QueuePanelForGuiUpdate();
        UpdateClientPanelVisuals();
    }

    private void UpdateClientPanelVisuals()
    {
        if (_panelFrame == null) { return; }
        RefreshPanelEntrySnapshot();

        int pageCount = GetPanelEntryPageCount();
        int safePage = Math.Max(0, Math.Min(_panelEntryPageIndex, Math.Max(0, pageCount - 1)));
        _panelEntryPageIndex = safePage;
        int totalAmount = 0;
        for (int i = 0; i < _panelEntrySnapshot.Count; i++)
        {
            totalAmount += Math.Max(0, _panelEntrySnapshot[i]?.Amount ?? 0);
        }

        if (_panelTitle != null)
        {
            _panelTitle.Text = $"{T("dbiotest.panel.title", "Database Terminal")} [{_resolvedDatabaseId}]";
        }

        if (_panelPageInfo != null)
        {
            _panelPageInfo.Text =
                $"{T("dbiotest.terminal.page", "Page")}: {safePage + 1}/{Math.Max(1, pageCount)} | " +
                $"{T("dbiotest.terminal.entries", "Entries")}: {_panelEntrySnapshot.Count} | " +
                $"{T("dbiotest.terminal.amount", "Amount")}: {totalAmount}";
        }

        if (_panelPrevButton != null)
        {
            _panelPrevButton.Enabled = _cachedSessionOpen && safePage > 0;
        }

        if (_panelNextButton != null)
        {
            _panelNextButton.Enabled = _cachedSessionOpen && (safePage + 1) < pageCount;
        }

        if (_panelCloseButton != null)
        {
            _panelCloseButton.Enabled = _cachedSessionOpen;
        }

        if (_panelStatusText != null)
        {
            _panelStatusText.Text = _cachedSessionOpen
                ? T("dbiotest.panel.takehint", "Click an icon to move 1 item to buffer.")
                : T("dbiotest.panel.closedhint", "Session closed. Use Open button on terminal.");
        }

        UpdatePanelBufferVisuals();
        ApplyPanelEntriesToButtons();
    }

    private void UpdatePanelBufferVisuals()
    {
        if (!IsFixedTerminal || _panelBufferFrame == null) { return; }

        var inventory = GetTerminalInventory();
        if (_panelBufferInfo != null)
        {
            _panelBufferInfo.Text = inventory == null
                ? T("dbiotest.panel.bufferunavailable", "Buffer unavailable")
                : T("dbiotest.panel.bufferhint", "Buffer");
        }

        if (inventory == null) { return; }

        inventory.RectTransform = _panelBufferFrame.RectTransform;
        var cam = GameMain.GameScreen?.Cam;
        if (cam != null)
        {
            inventory.Update((float)Timing.Step, cam);
        }
    }

    private void RequestPanelActionClient(TerminalPanelAction action)
    {
        if (action == TerminalPanelAction.None) { return; }
        if (Timing.TotalTime < _nextClientPanelActionAllowedTime)
        {
            LogPanelDebug($"action blocked by cooldown: {action}");
            return;
        }
        _nextClientPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        LogPanelDebug(
            $"action requested: {action} source=cs_panel id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"cachedOpen={_cachedSessionOpen} sessionActive={IsSessionActive()} inPlace={UseInPlaceSession} sessionVariant={SessionVariant}");

        if (IsServerAuthority)
        {
            LogPanelDebug($"action handled locally as server: {action} source=cs_panel");
            HandlePanelActionServer(action, Character.Controlled, "cs_panel_local");
            return;
        }

        _pendingClientAction = (byte)action;
        LogPanelDebug($"action sent to server event: {action} source=cs_panel");
        item.CreateClientEvent(this);
    }
#endif
}
