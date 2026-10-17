# P3-04 Save file: the plan

**Status:** approved by the owner on 2026-10-16 ("Go. Yes to all six recommendations."). Decisions SV1–SV6 are in
[phase0-decisions.md](phase0-decisions.md#phase-3-task-04-save-file-decided-2026-10-16).
**Branch:** `feature/p3-04-save-file`, from origin/main 3f05e1b.
**Task:** row 04 of the [Phase 3 plan](phase2-14-phase3-plan.md#3-tasks).
**Rules implemented:** SH-05 (what's saved), GR-09 (sparse 64 m delta tiles) and CF-06 (plans are saved)
([design rules §10](p3-02-design-rules.md#10-who-implements-what)).

## Scope and files

- **The format, reader, writer and atomic commit:** `Assets/MountainPlanner/Runtime/Persistence/Saves/**`.
- **Tests:** `Assets/MountainPlanner/Tests/Core/Save*Tests.cs` (run in `tools/domain-tests` and Unity), plus the
  fixture `TestData/formats/v1-save/`, added to `FormatFreezeTests`.
- **The game:** `AppFlow.Saves.cs` and `FlowScreens.Saves.cs` (partial files): Save, Ctrl+S, autosave, Continue, the
  Load Area save list. `GameAction.Save` in KeyBindings; the HUD's `bar-save` and `menu-save`; the Settings ›
  Gameplay Autosave row; Manage Areas' Delete counts an area's saves.
- **Mockup first:** a Load Area section of [ui-layout.html](prototypes/ui-layout.html), in its own region.
- **Demo:** `demo.bat` 62.
- **Not mine:** the delta and clearing tile types (task 05, `Runtime/Domain/Edits/**`, SV3); the tool UI (task 06).

## Layout

```
<data>/Saves/<packageId>/<saveId>/
    save.json            the save: format "save", Version 1, read through VersionedJson
    save.json.bak        the previous good save.json (the fallback)
    tiles/<sha256>.tile  one 64 m terrain edit tile (heights and clearing), named by the hash of its bytes
```

- Saves live outside the package, so the read-only demo (`LibraryEntry.Bundled`) saves the same way as a download,
  and an area's Rename (view.json) never touches them.
- **No frozen format changes.** A new `Saves/` folder moves nothing, so `library.json` stays v1; 0.3 §5 lists the
  folder and the new format.

## save.json, version 1

- `Version`, `Name`, `SavedUtc` (passed in, never read from the clock here), `GameVersion`, `Autosave`.
- `PackageId` and `HeightsHash` (the package's height-file hash): a save never opens on other terrain.
- `Design`: the document as plain JSON. The seed and IDs are 16-digit hex (64-bit values survive any JSON reader),
  trails and lifts are in ID order, lengths are integer millimetres; the undo history isn't saved (DD1). Points are
  whole millimetres from the site's centre on the package grid's axes (TE5), never absolute EPSG:6350.
- `Edits`: the tile size (64 m), then each tile's column, row (task 05's `EditTileKey`) and SHA-256.
- A tile file holds one `TerrainEditTile` (TE1), uncompressed, so Unity and .NET write identical bytes: a 16-byte
  header ("MPTL", layout 1, which parts, column, row), then 4,096 whole-mm height changes if it has heights, then 64
  clearing words if it has clearing. Undo isn't saved, so TE2's per-step edit sets don't reach the file.
- The same document, tiles and time always give the same bytes (a golden test).

## Atomic commit

1. Write each new tile to a temporary file and rename it into `tiles/`. Tiles already there are skipped.
2. Write `save.json.tmp` and flush it to disk.
3. `File.Replace` it over `save.json`, keeping `save.json.bak` (atomic on NTFS).
4. Delete tiles that neither `save.json` nor `save.json.bak` refers to.

A crash at any step leaves the old or the new `save.json`, never half of one. A load that finds a broken file or a
tile whose hash is wrong falls back to the backup and says so. The other frozen JSON files move to the same
`File.Replace` write (SV6).

## Versioning

- Frozen as v1 from the first merge (0.3 §5), with a v1 fixture.
- A newer save is refused with the standard message and shown greyed; nothing writes over it.
- The merge waits for task 05's tile types on main (SV4).

## The game

- **Save** (Ctrl+S, the bar's save symbol, the menu's Save game) writes the current save. The first save asks for a
  name, with the area's name filled in. The symbol is white while there are unsaved changes.
- **Autosave** writes the area's one Autosave slot every 10 minutes by default, only when something changed; it never
  overwrites a named save (SV1, SV2).
- **Leaving** (Exit or Quit) with unsaved changes asks "Save before leaving?".
- **Continue** opens the newest save of the area opened last; `recent.json` is unchanged.
- **Load Area:** each area lists its saves (name, when, trail and lift counts, an Autosave tag). Enter opens one, F2
  renames it, Delete removes it after asking; New design opens the area empty.
- **Manage Areas › Delete** says how many saves go with the area, and removes them with it.

## Tests

- **Core:** save then load gives identical document bytes and identical tiles; every crash point leaves the last good
  save; a future version is refused; the backup is used when save.json is broken; a bytes golden; the v1 fixture; the
  Phase 2 v1-library fixture opens unchanged.
- **EditMode:** the Save key binding; the save list's view model.
- **PlayMode:** save, exit to title, Continue, identical document, on a scratch `-data` folder.

## Demo (62)

No drawing tool exists yet, so the F1 developer panel gets **Add sample design** (one trail and one lift by command).
Then save, quit, Continue. A `-crashsave` flag kills the game mid-save, and the next launch opens the last good save.
