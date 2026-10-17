# P3-05 Terrain edit pipeline: the plan

**Status:** approved by the owner on 2026-10-16 ("Go. Start with PR A"). Decisions TE1–TE4 are in
[phase0-decisions.md](phase0-decisions.md#phase-3-task-05-terrain-edit-pipeline-decided-2026-10-16).
**Branch:** `feature/p3-05-terrain-edits`, from origin/main 3f05e1b.
**Task:** row 05 of the [Phase 3 plan](phase2-14-phase3-plan.md#3-tasks) (0.3 §10).
**Rules implemented:** SH-02, GR-09, GR-10, CL-05, CL-07 and CF-09 ([design rules
§10](p3-02-design-rules.md#10-who-implements-what)).

## What the code had

None of the edit seams existed yet:
- The float32 heights were read only while the cache was built; at runtime everything read Unity's 16-bit terrain.
- The forest had no clearing mask (0.3 §10 said it did).
- The trees were one immutable GPU buffer.
- The crown map and the contour labels were built once, at open.
- Nothing produced `TerrainRevision`.

## Two PRs

- **PR A, the tile types** (`Runtime/Domain/Edits/**`, `Tests/Core/TerrainEditTests.cs`), first and small, so task
  04 can serialise them:
  - `EditTileKey`: a 64 m tile's column and row from the cache's north-west corner; 16 × 16 per cache tile.
  - `TerrainEditTile`: 64 × 64 points of whole-millimetre height changes, and 64 × 64 clearing bits (1 m cells). A
    tile owns its west and north edge points. Immutable, with a builder; an all-zero part is dropped.
  - `TerrainEdits`: the sparse, immutable set of tiles in key order. `With` shares every tile it didn't touch, and
    `Changed(from, to)` lists the tiles to rebuild.
  - `TerrainEditBytes`: canonical bytes, shape version 1 (golden `bec06021…`). Task 04 picks the save encoding.
- **PR B, the pipeline:**
  - **The edited heights (SH-02):** the float32 source at a lattice point plus its change. The cursor's elevation and
    slope read it; the camera keeps the 16-bit heights so it matches the mesh.
  - **The rebuild:** the cache on disk stays as built from the package; edits apply in memory to the touched tiles
    on commit, undo, redo and load. Heights upload by sub-rectangle; cover and splat are recomputed over the dirty
    rectangle plus 8 m; the contour labels and snowpack refresh for the region. No cache version bump is expected.
  - **Clearing (CL-05, CL-07):** the natural forest is planted as today and filtered by the mask; it's never
    regrown, so nothing outside the footprint moves. The cull shader reads a clearing bit buffer, the crown map
    rebuilds over the dirty rectangle, and the cover's forest weight drops so the snow fills the clearing.
  - **Undo (TE2):** `DesignHistory` steps hold their `TerrainEdits`; `TerrainRevision` counts up and never repeats.
  - **Preview (TE3):** the yellow pending contours only.
  - **Timing (CF-09):** `-editbench` on Jackson Hole 5 km, through the measure lane: a 300 m × 100 ft test bench
    (TE4) at 10 spots, 5 rounds; preview, commit and undo p95 with a per-stage breakdown. Plus `demo.bat` 41 before
    and after, and 0 B per idle frame.
  - **Demo 63:** opens the demo with `-testedit`; a dev key grades a 300 m strip under the cursor, and Ctrl+Z and
    Ctrl+Y undo and redo it.

## Tests

- PR A: key maths, canonical bytes and their golden, undo giving byte-identical edits, malformed bytes refused.
- PR B: edited heights equal source plus change; undo gives byte-identical heights; heights outside the footprint
  unchanged (GR-06); the clearing filter keeps every tree outside and none inside, and undo restores the forest
  golden; cover and forest goldens unchanged outside edited tiles; a sub-rectangle upload equals a full rebuild
  (EditMode); 0 B idle frames (PlayMode).

## As built (PR B)

- **Domain (`Domain/Edits`):**
  - `EditedHeights` (SH-02): the source at a lattice point plus its change, bilinear changes between points.
  - `TestBench` (TE4): the straight 300 m test bench.
  - `PendingContours` (CF-08): marching squares over the changed cells.
  - `IHeightSource` and `EditLattice`.
- **`DesignHistory` (TE2, additive):**
  - `Submit(command, terrain)`, `Load(document, terrain)`, `Terrain` and `TerrainRevision`.
  - `Stamp(inputSequence)` uses the current terrain revision.
  - `DesignSnapshot` carries both.
- **Persistence:**
  - `PackageHeights`: the float32 heights read a 256-row chunk at a time, identical doubles to the cache build's `HeightField`, which now shares its sampling code.
  - `CoverField.BuildRegion` over any heights, with a clearing predicate; `BuildTile` is now the whole-tile case.
  - `ForestField.IsCleared` (CL-05).
  - `GridFile.ReadChunkIndex` and `ReadFloatChunk`.
- **World:**
  - `SplatTexels.ComposeWindow`, equal to `Compose` byte for byte (EditMode test).
  - `TerrainTiles.ApplySplatRegion` and `SetHeightsRegion`.
  - `ForestInstance.Flags` (`Cleared`), which the cull and crown-map kernels skip.
- **Presentation:**
  - `TerrainEditPipeline`: plans the patches in parallel on workers, then applies heights, splat and trees by rectangle; the heightmap is rebound after the sync. Snow and contour labels rebuild off the main thread, latest change wins.
  - `PendingContourView`, drawn with `MountainPlannerEdits/PendingContours.shader`.
  - `ForestRenderer.UpdateTrees` and `RedrawCrownCore`.
- **App:**
  - `TerrainEditSession`.
  - `MountainViewer.Edits.cs`: `-testedit` (F6, F7, F8, Ctrl+Z, Ctrl+Y), `-testbench x,z[,deg]`, `-testbench-preview` and `-editbench <out.json>`.
  - The HUD's elevation and slope read the edited float32 heights.
- **The cache isn't bumped:** it stays v12, built from the package alone. Edits apply in memory to the touched tiles.
- **Memory:**
  - the tree instances are kept, about 21 MB on a 5 km site;
  - the crown map's sum buffer is kept, about 25 MB of VRAM on 5 km;
  - `PackageHeights` holds up to 6 chunks a layer, about 60 MB;
  - the edit-time `CoverField` holds about 100 MB on 5 km.
  All of it is allocated only once a session starts.
- **Design points:** task 03's design points are millimetres. Absolute EPSG:6350 millimetres overflow an `int`, so the test bench's trail points are millimetres from the site's centre on the package grid's axes. This is raised with the owner.
- **The test bench's edges look serrated** where its 1:1 faces end at the footprint on the 1 m lattice. That look belongs to task 08 (grading) and task 07 (the style tile).
