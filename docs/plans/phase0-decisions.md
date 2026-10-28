# Phase 0 decision record

**Audience:** the project owner and coding agents. **Status:** living record; each deliverable adds its decisions when approved.

A decision here overrides older text in the [roadmap](unity-rebuild-roadmap.md) until the roadmap is amended (deliverable 0.6).

## Scope (2026-09-25)

| ID | Decision |
|---|---|
| S1 | **Iteration 1 is just the mountain:** a real-world site picker, USGS S1M 1 m lidar terrain, and game-made lighting, camera, ground cover, forest and snow |
| S2 | Drawing on the terrain (lifts, trails, etc.) and simulation are **future iterations**: planned for, not built. Only a clock placeholder exists |
| S3 | **Fully offline after the first download.** Downloads may be slow; about 1 GB per resort on disk is acceptable |
| S4 | US-only: contiguous US now; Hawaii and Puerto Rico when USGS publishes them |

## 0.1 Reference inventory: approved 2026-09-25

| ID | Decision |
|---|---|
| A1 | The [inventory](phase0-0.1-reference-inventory.md) is accepted as the record of the archived game; no corrections |
| A2 | Equipment uses generic names, not real brands or standards references, unless permission is obtained |

## 0.3 Technical architecture: approved 2026-09-25

Details in [0.3](phase0-0.3-technical-architecture.md).

