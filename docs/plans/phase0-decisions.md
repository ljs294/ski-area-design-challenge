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
| G2 | Bundled demo and menu background: **Crystal Mountain, Washington** (fallback 1 m lidar; not yet in S1M) |
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
| TR1 | **Real species with a stylized look:** the right species in the right places, rendered in the diorama style (A5). Phase 1 set: subalpine fir, mountain hemlock, Pacific silver fir, Douglas-fir, lodgepole pine, quaking aspen, krummholz |
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
