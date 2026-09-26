# Phase 0 · 0.3 Technical architecture

**Audience:** the project owner and coding agents. **Status:** approved 2026-09-25. Decisions are numbered **T1–T19** and summarized in [phase0-decisions.md](phase0-decisions.md). Inputs: the [roadmap](unity-rebuild-roadmap.md) and the [reference inventory](phase0-0.1-reference-inventory.md).

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
- **Fallback to the next-best data (decided; verified 2026-09-25):**
  - **Inside a published S1M tile, USGS has already done the fallback.** Voids wider than 10 m are backfilled from 3DEP 1/9 arc-second (about 3 m) or 1/3 arc-second (about 10 m) with a 50 m slope-weighted blend; smaller voids are interpolated. What remains as no-data is only where USGS has nothing at all.
  - **Where no S1M tile exists yet, or no-data remains, the game fills from the USGS 3DEP dynamic elevation service** (`3DEPElevation/ImageServer`). It mosaics every published 3DEP elevation product: project-based 1 m lidar where S1M has not been produced yet, otherwise 1/9 or 1/3 arc-second. It returns up to 8,000 × 8,000 pixels at 1 m per request and can deliver directly in EPSG:6350.
  - **Chain, per 1 km tile:** S1M (1 m) → 3DEP 1 m project lidar → 1/9 arc-second → 1/3 arc-second. The last covers the whole contiguous US, so **every site gets complete terrain**.
  - Where an S1M tile meets fallback data, the edge gets the same 50 m slope-weighted blend USGS uses, so there is no step.
  - **The manifest records the source and effective resolution of every tile.** The picker shows a data-quality overlay (1 m S1M / 1 m lidar / 3 m / 10 m) and warns before downloading a site that is not all 1 m.
  - The Phase 1 spike verifies that the dynamic service picks the finest source at a location, and reads which source it used.

**Data-quality score after download (T18, owner request).** When a download finishes, the game shows a terrain quality score and a one-line summary of the data used. Both are stored in the manifest and shown on the library card.
- **Score (0–100):** the core site's area-weighted source quality: S1M 1 m = 100, 1 m project lidar = 95, 1/9 arc-second (about 3 m) = 60, 1/3 arc-second (about 10 m) = 30. The ring is listed but not scored, because it is scenery.
- **Example one-liner:** *"Terrain quality 97/100: 96% USGS S1M 1 m lidar, 4% 3DEP 10 m · forest: Meta/WRI canopy (2019 imagery) · cover: ESA WorldCover 2021."*
- The weights are a first proposal and can be tuned.

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
- **No-data cells** (−999999), and 1 km tiles with no S1M file: filled from the fallback chain in §4.2, blended at the seams. The finished grid has no holes.
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
| **Forest** (plus tree height) | **Meta / WRI High Resolution Canopy Height** (AWS open data, GeoTIFF). Forest = canopy ≥3 m; the primary forest layer (D4) | 1 m | CC BY 4.0, commercial use allowed |
| Grassland, alpine/rock, snow/ice | **ESA WorldCover 2021 (v200)** classified tiles (AWS open data) | 10 m | CC BY 4.0 |
| **Developed** (decided) | WorldCover "built-up" + OpenStreetMap roads and buildings for crisp shapes | 10 m + vectors | CC BY 4.0; ODbL |
| Water | OpenStreetMap lakes and streams, checked against WorldCover water | Vectors | ODbL |

**Why this combination:**
- The canopy map gives the forest the **same 1 m detail as the lidar**, plus a real **tree height** for every cell, which the forest generator uses (§4.5).
- WorldCover alone is 10 m, which looks blocky next to 1 m terrain; it fills in the non-forest classes.
- It replaces the archive's WorldCover + NAIP imagery recipe, so NAIP is **not needed** in iteration 1.

