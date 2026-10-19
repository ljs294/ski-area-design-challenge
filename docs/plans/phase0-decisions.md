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
| LP20 | **Monta FG4 is a bottom-drive lift.** The drive terminal loads: twin tubular booms, a ribbed rounded hood with a window band over the bullwheel, inclined legs on two piers. The return is the top station: an exposed bullwheel on an inclined leg and a column. A Monta terminal's origin is on its bullwheel's axle at grade, +Z toward the line; the line gauge is 4,895 mm. Both terminals are lowered to the Sessellift chair's rope height (3,039 mm) by cutting their diagonal members at the same angles, so the lift carries that chair unchanged. The return's footings reach 2.5 m below grade (LP7) |
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
