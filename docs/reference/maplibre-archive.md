# MapLibre archive index

The MapLibre/TypeScript game is frozen at tag `maplibre-final` (branch `archive/maplibre`). It is **reference material only**: features, player-facing behaviour, doctrines and lessons. Nothing is copied, transpiled or embedded into the Unity project; see the roadmap's ground rules in [unity-rebuild-roadmap.md](../plans/unity-rebuild-roadmap.md).

To run it locally: `git worktree add ../mp-archive archive/maplibre`, then `npm ci` and `npm run dev` (or `launch-game.bat` for a release build) in that worktree.

All links below point at the immutable tag.

## Plans and design documents

| Document | What it records |
| --- | --- |
| [Guest performance audit](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/guest-performance-optimization.md) | Why the MapLibre game stuttered as guest counts grew (Document 1) |
| [Unity rebuild execution plan](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/unity-rebuild-execution-plan.md) | The approved plan for preservation, repo split and Phase 0 |
| [Simulation time and operations](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/simulation-time-and-operations.md) | The dual-clock time model and operations design |
| [Simulation design questions](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/simulation-design-questions.md) | Open and resolved simulation design questions |
| [Procedural buildings: pump house](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/procedural-buildings-pump-house.md) | Procedural building generation plan |
| [Architecture](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/architecture.md) | Landed architecture of the MapLibre game |
| [Dual-clock implementation](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/dual-clock-implementation.md) | How the dual clock was implemented, with [benchmarks](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/dual-clock-benchmarks.json) |
| [Integrated benchmark](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/performance/integrated-benchmark.md) | Performance benchmark method and [handoff](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/performance/integrated-benchmark-handoff.md) |
| [Agent architecture refactor](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/refactors/agent-architecture.md) | Refactor benchmarks and [metrics](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/refactors/metrics.md) |
| [Legacy poster app](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/history/legacy-poster-app.md) | History of the original app |
| [Agent guide](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/AGENTS.md) | Map layer order, hit priority and construction invariants |

## Code by domain

The roadmap (section 7) maps each area to the new Unity subsystem it informs.

| Domain | Archived code |
| --- | --- |
| Time model | [src/dualClock/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/dualClock), [time-engine/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/time-engine), [src/simulationKernel/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/simulationKernel) |
| Guests | [src/guestSimulation/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/guestSimulation) |
| Lifts | [src/lifts.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/lifts.ts) |
| Network and topology | [src/network.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/network.ts), [src/topology.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/topology.ts), [src/skiNodes.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/skiNodes.ts) |
| Trails and grading | [src/trails.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/trails.ts), [src/earthwork.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/earthwork.ts) |
| Ponds and dams | [src/pondEarthwork.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/pondEarthwork.ts), [src/damEarthwork.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/damEarthwork.ts), [src/pondGeometry.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/pondGeometry.ts) |
| Roads | [src/roads.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/roads.ts) |
| Snowmaking | [src/snowmakingHydraulicSolver.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingHydraulicSolver.ts), [src/snowmakingHydraulics.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingHydraulics.ts), [src/snowmakingNetwork.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingNetwork.ts), [src/snowmakingNodes.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingNodes.ts) |
| Snow and wear | [src/snow.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snow.ts), [src/snowSimulation.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowSimulation.ts) |
| Weather | [src/weather/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/weather), [weather-engine/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/weather-engine), [weather-service/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/weather-service) |
| Terrain and cover acquisition | [src/terrainIngest.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/terrainIngest.ts), [src/fourClassCover.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/fourClassCover.ts), [src/usgsTerrainCover.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/usgsTerrainCover.ts), [src/terrainPackage.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/terrainPackage.ts) |
| Buildings | [src/buildings.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/buildings.ts), [src/buildingMesh.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/buildingMesh.ts) |
| Saves | [src/types.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/types.ts), [src/gameSaveSchema.ts](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/gameSaveSchema.ts) |
| UI and screens | [src/app/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/app) (styles in [app.css](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/app.css)) |
| Desktop shell | [electron/](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/electron) |

The full tree: [maplibre-final](https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final). The deliverable 0.1 reference inventory will expand this index into a feature-by-feature record.
