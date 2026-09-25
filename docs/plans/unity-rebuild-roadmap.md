# Unity rebuild roadmap

**Audience:** the project owner and coding agents. **Status:** decided. **Unity is the chosen engine.** The game is being rebuilt completely from scratch. **Facts current as of:** September 2026 (see Sources).

> **Scope update (2026-09-25).** The rebuild proceeds in iterations. Iteration 1 is **just the mountain**: a real-world picker, USGS S1M 1 m lidar, and game-made lighting, camera, ground cover, forest and snow, offline after download. Drawing tools and simulation follow. Phases are now defined in [0.6 Milestone plan](phase0-0.6-milestones.md), and all Phase 0 decisions are in the [decision record](phase0-decisions.md); both take precedence over older text below.

## 1. Decision and ground rules

**Engine: Unity 6.3 LTS.** Universal Render Pipeline, Burst and Jobs, Entities Graphics where it pays, Splines, Shader Graph, VFX Graph, and UI Toolkit. Windows desktop first.

**This is a from-scratch rebuild, not a port:**
- No code from the MapLibre/TypeScript game is copied, transpiled, embedded or run. There is no JavaScript engine inside Unity and no Node sidecar.
- The archived game (branch `archive/maplibre`, tag `maplibre-final`) is **reference material**: features, player-facing behavior, domain doctrines (trail grading, pond earthwork, snow and wear, snowmaking hydraulics, the dual-clock time model) and lessons learned. Each is re-specified in `docs/` and re-implemented natively in C#. Any of it may be redesigned.
- Data formats and saves are new. Old saves and terrain packages are not imported; an importer could be considered later as an optional feature.
- The preserved MapLibre performance audit is [here at the `maplibre-final` tag](https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/guest-performance-optimization.md). Its lessons become rules (§5).

## 2. Scope the engine must serve

1. Real-world terrain acquired for any chosen site: elevation, imagery, land cover, a surround ring. Shown as a beautiful, stylized mountain.
2. Runtime terrain grading and earthwork for trails, roads, ponds, dams and building pads.
3. Dense procedural forest (hundreds of thousands of trees) with wind, shadows and clearing.
4. A snow surface driven by simulation fields (depth, surface class, wear), with grooming patterns and moguls.
5. 3D lifts: towers, sheaves, terminals, catenary haul rope, and moving chairs or cabins synced to the simulation.
6. Thousands of animated guests, plus vehicles (groomers, snowmobiles) and snow-gun VFX.
7. Construction tools, precise picking, and a free/orbit camera.
8. A sharp, data-rich tycoon UI.
9. Desktop distribution; offline play once a resort's data is downloaded.
10. A solo developer working with AI coding agents; a deterministic, well-tested simulation.

## 3. Why Unity (evaluation record)

The candidates you named, evaluated for a from-scratch rebuild. Code reuse is deliberately **not** a criterion.

**Current MapLibre GL JS** (v6 is in pre-release: it drops WebGL1 and moves to ESM).
- A map renderer. No native 3D model layer, PBR lighting, shadows, animation, physics or scene graph.
- Three.js can live in a custom layer, but it can't shadow or shade MapLibre's terrain.

**New map-based engine: Mapbox GL JS v3** (plus CesiumJS as the open alternative).
- Mapbox v3 adds glTF models, 3D lights and shadows. But it is proprietary: the license is valid only with an active Mapbox account, "for use only with the relevant Mapbox product(s)", and use is billed per map load. That doesn't fit an offline commercial desktop game.
- CesiumJS (Apache-2.0) is real 3D.
- Neither has game systems: crowds, cables, construction tooling.

**Unity 6.3 LTS.**
- Genre precedent: Parkitect and both Cities: Skylines games.
- C# is productive for a solo developer working with AI agents.
- URP scales down to integrated GPUs.
- Relevant tools: Burst/Jobs and Entities for scale, Splines, Terrain with runtime height edits, Shader Graph and VFX Graph, and UI Toolkit with native SVG, world-space UI and runtime data binding.
- Cesium for Unity 1.25 supports Unity 6.5.
- Licensing: Personal is free under $200k revenue; Pro is about $2.3k per seat per year.

