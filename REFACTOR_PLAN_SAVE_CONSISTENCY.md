# Database IO Test Refactor Plan (Save/No-Save Consistency)

## 1. Problem Statement

Current behavior can allow item duplication across campaign rounds:

- Barotrauma supports both:
  - `Save & Quit`
  - `Quit without saving`
- Our database may still appear to keep new items after a `Quit without saving` flow.
- Root cause is likely runtime in-memory state (`DatabaseStore`) surviving round flow in the same process, then being re-synced to terminal save fields.

## 2. Goals

- If player chooses `Quit without saving`, database must roll back to last committed state.
- If player chooses `Save & Quit`, database must commit current state.
- Keep server-authoritative behavior in multiplayer.
- Avoid introducing item loss/duplication during terminal session + automation coexistence.

## 3. Hook/Patch Reality Check

From LuaCs docs and runtime API capabilities:

- Available round hooks: `roundStart`, `roundEnd`.
- No dedicated built-in hook that explicitly distinguishes:
  - save-and-end
  - end-without-save
- Therefore, to distinguish reliably, we need method patching (`Hook.Patch` in LuaCs or Harmony/C# patch path).

## 4. Proposed Refactor (Phased)

### Phase A - Immediate Safety Hotfix

- On `roundStart`: clear volatile runtime cache (`DatabaseStore.Clear`) and rebuild from persisted terminal fields.
- Add stronger guard around early network summary sync (already partly done) to avoid startup races.
- Add explicit logs for:
  - round start cache clear
  - first terminal registration source (persisted vs runtime)

Result:
- Prevents most carry-over artifacts between rounds in same process.

### Phase B - Proper Commit/Rollback Model (Core Refactor)

Introduce two layers in `DatabaseStore`:

- `Committed` store:
  - Last save-confirmed state.
  - Source of truth for persistence.
- `Working` store:
  - Live round runtime state (interface ingest, terminal writes, automation consume).

New flow:

1. Round starts:
   - `Working <- deep clone(Committed)`.
2. During round:
   - all operations act on `Working`.
3. Save confirmed:
   - `Committed <- deep clone(Working)`.
   - sync `SerializedDatabase` to terminals.
4. Quit without save / rollback path:
   - `Working <- deep clone(Committed)`.
   - release sessions and clear stale locks.

### Phase C - Save/No-Save Signal Binding

Patch server-side method where save decision is explicit (recommended target: `GameServer.EndGame(..., wasSaved, ...)` decision chain).

- If `wasSaved == true`: call `CommitRound()`.
- If `wasSaved == false`: call `RollbackRound()`.
- Keep fallback protection with `roundStart` clear/rebuild.

## 5. API Changes (Planned)

Add to `DatabaseStore`:

- `BeginRound()`
- `CommitRound()`
- `RollbackRound(reason)`
- `ClearVolatile()`
- `RebuildFromPersistedTerminals()`

Update terminal/store sync behavior:

- Sync summaries from `Working`.
- Persist write (`SerializedDatabase`) only from `Committed` on commit point.

## 6. Multiplayer/Session Rules

- Keep single active terminal session lock per `databaseId`.
- On rollback:
  - force close all active sessions
  - serialize container leftovers back to `Working` first
  - then rollback to `Committed` (discarding unsaved round progress by design)
- Automation reads/writes must only target `Working`.

## 7. Test Matrix (Must Pass)

1. Campaign `Save & Quit`: database changes persist.
2. Campaign `Quit without saving`: database changes do not persist.
3. Dedicated server:
   - multiple players
   - one player terminal session open, another automation running
4. Crash-like path:
   - terminate round without save confirmation
   - ensure restart uses committed-only state.
5. Regression:
   - compact/sort/search/page switch does not duplicate or lose items.

## 8. Compatibility Notes

- This is a storage-layer refactor and may affect old in-progress test saves.
- Keep backup recommendation in release notes.
- Bump `modversion` on release after this refactor lands.

## 9. Discussion Items Before Implementation

- Confirm patch location:
  - strict `GameServer.EndGame(... wasSaved ...)` branch binding
  - plus `roundStart` fallback clear.
- Decide whether to include migration step for currently dirty runtime states.
- Decide logging verbosity in production vs debug.

## 10. AE2-Inspired Target Architecture (Data / Sync / View)

### 10.1 Data Layer (Authoritative Core)

- Replace single in-memory list with dual state:
  - `CommittedState`: last confirmed saved state.
  - `WorkingState`: live runtime state for ingest/terminal/automation.
- Split stored entries by behavior:
  - `AggregatedEntries`: stack-like logical counts for simple items.
  - `ExactEntries`: non-stackable / special condition / nested container entries.
- All mutating operations (insert/extract/compact/search source) must run on `WorkingState` only.
- `CommittedState` is write-protected except at `CommitRound`.

### 10.2 Sync Layer (Watcher + Delta)

- Introduce AE2-like publish/subscribe:
  - `WatchAll`: terminal UI sessions.
  - `WatchByIdentifier`: restocker/fabricator watchers.
- On mutation, emit delta event:
  - `key`, `oldAmount`, `newAmount`, `version`, `source`.
- Server sends only changed keys + summary counters.
- Client keeps passive cache and only redraws affected elements.

### 10.3 View Layer (Container Adapter, No Slot Prediction)

- Remove pre-estimation based page fill (`EstimateSlotUsage` path).
- New algorithm: generate items one by one, stop when `TryPutItem` fails.
- Current page items are `leased` from `WorkingState`:
  - taken by player = consumed from lease.
  - left in terminal at page switch/close = returned to `WorkingState`.
- Sorting/searching operates on stable view index list, never directly on raw list while page is active.

## 11. Task Plan (Ordered Backlog)

### Milestone M0 - Baseline & Safety Net

1. `T0.1` Create feature branch and freeze current behavior snapshot logs.
2. `T0.2` Add refactor flags (`EnableDeltaSync`, `EnableLeasePaging`, `EnableCommittedWorkingStore`) default off.
3. `T0.3` Add rollback-safe debug logs for round start/end and terminal register path.

Exit criteria:
- Can toggle new behavior off and reproduce old behavior for comparison.

### Milestone M1 - Save/No-Save Correctness (Highest Priority)

1. `T1.1` Add lifecycle bridge methods in `DatabaseStore`:
   - `BeginRound()`, `CommitRound()`, `RollbackRound(reason)`.
2. `T1.2` Patch save decision path (`GameServer.EndGame(... wasSaved ...)`) and bind:
   - `wasSaved=true -> CommitRound()`
   - `wasSaved=false -> RollbackRound()`
3. `T1.3` Keep `roundStart` fallback:
   - clear volatile runtime
   - rebuild from committed persisted data.
4. `T1.4` Force-close active sessions before rollback and release all locks.

Exit criteria:
- `Quit without saving` never keeps newly ingested items.
- `Save & Quit` persists changes deterministically.

### Milestone M2 - Data Core Split (Committed/Working)

1. `T2.1` Introduce internal models:
   - `DatabaseState`, `StateEntry`, `StateMutation`.
2. `T2.2` Refactor existing APIs to operate on `WorkingState`:
   - ingest, take, writeback, compact, automation consumes.
3. `T2.3` Add commit/rollback clone pipeline with version bump rules.
4. `T2.4` Persist only from `CommittedState` to terminal serialized field.

Exit criteria:
- No direct writes to committed data during normal gameplay.

### Milestone M3 - Delta Sync (Watcher/Interest)

1. `T3.1` Add `WatchRegistry` (watchAll + watchByKey).
2. `T3.2` Add mutation event bus:
   - collect touched keys during each operation.
3. `T3.3` Push delta packets to interested watchers only.
4. `T3.4` Add client fallback to full snapshot when version gap detected.

Exit criteria:
- Terminal no longer relies on full refresh for every change.
- Automation subscriptions receive only relevant key updates.

### Milestone M4 - Terminal View Rewrite (Lease Paging)

Default track for now: `Track-A` (container lease paging).  
Alternative `Track-B` (hybrid virtual list + real I/O buffer) is documented below and remains pending until final decision.

1. `T4.1` Remove slot estimation page materialization.
2. `T4.2` Add lease page lifecycle:
   - open page -> borrow entries
   - turn page/close -> capture and return leftovers
3. `T4.3` Change page fill to real insertion loop:
   - stop at first insertion failure
   - never spawn-drop due to overfill.
4. `T4.4` Rework sort/search pipeline:
   - source list immutable during active page render
   - recalc view after capture step only.

Exit criteria:
- No “page not full because wrong estimate” issue.
- No split/overflow caused by stack prediction mismatch.

### Milestone M5 - Automation Alignment

1. `T5.1` Restocker consume path uses `WorkingState` API only.
2. `T5.2` Fabricator material pull uses same consume API + optional condition policy.
3. `T5.3` During active terminal lease:
   - consume only from non-leased pool unless explicitly allowed.

Exit criteria:
- No duplication between terminal session and automation.

### Milestone M6 - Migration, Test, Release

1. `T6.1` Migration policy:
   - old serialized data -> initialize both committed and working at first load.
2. `T6.2` Run full matrix (singleplayer + dedicated server).
3. `T6.3` Update docs/changelog and known limitations.
4. `T6.4` Bump `modversion` in `filelist.xml`.

Exit criteria:
- Release candidate passes all save/no-save and pagination regressions.

## 12. Dependency Graph (Execution Order)

1. `M0` must finish before all.
2. `M1` must finish before `M2`/`M4`.
3. `M2` must finish before `M3` and `M5`.
4. `M4` depends on `M2` and partially on `M3`.
5. `M6` only starts after `M1`~`M5` all green.

## 13. Concrete File Touch Plan

- `CSharp/Shared/Services/DatabaseStore.cs`
  - lifecycle state split + commit/rollback + watcher integration.
- `CSharp/Shared/Components/DatabaseTerminalComponent.cs`
  - lease paging + capture/apply flow + summary sync adaptation.
- `CSharp/Shared/Components/DatabaseAutoRestockerComponent.cs`
  - watcher-based identifier updates + unified consume API.
- `CSharp/Shared/Components/DatabaseFabricatorOrderComponent.cs`
  - consume path migration and delta-awareness.
- `CSharp/Shared/Services/ItemSerializer.cs`
  - serialization invariants for state entry conversion.
- `CSharp/Shared/DatabaseIOMod.cs`
  - round lifecycle hook registration and teardown.
- `filelist.xml`
  - version bump at release milestone.

## 14. Risk Register + Mitigation

1. Risk: Hook patch misses a save path on dedicated server.
   - Mitigation: keep `roundStart` rebuild fallback and log both commit/rollback triggers.
2. Risk: Lease page return logic loses items.
   - Mitigation: always `CaptureCurrentPageFromInventory` before any page/sort/search transition.
3. Risk: Delta sync version drift.
   - Mitigation: version mismatch forces full snapshot refresh.
4. Risk: old saves with dirty runtime expectations.
   - Mitigation: one-time migration + explicit release note warning.

## 15. Definition of Done (Refactor Complete)

- Save behavior:
  - `Save & Quit` persists.
  - `Quit without saving` always rolls back.
- Terminal:
  - no overfill drops
  - no false empty slots caused by prediction
  - sort/search/page switch does not lose or duplicate items.
- Automation:
  - no cross-path duplication with active terminal sessions.
- Network:
  - delta updates stable in multiplayer (no desync spam).

## 16. Terminal UI Final Design Options (Pending)

Status: `Pending` (record options now, finalize after further discussion and tests).

### Option A - Real Container + Lease Paging (Current Default)

- Keep terminal based on real `ItemContainer`.
- Use lease-based page load/return to avoid slot prediction errors.
- Pros:
  - reuses vanilla drag/drop behavior.
  - lower implementation risk.
- Cons:
  - still constrained by real slot and stack behavior.
  - UX ceiling is lower than AE2-style terminal.

### Option B - Hybrid Virtual List + Real I/O Buffer (New Candidate)

- Main terminal list is virtual (custom rendered rows, not real item-per-slot mapping).
- Keep a small real container as input/output buffer only.
- Multiplayer sync strategy:
  - session open: send one `Snapshot`.
  - runtime changes: send in-memory `Delta` batches only.
  - version mismatch/reconnect/filter reset: force `Snapshot` resync.
- Pros:
  - avoids hard stack-cap mapping issues in main view.
  - keeps compatibility with real-item transfer through buffer.
  - balances UX and engineering risk.
- Cons:
  - requires custom list render and click handling.
  - needs robust request/ack protocol for extract/deposit actions.

### Option C - Full Custom C# UI (No Container Dependency)

- Fully custom grid/list and interactions, no container mapping.
- Pros:
  - maximum freedom (search/sort/filter/amount input/virtual stack display).
- Cons:
  - highest implementation and maintenance cost.
  - largest multiplayer sync surface.

### Option D - Proxy Dummy Items

- Show dummy display items in container and intercept put/take to map to database state.
- Pros:
  - can present large logical counts in limited slots.
- Cons:
  - drag/drop interception is complex.
  - higher risk of edge-case desync/ghost items.

### Option E - Runtime Prefab Stack Hack

- Force-edit prefab max stack at runtime.
- Decision: `Rejected` as final architecture.
- Reason:
  - global side effects and balance breakage.
  - high multiplayer desync risk.

## 17. UI Decision Gate

Final option selection criteria:

1. Dedicated server test with 2+ clients and unstable latency.
2. No duplicate/lost item under rapid page switch + automation consume.
3. Search/sort/update latency acceptable with large logical datasets.
4. Long-term code maintainability acceptable.

If Option A fails criteria, promote Option B as final target.
