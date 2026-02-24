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
    private bool TryChangePage(int delta, Character actor)
    {
        if (!IsSessionActive() || _sessionPages.Count <= 0) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        int targetIndex = _sessionCurrentPageIndex + delta;
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        // Capture first, then fully rebuild page mapping from source to avoid stale page/index state.
        CaptureCurrentPageFromInventory();

        if (targetIndex < 0 || targetIndex >= _sessionPages.Count)
        {
            LoadCurrentPageIntoInventory(actor);
            return false;
        }

        _sessionCurrentPageIndex = targetIndex;
        LoadCurrentPageIntoInventory(actor);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);

        DebugConsole.NewMessage(
            $"{Constants.LogPrefix} Page switched to {_sessionCurrentPageIndex + 1}/{Math.Max(1, _sessionPages.Count)} for '{_resolvedDatabaseId}'.",
            Microsoft.Xna.Framework.Color.LightGray);

        return true;
    }

    private bool TryJumpToMatch(int direction, Character actor)
    {
        if (!IsSessionActive() || _sessionPages.Count <= 0) { return false; }
        if (direction == 0) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        string keyword = (SearchKeyword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(keyword)) { return false; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        CaptureCurrentPageFromInventory();

        int pageCount = _sessionPages.Count;
        int start = Math.Max(0, _sessionCurrentPageIndex);
        int stepDir = direction > 0 ? 1 : -1;
        for (int step = 1; step <= pageCount; step++)
        {
            int index = WrapPageIndex(start + (step * stepDir), pageCount);
            if (PageMatchesKeyword(index, keyword))
            {
                _sessionCurrentPageIndex = index;
                LoadCurrentPageIntoInventory(actor);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return true;
            }
        }

        LoadCurrentPageIntoInventory(actor);
        return false;
    }

    private bool TryCycleSortMode(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }
        SortModeIndex = (NormalizeSortModeIndex(SortModeIndex) + 1) % 4;
        return TryRebuildPagesAfterResort(actor);
    }

    private bool TryToggleSortOrder(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }
        SortDescending = !SortDescending;
        return TryRebuildPagesAfterResort(actor);
    }

    private bool TryCycleSortModeWhenClosed()
    {
        if (IsSessionActive()) { return false; }
        SortModeIndex = (NormalizeSortModeIndex(SortModeIndex) + 1) % 4;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryToggleSortOrderWhenClosed()
    {
        if (IsSessionActive()) { return false; }
        SortDescending = !SortDescending;
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryCompactStoreWhenClosed()
    {
        if (IsSessionActive()) { return false; }

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            string blockedLine = $"{Constants.LogPrefix} Compact blocked: '{_resolvedDatabaseId}' is locked by another terminal session.";
            DebugConsole.NewMessage(blockedLine, Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Terminal", blockedLine);
            return false;
        }

        try
        {
            var allEntries = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);
            int beforeEntries = allEntries.Count;
            int beforeItems = CountFlatItems(allEntries);
            var beforeDiag = GetCompactionDiagnostics(allEntries);

            var compacted = DatabaseStore.CompactSnapshot(allEntries);
            compacted.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
            int afterEntries = compacted.Count;
            int afterItems = CountFlatItems(compacted);
            int mergedEntries = Math.Max(0, beforeEntries - afterEntries);

            if (!DatabaseStore.WriteBackFromTerminalContainer(_resolvedDatabaseId, compacted, item.ID))
            {
                DatabaseStore.AppendItems(_resolvedDatabaseId, compacted);
            }

            string compactLine =
                $"{Constants.LogPrefix} Closed compact db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"beforeEntries={beforeEntries} beforeItems={beforeItems} afterEntries={afterEntries} afterItems={afterItems} mergedEntries={mergedEntries} " +
                $"eligible={beforeDiag.eligible} blockedCond={beforeDiag.blockedCondition} blockedContained={beforeDiag.blockedContained} " +
                $"uniqueEligibleKeys={beforeDiag.uniqueKeys} potentialMergeEntries={beforeDiag.potentialMergeEntries} " +
                $"sort={GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} desc={SortDescending}";
            DebugConsole.NewMessage(
                compactLine,
                mergedEntries > 0 ? Microsoft.Xna.Framework.Color.LightGreen : Microsoft.Xna.Framework.Color.Yellow);
            ModFileLog.Write("Terminal", compactLine);

            UpdateSummaryFromStore();
            UpdateDescriptionLocal();
            TrySyncSummary(force: true);
            return true;
        }
        finally
        {
            DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        }
    }

    private bool TryCompactSessionItems(Character actor)
    {
        if (!IsSessionActive()) { return false; }
        if (!CanRunPageMutatingAction()) { return false; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        int preferredPage = _sessionCurrentPageIndex;
        CaptureCurrentPageFromInventory();
        var beforeData = FlattenAllPages();
        int beforeEntries = beforeData.Count;
        int beforeItems = CountFlatItems(beforeData);
        var beforeDiag = GetCompactionDiagnostics(beforeData);

        var compacted = DatabaseStore.CompactSnapshot(beforeData);
        compacted.Sort((a, b) => CompareBySortMode(a, b, (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex), SortDescending));
        int afterEntries = compacted.Count;
        int afterItems = CountFlatItems(compacted);
        int mergedEntries = Math.Max(0, beforeEntries - afterEntries);

        BuildPagesBySlotUsage(compacted, inventory, preferredPage);
        LoadCurrentPageIntoInventory(actor ?? _sessionOwner);

        string compactLine =
            $"{Constants.LogPrefix} Session compact db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"beforeEntries={beforeEntries} beforeItems={beforeItems} afterEntries={afterEntries} afterItems={afterItems} mergedEntries={mergedEntries} " +
            $"eligible={beforeDiag.eligible} blockedCond={beforeDiag.blockedCondition} blockedContained={beforeDiag.blockedContained} " +
            $"uniqueEligibleKeys={beforeDiag.uniqueKeys} potentialMergeEntries={beforeDiag.potentialMergeEntries} " +
            $"sort={GetSortModeLabel((TerminalSortMode)NormalizeSortModeIndex(SortModeIndex))} desc={SortDescending} " +
            $"search='{(SearchKeyword ?? "").Trim()}' page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)}";
        DebugConsole.NewMessage(
            compactLine,
            mergedEntries > 0 ? Microsoft.Xna.Framework.Color.LightGreen : Microsoft.Xna.Framework.Color.Yellow);
        ModFileLog.Write("Terminal", compactLine);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryRebuildPagesAfterResort(Character actor)
    {
        if (!CanRunPageMutatingAction()) { return false; }
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }

        int preferredPage = _sessionCurrentPageIndex;
        CaptureCurrentPageFromInventory();
        // Sort mode/order only reorders the view; do not rewrite storage order here.
        BuildPagesBySlotUsage(_sessionEntries, inventory, preferredPage);
        LoadCurrentPageIntoInventory(actor ?? _sessionOwner);

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private static int NormalizeSortModeIndex(int raw)
    {
        if (raw < 0 || raw > 3) { return 0; }
        return raw;
    }

    private static int CompareBySortMode(ItemData left, ItemData right, TerminalSortMode mode, bool descending)
    {
        left ??= new ItemData();
        right ??= new ItemData();

        int cmp;
        switch (mode)
        {
            case TerminalSortMode.Condition:
                cmp = left.Condition.CompareTo(right.Condition);
                break;
            case TerminalSortMode.Quality:
                cmp = left.Quality.CompareTo(right.Quality);
                break;
            case TerminalSortMode.StackSize:
                cmp = Math.Max(1, left.StackSize).CompareTo(Math.Max(1, right.StackSize));
                break;
            default:
                cmp = string.Compare(left.Identifier ?? "", right.Identifier ?? "", StringComparison.OrdinalIgnoreCase);
                break;
        }

        if (cmp == 0)
        {
            cmp = string.Compare(left.Identifier ?? "", right.Identifier ?? "", StringComparison.OrdinalIgnoreCase);
        }

        return descending ? -cmp : cmp;
    }

    private bool EntryMatchesKeyword(ItemData entry, string keyword)
    {
        if (entry == null) { return false; }
        string needle = (keyword ?? "").Trim();
        if (string.IsNullOrWhiteSpace(needle)) { return true; }

        string haystack = GetSearchTextForIdentifier(entry.Identifier);
        if (string.IsNullOrWhiteSpace(haystack)) { return false; }
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private string GetSearchTextForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        if (_searchTextCache.TryGetValue(id, out string cached))
        {
            return cached ?? "";
        }

        var terms = new List<string>();
        AddSearchTerm(terms, id);

        string entityNameKey = $"entityname.{id}";
        AddSearchTerm(terms, TextManager.Get(entityNameKey)?.Value);
        AddSearchTerm(terms, TextManager.Get(entityNameKey.ToLowerInvariant())?.Value);

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab != null)
        {
            AddSearchTerm(terms, TryReadPrefabName(prefab, "Name"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "name"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "OriginalName"));
            AddSearchTerm(terms, TryReadPrefabName(prefab, "originalName"));
        }

        string combined = string.Join("\n", terms.Distinct(StringComparer.OrdinalIgnoreCase));
        _searchTextCache[id] = combined;
        return combined;
    }

    private static string TryReadPrefabName(ItemPrefab prefab, string memberName)
    {
        if (prefab == null || string.IsNullOrWhiteSpace(memberName)) { return ""; }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var prop = prefab.GetType().GetProperty(memberName, flags);
        if (prop != null)
        {
            return ExtractTextValue(prop.GetValue(prefab));
        }

        var field = prefab.GetType().GetField(memberName, flags);
        if (field != null)
        {
            return ExtractTextValue(field.GetValue(prefab));
        }

        return "";
    }

    private static string ExtractTextValue(object value)
    {
        if (value == null) { return ""; }
        if (value is string text) { return text; }

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var valueProp = value.GetType().GetProperty("Value", flags);
        if (valueProp != null && valueProp.PropertyType == typeof(string))
        {
            return valueProp.GetValue(value) as string ?? "";
        }

        return value.ToString() ?? "";
    }

    private static void AddSearchTerm(List<string> terms, string value)
    {
        if (terms == null) { return; }
        string trimmed = (value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) { return; }
        terms.Add(trimmed);
    }

    private string ResolveDisplayNameForIdentifier(string identifier)
    {
        string id = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(id)) { return ""; }

        var prefab = ItemPrefab.FindByIdentifier(id.ToIdentifier()) as ItemPrefab;
        if (prefab != null)
        {
            string name = TryReadPrefabName(prefab, "Name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }

            name = TryReadPrefabName(prefab, "OriginalName");
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name.Trim();
            }
        }

        string entityName = TextManager.Get($"entityname.{id}")?.Value;
        if (!string.IsNullOrWhiteSpace(entityName))
        {
            return entityName.Trim();
        }

        return id;
    }

    private bool PageMatchesKeyword(int pageIndex, string keyword)
    {
        if (pageIndex < 0 || pageIndex >= _sessionPageSourceIndices.Count) { return false; }
        var sourceIndices = _sessionPageSourceIndices[pageIndex];
        if (sourceIndices == null || sourceIndices.Count == 0) { return false; }

        foreach (int sourceIndex in sourceIndices)
        {
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            var entry = _sessionEntries[sourceIndex];
            if (EntryMatchesKeyword(entry, keyword))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasAnyEmptyBufferSlot(Inventory inventory)
    {
        if (inventory == null) { return false; }
        for (int slot = 0; slot < inventory.Capacity; slot++)
        {
            if (inventory.GetItemAt(slot) == null)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryExtractOneVirtualItemData(string identifier, out ItemData extracted)
    {
        extracted = null;
        string wanted = (identifier ?? "").Trim();
        if (string.IsNullOrWhiteSpace(wanted)) { return false; }

        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            var entry = _sessionEntries[i];
            if (entry == null) { continue; }
            if (!string.Equals(entry.Identifier ?? "", wanted, StringComparison.OrdinalIgnoreCase)) { continue; }

            if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
            {
                extracted = entry.Clone();
                extracted.StackSize = 1;
                _sessionEntries.RemoveAt(i);
                return true;
            }

            int stackSize = Math.Max(1, entry.StackSize);
            if (stackSize <= 1)
            {
                extracted = entry.Clone();
                extracted.StackSize = 1;
                _sessionEntries.RemoveAt(i);
                return true;
            }

            extracted = ExtractStackPart(entry, 1);
            if (entry.StackSize <= 0)
            {
                _sessionEntries.RemoveAt(i);
            }
            return extracted != null;
        }

        return false;
    }

    private static int WrapPageIndex(int index, int count)
    {
        if (count <= 0) { return 0; }
        int wrapped = index % count;
        if (wrapped < 0) { wrapped += count; }
        return wrapped;
    }

    private bool CanRunPageMutatingAction()
    {
        if (!IsSessionActive()) { return false; }
        if (_currentPageLoadedAt <= 0) { return true; }
        if (Timing.TotalTime - _currentPageLoadedAt >= PageActionSafetySeconds) { return true; }
        return false;
    }

    private void InitializePages(List<ItemData> sourceItems, Character actor)
    {
        _sessionWritebackCommitted = false;
        _currentPageFillVerified = false;
        var inventory = GetTerminalInventory();
        BuildPagesBySlotUsage(sourceItems, inventory);
        if (inventory != null)
        {
            SpawnService.ClearInventory(inventory);
        }
    }

    private void BuildPagesFromCurrentInventory()
    {
        if (!SessionVariant) { return; }

        _sessionWritebackCommitted = false;
        _currentPageFillVerified = false;
        var inventory = GetTerminalInventory();
        var currentItems = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        BuildPagesBySlotUsage(currentItems, inventory);
    }

    private bool LoadCurrentPageIntoInventory(Character actor)
    {
        var inventory = GetTerminalInventory();
        if (inventory == null) { return false; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return false; }

        // In C# panel mode, keep the terminal buffer empty until user explicitly takes one entry.
        // This avoids auto-spawning full pages into the container.
        if (EnableCsPanelOverlay)
        {
            SpawnService.ClearInventory(inventory);
            _currentPageLoadedAt = 0;
            _pendingPageFillCheckGeneration = -1;
            _pendingPageFillCheckExpectedCount = 0;
            _pendingPageFillCheckPageIndex = -1;
            _pendingPageFillCheckRetries = 0;
            _currentPageFillVerified = true;
            return true;
        }

        SpawnService.ClearInventory(inventory);

        _pageLoadGeneration++;
        int loadGeneration = _pageLoadGeneration;

        var pageItems = CloneItems(_sessionPages[_sessionCurrentPageIndex]);
        if (pageItems.Count > 0)
        {
            SpawnService.SpawnItemsIntoInventory(
                pageItems,
                inventory,
                actor ?? _sessionOwner,
                    () => IsSessionActive() && loadGeneration == _pageLoadGeneration);
        }

        _currentPageLoadedAt = Timing.TotalTime;
        _pendingPageFillCheckGeneration = loadGeneration;
        _pendingPageFillCheckAt = Timing.TotalTime + PageFillCheckDelaySeconds;
        _pendingPageFillCheckExpectedCount = CountFlatItems(pageItems);
        _pendingPageFillCheckPageIndex = _sessionCurrentPageIndex;
        _pendingPageFillCheckRetries = 0;
        _currentPageFillVerified = _pendingPageFillCheckExpectedCount <= 0;

        return true;
    }

    private void TryRunPendingPageFillCheck()
    {
        if (!IsSessionActive())
        {
            _pendingPageFillCheckGeneration = -1;
            _currentPageFillVerified = false;
            return;
        }

        if (_pendingPageFillCheckGeneration != _pageLoadGeneration) { return; }
        if (Timing.TotalTime < _pendingPageFillCheckAt) { return; }
        if (_pendingPageFillCheckPageIndex != _sessionCurrentPageIndex)
        {
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        var inventory = GetTerminalInventory();
        if (inventory == null)
        {
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        int actualCount = CountFlatItems(ItemSerializer.SerializeInventory(_sessionOwner, inventory));
        if (actualCount >= _pendingPageFillCheckExpectedCount)
        {
            _currentPageFillVerified = true;
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        if (_pendingPageFillCheckRetries >= MaxPageFillCheckRetries)
        {
            string warnLine =
                $"{Constants.LogPrefix} Page fill under target db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
                $"expected={_pendingPageFillCheckExpectedCount} actual={actualCount} retries={_pendingPageFillCheckRetries}";
            DebugConsole.NewMessage(warnLine, Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write("Terminal", warnLine);
            _currentPageFillVerified = false;
            _pendingPageFillCheckGeneration = -1;
            return;
        }

        int nextRetry = _pendingPageFillCheckRetries + 1;
        string retryLine =
            $"{Constants.LogPrefix} Page fill retry db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"page={Math.Max(1, _sessionCurrentPageIndex + 1)}/{Math.Max(1, _sessionPages.Count)} " +
            $"expected={_pendingPageFillCheckExpectedCount} actual={actualCount} retry={nextRetry}";
        DebugConsole.NewMessage(retryLine, Microsoft.Xna.Framework.Color.Yellow);
        ModFileLog.Write("Terminal", retryLine);

        if (LoadCurrentPageIntoInventory(_sessionOwner))
        {
            _pendingPageFillCheckRetries = nextRetry;
        }
        else
        {
            _pendingPageFillCheckGeneration = -1;
        }
    }

    private void CaptureCurrentPageFromInventory()
    {
        CaptureCurrentPageFromInventory(clearInventoryAfterCapture: true);
    }

    private void CaptureCurrentPageFromInventory(bool clearInventoryAfterCapture)
    {
        if (!IsSessionActive()) { return; }
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPages.Count) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return; }

        var serialized = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        int expectedCurrentPageCount = CountFlatItems(_sessionPages[_sessionCurrentPageIndex]);
        int actualCurrentPageCount = CountFlatItems(serialized);
        if (expectedCurrentPageCount > 0 && actualCurrentPageCount == 0 && !_currentPageFillVerified)
        {
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} Preserve source page on capture db='{_resolvedDatabaseId}' terminal={item?.ID} " +
                $"expected={expectedCurrentPageCount} actual={actualCurrentPageCount} verified={_currentPageFillVerified}.");
            serialized = CloneItems(_sessionPages[_sessionCurrentPageIndex]);
        }

        var sourceIndices = _sessionPageSourceIndices.Count > _sessionCurrentPageIndex
            ? _sessionPageSourceIndices[_sessionCurrentPageIndex]
            : new List<int>();
        ReplaceSessionEntriesBySourceIndices(sourceIndices, serialized);

        if (clearInventoryAfterCapture)
        {
            SpawnService.ClearInventory(inventory);
        }

        _pendingPageFillCheckGeneration = -1;
        _currentPageFillVerified = false;

        // Keep current page index stable after source mutation.
        BuildPagesBySlotUsage(_sessionEntries, inventory, _sessionCurrentPageIndex);
    }

    private List<ItemData> FlattenAllPages()
    {
        return CloneItems(_sessionEntries);
    }

    private int CountPendingPageItems()
    {
        if (!IsSessionActive() || _sessionEntries.Count == 0)
        {
            return 0;
        }

        var protectedIndices = GetCurrentPageSourceIndexSet();
        int count = 0;
        foreach (int sourceIndex in _viewIndices)
        {
            if (protectedIndices.Contains(sourceIndex)) { continue; }
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            count += Math.Max(1, _sessionEntries[sourceIndex]?.StackSize ?? 1);
        }
        return count;
    }

    private int ResolvePageSize(Inventory inventory)
    {
        int invCapacity = Math.Max(1, inventory?.Capacity ?? 1);
        return Math.Max(1, Math.Min(invCapacity, TerminalPageSize));
    }

    private void BuildPagesBySlotUsage(List<ItemData> sourceItems, Inventory inventory, int preferredPageIndex = 0)
    {
        var clonedSource = CloneItems(sourceItems ?? new List<ItemData>());
        _sessionEntries.Clear();
        _sessionEntries.AddRange(clonedSource);
        RebuildViewIndices();
        MaterializePagesFromViewIndices(inventory, preferredPageIndex);
    }

    private void RebuildViewIndices()
    {
        _viewIndices.Clear();
        string keyword = (SearchKeyword ?? "").Trim();
        bool useKeyword = !string.IsNullOrWhiteSpace(keyword);
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            var entry = _sessionEntries[i];
            if (entry == null) { continue; }
            if (useKeyword && !EntryMatchesKeyword(entry, keyword)) { continue; }
            _viewIndices.Add(i);
        }

        _viewIndices.Sort((leftIndex, rightIndex) =>
            CompareBySortMode(
                leftIndex >= 0 && leftIndex < _sessionEntries.Count ? _sessionEntries[leftIndex] : null,
                rightIndex >= 0 && rightIndex < _sessionEntries.Count ? _sessionEntries[rightIndex] : null,
                (TerminalSortMode)NormalizeSortModeIndex(SortModeIndex),
                SortDescending));
    }

    private void MaterializePagesFromViewIndices(Inventory inventory, int preferredPageIndex)
    {
        _sessionPages.Clear();
        _sessionPageSourceIndices.Clear();
        _sessionCurrentPageIndex = -1;
        _sessionTotalEntryCount = _sessionEntries.Sum(entry => Math.Max(1, entry?.StackSize ?? 1));
        int pageSlotBudget = ResolvePageSize(inventory);
        int pageEntryBudget = Math.Max(1, TerminalPageSize);

        if (_viewIndices.Count == 0)
        {
            _sessionPages.Add(new List<ItemData>());
            _sessionPageSourceIndices.Add(new List<int>());
            _sessionCurrentPageIndex = 0;
            return;
        }

        var currentPage = new List<ItemData>();
        var currentSourceIndices = new List<int>();
        int usedSlots = 0;
        int usedEntries = 0;

        foreach (int sourceIndex in _viewIndices)
        {
            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count) { continue; }
            var entry = _sessionEntries[sourceIndex];
            if (entry == null) { continue; }

            int neededSlots = EstimateSlotUsage(entry, inventory);
            bool wouldOverflowSlots = currentPage.Count > 0 && usedSlots + neededSlots > pageSlotBudget;
            bool wouldOverflowEntries = usedEntries >= pageEntryBudget;
            if (wouldOverflowSlots || wouldOverflowEntries)
            {
                _sessionPages.Add(currentPage);
                _sessionPageSourceIndices.Add(currentSourceIndices);
                currentPage = new List<ItemData>();
                currentSourceIndices = new List<int>();
                usedSlots = 0;
                usedEntries = 0;
            }

            currentPage.Add(entry.Clone());
            currentSourceIndices.Add(sourceIndex);
            usedSlots += neededSlots;
            usedEntries++;

            if (usedSlots >= pageSlotBudget || usedEntries >= pageEntryBudget)
            {
                _sessionPages.Add(currentPage);
                _sessionPageSourceIndices.Add(currentSourceIndices);
                currentPage = new List<ItemData>();
                currentSourceIndices = new List<int>();
                usedSlots = 0;
                usedEntries = 0;
            }
        }

        if (currentPage.Count > 0 || _sessionPages.Count == 0)
        {
            _sessionPages.Add(currentPage);
            _sessionPageSourceIndices.Add(currentSourceIndices);
        }

        if (_sessionPages.Count <= 0)
        {
            _sessionPages.Add(new List<ItemData>());
            _sessionPageSourceIndices.Add(new List<int>());
        }

        if (preferredPageIndex < 0) { preferredPageIndex = 0; }
        if (preferredPageIndex >= _sessionPages.Count) { preferredPageIndex = _sessionPages.Count - 1; }
        _sessionCurrentPageIndex = preferredPageIndex;
    }

    private HashSet<int> GetCurrentPageSourceIndexSet()
    {
        if (_sessionCurrentPageIndex < 0 || _sessionCurrentPageIndex >= _sessionPageSourceIndices.Count)
        {
            return new HashSet<int>();
        }

        return new HashSet<int>(_sessionPageSourceIndices[_sessionCurrentPageIndex].Where(idx => idx >= 0));
    }

    private void ShiftPageSourceIndicesAfterRemoval(int removedSourceIndex)
    {
        if (removedSourceIndex < 0) { return; }
        foreach (var page in _sessionPageSourceIndices)
        {
            if (page == null) { continue; }
            for (int i = 0; i < page.Count; i++)
            {
                int idx = page[i];
                if (idx == removedSourceIndex)
                {
                    // Should not happen because current page is protected, but keep safe.
                    page[i] = -1;
                }
                else if (idx > removedSourceIndex)
                {
                    page[i] = idx - 1;
                }
            }

            page.RemoveAll(idx => idx < 0);
        }
    }

    private void ReplaceSessionEntriesBySourceIndices(List<int> sourceIndices, List<ItemData> replacement)
    {
        var removeSet = new HashSet<int>((sourceIndices ?? new List<int>())
            .Where(idx => idx >= 0 && idx < _sessionEntries.Count));

        var replacementItems = CloneItems(replacement ?? new List<ItemData>());
        if (removeSet.Count == 0)
        {
            _sessionEntries.AddRange(replacementItems);
            return;
        }

        int insertAt = removeSet.Min();
        var rebuilt = new List<ItemData>(_sessionEntries.Count - removeSet.Count + replacementItems.Count);
        bool inserted = false;
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (!inserted && i == insertAt)
            {
                rebuilt.AddRange(CloneItems(replacementItems));
                inserted = true;
            }

            if (removeSet.Contains(i)) { continue; }
            rebuilt.Add(_sessionEntries[i]?.Clone());
        }

        if (!inserted)
        {
            rebuilt.AddRange(CloneItems(replacementItems));
        }

        _sessionEntries.Clear();
        _sessionEntries.AddRange(rebuilt);
    }

    private int EstimateSlotUsage(ItemData entry, Inventory inventory)
    {
        if (entry == null) { return 1; }

        int stackCount = Math.Max(1, entry.StackSize);
        int stackPerSlot = ResolveStackPerSlot(entry, inventory);
        int slots = (int)Math.Ceiling((double)stackCount / Math.Max(1, stackPerSlot));
        return Math.Max(1, slots);
    }

    private int ResolveStackPerSlot(ItemData entry, Inventory inventory)
    {
        if (entry == null || inventory == null) { return 1; }

        var prefab = ItemPrefab.FindByIdentifier(entry.Identifier.ToIdentifier()) as ItemPrefab;
        if (prefab == null) { return 1; }

        int prefabLimit = Math.Max(1, prefab.GetMaxStackSize(inventory));
        int containerLimit = int.MaxValue;

        var container = item.GetComponent<ItemContainer>();
        if (container != null && inventory != null)
        {
            for (int slot = 0; slot < inventory.Capacity; slot++)
            {
                int slotLimit = Math.Max(1, container.GetMaxStackSize(slot));
                containerLimit = Math.Min(containerLimit, slotLimit);
            }
        }

        int resolved = containerLimit == int.MaxValue
            ? prefabLimit
            : Math.Min(prefabLimit, containerLimit);

        resolved = Math.Min(resolved, Inventory.MaxPossibleStackSize);
        return Math.Max(1, resolved);
    }

}


