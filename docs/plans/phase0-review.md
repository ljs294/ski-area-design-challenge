# Phase 0 review sheet: 0.1 and 0.3 drafts

**For:** the project owner.

**How to use it:** write your answer after each **Comment:**; "OK" accepts a recommendation. ✅ marks what you have already decided; ❓ marks what still needs you. When everything is answered, I fold it into the drafts and turn this sheet into a short decision record.

## Part 1 · Scope (recorded from your answers, 2026-09-25)

**Iteration 1 is just the mountain:**
- A real-world site picker; square sites of 2–5 km.
- Elevation from USGS 1 m lidar (**S1M**).
- The game supplies **lighting, camera, ground cover, forest and snow**.
- **Fully offline after the first download**, which may take longer; about 1 GB per resort is acceptable.

**Future iterations are planned, not built:** drawing on the terrain (lifts, trails and so on) and simulation ([0.3 §10](phase0-0.3-technical-architecture.md#10-future-iterations-and-the-seams-iteration-1-keeps-t15)). Deliverable 0.6 will rewrite the roadmap's Phases 1–4 to match.

**Comment** (only if something above is wrong):

## Part 2 · 0.1 Reference inventory

[0.1](phase0-0.1-reference-inventory.md) is a **record of the old MapLibre game**. Its lifts, trails, snowmaking and guest sections are reference for future iterations; for iteration 1, only §2–§5 and §10 matter.

### A1 · Is anything wrong or missing?

**Comment:**

### A2 · Real brand names (can wait until the drawing iteration)
**Recommendation:** use generic equipment names, not the real HKD snowgun or ANSI lift-standard references, unless you get permission.

**Comment:**

## Part 3 · 0.3 Technical architecture

### Decided

- ✅ **Site size:** squares of 2–5 km ([§4.2](phase0-0.3-technical-architecture.md#42-elevation-extent-and-resolution-t4)).
- ✅ **Snow:** a flat 12 in (0.305 m) across the whole map, drawn from a snow-depth texture that later versions can vary ([§4.6](phase0-0.3-technical-architecture.md#46-snow-t8)).
- ✅ **"Developed" ground-cover class** added ([§4.4](phase0-0.3-technical-architecture.md#44-ground-cover-sources-t6)).
- ✅ **Minimum hardware:** around an RTX 2060; 30 FPS or better at 1080p Standard ([§8](phase0-0.3-technical-architecture.md#8-performance-budgets-and-hardware-t13)).

### ❓ T6 · Ground-cover sources
**Proposal:**
- **Forest and tree height** from the Meta/WRI 1 m canopy-height map (CC BY 4.0), matching the lidar's 1 m detail.
- **Other classes** from ESA WorldCover 10 m (CC BY 4.0).
- **Water and developed shapes** from OpenStreetMap.
- **NAIP imagery is no longer needed.**

Caution: the canopy map comes from 2010s satellite imagery, so the game checks it against WorldCover ([§4.4](phase0-0.3-technical-architecture.md#44-ground-cover-sources-t6)).

**Comment:**

### ❓ T4 · S1M coverage rule and ring detail
S1M isn't finished nationwide.
- **Recommendation:** the picker only allows sites fully covered by S1M and shows coverage on the map.
- The 3 km surround ring uses S1M's 2 m level (upgraded from 8 m, thanks to the 1 GB budget).

Details of how the lidar becomes Unity terrain are in [§4.3](phase0-0.3-technical-architecture.md#43-from-lidar-to-unity-terrain-t4).

**Comment:**

### ❓ T8-Q · Lakes under the snow
Should lakes show **frozen and snow-covered** (consistent with "12 in everywhere") or as **open water**?

**Comment:**

### ❓ T13-Q · Minimum CPU and RAM
**Proposal:** Ryzen 5 3600 / Core i5-9600K class, 16 GB RAM, SSD, alongside the RTX 2060.

**Comment:**

### Recommendations ("OK" is enough)

**T1 · Code modules.** Engine-free `Domain`, `Persistence` and `Acquisition`; `Simulation` becomes a clock placeholder; then `World`, `Presentation`, `UI` and `App` ([§2](phase0-0.3-technical-architecture.md#2-assemblies-t1)).
**Comment:**

**T2 · Runtime.** Background tasks for downloads and loading; Burst jobs for forest and splat; derived data may be cached but never replaces the source ([§3](phase0-0.3-technical-architecture.md#3-runtime-structure-t2)).
**Comment:**

**T3 · Coordinates.** Keep S1M's own grid (CONUS Albers, metres), so the lidar is never resampled ([§4.1](phase0-0.3-technical-architecture.md#41-coordinates-keep-s1ms-native-grid-t3)).
**Comment:**

**T5 / T11 · Package, cache and library.** An immutable package (about 650 MB for a 5 km site) plus a rebuildable terrain cache; opening a resort makes no network calls ([§5](phase0-0.3-technical-architecture.md#5-package-cache-and-library-t5-t11)).
**Comment:**

**T7 · Forest.** Deterministic trees per 64 m tile; tree size from canopy height; about 650,000 trees on a 5 km site; impostors in the distance ([§4.5](phase0-0.3-technical-architecture.md#45-forest-t7)).
**Comment:**

**T9 · Lighting and camera.** Real sun position from a time/date scrubber; cascaded shadows; orbit camera bounded to the ring, plus free-fly ([§4.7](phase0-0.3-technical-architecture.md#47-lighting-and-camera-t9)).
**Comment:**

**T10 · Providers.** USGS S1M, Meta/WRI canopy, ESA WorldCover and OpenStreetMap; a picker map on public-domain USGS tiles; no Cesium ion, Esri or CARTO ([§6](phase0-0.3-technical-architecture.md#6-acquisition-and-provider-terms-t10)).
**Comment:**

**T12 · Determinism.** Every open produces identical tiles and forest; keyed randomness; golden tests ([§7](phase0-0.3-technical-architecture.md#7-determinism-t12)).
**Comment:**

**T14 · Testing and CI.** Mostly engine-free tests; a real 2 km S1M test site checked in; a `dotnet test` CI job needing no Unity licence ([§9](phase0-0.3-technical-architecture.md#9-testing-t14)).
**Comment:**

**T15 · Seams for later.** Float32 source heights separate from Unity's 16-bit view; a clearing mask in forest density; an immutable package; the snow-depth texture; the clock placeholder ([§10](phase0-0.3-technical-architecture.md#10-future-iterations-and-the-seams-iteration-1-keeps-t15)).
**Comment:**

**T16 · Unity packages.** Add Performance Testing, Burst, Collections, Mathematics and Newtonsoft JSON; remove AI Navigation and Timeline ([§11](phase0-0.3-technical-architecture.md#11-packages-for-phase-1-t16)).
**Comment:**

## Anything else

**Comment:**
