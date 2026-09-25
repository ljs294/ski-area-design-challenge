# Phase 0 · 0.3 Technical architecture

**Audience:** the project owner and coding agents. **Status:** draft for review (2026-09-25). Decisions are numbered **T1–T14**; open questions are collected in [phase0-review.md](phase0-review.md). Inputs: the [roadmap](unity-rebuild-roadmap.md) and the [reference inventory](phase0-0.1-reference-inventory.md).

## 1. Scope

**The product for now is a mountain painter:**
1. Pick a real site and download its data.
2. See it as a stylized 3D mountain.
3. Design on it with construction tools; the exact set is question S1 in the review.
4. Save and load.

**Out of scope for now:** guests, weather and snow simulation, economy, operations. Simulation is reduced to a **clock placeholder** (§10). It also serves as the "view time" that drives time-of-day lighting and a season preview.

**This document decides:** assemblies, runtime structure and threading, world representation, data formats, data acquisition and provider terms, determinism, performance budgets and testing. **It defers** game design to 0.2, UI to 0.4 and art to 0.5.

The roadmap's simulation rules (§5, §7) and its Phase 1 guest targets (§6) remain the plan for later. They are not active until simulation is back in scope.

## 2. Assemblies (T1)

The M0 skeleton has `Simulation → World → Presentation → UI`. In a painter, the engine-free code is the design model and the construction math, not a simulation. **Proposal:**

| Assembly | Engine refs | References | Holds |
|---|---|---|---|
| `MountainPlanner.Domain` | None (`noEngineReferences`) | — | Identifiers, units, local-metre geometry, grids, keyed hash RNG, the revisioned design document, commands, construction rules and earthwork math (grading, ponds, dams), the ski network graph, lift catalog |
| `MountainPlanner.Simulation` | None | Domain | **Placeholder only:** the clock interface and a manual view clock (§10) |
| `MountainPlanner.Persistence` | None | Domain | Save and resort-package formats, versioning, migrations |
| `MountainPlanner.Acquisition` | None | Domain, Persistence | Provider clients, GeoTIFF decoding, package building and validation |
| `MountainPlanner.World` | Yes | Domain, Simulation, Persistence | Unity Terrain sync, height/splat/mask updates, vegetation generation (Burst), spatial queries, tool preview orchestration |
| `MountainPlanner.Presentation` | Yes | World (+ below) | Terrain material, forest rendering, lifts and ropes, infrastructure meshes, lighting, camera |
| `MountainPlanner.UI` | Yes | Presentation (+ below) | UI Toolkit screens, themes, view models, world-space labels |
| `MountainPlanner.App` | Yes | All | Bootstrap, scene flow, input routing, background-task host, composition root |

**Rules:**
- References only point down the table, never up.
- Engine-free assemblies may use the .NET base library plus approved precompiled DLLs only (Newtonsoft.Json, LibTiff.NET).
- The architecture tests from M0 grow to cover the new order.
- **Migration:** rename the M0 `Simulation` assembly to `Domain`, add the new ones, and update `AGENTS.md` in the same PR.

**Why engine-free matters:**
- The core can be unit-tested in milliseconds, and even outside Unity (§9).
- Acquisition can run as a command-line tool that builds test fixtures.
- The rule forces the model/view separation the archive had to retrofit.

## 3. Runtime structure

### 3.1 One authored design document (T2)

- **Contents:** everything the player builds or edits: terrain edits, clearings, trails, nodes and junctions, lifts, and any other in-scope structures.
- **Immutable snapshots with a monotonic revision.** Every change is a typed **command** applied against a named revision; a command built on a stale revision is rejected without effect. This is doctrine 2 in 0.1 §9.
- **Undo/redo** falls out of immutable snapshots: keep a bounded history of revisions. 0.2 decides whether players get undo; the structure supports it at no extra cost.
- **Derived data is never saved:** network graph, trail stats, meshes, splat maps, forest instances. It is rebuilt from the document and terrain, either on load or incrementally per change.

### 3.2 Construction flow

```
tool input ──► preview request (revision-stamped) ──► background compute ──► review state
                                                                         │
confirm ──► command(expected revision) ──► Domain validates + applies ──► new revision
                                                                         │
            World (terrain/masks/forest patches) ◄── observers ──► Presentation, UI
```

- **One owner:** the active tool owns preview, cursor and camera overrides. Changing tool cancels the previous tool synchronously and restores the overrides exactly once.
- **One confirmation at a time;** a double confirm is rejected.
- **Late previews** compare revision and geometry keys and are dropped if superseded.
- **Committing** updates the height grid, cover/clearing masks and affected forest tiles in one transaction, before observers are notified.

