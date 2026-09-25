# Phase 0 · 0.3 Technical architecture

**Audience:** the project owner and coding agents. **Status:** draft for review (2026-09-25). Decisions are numbered **T1–T16**; comment on them in [phase0-review.md](phase0-review.md). Inputs: the [roadmap](unity-rebuild-roadmap.md) and the [reference inventory](phase0-0.1-reference-inventory.md).

## 1. Scope

**Iteration 1 is just the mountain:**
1. Pick a real site on a world map.
2. Download its elevation from USGS 1 m lidar, specifically the **Seamless 1-meter DEM (S1M)**.
3. The game renders it as a stylized 3D mountain, supplying the **lighting, camera, ground cover, forest and snow**.

There are no player drawing tools and no simulation.

**Future iterations are planned, not built:**
- **Drawing on the terrain:** ski lifts, trails and the other tools recorded in 0.1 §6.
- **Simulation:** time, weather, snow, guests (0.1 §7).

§10 says what iteration 1 must do now so these can be added without rework. Beyond that, this document only records their direction.

**Deferred to other deliverables:** game design (0.2), UI (0.4), art (0.5).

## 2. Assemblies (T1)

| Assembly | Engine refs | References | Holds |
|---|---|---|---|
| `MountainPlanner.Domain` | None (`noEngineReferences`) | — | Identifiers, units, georeferencing, grids, keyed hash randomness, the resort model. Later: the design document and construction math |
| `MountainPlanner.Simulation` | None | Domain | **Placeholder:** the clock interface and a manual view clock (§10) |
| `MountainPlanner.Persistence` | None | Domain | Resort-package and library formats, versioning. Later: saves |
| `MountainPlanner.Acquisition` | None | Domain, Persistence | S1M and other provider clients, the Cloud Optimized GeoTIFF reader, package building and validation |
| `MountainPlanner.World` | Yes | Domain, Simulation, Persistence | Unity Terrain tiles, splat and cover masks, forest generation (Burst), water surfaces, spatial queries |
| `MountainPlanner.Presentation` | Yes | World (+ below) | Terrain and snow materials, forest rendering, sky and lighting, camera |
| `MountainPlanner.UI` | Yes | Presentation (+ below) | UI Toolkit screens, site picker, themes, view models |
| `MountainPlanner.App` | Yes | All | Bootstrap, scene flow, input routing, background-task host |

**Rules:**
- References only point down the table, never up.
- Engine-free assemblies use the .NET base library plus approved precompiled DLLs only (Newtonsoft.Json).
- **Migration from the M0 skeleton:** rename `Simulation` to `Domain`, re-add `Simulation` as the placeholder, add `Persistence`, `Acquisition` and `App`, extend the architecture tests, and update `AGENTS.md`, all in the same PR.

**Why engine-free matters:**
- The core is unit-testable in milliseconds, even outside Unity (§9).
- Acquisition can run as a command-line tool that builds test fixtures.
- The drawing tools and simulation can later be built and tested on the same foundation.

## 3. Runtime structure (T2)

**State in iteration 1:**
- The **resort package**: downloaded data, immutable (§5).
- A small **view state** per resort: last camera, bookmarks, view time and display settings.
- Nothing the player makes needs saving yet.

**Threading:**
- **Main thread:** Unity scene, input, UI, and applying finished data to `TerrainData` and GPU buffers.
- **Background .NET tasks,** with progress and cancellation: acquisition (downloads, decoding, package build) and package loading.
- **Burst jobs** (World): building splat and cover masks, forest instances and terrain normals when a resort opens.
- **No simulation thread** (§10).

**Derived data is never saved:** splat maps, forest instances and meshes are rebuilt from the package when a resort opens (0.1 §9, doctrine 1).

## 4. World representation

### 4.1 Coordinates: keep S1M's native grid (T3)

**The data:**
- S1M tiles use **NAD83(2011) CONUS Albers Equal Area (EPSG:6350)**, horizontal units in metres.
- Heights are **NAVD88 (GEOID18) metres**.
- Pixels are exactly 1 m, and tile corners fall on whole kilometres.

**Decision:**
- The resort package uses **the same grid**, so the lidar heights are **never resampled** and the full 1 m detail survives.
- Unity world `x` = Albers x − x₀ and `z` = Albers y − y₀, with a local origin at the site centre; `y` = elevation. One unit is one metre.

**Scale:**
- Albers is equal-area, not true-distance. The local scale error is within about ±1% across the contiguous US, and effectively constant across one site.
- The manifest stores the site's scale factors. Future measuring tools (trail length, lift length) apply them; rendering ignores them.

**Other layers** (land cover, imagery, water) are reprojected onto this grid at download time.

**Rejected alternative:** UTM. It is true to scale, but it would resample the 1 m lidar and blur it.

