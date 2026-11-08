# Format fixtures (task 08, T11)

Saved files from each frozen format version. The rules are in
[0.3 §5](../../docs/plans/phase0-0.3-technical-architecture.md#5-package-cache-and-library-t5-t11), and the tests are
`FormatFreezeTests` in `Assets/MountainPlanner/Tests/Core`.

- **`v1-library/`** is a Phase 1 data folder, laid out as the game keeps `%LOCALAPPDATA%\SkiAreaDesignChallenge`:
  - It has no `library.json`, because Phase 1 wrote none. A folder without one is layout v1.
  - `recent.json`, the recently opened list (v1).
  - Under `Resorts/`, two real Phase 1 package manifests (format 1), with no grids:
    - Jackson Hole 2 km, the committed test terrain;
    - Crystal Mountain 5 km, on 3DEP fallback terrain (10 m, 3 m and 1 m). Its folder name differs from its package
      id, as it does after `acquire refresh-osm`.
  - `Resorts/5792676e513f5302/view.json`, a view state (v1).
  - `Downloads/sugarloaf-21248405/download.json`, a paused download (v1).

- **`v2-view/view.json`** is a view state as task P2-04 saves it (v2): the Jackson Hole view above, renamed
  "Teton Village" in Manage Areas. v2 added `Name`, the player's own name for the area; a v1 file migrates with an
  empty one, so the area keeps the name it was downloaded with.

- **`v1-save/Saves/5792676e513f5302/save-001/`** is a save (task P3-04, format "save" v1) of the Jackson Hole area:
  `save.json` (the P3-03 scripted design plus a planned trail) and three terrain edit tiles under `tiles/` (graded and
  cleared, graded only, cleared only), each named by its SHA-256.
  `SaveFileTests` checks that it reads unchanged and that today's writer reproduces it byte for byte. Git keeps these
  bytes exactly (`-text` in `.gitattributes`).

- **`v3-save/Saves/5792676e513f5302/save-001/`** is the same save in format v3 (task P3-09, LT44): every tower carries
  its `Kind`, and the lift with towers has one of each kind the player can choose (hold-down, support, combination).
  v1 and v2 saves migrate with every tower `auto`.

- **`v4-save/Saves/5792676e513f5302/save-001/`** is the same save in format v4 (task P3-15, TB-14): the design (shape 4)
  gains `Strokes`, one confirmed terrain brush stroke of each kind (raise, lower, flatten, smooth, slope), with their
  dabs delta-coded. v1, v2 and v3 saves migrate with no strokes. `SaveFileTests` checks that the writer reproduces it
  byte for byte, and that every older fixture opens through v3 → v4 unchanged.

- **`v5-save/Saves/5792676e513f5302/save-001/`** is the same save in format v5 (task P3-10, NW-15): the design (shape 5)
  gains `Areas`, the names the player gave Areas, each with the lift ends it held; here one, "Teton Village". v1 to v4
  saves migrate with no Areas named. `SaveFileTests` checks that the writer reproduces it byte for byte, and that every
  older fixture opens through v4 → v5 unchanged.

**`demo/`** holds files from a future version (format 99), for `demo.bat` 44 and 45. Each has only what the refusal
reads: a version number, and a name for the greyed library row.

- **`v6-save/Saves/5792676e513f5302/save-001/`** is the same save in format v6 (task P3-10, owner NT24): the design
  (shape 6) gains `TrailLines`, the trails made of two or more sections. Here "Lower Run 1" is taken back into Run 1 and
  cut once more, so Run 1 is three sections with a shared cut node. v1 to v5 saves migrate with none: every trail is one
  section, as it was. `SaveFileTests` checks that the writer reproduces it byte for byte, and that every older fixture
  opens through v5 → v6 unchanged.

- **`v7-save/Saves/5792676e513f5302/save-001/`** is the same save in format v7 (task P3-10, owner NT43–NT46): a shaped
  trail holds spans of runs between nodes, each end by its anchor (head, tail, a run landing, a crossing, a lift
  station, a cut) and its place along the run, and the design gains `Cuts`. Here the v6 trail's sections are whole-run
  spans, and a second trail, "Teton Ridge", ends at every kind of anchor, with one cut point. v1 to v6 saves migrate with
  no cut points and a v6 trail's sections as whole runs.

- **`v8-save/Saves/5792676e513f5302/save-001/`** is the same save in format v8 (task P3-10, sections finding 7): a
  trail keeps its clearing's `Outline` (rings of points, the 250 mm cells inside them by the even-odd rule) instead of
  the `Parts` it was painted with. Here Run 2 is repainted round an unpainted island, so one clearing has a hole. v1 to
  v7 saves migrate by rasterising each trail's strokes once; `SaveFileTests` pins the converted v7 fixture's hash, and a
  game opening an older save grades its built trails once more.

- **`v9-save/Saves/5792676e513f5302/save-001/`** is the same save in format v9 (task P3-15 round 4, owner BR22): the
  design (shape 9) gives each terrain stroke its clearing depth (TB-10 v2, `ClearMm`): the v8 fixture's five strokes clear
  at 1 ft (305), and a sixth, painted with Clear trees off, clears nothing (0). v1 to v8 saves migrate with every stroke at
  305, so they replay with the trees they had. `SaveFileTests` checks that the writer reproduces it byte for byte.

- **`v10-save/Saves/5792676e513f5302/save-001/`** is the same save in format v10 (lift overhaul B2, owner LD9): each lift
  keeps its `Drive` end, `"top"` or `"bottom"` (design shape 10). Here the first Sessellift FGQ-4 drives at the bottom,
  the second lift is a Monta FG4 driven at the top, and a Sessellift Stern Q-4 (`sessellift-stern4`, a catalog id new in
  v10, bottom drive only) is added. v1 to v9 saves migrate with each lift at its type's own end: the Sessellift's top, the
  Monta's bottom. `SaveFileTests` checks that the writer reproduces it byte for byte.

- **`v11-save/Saves/5792676e513f5302/save-001/`** is the same save in format v11 (task 4.21, TD-07): each trail and
  each shaped trail keeps its `Posted` rating, `"green"`, `"blue"`, `"black"`, `"double-black"` or `null` (design shape
  11). Here the shaped Run 1 is posted blue, Teton Ridge black, Run 2's own trail green and the first Run 1 piece
  double-black; the plan "Planned glade" stays null. v1 to v10 saves migrate with every one null; the game posts an
  older save's built trails once when it opens. `SaveFileTests` checks that the writer reproduces it byte for byte.

- **`v12-save/Saves/5792676e513f5302/save-001/`** is the same save in format v12 (task 4.03, owner 2026-11-08: LO-07 v2,
  LO-09, SI-12 v2). The design (shape 12) keeps the resort's `Hours` (`OpenSecond`, `LastChairSecond`, `StopSecond`; here
  9:00, 3:45 and 4:00), and each lift its `Schedule` (`"daily"` or `"peak-days"`) and its own `Hours` (or null to follow the
  resort's); here the Stern Chair runs on peak days, 9:30 to 3:15. The save gains a top-level `"Sim"`, the simulation
  sidecar's `{ SidecarVersion, Sha256 }`, null here (no simulation is saved before task 4.03's PR B). v1 to v11 saves migrate
  with the old hours (8:30, 4:15, 4:30), every lift daily, and `"Sim"` null. `SaveFileTests` checks that the writer
  reproduces it byte for byte.

- **`v1-sim/golden.sim`** is a simulation sidecar (task 4.03 PR B, SI-12 v2, `SimSidecar` v1): the golden script's world
  at 10:00 on Day 4 of its first season, with a 150 × 90 cell snow field, as a save keeps it under `sim/<sha256>.sim`.
  Its header carries the replay header (SI-22: the Pacing hash, the resort seed, the design's revision hash, the start
  year and the tick), and its last eight bytes the state hash the reader must reach. `SimSaveTests` checks that the
  writer reproduces it byte for byte and that it reads back. A change to the sidecar bumps `SimSidecar.Version` and
  adds a `v2-sim` fixture; a change to `Pacing`'s defaults moves the header's hash, so the fixture is made again only
  with that change approved.

The `v1-library` files are never edited. When a format gains version N + 1:
- add a `vN+1-…` folder;
- keep this one, and test that it still migrates to the same values.