### 3.3 Threading (T3)

- **Main thread:** Unity scene, input, UI, applying finished patches to `TerrainData` and GPU buffers.
- **Background work:**
  - Engine-free math (grading solve, pond/dam earthwork, graph rebuild) runs as .NET tasks with cancellation tokens.
  - Grid-heavy work (vegetation sampling, splat/mask rasterization, normals) runs as Burst jobs owned by World.
  - Both return results tagged with the input revision.
- **No persistent simulation thread now.** §10 records where it will sit.

## 4. World representation

### 4.1 Coordinates (T4)

- **Frame:** the resort package is stored in the **UTM zone of the site centre** (WGS 84), in metres, with a local origin at the site centre.
- **Unity axes:** world `x` = easting − E₀, `z` = northing − N₀, `y` = elevation. One unit is one metre.
- **Precision:** at a 10 km extent, float32 precision is about 1 mm, so no floating-origin tricks are needed.
- **Why:** square metre pixels remove the degree/metre aspect bug class from 0.1 §3. Providers can deliver rasters in UTM directly, and UTM distortion across a 10 km site is about 0.04% at most.
- **Geographic conversions** exist only at the acquisition and display boundary.

### 4.2 Heights and terrain (T5)

- **Authoritative heights** live in an engine-free float32 grid owned by Domain. Unity `TerrainData` is a *view* of it.
- **Resolution:**
  - Core at **2 m** (a 5 km site is 2,500²). 1 m is an option where USGS 1 m lidar exists and grading needs it; Phase 1 measures.
  - Surround ring at 10 m out to 3 km.
- **Rendering:** a grid of Unity Terrain tiles, each 2ⁿ+1 heights (for example 1,025² covering about 2 km at 2 m), plus lower-resolution neighbour tiles for the ring. Everything sits behind a `ITerrainSurface` interface, so a custom mesh can replace Unity Terrain if the look or grading needs it (roadmap §8).
- **Edits:** grading writes a height patch, then `SetHeightsDelayLOD` during the preview, then `SyncHeightmap` on commit. The splat, clearing mask and forest tiles covering the same rectangle update in the same transaction.

### 4.3 Package is immutable; edits live in the save (T6)

The archive mutated the terrain package in place, which made save ordering a constant hazard (0.1 §8).
- The **package holds the untouched downloaded terrain** and never changes after it is built.
- The **save holds sparse delta tiles:** height deltas and cover/clearing masks, in 64 m tiles, stored only where edited.
- Loading a save means base package + deltas. A save therefore never depends on write ordering, and one package can serve many saves.

### 4.4 Cover, splat and forest

- The four cover classes come from the package. Clearing edits are a mask delta.
- The terrain splat is computed from cover, slope, altitude and the snow preview, and updated by dirty rectangle.
- **Forest** (roadmap §9) is procedural and never saved:
  - Poisson-disc sampling per 64 m tile, seeded by `hash(resortSeed, tileX, tileY)`.
  - Density = forest fraction × slope mask × treeline falloff × (1 − clearing).
  - Edits regenerate only the affected tiles.

### 4.5 Snow without simulation

- There is no snow model. The mountain shows **stylized, static snow cover** driven by elevation, slope, aspect and the view clock's date.
- Trails and clearings get a groomed look.
- Question S3 in the review asks whether that is enough for now.

## 5. Data formats (T7)

**Resort package**
- Location: `<data>/Resorts/<packageId>/`, where `packageId` is a content hash.
- `manifest.json`: format version, site, UTM zone and origin, bounds, per-layer provenance (provider, dataset version, request date), licence and attribution text, per-file hashes.
- Binary layers:
  - core and ring heights: float32, little-endian, row-major
  - cover classes: 1 byte per cell
  - imagery: compressed tiles
  - vectors: compact JSON
- Validated on build and on open.

**Save**
- One `.mpsave` file: a zip, store-only for already-compressed entries. Entries:
  - `manifest.json`: save format version, game version, package ID and hash, created/updated times, view time
  - `design.json`: the design document; human-readable, stable IDs, versioned
  - `terrain-delta.bin` and `cover-delta.bin`: sparse 64 m tiles
  - `thumbnail.png`
- **Atomic write:** temp file, flush, rename. Keep the previous save as `.bak`.
- **Autosave** runs off the main thread from an immutable snapshot, so it never blocks input.

