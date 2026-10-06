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

**`demo/`** holds files from a future version (format 99), for `demo.bat` 44 and 45. Each has only what the refusal
reads: a version number, and a name for the greyed library row.

The `v1-library` files are never edited. When a format gains version N + 1:
- add a `vN+1-…` folder;
- keep this one, and test that it still migrates to the same values.
