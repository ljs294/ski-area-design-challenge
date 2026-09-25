# Phase 0 review sheet: 0.1 and 0.3 drafts

**For:** the project owner. **Purpose:** one place to comment on both drafts. Each item links to the detail. Under each item, write your answer after **Comment:**. "OK" is enough to accept a recommendation. Once you're done, I fold your answers into the drafts and turn this sheet into a short decision record.

- Draft 0.1: [Reference inventory](phase0-0.1-reference-inventory.md), a record of the archived game.
- Draft 0.3: [Technical architecture](phase0-0.3-technical-architecture.md), for the mountain-painter scope.

## Scope (please answer these first)

### S1 · What is in the mountain painter?
Tick what the first playable painter should include. **Recommended first set:** the items marked ★. Everything else goes to later phases.

- [ ] ★ Site picker, download, and the resort library
- [ ] ★ Stylized 3D mountain: terrain, cover, forest, static snow, time-of-day lighting
- [ ] ★ Trails: paint, difficulty, grading (level bench, 45° faces), tree clearing
- [ ] ★ Lifts: two-point placement, type catalog, towers, haul rope and chairs (static or animated on a loop)
- [ ] ★ Nodes, connector paths and junctions (the ski network graph and the connectivity check)
- [ ] Roads
- [ ] Ponds and dams (earthwork)
- [ ] Snowmaking network layout (pipes, hydrants, guns), with or without the hydraulic check
- [ ] Buildings (pump house, lodges)
- [ ] Guest Entrance marker
- [ ] Undo/redo
- [ ] Trail-map / "poster" view

**Comment:**

### S2 · US-only or worldwide sites?
USGS elevation and NAIP imagery are US-only and give the best data. **Recommendation:** US-only for now, behind a provider interface that allows a worldwide set later ([0.3 §6](phase0-0.3-technical-architecture.md#6-data-acquisition-and-provider-terms-t8-t9)).

**Comment:**

### S3 · Snow without simulation
**Proposal:** static, stylized snow cover from elevation, slope, aspect and the view date; trails and clearings look groomed ([0.3 §4.5](phase0-0.3-technical-architecture.md#45-snow-without-simulation)).

**Comment:**

### S4 · Update the roadmap to match
The roadmap's Phase 1 slice (§6) still lists 3,000 guests and a simulation bar. **Proposal:** the 0.6 milestone plan redefines Phases 1–4 around the painter, and a small roadmap edit records the scope change.

**Comment:**

## 0.1 Reference inventory

### A1 · Accuracy and gaps
Is anything wrong or missing, especially in §2 (screens), §6 (construction rules) and §11 (not built)?

**Comment:**

### A2 · Real brand and standard names
The archive modelled the HKD Impulse R5 snowgun by name and cited ANSI B77.1 for lift speeds ([0.1 §12](phase0-0.1-reference-inventory.md#12-third-party-names-and-data-terms-to-revisit)). **Recommendation:** use generic equipment names unless you get permission.

**Comment:**

## 0.3 Technical architecture: decisions

### T1 · Assemblies
Rename the engine-free `Simulation` assembly to `Domain` (design model, construction math, network graph). Keep `Simulation` as the clock placeholder only. Add `Persistence`, `Acquisition` and `App` ([§2](phase0-0.3-technical-architecture.md#2-assemblies-t1)).

**Comment:**

### T2 · One revisioned design document
All player edits are commands against immutable, revisioned snapshots; stale commands are rejected; derived data is never saved. This makes undo cheap ([§3.1](phase0-0.3-technical-architecture.md#31-one-authored-design-document-t2)).

**Comment:**

### T3 · Threading
Main thread for the scene and UI; .NET tasks for engine-free math; Burst jobs for grid and forest work; results tagged with a revision. No simulation thread ([§3.3](phase0-0.3-technical-architecture.md#33-threading-t3)).

**Comment:**

### T4 · Coordinates
Store packages in the site's UTM zone, in metres, with a local origin; Unity units are metres ([§4.1](phase0-0.3-technical-architecture.md#41-coordinates-t4)).

**Comment:**

### T5 · Terrain resolution and representation
Authoritative 2 m float grid (1 m optional where lidar exists), a 10 m surround ring, and Unity Terrain tiles behind an interface so a custom mesh can replace them ([§4.2](phase0-0.3-technical-architecture.md#42-heights-and-terrain-t5)). **Question:** 2 m by default, or push for 1 m?

**Comment:**

### T6 · Immutable package; edits in the save
The downloaded package never changes; the save stores sparse height and clearing deltas. This removes the archive's write-ordering hazard ([§4.3](phase0-0.3-technical-architecture.md#43-package-is-immutable-edits-live-in-the-save-t6)).

**Comment:**

### T7 · File formats
Package folder with a JSON manifest and binary layers. The save is one zip (`.mpsave`) containing a JSON design document, binary deltas and a thumbnail. Newtonsoft.Json; atomic writes; keep a `.bak` ([§5](phase0-0.3-technical-architecture.md#5-data-formats-t7)). **Question:** saves may break freely until when? **Recommendation:** until the first build shared with other players.

**Comment:**

### T8 · Providers
Keep USGS 3DEP, NAIP, WorldCover (switch to the classified GeoTIFFs), OSM via Overpass and Nominatim. **Drop** Esri, CARTO and OSM tile servers. **Reject** Cesium ion for a commercial game. Use a UI Toolkit slippy map over USGS National Map tiles for the picker ([§6](phase0-0.3-technical-architecture.md#6-data-acquisition-and-provider-terms-t8-t9)).

**Comment:**

### T9 · Coverage
Answered by S2.

### T10 · Determinism for construction and generation
Same inputs, same outputs; keyed randomness; banned non-deterministic APIs in engine-free code; deterministic Burst mode for the forest; golden fixtures ([§7](phase0-0.3-technical-architecture.md#7-determinism-t10)).

**Comment:**

### T11 · Performance budgets
Frame p95 ≤20 ms at 1080p High on the reference PC with the full forest; ≥30 FPS on an integrated GPU at Performance; ≤100 ms tool response and grading preview; zero per-frame allocations; open ≤10 s; save ≤1 s ([§8](phase0-0.3-technical-architecture.md#8-performance-budgets-t11)). **Question:** which integrated GPU or laptop is the minimum target?

**Comment:**

### T12 · Testing and CI
Mostly engine-free tests; a checked-in 2 km test terrain in Git LFS; add a `dotnet test` CI job for the engine-free code (no Unity licence needed); Unity CI later ([§9](phase0-0.3-technical-architecture.md#9-testing-t12)).

**Comment:**

### T13 · Simulation placeholder
`IGameClock` plus a manual view clock for lighting and the snow preview. Nothing advances time ([§10](phase0-0.3-technical-architecture.md#10-the-simulation-placeholder-t13)).

**Comment:**

### T14 · Packages
Add Performance Testing, Burst, Collections, Mathematics, Splines, Newtonsoft JSON and LibTiff.NET. Remove AI Navigation and Timeline for now. No Entities ([§11](phase0-0.3-technical-architecture.md#11-packages-for-phase-1-t14)).

**Comment:**

## Anything else

**Comment:**
