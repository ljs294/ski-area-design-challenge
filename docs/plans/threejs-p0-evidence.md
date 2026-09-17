# Three.js P0 evidence

Date: 2026-09-15  
Plan: `docs/plans/threejs-migration.md`, revision 6  
Branch / baseline: `3js-edition`, `1cca4e0808b96fa0fc43d48c4289a118b29b30ec`; pinned comparison SHA `b915aef292da1bc31cd780b3555f3176a78ab5a7`

## P0-A — baseline

The pinned comparison contains the simulation merge and its integrated benchmark work. The current branch differs from it across 695 paths, including the current MapLibre runtime, dual-clock/simulation integration, benchmark harness, and generated profiling output. P0 does not move or modify any of those product paths.

Current reusable entry points:

- Application shell: `src/app/App.tsx`, `GameplayWorkspace.tsx`, `MapViewChrome.tsx`, `GameWindow.tsx`, `GameTabs.tsx`, `GameToolbar.tsx`, `ui.css`, and `gameWindows.css`.
- Terrain package/storage: `src/types/terrain.ts`, `src/terrainStorageClient.ts`, `electron/ipcTerrainStorage.ts`, and `src/app/resortProtocols.ts`.
- Input baseline: `useLiftController.ts`, `useTrailMapInput.ts`, `useNodePathController.ts`, and `trailBrush.ts`.
- Construction/grade path: `terrainGradeClient.ts` → worker/engine → controller review → `committedDocumentTransaction.ts` → terrain/topology publications → best-effort cover clearing.
- Deterministic workflows: `tests/e2e/feature-workflows/{construction-save,lift-preview,trail-painting}.spec.ts` and the repository `npm run check` gate.

Interaction baseline retained by the prototype:

| Workflow | Gesture and ownership |
| --- | --- |
| Lift | Click first terminal, move a live preview, click second terminal to enter review; Escape cancels. |
| Trail | Start at an eligible lift/trail anchor within 60 world metres; capture pointer; preserve distance-spaced samples; release finishes; Escape cancels. |
| Camera | Orbit/pan/zoom through one controls owner. `O` points the perspective camera down the local vertical for overhead view. Tool capture disables orbit until release/cancel. |
| Picking | Camera ray is intersected against the authoritative bilinear/feathered terrain sampler, independent of rendered triangles. |
| Overlay | World handles share the scene camera. The HTML coordinate label is projected and written in the same animation frame before rendering. |

Representative installed live packages:

| Class | Package | Core width | Analytical grid |
| --- | --- | ---: | ---: |
| Small | Bromley Mountain | 3,479 m | 2000² |
| Typical | Doublehead | 4,868 m | 2000² |
| Large | Crystal | 6,419 m | 2000² |

Target lane: 1920×1080 CSS/internal resolution, DPR 1, 60 Hz; warm scene; near/typical/far orbit and overhead views; 80 pointer samples across terrain; lift click–move–click; anchored captured trail; camera motion with label. Hardware inventory: Intel Core i7-6600U, 8.4 GB RAM, Intel HD Graphics 520 driver 30.0.100.9806, Windows 10 build 19045. This is a conservative development lane and was in Power Saver mode. GPU timing and qualified release FPS remain unavailable in headless Chromium.

## P0-B — standalone prototype

The disposable app is in `prototypes/three-terrain/`; it is not imported by the product or Graphics Lab. From that folder:

```text
npm install
npm run dev
```

The development server reads existing desktop packages from `%APPDATA%/ski-area-design-challenge/terrains` (override with `MOUNTAIN_PLANNER_TERRAIN_DIR`) and also accepts a complete TerrainRecord JSON. It builds 64×64-cell fixed-detail chunks, vertex ground-cover colors, restrained directional/hemisphere lighting, orbit/overhead controls, analytical picking, lift placement, 60 m trail-anchor eligibility, captured/resampled trail painting, cancellation, world handles, and a synchronized DOM label.

Recorded Doublehead typical view: [doublehead-typical.png](../../prototypes/three-terrain/artifacts/doublehead-typical.png). Tool and overhead captures are beside it. The corresponding current-game save preview is `C:\Users\vermo\AppData\Roaming\ski-area-design-challenge\saves\9217af9d-7fbc-4217-80de-efbaa8ff849c.preview.jpg` (local evidence, not copied into the repository).

The first prototype exposed a hard rectangular core/surround color seam. The bounded P0 follow-up now feathers ground-cover color into an elevation-based surround treatment. The remaining stylistic target is a restrained tabletop alpine diorama: readable land-cover masses and terrain form first, fine feature geometry second, without photorealistic objects.

## P0-C — measurements and decisions

Doublehead at the fixed 257×257 display grid:

| Measurement | Result |
| --- | ---: |
| Chunks / triangles / vertices | 16 / 131,072 / 66,564 |
| Position + color + index buffers | 3.0 MiB |
| Analytical picking p95, 20–80 pointer moves | 5.4–14.4 ms |
| Maximum fixed-grid midpoint surface error | 32.0 m |
| Headless frame interval | unavailable (headless Chromium reported 0; not accepted as FPS evidence) |

Decision: use mixed display detail or an equivalently bounded interaction-neighborhood refinement in P2. The 257² fixed grid is useful for the far view and proves the chunk/resource shape, but its 32 m worst-case error cannot satisfy near-view handle/stroke agreement. Analytical resolution remains the package grid. Before construction is enabled, P2 must prove shared edges, four chunks, core/surround transitions, deformation across levels, stable analytical picking, and no handle movement when display detail changes.

Numeric budgets fixed for P1–P4:

- Qualified GPU lane: frame interval p95 ≤20 ms and p99 ≤33.3 ms after warmup at 1920×1080, DPR 1.
- Terrain picking: p95 ≤16 ms and p99 ≤33 ms during a dense/grazing pointer workload; ordinary visible input response <100 ms.
- Analytical/display agreement: ≤2 CSS px at the closest supported view and ≤3 m world error around an active stroke or selected handle.
- Confirmation: longest application-thread interruption ≤50 ms; confirm-to-visible p95 ≤100 ms and p99 ≤250 ms, reported separately from worker analysis time.
- Terrain presentation memory: ≤256 MiB resident GPU terrain/cover resources on the large fixture and ≤512 MiB peak combined source/worker/staging terrain memory during a grade.
- Long-session resource tolerance: after five load/dispose cycles, live renderer resources and JS heap settle within 10% of the post-warmup baseline.

Future approaches recorded for the P1 boundary:

- Deformation invalidation accumulates sparse accepted patches from the last presented revision and expands for interpolation, normals, chunk borders, contours, picking bounds, and every resident detail level.
- Authoritative terrain arrays stay with the session/storage owner. Workers receive copies or explicitly owned transfer buffers; renderers own only derived staging/GPU buffers.
- Save publication uses immutable terrain generations followed by atomic design-head publication. Editing during a save remains dirty; preview/index failure cannot reverse an authoritative save.
- Simulation/weather/live operations are capability-unavailable in the design runtime; no fabricated controller or values.
- P1 is bounded to renderer-independent committed ownership, failure-isolated notifications, fork storage identity, and recoverable design/terrain publication. It does not integrate the Three.js host into gameplay.

Visual decision: proceed. The real-package scene establishes readable relief, cover, controls, handles, and overlay synchronization. The color seam was fixed in P0. Near-view surface accuracy is the measured deficiency and is explicitly assigned to the mixed-detail/refined-neighborhood proof before construction, rather than hidden by accepting the coarse mesh.