### 4.2 Elevation: S1M core, overview ring (T4)

- **Core site:** S1M at full 1 m resolution.
- **Recommended site size: square boxes of 2–5 km.** A 5 km box is 25 million heights: 100 MB as float32 in memory, and 25 Unity Terrain tiles of 1,025² covering 1,024 m each. A 10 km box at 1 m would be four times that. Decision T4-Q in the review.
- **Surround ring**, out to 3 km beyond the box, so the edge is never a cliff (0.1 §3). It uses S1M's built-in **8 m overview**: same source, same datum, no seam.
- **Where S1M has no tile yet:**
  - S1M production is still in progress; tiles are added as they are published.
  - The **picker only allows boxes fully inside S1M coverage.**
  - Ring gaps fall back to USGS 3DEP 1/3 arc-second (about 10 m) from the 3DEP web service.
  - Decision T4-Q2.
- **Rendering:** Unity Terrain tiles behind an `ITerrainSurface` interface. A custom mesh can replace them if the look demands it (roadmap §8). With 16-bit heights, 1,500 m of relief gives about 2.3 cm steps.
- The authoritative height grid lives in Domain; `TerrainData` is only a view of it. That is what later drawing tools will edit (§10).

### 4.3 Ground cover (T6)

- **Classes:** forest, alpine/rock, grassland, water, plus developed land (new; used for existing towns and roads).
- **Source:** ESA WorldCover 10 m classes (CC BY 4.0), taken from the classified GeoTIFF tiles rather than the archive's coloured map tiles.
- **Refinement to 1 m:**
  - Forest edges use USGS NAIP aerial imagery (NDVI and texture), so they don't look like 10 m blocks next to 1 m terrain. The archive proved this recipe (0.1 §5).
  - Water comes from OpenStreetMap lake and stream geometry, never from imagery alone.
- **Terrain material:** a splat blended from cover, slope (rock above about 35–40°), altitude and snow; the art direction is 0.5's job.

### 4.4 Forest (T7)

Roadmap §9, unchanged:
- **Placement:** Poisson-disc sampling per 64 m tile, seeded by `hash(resortSeed, tileX, tileY)`. Deterministic and never saved.
- **Density** = forest fraction × slope mask (under 40°) × treeline falloff. Species vary by altitude and aspect.
- **Rendering:** GPU-culled `RenderMeshIndirect` (or `BatchRendererGroup`); LODs down to an impostor beyond about 300 m; vertex wind; snow on branches.
- **Estimate:** about 650,000 trees on a 5 km site (25 km²) at 65% forest.
- **Later:** a clearings mask (for trails and lift lines) multiplies into density, so drawing tools can remove trees by tile.

### 4.5 Snow (T8)

- **No snow model.** Snow is **stylized and procedural**, driven by elevation, slope, aspect and the **view date**: a seasonal snow line that rises and falls, less snow on steep and sun-facing slopes, and snow loading on trees.
- **The seam for later:** the shader reads snow from a **snow-depth texture**. Iteration 1 fills that texture procedurally; a future simulation writes the same texture by dirty rectangle (roadmap §8). The look does not change when simulation arrives, only where the numbers come from.

### 4.6 Lighting and camera (T9)

- **Sun:** position from the site's latitude and longitude and the view time, using the NOAA solar-position algorithm. There is one directional light and a sky that follows the sun.
- **Shadows:** cascaded shadows. Only the near cascade has tree casters; a baked canopy-shadow term handles the distance.
- **View time:** set by a scrubber (time of day and date); it drives the sun and the snow line. This is the clock placeholder (§10).
- **Camera:** orbit / RTS-style with terrain collision, bounded to the surround ring, plus a free-fly mode. Default keys carry over from the archive: W/A/S/D pan, Q/E rotate, R/F tilt, N north (0.1 §2); 0.4 finalises them.

## 5. Package and library formats (T5, T11)

**Resort package: immutable after download.** The archive mutated its terrain package in place, and that caused save-ordering bugs (0.1 §8). Here a package is written once. Later drawing edits will live in saves as sparse deltas on top of it (§10).
- **Location:** `<data>/Resorts/<packageId>/`, where `packageId` is a content hash.
- **`manifest.json`:**
  - format version
  - site box
  - EPSG:6350, local origin and scale factors
  - per-layer provenance (provider, product, tile IDs and dates, request time)
  - attribution text
  - per-file hashes
- **Layers:**
  - `heights-core.f32` (1 m) and `heights-ring.f32` (8 m); little-endian, row-major, NaN for no data
  - `cover.u8` (1 m classes)
  - `imagery/` (compressed tiles, used for cover refinement)
  - `water.json` (lakes and streams)
- **Validation:** alignment and hashes are checked when the package is built and again when it is opened.

