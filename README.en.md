# Database IO Test (0.1.0)

## Status
- This is a **test build**.
- Core gameplay logic is implemented in **C# (LuaCs)**.
- There is **no active Lua UI** in this version.
- The `Lua/` folder is currently kept only for future work and is effectively deprecated for runtime use.

## What This Mod Adds
- `DatabaseInterface` (handheld) and `DatabaseInterfaceFixed` (wired machine)
  - Ingests items and serializes them into a shared database.
- `DatabaseTerminal` (handheld) and `DatabaseTerminalFixed` (wired machine)
  - Session-based access to stored items.
  - Paging, search, sort, compact actions via XML + C# logic.
- `DatabaseAutoRestocker`
  - Pulls configured items from database and refills linked targets.
- Fabricator integration (`DB Fill` button via override)
  - Pulls recipe materials from database into fabricator input.

## Important Notes
- `filelist.xml` does not load Lua scripts for this mod.
- Current UI behavior is XML CustomInterface + C# components only.
- This is still an evolving data model.

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

## Requirements
- Barotrauma with LuaCs enabled.
- This package enabled in content packages.

