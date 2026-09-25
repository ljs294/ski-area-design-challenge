# Phase 0 review sheet: 0.1 and 0.3 drafts

**For:** the project owner.

**How to use it:** three short parts. Write your answer after each **Comment:**; "OK" accepts a recommendation. Links go to the detail. When you're done, I fold your answers into the drafts and turn this sheet into a short decision record.

## Part 1 · Scope: please confirm

**Iteration 1 is just the mountain:**
- A real-world site picker.
- Elevation from USGS 1 m lidar, the **S1M** product.
- The game renders it with its own **lighting, camera, ground cover, forest and snow**.
- **No drawing tools and no simulation.**

**Future iterations are planned, not built:**
- Drawing on the terrain (lifts, trails and so on).
- Simulation (time, weather, snow, guests).

The architecture keeps room for both ([0.3 §10](phase0-0.3-technical-architecture.md#10-future-iterations-and-the-seams-iteration-1-keeps-t15)).

The roadmap's Phase 1 still describes guests and lifts. Deliverable 0.6 (the milestone plan) will rewrite Phases 1–4 to match this scope.

**Comment:**

## Part 2 · 0.1 Reference inventory

[0.1](phase0-0.1-reference-inventory.md) is a **record of the old MapLibre game**. It lists lifts, trails, snowmaking and guests because the old game had them. Those sections are reference for future iterations, not work for iteration 1. For iteration 1, only §2–§5 and §10 matter.

### A1 · Is anything wrong or missing?

**Comment:**

### A2 · Real brand names (can wait until the drawing iteration)
The old game named a real snowgun (HKD Impulse R5) and cited a lift standard (ANSI B77.1). **Recommendation:** use generic names unless you get permission.

**Comment:**

## Part 3 · 0.3 Technical architecture: decisions

[0.3](phase0-0.3-technical-architecture.md) covers iteration 1 only. **Four items need your input** (marked ❓). The rest are recommendations; "OK" is fine.

### ❓ T4 · Elevation: S1M core and surround ring
1 m S1M for the site; S1M's own 8 m overview for a 3 km surround ring ([§4.2](phase0-0.3-technical-architecture.md#42-elevation-s1m-core-overview-ring-t4)).
- **Question 1, site size:** 1 m data is heavy. **Recommendation:** square sites of 2–5 km; 10 km would be four times the memory of 5 km.
- **Question 2, coverage:** S1M isn't finished nationwide yet. **Recommendation:** the picker only allows sites fully covered by S1M, and shows the coverage on the map.

**Comment:**

### ❓ T6 · Ground cover
ESA WorldCover (10 m) refined to 1 m with USGS aerial imagery (NAIP), plus OpenStreetMap lakes and streams. This adds a new "developed" class for towns and roads ([§4.3](phase0-0.3-technical-architecture.md#43-ground-cover-t6)).

**Comment:**

### ❓ T8 · Snow
Stylized, procedural snow from elevation, slope, aspect and the chosen date: a seasonal snow line and snow on trees. It is drawn from a snow-depth texture, which a future simulation can fill instead ([§4.5](phase0-0.3-technical-architecture.md#45-snow-t8)).

**Comment:**

### ❓ T13 · Performance budgets
Frame p95 ≤20 ms at 1080p High on your RTX 3060 Ti PC with the full forest; ≥30 FPS on an integrated GPU at the Performance preset; open a resort in ≤10 s; download a 5 km site in ≤5 min ([§8](phase0-0.3-technical-architecture.md#8-performance-budgets-t13)).
- **Question:** which laptop or integrated GPU should be the minimum target?

**Comment:**

### T1 · Code modules (assemblies)
Engine-free `Domain`, `Persistence` and `Acquisition`; `Simulation` becomes a clock placeholder; then `World`, `Presentation`, `UI` and `App` ([§2](phase0-0.3-technical-architecture.md#2-assemblies-t1)).

**Comment:**

### T2 · Runtime
Main thread for the scene; background tasks for downloads and loading; Burst jobs for forest and splat building; nothing the player makes needs saving yet ([§3](phase0-0.3-technical-architecture.md#3-runtime-structure-t2)).

**Comment:**

### T3 · Coordinates
Keep S1M's own map grid (CONUS Albers, metres), so the 1 m lidar is never resampled; store scale factors for future measuring tools ([§4.1](phase0-0.3-technical-architecture.md#41-coordinates-keep-s1ms-native-grid-t3)).

**Comment:**

### T5 / T11 · Package and library
A downloaded resort is an immutable folder with a JSON manifest and binary layers. A library lists downloaded resorts; a small JSON per resort holds the camera and view settings. Formats may change freely until the first build shared with other players ([§5](phase0-0.3-technical-architecture.md#5-package-and-library-formats-t5-t11)).

**Comment:**

### T7 · Forest
Procedural, deterministic trees per 64 m tile, never saved; about 650,000 trees on a 5 km site; GPU instancing with impostors ([§4.4](phase0-0.3-technical-architecture.md#44-forest-t7)).

**Comment:**

### T9 · Lighting and camera
The sun from real location and a time/date scrubber; cascaded shadows; an orbit camera bounded to the surround ring, plus free-fly ([§4.6](phase0-0.3-technical-architecture.md#46-lighting-and-camera-t9)).

**Comment:**

### T10 · Download method and providers
Read only the needed parts of S1M files from USGS's public S3 bucket, with a small built-in reader. The picker map uses public-domain USGS map tiles with an S1M coverage overlay. Reject Cesium ion, Esri, CARTO and OSM tile servers on licensing grounds ([§6](phase0-0.3-technical-architecture.md#6-data-acquisition-and-provider-terms-t10)).

**Comment:**

### T12 · Determinism
Forest, cover and splat come out identically every time a resort opens: keyed randomness, banned non-deterministic APIs, golden tests ([§7](phase0-0.3-technical-architecture.md#7-determinism-t12)).

**Comment:**

### T14 · Testing and CI
Mostly engine-free tests; a real 2 km S1M test site checked in; add a `dotnet test` CI job (no Unity licence needed) ([§9](phase0-0.3-technical-architecture.md#9-testing-t14)).

**Comment:**

### T15 · Seams for future iterations
Height grid owned by engine-free code; a clearing mask in forest density; an immutable package; a snow-depth texture; the clock placeholder ([§10](phase0-0.3-technical-architecture.md#10-future-iterations-and-the-seams-iteration-1-keeps-t15)).

**Comment:**

### T16 · Unity packages
Add Performance Testing, Burst, Collections, Mathematics and Newtonsoft JSON. Remove AI Navigation and Timeline for now ([§11](phase0-0.3-technical-architecture.md#11-packages-for-phase-1-t16)).

**Comment:**

## Anything else

**Comment:**