**Library:** a list of downloaded resorts with name, location, size on disk and a thumbnail. Deletion needs confirmation.

**View state:** a small versioned JSON per resort.

**Serialization:** Newtonsoft.Json (MIT, via `com.unity.nuget.newtonsoft-json`) for JSON; hand-written binary readers and writers for grids; no reflection-based binary serializers.

**Versioning:** every format carries an integer version. **Recommendation:** formats may change freely until the first build shared with other players; after that, readers migrate older versions with fixture-tested functions.

## 6. Data acquisition and provider terms (T10)

**S1M access** (checked 2026-09-25):
- The files are on USGS's public S3 bucket: `prd-tnm.s3.amazonaws.com/StagedProducts/Elevation/S1M/`.
- Each tile is a **Cloud Optimized GeoTIFF** covering 10 km × 10 km at 1 m (10,000²), 200–450 MB.
  - Compression is LZW with a floating-point predictor, in 512² internal tiles.
  - Overviews exist at 2, 4, 8, 16 and 32 m.
  - No data is −999999.
- Tile names encode the Albers corner in kilometres; for example `n0470e1490` starts at x = 1,490,000 m, y = 470,000 m.
- A coverage index, `FullExtentSpatialMetadata/S1M_Products.gpkg` (18 MB), is updated daily.
- All 3DEP products are **public domain**.

**How the game downloads:**
- It **never downloads whole tiles.** It reads only the internal 512² blocks covering the box (and the 8 m overview blocks for the ring) with HTTP range requests.
- **Estimate:** about 100 blocks for a 5 km site, on the order of 50–100 MB. Phase 1 measures it.
- **Reader:** a small custom Cloud Optimized GeoTIFF reader in Acquisition. It covers only TIFF directory parsing, tile offsets, LZW and the floating-point predictor, and is verified against a reference fixture. That is simpler and more controllable than wrapping a general TIFF library around range requests.

**Coverage lookup:** the picker must show where S1M exists.
- **Recommended:** a coverage service built from the daily index (tile names → availability), fetched when the picker opens and cached. It also allows a direct check by listing the tile's S3 folder.
- Phase 1 confirms the index format. It is SQLite-based, so either the game reads it with a small SQLite dependency, or a tiny step converts it; decided in the Phase 1 spike.

**Pipeline:**
1. Select a site in covered area.
2. Download core heights and ring.
3. Download land cover and NAIP.
4. Download water.
5. Build the package.
6. Validate.
7. Add it to the library.

It runs off the main thread with progress, cancellation, retries with backoff, polite rate limits and an identifying User-Agent. Each stage caches its result, so a failed run resumes. The same code runs as a command-line tool.

**Providers:**

| Data | Provider | Terms | Recommendation |
|---|---|---|---|
| Core and ring elevation | USGS 3DEP **S1M** (public S3, Cloud Optimized GeoTIFF) | Public domain | **Use** |
| Ring gap fill | USGS 3DEP 1/3 arc-second via the 3DEP ImageServer | Public domain | Use only for ring gaps |
| Land cover | ESA WorldCover 10 m classified tiles (AWS open data) | CC BY 4.0 | **Use** |
| Cover refinement | USGS NAIP imagery | Public domain | **Use** |
| Lakes and streams | OpenStreetMap via Overpass (configurable endpoint) | ODbL: attribution; fair-use limits on public servers | **Use** |
| Place search | Nominatim | Maximum 1 request/s, user-triggered, no autocomplete, attribution | Use for a search box |
| Picker basemap | USGS National Map tiles (imagery, topo) in a UI Toolkit slippy map, with an S1M coverage overlay | Public domain | **Recommended** |
| Picker basemap | Cesium for Unity + Cesium ion | Free tier only under $50K revenue or funding; embedding ion in a product sold to others needs an integration licence | Reject |
| Picker basemap | OpenStreetMap Foundation tile servers; Esri World Imagery; CARTO | OSM Foundation forbids bulk/prefetch; Esri needs an account token and restricts commercial use; CARTO needs a key | Reject |

**Coverage is US-only**, which S1M already implies. It covers the contiguous US now, with Hawaii and Puerto Rico planned. A worldwide option (for example Copernicus DEM, 30 m) would be a separate, lower-detail provider behind the same interface. It is not planned.

## 7. Determinism (T12)

Iteration 1 computes, rather than stores, the splat, cover refinement and forest. They must come out identically every time a resort opens.

**Rules for engine-free code and reproducible Burst output:**
- Keyed hash randomness only. No `System.Random`, `UnityEngine.Random`, `Guid.NewGuid`, `DateTime.Now` or unordered parallel reductions.
- Stable iteration order.
- `FloatMode.Deterministic` for Burst jobs whose output must be reproducible, such as forest placement. Burst supports this on 64-bit platforms.

