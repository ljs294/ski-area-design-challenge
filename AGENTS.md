# Mountain Planner (Unity) agent guide
This file is the canonical guidance for repository-aware agents. `CLAUDE.md` contains only `@AGENTS.md`;
a nested `AGENTS.md` needs a same-directory `CLAUDE.md` with the same import.

## Product
- A from-scratch rebuild of the ski resort tycoon in Unity 6.3 LTS (URP), Windows desktop first. The
  editor version is pinned in `ProjectSettings/ProjectVersion.txt`; do not upgrade without approval.
- The archived MapLibre/TypeScript game (`archive/maplibre`, tag `maplibre-final`) is reference only.
  Never copy, transpile, or embed its code; re-specify behaviour in `docs/` and implement natively.
- The roadmap is `docs/plans/unity-rebuild-roadmap.md`; phase plans live in `docs/plans/`.

## Commands
- Repo checks: `node tools/repo-checks/check.mjs`.
- Tests (editor closed): `<Unity.exe> -batchmode -projectPath . -runTests -testPlatform EditMode`
  (and `PlayMode`), results under `test-results/`.

## Assemblies
`Simulation -> World -> Presentation -> UI`, never the reverse. `Simulation` has no UnityEngine
references and is deterministic. Each assembly has a matching test assembly.

## Simulation rules
- Fixed tick, integer money, keyed seeded randomness; no wall-clock or frame-rate dependence.
- Golden-trajectory tests stay identical unless a behaviour change is approved.
- Snapshots out, commands in: presentation and UI never mutate simulation state directly.

## Performance rules
- Per-frame paths allocate nothing per agent (NativeArrays, Burst jobs, pooled buffers).
- UI view models update from snapshots at bounded rates; no UI rebuild per tick.
- Agent elevation comes from precomputed samples; textures update by dirty rectangle.
- Budgets: frame p95 ≤20 ms at 1080p on the reference GPU; measure before and after perf work.

## Unity and Git hygiene
- Commit every `.meta`; never commit `Library/`, `Temp/`, `Logs/`, `UserSettings/`, or project files.
- Binaries use Git LFS; scenes and prefabs merge with UnityYAMLMerge; one owner per scene/prefab.
- Asset Serialization stays Force Text.

## Branches
- `main` is the current version; topic branches merge by PR.
- `archive/maplibre` and `maplibre-final` are frozen. `archive/unity` moves only to a green milestone
  tagged `unity-mN`.

## Routing
| Area | Start here | Main concern |
| --- | --- | --- |
| Simulation | `Assets/MountainPlanner/Runtime/Simulation/` | Determinism, tick budget |
| World | `Assets/MountainPlanner/Runtime/World/` | Terrain, grading, vegetation, construction |
| Presentation | `Assets/MountainPlanner/Runtime/Presentation/` | Rendering, LOD, instancing |
| UI | `Assets/MountainPlanner/Runtime/UI/` | UI Toolkit, themes, view models |
| Tests | `Assets/MountainPlanner/Tests/` | EditMode and PlayMode |
| Repo checks | `tools/repo-checks/` | Docs pairing, .meta integrity |
| Plans | `docs/plans/` | Roadmap and phase plans |
| Archived reference | `docs/reference/maplibre-archive.md` | Links into the frozen MapLibre game |
