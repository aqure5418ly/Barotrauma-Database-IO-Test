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
using Microsoft.Xna.Framework.Input;
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

    private void LogHandheldDiag(string stage, Character controlled = null, Character actor = null)
    {
        if (IsFixedTerminal || !ModFileLog.IsDebugEnabled) { return; }
        if (Timing.TotalTime < _nextHandheldDiagLogAt) { return; }
        _nextHandheldDiagLogAt = Timing.TotalTime + 0.2;

        controlled ??= Character.Controlled;
        int controlledId = controlled?.ID ?? -1;
        int actorId = actor?.ID ?? -1;
        int selectedId = controlled?.SelectedItem?.ID ?? -1;
        int secondaryId = controlled?.SelectedSecondaryItem?.ID ?? -1;
        bool panelVisible = _panelFrame?.Visible ?? false;
        bool panelEnabled = _panelFrame?.Enabled ?? false;
        bool manualHide = Timing.TotalTime < _panelManualHideUntil;

        ModFileLog.WriteDebug(
            "Terminal",
            $"{Constants.LogPrefix} HandheldDiag stage={stage} id={item?.ID} actor={actorId} ctrl={controlledId} " +
            $"sid={selectedId} ssid={secondaryId} armed={_handheldPanelArmedByUse} " +
            $"panel={panelVisible}/{panelEnabled} manualHide={manualHide}");
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

        bool shouldEnableXmlPanel = false;
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
            $"|cachedOpen={_cachedSessionOpen}|sessionActive={sessionActive}|inPlace={UseInPlaceSession}" +
            $"|focus={focusOwner}|focusRemain={focusRemaining:0.00}|panel={panelVisible}/{panelEnabled}|sid={selectedId}|ssid={selectedSecondaryId}";
        if (signature == _lastPanelEvalSignature && Timing.TotalTime < _nextPanelEvalLogAllowedTime) { return; }

        _lastPanelEvalSignature = signature;
        _nextPanelEvalLogAllowedTime = Timing.TotalTime + PanelEvalLogCooldown;
        LogPanelDebug($"eval {signature}");
    }

    private enum LocalSortMode : int
    {
        Name = 0,
        Amount = 1,
        Quality = 2
    }

    private const int LeftClickTakeGroupCap = 8;
    private static readonly (string key, string fallback, int categoryFlag)[] PanelCategories = new[]
    {
        ("dbiotest.category.all", "All", -1),
        ("dbiotest.category.material", "Materials", (int)MapEntityCategory.Material),
        ("dbiotest.category.medical", "Medical", (int)MapEntityCategory.Medical),
        ("dbiotest.category.weapon", "Weapons", (int)MapEntityCategory.Weapon),
        ("dbiotest.category.electrical", "Electrical", (int)MapEntityCategory.Electrical),
        ("dbiotest.category.equipment", "Equipment", (int)MapEntityCategory.Equipment),
        ("dbiotest.category.misc", "Misc", (int)MapEntityCategory.Misc)
    };

    private int GetIconGridColumns()
    {
        return _cellSizeMode switch
        {
            CellSizeMode.Large => 8,
            CellSizeMode.Medium => 10,
            CellSizeMode.Small => 12,
            _ => 10
        };
    }

    private string GetCellSizeLabel()
    {
        return _cellSizeMode switch
        {
            CellSizeMode.Large => T("dbiotest.ui.cellsize.large", "Large"),
            CellSizeMode.Small => T("dbiotest.ui.cellsize.small", "Small"),
            _ => T("dbiotest.ui.cellsize.medium", "Medium")
        };
    }

    private string GetCellSizeButtonLabel()
    {
        string template = T("dbiotest.ui.cellsize.button", "Cell: {0}");
        string value = GetCellSizeLabel();
        try
        {
            return string.Format(template, value);
        }
        catch
        {
            return $"{template} {value}";
        }
    }

    private void CycleCellSizeMode()
    {
        _cellSizeMode = _cellSizeMode switch
        {
            CellSizeMode.Large => CellSizeMode.Medium,
            CellSizeMode.Medium => CellSizeMode.Small,
            _ => CellSizeMode.Large
        };
        _runtimeCellSizeMode = _cellSizeMode;
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

            string variantKey = fields.Length > 6 ? (fields[6] ?? "").Trim() : "";
            bool hasContained = false;
            if (fields.Length > 7)
            {
                string containedRaw = (fields[7] ?? "").Trim();
                hasContained = string.Equals(containedRaw, "1", StringComparison.Ordinal) ||
                               string.Equals(containedRaw, "true", StringComparison.OrdinalIgnoreCase);
            }

            int variantQuality = quality;
            if (fields.Length > 8)
            {
                int.TryParse(fields[8] ?? "0", out variantQuality);
            }

            float variantCondition = condition;
            if (fields.Length > 9)
            {
                float.TryParse(
                    fields[9] ?? "100",
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out variantCondition);
            }

            if (string.IsNullOrWhiteSpace(variantKey))
            {
                // Backward-compatible fallback for legacy 6-field payloads.
                variantKey = $"legacy:{id.ToLowerInvariant()}|q={Math.Max(0, variantQuality)}|c={Math.Max(0f, variantCondition):0.00}|r={i}";
            }

            rows.Add(new TerminalVirtualEntry
            {
                Identifier = id,
                PrefabIdentifier = string.IsNullOrWhiteSpace(prefabId) ? id : prefabId,
                DisplayName = displayName,
                VariantKey = variantKey,
                HasContainedItems = hasContained,
                VariantQuality = Math.Max(0, variantQuality),
                VariantCondition = Math.Max(0f, variantCondition),
                CategoryInt = ResolveCategoryForIdentifier(string.IsNullOrWhiteSpace(prefabId) ? id : prefabId),
                Amount = Math.Max(0, amount),
                BestQuality = Math.Max(0, quality),
                AverageCondition = Math.Max(0f, condition)
            });
        }

        return rows;
    }

    private int ResolveCategoryForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return 0; }

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab == null) { return 0; }
        return (int)prefab.Category;
    }

    private bool RefreshPanelEntrySnapshot(bool force = false)
    {
        if (!force && Timing.TotalTime < _nextPanelEntryRefreshAt) { return false; }
        _nextPanelEntryRefreshAt = Timing.TotalTime + PanelEntryRefreshInterval;

        List<TerminalVirtualEntry> source;
        if (IsServerAuthority)
        {
            source = GetVirtualViewSnapshot(refreshCurrentPage: false);
        }
        else
        {
            source = ParsePanelEntriesFromLuaPayload();
        }

        var nextSnapshot = new List<TerminalVirtualEntry>();
        if (source != null)
        {
            foreach (var entry in source)
            {
                if (entry == null) { continue; }
                string identifier = (entry.Identifier ?? "").Trim();
                if (string.IsNullOrWhiteSpace(identifier)) { continue; }
                string prefabIdentifier = string.IsNullOrWhiteSpace(entry.PrefabIdentifier) ? identifier : entry.PrefabIdentifier.Trim();
                string localizedDisplay = ResolveDisplayNameForIdentifier(identifier);
                string displayName = string.IsNullOrWhiteSpace(localizedDisplay)
                    ? (string.IsNullOrWhiteSpace(entry.DisplayName) ? identifier : entry.DisplayName.Trim())
                    : localizedDisplay;
                string variantKey = string.IsNullOrWhiteSpace(entry.VariantKey)
                    ? $"local:{identifier.ToLowerInvariant()}|q={Math.Max(0, entry.VariantQuality > 0 ? entry.VariantQuality : entry.BestQuality)}|c={Math.Max(0f, entry.VariantCondition > 0f ? entry.VariantCondition : entry.AverageCondition):0.00}|i={nextSnapshot.Count}"
                    : entry.VariantKey.Trim();

                nextSnapshot.Add(new TerminalVirtualEntry
                {
                    Identifier = identifier,
                    PrefabIdentifier = prefabIdentifier,
                    DisplayName = displayName,
                    VariantKey = variantKey,
                    HasContainedItems = entry.HasContainedItems,
                    VariantQuality = Math.Max(0, entry.VariantQuality > 0 ? entry.VariantQuality : entry.BestQuality),
                    VariantCondition = Math.Max(0f, entry.VariantCondition > 0f ? entry.VariantCondition : entry.AverageCondition),
                    CategoryInt = ResolveCategoryForIdentifier(prefabIdentifier),
                    Amount = Math.Max(0, entry.Amount),
                    BestQuality = Math.Max(0, entry.BestQuality),
                    AverageCondition = Math.Max(0f, entry.AverageCondition)
                });
            }
        }

        nextSnapshot = nextSnapshot
            .OrderBy(entry => entry.DisplayName ?? entry.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Identifier ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.VariantQuality)
            .ThenBy(entry => entry.VariantCondition)
            .ThenBy(entry => entry.VariantKey ?? "", StringComparer.Ordinal)
            .ToList();

        bool changed = !ArePanelEntryListsEqual(_panelEntrySnapshot, nextSnapshot);
        if (!changed) { return false; }

        _panelEntrySnapshot.Clear();
        _panelEntrySnapshot.AddRange(nextSnapshot);
        return true;
    }

    private static bool ArePanelEntryListsEqual(
        IReadOnlyList<TerminalVirtualEntry> current,
        IReadOnlyList<TerminalVirtualEntry> next)
    {
        if (ReferenceEquals(current, next)) { return true; }
        if (current == null || next == null) { return false; }
        if (current.Count != next.Count) { return false; }

        for (int i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a == null && b == null) { continue; }
            if (a == null || b == null) { return false; }
            if (!string.Equals(a.Identifier ?? "", b.Identifier ?? "", StringComparison.OrdinalIgnoreCase)) { return false; }
            if (!string.Equals(a.PrefabIdentifier ?? "", b.PrefabIdentifier ?? "", StringComparison.OrdinalIgnoreCase)) { return false; }
            if (!string.Equals(a.DisplayName ?? "", b.DisplayName ?? "", StringComparison.Ordinal)) { return false; }
            if (!string.Equals(a.VariantKey ?? "", b.VariantKey ?? "", StringComparison.Ordinal)) { return false; }
            if (a.HasContainedItems != b.HasContainedItems) { return false; }
            if (a.VariantQuality != b.VariantQuality) { return false; }
            if (Math.Abs(a.VariantCondition - b.VariantCondition) > 0.001f) { return false; }
            if (a.CategoryInt != b.CategoryInt) { return false; }
            if (a.Amount != b.Amount) { return false; }
            if (a.BestQuality != b.BestQuality) { return false; }
            if (Math.Abs(a.AverageCondition - b.AverageCondition) > 0.001f) { return false; }
        }

        return true;
    }

    private static bool MatchCategory(TerminalVirtualEntry entry, int categoryFlag)
    {
        if (entry == null) { return false; }
        if (categoryFlag < 0) { return true; }
        return entry.CategoryInt == categoryFlag;
    }

    private static bool MatchSearch(TerminalVirtualEntry entry, string searchText)
    {
        if (entry == null) { return false; }
        if (string.IsNullOrWhiteSpace(searchText)) { return true; }
        string keyword = searchText.Trim();
        if (keyword.Length <= 0) { return true; }

        string haystack = $"{entry.DisplayName} {entry.Identifier}".ToLowerInvariant();
        return haystack.Contains(keyword.ToLowerInvariant());
    }

    private int ComparePanelEntries(TerminalVirtualEntry left, TerminalVirtualEntry right)
    {
        left ??= new TerminalVirtualEntry();
        right ??= new TerminalVirtualEntry();

        int cmp;
        switch ((LocalSortMode)_localSortMode)
        {
            case LocalSortMode.Amount:
                cmp = left.Amount.CompareTo(right.Amount);
                break;
            case LocalSortMode.Quality:
                cmp = left.BestQuality.CompareTo(right.BestQuality);
                break;
            default:
                cmp = StringComparer.OrdinalIgnoreCase.Compare(
                    left.DisplayName ?? left.Identifier ?? "",
                    right.DisplayName ?? right.Identifier ?? "");
                break;
        }

        if (cmp == 0)
        {
            cmp = StringComparer.OrdinalIgnoreCase.Compare(left.Identifier ?? "", right.Identifier ?? "");
        }
        if (cmp == 0)
        {
            cmp = left.VariantQuality.CompareTo(right.VariantQuality);
        }
        if (cmp == 0)
        {
            cmp = left.VariantCondition.CompareTo(right.VariantCondition);
        }
        if (cmp == 0)
        {
            cmp = StringComparer.Ordinal.Compare(left.VariantKey ?? "", right.VariantKey ?? "");
        }
        if (_localSortDescending) { cmp = -cmp; }
        return cmp;
    }

    private string GetSortButtonLabel()
    {
        string key;
        string fallback;
        switch ((LocalSortMode)_localSortMode)
        {
            case LocalSortMode.Amount:
                key = _localSortDescending ? "dbiotest.ui.sort.countdesc" : "dbiotest.ui.sort.countasc";
                fallback = _localSortDescending ? "Count High-Low" : "Count Low-High";
                break;
            case LocalSortMode.Quality:
                key = _localSortDescending ? "dbiotest.ui.sort.qualitydesc" : "dbiotest.ui.sort.qualityasc";
                fallback = _localSortDescending ? "Quality High-Low" : "Quality Low-High";
                break;
            default:
                key = _localSortDescending ? "dbiotest.ui.sort.namedesc" : "dbiotest.ui.sort.nameasc";
                fallback = _localSortDescending ? "Name Z-A" : "Name A-Z";
                break;
        }

        return T(key, fallback);
    }

    private void CycleLocalSortMode()
    {
        switch ((LocalSortMode)_localSortMode)
        {
            case LocalSortMode.Name:
                if (!_localSortDescending)
                {
                    _localSortDescending = true;
                }
                else
                {
                    _localSortMode = (int)LocalSortMode.Amount;
                    _localSortDescending = false;
                }
                break;
            case LocalSortMode.Amount:
                if (!_localSortDescending)
                {
                    _localSortDescending = true;
                }
                else
                {
                    _localSortMode = (int)LocalSortMode.Quality;
                    _localSortDescending = false;
                }
                break;
            default:
                if (!_localSortDescending)
                {
                    _localSortDescending = true;
                }
                else
                {
                    _localSortMode = (int)LocalSortMode.Name;
                    _localSortDescending = false;
                }
                break;
        }
    }

    private static bool IsSingleTakeModifierPressed()
    {
        var state = Keyboard.GetState();
        return state.IsKeyDown(Keys.LeftShift) || state.IsKeyDown(Keys.RightShift);
    }

    private static int ResolveTakeStackSize(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return 1; }
        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        int maxStack = prefab?.MaxStackSize ?? 1;
        if (maxStack <= 1) { return 1; }
        return Math.Max(1, Math.Min(maxStack, LeftClickTakeGroupCap));
    }

    private static Sprite ResolveEntryIcon(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return null; }
        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        return prefab?.InventoryIcon ?? prefab?.Sprite;
    }

    private string BuildItemTooltip(TerminalVirtualEntry entry, bool hasSecondaryClick)
    {
        if (entry == null) { return ""; }
        string displayName = entry.DisplayName ?? entry.Identifier ?? "";
        int amount = Math.Max(0, entry.Amount);
        int quality = Math.Max(0, entry.VariantQuality > 0 ? entry.VariantQuality : entry.BestQuality);
        float condition = Math.Max(0f, entry.VariantCondition > 0f ? entry.VariantCondition : entry.AverageCondition);
        string hasContainedText = entry.HasContainedItems
            ? T("dbiotest.panel.variant.contained.yes", "Yes")
            : T("dbiotest.panel.variant.contained.no", "No");
        string secondaryHint = hasSecondaryClick
            ? T("dbiotest.panel.takehint.secondary", "Right click: take 1")
            : T("dbiotest.panel.takehint.shift", "Shift+Left: take 1");

        return $"{displayName}\n" +
               $"{T("dbiotest.panel.variant.key", "Variant")}: {entry.VariantKey}\n" +
               $"{T("dbiotest.panel.variant.contained", "Contained Items")}: {hasContainedText}\n" +
               $"{T("dbiotest.terminal.amount", "Amount")}: {amount}\n" +
               $"{T("dbiotest.terminal.quality", "Quality")}: {quality} | {T("dbiotest.terminal.condition", "Condition")}: {condition:0.#}%\n" +
               $"{T("dbiotest.panel.takehint.primary", "Left click: take a stack")} ({LeftClickTakeGroupCap} cap)\n" +
               $"{secondaryHint}";
    }

    private bool TryBindSecondaryClick(GUIButton button, Func<GUIButton, object, bool> handler)
    {
        if (button == null || handler == null) { return false; }
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        string[] memberNames = { "OnSecondaryClicked", "SecondaryClicked", "OnSecondaryClick" };
        Type buttonType = button.GetType();

        for (int i = 0; i < memberNames.Length; i++)
        {
            string name = memberNames[i];
            try
            {
                var property = buttonType.GetProperty(name, flags);
                if (property != null && property.CanWrite)
                {
                    Delegate callback = Delegate.CreateDelegate(property.PropertyType, handler.Target, handler.Method, false);
                    if (callback != null)
                    {
                        property.SetValue(button, callback);
                        return true;
                    }
                }
            }
            catch
            {
                // Continue fallback probing.
            }

            try
            {
                var field = buttonType.GetField(name, flags);
                if (field != null)
                {
                    Delegate callback = Delegate.CreateDelegate(field.FieldType, handler.Target, handler.Method, false);
                    if (callback != null)
                    {
                        field.SetValue(button, callback);
                        return true;
                    }
                }
            }
            catch
            {
                // Continue fallback probing.
            }
        }

        return false;
    }

    private void CreateIconCell(GUILayoutGroup row, TerminalVirtualEntry entry)
    {
        if (row == null || entry == null) { return; }
        int columns = Math.Max(1, GetIconGridColumns());

        var button = new GUIButton(
            new RectTransform(new Vector2(1f / columns, 1f), row.RectTransform),
            "",
            style: "GUIButtonSmallFreeScale");

        Sprite icon = ResolveEntryIcon(entry.PrefabIdentifier ?? entry.Identifier ?? "");
        if (icon != null)
        {
            var iconImage = new GUIImage(
                new RectTransform(new Vector2(0.9f, 0.9f), button.RectTransform, Anchor.Center),
                icon,
                scaleToFit: true);
            iconImage.CanBeFocused = false;
        }

        var amountLabel = new GUITextBlock(
            new RectTransform(new Vector2(0.9f, 0.32f), button.RectTransform, Anchor.BottomRight),
            $"x{Math.Max(0, entry.Amount)}",
            font: GUIStyle.SmallFont,
            textAlignment: Alignment.BottomRight);
        amountLabel.TextColor = Color.White;
        amountLabel.CanBeFocused = false;

        int variantQuality = Math.Max(0, entry.VariantQuality > 0 ? entry.VariantQuality : entry.BestQuality);
        float variantCondition = Math.Max(0f, entry.VariantCondition > 0f ? entry.VariantCondition : entry.AverageCondition);
        string variantBadge = $"Q{variantQuality}";
        if (entry.HasContainedItems)
        {
            variantBadge += " C";
        }
        variantBadge += $" {MathF.Round(variantCondition)}%";
        var badgeLabel = new GUITextBlock(
            new RectTransform(new Vector2(0.95f, 0.24f), button.RectTransform, Anchor.TopLeft),
            variantBadge,
            font: GUIStyle.SmallFont,
            textAlignment: Alignment.TopLeft);
        badgeLabel.TextColor = Color.LightGray;
        badgeLabel.CanBeFocused = false;

        bool hasSecondaryClick = TryBindSecondaryClick(
            button,
            (_, __) =>
            {
                RequestPanelTakeByIdentifierClient(entry.Identifier, entry.VariantKey, 1);
                return true;
            });

        button.ToolTip = BuildItemTooltip(entry, hasSecondaryClick);
        button.Enabled = Math.Max(0, entry.Amount) > 0;
        button.OnClicked = (_, __) =>
        {
            int count = IsSingleTakeModifierPressed() ? 1 : ResolveTakeStackSize(entry.Identifier);
            RequestPanelTakeByIdentifierClient(entry.Identifier, entry.VariantKey, count);
            return true;
        };
    }

    private List<TerminalVirtualEntry> BuildFilteredEntries()
    {
        var filtered = _panelEntrySnapshot
            .Where(entry => MatchCategory(entry, _selectedCategoryFlag))
            .Where(entry => MatchSearch(entry, _currentSearchText))
            .ToList();
        filtered.Sort(ComparePanelEntries);
        return filtered;
    }

    private float GetIconGridRowRelativeHeight(int rowCount, int columns)
    {
        if (_panelIconGridList == null) { return 0.28f; }

        float width = Math.Max(1f, _panelIconGridList.Rect.Width);
        float height = Math.Max(1f, _panelIconGridList.Rect.Height);
        float aspect = width / height;
        float squareHeight = aspect / Math.Max(1, columns);

        // When there are only a few rows, deliberately allocate more height so cells don't look like thin bars.
        float sparseBoostHeight = rowCount <= 0 ? squareHeight : (0.75f / Math.Max(1, rowCount));
        float targetHeight = rowCount <= 3
            ? Math.Max(squareHeight, sparseBoostHeight)
            : squareHeight;

        float minClamp;
        float maxClamp;
        switch (_cellSizeMode)
        {
            case CellSizeMode.Large:
                minClamp = 0.16f;
                maxClamp = 0.30f;
                break;
            case CellSizeMode.Small:
                minClamp = 0.11f;
                maxClamp = 0.20f;
                break;
            default:
                minClamp = 0.13f;
                maxClamp = 0.24f;
                break;
        }

        // Clamp per mode to keep behavior stable across HUD scales and aspect ratios.
        return Math.Max(minClamp, Math.Min(maxClamp, targetHeight));
    }

    private static int ComputeIconGridEntriesSignature(IReadOnlyList<TerminalVirtualEntry> entries)
    {
        if (entries == null || entries.Count <= 0) { return 0; }
        unchecked
        {
            int hash = 17;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) { continue; }
                hash = (hash * 31) + StringComparer.OrdinalIgnoreCase.GetHashCode(entry.Identifier ?? "");
                hash = (hash * 31) + StringComparer.Ordinal.GetHashCode(entry.VariantKey ?? "");
                hash = (hash * 31) + (entry.HasContainedItems ? 1 : 0);
                hash = (hash * 31) + Math.Max(0, entry.VariantQuality);
                hash = (hash * 31) + (int)MathF.Round(Math.Max(0f, entry.VariantCondition));
                hash = (hash * 31) + Math.Max(0, entry.Amount);
                hash = (hash * 31) + Math.Max(0, entry.BestQuality);
                hash = (hash * 31) + (int)MathF.Round(Math.Max(0f, entry.AverageCondition));
            }
            return hash;
        }
    }

    private void RefreshIconGrid(bool force = false)
    {
        if (_panelIconGridList?.Content == null) { return; }

        var filtered = BuildFilteredEntries();
        int activeColumns = Math.Max(1, GetIconGridColumns());
        int rowCount = Math.Max(1, (int)Math.Ceiling(filtered.Count / (double)activeColumns));
        float rowHeight = GetIconGridRowRelativeHeight(rowCount, activeColumns);
        int entrySignature = ComputeIconGridEntriesSignature(filtered);
        int gridWidth = _panelIconGridList.Rect.Width;
        int gridHeight = _panelIconGridList.Rect.Height;
        string signature =
            $"{_cachedSessionOpen}|{_localSortMode}|{_localSortDescending}|{_selectedCategoryFlag}|{(_currentSearchText ?? "").Trim()}|" +
            $"{filtered.Count}|mode={_cellSizeMode}|cols={activeColumns}|{rowCount}|{rowHeight:0.###}|{gridWidth}x{gridHeight}|{entrySignature}";
        if (!force && signature == _lastIconGridRenderSignature) { return; }
        _lastIconGridRenderSignature = signature;
        LogPanelDebug($"grid rebuild mode={_cellSizeMode} cols={activeColumns} rows={rowCount} rowHeight={rowHeight:0.###} grid={gridWidth}x{gridHeight} entries={filtered.Count}");

        _panelIconGridList.Content.ClearChildren();

        if (filtered.Count <= 0)
        {
            new GUITextBlock(
                new RectTransform(new Vector2(1f, 0.12f), _panelIconGridList.Content.RectTransform),
                T("dbiotest.ui.empty", "No items in this session."),
                textAlignment: Alignment.Center);
            return;
        }

        for (int r = 0; r < rowCount; r++)
        {
            // GUIListBox handles row height more reliably when each row is a frame element.
            var rowElement = new GUIFrame(
                new RectTransform(new Vector2(1f, rowHeight), _panelIconGridList.Content.RectTransform),
                style: "ListBoxElementSquare")
            {
                CanBeFocused = false
            };
            rowElement.HoverColor = rowElement.Color;

            var row = new GUILayoutGroup(
                new RectTransform(Vector2.One, rowElement.RectTransform),
                isHorizontal: true)
            {
                AbsoluteSpacing = 2
            };

            for (int c = 0; c < activeColumns; c++)
            {
                int idx = r * activeColumns + c;
                if (idx >= filtered.Count) { break; }
                CreateIconCell(row, filtered[idx]);
            }
        }
    }

    private void RequestPanelTakeByIdentifierClient(string identifier, string variantKey, int count)
    {
        if (string.IsNullOrWhiteSpace(identifier)) { return; }
        string wantedVariantKey = (variantKey ?? "").Trim();
        int takeCount = Math.Max(1, count);
        if (Timing.TotalTime < _nextClientPanelActionAllowedTime)
        {
            LogPanelDebug($"take blocked by cooldown identifier='{identifier}' variant='{wantedVariantKey}' count={takeCount}");
            return;
        }
        _nextClientPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;

        if (IsServerAuthority)
        {
            Character actor = Character.Controlled;
            string result = TryTakeByVariantKeyCountFromVirtualSession(identifier, wantedVariantKey, takeCount, actor);
            if (string.IsNullOrEmpty(result))
            {
                LogPanelDebug($"take local success identifier='{identifier}' variant='{wantedVariantKey}' count={takeCount}");
            }
            else
            {
                LogPanelDebug($"take local failed identifier='{identifier}' variant='{wantedVariantKey}' count={takeCount} reason='{result}'");
            }
            RefreshPanelEntrySnapshot(force: true);
            RefreshIconGrid(force: true);
            UpdateClientPanelVisuals();
            return;
        }

        _pendingClientTakeIdentifier = identifier;
        _pendingClientTakeCount = takeCount;
        _pendingClientTakeVariantKey = wantedVariantKey;
        _pendingClientAction = (byte)TerminalPanelAction.TakeByIdentifier;
        LogPanelDebug($"take sent to server identifier='{identifier}' variant='{wantedVariantKey}' count={takeCount}");
        item.CreateClientEvent(this);
    }

    private void SetPanelVisible(bool visible, string reason)
    {
        bool prevFrameVisible = _panelFrame?.Visible ?? false;
        bool prevFrameEnabled = _panelFrame?.Enabled ?? false;
        if (visible && !_panelLastVisible && _cellSizeMode != _runtimeCellSizeMode)
        {
            _cellSizeMode = _runtimeCellSizeMode;
            _lastIconGridRenderSignature = "";
        }

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
                $"panel {(visible ? "show" : "hide")} id={item?.ID} reason={reason} " +
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

        _panelFrame = new GUIFrame(
            new RectTransform(new Vector2(0.6f, 0.78f), GUI.Canvas, Anchor.Center)
            {
                RelativeOffset = new Vector2(0f, -0.03f)
            },
            style: "ItemUI");
        _panelFrame.Visible = false;
        _panelFrame.Enabled = false;
        LogPanelDebug(
            $"panel created id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"rect=({_panelFrame.Rect.X},{_panelFrame.Rect.Y},{_panelFrame.Rect.Width},{_panelFrame.Rect.Height}) " +
            $"canvas=({GUI.Canvas.Rect.X},{GUI.Canvas.Rect.Y},{GUI.Canvas.Rect.Width},{GUI.Canvas.Rect.Height})");
        LogPanelDebug(
            $"panel draw queue methods id={item?.ID} withOrder={(AddToGuiUpdateListMethodWithOrder != null)} noArgs={(AddToGuiUpdateListMethodNoArgs != null)}");

        _panelMainLayout = new GUILayoutGroup(
            new RectTransform(new Vector2(0.95f, 0.95f), _panelFrame.RectTransform, Anchor.Center),
            isHorizontal: false)
        {
            AbsoluteSpacing = 6
        };

        var topBar = new GUILayoutGroup(
            new RectTransform(new Vector2(1f, 0.08f), _panelMainLayout.RectTransform),
            isHorizontal: true)
        {
            AbsoluteSpacing = 6
        };

        _panelTitle = new GUITextBlock(
            new RectTransform(new Vector2(0.82f, 1f), topBar.RectTransform),
            T("dbiotest.panel.title", "Database Terminal"),
            font: GUIStyle.SubHeadingFont,
            textAlignment: Alignment.CenterLeft);

        _panelCloseButton = new GUIButton(new RectTransform(new Vector2(0.18f, 1f), topBar.RectTransform), T("dbiotest.panel.close", "Close"));
        _panelCloseButton.OnClicked = (_, __) =>
        {
            _panelManualHideUntil = Timing.TotalTime + 1.0;
            if (!IsFixedTerminal)
            {
                _handheldPanelArmedByUse = false;
            }
            ReleaseClientPanelFocusIfOwned("manual close");
            SetPanelVisible(false, "manual close");
            return true;
        };

        _panelToolbarLayout = new GUILayoutGroup(
            new RectTransform(new Vector2(1f, 0.08f), _panelMainLayout.RectTransform),
            isHorizontal: true)
        {
            AbsoluteSpacing = 6
        };

        _cellSizeMode = _runtimeCellSizeMode;
        _panelSearchBox = new GUITextBox(
            new RectTransform(new Vector2(0.56f, 0.9f), _panelToolbarLayout.RectTransform),
            _currentSearchText ?? "",
            createClearButton: true);
        _panelSearchBox.ToolTip = T("dbiotest.ui.search.tooltip", "Search by item name or identifier.");
        _panelSearchBox.OnTextChanged += (_, text) =>
        {
            _currentSearchText = text ?? "";
            RefreshIconGrid(force: true);
            UpdateClientPanelVisuals();
            return true;
        };

        _panelSortButton = new GUIButton(
            new RectTransform(new Vector2(0.22f, 0.9f), _panelToolbarLayout.RectTransform),
            GetSortButtonLabel(),
            style: "GUIButtonSmall");
        _panelSortButton.OnClicked = (_, __) =>
        {
            CycleLocalSortMode();
            RefreshIconGrid(force: true);
            UpdateClientPanelVisuals();
            return true;
        };

        _panelCellSizeButton = new GUIButton(
            new RectTransform(new Vector2(0.22f, 0.9f), _panelToolbarLayout.RectTransform),
            GetCellSizeButtonLabel(),
            style: "GUIButtonSmall");
        _panelCellSizeButton.OnClicked = (_, __) =>
        {
            CycleCellSizeMode();
            RefreshIconGrid(force: true);
            UpdateClientPanelVisuals();
            return true;
        };

        _panelSummaryInfo = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.06f), _panelMainLayout.RectTransform),
            "",
            textAlignment: Alignment.CenterLeft);

        _panelContentLayout = new GUILayoutGroup(
            new RectTransform(new Vector2(1f, 0.58f), _panelMainLayout.RectTransform),
            isHorizontal: true)
        {
            AbsoluteSpacing = 6
        };

        _panelCategoryLayout = new GUILayoutGroup(
            new RectTransform(new Vector2(0.16f, 1f), _panelContentLayout.RectTransform),
            isHorizontal: false)
        {
            AbsoluteSpacing = 3
        };
        _panelCategoryButtons.Clear();
        for (int i = 0; i < PanelCategories.Length; i++)
        {
            int categoryFlag = PanelCategories[i].categoryFlag;
            string categoryLabel = T(PanelCategories[i].key, PanelCategories[i].fallback);
            var categoryButton = new GUIButton(
                new RectTransform(new Vector2(1f, 1f / PanelCategories.Length), _panelCategoryLayout.RectTransform),
                categoryLabel,
                style: "GUIButtonSmall");
            categoryButton.OnClicked = (_, __) =>
            {
                _selectedCategoryFlag = categoryFlag;
                RefreshIconGrid(force: true);
                UpdateClientPanelVisuals();
                return true;
            };
            _panelCategoryButtons.Add(categoryButton);
        }

        _panelIconGridList = new GUIListBox(
            new RectTransform(new Vector2(0.84f, 1f), _panelContentLayout.RectTransform))
        {
            Spacing = 0
        };

        _panelBufferInfo = null;
        _panelBufferFrame = null;
        _panelBufferDrawer = null;
        _panelBufferInfo = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.04f), _panelMainLayout.RectTransform),
            T("dbiotest.panel.bufferpartition", "Buffer (slots 1-5: input, 6-10: output)"),
            textAlignment: Alignment.Left);

        _panelBufferFrame = new GUIFrame(
            new RectTransform(new Vector2(1f, 0.12f), _panelMainLayout.RectTransform),
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

        _panelStatusText = new GUITextBlock(
            new RectTransform(new Vector2(1f, 0.08f), _panelMainLayout.RectTransform),
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

        if (item == null || item.Removed)
        {
            LogPanelEval("skip:candidate", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            ReleaseClientPanelFocusIfOwned("invalid item or unsupported panel mode");
            SetPanelVisible(false, "invalid item or unsupported panel mode");
            return;
        }

        if (Timing.TotalTime < _panelManualHideUntil)
        {
            controlled = Character.Controlled;
            LogPanelEval("hide:manual", controlled, isSelected, isInControlledInventory, isNearby, shouldShow, distance);
            SetPanelVisible(false, "manual hide cooldown");
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
        if (IsFixedTerminal)
        {
            shouldShow = isSelected || isNearby;
        }
        else
        {
            shouldShow = _handheldPanelArmedByUse && (isSelected || isInControlledInventory);
        }
        if (!shouldShow)
        {
            if (!IsFixedTerminal && !isSelected && !isInControlledInventory)
            {
                _handheldPanelArmedByUse = false;
            }
            if (!IsFixedTerminal)
            {
                LogHandheldDiag($"panel_hide selected={isSelected} inv={isInControlledInventory} nearby={isNearby}", controlled);
            }
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
        bool snapshotChanged = RefreshPanelEntrySnapshot();
        int totalAmount = 0;
        for (int i = 0; i < _panelEntrySnapshot.Count; i++)
        {
            totalAmount += Math.Max(0, _panelEntrySnapshot[i]?.Amount ?? 0);
        }

        if (_panelTitle != null)
        {
            _panelTitle.Text = $"{T("dbiotest.panel.title", "Database Terminal")} [{_resolvedDatabaseId}]";
        }

        if (_panelSummaryInfo != null)
        {
            _panelSummaryInfo.Text =
                $"{T("dbiotest.terminal.entries", "Entries")}: {_panelEntrySnapshot.Count} | " +
                $"{T("dbiotest.terminal.amount", "Amount")}: {totalAmount}";
        }

        if (_panelSortButton != null)
        {
            _panelSortButton.Text = GetSortButtonLabel();
            _panelSortButton.Enabled = true;
        }
        if (_panelCellSizeButton != null)
        {
            _panelCellSizeButton.Text = GetCellSizeButtonLabel();
            _panelCellSizeButton.Enabled = true;
        }

        if (_panelSearchBox != null &&
            !string.Equals(_panelSearchBox.Text ?? "", _currentSearchText ?? "", StringComparison.Ordinal))
        {
            _panelSearchBox.Text = _currentSearchText ?? "";
        }

        if (_panelCloseButton != null)
        {
            _panelCloseButton.Enabled = true;
        }

        if (_panelStatusText != null)
        {
            string takeHint = T("dbiotest.panel.takehint", "Left click takes a stack. Right click (or Shift+Left) takes 1.");
            if (IsFixedTerminal)
            {
                string flowHint = T("dbiotest.panel.bufferflow", "Input slots 1-5 auto-ingest, output slots 6-10 receive extracted items.");
                _panelStatusText.Text = $"{takeHint} | {flowHint}";
            }
            else
            {
                _panelStatusText.Text = takeHint;
            }
        }

        UpdatePanelBufferVisuals();
        // Always call RefreshIconGrid - the internal signature check handles dedup.
        RefreshIconGrid(force: snapshotChanged);
    }

    private void UpdatePanelBufferVisuals()
    {
        if (_panelBufferFrame == null) { return; }

        var inventory = GetTerminalInventory();
        if (_panelBufferInfo != null)
        {
            if (inventory == null)
            {
                _panelBufferInfo.Text = T("dbiotest.panel.bufferunavailable", "Buffer unavailable");
            }
            else
            {
                _panelBufferInfo.Text = IsFixedTerminal
                    ? T("dbiotest.panel.bufferpartition", "Buffer (slots 1-5: input, 6-10: output)")
                    : T("dbiotest.panel.bufferhint", "Buffer");
            }
        }

        if (inventory == null) { return; }

        inventory.RectTransform = _panelBufferFrame.RectTransform;
        var cam = GameMain.GameScreen?.Cam;
        if (cam != null)
        {
            inventory.Update((float)Timing.Step, cam);
        }
    }

#endif
}