**Ground cover must never look blocky (owner requirement).** WorldCover's 10 m cells must not show as squares on 1 m terrain. How:
- **Forest** comes from the 1 m canopy map, so it is already at terrain resolution.
- **Soft class weights, not hard classes.** Each 10 m class is turned into a per-class weight (1 inside, 0 outside) and upsampled smoothly to 1 m. The terrain blends layers by weight rather than switching at a cell edge.
- **Terrain-aware boundaries.**
  - Slope and elevation from the 1 m lidar decide rock versus grass locally: rock on steep faces, alpine above the treeline.
  - Fine noise breaks any remaining straight 10 m edges.
  - The rules are deterministic (§7).
- **Vectors are rasterized at 1 m with anti-aliasing:** OpenStreetMap water, roads and buildings.
- **Height-based texture blending** in the terrain shader, so transitions look like grass giving way to rock, not a cross-fade.
- **Acceptance check:** a Phase 1 visual review at close camera range, including a test view where the snow layer is off. A slice with visible 10 m stair edges fails.

**Cautions:**
- The canopy map is built from satellite imagery of the 2010s. The package records the source date, and a **sanity check** compares its forest fraction with WorldCover's; the archive's lidar-forest failure showed why (0.1 §5).
- **Measured against lidar (data-spike report §6, D4):** the canopy map was 82% correct against WorldCover's 72%. It under-counts sparse and short trees (cover 18% against a true 27%) and reads heights low (median 7 m against 12 m). So a 10 m cell is forest if **any** canopy sample reaches 3 m, and density and height get calibration factors (about 1.5× and 1.5–1.7× at Jackson Hole), refined as more lidar truth sites are added.
- **US-only, public-domain alternative:** USGS Annual NLCD at 30 m. It is coarser; kept as a fallback only.

**Tree species (TR3).** Which species grow in each 30 m cell comes from the USFS **FIA BIGMAP** tree-species biomass layers: 30 m, 327 species, 2018 conditions, contiguous US. If BIGMAP is unavailable or unusable for an area, **LANDFIRE Existing Vegetation Type** (30 m, public domain) gives forest community types instead. The package stores a compact species table (top species and weights per 30 m cell). The data spike verifies BIGMAP's licence and a way to download just a site's area (it is distributed nationally, one file per species); LANDFIRE has a web service for that.

### 4.5 Forest (T7)

- **Placement:** Poisson-disc sampling per 64 m tile, seeded by `hash(resortSeed, tileX, tileY)`. Deterministic, never saved (it can be cached).
- **Density** comes from calibrated canopy cover and slope. **Tree size comes from calibrated canopy height** (D4), so tall old stands and short regrowth look different.
- **Species** are drawn per tree from the cell's BIGMAP species weights (TR3). Each species maps to a model in the species library (0.5 §3); unmapped species fall back to the nearest look-alike. Krummholz forms replace trees in the band just below the local treeline.
- **Rendering:** our own GPU-culled `RenderMeshIndirect` instancing and our own octahedral **impostor baker**; LODs down to an impostor beyond about 300 m. Paid renderers or impostor tools (GPU Instancer Pro, Nature Renderer Pro, Amplify Impostors) are used only with the owner's explicit OK (TR2); the free Nature Renderer 6 may be evaluated.
- **One tree shader (TR4)** for every species, with three inputs:
  - **wind:** trunk sway, branch bend and leaf flutter, from weights baked into vertex colours
  - **snow load:** 0–1, snow on upward-facing branch surfaces; fixed at full in iteration 1, and later driven by weather
  - **season:** 0–1, the leaf colour ramp plus leaf shrink or fade; winter in iteration 1, so deciduous trees and larches are bare
- Trees are built with **separate leaf geometry and materials**, so seasons need no new models later.
- **Estimate:** about 650,000 trees on a 5 km site at 65% forest, plus the ring. Ring trees use impostors only.
- **Later:** a clearing mask (for trails and lift lines) multiplies into density, so drawing tools remove trees tile by tile.

### 4.6 Snow (T8)

- **Iteration 1 (decided):** a **flat 12 in (0.305 m) of snow across the entire map**: every land cell white, trees carrying snow. There is no snow model and no variation.
- **Visual exception (A2):** faces steeper than about 55° render bare rock. The snow-depth data stays a flat 12 in; only the shader lets rock show through.
- **The seam for later:**
  - The terrain shader reads snow from a **snow-depth texture**. Iteration 1 fills it with a constant 0.305 m.
  - Varied snow (snow line, sun and wind effects) or a future simulation writes the same texture by dirty rectangle.
  - Nothing else changes when that happens.
