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

The `v1-library` files are never edited. When a format gains version N + 1:
- add a `vN+1-…` folder;
- keep this one, and test that it still migrates to the same values.