| ID | Decision | Section |
|---|---|---|
| T1 | Assemblies: engine-free `Domain`, `Persistence` and `Acquisition`; `Simulation` becomes the clock placeholder; then `World`, `Presentation`, `UI` and `App` | [§2](phase0-0.3-technical-architecture.md#2-assemblies-t1) |
| T2 | Background tasks for acquisition and loading; Burst for forest and splat; derived data cached but never the only copy | [§3](phase0-0.3-technical-architecture.md#3-runtime-structure-t2) |
| T3 | Keep S1M's native grid (EPSG:6350, NAVD88 metres); lidar is never resampled | [§4.1](phase0-0.3-technical-architecture.md#41-coordinates-keep-s1ms-native-grid-t3) |
| T4 | Square sites of 2–5 km at 1 m; a 3 km ring at 2 m. Fallback per 1 km tile: S1M → 1 m project lidar → 1/9″ → 1/3″ (3DEP dynamic service), blended over 50 m | [§4.2](phase0-0.3-technical-architecture.md#42-elevation-extent-and-resolution-t4), [§4.3](phase0-0.3-technical-architecture.md#43-from-lidar-to-unity-terrain-t4) |
| T5/T11 | Immutable package of float32 source layers plus a rebuildable 16-bit terrain cache (about 650 MB for 5 km); no network when opening a resort | [§5](phase0-0.3-technical-architecture.md#5-package-cache-and-library-t5-t11) |
| T6 | Ground cover: Meta/WRI 1 m canopy (forest and tree height), ESA WorldCover (other classes), OpenStreetMap (water and developed); a "developed" class; **must not look blocky** | [§4.4](phase0-0.3-technical-architecture.md#44-ground-cover-sources-t6) |
| T7 | Deterministic procedural forest per 64 m tile; tree size from canopy height | [§4.5](phase0-0.3-technical-architecture.md#45-forest-t7) |
| T8 | Snow: a flat 12 in everywhere through the snow-depth texture. Lakes frozen and snow-covered through a per-water-body surface state for a future weather engine | [§4.6](phase0-0.3-technical-architecture.md#46-snow-t8) |
| T9 | Real sun position from a time/date scrubber; orbit camera bounded to the ring, plus free-fly | [§4.7](phase0-0.3-technical-architecture.md#47-lighting-and-camera-t9) |
| T10 | Providers: S1M, the 3DEP dynamic service, Meta/WRI canopy, WorldCover, OpenStreetMap, Nominatim, USGS map tiles. Not Cesium ion, Esri or CARTO | [§6](phase0-0.3-technical-architecture.md#6-acquisition-and-provider-terms-t10) |
| T12 | Deterministic derived data: keyed randomness, banned APIs, golden tests | [§7](phase0-0.3-technical-architecture.md#7-determinism-t12) |
| T13 | Minimum spec: RTX 2060, Ryzen 5 3600 / Core i5-9600K class, 16 GB RAM, SSD, 30 FPS or better at 1080p Standard. Reference PC: RTX 3060 Ti, p95 ≤20 ms at High | [§8](phase0-0.3-technical-architecture.md#8-performance-budgets-and-hardware-t13) |
| T14 | Mostly engine-free tests; a checked-in 2 km S1M test site; a `dotnet test` CI job | [§9](phase0-0.3-technical-architecture.md#9-testing-t14) |
| T15 | Seams for drawing and simulation: float32 source heights, clearing mask, immutable package, snow-depth texture, lake state, clock placeholder | [§10](phase0-0.3-technical-architecture.md#10-future-iterations-and-the-seams-iteration-1-keeps-t15) |
| T16 | Add Performance Testing, Burst, Collections, Mathematics and Newtonsoft JSON; remove AI Navigation and Timeline | [§11](phase0-0.3-technical-architecture.md#11-packages-for-phase-1-t16) |
| T17 | Map layers: Snow, Ground cover, Forest (on by default) and a Cover map overlay (off); instant toggles | [§4.8](phase0-0.3-technical-architecture.md#48-map-layers-t17) |
| T18 | A terrain quality score (0–100) and a one-line data summary after download. Weights: S1M 100, 1 m lidar 95, about 3 m 60, about 10 m 30 | [§4.2](phase0-0.3-technical-architecture.md#42-elevation-extent-and-resolution-t4) |
| T19 | Site picker: a pop-up mini-map with search, a 2–5 km slider (0.1 km steps), click to centre, and a required map name with a suggestion | [§6.1](phase0-0.3-technical-architecture.md#61-site-picker-t19) |

## 0.2 Game design and flow: approved 2026-09-25

Details in [0.2](phase0-0.2-game-design.md#8-decisions-owner-answers-2026-09-25).

| ID | Decision |
|---|---|
| G1 | Title: **Ski Area Design Challenge** (for now) |
| G2 | Bundled demo and menu background: **Jackson Hole, Wyoming** (S1M 1 m, 100/100). Was Crystal Mountain; changed by D1 |
| G3 | Photo mode in iteration 1 |
| G4 | Free-fly down to a few metres above the snow; no walking; bounded to the ring |
| G5 | Background downloads, one at a time |
| G7 | Optional satellite imagery layer from USGS NAIP (nice to have) |
| G8 | Unity-native graphics settings menu |
| G9 | Other keep/change/drop decisions accepted |

## 0.4 UI/UX spec: approved 2026-09-25

Details in [0.4](phase0-0.4-ui-ux.md#9-decisions-owner-approval-2026-09-25).

| ID | Decision |
|---|---|
| U1 | Screen layouts for the picker, quality card and mountain HUD approved |
| U2 | Controls: free-fly C, tilt R/F, layers 1–5, hide UI H, photo mode P |
| U3 | Unity-native graphics menu option list approved |
| U4 | Font: Inter |
| U5 | Time scrubber resets to 10:30, January 15 |

## 0.5 Art direction: approved 2026-09-25

Details in [0.5](phase0-0.5-art-direction.md#9-decisions-owner-approval-2026-09-25).

| ID | Decision |
|---|---|
| A1 | Diorama base with rock-strata walls at the ring edge |
| A2 | Bare rock on faces steeper than about 55° (visual only) |
| A3 | Superseded by TR3 (BIGMAP species data places deciduous trees) |
| A4 | Moonlit night included |
| A5 | Model-railway / Parkitect diorama look, not photoreal |

## Trees: decided 2026-09-25

| ID | Decision |
|---|---|
| TR1 | **Real species with a stylized look:** the right species in the right places, rendered in the diorama style (A5). Phase 1 set revised by D3 |
| TR2 | **Free tools only:** Tree It, EZ-Tree (MIT), Blender (Sapling / Geometry Nodes). No SpeedTree or paid tree tools; paid renderers only with explicit OK. Our own instancing and impostor baker |
| TR3 | Species data from USFS FIA **BIGMAP** (30 m, 327 species), falling back to **LANDFIRE** Existing Vegetation Type; BIGMAP licence and area download verified in the data spike |
| TR4 | One tree shader with **wind, snow load and season** inputs; trees built with separate leaf geometry. Iteration 1: full snow load, winter (deciduous trees bare) |

## 0.6 Milestone plan: approved 2026-09-25

| ID | Decision |
|---|---|
| M1 | No RTX 2060 available. Phase 1 stand-in: this PC reaches about 55 FPS at Medium within a 6 GB VRAM budget. Phase 2: a tester's PC or a rented cloud GPU. The old laptop is a Low-preset smoke test |
| M2 | Drawing (Phase 3) before simulation (Phase 4) |
| M3 | First shareable build at the end of Phase 2, to chosen testers |

## 0.7 Detailed Phase 1 plan: approved 2026-09-25

| ID | Decision |
|---|---|
| P1 | Phase 1: 12–14 weeks |
| P2 | Blender and Tree It installed for the free tree pipeline |
| P3 | Review gates: data spike (01), style tile (08), Phase 1 exit (16) |
| P4 | The owner approves every PR before merge; PRs include demo steps |

## Data spike (review gate 1): decided 2026-09-25

Details in [phase1-data-spike-report.md](phase1-data-spike-report.md) §5–6.

| ID | Decision |
|---|---|
| D1 | The bundled demo, menu background and benchmark site is **Jackson Hole** (5 km, 100/100). Crystal Mountain stays as a regular download and the fallback-path test site |
| D2 | The checked-in test terrain is **Jackson Hole 2 km** |
| D3 | Phase 1 species: every species over 3% at **Jackson Hole** or **Crystal Mountain**: Douglas-fir, Engelmann spruce, quaking aspen, subalpine fir, lodgepole pine, Pacific silver fir, mountain hemlock, western hemlock and noble fir, plus the krummholz form. **Corrected 2026-09-26** after the species-sampling bug (spike report §8) |
| D4 | The **canopy map is the primary forest layer**: any tree in a 10 m cell counts, with density and height calibrated. Scored against lidar truth it was 82% correct against WorldCover's 72%. WorldCover supplies the non-forest classes. Task 07 keeps a lidar-truth regression test; more truth sites follow |
| D5 | BIGMAP is treated as public-domain federal data and credited in the game |
| D6 | Hands-on demos run from **`demo.bat`** at the repo root; each phase adds its entries, and the game gets a Play entry once it builds |

## Trees review, round 1: decided 2026-09-26

Details in [phase1-trees-first-look.md](phase1-trees-first-look.md).

| ID | Decision |
|---|---|
| T1 | More realistic than round 1, still stylized: skeleton plus alpha-textured cards (round 2). Show Jackson, NH hardwoods too |
| T3 | Trees export as **FBX** (Unity-native), not glTF |
| T4 | **Blender 5.2 LTS** is the pinned tree tool |
| T5 | Palette adds pale aspen and birch bark, `#D3D0C2` |

## Trees review, rounds 2 and 3: decided 2026-09-27

| ID | Decision |
|---|---|
| T6 | The tree look is approved (round 3): a skeleton plus procedural alpha-textured cards. Tree It isn't demoed for now |
| T7 | Start the game at a **medium snow load** on trees; tune it in the style tile |
| T8 | Keep the Jackson, NH hardwoods (sugar maple, red maple, yellow birch, paper birch, beech) in the library |
| T9 | **Seasons: one model per tree for the whole year.** Shader inputs for leaves present, colour turn, spring tint and snow load, driven by a phenology table per species (dates shifted by latitude and elevation, with a per-tree offset) and the view date; per-card season data is in the meshes |
| T10 | **Tree budgets, enforced at build time:** LOD0 ≤10,000, LOD1 ≤2,500, LOD2 ≤500 triangles, then an impostor beyond 150 m; confirmed or tightened by the Unity measurements in task 08 (0.3 §8.1) |

## Acquisition: decided 2026-09-27

| ID | Decision |
|---|---|
| U6 | **Download progress is always visible and specific:** an overall bar with time remaining, plus a detail line naming the current step and its progress (for example "Terrain: downloading sector 5 of 19 · 35%"), updated at least 4 times a second. Long downloads and preparation are acceptable; silent ones are not |
| P5 | Resort package files are **losslessly compressed**. Only the 2 km Jackson Hole test terrain is committed (Git LFS); CI fetches only small test fixtures, and tests that need the test terrain run locally |
| P6 | Task 04 ships in two PRs: **04a** terrain (heights, fallback and blend, package format, resume, quality score, CLI, progress) and **04b** the other layers (canopy, land cover, species) |
| P7 | **Canopy covers the 1 m core only.** Its files have no overviews, so the 11 km ring would add 500–700 MB per map. The ring's forest comes from WorldCover (10 m) and BIGMAP species (30 m); it's scenery |
| F1 | **Flora quality score** beside the terrain score: coverage 35%, canopy/WorldCover agreement 25%, species fidelity 25%, recency 15%, with a one-liner; measured from the package's own layers (0.3 §4.2) |

## Lift asset pilot: decided 2026-09-28

Details in [lift-pilot-sessellift-fgq4.md](lift-pilot-sessellift-fgq4.md). The pilot comes ahead of Phase 3 (lifts) to test whether Claude can author game-ready 3D assets.

| ID | Decision |
|---|---|
| LP1 | **Pilot scope:** one fixed-grip quad chairlift from a fictional maker: a drive terminal, a return terminal and one quad chair. Modelled: machinery, mast and column, hood, sheave trains, catwalks and railings, ladders, lifting frame, tension carriage, and the pedestal and footing below grade. Left out: conveyor and vault, huts, gates, fences, ramps, the bullwheel chair-guide ring and the load and unload interfaces |
| LP2 | **Budgets, enforced at build time** (`tools/assets/lifts/budgets.json`): terminal LOD0 ≤24,000, LOD1 ≤10,000, LOD2 ≤2,500, LOD3 ≤500 triangles; chair ≤1,600, 250 and 60 (LOD0 raised from 800 by the owner on 2026-09-29 so the grips can be modelled). They replace the roadmap's terminal ≤8k (§12). Assets export as FBX (T3) |
| LP3 | **Maker code names:** Sessellift (German), Monta (French) and Chairworks (American); defunct classic makers get fictional homage names later. Real makers, resorts, drawings and other sources are never named in the repo, commits or PRs; the models are our own designs |
| LP4 | **Moving parts sit on their real axles:** the bullwheel and every sheave are separate child meshes with their pivot on the axle, static for now |
| LP5 | **Livery on the drive hood only.** The player picks the hood colour at runtime; everything else keeps the maker's colours |
| LP6 | **Snow through `_SnowLoad`** on up-facing surfaces, like the trees |
| LP7 | Terminal pedestals and footings extend **2.5 m below grade**, so terminals meet sloping ground |
| LP8 | **Hood windows are tinted glass** with a dark interior lining and a cheap machinery silhouette behind them; end windows match the side glass in tint and band |
| LP9 | **Entry sheave trains come from photos** where the terminal drawings leave them out: 4-sheave trains on both terminals, support at the drive and hold-down at the return |
| LP10 | **The owner approves every model change** from review photos before it's imported into Unity |

## Line towers: decided 2026-09-28

Details in [lift-towers-sessellift-fgq4.md](lift-towers-sessellift-fgq4.md). The sheave assemblies follow the owner's own reference model of a quad chairlift; the model stays local and is never named.

| ID | Decision |
|---|---|
| LP11 | **Line towers are a modular kit:** a base (footing 2.5 m below grade, pier, base plate), 1 m mast sections with the ladder, and a head. The game stacks sections to any height in 1 m steps. The head is the drive terminal's entry head (shared code, `liftkit/heads.py`) on a mast cap, unchanged, with its platform kept inside the ropes |
| LP12 | **Head types and sheave counts:** general support towers carry 4 or 6 sheaves per rope on a nearly flat arc; breakover towers 8 on the reference arc; hold-down towers 8 (the breakover assembly flipped); combination towers 4 hold-down sheaves directly over 4 support sheaves on triangular plates. The assemblies (rockers, train yokes, equaliser) follow the owner's reference model |
| LP13 | **Every sheave assembly hangs from below the crossarm end** on two lug plates, its pin 320 mm under the crossarm. The return terminal's integrated tower is a hold-down tower with the same connection (8 sheaves per rope), and replaces LP9's 4-sheave hold-down trains there. Its row is levelled at the first sheave, so the rope runs level through the loading area and the line leaves the station climbing at 16° (a 13 m arc, the tightest whose tilted equaliser clears the crossbeam) |
| LP14 | **Towers are galvanised throughout; in every sheave row only the first and last sheave are red** (lightning grounding) and the rest galvanised, on the terminals' trains too. Tower number plates are black |
| LP15 | **Tower budgets, enforced at build time:** head LOD0 ≤16,000, LOD1 ≤5,000, LOD2 ≤1,000, LOD3 ≤200 triangles, switching at 15, 45, 150 and 800 m (doubled by the PC lodBias); mast section ≤150 and base ≤400 at LOD0. Sheaves spin only at LOD0; from LOD1 a head is one mesh |

## Task 09, forest at scale: decided 2026-09-30

| ID | Decision |
|---|---|
| F2 | Placement is Poisson-disc per 64 m tile, grown with Burst in the game and as plain C# in the downloader and CI (identical bytes); the forest's golden hashes are new, and caches rebuild once |
| F3 | Krummholz: each site's treeline from its own canopy map; trees turn into krummholz in the 150 m below it, all of them in the top 50 m; mats only on gentler slopes |
| F4 | Trees switch LOD sooner (screen heights 0.35 / 0.14 / 0.05, was 0.25 / 0.10 / 0.05), so dense forests such as Sugarloaf's fit the frame budget (option A). A live "Auto" tree-detail setting belongs with the graphics settings menu later |
| F5 | The species priority survey counts every operating downhill ski area in the lower 48 once (OpenSkiMap); New England's species are task 09 phase 2 (NE1-NE4 in [new-england-species-plan.md](new-england-species-plan.md)) |

## SLE snow guns: decided 2026-09-29

Details in [snow-guns-sle.md](snow-guns-sle.md). The guns follow the owner's own reference model of a stick gun for their dimensions and the owner's photos of real units for the heads and the tripod; the model stays local and is never named.

| ID | Decision |
|---|---|
| LP16 | **SLE is the code name of our snowmaking maker** (LP3 applies: no real maker is named). Two guns: a stick gun on a lance of 10, 20 or 30 ft of pipe (one build, three variants) and a ground gun on an aluminium tripod. Both carry the same head: the fan block with its 100 × 100 mm, 12-nozzle face, a 2 in barrel 305 mm long and a 74 mm nucleator cap; on the stick gun it sits on a Y block at the lance's end |
| LP17 | **A snow gun's origin is at grade:** on the stick gun's base mast (a 4 in post standing 1.08 m above grade and 0.45 m below it, the lance pinned 1.15 m up) or under the ground gun's tripod pivot. +Z is the way it fires |
| LP18 | **Aim is per instance:** each gun has one hinged moving part (the lance, or the ground gun's gun), tilted about X on its pivot; turning the whole gun about +Y is the placement's yaw. The stick gun's stay rides with the lance, so its tilt stays within a few degrees |
| LP19 | **Snow gun budgets, enforced at build time:** LOD0 ≤600, LOD1 ≤160, LOD2 ≤40, LOD3 ≤12 triangles, switching at 12, 40, 150 and 600 m (doubled by the PC lodBias). Shadows from LOD0-1. Built for hundreds on a mountain |

## Monta FG4 and Chairworks chairs: decided 2026-09-29

**Context:** a second terminal pair and a second chair, each from the owner's own reference model (measured
locally, never named), and one fixed grip for every fixed-grip chair. The owner approved every model from review
photos (LP10). Details: [lift-monta-fg4.md](lift-monta-fg4.md) and [chairs-chairworks.md](chairs-chairworks.md).

| # | Decision |
|---|---|
| LP20 | **Monta FG4 is a bottom-drive lift.** The drive terminal loads: twin tubular booms, a ribbed rounded hood with a window band over the bullwheel, inclined legs on two piers. The return is the top station: an exposed bullwheel on an inclined leg and a column. A Monta terminal's origin is on its bullwheel's axle at grade, +Z toward the line; the line gauge is 4,895 mm (amended in task P3-09, 2026-10-19: 4,120 mm, the Sessellift's, with the bullwheel scaled and the sheaves moved in, so both lifts share one tower head kit). Both terminals are lowered to the Sessellift chair's rope height (3,039 mm) by cutting their diagonal members at the same angles, so the lift carries that chair unchanged. The return's footings reach 2.5 m below grade (LP7) |
| LP21 | **The Chairworks quad chair is one body with two grips** (variants): the detachable grip built part by part, or the fixed grip (LP22). The body: the hanger's dogleg into a clamp on the top bar; two inboard side frames of bent tube (300 mm knees, 120 mm corners); a looped seat frame; a bench and a low backrest. Tube sizes match the Sessellift chair's: hanger Ø80, frame Ø60, seat rails Ø52 |
| LP22 | **One fixed grip for every fixed-grip chair** (`fixed_grip.json`): a cast arm and housing that the hanger flows out of through a socket, dark blades over the top half of the rope, then the collar, spring, nut and bolt. The Sessellift chair carries it too |
| LP23 | **No safety bar on the Chairworks chair for now** (the owner). On this chair a bar that closes over riders cannot be raised compactly in front of the hanger: the rope is under 1 m above the top bar and tower sheaves reach about 430 mm below the rope, so every raised pose in front sticks out forward. To revisit: modelled down in front, up with the footrest forward on new brackets, or behind the seat, tucked in |

## Task 15, benchmark and budgets: decided 2026-10-05

**Context:** the benchmark the 0.3 §8 budgets are checked with (T13), and the quality presets it measures. Details and
numbers: [phase1-benchmark-report.md](phase1-benchmark-report.md).

| # | Decision |
|---|---|
| B1 | **Four quality presets, one Unity quality level and URP asset each** (`Assets/Settings/Quality_*_RPAsset.asset`), chosen by `-quality` (High by default). The owner approved them from side-by-side captures. Low: render scale 0.8, no MSAA, 60 m single-cascade 1024 shadows without soft shadows, tree LOD bias 1, Low terrain detail, no terrain sky occlusion or distant shadows. Medium: 2× MSAA, 100 m two-cascade 2048 shadows with low soft shadows, LOD bias 1.5, Medium terrain. High (today's look): 4× MSAA, 150 m four-cascade 2048 shadows with high soft shadows, LOD bias 2, High terrain. Ultra: as High with 250 m, 4096 shadows, LOD bias 3 and Ultra terrain. Unity's Mobile and PC levels are gone |
| B2 | **The benchmark is a fixed camera path**, not fixed views: the eight views as legs, each a 3 s flight and a 6 s turning hold, so the camera always moves. One lap warms up (and takes each leg's screenshot), two are measured. Results are JSON with the commit SHA (stamped at build time), checked against the preset's budget |
| B3 | **Budgets checked per preset at 1080p on the reference PC:** High p95 ≤20 ms, p99 ≤33.3 ms, under 1% of frames over 50 ms, graphics memory ≤7 GB. Medium (the RTX 2060 stand-in, exit criterion 5) p95 ≤18 ms, graphics memory ≤6 GB. Low a smoke run at 30 FPS. Ultra measured, not checked |
| B4 | **"0 bytes of garbage per frame in steady state"** means under a byte per frame on average, no garbage collection and no recurring allocation (at most one frame in 10,000 allocating, such as a text's first draw). The release game checks it by heap growth and collections; the Development game reads Unity's per-frame counter and graphics memory, which release games don't record |
| B5 | **Instanced terrain is on by default** (the owner): it renders the same, and at High cuts draw calls by 43% and frame p95 by 0.4 ms. `-noinstancing` turns it off |

## Task 16, Phase 1 exit: decided 2026-10-05

**Context:** the Phase 1 exit gate. Review: [phase1-16-exit-review.md](phase1-16-exit-review.md); the Phase 2 plan:
[phase1-16-phase2-plan.md](phase1-16-phase2-plan.md).

| # | Decision |
|---|---|
| E1 | **Data seams on real data move to Phase 2** (task 12): the fallback blend is tested on synthetic data only, and no downloaded area yet mixes S1M with the 3DEP fallback. Exit criterion 3 is accepted on that basis |
| E2 | **Golden tests stay local-only.** CI fetches only the small LFS fixtures; a PR that changes cover or forest runs the full `dotnet test` with LFS checked out (no skips) and says so |
| E3 | **Phase 2 scope:** 0.6 §3 plus the ship-quality backlog (the Trailhead HUD build, light-theme text and solid panels, cache housekeeping, Auto tree detail, far impostors, forest-floor discs). NAIP imagery last and cut first. Seasons, next species, regional calibration, back-face tint and broadleaf height spread are later |
| E4 | **Estimates:** task-weeks per task, plus a calendar forecast at Phase 1's ratio padded for hardware, testers and reviews |
| E5 | **Milestone:** the merge commit of the exit PR is tagged `unity-m1`, and `archive/unity` is fast-forwarded to it |
| E6 | **Ultrawide support in Phase 2** (the owner's monitor): 21:9 and 32:9 layouts, HUD, resolution and vertical-FOV-preserving camera, and a 3440×1440 benchmark; 1080p stays the budget of record |

## Phase 2 task 08, formats frozen: decided 2026-10-06

**Context:** T11, the format freeze. Rules: [0.3 §5](phase0-0.3-technical-architecture.md#5-package-cache-and-library-t5-t11).

| # | Decision |
|---|---|
| V1 | **The library index is a version marker only:** `<data>/library.json` holds the layout version of the data folder, and the listing stays a scan of the packages, so it can never disagree with the disk. A folder without one is layout v1 (Phase 1). It is written when the first package is added |
| V2 | **An area made by a newer version of the game shows as a greyed row** in the library: its name and size, "Made by a newer version of Mountain Planner. Update the game to open it.", Open disabled and Delete allowed |

## Phase 2 task 05, settings: decided 2026-10-08

**Context:** S8 Settings (0.4 S8), the graphics option list (U3), ultrawide (E6) and Auto tree detail (F4).

| # | Decision |
|---|---|
| ST1 | **The graphics menu (U3):** quality preset (Low, Medium, High, Ultra, or Custom once any row changes), render scale 50–200%, anti-aliasing (Off, FXAA, SMAA, MSAA 2×/4×), shadows (Off to Ultra, the presets' shadow sets), shadow distance, terrain detail, terrain shading, tree detail (Auto or a fixed level) and textures. Forest density, forest draw distance, an Apply/Revert bar and a monitor choice are dropped: every row applies at once, and Restore defaults goes back |
| ST2 | **Display:** window mode, resolution (21:9 and 32:9 where the screen has them; any shape that fits as a window), V-Sync, a frame-rate cap and a field-of-view slider. A display change asks "Keep these settings?" and goes back by itself after 15 s |
| ST3 | **The FOV rule for every game camera:** the setting is the vertical angle (45–75°, default 60°); wider screens see more to the sides, never less above and below |
| ST4 | **Controls:** invert scroll zoom, zoom speed, and every key in the key map rebindable (two keys per action; a taken key swaps; Esc, Enter and the modifiers alone can't be bound) |
| ST5 | **Data:** the library folder (changed at the title, never during a download), disk use with Free space, and offline mode |
| ST6 | **Auto tree detail** times the first open on each graphics card and screen size: Ultra's LOD bias first, stepping down, keeping the first whose p95 is within 12 ms (60% of the budget) |
| ST7 | **Settings are not a frozen format:** they live in PlayerPrefs under `MountainPlanner.*`, beside the theme, scale and units. A -quality on the command line uses the preset alone for that run (benchmarks measure B1 exactly); -treedetail auto times Auto again |

## Phase 2 task 03.1, loading screen and brand: decided 2026-10-11

**Context:** the loading screen over every load, and the studio's and the game's marks (owner decisions 2026-10-08 to
2026-10-11; the marks are described in docs/brand/README.md).

| # | Decision |
|---|---|
| BR1 | **The brand is Twin Peaks:** two crisp peaks in the demo mountain's real twin proportions, the lower in front, every outer flank 2:1. The game's mark adds a winding trail and a straight lift bar; the studio's is the plain pair. A fresh-concept round (docs/plans/brand/brief.md) was judged too bland and set aside |
| BR2 | **The As are peaks:** in the game's name a cap-height peak at the mark's angle; in the Alpine Labs lockup a third peak rising from behind the pair, its summit level with the front one (below 32 px, the plain pair and an ordinary A) |
| BR3 | **One colour:** ink on light, white on dark, tinted from USS; no orange (planned work) and no rating colours in the marks. Lines are 1.0 (the trail 1.08), 1.25 from 64 px down; the 16 and 24 px icons are the plain pair |
| BR4 | **The loading screen** is the title's own picture with one box at the HUD panels' opacity: the game's lockup, "Opening …", the bar, a click-only Details list (remembered), and Alpine Labs emphasised. The title stays at 60° FOV so the picture matches; the download card, pill and dialogs draw above the box |
| BR5 | **The company name and the app icon change at the first release (task 10).** The ink masters in docs/brand/masters are ready for it; the wordmarks are outlined from Overpass (OFL) by tools/brand/export.py |
| BR6 | **On the title,** the name plate carries the game's logo and name; Alpine Labs is a plate of its own in the bottom-right corner (it does nothing), and the corner plates move like the trail signs when pointed at |

## Phase 2 task 10, build and release: decided 2026-10-12

**Context:** the one-command Windows release, the version, crash and error logs, and the zip that task 12's tester
build uses. Tooling in `tools/release/README.md`.

| # | Decision |
|---|---|
| RL1 | **The company is Alpine Labs.** Unity's companyName moves the player's prefs key and Player.log folder; on the first start under the new name every old value is copied once (App.Diagnostics.PrefsMigration, old key kept). The data root `%LOCALAPPDATA%\SkiAreaDesignChallenge` never moves |
| RL2 | **Plain `v0.x.y` tags** (no -alpha suffix): a build of a tagged, clean commit is that version; anything else reads `0.2.0+dev.7.abc1234` (`.dirty` with uncommitted changes). The first release tag is `v0.2.0` |
| RL3 | **The version** shows bottom left on the title and in Credits, heads Player.log and every error log, and names the zip |
| RL4 | **Error logs** go to `%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs\errors-<stamp>.log`, one per session with an error, the newest 10 kept, 5 MB each at most. The session's first error shows a notice once, with Open folder; Settings › Data › Logs opens the folder or copies the newest log |
| RL5 | **A release build needs the demo:** without the Jackson Hole 5 km package (the library's newest, or -DemoPackage) it fails; dev builds still only warn (P2-03) |
| RL6 | **A zip, no installer:** unzip and run, with README.txt and NOTICES.txt; not code-signed (Windows SmartScreen says "More info › Run anyway") |
| RL7 | **The C# compile gap in CI** is closed locally, for free: the release command compiles everything, and an opt-in pre-push hook (`git config core.hooksPath tools/git-hooks`) runs `tools/release/compile-check.ps1` when Unity C# changed. No Unity on GitHub runners (it would need the owner's Unity credentials as secrets and LFS bandwidth per run) |

## Phase 2 task 14, Phase 2 exit: decided 2026-10-16

**Context:** the Phase 2 exit gate. Review: [phase2-14-exit-review.md](phase2-14-exit-review.md); the Phase 3 plan:
[phase2-14-phase3-plan.md](phase2-14-phase3-plan.md). The tester build's own decisions (TB1–TB5) are in
[phase2-tester-findings.md](phase2-tester-findings.md).

| # | Decision |
|---|---|
| E7 | **Exit 1 (keyboard only, 50–150% scale) is met** by the keyboard PlayMode tests at 50, 100 and 150%, plus the picker's and photo mode's keys. Photo mode's arrow keys move the camera rather than the bar's focus; Tab reaches the bar (Phase 3 task 01) |
| E8 | **Exit 2 (real minimum-spec hardware) is waived for iteration 1:** the owner skipped task 11 (2026-10-12). Phase 1's stand-in stands (Medium p95 4.7 ms). An RTX 2060-class check is required before the first public release, not in Phase 3 |
| E9 | **Exit 4 is accepted as partly met** (TB5: the owner downloaded and explored on the laptop; no photo test, no no-help tester). Phase 3's tester build runs the full no-help session, photo included |
| E10 | **Perf baselines are re-pinned at the start of Phase 3** (task 01), at the benchmark's 10:30 time; perf PRs compare against them again |
| E11 | **The exe stays unsigned** until a public release is planned; testers Unblock the zip. Code signing costs money and is decided then |
| E12 | **The "Alpine Labs" trademark search** happens before the first public release, not in Phase 3 |
| E13 | **Phase 3 scope:** 0.6 §4 plus the Phase 2 carry-over; fixed-grip chairlifts only (the Sessellift FGQ-4 and Monta FG4 assets). Detachables, ponds, dams, snowmaking, buildings, seasons and new species are later. Estimates as E4 |
| E14 | **Milestone:** the merge commit of the exit PR is tagged `unity-m2`, and `archive/unity` is fast-forwarded to it |
| E15 | **Every task starts with a planning phase** (owner, 2026-10-16): the thread posts a 🟦 plan (scope, owned files, approach, tests and demo, questions with recommendations) and waits for the owner's go before implementing; a scope change mid-task means re-planning the same way |

## Phase 3 task 02, design rules: decided 2026-10-16

**Context:** the Phase 3 design rules gate. Rules: [p3-02-design-rules.md](p3-02-design-rules.md); plan and rounds of
rulings: [p3-02-plan.md](p3-02-plan.md). Rule IDs in brackets.

| # | Decision |
|---|---|
| DR1 | **Trail difficulty in percent:** the slope layer's bands (green under 25%, blue 25–40%, black 40–60%, double black from 60%), rated by (3 × mean + max) / 4 of the graded surface's fall-line slope, not 0.1's degrees (TD-01 to TD-03) |
| DR2 | **Imperial first** in every rule and readout, metric in brackets; the game stores metres (SH-01) |
| DR3 | **Grading:** a level bench, 1:1 (45°) cut and fill faces, no widen-to-fit, never outside the painted footprint; too-steep stretches stay natural and are reported (GR) |
| DR4 | **Brush** 25–400 ft (7.6–122 m), default 100 ft; minimum bench 25 ft (TR-01, GR-04) |
| DR5 | **No grading cost in Phase 3;** cut, fill and balance are tracked in yd³ (GR-08) |
| DR6 | **Rope speed 450 fpm, fixed;** a player setting comes later (LC-02) |
| DR7 | **Chair spacing** is a lift property, fixed and not editable for now; capacity follows from it (LC-03) |
| DR8 | **Lift flow:** the bottom terminal first (a ghost that anchors, then swings), the top terminal right after (fixing the line), then towers from the bottom up (LF-01 to LF-05) |
| DR9 | **Towers Automatic or By hand**, a switch in the lift menu: Automatic the first time, then the last choice; automatic towers modest (30–40 ft); Fill the rest and Re-place towers (LF-01, TW-12) |
| DR10 | **The rope:** catenary spans whose slope matches the tangent leaving each sheave; tension from the counterweight plus load × height; two load cases; the top tension within the rating (RP) |
| DR11 | **Head types from the rope's load:** support or breakover, hold-down, or combination; several breakovers or hold-downs close together where one can't take the bend (TW-04 to TW-06) |
| DR12 | **Clearance and heights:** chairs' bottom at least 15 ft above bare ground; towers 25–100 ft in 5 ft steps, moved with Page Up / Page Down (TW-01, TW-02, LF-09) |
| DR13 | **Siting:** towers at least 50 ft apart, not in lakes or on roads; terminals level within 10 ft and at least 300 ft apart; everything inside the core box (TW-03, LF-08, SH-06) |
| DR14 | **Placement aids:** a status bar for the tower in hand (with how far the next tower can go) and a lift profile strip above the status bar, with a True scale switch (TW-13, LF-10) |
| DR15 | **Towers plumb by default,** optionally leaned square to the rope (TW-14) |
| DR16 | **Readouts:** % of rating with lbf in the detail while building; a simple card once built (RP-06, TW-11, LF-11) |
| DR17 | **The network:** no Guest Entrance; lifts are the backbone, and a lift is connected when a descent from its top reaches a lift's bottom (NW-03) |
| DR18 | *Superseded by DR25:* paths as drawn 20 ft connectors |
| DR19 | **Lift numbers** are checked against real fixed-grip quads and tuned at the style tile: a 1⅝ in rope rated 42,800 lbf (a factor of safety of 5), an 18,000 lbf counterweight per side, 350 lb chairs with 170 lb riders, so about 900 ft of rise at 2,400 pph (LC-02, LC-05) |
| DR20 | **From the lift mockup review:** a red tower can't be placed; a placed one that turns red blocks Break ground; Delete removes the selected tower; Esc backs out one step; the commit words are Break ground and Save plan, with Cancel on the panel's ✕ (CF-06, CF-07, LF-04) |
| DR21 | **Review format:** Markdown documents, no web page |
| DR22 | **Trails are painted areas:** several brush widths, islands allowed, one connected piece; the head and tail are chosen in their own stage after painting; the centreline is automatic and as central as possible (TR-01 to TR-03, TF) |
| DR23 | **Built to grow:** every trail carries Grading (Graded or Natural), Kind (Run, Glade, Zone) and Clearing (Clear, Thin, None); Phase 3 builds runs. A cat track is a narrow graded run, not a kind; the grading envelope (GR-11) prepares for cat tracks through steep terrain (TR-06, GR-11, NW-11) |
| DR24 | **The rating estimate** updates only between strokes: (3 × mean + 90th percentile) / 4 of the footprint's natural slope; the final rating follows the head and tail (TD-06) |
| DR25 | **Paths are automatic:** where two trails come within 30 ft without touching, the network adds a path so skiers can cross; it changes no ground (PA-05 to PA-08) |
| DR26 | **Junctions come from geometry:** heads and tails snap to any lift's top or bottom or any point on another trail, and overlaps make junctions. A trail can span several network segments yet draws as one line; Split and Join tools rename and remap trails (NW-07 to NW-10) |
| DR27 | **Network checks and view:** the warnings "No way down", "No lift serves this" and "Leads nowhere", and an Analysis › Network view that highlights what each lift reaches (NW-12, NW-13) |

## Phase 3 task 03, design document: decided 2026-10-16

**Context:** the design document, commands and undo (T15). Plan: [p3-03-plan.md](p3-03-plan.md). The owner approved
the plan and all four recommendations.

| # | Decision |
|---|---|
| DD1 | **Undo:** 100 steps per session, cleared when a design is loaded; the undo history isn't saved |
| DD2 | **Split** stores a cut line: each part keeps the original strokes, clipped to its side. **Join** removes the cut, so a split then a join restores the trail exactly (NW-10) |
| DD3 | **Heads and tails are stored as points only;** what they attach to (a lift end or a trail within 30 ft) is derived each time the network is rebuilt (TR-05, SH-05) |
| DD4 | **Format:** Domain owns the document's shape (version 1) and its canonical bytes, used for goldens and equality; task 04 picks the save file's encoding and freezes it |

## Phase 3 task 04, save file: decided 2026-10-16

**Context:** the save file. Plan: [p3-04-plan.md](p3-04-plan.md). The owner approved the plan and all six
recommendations.

| # | Decision |
|---|---|
| SV1 | **Several named saves per area, plus one Autosave slot;** autosave never overwrites a named save |
| SV2 | **Autosave every 10 minutes** by default, only when something changed (Off, 5, 10, 15 or 30 in Settings › Gameplay); Exit and Quit ask "Save before leaving?" when there are unsaved changes |
| SV3 | **Task 05 defines the delta and clearing tile types** engine-free in `Domain/Edits`; the save writes them byte for byte |
| SV4 | **The save freezes as v1 only after task 05's tile types are on main** |
| SV5 | **The Load Area save list is mocked up first,** in its own section of ui-layout.html |
| SV6 | **Frozen JSON files are written with `File.Replace`,** so a crash never leaves a format without its file |

## Phase 3 task 05, terrain edit pipeline: decided 2026-10-16

**Context:** height and clearing edits over the immutable package. Plan: [p3-05-plan.md](p3-05-plan.md). The owner
approved the plan and all four recommendations.

| # | Decision |
|---|---|
| TE1 | **Delta tiles:** 64 m tiles on the cache's grid (16 × 16 per 1,024 m cache tile), 64 × 64 points at 1 m; heights as whole-millimetre changes (0 = untouched) plus one clearing bit per 1 m cell (GR-09, CL-05) |
| TE2 | **Undo:** each `DesignHistory` step holds its terrain edit set; undo and redo switch sets, and only the tiles that differ are rebuilt (CF-05, CL-07) |
| TE3 | **Preview:** the yellow pending contours only (CF-08); the ground changes at Break ground |
| TE4 | **Grading:** task 05 builds a straight test bench only, for timing and the demo; task 08 writes the GR engine to the same edit tiles |
| TE5 | **Design points** (task 03's `PointMm`) are whole millimetres **from the site's centre** on the package grid's axes (the game's local frame), not absolute EPSG:6350: absolute millimetres overflow an `int` (Jackson Hole's northing is about 2,500 km). Decided 2026-10-17 at PR B's review |

## Phase 3 task 07, style tile: decided 2026-10-17

**Context:** one graded, cleared trail and one Sessellift FGQ-4 on Jackson Hole 5 km, built by command, to settle the
look before tasks 08 and 09. Plan: [p3-07-plan.md](p3-07-plan.md). The owner answered the plan's twelve questions.

| # | Decision |
|---|---|
| SK1 | **Grader:** task 07 writes the GR-01 to GR-05 grader along a centreline in Domain; task 08 extends it to painted footprints. Graded or Natural stays the player's choice per trail (TR-06) |
| SK2 | **Rounded tie-ins** at the crest and toe of the 1:1 faces are allowed if the captures need them (a new rule, GR-12, inside the footprint) |
| SK3 | **Graded and groomed look data:** a third splat map on edited tiles only, built at runtime (bench, cut, fill); no cache bump, no format change. **Corduroy is tighter than 1 ft**: about 1 in (25 mm) ridges up close, with groomer-pass seams about 15 ft apart at distance |
| SK4 | **Summer:** `-season summer` turns snow off and sets a mid-July sun; the bench is mown grass, the faces raw earth |
| SK5 | **Edges:** CL-02's 10 ft ragged band and CL-03's 55 ft straight lift corridor are built in 07 |
| SK6 | **Rope:** drawn as a round cable along simple catenary spans (RP-03 tension); task 09 adds RP-02's tangent solve and the forces |
| SK7 | **Leaned towers are needed** for hold-downs and breakovers: the style tile's lift stands Leaned (TW-14, the lift-level setting in save v1). The tilted base plate goes to a Blender model thread, with owner approval before import. A per-tower stance would be a save v2 change for task 09, through the coordinator |
| SK8 | **LC-02 tuned:** rope to chair bottom 8.7 ft (2.66 m, measured on the model); Sessellift line gauge 13.5 ft (4.12 m); the cable drawn at 1⅝ in. The other LC-02 values and TW-14's 15° cap stand |
| SK9 | **#87 stand-ins:** 07 measures and records the terminals' real footprints; the sag and "hold-downs ahead" thresholds stay with task 09's tool |
| SK10 | **Site:** the thread picks it where the slope and forest benchmark views see it, and shows a marked overview first |
| SK11 | **Built work** draws as models and terrain; `DesignOverlay` keeps drawing plans only |
| SK12 | **Review:** a fly-by movie, screenshots, and the style tile live in the game (demo.bat 65) |
| SK13 | **TW-14 v2, lean where needed** (owner, 2026-10-17): on a Leaned lift a tower leans where the rope crosses its head at more than 10°, or where its head is a breakover, hold-down or combination; the rest stay plumb (derived, no save change). Leans come in 2.5° steps up to 15°, one base model each, and **the foundation pier leans with the mast, exactly in line with it** |
| SK14 | **Terminals** (owner, 2026-10-17): they need clear space round them for loading and unloading: CL-09, the model's footprint plus 20 ft all round and 50 ft more along the line in front *(tuned at 07)*, built in 07. **Terminal elevation adjustable like towers** and **ramps for top terminals** that blend into the terrain are wanted; the elevation is authored data, so it's a save v2 change for task 09 through the coordinator, and the ramps come with it (a graded pad from the same grader) |
| SK15 | **TW-16, terminals are heads** (owner, 2026-10-17): the rope runs level through each terminal; the bottom terminal's hold-down row turns it up to leave at no more than 15.9°, with a breakover tower soon after; the top terminal's support row brings it level to the bullwheel from at most 18°, with at least one breakover before it. Station breakovers go in first, about 70 ft from each bullwheel *(tuned at 07)* |
| SK16 | **Round 2 scope** (owner, 2026-10-17, after the in-game review, `p3-07-findings.md`): all twelve findings are fixed in 07, including the rope-over-sheaves posing first planned for 09 (trains rocking level by level to the rope, the terminals' rows, the chair grip following the rope); 07 runs about a week longer and 09 shrinks by the same. Models are built in a separate model thread, approved from photos before import |
| SK17 | **Rope 1¾ in** (LC-02 v2): the top of the real fixed-grip range (1⅜–1¾ in); weight, breaking strength and rated tension re-read from the same rope table, and RP-05's rise re-derived |
| SK18 | **Station towers** (TW-02 v2, TW-03 v2): in the exempt load/unload zone a tower may carry the rope from **15 ft (4.6 m)**, and may stand from 15 ft past the terminal's front; the default is about 40 ft from the bullwheel. How close becomes a player setting later (shorter against wind) |
| SK19 | **Terminal pads** (LF-12): every terminal is built on a level pad (its footprint plus 20 ft; the top's runs to 60 ft behind the bullwheel for the unload zone) with 1:1 faces; only the plinth shows. **The raise stays as a last resort**: pad, then hold-down towers (TW-16 v2), then a raise filled up to by the pad |
| SK20 | **Trail surface:** dense corduroy ridges (about 1 in) with faint groomer lanes; the crest by wider GR-12 rounding (chosen from captures, starting at the bench edge) plus smoothed shading |
| SK21 | **Fixes are general** (owner, 2026-10-18, round 3): every fix lives in the rules and engine, so anything a player draws gets it with no tweaks; the style tile's frozen towers, 200 ft pad envelope and rounding flag are removed. **Terminal ends** (owner, through the coordinator): a lift's terminals face where its bullwheels are (the Monta's drive is its bottom, task 09); each end's row, angles and footprint are read from the model at that end (TW-16 v3, LC-01) |
| SK22 | **TW-16 v3** (findings r3 #1, #3): the return's hold-down row aimed at 14.3° (90% of 15.9°); the drive's support row gets a **level last span**, never pulling up; remedies in order: station tower position, line towers, the trench (bottom), the raised unload ramp (top, owner Q6), the bottom raise last |
| SK23 | **TW-01 v2 and TW-08 v2** (finding r3 #4): clearance under each rope across the chair; the low zones from each pad's edge climb along a straight line to 15 ft at **100 ft** from the bullwheel (owner Q5), replacing the exemption that swallowed a whole span |
| SK24 | **GR-11 v2, the lift-line envelope** (finding r3 #3): a trench under both chair paths plus 5 ft each side (about 30 ft; 24 ft planned, widened so the ground under the chairs' edges is really cut), level, cut only, 1:1, at most 10 ft, clear of TF-11's 15 ft round line towers; CL-09 v3 clears it; TF-11 v2 exempts the lift's own earthwork |
| SK25 | **LF-12 v2** (finding r3 #2, owner Q2): pads are the measured footprint plus 5 ft, no unload extension, faces within 50 ft |
| SK26 | **TW-02 v3 / TW-03 v3** (owner Q1): station-tower steel in 5 ft steps, the foundation shows the rest (derived, no save change); the bottom station tower stands where the chairs have climbed to 15 ft, the top one at the closest 15 ft slot |
| SK27 | **RP-08** (owner Q3): the rope is drawn at the load it carries (empty sag); checks keep both load cases. Tension stays 18,000 lbf |
| SK28 | **Real-ground check** (round 3): a fan of 32 Jackson Hole 2 km lines between LF-08 terminal sites; 23 build within every rule, the rest red for TW-06 crests on cliffy ground or small shortfalls just past a low zone (task 09's full TW-12 placement) |
| SK29 | **Round 4 split** (owner, 2026-10-18): TW-05's full per-sheave forces in both load cases (the 750 lbf group minimum on hold-downs), any span rule, and the support on long steep spans and crests (findings r4 #1, #2b, #3b) go to **task 09** with its load gauges; round 3's small shortfalls past a low zone too |
| SK30 | **Steep terminal sites** (owner, round 4): allowed; the terminal is raised (cutting at most 10 ft into the hill) and stands on its pier with **nothing filled up to it**: pads are cut-only clearance envelopes (LF-08 v2, LF-12 v3); an inlaid unload ramp model comes later. Supersedes SK22's filled unload ramp. Raised levels go to the view in memory until save v2 (SK14) |

## Phase 3 task 08, trails: decided 2026-10-19

**Context:** painting, the automatic centreline, grading, clearing, the profile, Edit and Remove, the Trails map layer
and Analysis › Trails. Plan: [p3-08-plan.md](p3-08-plan.md). The owner answered "Ok to all" to the plan's twelve
questions, and added TL13.

| # | Decision |
|---|---|
| TL1 | **Centreline (TR-03):** a cost path from head to tail whose cost rises near any edge or island, then smoothed inside the footprint and cut into 25 ft stations |
| TL2 | **GR-03 v2 and GR-13, the edge envelope:** each side's bench budget runs to the painted edge along the section; every cell then stays between 1:1 slopes rising from the natural ground at the footprint's boundary (edges, islands, earlier trails, TF-11's rounds). The style tile's trail moves to the same engine and its golden heights re-pin |
| TL3 | **Edit and delete terrain:** the touched tiles are rebuilt by replaying the built work that reaches them, in order: trails by ID, then lifts' earthwork through a hook task 09 fills |
| TL4 | **Overlaps (TF-10):** the earlier trail keeps its grading; trees are cleared under both |
| TL5 | **TF-11:** task 08 keeps 15 ft round tower bases and terminals and puts no fill in a lift's envelope; the chair-clearance block uses task 09's clearance check when it lands |
| TL6 | **Trails map layer:** trail-map lines in the rating's colour, name centred, symbol at the head, plans orange; on by default; Shift+4. The graded ground stays visible when the layer is off |
| TL7 | **Profile:** the TF-04 strip shows in New and Edit trail; the focus card stays simple (TF-08) |
| TL8 | **Analysis › Trails columns:** Trail, Length, Vertical, Grading, Status (Built or Planned); surface and open or closed wait for the simulation |
| TL9 | **Shared table:** task 08 writes a generic `AnalysisList` (table, filters, focus-card shell) that task 09 reuses for Lifts; the first to merge lands it |
| TL10 | **Fixtures:** four crops of the Jackson Hole 2 km package (sidehill, ridge, gully, too-steep face) with golden heights, plus analytic surfaces |
| TL11 | **Mockup first:** the Edit trail panel follows TF-07, Remove confirms, the review's terrain rows, the live Trails layer row and the Grading column; screenshots in both themes |
| TL12 | **One PR**, with the mockup approval as a checkpoint in the thread |
| TL13 | **Grading only when the player chooses it** (owner): a new trail starts **Natural**; it is graded only when the player switches it to Graded. Natural trails clear trees and change no ground (TF-06) |
| TL14 | **A terrain modification tool** in the style of Cities: Skylines 2 (owner): raise, lower, level, smooth and slope brushes for grading cat tracks, junctions and the ground round lifts by hand. It becomes **its own Phase 3 task after 08 and 09**, planned first; its strokes are authored data (a save change the coordinator sequences). Task 08 builds TL3's replay so hand edits can slot in |
| TL15 | **TD-01 v2, the rating reads the bench** (owner, after the first Jackson Hole pictures): a benched station's median slope is taken over its bench cells, the running surface; ungraded stations and Natural trails read the whole section inside the footprint. The 1:1 faces no longer make a graded trail rate harder than its natural ground |
| TL16 | **Mockup approved** (owner, 2026-10-19): Edit trail follows TF-07 (rating read from the ground; Natural or Graded; Repaint, Move ends, Rename; Split and Join greyed until task 10), Remove trail restores the ground and trees, Analysis › Trails shows Grading and Built or Planned |
| TL17 | **GR-05 v2, bench widths follow their neighbours** (owner, 2026-10-19, after the built trail's faces banded on rough lidar): the narrowest width within 50 ft, then averaged over 50 ft, never wider than GR-03. The owner restated GR-14 (TL13): grading is only ever an option, never always on |
| TL18 | **The edit budgets hold on a 1 km run** (owner, 2026-10-19): preview ≤100 ms and commit ≤250 ms at p95 on a 1 km (3,280 ft) trail, not only CF-09's 1,000 ft; task 08 speeds up the edit pipeline's commit to get there |
| TL19 | **Map controls work at all times** (owner, 2026-10-19): the camera keys (W A S D, Q E, R F, C, Home, the arrows, Page Up / Down, + / −) keep working while the Toolbox is open and a tool is armed or editing, replacing P3-06's "the tray owns the letters". Edit moves from E (rotate right) to **M**; tool letters keep clear of the camera keys; the brush and tower keys take [ ] and Page Up / Down only while the tool uses them. A focused text field (a trail's name) still has the keys |
| TL20 | **Typing owns the keys; ends join runs midway; paint like a brush** (owner, 2026-10-19): while any text field has the focus no key binding fires (camera, tools, shortcuts), a click keeps a field focused and a click on the map lets go; a head or tail snaps to any point on another trail's centreline within 30 ft (TR-05 as written; the line snap had never been wired); painting fills the ground as the brush moves (the mockup's footprint canvas, TF-02): the trail orange, a patch apart red, no edge lines |
| TL21 | **Grading that skis** (owner, 2026-10-19, reports of narrow benches, steps and terraces): GR-01 v2, the bench keeps up to 10% side slope (faces stay inside the paint, GR-06 unchanged); GR-05 v3, smooth sections through bends and a ramp into too-steep stretches; rock and cliff shells hide where an edit moves the ground over 10 cm; the four grading fixtures re-recorded (approved). The style tile's strip grader keeps v1 |
| TL22 | **Bends and shading** (owner, 2026-10-20, circled bends in the review pictures; "steppy, like a rendering issue"): inside a bend tighter than the trail is wide, the nearest two sections blend over 8 m (GR-05 v3), each point's face meets the paint edge along its own section, not the nearest edge in any direction; the terrain shader smooths the shading over 4 m on the whole built trail (the heightmap's 4.4 cm height steps showed as bands); captures can open a save (-opensave) and regrade it with the current build (-regrade, display only) |
| TL23 | **Smoother graded trails** (owner, 2026-10-20, "can we get even better?"): measured on the owner's Runs 2-4, side slope widens the bench and saves earth but doesn't smooth, and the long profile does; so GR-01 v3, the bench's profile averaged over 75 ft each way (rolls 8-10% RMS down to 2-3.5%), and GR-01 v2's side-slope cap raised to 20% (bench 6-9 points wider, less cut). Fixtures re-recorded |
| TL24 | **The adversarial smoothness review** (owner, 2026-10-20: "make sure the final slope is as smooth looking as possible"): an independent read-only reviewer and a measured kink audit (TrailPictures.SmoothnessAudit) found small cliffs (GR-13 falling back to natural), seams out of each end, a level run-on and bent profile at short last stretches, washboard on marginal sidehills, unrounded clamp creases, shading stripes at tile edges and paint outlines on natural trails. Fixed: GR-13 clamps between crossing slopes; ends measured square with the end grade carried on; a local-fit profile; benched runs of 3+ stations with an eased ramp; GR-12 v3 finish; narrow-bench and tilted-edge rounding; the shader smooths graded trails only, by the true sample span; the trail map takes the most any trail covers. Sharp creases (over 0.5 m/m²) fell from 54-1,212 to 0-8 per trail, except on 70%+ ground. Open: the trail map stops at 2 km on larger sites; islands are found by station rays; terrain LOD |
| TL25 | **The brush** (owner, 2026-10-20: "laggy, not very elegant"; plan approved): measured with a paint bench, frames were never slow (4.5 ms p95 dragging) but paint landed every half radius, up to 25 ft behind the cursor. Now strokes keep a point every quarter brush (1 m at least) and paint is stamped every frame with a tail to the pointer; the paint uses the lattice's own cells and the footprint's capsules, anti-aliased, and letting go replays the same strokes (no pop; footprint shown 33 ms p95 after); the map grows over what's painted at 1 m on any site; a crisp edge line and a brush cursor drawn on the ground; three styles captured; **the owner chose B** (a blue fill with a blue edge; a patch apart red) |
| TL26 | **Paint and run-line findings** (owner, 2026-10-20; [p3-08-findings-paint.md](p3-08-findings-paint.md); plan approved). (1) The paint goes up through a raw buffer and a compute kernel. The old shared staging texture put blocks of paint in the wrong places once the player's render thread was involved. A `-force-gfx-mt` EditMode check runs in `release.ps1`, and the player check is `-paintcheck`. (2) A footprint result arriving mid-stroke no longer redraws: the stroke's own let-go does. (3) Every preview line is sized by each stretch's own distance, not its middle's. (4) **Run lines fade out near the camera**, gone within 40 m and fully drawn from 150 m (the owner chose ~100 m and run lines only; lift and tool lines don't fade). (5) Map names hide behind the ground: the contour labels' sight test, shared |

## Phase 3 task 09, lifts: decided 2026-10-19

**Context:** fixed-grip chairlifts placed, towered, roped and edited by the player. Plan: [p3-09-plan.md](p3-09-plan.md).
The owner answered revision 1's eight questions, then asked for an adversarial review before the go.

| # | Decision |
|---|---|
| LT1 | **Towers:** both automatic placement by the game and fully individual placement (By hand) from day 1; the mode is per lift |
| LT2 | **Editing a built lift** = moving towers (along the line, height) and moving terminals |
| LT3 | **Save v2** stores each lift's absolute terminal levels (whole mm); the design golden is re-pinned |
| LT4 | **The mockup's stand-ins are dropped:** footprints measured on the models, TW-13's real "next tower within" reach, "Hold-downs ahead" from the real forces |
| LT5 | **Physics picks the heads:** b8-heavy lines are fine; head counts are shown on two sites |
| LT6 | **The Monta FG4's top return** is a fixed row, its angle range measured from its single deflection sheaves; a Blender rebuild only if captures show the rope leaving them |
| LT7 | **The Lifts map layer** is Shift+2 (Trails Shift+4, P3-08) |
| LT8 | **Two PRs:** A the engine and save v2, B the game |
| LT9 | **Moving a built lift's terminals** (owner "Proceed" on revision 2): either end moves anywhere, re-aiming the line; Automatic re-places the towers; By hand keeps each tower's distance from the bottom terminal and drops those past the new top after asking; the lift's earthwork rebuilds (LF-05 v2) |
| LT10 | **Lift lines on the map:** as the mockup (the Lifts layer, every zoom, names on the Labels layer), but the red line fades out once the camera is close enough to see the 3D lift (about 2,000 ft) |
| LT11 | **Tensioning:** the counterweight stays at the bottom terminal on both lifts (RP-03 as written); the Monta's bottom station drives and tensions |
| LT12 | **Line gauge** (owner, 2026-10-19, after seeing the Monta check lift): the Monta FG4 runs at the Sessellift's 13.5 ft (4.12 m) gauge, its bullwheels and sheaves moved in, so both lifts share the tower heads; widened Monta tower heads were dropped |
| LT13 | **Rise limit, not tension** (owner, mockup rounds, PR #92): RP-05's rope-tension limit no longer blocks a lift. Each type has a rise limit: fixed-grip double 2,800 ft, triple 2,500, quad 2,200; detachable quad 3,600, six 3,300, eight 3,000; gondola 4,500; tram none. The rope is assumed sized to suit; tension (counterweight + load × rise, Q12) still shapes the sag and the head loads. The strip's tension bar becomes a rise bar |
| LT14 | **Lenient placement:** only chair clearance (TW-01), tower spacing (TW-03), unfinished gaps and the rise limit block a lift. Head loads (TW-05/06), the rope's angle into a station (TW-16) and terminal ground varying over 40 ft are amber warnings; over-loaded heads are one warning line. The engine keeps its remedies (TW-06 partners, the trench, terminal raises; Q13) but never turns red for a warning |
| LT15 | **Lift types:** a Type (Fixed-grip chair, Detachable chair, Detachable gondola, Aerial tram), its carrier (Double/Triple/Quad; Quad/Six/Eight; 6/8/10 a cabin; 60/100 a cabin) and a Model on every type (the fixed quad: Sessellift FGQ-4 or Monta FG4; others a maker). Placeholder numbers from the mockup's LIFT_TYPES; every type draws with the quad's models (no model changes); trams stand on towers up to 250 ft. Catalog ids (Q14): `sessellift-fgq4`, `monta-fg4`, and `<carrier>:<maker>` for the rest (`fixed-double`, `fixed-triple`, `detach-quad`, `detach-six`, `detach-eight`, `gondola-6`, `gondola-8`, `gondola-10`, `tram-60`, `tram-100`; makers `sessellift`, `monta`, `chairworks`); no save change |
| LT16 | **Every lift-menu choice is a dropdown;** Towers is one quiet line (Automatic ▾ · Plumb ▾) |
| LT17 | **Keys after TL19:** `,` and `.` slide a tower 5 ft (Shift 50 ft); Page Up / Page Down change a tower's height or a terminal's level only while one is claimed (selected or in hand); otherwise the camera has them. Toolbox Edit is M |
| LT18 | **The changes amend PR #91** (Q15). The mockup's 400 ft cap on a station's exempt zone is its own engine's fix: ours already ends the low zone 100 ft from the bullwheel (TW-01 v2). Ride times round to the second |
| LT19 | **Placing a terminal like a plopped building** (owner, 2026-10-19): the terminal's own model follows the pointer while it's placed (bottom and top), and Page Up / Page Down raise or lower its level (SK14's stored level) before the click, as in Cities: Skylines |
| LT20 | **Turning a terminal is a preview** (owner, 2026-10-20): turning the bottom terminal before the first click only shows the space it will take; once the top terminal is placed the bottom faces along the line (LF-02/LF-03). The top terminal's ghost always faces back along the line |
| LT21 | **Turn input:** Shift + mouse wheel, with `,` and `.` for keyboard use; the wheel alone still zooms |
| LT22 | **The terminal being placed** is its real model, see-through, tinted green (it can stand), amber (it will stand on its pier) or red (it can't) |
| LT23 | **Hovering a built lift** (any terminal, tower, rope or chair) lights the whole lift: a white casing round its silhouette with a soft halo (UI-15's white). Built lifts also show as red trail-map lines when zoomed out, fading to hidden close up (LT10) |
| LT24 | **Straight map lines** (owner, 2026-10-20): far out, a built lift's map line is one straight stroke from bullwheel to bullwheel, as a ski map draws it, drawn over the ground so a ridge between the ends never cuts it; tower ticks sit on the stroke. **Remove lift** (task 09): the first click selects the lit lift, a second click or Enter removes it with its pads, trench and clearing in one undoable step; its ground comes back by replaying every other built lift's stored earthwork in order (`LiftReplay`, search-free, the builds' own bytes). Lifts are the only earthwork on main; P3-08's TL3 replay takes this over when trails grade |
| LT25 | **Towers by hand** (LF-04): the tower in hand follows the pointer up the line from the last tower (`,` `.` slide it, LT17), Page Up / Page Down its height; click or Enter places it where it's green; Delete takes the last one back; Enter with the line finished goes to the review. **Fill the rest** takes the automatic lift's towers past the last placed one where they check out, the furthest green place otherwise (`TowerFill`, deterministic, a named reason when it can't finish). The span after the bottom station tower may be trenched up to 15 ft before the next tower in the band and the full check, as TW-13's reach already allowed. On the map, placed towers are lift-red dots and the tower in hand green or red; a 3D tower ghost is a possible follow-up |
| LT26 | **Edit lift** (LT2) steps through its parts with `[` and `]` (bottom terminal, towers, top terminal), not the mockup's Tab, which gives the tool window the keyboard (P3-06 Q7). It edits built lifts; the built lift keeps its 3D shape until Apply (the draft draws on the map, the lift stays lit). Its Cancel is the window's second button |
| LT27 | **The type menu** is shared by New and Edit lift (`LiftTypeMenu`): Type, Chair (Cabin for gondolas and trams), Model, each a dropdown over the window (`ToolDropdown`); Esc closes an open dropdown before anything else |
| LT28 | **Cabin sizes read "8 person"** (owner, 2026-10-20), not "8 a cabin": the Cabin menu (6, 8, 10 person; 60, 100 person) and the type names ("Gondola, 8 person"), in the game and the mockup |
| LT29 | **The Burst chair job waits** (task 09, measured first, 0.3 §8.1): with 40 built quads (3,898 chairs, `-liftbench 40`) High's frame p95 is 7.80 ms against 7.42 ms with none (GPU-bound), and posing and drawing every chair costs 0.44 ms a frame on the main thread with 0 B garbage. Each lift keeps posing its own chairs (LOD per chair, instanced); the job comes back if the simulation's CPU load makes that 0.44 ms matter. Two of the 40 automatic lines came out short of TW-01 (Bench 20, 38): a follow-up for TW-12 |
| LT30 | **Lifts join the design's earthwork fold** (TL3, TL24; after P3-08 merged, #93): `LiftEarthwork` makes every built lift a piece laid after every trail, in creation order, its writes what `LiftBuild.Earthwork` changes on the ground the fold hands it. A trail graded under a lift is cut again by the lift's pads and trench; New, Edit and Remove lift make their edits with the fold (command applied, earthwork rebuilt, accepted once in), as the trail tools do, so removing or editing a lift keeps every trail's grading. `LiftReplay` (LT24) stays only as the fallback where there's no fold (tool tests) |
| LT31 | **Analysis › Lifts** on P3-08's `AnalysisList` (the mockup's columns: Lift, Model, Length, Vertical, An hour, Wait as "—" until the simulation, Status), a row or card lighting its lift, the card's Edit and Remove; **lift names** over the middle of each line on the Labels layer. Main's Trails row in the layers list was live but drawn greyed; it now has its tick and Shift 4 |
| LT32 | **No station tower after a terminal with its own station** (owner, 2026-10-20, M1; TW-16 v4): a terminal built with its hold-down tower in it (the Monta's drive, `TerminalEnd.OwnStation`) gets no station tower; the first line tower keeps the rope's exit within the row's range, automatically and by hand, and stands tall enough for the chairs to clear beside it. The Sessellift's return also holds the rope down but has no tower of its own, so it keeps its station tower |
| LT33 | **Every train rocks to the rope** (M2): the view poses a part without pivots as a fixed point at its rope exit instead of switching posing off for the line, and logs it, so the Monta drive's hold-down trains (pivoted, 6–16°) rock to the rope. *Revised (owner, 2026-10-20, model session):* the Monta return's deflection row stays fixed (LT6), no rockers; "rotate like the SSL" meant the drive's trains |
| LT34 | **A terminal stands where it's placed** (M3, LF-12 v4): at the ground at its origin plus the level set with Page Up / Page Down; the build never raises it (LF-08 v2's steep raise and the last-resort raises go). Where a line needs a different level, the review says so with the amount ("raise it 6 ft (Page Up)") |
| LT35 | **Pads flatten** (M3, LF-12 v4): a terminal's pad is graded to its level across its footprint, cut and fill, with 1:1 faces outside it; both makes, both ends. The lift earthwork goldens and the Sugarloaf fixture's pads are re-pinned for it (owner OK) |
| LT36 | **The Monta's foundations run 15 m down** (M4): every Monta pier and foundation carries to 15 m below the terminal's 0.00 level with a `pier_top` socket, as the Sessellift's; the game places a footing at grade under each. The Monta's top-return angle (0.4°, flagged) is looked at after these fixes |
| LT37 | **One tower editor in both lift tools** (F1–F3): towers and terminals are selected by pointing at them in 3D (or `[` `]`); Page Up / Page Down change a selected tower 5 ft; a click on the line where there's no tower, Add tower or Insert adds one; **Edit towers** replaces Re-place towers and **Revert to auto** places them all automatically again (asking first over towers set by hand) |
| LT38 | **The line in 3D while it's built and edited** (F4): the tower in hand is a see-through model on the line (green or red, the head the game picks, TW-04: the player doesn't choose heads); placed towers and terminals are real models with the rope posed through them, redrawn after each change; Edit lift shows its draft in place of the built lift (replacing LT26's last sentence) |
| LT39 | **Naming and the properties panel** (F5, F6): the name in New and Edit lift's window title is clicked (or F2) to rename; clicking a lift on the map with no tool armed opens its panel (the Analysis card, with the figures, Edit and Remove), whose name renames it in one undoable step |
| LT40 | **Automatic placement says why it leaves the chairs short** (TW-12, follow-up to LT29; owner go 2026-10-20): where the towers can't clear a stretch (towers at TW-02's 100 ft, a crest too sharp with no room for a partner) the search leaves that stretch and finishes the rest of the line, instead of stopping there. Any stretch still short of TW-01 v2 that a level hint (LT34) doesn't put right gets a red **TW-12** reason that names the place and the fix: the ground under the chairs standing above the line (a rock beside it: move the line), the ground before or past a terminal standing above the steepest line its row takes (a terminal below a crest, TW-16: move the terminal), or the towers' limit. Verified on `-liftbench 40`'s two short lines (Bench 20: a rock under the uphill chairs 100 ft above the line; Bench 38: a top terminal 120 ft below the crest in front of it), recorded as `TestData/lift-profiles/jackson-bench.bin`; Sugarloaf and the 32-line fan are unchanged |
| LT41 | **Detachable lifts, the Sessellift D6 first** (owner, 2026-10-23; [plan](p3-09-detachables-plan.md)): a detachable type stores its loading **interval** (D6 7.2 s, 3,000 an hour), so line spacing = line speed × interval (D6 120 ft, about 2.7× a fixed grip; detachable quad 100 ft, eight 135 ft); carriers slow into the stations on the rail at fractions of line speed (boarding 1.0 m/s, unloading 1.4 m/s at 1,000 fpm, within 0.8–1.2 and 1.1–1.6 m/s; 1.0 m/s² ramps), so a later line-speed slider scales loading speed and spacing by itself; heads are chosen from the type's own set (the D6 adds S10/B10/D10); every other type stays in the menu but can't be built (FGQ-4, Monta FG4 and D6 only); lifts take a primary and an accent colour, type defaults now (D6 black and white), a picker later; swing (G5) checked per type in the asset tests; gondola models out of scope, the code reusable for them; procedural names on the lifts are a future feature |
| LT42 | **A detachable terminal's pad ends at the hood's middle** (owner, 2026-10-23, second review E1; LF-12 v5): the front half of a detachable terminal (its nose, bullwheel and station rail toward the line) stands over natural ground, which the pad leaves alone; the middle is measured from each terminal's own model. Both D6 ends; the quads keep LF-12 v2 |
| LT43 | **Detachables are level at both ends** (owner, 2026-10-23, second review E2 and E4; TW-16 v5): the rope runs level between each detachable terminal and its station tower, at the terminal's rope height, and the turn to the line is shared by the station tower and the towers after it, never one head past what it carries. Detachables only: the FGQ-4's return keeps its 14.3° exit and the Monta's drive its own station (TW-16 v3/v4) |
| LT44 | **The player chooses a hand-placed tower's kind** (owner, 2026-10-23, second review E6; TW-04 v3; replaces LT5's "physics picks the heads" and LT38's "the player doesn't choose heads" for towers set by hand): Auto (the game's choice, as before), Support, Hold-down or Combination; the game sizes the sheaves from the bend and load, and a kind that can't carry its bend is red with its reason, never changed. Automatic placement stays Auto; Edit towers can set one tower. Stored with the tower (save v3) |
| LT45 | **Taller towers before more of them** (owner, 2026-10-23, second review E3: "the game may be arbitrarily restricting tower height"; TW-12 v2, TW-06 v2): TW-02's 100 ft stays the line-tower maximum, and placement uses height up to it before adding towers. Its soft caps (60 ft, a crest's neighbours by 20 ft, partners always 50 ft away) are gone; the line between the station towers is planned together as the fewest towers whose heads all carry their bends, with heights near a modest 35 ft |

## Phase 3 task 14, problem reports and Send feedback: decided 2026-10-25

**Context:** the owner asked "How will I debug user issues in the future?" and "We should also just provide a button
for users to send logs", then widened it to suggestions and to tracking issues. Friends get the game from a GitHub
Release.

| # | Decision |
|---|---|
| PR1 | **One Send feedback dialog,** Problem or Suggestion. The error notice and Settings › Data › Logs open it on Problem; the in-game menu opens it on Suggestion. No hotkey (bindable later) |
| PR2 | **Sending:** the game saves the zip in `<data>/Reports`, shows it in Explorer and opens a pre-filled GitHub issue form (kind, title, version and GPU only in the link); the player drags the zip into the issue. Nothing is uploaded by the game and no key or token is in the build. Friends without GitHub send the file directly |
| PR3 | **No email address and no mail-to-issue script** (owner, 2026-10-25). Uploading from the game (a free relay) stays a later decision |
| PR4 | **GitHub Issues track problems and suggestions** (labels `problem`, `suggestion`, `crash`, area and status labels, a milestone per release, a free project board). Discussions stays off; **the wiki is on hold** |
| PR5 | **The report:** `report.json` ("report" v1, not frozen), the error logs, Player.log and Player-prev.log (last 4 MB each), Unity's newest crash dump, the open save (read only; included by default, with a toggle), a screenshot (on by default), system info and every setting, the player's note. The package itself is never included, only its ID and attribution. The zip stays under GitHub's 25 MB |
| PR6 | **Privacy:** nothing is sent automatically; the dialog lists every file and its size first; the Windows user name in any path or log becomes `%USERNAME%`; no computer name |
| PR7 | **After a crash** the title offers a report once, at the next launch (a running marker the clean quit removes); never in tool runs or scratch `-data` folders; Settings › Data can turn it off |
| PR8 | **Symbols:** the build is Mono; `release.ps1` keeps each tagged release's game DLLs and PDBs in `D:\skiAreaDesignChallenge\symbols\<version>`, outside git and never shipped. Native frames use Unity's public symbol server |
| PR9 | **Opening a report:** `tools/reports/open-report.ps1` (demo.bat 71o; a zip or `-Issue N`) unpacks into a scratch `-data` folder, copies the area in from the owner's library (or names the package to download), opens the save, prints the system info and starts the game with `-reportsettings`: the reported settings for that session only, under their own prefix, never over the owner's |
| PR10 | **The old backlog stays in the docs;** new findings go to Issues from v0.3.0 |

## Phase 3 task 10, paths, junctions and the network: decided 2026-10-25

**Context:** automatic paths, derived junctions, Split and Join, the derived ski network with its connectivity check and
warnings, the status bar counts, Analysis › Network and map label collisions. Plan: [p3-10-plan.md](p3-10-plan.md).
The owner said "Go. Yes to all nine recommendations."

| # | Decision |
|---|---|
| NT1 | **Split and Join are buttons in the Edit trail window** (as the mockup), not Toolbox tiles; the tray's letters stay free |
| NT2 | **A split pair is graded as one trail,** so Split never changes the ground (NW-10); TrailEarthwork grades the two sides of one cut as one centreline |
| NT3 | **Joining two separately painted Graded trails is allowed:** the seam regrades (within about ±75 ft, GR-01 v3) and undo restores it. Natural trails and a split pair's join change no ground |
| NT4 | **Analysis › Network is its own tab,** after Trails |
| NT5 | **The status bar shows built lift and trail counts now;** a count turns amber when anything it counts has an NW-12 warning, its tooltip says what, and a click opens Analysis › Network. Guests stay greyed until Phase 4 |
| NT6 | **"Leads nowhere" is literal:** a trail whose tail joins no lift bottom, no other trail and no path. "No way down" on the lift covers longer dead ends |
| NT7 | **Label collisions:** lift names win over trail names; a trail name slides along its line, then hides. Contour labels are unchanged |
| NT8 | **Timing:** the engine-free work (geometry, connectivity, tests, the network golden) and the mockup start now; the game UI, lift nodes in the game and labels wait for #94 (labels also #97), merging main as each lands |
| NT9 | **Segment ratings** (NW-08) are computed and tested but not shown; the player sees one rating per trail |
| NT10 | **A trail's reach stops at the lifts it leads to** (owner, 2026-10-26, finding 5): a lift whose bottom the descent reaches lights as reached, but the highlight doesn't ride it |
| NT11 | **Clearing a pick** (finding 6): Esc, a click on empty ground, or the card's ✕ |
| NT12 | **The card and the legend share one right-hand column** (finding 3): the legend on top, folded to its title line while a card is open (▸ unfolds it), the card below |
| NT13 | **Stations** (owner, 2026-10-26, from Kearsarge: Chair 5 built beside a run's middle, Chair 1 and 2 bottoms 157 ft apart): every terminal has a station reaching 100 ft; overlapping stations merge; a trail joins a station when its end or footprint comes within the reach (NW-14, NW-02/03/07 v2) |
| NT14 | **A transfer counts:** a lift whose top sits in a station with another lift's bottom has a way down |
| NT15 | **A station looks like an area ring** in the Network view: a soft circle the size of its reach, the terminal circles inside; mockup first |
| NT17 | **One rounded shape per hub** (owner, 2026-10-26): a station is drawn as one smooth outline round all its terminals, 100 ft out from them; what's drawn is exactly what catches trails (NW-14 v2) |
| NT18 | **A hub grows by taking in more**: the reach stays 100 ft; shapes that touch merge, again until none do; buildings will join later |
| NT19 | **Areas** (owner: "Hubs should be able to be named once they have a certain amount of items in them, right now more than one lift … Base areas should act as a larger node that allows all of them to interact"): a hub with 2+ lifts is an Area with a default name, listed in Analysis › Network, with a card (renamable) and a map label; names saved (save v5, after P3-15's v4: Master Planner, 2026-10-26; NW-15) |
| NT20 | **Placing a lift shows the hub growing**: the hub the terminal would join is redrawn live as it would be |
| NT22 | **Automatic paths off for now** (owner, 2026-10-26: "There are a lot of little paths connecting trails in places where the trees are thin. I do not want this feature yet, it is a bit out of control now"): PA-05 to PA-08 switched off (`DesignNetwork.AutomaticPaths`); the code is kept; the Network view, its key and line show no paths; their tests set aside |
| NT7 v2 | **Labels placed together** (owner, 2026-10-26: go with the plan as recommended): lift, Area, run and landmark names are placed in one pass (`UI/LabelPlacement`), lifts first and always shown, then Areas (centre, then just above or below), runs longest first (five spots along the line), then landmarks; ties by design order; a name with no free spot hides. Contour labels are unchanged |
| NT23 | **Names along their lines** (owner, 2026-10-26: "trail and lift names should be centered and aligned along the run length like the mockup"): a run's and a lift's name is centred on its line and slanted with it, read left to right; turned straight along the line around the spot, not bent letter by letter; Area and landmark names stay upright |
| NT21 | **Later (not task 10):** a cost or unlock for the number of base areas, and a skills/unlock tree; to the coordinator's backlog (the economy and buildings come after Phase 3) |
| NT16 | **The catchment ring while placing a lift** ("just like placing service buildings in Cities: Skylines 2"): the terminals being placed show their 100 ft ring, existing terminals theirs fainter, and the trails it would catch light up; mockup first |
| NT24 | **A trail is made of sections** (owner, 2026-10-27: "a trail is a collection of segments … allow the user to determine which segments are part of a named trail line"): a trail is a named, ordered chain of sections (a saved `TrailLine`, save v6 / Shape 6); a section is a painted piece between two nodes, and every section is in exactly one trail. Researched against OpenStreetMap route relations (the name on the group, physical tags on the pieces) and Cities: Skylines 2's automatic road names (player complaints): only the player changes membership |
| NT25 | **Edit sections** in the trail's pane replaces the Split and Join buttons (supersedes NT1), with two modes, **Choose** (default) and **Cut** |
| NT26 | **A trail is one line, top to bottom:** no forks |
| NT27 | A section taken out that nothing takes becomes **its own trail** with the next free name ("Run 12") |
| NT28 | A trail left in two pieces becomes **"Upper <name>"** (the top piece, keeping the trail's ID) and **"Lower <name>"**; with no sections left, it goes away |
| NT29 | **Natural/Graded and clearing are chosen only at creation;** the Edit pane has no Grading switch (supersedes TF-07's regrade in Edit). Grooming will be per trail (later phase) |
| NT30 | **Repaint and Move ends stay, one section at a time;** the section clicked to open the pane is preselected |
| NT31 | **Putting sections in a trail never changes the ground;** only cut nodes can be rejoined, which changes nothing (NT2). Retires NT3's seam regrade |
| NT32 | **A trail's sections are all planned or all built;** Break ground on a planned trail builds all its sections |
| NT33 | **Remove trail removes every section;** ground and trees come back (CL-07). Later: the earthwork stays and the trees grow back (coordinator backlog) |
| NT34 | **A trail is rated over its whole length** (TD-02 on the joined stations), not by its hardest section |
| NT35 | **Choose: left-click adds, right-click takes out.** A left-click routes: clicking any section beyond the trail's top or bottom adds the whole chain needed to reach it, so a whole other trail merges in one click; the hover previews the result first |
| NT36 | The player's word is **sections**; "segments" stays the Network view's junction-to-junction stretches (NW-08) |
| NT37 | Edit sections **may take a section from the middle of another trail;** before Done the window lists every trail it changes ("Run 4 becomes Upper Run 4 and Lower Run 4") with lengths and ratings before and after |
| NT38 | **"Continues Run 1":** when a new trail's top lands on another's bottom (or its bottom on another's top), New trail's review offers to add it to that trail, ticked by default |
| NT39 | **Inside Edit sections, Ctrl+Z / Ctrl+Y undo and redo one click;** Esc asks "Discard these changes?" when anything changed; Done (Enter) applies all as one undoable step |
| NT40 | **Automatic names are shown and editable before Done** and in reviews; cutting "Upper Run 4" gives "Upper Run 4" and "Lower Run 4", never "Upper Upper"; a typed name beats a "Run N" default |
| NT42 | **Hidden trail nodes** (finding S1, owner 2026-10-27: "plan for some generosity, as with hubs, but these hubs must NOT be shown to the player or have names"): section ends within **100 ft** of each other (NW-14's station reach) are one node, so they meet end to end for a trail's sections. Follow-up (owner: "nodes that are close together automatically merge … could make pathfinding better"): **every network node** within 100 ft merges (section ends, junctions, path ends; lift stations still win), so the graph has no slivers. Under the hood: derived, never saved, never named; the map only **draws the lines meeting** at the merged point (ends and ground stay as built) |
| NT43 | **Segments and sections** (findings S2, S3; owner: "sections refer to a segment that is a part of a trail. A segment is a line between two nodes"): a segment is the stretch of a painted run between two nodes; a trail's sections are the segments it holds, top first, end to end through shared nodes; a trail may change runs at a node (a reroute). Supersedes B1's section-as-painted-piece |
| NT44 | **Every junction is a node** (S2: "whenever a trail ends or crosses should create a split node"): an end landing on a run, a crossing, a lift station passed through; each run splits there on its own, with no save, and stays one trail ("Still one trail"). Plans split among plans, built among built |
| NT45 | **Cut is a point on the run** (S3): it adds a node there, works on any bend, and never touches paint, footprint or ground; Rejoin removes a cut point only |
| NT46 | **A node that disappears** leaves one longer segment; if the stretches either side were in different trails, it stays with the trail above |
| NT47 | **Grading is permanent once built** (sections finding 2, 2026-10-27): Grading is a "Graded" checkbox. A plan ticks and unticks freely; a built natural trail can be graded later; a built graded trail stays graded (the box is ticked and locked, and the commands refuse it) |
| NT48 | **A trail keeps its clearing's shape, not strokes** (sections finding 7, 2026-10-27; save v8, Shape 8): the clearing is the 250 mm cells (centred on the design frame's whole and half metres) inside its outline rings by the even-odd rule; strokes live only in the trail tool while painting, and Repaint paints onto the trail's own clearing (every trail, split ones too). Split clips the clearing and Join takes both. Saves before v8 convert each trail's strokes once; on opening, an older save's built work is graded once more from the untouched ground, in memory only (the file is untouched until the player saves), with a notice. The owner's Kearsarge save converts with no footprint cell changed and nothing moved; it doubles in size (307 KB to 616 KB) |
| NT41 | **A cut node is shared:** Move ends on one section moves both sections; the review shows both |

## Phase 3 task 15, terrain brush: decided 2026-10-26

**Context:** the owner asked for a Cities: Skylines 2-style terrain tool during task 08 (TL14), for hand-grading cat
tracks, junctions and the ground round lifts, and started it on 2026-10-26 ("get the brush going"). The plan went
through three revisions: five brushes (rev 1), the CS2 set and one pane (rev 2), then a confirmation and fixed
structures (rev 3, approved). Plan: [p3-15-plan.md](p3-15-plan.md); rules TB-01 to TB-14 in
[p3-02-design-rules.md §4.6](p3-02-design-rules.md).

| # | Decision |
|---|---|
| BR1 | **A Terrain tab in the Toolbox** holds the CS2 set: **Raise/Lower** (hold left to raise, hold right to lower), **Flatten** (right click samples the target elevation under the pointer, left paints toward it), **Smooth**, and **Slope** (right click sets the target elevation; a left drag ramps the ground toward it). Tray letters B, L, O and G |
| BR2 | **One brush pane for all four,** nearly identical: Diameter and Strength, plus the Target elevation (Flatten, Slope) and the Slope (Slope) where they apply |
| BR3 | **Slope picks its grade by trail rating:** four chips (● Easiest under 25%, ■ More difficult 25–40%, ◆ Most difficult 40–60%, ◆◆ Experts only 60% and up, from TD-03) choose the band, and a slider sets the grade inside it |
| BR4 | **The tool owns the right mouse button** while a Terrain tool is armed (right-drag pan comes back when it closes; WASD and the arrows still pan, middle-drag still turns) |
| BR5 | **The ground moves live and at once:** "when raising or lowering terrain you should see it raise as you hold the mouse" |
| BR6 | **Strokes replay last,** after every trail and lift (TL3's fold, phase 2): hand grading is the finishing pass. Raise and Lower are relative, so they ride on a regraded trail |
| BR7 | **A final confirmation:** strokes stay pending until Break ground, whose review gives the cut and fill. The confirmed session is one undo step; Ctrl+Z inside the tool takes back the last pending stroke |
| BR8 | **The cost is volumes for now:** cut, fill and the net import or export (yd³, m³ in metric), trees cleared and towers re-heighted. A money line joins it with the Phase 4 economy |
| BR9 | **Terminals, and later every building, are fixed:** the brush never changes the ground of their pads, and the ground may slope into them at **no more than 1:1** |
| BR10 | **A lift's clearance envelope can't be infringed:** the brush never brings ground closer to the line than the clearance rule allows |
| BR11 | **Towers follow the ground:** a tower's head (and so the rope) stays where it was, in the same orientation, and the tower's height changes with the ground at its base. Where the tower would pass its height limits, **the brush stops** there ("held by tower 7") |
| BR12 | **Save v4** (design Shape 4) adds the strokes; the design golden re-pins (owner OK with the plan) |
| BR13 | **Close-up contours** (owner, 2026-10-26, with the brush): finer contour levels appear as you zoom in, **40 → 20 → 10 ft** (metric 10 → 5 → 2.5 m), each fading in by its on-screen spacing as the 40 ft level already does; the finer levels read the 1 m heightmap per pixel. Labels stay every 200 ft (50 m), the yellow pending contours at 40 ft. **Contours switch on by themselves while a Terrain brush is armed** and go back as they were when it closes |
| BR14 | **Slope stays literal; the bill counts live** (owner, 2026-10-26, after the in-game review): the Slope brush toward a target far below or above the ground keeps its steep side walls (no automatic 1:1 side faces), as CS2's does. While you drag, the pane's **cut and fill update live** (with trees cleared and holds), about five times a second |
| BR15 | **The Terrain brushes share one session** (owner, 2026-10-26): switching between Raise/Lower, Flatten, Smooth and Slope keeps the pending strokes, the ground they made, the bill and the target; one Break ground reviews them all and Ctrl+Z works across them. Switching to another kind of tool still discards, with a notice (P3-06 Q8) |
| BR16 | **Slope runs between two points** (owner, 2026-10-26): no grade control; right click sets the target point (where and how high), the press the start, and the ground ramps evenly between them, level across, by where each point falls along the line. The pane shows the ramp's length and the grade it makes with its rating. Slope paints only once a target is set. Replaces the rating chips and grade slider (BR3's slope part) |

## Phase 3 task 11, benchmark with a drawn resort: decided 2026-10-28

**Context:** the evidence for Phase 3's exit criteria 2 and 3: a drawn resort on Jackson Hole 5 km, the benchmark
flown over it, and the edit budgets timed with it loaded. The plan was posted on 2026-10-26, held by the owner until
task 10 was complete (2026-10-27), re-posted for save v8 and approved on 2026-10-28 ("It is your time"), with every
recommendation taken. Plan: [p3-11-plan.md](p3-11-plan.md).

| # | Decision |
|---|---|
| DB1 | **The test design is frozen lines in code, built by commands on every run** (as the style tile is): trail strokes and lift ends in local metres, built through `TrailShape.FromStrokes` and AddTrail, and through LiftBuild, so it follows the engine and every save version; no save fixture, no format change |
| DB2 | **Layout: six lift pods**, about three runs off each lift's top, plus two cat tracks joining pods; the owner approves screenshots before timing |
| DB3 | **Lifts:** two Sessellift FGQ-4, two Monta FG4 and two Sessellift D6 |
| DB4 | **Trails:** ten graded and ten natural, 60–200 ft wide, two with tree islands, plus two graded 40 ft cat tracks |
| DB5 | **The eight baselines stay as they are:** the new legs fly only with `-drawnresort`, and the drawn runs get files of their own |
| DB6 | **Lift edits are timed and reported, not gated:** CF-09's budgets cover grading |
| DB7 | **The measured view includes task 10's network** (overlay, labels, stations), on main before the first run |
| DB8 | **A missed budget is reported with its numbers and goes to the owner;** no fix in this task without the owner's OK |