- **Lakes (decided): frozen and snow-covered in iteration 1.**
  - Each lake and stream is a water body with a **surface state**: open water, ice, or snow-covered ice, plus ice and snow thickness.
  - Iteration 1 sets every water body to snow-covered ice; the renderer draws whatever the state says.
  - A future weather engine will freeze and thaw each lake by changing that state. The lake itself and the rendering do not change.

### 4.7 Lighting and camera (T9)

- **Sun:** position from the site's latitude and longitude and the view time (NOAA solar-position algorithm). One directional light, and a sky that follows the sun.
- **Shadows:** cascaded. Tree shadows only in the near cascade, with a baked canopy-shadow term for distance.
- **View time:** a time-of-day and date scrubber. It drives the sun only; snow stays flat in iteration 1. This is the clock placeholder (§10).
- **Camera:** orbit / RTS-style with terrain collision, bounded to the ring, plus free-fly. Default keys follow the archive (W/A/S/D, Q/E, R/F, N); 0.4 finalises them.

### 4.8 Map layers (T17)

Owner requirement: the player can toggle ground cover on and off.

| Layer | What it toggles | Default |
|---|---|---|
| Snow | The 12 in snow cover. With it off, the ground cover underneath shows | On |
| Ground cover | Terrain textured by cover class (forest floor, grass, rock, developed, water) versus plain shaded terrain | On |
| Forest | The trees | On |
| Cover map | A flat-colour overlay of the cover classes, for reading the data, like the old game's analysis layer | Off |
| Satellite imagery (nice to have, G7) | USGS NAIP aerial imagery (about 0.6 m, public domain) draped on the terrain in place of the stylized ground. Downloaded with the mountain if enabled in the picker (about 50–100 MB extra for 5 km) | Off |

**Performance:** toggling costs nothing noticeable. Layers are shader switches and render-list visibility, never a rebuild of terrain or forest.

**Note:** with 12 in of snow everywhere, the ground cover mostly shows through only when the Snow layer is off. The forest shows either way.

