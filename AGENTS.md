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
- Repo checks: `node tools/repo-checks/check.mjs` (also bans Unity and nondeterministic APIs in
  engine-free code).
- Engine-free tests without Unity: `dotnet test tools/domain-tests/Tests`.
- Hands-on demos: double-click `demo.bat`; add an entry for anything a PR makes demoable.
- Tests (editor closed): `<Unity.exe> -batchmode -projectPath . -runTests -testPlatform EditMode`
  (and `PlayMode`), results under `test-results/`.

## Assemblies
`Domain -> Simulation -> Persistence -> Acquisition -> World -> Presentation -> UI -> App`; references
only point left, per the allow-list in `docs/plans/phase0-0.3-technical-architecture.md` §2.
- Engine-free (`noEngineReferences`): `Domain`, `Simulation`, `Persistence`, `Acquisition`. Their tests
  live in `Tests/Core`, which Unity and `tools/domain-tests` (.NET, CI) both compile: no Unity APIs there.
- `Simulation` is only a clock placeholder (`IGameClock`, `ManualViewClock`) in iteration 1.
- Architecture tests in `Tests/EditMode` enforce the order and allow-lists.

## Determinism rules
- `Domain` and `Simulation`: keyed hash randomness only; no `System.Random`, `Guid.NewGuid`,
  wall-clock time or unordered parallelism (repo check). Stable iteration order.
- The same resort opens to the same tiles, splat and forest; golden hashes stay identical unless a
  behaviour change is approved.
- Future simulation: snapshots out, commands in; presentation and UI never mutate its state directly.

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
| Domain | `Assets/MountainPlanner/Runtime/Domain/` | Georeferencing, grids, determinism |
| Simulation | `Assets/MountainPlanner/Runtime/Simulation/` | Clock placeholder; future simulation |
| Persistence | `Assets/MountainPlanner/Runtime/Persistence/` | Package, cache and library formats |
| Acquisition | `Assets/MountainPlanner/Runtime/Acquisition/` | Providers, COG reader, package builds |
| World | `Assets/MountainPlanner/Runtime/World/` | Terrain, grading, vegetation, construction |
| Presentation | `Assets/MountainPlanner/Runtime/Presentation/` | Rendering, LOD, instancing |
| UI | `Assets/MountainPlanner/Runtime/UI/` | UI Toolkit, themes, view models |
| App | `Assets/MountainPlanner/Runtime/App/` | Bootstrap, scene flow, background tasks |
| Tests | `Assets/MountainPlanner/Tests/` | Core (engine-free), EditMode and PlayMode |
| Repo checks | `tools/repo-checks/` | Docs pairing, .meta integrity, banned APIs |
| .NET tools | `tools/domain-tests/`, `tools/data-spike/` | Unity-free builds and tests |
| Plans | `docs/plans/` | Roadmap and phase plans |
| Archived reference | `docs/reference/maplibre-archive.md` | Links into the frozen MapLibre game |
