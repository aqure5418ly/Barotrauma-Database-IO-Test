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
    public int CountTakeableForAutomation(Func<ItemData, bool> predicate)
    {
        if (!IsServerAuthority || predicate == null || !IsSessionActive())
        {
            return 0;
        }

        var protectedIndices = GetCurrentPageSourceIndexSet();
        int count = 0;
        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (protectedIndices.Contains(i)) { continue; }
            var entry = _sessionEntries[i];
            if (entry == null || !predicate(entry)) { continue; }
            count += Math.Max(1, entry.StackSize);
        }

        return count;
    }

    public bool TryTakeItemsFromNonCurrentPagesForAutomation(
        Func<ItemData, bool> predicate,
        int amount,
        DatabaseStore.TakePolicy policy,
        out List<ItemData> taken)
    {
        taken = new List<ItemData>();
        if (!IsServerAuthority || predicate == null || amount <= 0 || !IsSessionActive())
        {
            return false;
        }

        if (CountTakeableForAutomation(predicate) < amount)
        {
            return false;
        }

        int remaining = amount;
        while (remaining > 0)
        {
            if (!TryFindAutomationCandidate(predicate, policy, out int sourceIndex))
            {
                break;
            }

            if (sourceIndex < 0 || sourceIndex >= _sessionEntries.Count)
            {
                break;
            }

            var entry = _sessionEntries[sourceIndex];
            if (entry == null)
            {
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
                continue;
            }

            if (entry.ContainedItems != null && entry.ContainedItems.Count > 0)
            {
                taken.Add(entry.Clone());
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
                remaining--;
                continue;
            }

            int stackSize = Math.Max(1, entry.StackSize);
            if (stackSize <= remaining)
            {
                taken.Add(entry.Clone());
                remaining -= stackSize;
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
                continue;
            }

            var part = ExtractStackPart(entry, remaining);
            if (part != null)
            {
                taken.Add(part);
            }
            remaining = 0;

            if (entry.StackSize <= 0)
            {
                _sessionEntries.RemoveAt(sourceIndex);
                ShiftPageSourceIndicesAfterRemoval(sourceIndex);
            }
        }

        if (remaining > 0)
        {
            return false;
        }

        RebuildViewIndices();
        _sessionAutomationConsumedCount += CountFlatItems(taken);
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        return true;
    }

    private bool TryFindAutomationCandidate(
        Func<ItemData, bool> predicate,
        DatabaseStore.TakePolicy policy,
        out int sourceIndex)
    {
        sourceIndex = -1;
        if (predicate == null) { return false; }
        var protectedIndices = GetCurrentPageSourceIndexSet();

        if (policy == DatabaseStore.TakePolicy.Fifo)
        {
            for (int i = 0; i < _sessionEntries.Count; i++)
            {
                if (protectedIndices.Contains(i)) { continue; }
                var entry = _sessionEntries[i];
                if (entry == null || !predicate(entry)) { continue; }
                sourceIndex = i;
                return true;
            }

            return false;
        }

        float bestCondition = 0f;
        int bestQuality = 0;
        const float eps = 0.0001f;

        for (int i = 0; i < _sessionEntries.Count; i++)
        {
            if (protectedIndices.Contains(i)) { continue; }
            var entry = _sessionEntries[i];
            if (entry == null || !predicate(entry)) { continue; }

            if (sourceIndex < 0)
            {
                sourceIndex = i;
                bestCondition = entry.Condition;
                bestQuality = entry.Quality;
                continue;
            }

            bool replace;
            if (policy == DatabaseStore.TakePolicy.HighestConditionFirst)
            {
                replace = entry.Condition > bestCondition + eps ||
                          (Math.Abs(entry.Condition - bestCondition) <= eps && entry.Quality > bestQuality);
            }
            else
            {
                replace = entry.Condition < bestCondition - eps ||
                          (Math.Abs(entry.Condition - bestCondition) <= eps && entry.Quality < bestQuality);
            }

            if (replace)
            {
                sourceIndex = i;
                bestCondition = entry.Condition;
                bestQuality = entry.Quality;
            }
        }

        return sourceIndex >= 0;
    }

    private void OpenSessionInternal(Character character, bool forceTakeover = false)
    {
        if (UseInPlaceSession && !SessionVariant)
        {
            OpenSessionInPlace(character, forceTakeover);
            return;
        }

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            if (!TryHandleLockedOpen(character, forceTakeover))
            {
                return;
            }

            if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
            {
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                    $"{Constants.LogPrefix} Taking over '{_resolvedDatabaseId}'... retry in a moment.",
                    Microsoft.Xna.Framework.Color.Orange);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return;
            }
        }

        ClearTakeoverPrompt();
        var allData = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);

        bool started = SpawnReplacement(
            OpenSessionIdentifier,
            character,
            null,
            (spawned, actor) =>
            {
                var terminal = spawned.GetComponent<DatabaseTerminalComponent>();
                if (terminal != null)
                {
                    terminal.ResolveDatabaseId(_resolvedDatabaseId);
                    terminal.DatabaseVersion = DatabaseVersion;
                    terminal.SerializedDatabase = SerializedDatabase;
                    terminal._sessionOwner = character;
                    terminal._sessionOpenedAt = Timing.TotalTime;
                    terminal._sessionAutomationConsumedCount = 0;
                    terminal.InitializePages(allData, actor);
                    terminal._nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
                    terminal._nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
                    terminal.UpdateSummaryFromStore();
                    terminal.UpdateDescriptionLocal();
                    terminal.TrySyncSummary(force: true);
                    terminal.RefreshLuaB1BridgeState(force: true);
                }

                DatabaseStore.TransferTerminalLock(_resolvedDatabaseId, item.ID, spawned.ID);
            },
            () =>
            {
                DatabaseStore.AppendItems(_resolvedDatabaseId, allData);
                DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Failed to open session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
            });

        if (!started)
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, allData);
            DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        }
    }

    private void OpenSessionInPlace(Character character, bool forceTakeover = false)
    {
        if (_inPlaceSessionActive) { return; }

        FlushIdleInventoryItems();

        if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
        {
            if (!TryHandleLockedOpen(character, forceTakeover))
            {
                return;
            }

            if (!DatabaseStore.TryAcquireTerminal(_resolvedDatabaseId, item.ID))
            {
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                    $"{Constants.LogPrefix} Taking over '{_resolvedDatabaseId}'... retry in a moment.",
                    Microsoft.Xna.Framework.Color.Orange);
                UpdateSummaryFromStore();
                UpdateDescriptionLocal();
                TrySyncSummary(force: true);
                return;
            }
        }

        ClearTakeoverPrompt();
        var allData = DatabaseStore.TakeAllForTerminalSession(_resolvedDatabaseId, item.ID);
        _sessionOwner = character;
        _sessionOpenedAt = Timing.TotalTime;
        _inPlaceSessionActive = true;
        _sessionAutomationConsumedCount = 0;
        InitializePages(allData, character);
        _nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
        _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        ModFileLog.Write(
            "FixedTerminal",
            $"{Constants.LogPrefix} in-place open committed id={item?.ID} db='{_resolvedDatabaseId}' " +
            $"actor='{character?.Name ?? "none"}' force={forceTakeover} items={allData.Count} pageSize={TerminalPageSize}");
        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} In-place terminal session opened for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private void FlushIdleInventoryItems()
    {
        if (!UseInPlaceSession) { return; }
        if (IsSessionActive()) { return; }

        var inventory = GetTerminalInventory();
        if (inventory == null) { return; }

        var leakedItems = inventory.AllItems
            .Where(it => it != null && !it.Removed)
            .Distinct()
            .ToList();
        if (leakedItems.Count == 0) { return; }

        Character carrier = item.ParentInventory?.Owner as Character;
        int returnedCount = 0;
        int droppedCount = 0;

        foreach (Item leaked in leakedItems)
        {
            inventory.RemoveItem(leaked);

            bool returned = false;
            if (item.ParentInventory != null)
            {
                returned = item.ParentInventory.TryPutItem(leaked, carrier, CharacterInventory.AnySlot);
            }

            if (returned)
            {
                returnedCount++;
            }
            else
            {
                leaked.Drop(carrier);
                droppedCount++;
            }
        }

        if (returnedCount > 0 || droppedCount > 0)
        {
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                $"{Constants.LogPrefix} Cleared {leakedItems.Count} idle terminal item(s) for '{_resolvedDatabaseId}' (returned={returnedCount}, dropped={droppedCount}).",
                Microsoft.Xna.Framework.Color.Orange);
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} FlushIdleInventory db='{_resolvedDatabaseId}' itemId={item?.ID} total={leakedItems.Count} returned={returnedCount} dropped={droppedCount}");
        }
    }

    private void CloseSessionInternal(string reason, bool convertToClosedItem, Character requester)
    {
        bool committed = false;
        Action commitSession = () =>
        {
            if (committed) { return; }
            committed = true;
            CommitSessionInventoryToStore();
        };

        if (convertToClosedItem)
        {
            Character owner = requester ?? _sessionOwner;
            bool converted = SpawnReplacement(
                ClosedTerminalIdentifier,
                owner,
                commitSession,
                (spawned, actor) =>
                {
                    var terminal = spawned.GetComponent<DatabaseTerminalComponent>();
                    if (terminal != null)
                    {
                        terminal.ResolveDatabaseId(_resolvedDatabaseId);
                        terminal.DatabaseVersion = DatabaseVersion;
                        terminal.SerializedDatabase = SerializedDatabase;
                        terminal._nextToggleAllowedTime = Timing.TotalTime + ToggleCooldownSeconds;
                        terminal.UpdateSummaryFromStore();
                        terminal.UpdateDescriptionLocal();
                        terminal.TrySyncSummary(force: true);
                        terminal.RefreshLuaB1BridgeState(force: true);
                    }
                },
                () =>
                {
                    commitSession();
                    DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Failed to close session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
                });

            if (!converted)
            {
                commitSession();
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Failed to close session terminal for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.OrangeRed);
            }
        }
        else
        {
            commitSession();
        }

        DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} Terminal session closed ({reason}) for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private bool HandlePanelActionServer(TerminalPanelAction action, Character actor, string source = "unknown")
    {
        if (ModFileLog.IsDebugEnabled)
        {
            ModFileLog.Write(
                "Panel",
                $"{Constants.LogPrefix} action dispatch source={source} action={action} id={item?.ID} db='{_resolvedDatabaseId}' " +
                $"actor='{actor?.Name ?? "none"}' owner='{_sessionOwner?.Name ?? "none"}' " +
                $"sessionActive={IsSessionActive()} cachedOpen={_cachedSessionOpen} inPlace={UseInPlaceSession} sessionVariant={SessionVariant}");
        }

        if (action == TerminalPanelAction.OpenSession)
        {
            if (SessionVariant || _inPlaceSessionActive)
            {
                LogFixedTerminal($"open rejected: already session id={item?.ID}");
                return false;
            }
            if (!HasRequiredPower())
            {
                LogFixedTerminal($"open rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
                return false;
            }
            if (Timing.TotalTime < _nextPanelActionAllowedTime)
            {
                LogFixedTerminal($"open rejected: cooldown id={item?.ID}");
                return false;
            }

            LogFixedTerminal($"open request id={item?.ID} actor={actor?.Name ?? "none"} db='{_resolvedDatabaseId}'");
            OpenSessionInternal(actor);
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            return true;
        }
        if (action == TerminalPanelAction.ForceOpenSession)
        {
            if (SessionVariant || _inPlaceSessionActive)
            {
                LogFixedTerminal($"force rejected: already session id={item?.ID}");
                return false;
            }
            if (!HasRequiredPower())
            {
                LogFixedTerminal($"force rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
                return false;
            }
            if (Timing.TotalTime < _nextPanelActionAllowedTime)
            {
                LogFixedTerminal($"force rejected: cooldown id={item?.ID}");
                return false;
            }

            LogFixedTerminal($"force request id={item?.ID} actor={actor?.Name ?? "none"} db='{_resolvedDatabaseId}'");
            OpenSessionInternal(actor, forceTakeover: true);
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            return true;
        }

        if (action != TerminalPanelAction.CloseSession && !HasRequiredPower())
        {
            LogFixedTerminal($"{action} rejected: no power id={item?.ID} voltage={GetCurrentVoltage():0.##}");
            return false;
        }
        if (Timing.TotalTime < _nextPanelActionAllowedTime)
        {
            LogFixedTerminal($"{action} rejected: cooldown id={item?.ID}");
            return false;
        }

        if (!IsSessionActive())
        {
            bool closedApplied = action switch
            {
                TerminalPanelAction.CycleSortMode => TryCycleSortModeWhenClosed(),
                TerminalPanelAction.ToggleSortOrder => TryToggleSortOrderWhenClosed(),
                TerminalPanelAction.CompactItems => TryCompactStoreWhenClosed(),
                _ => false
            };

            if (closedApplied)
            {
                _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
            }
            else if (action == TerminalPanelAction.CloseSession)
            {
                LogFixedTerminal($"close rejected: no active session id={item?.ID}");
            }
            return closedApplied;
        }

        if (!CanCharacterControlSession(actor))
        {
            LogFixedTerminal(
                $"{action} rejected: owner mismatch id={item?.ID} owner={_sessionOwner?.Name ?? "none"} actor={actor?.Name ?? "none"}");
            return false;
        }

        bool applied;
        switch (action)
        {
            case TerminalPanelAction.PrevPage:
                applied = TryChangePage(-1, actor);
                break;
            case TerminalPanelAction.NextPage:
                applied = TryChangePage(1, actor);
                break;
            case TerminalPanelAction.PrevMatch:
                applied = TryJumpToMatch(-1, actor);
                break;
            case TerminalPanelAction.NextMatch:
                applied = TryJumpToMatch(1, actor);
                break;
            case TerminalPanelAction.CycleSortMode:
                applied = TryCycleSortMode(actor);
                break;
            case TerminalPanelAction.ToggleSortOrder:
                applied = TryToggleSortOrder(actor);
                break;
            case TerminalPanelAction.CompactItems:
                applied = TryCompactSessionItems(actor);
                break;
            case TerminalPanelAction.CloseSession:
                if (!UseInPlaceSession && Timing.TotalTime - _sessionOpenedAt < MinSessionDurationBeforeClose)
                {
                    LogFixedTerminal(
                        $"close rejected: min duration id={item?.ID} openFor={(Timing.TotalTime - _sessionOpenedAt):0.###}");
                    applied = false;
                    break;
                }
                if (SessionVariant)
                {
                    CloseSessionInternal("panel close", true, actor);
                }
                else
                {
                    CloseSessionInPlace("panel close");
                }
                applied = true;
                break;
            default:
                applied = false;
                break;
        }

        if (applied)
        {
            _nextPanelActionAllowedTime = Timing.TotalTime + PanelActionCooldownSeconds;
        }
        else if (action == TerminalPanelAction.CloseSession)
        {
            LogFixedTerminal($"close rejected: generic id={item?.ID} session={IsSessionActive()} owner={_sessionOwner?.Name ?? "none"}");
        }

        return applied;
    }

    private bool TryHandleLockedOpen(Character actor, bool forceTakeover)
    {
        if (forceTakeover)
        {
            bool forced = DatabaseStore.TryForceCloseActiveSession(_resolvedDatabaseId, item.ID, actor);
            if (forced)
            {
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                    $"{Constants.LogPrefix} Force takeover requested for '{_resolvedDatabaseId}'.",
                    Microsoft.Xna.Framework.Color.Orange);
                ModFileLog.Write(
                    "Terminal",
                    $"{Constants.LogPrefix} force takeover requested db='{_resolvedDatabaseId}' by terminal={item?.ID} actor={actor?.Name ?? "none"}");
            }
            else
            {
                LogFixedTerminal($"force takeover failed db='{_resolvedDatabaseId}' terminal={item?.ID}");
            }
            return forced;
        }

        if (actor == null)
        {
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} {_resolvedDatabaseId} is already locked by another terminal.", Microsoft.Xna.Framework.Color.OrangeRed);
            return false;
        }

        if (_pendingTakeoverRequesterId == actor.ID && Timing.TotalTime <= _pendingTakeoverUntil)
        {
            ClearTakeoverPrompt();
            bool forced = DatabaseStore.TryForceCloseActiveSession(_resolvedDatabaseId, item.ID, actor);
            if (forced)
            {
                DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
                    $"{Constants.LogPrefix} Force takeover requested for '{_resolvedDatabaseId}'.",
                    Microsoft.Xna.Framework.Color.Orange);
                ModFileLog.Write(
                    "Terminal",
                    $"{Constants.LogPrefix} force takeover requested db='{_resolvedDatabaseId}' by terminal={item?.ID} actor={actor?.Name ?? "none"}");
                return true;
            }
        }

        _pendingTakeoverRequesterId = actor.ID;
        _pendingTakeoverUntil = Timing.TotalTime + TakeoverConfirmWindowSeconds;
        DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(
            $"{Constants.LogPrefix} {_resolvedDatabaseId} is locked. Use again within {TakeoverConfirmWindowSeconds:0.#}s to force takeover.",
            Microsoft.Xna.Framework.Color.Orange);
        return false;
    }

    private void LogFixedTerminal(string message)
    {
        if (!UseInPlaceSession) { return; }
        ModFileLog.Write("FixedTerminal", $"{Constants.LogPrefix} {message}");
    }

    private void ClearTakeoverPrompt()
    {
        _pendingTakeoverRequesterId = -1;
        _pendingTakeoverUntil = 0;
    }

    private bool CanCharacterControlSession(Character actor)
    {
        if (_sessionOwner == null) { return true; }
        if (actor == null || actor.Removed || actor.IsDead) { return false; }
        return _sessionOwner == actor;
    }

    private bool ShouldAutoClose()
    {
        if (!IsSessionActive()) { return false; }
        if (item.Removed) { return true; }
        if (!HasRequiredPower()) { return true; }
        if (_sessionOwner != null && (_sessionOwner.Removed || _sessionOwner.IsDead)) { return true; }
        if (_sessionOpenedAt > 0 && Timing.TotalTime - _sessionOpenedAt > Math.Max(5f, TerminalSessionTimeout)) { return true; }
        return false;
    }
    private void CloseSessionInPlace(string reason)
    {
        if (!_inPlaceSessionActive) { return; }

        string ownerName = _sessionOwner?.Name ?? "none";
        int sessionEntryCount = _sessionEntries.Count;
        int pendingPageItemCount = CountPendingPageItems();
        CommitSessionInventoryToStore();
        _inPlaceSessionActive = false;
        _sessionOwner = null;
        _sessionOpenedAt = 0;
        ModFileLog.Write(
            "FixedTerminal",
            $"{Constants.LogPrefix} in-place close committed id={item?.ID} db='{_resolvedDatabaseId}' reason='{reason}' " +
            $"owner='{ownerName}' sessionEntries={sessionEntryCount} pendingItems={pendingPageItemCount}");

        UpdateSummaryFromStore();
        UpdateDescriptionLocal();
        TrySyncSummary(force: true);
        RefreshLuaB1BridgeState(force: true);
        DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} In-place terminal session closed ({reason}) for '{_resolvedDatabaseId}'.", Microsoft.Xna.Framework.Color.LightGray);
    }

    private bool SpawnReplacement(string targetIdentifier, Character actor, Action beforeSwap, Action<Item, Character> onSpawned, Action onSpawnFailed)
    {
        if (string.IsNullOrWhiteSpace(targetIdentifier))
        {
            return false;
        }
        if (Entity.Spawner == null)
        {
            return false;
        }

        var prefab = ItemPrefab.FindByIdentifier(targetIdentifier.ToIdentifier()) as ItemPrefab;
        if (prefab == null)
        {
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage($"{Constants.LogPrefix} ItemPrefab not found: {targetIdentifier}", Microsoft.Xna.Framework.Color.OrangeRed);
            return false;
        }

        var parentInventory = item.ParentInventory;
        int slotIndex = parentInventory != null ? parentInventory.FindIndex(item) : -1;

        bool wasSelected = false;
        if (actor != null)
        {
            wasSelected = actor.SelectedItem == item || actor.SelectedSecondaryItem == item;
        }

        var position = item.WorldPosition;
        var submarine = item.Submarine;

        Entity.Spawner.AddItemToSpawnQueue(prefab, position, submarine, onSpawned: spawned =>
        {
            if (spawned == null)
            {
                onSpawnFailed?.Invoke();
                return;
            }

            bool placed = false;
            if (parentInventory != null)
            {
                beforeSwap?.Invoke();
                parentInventory.RemoveItem(item);

                if (slotIndex >= 0)
                {
                    placed = parentInventory.TryPutItem(spawned, slotIndex, false, false, actor, true, true);
                }

                if (!placed)
                {
                    placed = parentInventory.TryPutItem(spawned, actor, CharacterInventory.AnySlot);
                }
            }

            if (!placed)
            {
                spawned.Drop(actor);
            }

            if (wasSelected && actor != null)
            {
                actor.SelectedItem = spawned;
            }

            if (parentInventory == null)
            {
                beforeSwap?.Invoke();
            }

            onSpawned?.Invoke(spawned, actor);
            SpawnService.RemoveItem(item);
        });

        return true;
    }
    private void CommitSessionInventoryToStore()
    {
        if (_sessionWritebackCommitted)
        {
            ModFileLog.Write(
                "Terminal",
                $"{Constants.LogPrefix} Session writeback skipped db='{_resolvedDatabaseId}' terminal={item?.ID} reason='already committed'.");
            return;
        }

        var inventory = GetTerminalInventory();
        var bufferData = ItemSerializer.SerializeInventory(_sessionOwner, inventory);
        var remainingData = CloneItems(_sessionEntries);
        remainingData.AddRange(CloneItems(bufferData));

        int capturedTopLevelEntries = remainingData.Count;
        int capturedFlatItems = CountFlatItems(remainingData);

        var compactedData = DatabaseStore.CompactSnapshot(remainingData);
        int compactedTopLevelEntries = compactedData.Count;
        int compactedFlatItems = CountFlatItems(compactedData);

        bool wroteBackToLockedStore = DatabaseStore.WriteBackFromTerminalContainer(_resolvedDatabaseId, compactedData, item.ID);
        if (!wroteBackToLockedStore)
        {
            DatabaseStore.AppendItems(_resolvedDatabaseId, compactedData);
        }

        ModFileLog.Write(
            "Terminal",
            $"{Constants.LogPrefix} Session writeback db='{_resolvedDatabaseId}' terminal={item?.ID} " +
            $"capturedEntries={capturedTopLevelEntries} capturedItems={capturedFlatItems} " +
            $"bufferEntries={bufferData.Count} bufferItems={CountFlatItems(bufferData)} " +
            $"compactedEntries={compactedTopLevelEntries} compactedItems={compactedFlatItems} " +
            $"writtenBackItems={compactedFlatItems} lockedWrite={wroteBackToLockedStore}");

        if (inventory != null)
        {
            SpawnService.ClearInventory(inventory);
        }

        DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);

        if (_sessionAutomationConsumedCount > 0)
        {
            string line = $"{Constants.LogPrefix} Session '{_resolvedDatabaseId}' consumed {_sessionAutomationConsumedCount} item(s) by automation.";
            DatabaseIOTest.Services.ModFileLog.TryConsoleMessage(line, Microsoft.Xna.Framework.Color.LightGray);
            ModFileLog.Write("Terminal", line);
        }

        _sessionEntries.Clear();
        _viewIndices.Clear();
        _sessionPages.Clear();
        _sessionPageSourceIndices.Clear();
        _sessionCurrentPageIndex = -1;
        _sessionTotalEntryCount = 0;
        _sessionAutomationConsumedCount = 0;
        _pendingPageFillCheckGeneration = -1;
        _sessionWritebackCommitted = true;
    }
    public bool RequestForceCloseForTakeover(string reason, Character requester, bool convertToClosedItem = true)
    {
        if (!IsServerAuthority) { return false; }

        if (SessionVariant)
        {
            CloseSessionInternal(reason, convertToClosedItem, requester ?? _sessionOwner);
            return true;
        }

        if (_inPlaceSessionActive)
        {
            CloseSessionInPlace(reason);
            return true;
        }

        DatabaseStore.ReleaseTerminal(_resolvedDatabaseId, item.ID);
        return true;
    }
}