**Unreal Engine 5.8.**
- Best photoreal ceiling: Lumen, Virtual Shadow Maps, PCG (production-ready since 5.7). Nanite Foliage is experimental. The Mass crowd system was reworked in 5.8.
- Costs: C++ and Blueprints (binary assets agents can't edit), slow iteration, weak dashboard UI, runtime grading needs custom terrain, a higher GPU floor, and a 5% royalty above $1M.

| Criterion (weight) | MapLibre | Mapbox v3 | CesiumJS | **Unity 6.3** | Unreal 5.8 |
|---|---|---|---|---|---|
| Terrain, forest and lighting visuals (20) | 2 | 3 | 3 | 4 | 5 |
| Tycoon systems: cables, vehicles, animation, construction (20) | 1.5 | 2 | 2 | 5 | 4 |
| Thousands of animated guests (10) | 3 | 2 | 2 | 5 | 5 |
| Real-world terrain and runtime grading (10) | 5 | 4 | 4 | 4 | 3 |
| Sharp, data-rich tycoon UI (10) | 5 | 5 | 5 | 4 | 2 |
| Solo + AI-agent development velocity (15) | 3 | 3 | 3 | 4 | 2 |
| Hardware reach (5) | 4 | 4 | 4 | 5 | 3 |
| Licensing, cost and offline fit (5) | 5 | 1 | 5 | 3 | 4 |
| **Weighted score** | 61% | 59% | 63% | **86%** | 73% |

**Result: Unity.** Unreal gains only on visuals and licensing. It would need the visuals weight above about 85 (from 20) to overtake Unity, which doesn't fit this game's stylized Parkitect-like direction.

## 4. Target architecture

```
Unity 6.3 LTS desktop client — one C# codebase
├─ Acquisition (C#)     site picker → elevation (USGS 3DEP), imagery (NAIP), land cover (ESA WorldCover),
│                       roads/water (OSM), weather history (Daymet, NASA POWER) → local resort package
├─ Simulation (pure C#, deterministic, noEngineReferences asmdef; Burst-friendly SoA; fixed tick on a
│   │                    worker thread)   time & weather · guests · lifts & queues · snow & wear ·
│   │                    snowmaking hydraulics · economy · commands in, snapshots out
│   ▼ double-buffered snapshot (NativeArrays)
├─ World                terrain heights/splat/snow textures · grading & earthwork · vegetation · construction
├─ Presentation (URP)   terrain shader · instanced forest · lifts/ropes/chairs · guests (instancing/VAT) · VFX
├─ UI (UI Toolkit)      view models bound to throttled snapshots · screen UI · world-space labels
└─ Persistence          new versioned save format · resort package cache
```

World units are metres in a local frame centred on the resort. At a 5 km extent Float32 precision is under 1 mm, so none of the Mercator or relative-to-center workarounds from the web version are needed.

## 5. Lessons from the MapLibre audit, adopted as rules

- The simulation publishes snapshots; presentation and UI read them and never mutate simulation state directly. Changes go through a command queue.
- UI view models update at bounded rates: controls immediately, the HUD when the displayed value changes, dashboards at ≤4 Hz. The UI never rebuilds per tick.
- Per-frame paths allocate nothing per agent: NativeArrays, Burst jobs and pooled buffers.
- Fields that change hourly (weather, lighting) recompute when they change, not every tick.
- Agent elevation comes from precomputed route or heightfield samples, never per-agent queries each frame.
- Textures update by dirty rectangle, never by full regeneration.
- Golden-trajectory determinism tests and frozen performance budgets exist from day one:
  - Frame p95 ≤20 ms at 1080p on the reference GPU.
  - <1% of frames over 50 ms.
  - Pause/selection response ≤100 ms.

## 6. Simulation vertical slice: validation milestone (now Phase 4)

**Moved (2026-09-25):** this slice is now the Phase 4 milestone in [0.6](phase0-0.6-milestones.md#5-phase-4-simulation-foundations-iteration-3). Phase 1 is the mountain vertical slice. The integrated-GPU target is replaced by an RTX 2060 minimum spec (T13).

This proves the architecture and the budgets; it does not reopen the engine choice. Built from scratch:
- One real resort area via the new C# acquisition pipeline, or a checked-in test area.
- Terrain splatted by cover, slope and snow.
- A Poisson-disc forest.
- Two or three lifts with towers, ropes and moving chairs.
- A first native guest simulation with 3,000 guests.
- One grading edit.
- The toolbar, simulation bar and one dashboard, following the style guide.

**Exit criteria:**
- 60 FPS at 1080p on the RTX 3060 Ti; ≥30 FPS on a current integrated GPU.
- Grading edits visible in ≤100 ms.
- Simulation tick within its budget.
- A UI sharpness and flow review.
- Velocity versus estimate.

A miss changes the approach within Unity (terrain representation, LOD budgets, pipeline settings), not the engine.

## 7. Simulation, designed from scratch

- **Architecture, decided in deliverable 0.3.** Recommended:
  - A plain C# deterministic core with struct-of-arrays data (NativeArrays) and Burst jobs for hot loops, in an engine-independent assembly that is fully unit-testable.
  - Entities or instancing used for *presentation* at scale.
- **Time model:** re-specify the dual-clock idea (macro accounting time plus micro movement time) using the archived design docs. Keep it, simplify it or replace it in 0.2/0.3.
- **Determinism:** fixed tick, integer money, keyed domain-separated randomness, golden-trajectory tests.
- **Reference map:** the new subsystem each archived module informs. It is specified anew, never translated:

| Archived reference (at `maplibre-final`) | New Unity subsystem |
|---|---|
| `src/dualClock/`, `src/guestSimulation/` | Simulation/Time, Simulation/Guests, Simulation/Lifts |
| `src/network.ts`, `src/topology.ts`, `src/skiNodes.ts` | Simulation/Network (CSR graph) and World/Topology |
| `src/trails.ts`, `src/earthwork.ts`, `src/pondEarthwork.ts`, `src/damEarthwork.ts` | World/Construction and World/Earthwork |
| `src/snowmakingHydraulicSolver.ts`, `src/snowmakingHydraulics.ts` | Simulation/Snowmaking |
| `src/snow.ts`, `src/dualClock/wear.ts` | Simulation/Snow |
| `src/weather/`, `weather-engine/`, `weather-service/` | Acquisition/Weather and Simulation/Weather |
| `src/terrainIngest.ts`, `src/fourClassCover.ts`, `src/usgsTerrainCover.ts` | Acquisition/Terrain and Acquisition/Cover |

## 8. Terrain and data acquisition

- **Acquisition in C#:** `HttpClient` downloads, GeoTIFF decoding (for example BitMiracle LibTiff.NET, after a license check), WorldCover, NAIP, OSM and weather history.
  - Lessons kept: trust the GeoTIFF's own returned bounds; download a surround ring; play offline after download.
  - Each provider's terms are reviewed in 0.3.
- **Site picker:** the Cesium for Unity globe (Cesium ion account and terms), or a UI Toolkit slippy map over a provider whose tile terms allow it. Decided in 0.3.
- **Representation:**
  - Unity Terrain heightmap, up to 4097² with 16-bit heights (about 1.5 cm steps over 1,000 m of relief), with lower-resolution neighbour terrains for the surround ring.
  - A custom CDLOD mesh behind the same service interface if grading or the look requires it.
  - Grading: a height patch → `TerrainData.SetHeightsDelayLOD` → `SyncHeightmap` when the edit ends. Splat and forest masks update in the same transaction.
- **Material:** a stylized splat by cover, slope and altitude, with triplanar rock on cliffs. Snow comes from simulation depth, surface and wear textures updated by dirty rectangle:

```hlsl
float slope   = 1 - saturate(dot(normalWS, float3(0,1,0)));
float rock    = smoothstep(0.55, 0.75, slope);
float snowAmt = saturate(SAMPLE(_SnowDepth, uv).r / 0.25) * (1 - smoothstep(0.60, 0.80, slope));
float groomed = SAMPLE(_SurfaceClass, uv).g;     // corduroy normal on groomed runs
float worn    = SAMPLE(_Exposure, uv).r;         // skier wear darkens/ices
albedo = lerp(lerp(coverAlbedo, rockAlbedo, rock), snowAlbedo(groomed, worn), snowAmt);
```

## 9. Vegetation

- **Generation** (Burst job, deterministic, never saved):
  - Bridson Poisson-disc sampling per 64 m tile, seeded by `hash(resortSeed, tileX, tileY)`.
  - Density = forest fraction × slope mask (<40°) × treeline falloff × trail/road clearing.
  - Edits re-run only the affected tiles.
  - Species by altitude and aspect.
  - Estimate: about 650k trees at 65% forest over 25 km².
- **Rendering:**
  - Compute-shader culling into `Graphics.RenderMeshIndirect` (or BatchRendererGroup).
  - LODs: LOD0 at ≤1.5k tris, LOD1 at ≤300, then an octahedral impostor beyond about 300 m.
  - Vertex wind.
  - Only near-cascade shadow casters, plus a baked canopy-shadow mask.
  - Snow on branches.
- **Buy vs build:** evaluate GPU Instancer, Nature Renderer and Amplify Impostors in Phase 1.

## 10. Tycoon systems

- **Lifts:**
  - Each span's haul rope is a catenary `y(x) = a·cosh((x − x₀)/a) + c` with `a = H/w` (horizontal tension over weight per metre), rendered as a spline tube.
  - Chair count = `capacityPph × rideTimeS / 3600 / seatsPerChair`, spaced by arc length and advanced at line speed. Detachable terminals move chairs onto a slow rail.
  - The simulation is authoritative for boarding.
- **Guests and vehicles**, with LOD tiers:
  - <60 m: skinned mesh.
  - 60–400 m: instanced vertex-animation textures.
  - 400 m+: dot or impostor.
  - Beyond that: aggregate flow.
  - Groomers write corduroy into the surface texture.
- **Construction:** Splines for trails, roads, pipes and lift lines, with a single-owner preview → review → confirm flow.
- **Picking:** physics raycasts for structures and a screen-space spatial hash for guests, with priority guests > snowmaking > building > lift > trail > dam > pond > road.
- **Camera:** orbit/RTS with terrain collision, follow mode and a cinematic mode.

## 11. UI: tech stack and design direction

**Stack: Unity UI Toolkit (6.3 LTS)** for all screen UI and in-world labels.

| Need | How UI Toolkit meets it |
|---|---|
| Sharp at any resolution and DPI | Signed-distance-field text (TextCore); native SVG import as UI Toolkit Vector Images for icons and lift symbols; resolution-independent Panel Settings scaling |
| Look like the current game | USS is CSS-like with custom properties, so the current token set ports directly (list below) |
| Data-rich dashboards | Runtime data binding to view models; virtualized `ListView`/`MultiColumnListView`; `Painter2D` for charts, trail profiles and snow-depth graphs |
| Polish | USS transitions; UI Toolkit custom shaders and filters (e.g. background blur, like today's `backdrop-filter`) |
| In-world UI | World-space UI Toolkit panels for lift names along lines, trail signs and guest thought bubbles |
| Accessibility | A UI scale of 50–150% (as today), keyboard focus navigation, light/dark themes |

**Tokens to port** into `Theme-Light.tss` and `Theme-Dark.tss`:
- Colours: `--accent #155ab6`, `--text #1e2a32`, `--surface #fff`, `--app-bg #f3f5f2`, `--border #d6dfe1`, `--danger #b33338`, `--success #276748`, plus the dark-theme set.
- Radii 4/6/12 px; 14 px base text; the 38 px docked simulation bar.
- Compact game windows with connected tabs.
- Outline icons.
- Font: a redistributable face such as Inter or Noto Sans, chosen in the style tile. Segoe UI can't be shipped.

**Alternatives considered:**
- uGUI: legacy, and less CSS-like.
- NoesisGUI: XAML vector UI, commercial. The fallback if UI Toolkit falls short.
- Coherent Gameface: HTML/CSS in-game UI, commercial and heavier.
- Embedded web views: rejected for performance and consistency.

**Improving game flow** is designed before any UI is built, in deliverable 0.4:
1. Inventory the archived game's screens and journeys: home/ski-sign menu, setup workspace, toolbar and simulation bar, floating game windows, dashboards, inspectors, construction review.
2. Identify friction.
3. Propose improved flows, then wireframes, then a style tile, then a UI Builder prototype in Phase 1.

Candidate improvements to evaluate:
- A unified build palette with contextual tool options.
- One consistent preview → cost → confirm pattern across every construction tool.
- In-world selection with anchored inspector cards.
- An advisor/notification feed replacing scattered warnings.
- RollerCoaster Tycoon-style guest thoughts surfaced in the world.
- Collapsible persistent dashboards.
- Keyboard-first shortcuts.

## 12. Asset pipeline

- **Formats:**
  - Blender → glTF 2.0 → Unity through glTFast (FBX is acceptable). One unit is one metre.
  - Unity's importer handles mesh and texture compression (BC7) and Addressables.
  - meshoptimizer/gltfpack is used for LOD simplification.
  - Draco, meshopt compression and KTX2 matter only for runtime-downloaded content or mods.
- **Procedural assets** via headless Blender (`blender -b -P tools/assets/build_lift_tower.py -- --height 12 --sheaves 8`):
  - Parametric towers, terminals, sheaves, chairs and cabins.
  - A modular lodge kit on a 1 m grid.
  - Geometry Nodes for variations. Colliders and LODs are emitted by the same scripts.
- **Generative 3D** (Hunyuan3D, TRELLIS, Meshy, Tripo, Rodin), for props and concept blocking only. Every result goes through:
  1. Quad remesh or decimation.
  2. xatlas UVs.
  3. Albedo bake, then palette quantization.
  4. meshoptimizer LODs.
  5. Budget checks.
  Watch for baked-in lighting, topology problems, inconsistent scale, missing rigs, and license limits (some open-weight licenses restrict regions or usage).
- **Guests:** one modular rigged base (~1.2k tris) with swappable clothing, skis and snowboards, per-instance palette colours, and baked vertex-animation textures for the mid LOD.
- **Cohesion:**
  - One 512² palette atlas.
  - Stylized PBR: constant roughness per material class, metallic only on lift steel.
  - A shared snow material function.
  - One lighting rig driven by the simulation's solar position and weather.
  - An optional toon ramp and outline.
  - A time-of-day colour-grading LUT.

| Asset | LOD0 tris | Texture |
|---|---|---|
| Guest | ≤1,500 | palette |
| Chair / cabin | ≤800 / ≤3,000 | palette |
| Tower | ≤2,000 | palette |
| Terminal | ≤8,000 | palette + 1k detail |
| Lodge module | ≤15,000 | palette + 2k hero |
| Tree | ≤1,500 / ≤300 / 2 (impostor) | 1k atlas |

## 13. Phases and planning approach

**When the plans are written:** Phase 0 defines **all** phases at milestone level (scope, exit criteria, dependencies, risks) and writes the **detailed** Phase 1 implementation plan. Each later phase's detailed plan is the final deliverable of the preceding phase, reviewed and approved by you before that phase starts. The vertical slice will change what later phases need, so detailing them now would produce stale plans.

**Phase 0 deliverables** (docs on `main`, in order; 0.1 and 0.3 can proceed in parallel with 0.2):

| # | Deliverable | Content |
|---|---|---|
| 0.1 | Reference inventory | Every feature, rule and doctrine of the archived game worth knowing, with links at `maplibre-final` |
| 0.2 | Game design and game flow | What the Unity game is; keep, change, add; core loops; progression |
| 0.3 | Technical architecture | Assemblies, simulation architecture and threading, data and save formats, acquisition and provider terms, determinism, performance budgets, testing |
| 0.4 | UI/UX spec and style guide | Screen and journey inventory, improved flows, wireframes, tokens, components |
| 0.5 | Art direction and asset plan | Style tile, palette, budgets, procedural vs generated |
| 0.6 | Milestone plan, Phases 1–4 | Scope, exit criteria, dependencies, risks |
| 0.7 | Detailed Phase 1 plan | Task breakdown, order, acceptance tests |

**Estimates:** superseded by [0.6](phase0-0.6-milestones.md) (2026-09-25): Phase 1 mountain vertical slice (12–14 weeks), Phase 2 iteration 1 complete (2–3 months), Phase 3 drawing (3–5 months), Phase 4 simulation foundations (4–6 months). The original table is kept below for the record.

**Original estimates** (rough, solo + AI, from scratch; uncertain):

| Phase | Duration | Outcome | Detailed plan written |
|---|---|---|---|
| 0: Foundation and definition | 3–5 weeks | Repo split and preservation; Unity bootstrap; 0.1–0.7 | This roadmap and Phase 0 |
| 1: Vertical slice | 8–12 weeks | §6 exit criteria met | 0.7 |
| 2: Core systems | 4–6 months | Acquisition, terrain and grading, forest, lifts, guest simulation v1, camera, picking, saves, core UI | End of Phase 1 |
| 3: Feature completeness | 6–10 months | All construction tools, snowmaking, economy, weather, dashboards | End of Phase 2 |
| 4: Depth and release | Ongoing | Groomers, VFX, night, detailed avatars, Steam release | End of Phase 3 |

## 14. Risks

- Scope creep toward matching every archived feature. Mitigated by 0.2 deciding what to keep.
- UI effort.
- ECS complexity; mitigated by keeping ECS to presentation.
- Data-provider and Cesium ion terms.
- Cross-platform determinism.
- Asset production volume; mitigated by procedural generation and the palette.
- Unity pricing or policy changes.

## 15. Repository and branch management

**Model:** one repository. `main` is always the version being worked on, which after the bootstrap is the Unity project. The archives preserve what came before.

| Ref | Kind | Meaning |
|---|---|---|
| `main` | Active branch | The current Unity version. Topic branches (`feature/*`, `fix/*`, `docs/*`) merge by PR. |
| `archive/maplibre` | Frozen branch (locked on GitHub) | The final MapLibre game with both planning documents; never pushed to again |
| `maplibre-final` | Annotated tag | The same commit, immutable |
| `archive/unity` | Rolling branch | Fast-forwarded to `main` only at a green milestone: always the last known-good Unity version |
| `unity-m0`, `unity-m1`, … | Annotated tags | Each milestone, immutable |
| `threejs-edition-final` | Annotated tag | The earlier three.js experiment (its branch is kept) |
| `v0.1`, `legacy/v0.1` | Existing | Untouched |

```
simulation ──●68750a0──●(docs + archive note)
                        │ fast-forward
main ───────────────────●────────●(clean slate + Unity project, via PR)──●──…──●──…──►
                        │        unity-m0 ▲                          unity-m1 ▲
      maplibre-final ◄──┤   archive/unity ┴──── fast-forwarded at milestones ┴──►
      archive/maplibre ◄┘   (frozen)
```

**What happens to `origin/main`:**
- **Its files are replaced.** After the bootstrap PR merges, `main` holds only the Unity project, docs and tools.
- **Its history is kept.** The replacement is one ordinary commit on top of the MapLibre history, so there is no force-push. `git log main` still lists the MapLibre commits before it.
- `git checkout archive/maplibre` (or the `maplibre-final` tag on GitHub) returns the full MapLibre game.

**Layout on `main`:**

```
/
├─ Assets/MountainPlanner/
│   ├─ Runtime/Simulation/   (asmdef: noEngineReferences; deterministic core)
│   ├─ Runtime/World/        (terrain, grading, vegetation, construction)
│   ├─ Runtime/Presentation/ (rendering, LOD, instancing, VFX)
│   ├─ Runtime/UI/           (UI Toolkit: UXML, USS/TSS themes, view models)
│   ├─ Editor/
│   └─ Tests/EditMode/, Tests/PlayMode/
├─ Packages/manifest.json    (pinned packages)
├─ ProjectSettings/          (Force Text, Visible Meta Files, URP; version pinned)
├─ docs/plans/unity-rebuild-roadmap.md
├─ docs/reference/maplibre-archive.md   (links into maplibre-final)
├─ tools/repo-checks/        (zero-dependency Node: agent-docs checks, .meta integrity)
├─ tools/assets/             (Blender scripts, budgets; later)
├─ .github/workflows/repo-checks.yml
├─ .gitignore  .gitattributes  README.md  AGENTS.md  CLAUDE.md
```

**Git configuration:**
- `.gitignore`: the standard Unity set: `Library/`, `Temp/`, `Obj/`, `Build*/`, `Logs/`, `UserSettings/`, `MemoryCaptures/`, `.vs/`, `*.csproj`, `*.sln`, `*.pidb`, `*.userprefs`, `sysinfo.txt`, plus `test-results/`.
- `.gitattributes`, one pattern per line:
  - Git LFS for `*.png`, `*.psd`, `*.tga`, `*.exr`, `*.hdr`, `*.fbx`, `*.blend`, `*.wav`, `*.ogg`, `*.mp4`, `*.ttf`, `*.otf`.
  - `merge=unityyamlmerge eol=lf` for `*.unity`, `*.prefab`, `*.asset`, `*.mat`, `*.anim`, `*.controller`, `*.physicMaterial`.
  - `*.meta eol=lf`.
- Per-clone setup, documented in `README.md`: `git lfs install`, plus the UnityYAMLMerge driver entry (`driver = '<Editor>/Data/Tools/UnityYAMLMerge.exe' merge -h -p --force %O %B %A %A`, checked against the installed editor's docs).
- Watch the GitHub LFS storage and bandwidth quota.

**Checks and CI:**
- `tools/repo-checks/check.mjs` ports the archived agent-docs rules (paired `CLAUDE.md`, ≤150 lines, ≤8,000 characters, ≤20 routing rows, resolvable local links; skips `Library/` and the other generated folders). It adds `.meta` integrity: every asset has a `.meta`, and there are no orphans.
- `repo-checks.yml` runs it on every PR.
- Unity tests run locally in batchmode until a GameCI workflow is added. That needs Unity license secrets in the repository.

**New root `AGENTS.md` for `main` (draft, about 3.5k characters):**

```markdown
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
```

**Prerequisites (your actions):**
- Install Unity 6.3 LTS with Windows build support through Unity Hub, and activate a Personal license. This PC has UE 5.8 but no Unity editor.
- Lock `archive/maplibre` and require PRs on `main` in the GitHub branch settings, or confirm and I'll do it with `gh api`.
- Confirm your GitHub LFS quota.

**Optional housekeeping, only with your go-ahead:** about 14 older local branches (`back/refactor-d1`, `backup/refactor-d4-audit`, `feature/*`, `snowmaking/*`, …) could be tagged `archive/<name>` and then pruned. Nothing is deleted without approval.

## Sources (checked September 2026)

- MapLibre April 2026 newsletter: https://maplibre.org/news/2026-05-02-maplibre-newsletter-april-2026/
- MapLibre GL JS roadmap: https://maplibre.org/roadmap/maplibre-gl-js/
- Mapbox GL JS v3.0.0 release: https://github.com/mapbox/mapbox-gl-js/releases/tag/v3.0.0
- Mapbox GL JS license: https://github.com/mapbox/mapbox-gl-js/blob/main/LICENSE.txt
- Mapbox GL JS pricing: https://docs.mapbox.com/mapbox-gl-js/guides/pricing/
- Unity 6.3 LTS announcement: https://unity.com/blog/unity-6-3-lts-is-now-available
- Unity 6 support and releases: https://unity.com/releases/unity-6/support
- Unity 6 UI Toolkit updates: https://unity.com/blog/unity-6-ui-toolkit-updates
- Unity vector graphics in UI Toolkit: https://docs.unity3d.com/6000.4/Documentation/Manual/ui-systems/work-with-vector-graphics.html
- Unity plans: https://unity.com/products
- Unity pricing updates: https://unity.com/products/pricing-updates
- Unreal Engine 5.7 announcement: https://www.unrealengine.com/news/unreal-engine-5-7-is-now-available
- Unreal Engine 5.8 release notes: https://dev.epicgames.com/documentation/unreal-engine/unreal-engine-5-8-release-notes
- Unreal Engine licensing: https://www.unrealengine.com/license
- Cesium for Unity: https://cesium.com/platform/cesium-for-unity/
- Cesium releases, August 2026: https://cesium.com/blog/2026/08/04/cesium-releases-in-august-2026/
