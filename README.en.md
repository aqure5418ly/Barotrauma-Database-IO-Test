# Database IO Test（1.1.5）

## Status
- Core gameplay logic and UI are implemented in **C# (LuaCs)**.
- Terminal interaction is currently **C# UI + XML CustomInterface**.
- Data model and interaction flow are still evolving.

## What This Mod Adds
- `DatabaseStorageAnchor`
  - Persistence anchor for shared database storage.
- `DatabaseInterface` (handheld) and `DatabaseInterfaceFixed` (stationary)
  - Ingest items and serialize them into the shared database.
- `DatabaseTerminal` (handheld), `DatabaseTerminalFixed` (stationary) and `DatabaseCraftTerminal` (fabricator-hybrid)
  - Atomic access to database items (no session lock).
  - Multiple terminals can be opened simultaneously without conflicting.
  - Paging, search, sort and compact actions via C# UI.
- `DatabaseAutoRestocker`
  - Pulls configured items from database and refills linked targets.
- Fabricator integration (`DB Fill` button via override)
  - Pulls recipe materials from database into fabricator input.

## Runtime Requirements
- Install **LuaCsForBarotrauma**.
- Open **LuaCs Settings** (top-right in game) and enable **Enable CSharp Scripting**.
- Enable this mod in your content package list.

## Inspiration And References
- Applied Energistics 2 (AE2): https://appliedenergistics.org/
- Item IO Framework: https://steamcommunity.com/sharedfiles/filedetails/?id=2950383008
- IO Storage: https://steamcommunity.com/sharedfiles/filedetails/?id=3646358075
- UI reference Super Terminal: https://steamcommunity.com/sharedfiles/filedetails/?id=3670545214&searchtext=superterminal

These projects are design/implementation references only. This mod is not affiliated with them.

## Future Plans
1. **Codebase Cleanup:** Refactor and clean up the legacy code.
2. **API Safety:** Expose core mechanisms as safe, external-facing APIs.
3. **Precompiled Release:** Transition to releasing precompiled DLL versions.
4. **Gameplay Expansion:** Design custom missions, events, and automated machines to enrich gameplay content.
5. **DLC Compatibility:** Update and adapt to upcoming game DLCs.

## Save Compatibility Warning
- Future updates may change internal database serialization format.
- Old campaign saves may be affected (missing items, duplicated items, or reset database state are possible in breaking updates).
- **Always backup saves before updating this mod.**

## Recommended Update Procedure
1. Close any active terminal session.
2. End round / save campaign normally.
3. Backup your save files.
4. Update mod files.
5. Re-test with a non-critical save first.
