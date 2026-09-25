# Phase 0 · 0.3 Technical architecture

**Audience:** the project owner and coding agents. **Status:** draft for review (revised 2026-09-25 with the owner's first answers). Decisions are numbered **T1–T16**; comment in [phase0-review.md](phase0-review.md). Inputs: the [roadmap](unity-rebuild-roadmap.md) and the [reference inventory](phase0-0.1-reference-inventory.md).

## 1. Scope

**Iteration 1 is just the mountain:**
1. Pick a real site (a **square of 2–5 km**) on a world map.
2. Download its elevation from USGS 1 m lidar, specifically the **Seamless 1-meter DEM (S1M)**.
3. The game renders it with its own **lighting, camera, ground cover, forest and snow**.

**Offline after the first download.** Downloading may take a while, and a resort may use **about 1 GB on disk**. After that, opening and playing a resort needs no internet. Only the site picker, which browses and downloads new sites, is online.

**Future iterations are planned, not built:**
- **Drawing on the terrain:** lifts, trails and the other tools recorded in 0.1 §6.
- **Simulation:** 0.1 §7.

§10 lists what iteration 1 does now so those fit without rework. **Deferred to other deliverables:** game design (0.2), UI (0.4), art (0.5).

## 2. Assemblies (T1)

| Assembly | Engine refs | References | Holds |
|---|---|---|---|
| `MountainPlanner.Domain` | None (`noEngineReferences`) | — | Identifiers, units, georeferencing, grids, keyed hash randomness, the resort model. Later: the design document and construction math |
| `MountainPlanner.Simulation` | None | Domain | **Placeholder:** the clock interface and a manual view clock (§10) |
| `MountainPlanner.Persistence` | None | Domain | Resort-package, cache and library formats, versioning. Later: saves |
| `MountainPlanner.Acquisition` | None | Domain, Persistence | Provider clients, the Cloud Optimized GeoTIFF reader, package building and validation |
| `MountainPlanner.World` | Yes | Domain, Simulation, Persistence | Unity Terrain tiles, splat and masks, forest generation (Burst), water, spatial queries |
| `MountainPlanner.Presentation` | Yes | World (+ below) | Terrain and snow materials, forest rendering, sky and lighting, camera |
| `MountainPlanner.UI` | Yes | Presentation (+ below) | UI Toolkit screens, site picker, themes, view models |
| `MountainPlanner.App` | Yes | All | Bootstrap, scene flow, input routing, background-task host |

**Rules:**
- References only point down the table, never up.
- Engine-free assemblies use the .NET base library plus approved precompiled DLLs only (Newtonsoft.Json).
- **Migration from the M0 skeleton:** rename `Simulation` to `Domain`, re-add `Simulation` as the placeholder, add `Persistence`, `Acquisition` and `App`, extend the architecture tests, and update `AGENTS.md`, all in one PR.
- **Why engine-free matters:** the core is unit-testable in milliseconds, even outside Unity (§9); acquisition doubles as a command-line tool; the future tools and simulation build on the same foundation.

## 3. Runtime structure (T2)

**State in iteration 1:**
- The **resort package**: downloaded source data, immutable.
- Its **terrain cache**: Unity-ready data, regenerable (§5).
- A small **view state** per resort: camera, bookmarks, view time, display settings.
- Nothing the player makes needs saving yet.

**Threading:**
- **Main thread:** Unity scene, input, UI, handing finished data to `TerrainData` and GPU buffers.
- **Background .NET tasks,** with progress and cancellation: downloads, decoding, package and cache building, loading.
- **Burst jobs:** splat, masks and forest instances.
- **No simulation thread.**

**Source data vs derived data:** the source layers are the authority. Everything derived from them (terrain tiles, splat, forest) can be rebuilt from them at any time. It may be *cached* on disk for speed, but it is never the only copy of anything.

## 4. World representation

### 4.1 Coordinates: keep S1M's native grid (T3)

**The data:**
- S1M uses **NAD83(2011) CONUS Albers Equal Area (EPSG:6350)**, with metre units, 1 m pixels, and tile corners on whole kilometres.
- Heights are **NAVD88 (GEOID18) metres**.

**Decision:**
- The package uses the **same grid**, so the lidar heights are **never resampled**.
- Unity world `x` = Albers x − x₀ and `z` = Albers y − y₀, with a local origin at the site centre; `y` = elevation; one unit is one metre.
- Albers scale error is within about ±1% across the contiguous US and effectively constant across one site. The manifest stores the site's scale factors for future measuring tools; rendering ignores them.
- Other layers (canopy, land cover, water) are reprojected onto this grid at download time.
- **Rejected alternative:** UTM, which would resample and blur the lidar.

### 4.2 Elevation extent and resolution (T4)

- **Site:** a square of **2–5 km** (decided), at full **1 m** resolution. A 5 km site is 25 million heights.
- **Surround ring:** 3 km beyond the site on every side (so a 5 km site sits in an 11 km square). It uses S1M's built-in **2 m** copy: same source, same datum, no seam. The 1 GB budget allows 2 m rather than the 8 m in the first draft.
- **Coverage:**
  - S1M production is still in progress.
  - **Recommendation** (open question T4-Q2): the picker only allows sites fully inside S1M coverage, and draws the coverage on the map.
  - Ring gaps fall back to USGS 3DEP 1/3 arc-second (about 10 m).

### 4.3 From lidar to Unity terrain (T4)

This is the full path, from USGS files to what the player sees.

```
USGS S3: S1M Cloud Optimized GeoTIFF tiles (10 km, 1 m, float32)
   │ ① range-read only the 512² blocks we need (core: 1 m level; ring: 2 m level)
   ▼
Decode: LZW → floating-point predictor → float32 metres; stitch blocks across tiles; fill voids
   │ ② write the authoritative grids (lossless)
   ▼
Resort package:  heights-core.f32 (1 m)  +  heights-ring.f32 (2 m)  + manifest
   │ ③ once, after download (and whenever the cache version changes)
   ▼
Terrain cache: 1,024 m tiles → 16-bit heightmaps (core 1,025², ring 513²), control/splat maps
   │ ④ on opening a resort
   ▼
Unity: one Terrain per tile (TerrainData + neighbours), terrain material, forest, water
```

**① Download.**
- A 5 km site touches up to four 10 km S1M tiles, depending on where it falls.
- The game reads the files' internal directories, then fetches only the **512 × 512 blocks** that overlap the site (1 m level) and the ring (2 m level), using HTTP range requests.
- **Estimate:** about 100 blocks for the core and a similar volume for the ring, roughly 100–200 MB compressed. Phase 1 measures it.
- Transfers are resumable per block.

**② Decode and store.**
- A small built-in reader handles exactly what S1M uses: TIFF directories, tile offsets, **LZW** decompression and the **floating-point predictor**. Output is float32 metres.
- Blocks from neighbouring USGS tiles are stitched into one continuous grid; the tiles share one grid, so there is no resampling.
- **No-data cells** (−999999):
  - Small voids are filled by interpolation.
  - The core must have none after filling, or the site is refused.
  - The ring falls back to the 10 m product.
- The result is saved **losslessly as float32**. This is the authoritative terrain; future drawing tools edit on top of it (§10).

**③ Build the terrain cache** (once per resort, a few seconds to a minute; Phase 1 measures):
- Split the grids into **1,024 m tiles on the Albers kilometre grid.**
  - Core tiles hold 1,025 × 1,025 heights (1 m).
  - Ring tiles hold 513 × 513 heights (2 m).
  - Neighbouring tiles share their edge row, so there are no cracks.
  - Where a 2 m ring tile meets a 1 m core tile, the core edge is matched to the ring's samples.
- **Heights to 16-bit:** Unity Terrain stores heights as 16-bit fractions of a height range. Heights are mapped over the site's lowest-to-highest range. With 1,500 m of relief that is **2.3 cm steps**, well below lidar's own vertical accuracy (about 10 cm), so nothing visible is lost. The float32 source keeps full precision for later.
- Also cached per tile: the splat/control maps (from cover and slope) and the cover and canopy grids at tile resolution.
- **Size:** a 5 km site in an 11 km square makes 121 tiles: 25 core and 96 ring.

**④ Load.**
- One Unity `Terrain` object per tile. Heights go in through `TerrainData` (the fast GPU path where possible, `SetHeights` otherwise).
- Neighbours are linked so level of detail blends across tile edges.
- Unity Terrain's built-in quadtree level of detail and draw-instancing render the whole area; each quality preset sets the pixel-error tolerance.
- The terrain material, the forest (§4.5) and water surfaces are then built on top.
- **Target:** ≤10 s from opening to a playable view (§8).

**Why Unity Terrain:**
- It already does level of detail, instancing, normal calculation, collision and height editing, which are exactly what future drawing tools need.
- Everything sits behind an `ITerrainSurface` interface, so a custom mesh can replace it if the look demands it (roadmap §8).

### 4.4 Ground cover: sources (T6)

| Class | Source | Resolution | Terms |
|---|---|---|---|
| **Forest** (plus tree height) | **Meta / WRI High Resolution Canopy Height** (AWS open data, GeoTIFF). Forest = canopy ≥3 m | 1 m | CC BY 4.0, commercial use allowed |
| Grassland, alpine/rock, snow/ice | **ESA WorldCover 2021 (v200)** classified tiles (AWS open data) | 10 m | CC BY 4.0 |
| **Developed** (decided) | WorldCover "built-up" + OpenStreetMap roads and buildings for crisp shapes | 10 m + vectors | CC BY 4.0; ODbL |
| Water | OpenStreetMap lakes and streams, checked against WorldCover water | Vectors | ODbL |

**Why this combination:**
- The canopy map gives the forest the **same 1 m detail as the lidar**, plus a real **tree height** for every cell, which the forest generator uses (§4.5).
- WorldCover alone is 10 m, which looks blocky next to 1 m terrain; it fills in the non-forest classes.
- It replaces the archive's WorldCover + NAIP imagery recipe, so NAIP is **not needed** in iteration 1.

**Cautions:**
- The canopy map is built from satellite imagery of the 2010s. The package records the source date, and a **sanity check** compares its forest fraction with WorldCover's; the archive's lidar-forest failure showed why (0.1 §5).
- **US-only, public-domain alternative:** USGS Annual NLCD at 30 m. It is coarser; kept as a fallback only.

### 4.5 Forest (T7)

- **Placement:** Poisson-disc sampling per 64 m tile, seeded by `hash(resortSeed, tileX, tileY)`. Deterministic, never saved (it can be cached).
- **Density** comes from canopy cover and slope. **Tree size comes from canopy height**, so tall old stands and short regrowth look different. Species vary by altitude and aspect.
- **Rendering:** GPU-culled `RenderMeshIndirect` or `BatchRendererGroup`; LODs down to an impostor beyond about 300 m; vertex wind; snow on branches.
- **Estimate:** about 650,000 trees on a 5 km site at 65% forest, plus the ring. Ring trees use impostors only.
- **Later:** a clearing mask (for trails and lift lines) multiplies into density, so drawing tools remove trees tile by tile.

### 4.6 Snow (T8)

- **Iteration 1 (decided):** a **flat 12 in (0.305 m) of snow across the entire map**: every land cell white, trees carrying snow. There is no snow model and no variation.
- **The seam for later:**
  - The terrain shader reads snow from a **snow-depth texture**. Iteration 1 fills it with a constant 0.305 m.
  - Varied snow (snow line, sun and wind effects) or a future simulation writes the same texture by dirty rectangle.
  - Nothing else changes when that happens.
- **Open (T8-Q):** whether lakes show frozen and snow-covered, or as open water.

### 4.7 Lighting and camera (T9)

- **Sun:** position from the site's latitude and longitude and the view time (NOAA solar-position algorithm). One directional light, and a sky that follows the sun.
- **Shadows:** cascaded. Tree shadows only in the near cascade, with a baked canopy-shadow term for distance.
- **View time:** a time-of-day and date scrubber. It drives the sun only; snow stays flat in iteration 1. This is the clock placeholder (§10).
- **Camera:** orbit / RTS-style with terrain collision, bounded to the ring, plus free-fly. Default keys follow the archive (W/A/S/D, Q/E, R/F, N); 0.4 finalises them.

## 5. Package, cache and library (T5, T11)

**Resort package: immutable source data, written once.**
- Location: `<data>/Resorts/<packageId>/`, where `packageId` is a content hash.
- `manifest.json`:
  - format version
  - site
  - EPSG:6350, origin and scale factors
  - per-layer provenance (provider, product, tile IDs and dates)
  - **attribution text**
  - per-file hashes
- Layers:
  - `heights-core.f32` and `heights-ring.f32`
  - `canopy.u8` (height in 0.25 m steps)
  - `cover.u8`
  - `water.json` and `developed.json`

**Terrain cache:** `<data>/Resorts/<packageId>/cache-v<N>/`, holding per-tile 16-bit heightmaps, control maps and forest tile data. It is rebuilt automatically if missing or if the cache version changes.

**Estimated size, 5 km site (budget about 1 GB):**

| Item | Size |
|---|---|
| Core heights, 1 m float32 | 100 MB |
| Ring heights, 2 m float32 | 96 MB |
| Canopy and cover grids (1 m core, 2 m ring) | 100 MB |
| Water, developed, manifest | <10 MB |
| Terrain cache: 16-bit tiles (~100 MB), control/splat maps (~200 MB), forest (~20 MB) | ~320 MB |
| **Total** | **about 650 MB** |

**Offline:** the package holds everything the game needs to open a resort, including attribution text for the credits screen. Opening a resort makes **no network calls**.

**Library:** downloaded resorts with name, location, size on disk and a thumbnail. Deletion needs confirmation.

**View state:** a small versioned JSON per resort.

**Serialization:** Newtonsoft.Json (MIT, via `com.unity.nuget.newtonsoft-json`); hand-written binary readers and writers for grids.

**Versioning:** integer versions everywhere. Formats may change freely until the first build shared with other players; after that, fixture-tested migrations.

## 6. Acquisition and provider terms (T10)

**S1M access** (checked 2026-09-25):
- Public S3: `prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/S1M/`. Each tile is a 10 km × 10 km **Cloud Optimized GeoTIFF**, 200–450 MB.
  - LZW compression with a floating-point predictor, in 512² internal blocks.
  - Overviews at 2, 4, 8, 16 and 32 m. No data is −999999.
- Names encode the Albers corner in kilometres: `n0470e1490` starts at x = 1,490,000 m, y = 470,000 m.
- A daily coverage index: `FullExtentSpatialMetadata/S1M_Products.gpkg`.
- Public domain.

**Coverage lookup:**
- The picker needs to know where S1M exists. **Recommended:** a coverage map built from the daily index, fetched when the picker opens and cached.
- The index is a SQLite-based file, so the Phase 1 spike decides whether to read it with a small SQLite library or convert it.
- A direct check (listing the tile's S3 folder) confirms a tile before download.

**Pipeline:**
1. Choose a covered site.
2. Download core and ring heights.
3. Download canopy.
4. Download land cover.
5. Download OSM water and developed shapes.
6. Build the package.
7. Validate alignment and hashes.
8. Build the terrain cache.
9. Add it to the library.

It runs off the main thread with progress, cancellation, retries, polite rate limits and an identifying User-Agent, and resumes after failures. The same code runs as a command-line tool.

**Providers:**

| Data | Provider | Terms | Use |
|---|---|---|---|
| Elevation | USGS 3DEP **S1M** | Public domain | **Yes** |
| Ring gap fill | USGS 3DEP 1/3 arc-second (3DEP ImageServer) | Public domain | Ring gaps only |
| Forest, tree height | Meta / WRI High Resolution Canopy Height (AWS) | CC BY 4.0 | **Yes** |
| Other land cover | ESA WorldCover 2021 v200 (AWS) | CC BY 4.0 | **Yes** |
| Water, roads, buildings | OpenStreetMap via Overpass (configurable endpoint) | ODbL; fair-use limits | **Yes** |
| Place search | Nominatim | Maximum 1 request/s, user-triggered, no autocomplete, attribution | Search box |
| Picker basemap | USGS National Map tiles (imagery, topo) in a UI Toolkit slippy map, with an S1M coverage overlay | Public domain | **Recommended** |
| Fallback land cover | USGS Annual NLCD 30 m | Public domain | Fallback only |
| Not used | Cesium ion; Esri World Imagery; CARTO; OSM Foundation tiles; NAIP (not needed now) | Commercial or usage restrictions (see first draft) | No |

**Coverage is US-only** (contiguous US now; Hawaii and Puerto Rico are planned by USGS).

## 7. Determinism (T12)

Every open of a resort must produce the same terrain tiles, splat and forest, which is what makes caching and future golden tests safe.

**Rules:**
- Keyed hash randomness only. No `System.Random`, `UnityEngine.Random`, `Guid.NewGuid`, `DateTime.Now` or unordered parallel reductions.
- Stable iteration order.
- `FloatMode.Deterministic` for Burst jobs with reproducible output (forest placement); supported on 64-bit.

**Scope:** same build, Windows x64. **Tests:** golden hashes on the checked-in test terrain.

## 8. Performance budgets and hardware (T13)

**Hardware:**
- **Minimum spec (decided: around an RTX 2060):** NVIDIA RTX 2060 (6 GB) with a Ryzen 5 3600 / Core i5-9600K-class CPU, 16 GB RAM and an SSD. The CPU and RAM are open question T13-Q.
- **Reference PC:** RTX 3060 Ti, Ryzen 5 5600X, about 16 GB RAM (the archive's measurement machine).

| Budget | Target | Conditions |
|---|---|---|
| Frame time, reference PC | p95 ≤20 ms, p99 ≤33.3 ms, <1% over 50 ms | 1080p, High preset, 5 km site + ring, full forest, snow, shadows |
| Frame time, minimum spec | p95 ≤33.3 ms (30 FPS or better) | 1080p, Standard preset, same scene |
| VRAM | ≤5 GB at Standard; ≤7 GB at High | 5 km site |
| RAM | ≤8 GB process | 5 km site |
| Camera and input response | p95 ≤100 ms | Any preset |
| Garbage collection | 0 bytes per frame in steady state | Camera moving |
| Open a downloaded resort | ≤10 s | SSD, cache present |
| First download and build | No hard limit; show progress and a time estimate; resumable | 5 km site |
| Disk per resort | ≤1 GB | 5 km site |

Measured with Unity's Performance Testing package in a benchmark scene with a fixed camera path. Results are recorded as JSON with the commit SHA.

## 9. Testing (T14)

- **Most tests are engine-free EditMode tests:** georeferencing, the COG reader (LZW, predictor, block stitching, voids), package and cache formats, 16-bit conversion error bounds, cover and canopy processing, forest placement, solar position. Provider responses are **recorded, never live**.
- **PlayMode:** scene boot; opening a resort; tile seams (no cracks, matched edges); `TerrainData` matching the authoritative grid within the 16-bit step.
- **Checked-in test terrain:** a real 2 km S1M site built by the acquisition tool and committed through Git LFS. It is public domain, works offline, and is Phase 1's test area.
- **Performance and live-provider tests:** opt-in only.
- **Continuous integration:**
  - Now: `repo-checks`.
  - **Recommended next:** a `dotnet test` job for the engine-free code, which needs no Unity licence.
  - A GameCI Unity run needs licence secrets; verify Personal-licence activation first.
- **Guard rail:** a repo check that fails if engine-free folders use banned APIs (§7) or `UnityEngine`.

## 10. Future iterations and the seams iteration 1 keeps (T15)

**Drawing on the terrain** (lifts, trails and the rest):
- A revisioned design document: immutable snapshots, typed commands against a named revision, stale commands rejected. This also makes undo cheap.
- Terrain and cover edits are stored in a small **save file as sparse 64 m delta tiles** over the immutable package; the affected cache tiles are rebuilt.
- 0.1 §6 and §9 are the starting reference.
- **What iteration 1 does now for this:**
  - The authoritative float32 heights are separate from Unity's 16-bit view.
  - Forest density already takes a clearing mask (empty for now).
  - The package is immutable.
  - The tile grid is also the edit granularity.

**Simulation:**
- A real clock implementing `IGameClock` on a dedicated thread; snapshots out, commands in; roadmap §5 and §7 become the active rules; the simulation writes the snow-depth texture.
- **What iteration 1 does now for this:** the placeholder clock, the snow-depth texture seam, keyed randomness and the engine-free domain.

**The clock placeholder** (`MountainPlanner.Simulation`):

```csharp
public readonly struct ViewTime { public readonly int Year, DayOfYear, SecondOfDay; } // no DateOnly in Unity's .NET profile
public interface IGameClock { ViewTime Now { get; } event Action<ViewTime> Changed; }
public sealed class ManualViewClock : IGameClock { /* set by the time-of-day/date scrubber; never advances on its own */ }
```

## 11. Packages for Phase 1 (T16)

- **Keep:** URP 17.3, Input System, Test Framework and the IDE integrations.
- **Add:** Performance Testing, Burst, Collections, Mathematics and Newtonsoft JSON.
- **Remove until needed:** AI Navigation and Timeline. Splines returns with the drawing tools.
- **Not now:** Entities / Entities Graphics.

## 12. Risks

- **S1M coverage is incomplete.** Mitigated by showing coverage in the picker, only allowing covered sites, and the 10 m ring fallback.
- **S1M layout or index changes** during production. Mitigated by one provider module and recorded-response tests.
- **Canopy map age or local errors.** Mitigated by recording the date, the WorldCover sanity check and the NLCD fallback.
- **1 m terrain cost on the minimum spec.** Mitigated by the 2–5 km limit, 2 m ring tiles, per-preset pixel error and Phase 1 measurement.
- **Unity Terrain look at 1 m.** Fallback: a custom mesh behind `ITerrainSurface`.
- **Forest at scale on the minimum spec.** Mitigated by impostors, culling and presets.