**Serialization**
- **Newtonsoft.Json** (MIT, via Unity's `com.unity.nuget.newtonsoft-json` package) for the manifests and the design document.
- Hand-written readers and writers for binary grids.
- No reflection-based binary serializers; they are brittle under IL2CPP and across versions.

**Versioning:** every format carries an integer version; readers migrate older versions with pure, fixture-tested functions. Question T7-Q asks when the compatibility promise starts.

## 6. Data acquisition and provider terms (T8, T9)

**Pipeline:** select site (square 2–10 km box) → download core elevation and ring → imagery → land cover → vector context → build package → validate alignment and hashes → add to library.
- Runs off the main thread with progress, cancellation, retries with backoff, per-provider rate limits and an identifying User-Agent.
- Each stage caches its result, so a failed run resumes rather than restarts.
- **Trust the extent the provider returns** (0.1 §3).
- The same engine-free code runs as a command-line tool that builds the checked-in test terrain used by tests (§9).

**Providers**, with terms checked September 2026:

| Data | Provider | Coverage | Terms | Recommendation |
|---|---|---|---|---|
| Core and ring elevation | USGS 3DEP ImageServer `exportImage` (request in UTM with `imageSR`; verify in Phase 1) | US | Public domain | **Keep** |
| Worldwide elevation | Copernicus DEM GLO-30 (AWS open data) | Global, 30 m | Free incl. commercial; attribution notice required | Option if worldwide is chosen |
| Imagery | USGS NAIP ImageServer | Contiguous US | Public domain | **Keep** |
| Land cover | ESA WorldCover 10 m, **classified GeoTIFF tiles** (AWS open data) instead of the archive's coloured WMTS tiles | Global | CC BY 4.0 | **Keep; switch access method** |
| Roads, streams, lakes | OpenStreetMap via Overpass (configurable endpoint) | Global | ODbL: attribution; public instances have fair-use limits | **Keep** |
| Place search | Nominatim | Global | Maximum 1 request/s, user-triggered only, no autocomplete, attribution | Keep for an explicit search box |
| Site-picker map | Cesium for Unity + Cesium ion | Global | Community tier free only under $50K revenue or funding; commercial $149/month; embedding ion in a product sold to others needs an integration licence | **Reject** for a commercial game |
| Site-picker map | USGS National Map basemap tiles (imagery, topo) in a UI Toolkit slippy map | US | Public domain | **Recommended** |
| Site-picker map | OpenStreetMap Foundation tile servers | Global | Policy forbids bulk or prefetch use and expects light usage | Reject for a shipped product |
| Display basemaps used by the archive | Esri World Imagery, CARTO | Global | Esri needs an ArcGIS account token and restricts commercial use; CARTO needs a key, free commercial up to 1M requests/month | **Drop** |
| Weather history (future) | Daymet; NASA POWER | North America; global | Free; citation requested | Out of scope now |

**T9, coverage:**
- USGS elevation and NAIP imagery make the best packages US-only.
- **Recommendation:** US-only for now, which matches the archive.
- Keep a provider interface so a worldwide set (Copernicus DEM + WorldCover + a licensed imagery source) can be added.
- This is question S2 in the review.

**GeoTIFF:**
- Decode with BitMiracle **LibTiff.NET** (New BSD, .NET Standard 2.0).
- Add a small reader for the GeoTIFF tags we need (tie point, pixel scale, geo keys).
- Pin the package version.

## 7. Determinism (T10)

In a painter, determinism means **the same inputs always produce the same outputs** for everything that is computed rather than stored. That covers grading, earthwork volumes, clearings, the network graph and the forest. It is what makes derived data safe to regenerate on load, and what makes golden tests possible.

**Rules for engine-free code and persisted or compared Burst output:**
- Keyed hash randomness only. No `System.Random`, `UnityEngine.Random`, `Guid.NewGuid`, `DateTime.Now` or `Parallel.For` reductions.
- Stable iteration order: no enumeration of `Dictionary` or `HashSet` in outputs without sorting.
- Burst jobs whose output must be reproducible (forest positions) use `FloatMode.Deterministic`, which Burst now supports on 64-bit platforms. Purely visual jobs use the default mode.
- Volumes are reported rounded (0.1 m³), so the numbers the player reads are stable.

**Scope:** same build, Windows x64. Cross-platform bit-equality is not required while the game is single-player.

**Tests:** golden fixtures for grading sections, pond/dam surfaces, cut/fill totals and forest tile hashes on the checked-in test terrain (§9).

The simulation-specific rules (fixed tick, integer money, golden trajectories) are recorded in roadmap §5 and §7 and activate with simulation (§10).

## 8. Performance budgets (T11)

**Frozen before features are built.** Measured with Unity's Performance Testing package and profiler recorders, in a benchmark scene with a fixed camera path. Results are recorded as JSON with the commit SHA.

| Budget | Target | Conditions |
|---|---|---|
| Frame time | p95 ≤20 ms, p99 ≤33.3 ms, <1% of frames over 50 ms | 1080p, High preset, reference PC; Phase 1 scene with full forest (~650k trees on 25 km²) and all infrastructure |
| Integrated GPU | ≥30 FPS | 1080p, Performance preset; reference device to be chosen (review question T11-Q) |
| Input, selection, tool response | p95 ≤100 ms | Any preset |
| Grading preview visible | ≤100 ms after input settles | 2 km trail |
| Commit | ≤250 ms including splat, mask and forest-tile patches | 2 km trail |
| Garbage collection | 0 bytes allocated per frame in steady state | Camera moving, no tool active |
| Open a resort | ≤10 s from a local package on SSD | 10 km site |
| Save | ≤1 s, never blocking input | Typical design |
| Memory | Provisional: ≤4 GB RAM, ≤3 GB VRAM at High; ≤1.5 GB VRAM at Performance | 10 km site |

**Reference PC:** Ryzen 5 5600X, about 16 GB RAM, RTX 3060 Ti. This is the archive's measurement machine.

## 9. Testing (T12)

- **Most tests are engine-free EditMode tests:** geometry, earthwork, document and commands, formats and migrations, acquisition parsing (from recorded responses, never live), and golden fixtures.
- **PlayMode:** scene boot, the tool flow from preview to confirm, save/load round trips, `TerrainData` staying in sync with the authoritative grid.
- **Performance tests:** opt-in and hardware-tagged.
- **Live-provider tests:** opt-in only.
- **Checked-in test terrain:** a small real area (for example 2 km) built by the acquisition CLI and committed through Git LFS. It makes tests reproducible offline and gives Phase 1 its test area (roadmap §6).
- **Continuous integration:**
  - Now: `repo-checks`.
  - **Recommended next:** a `tools/domain-tests` .NET project that compiles the engine-free assemblies' source files and runs their tests with `dotnet test` on GitHub Actions. That needs no Unity licence and takes seconds.
  - A GameCI Unity run needs licence secrets; Personal-licence activation in CI has changed several times, so verify it before relying on it.
- **Guard rails:** a repo check that fails if engine-free folders use banned APIs (§7) or `UnityEngine`.

## 10. The simulation placeholder (T13)

**What exists now:** `MountainPlanner.Simulation` contains only:

```csharp
public readonly struct ViewTime { public readonly int Year, DayOfYear, SecondOfDay; } // no DateOnly in Unity's .NET profile
public interface IGameClock { ViewTime Now { get; } event Action<ViewTime> Changed; }
public sealed class ManualViewClock : IGameClock { /* set by a UI scrubber; never advances on its own */ }
```

- **Presentation** reads `IGameClock` for sun position, lighting and the snow preview date.
- **Nothing advances time.** There is no tick, no speed control and no simulation thread.

**Constraints kept so simulation can be added later without rework:**
- The domain stays engine-free.
- All changes are commands against revisions.
- Derived data is rebuilt, not saved.
- Randomness is keyed.
- Time, when it arrives, is an `int64` count of simulated milliseconds.

When simulation returns, a real clock implements `IGameClock` on a dedicated thread. Roadmap §5 and §7 then become the active rules.

## 11. Packages for Phase 1 (T14)

- **Keep:**
  - URP 17.3, Input System, Test Framework and the IDE integrations.
- **Add:**
  - Performance Testing, Burst, Collections, Mathematics, Splines and Newtonsoft JSON.
  - LibTiff.NET as a pinned precompiled DLL in the Acquisition assembly.
- **Remove until needed:**
  - AI Navigation: guests will use the network graph, not NavMesh.
  - Timeline.
- **Not now:** Entities / Entities Graphics. Forest and instancing use `RenderMeshIndirect` or `BatchRendererGroup` directly; revisit only if measurements demand it.

## 12. Risks

- **Unity Terrain may not give the look or the grading precision** at 2 m. The fallback is a custom mesh behind `ITerrainSurface` (Phase 1 decides).
- **Forest scale** (~650k trees) on integrated GPUs. Mitigated by impostors, culling and the Performance preset; buy-versus-build is evaluated in Phase 1 (roadmap §9).
- **Provider endpoints change.** Mitigated by recorded-response tests, configurable endpoints and cached packages.
- **Scope creep back toward simulation** before the painter is solid. Mitigated by the placeholder boundary in §10.