**Scope:** same build, Windows x64.

**Tests:** golden hashes for forest tiles and cover refinement on the checked-in test terrain.

## 8. Performance budgets (T13)

Frozen before features are built. Measured with Unity's Performance Testing package in a benchmark scene with a fixed camera path; results are recorded as JSON with the commit SHA.

| Budget | Target | Conditions |
|---|---|---|
| Frame time | p95 ≤20 ms, p99 ≤33.3 ms, <1% of frames over 50 ms | 1080p, High preset, reference PC, 5 km site, full forest, snow, shadows |
| Integrated GPU | ≥30 FPS | 1080p, Performance preset; reference device to be chosen (T13-Q) |
| Camera and input response | p95 ≤100 ms | Any preset |
| Garbage collection | 0 bytes allocated per frame in steady state | Camera moving |
| Open a downloaded resort | ≤10 s | 5 km site, SSD |
| Download and build a package | ≤5 min | 5 km site, 50 Mbit/s connection; provisional |
| Memory | Provisional: ≤4 GB RAM, ≤3 GB VRAM at High; ≤1.5 GB VRAM at Performance | 5 km site |

**Reference PC:** Ryzen 5 5600X, about 16 GB RAM, RTX 3060 Ti. This is the archive's measurement machine.

## 9. Testing (T14)

- **Most tests are engine-free EditMode tests:** georeferencing and scale factors, the COG reader, package format and validation, cover refinement, forest placement, solar position. Provider responses are **recorded, never live**.
- **PlayMode:** scene boot; opening a resort; `TerrainData` staying in sync with the authoritative grid.
- **Checked-in test terrain:** a real 2 km S1M site built by the acquisition tool and committed through Git LFS. It is public domain, reproducible offline, and it doubles as Phase 1's test area.
- **Performance and live-provider tests:** opt-in only.
- **Continuous integration:**
  - Now: `repo-checks`.
  - **Recommended next:** a `tools/domain-tests` .NET project that compiles the engine-free sources and runs their tests with `dotnet test` on GitHub Actions. It needs no Unity licence.
  - A GameCI Unity run needs licence secrets; Personal-licence activation in CI has changed several times, so verify it first.
- **Guard rail:** a repo check that fails if engine-free folders use banned APIs (§7) or `UnityEngine`.

## 10. Future iterations and the seams iteration 1 keeps (T15)

**Drawing on the terrain** (lifts, trails and the rest):
- A revisioned **design document**: immutable snapshots, typed commands against a named revision, stale commands rejected, derived data never saved. This also makes undo cheap.
- Edits to terrain and cover are stored in the **save as sparse 64 m delta tiles** over the immutable package.
- The construction rules and doctrines recorded in 0.1 §6 and §9 are the starting reference.
- **What iteration 1 does now for this:**
  - The authoritative height grid is engine-free, with `TerrainData` as a view.
  - Forest density already takes a clearing mask (empty for now).
  - The package is immutable.

**Simulation:**
- A real clock implementing `IGameClock` on a dedicated thread; snapshots out, commands in; roadmap §5 and §7 become the active rules; the simulation writes the snow-depth texture.
- **What iteration 1 does now for this:** the simulation placeholder, the snow-depth texture seam (§4.5), keyed randomness and the engine-free domain.

**The clock placeholder** (`MountainPlanner.Simulation`):

```csharp
public readonly struct ViewTime { public readonly int Year, DayOfYear, SecondOfDay; } // no DateOnly in Unity's .NET profile
public interface IGameClock { ViewTime Now { get; } event Action<ViewTime> Changed; }
public sealed class ManualViewClock : IGameClock { /* set by the time-of-day/date scrubber; never advances on its own */ }
```

## 11. Packages for Phase 1 (T16)

- **Keep:** URP 17.3, Input System, Test Framework and the IDE integrations.
- **Add:** Performance Testing, Burst, Collections, Mathematics and Newtonsoft JSON.
- **Remove until needed:** AI Navigation and Timeline. Splines comes back with the drawing tools.
- **Not now:** Entities / Entities Graphics; revisit only if measurements demand them.

## 12. Risks

- **S1M coverage is incomplete.** Mitigated by showing coverage in the picker, only allowing covered boxes, and the 10 m ring fallback. USGS is producing tiles continuously.
- **S1M layout or index changes** while production continues. Mitigated by recorded-response tests and a single provider module.
- **1 m terrain memory and rendering cost.** Mitigated by the 2–5 km site size, tiled terrains and presets; Phase 1 measures.
- **Unity Terrain look at 1 m.** Fallback: a custom mesh behind `ITerrainSurface`.
- **Forest at scale on integrated GPUs.** Mitigated by impostors, culling and the Performance preset.
