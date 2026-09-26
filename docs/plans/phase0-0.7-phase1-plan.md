# Phase 0 · 0.7 Detailed Phase 1 plan: mountain vertical slice

**Audience:** the project owner and coding agents. **Status:** approved 2026-09-25 (decisions in §6). Phase 1 scope and exit criteria are in [0.6 §2](phase0-0.6-milestones.md#2-phase-1-mountain-vertical-slice); decisions are in the [decision record](phase0-decisions.md). **Phase 1 implementation starts when you approve this document.** Questions are in §6.

## 1. How Phase 1 runs

- **Branch and review:** one topic branch per task (`feature/p1-NN-…`), merged by PR into `main` **only after the owner approves it** (P4). Each PR includes hands-on demo steps. Code changes go through PRs; planning docs follow the one-branch rule.
- **Every PR must pass:**
  - `node tools/repo-checks/check.mjs`
  - `dotnet test` for the engine-free code (from task 02)
  - Unity EditMode and PlayMode tests in batchmode, locally
- **Owner gates:** tasks marked **⛳** stop for your review: the data-spike report, the style tile, and the Phase 1 exit.
- **Live providers** are used only by the acquisition tool and opt-in tests. Everything else uses recorded fixtures.
- **Prerequisites (your actions):** install **Blender 4.x LTS** and **Tree It** (both free) for tree assets (task 08). The .NET 10 SDK is already installed.

## 2. Task order

```
01 Data spike ⛳ ─┬─► 03 Georeferencing ─► 04 Acquisition + CLI ─► 05 Package/cache/library ─► 06 Terrain in Unity
02 Assemblies ───┘                                                                               │
                                     ┌───────────────────────────────────────────────────────────┤
                                     ▼                                                           ▼
                              07 Ground cover ─► 08 Style tile ⛳ ─► 09 Forest ─► 10 Snow/lakes/edge       11 Light + camera
                                                                   └──────────► 12 Layers ◄──────────────────┘
13 Picker ─► 14 Download UI, quality card, menu, library ─► 15 Benchmark ─► 16 Exit ⛳
```

Task 02 runs in parallel with 01. Task 11 can start once 06 lands. Tasks 13–14 can start after 05.

## 3. Tasks

| # | Task | Deliverables | Acceptance | Est. |
|---|---|---|---|---|
| 01 ⛳ | **Data spike** (engine-free C#, run as tests and a scratch CLI) | Cloud Optimized GeoTIFF reader: directory parsing, block offsets, HTTP range reads, LZW, floating-point predictor. Fallback fetch from `3DEPElevation/ImageServer` in EPSG:6350 at 1 m. Canopy, WorldCover, **BIGMAP species** (area download; licence check) and **LANDFIRE** (web service) access. Coverage-index reading decision. Recorded fixtures. **Report:** `docs/plans/phase1-data-spike-report.md` (sizes, timings, surprises) | A decoded S1M block has no NaNs and valid heights, and averages to the file's own 2 m overview within 0.05 m. Crystal Mountain 5 km returns a complete grid, with the source identified. Canopy, WorldCover and species layers align to the S1M grid within 1 cell. The BIGMAP licence and area-download method are confirmed, or LANDFIRE is chosen. The report is reviewed with you | 1.5 wk |
| 02 | **Assembly migration (T1)** | `Simulation` renamed to `Domain`; new `Simulation` placeholder (`IGameClock`, `ManualViewClock`), `Persistence`, `Acquisition`, `App`. Architecture tests extended. `AGENTS.md` updated. `tools/domain-tests` (.NET) plus a CI job. Banned-API repo check | All tests green in Unity and CI. The repo check fails on a deliberately planted `System.Random` in `Domain` | 0.5 wk |
| 03 | **Georeferencing and grids** (Domain) | EPSG:6350 forward and inverse; scale factors; local frame; grid and tile types | Round trip <1 mm; reference points match precomputed PROJ values to <1 cm; scale factor within ±1% | 0.5 wk |
| 04 | **Acquisition pipeline and CLI** | Stages; resumable cache; per-1 km-tile fallback chain with 50 m blend; manifest provenance and attribution; **quality score and one-liner (T18)**; `tools/acquire` .NET CLI. Builds the **2 km test terrain** (Jackson Hole; committed, Git LFS), the **Jackson Hole 5 km demo** and **Crystal Mountain 5 km** (both local only) | Re-running is idempotent and gives identical package hashes. A download killed midway resumes. The blend has no step >0.2 m at seams. Score arithmetic matches the T18 unit tests | 1.5 wk |
| 05 | **Package, cache and library** (Persistence) | Package read/write and validation; cache build (1,025²/513² 16-bit tiles, shared edges, core-to-ring edge match); library index; view-state JSON | Round-trip tests. 16-bit error ≤ half a step. Truncated or corrupt files are detected. A cache version bump triggers a rebuild | 1 wk |
| 06 | **Terrain in Unity** (World) | `ITerrainSurface`; one Terrain per tile with neighbours; per-preset pixel error; loading pipeline | PlayMode: neighbouring edge heights equal; `TerrainData` matches source within one step. The Jackson Hole demo opens in ≤10 s. **The open test runs with networking disabled** | 1 wk |
| 07 | **Ground cover (T6)** | Canopy → forest mask and tree height, **calibrated (D4)**; WorldCover classes; **species table from BIGMAP or LANDFIRE (TR3)**; OSM water and developed land rasterized at 1 m with anti-aliasing; soft class weights; terrain-aware boundaries; control/splat maps; cover-map overlay | Golden hashes stable. **The forest rule scores ≥80% against the Jackson Hole lidar truth set (D4; `research/forest_truth.py` recorded as a fixture).** The forest-fraction check against WorldCover works. Nothing blocky at close range (checked in 08) | 1 wk |
| 08 ⛳ | **Style tile** (0.5 §7) | A 1 km scene: 5 ground layers + snow, **3 species** (subalpine fir, mountain hemlock, bare aspen; 3 variants each) through the **free pipeline** (Tree It / EZ-Tree / Blender → the finishing script in `tools/assets/`) and the **tree shader** (wind, snow load, season hook; TR4), frozen lake, diorama strata edge (A1), bare rock over 55° (A2), 4 lighting presets + LUTs, HUD mock | **Your review passes:** the look matches 0.5, with no blocky cover, seams or noise. Screenshots are in the PR | 1.5 wk |
| 09 | **Forest at scale (T7)** | The remaining Phase 1 species (D3: Engelmann spruce, whitebark pine, limber pine, western hemlock, noble fir, Alaska yellow-cedar, krummholz) and the `species-map`; deterministic Burst Poisson sampling per 64 m tile; species drawn from cell weights; our own GPU instancing and impostor baker (TR2) | Both 5 km sites (Jackson Hole and Crystal Mountain) forested with their own species. Tile hashes are identical across runs. The forest fits within the frame budget in 15 | 1.5 wk |
| 10 | **Snow, lakes and edge** | Constant 12 in snow-depth texture; lake surface-state API (frozen); strata-wall edge at full scale | The lake state switches rendering via the API (unit test and visual). No gaps at the ring edge | 0.5 wk |
| 11 | **Lighting and camera (T9)** | NOAA solar position; sky; LUT blend; moonlit night (A4); shadows; orbit and free-fly with terrain collision and ring bounds; controls (U2) | Sun position within 0.1° of published values. The camera never goes below the snow or beyond the ring. Input response ≤100 ms | 1 wk |
| 12 | **Map layers (T17)** | Snow, Ground cover, Forest, Cover map toggles (keys 1–4; 5 reserved for imagery) | A toggle takes effect within one frame, with no rebuild (profiler marker) | 0.25 wk |
| 13 | **Site picker (T19)** | UI Toolkit tile map (USGS tiles), data-quality overlay, Nominatim search (Enter only, ≤1 request/s), 2–5 km slider (0.1 km steps), click to centre, exact Albers square, name suggestion, estimate line | Square corners are exact in EPSG:6350 (test). The rate-limit test passes. The offline panel shows without a network | 1 wk |
| 14 | **Download UI, quality card, menu and library** (functional styling) | Progress stages, minimise and resume; S5 card; minimal S1 and S2 | End to end: picker → download of a new site that includes a fallback area → quality card → open offline | 1 wk |
| 15 | **Benchmark and budgets (T13)** | A benchmark scene on the Jackson Hole demo with a fixed camera path; Performance Testing JSON results; a garbage-collection check | Reference PC: p95 ≤20 ms at 1080p High. Minimum-spec proxy: 30 FPS or better at Medium. 0 bytes GC per frame. Results committed with the commit SHA | 0.5 wk |
| 16 ⛳ | **Phase 1 exit** | Exit review against 0.6 §2; velocity versus estimate; **detailed Phase 2 plan**; `unity-m1` tag and `archive/unity` fast-forward | All seven 0.6 exit criteria met, or an explicit decision on each miss | 0.5 wk |

**Total:** about 14 weeks of task time (task 09 grew by half a week for the larger species set, D3). That is above the 8–12 week estimate in 0.6, so the estimate is revised here: **12–14 weeks**, with the data spike and the style tile as the biggest uncertainties.

## 4. Test data

- **2 km test terrain: Jackson Hole** (D2; S1M, 100/100). It is committed through Git LFS, and is the fixture for all offline tests.
- **Jackson Hole, Wyoming (5 km):** the demo, menu and benchmark site (D1). Built locally by the CLI and not committed (LFS quota; T14).
- **Crystal Mountain, Washington (5 km):** the fallback-path test site and the home of the Cascades species (D3). It is not in S1M yet. Built locally and not committed.
- **Forest truth set:** the Jackson Hole 2 km lidar canopy score (data-spike report §6), with more sites to follow.
- **Recorded provider responses:** small captured blocks and responses for each provider, committed as test fixtures.

## 5. Definition of done (every task)

- The acceptance checks in §3 pass and are automated where possible.
- The PR describes what changed and how it was verified, with screenshots for visual tasks.
- No new per-frame allocations; no banned APIs in engine-free code.
- `AGENTS.md` and docs are updated if the architecture changed.

## 6. Decisions (owner answers, 2026-09-25)

| ID | Decision |
|---|---|
| P1 | Phase 1 estimate of 12–14 weeks accepted |
| P2 | Blender approved. After research: **free tools only** for trees (Tree It, EZ-Tree, Blender), with real species (TR1–TR4) |
| P3 | Three review gates: data-spike report (01), style tile (08), Phase 1 exit (16) |
| P4 | **The owner approves every PR before it is merged.** Each PR includes demo steps for hands-on testing |