The old game's analysis layers (hillshade, contours, slope bands, aspect; 0.1 §4) are candidates for 0.4 (UI) to add later.

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
- Any site in the contiguous US can be downloaded (the fallback chain in §4.2 guarantees complete terrain). The picker shows a **data-quality overlay**, built from the daily S1M index and fetched when the picker opens, then cached. It warns before a site that is not all 1 m.
- The index is a SQLite-based file, so the Phase 1 spike decides whether to read it with a small SQLite library or convert it.
- A direct check (listing the tile's S3 folder) confirms a tile before download.

**Pipeline:**
1. Choose a site.
2. Download core and ring heights: S1M first, then fallback tiles.
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
| Fallback elevation | USGS 3DEP dynamic elevation service (`3DEPElevation/ImageServer`): 1 m project lidar, 1/9 and 1/3 arc-second | Public domain | **Yes**, wherever S1M is missing (core and ring) |
| Forest, tree height | Meta / WRI High Resolution Canopy Height (AWS) | CC BY 4.0 | **Yes** |
| Other land cover | ESA WorldCover 2021 v200 (AWS) | CC BY 4.0 | **Yes** |
| Water, roads, buildings | OpenStreetMap via Overpass (configurable endpoint) | ODbL; fair-use limits | **Yes** |
| Place search | Nominatim | Maximum 1 request/s, user-triggered, no autocomplete, attribution | Search box |
| Picker basemap | USGS National Map tiles (imagery, topo) in a UI Toolkit slippy map, with an S1M coverage overlay | Public domain | **Recommended** |
| Tree species | USFS FIA BIGMAP (30 m, 327 species) | Federal data; licence verified in the data spike | **Yes** |
| Tree species fallback | LANDFIRE Existing Vegetation Type (30 m) | Public domain | Fallback |
| Fallback land cover | USGS Annual NLCD 30 m | Public domain | Fallback only |
| Satellite imagery layer (nice to have) | USGS NAIP | Public domain | Optional layer (G7) |
| Not used | Cesium ion; Esri World Imagery; CARTO; OSM Foundation tiles; NAIP (not needed now) | Commercial or usage restrictions (see first draft) | No |

**Coverage is US-only** (contiguous US now; Hawaii and Puerto Rico are planned by USGS).

### 6.1 Site picker (T19)

**How it works (owner's design, 2026-09-25).** A pop-up window holding a small built-in map:
1. **Search** for a place, or pan and zoom the map.
2. **Size the square** with a slider, 2–5 km (0.1 km steps).
3. **Click the map** to centre the square on that point. Clicking again moves it.
4. **Name the map.** A name is required before downloading. It is prefilled with a suggestion (the nearest named peak or place), and the player can edit it.
5. **Download.** The picker shows the site's expected quality score and download size first (§4.2), then the download progress.

**Technical notes:**
- **Map engine:** a lightweight tile map built in UI Toolkit, not a third-party map SDK.
  - Shows USGS National Map imagery or topo tiles (public domain).
  - Dragging pans; the scroll wheel zooms.
  - Tiles are cached in memory, and on disk only as provider terms allow.
  - The picker is the only part of the game that needs internet.
- **The square is exact in real metres.** It is defined on the package grid (CONUS Albers) around the clicked centre, then drawn projected onto the map, so on screen it may look very slightly skewed. What you see is exactly what downloads.
- **Overlays:** S1M coverage and data quality (§6), and the square with its size label.
- **Search:** Nominatim, run only when the player presses Enter (no autocomplete), at most 1 request per second, with attribution shown. The name suggestion uses one reverse lookup when the square is placed.
- **Layout, wording and states:** designed and wireframed in 0.4 (UI/UX).

## 7. Determinism (T12)

Every open of a resort must produce the same terrain tiles, splat and forest, which is what makes caching and future golden tests safe.

**Rules:**
- Keyed hash randomness only. No `System.Random`, `UnityEngine.Random`, `Guid.NewGuid`, `DateTime.Now` or unordered parallel reductions.
- Stable iteration order.
- `FloatMode.Deterministic` for Burst jobs with reproducible output (forest placement); supported on 64-bit.

**Scope:** same build, Windows x64. **Tests:** golden hashes on the checked-in test terrain.

## 8. Performance budgets and hardware (T13)

**Hardware:**
- **Minimum spec (decided: around an RTX 2060):** NVIDIA RTX 2060 (6 GB) with a Ryzen 5 3600 / Core i5-9600K-class CPU, 16 GB RAM and an SSD (CPU and RAM decided).
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
- **Jackson Hole demo (G2, D1):** the 5 km site, built by the same tool and bundled into release builds for the demo and the menu background. At several hundred MB it is **not** committed to Git by default (LFS quota); the build script regenerates or caches it.
- **Crystal Mountain (5 km):** built locally, never committed. It is not currently in S1M, so it exercises the fallback path.
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
- **What iteration 1 does now for this:** the placeholder clock, the snow-depth texture seam, water-body surface states (frozen now; the weather engine later freezes and thaws them), the tree shader's snow-load and season inputs (TR4), keyed randomness and the engine-free domain.

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

- **S1M coverage is incomplete.** Mitigated by the automatic fallback chain, the data-quality overlay in the picker and the post-download quality score.
- **Seams between S1M and fallback data.** Mitigated by the 50 m slope-weighted blend; checked in the Phase 1 visual review.
- **S1M layout or index changes** during production. Mitigated by one provider module and recorded-response tests.
- **Canopy map age or local errors.** Mitigated by recording the date, the WorldCover sanity check, the NLCD fallback, and calibration against lidar truth sites (D4).
- **1 m terrain cost on the minimum spec.** Mitigated by the 2–5 km limit, 2 m ring tiles, per-preset pixel error and Phase 1 measurement.
- **Unity Terrain look at 1 m.** Fallback: a custom mesh behind `ITerrainSurface`.
- **Forest at scale on the minimum spec.** Mitigated by impostors, culling and presets.
