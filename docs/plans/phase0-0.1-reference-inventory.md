# Phase 0 · 0.1 Reference inventory: the archived MapLibre game

**Audience:** the project owner and coding agents. **Status:** approved 2026-09-25. **Source:** tag `maplibre-final` (commit `c29228f`). Decisions: [phase0-decisions.md](phase0-decisions.md).

This is a record of what the archived game does, the rules behind it, and what was learned building it. It describes behaviour, not code. Nothing here is carried into Unity: it is the input to deliverable 0.2, which decides what to keep, change, add or drop. Every link points at the frozen tag.

**Current scope (decided 2026-09-25):** Unity iteration 1 is **just the mountain**: a real-world picker, USGS 1 m lidar elevation (the S1M product), and the game's own lighting, camera, ground cover, forest and snow. Drawing on the terrain (lifts, trails, etc.) and simulation are **future iterations**. This inventory records the whole archived game, so most of it, especially §6 (construction) and §7 (simulation), is reference for those future iterations, not for iteration 1. The parts that matter now are §2 (screens), §3 (terrain data), §4 (layers), §5 (ground cover) and §10 (performance).

**Status tags used below:**
- **Shipped:** works in new (schema-17) games.
- **Legacy-only:** works only in old schema 1–16 games; not wired into the final dual-clock simulation.
- **Partial:** exists with stated simplifications or placeholders.
- **Seam only:** an interface or data contract exists, but no behaviour.
- **Not built:** discussed or planned, not implemented.

## 1. The product in one paragraph

A single-player desktop game (Electron + React + MapLibre) in which the player picks a real 2–10 km square of mountain anywhere in the continental US, downloads its terrain, and designs a ski resort on it: lifts, graded trails, connector paths, roads, ponds, dams, a snowmaking network and pump houses. A deterministic simulation then runs a winter of chronological weather, natural snow, skier wear, guest admissions, lift queues, amenities and ticket revenue. The look is an analytical map (hillshade, contours, slope and aspect shading, land cover) with an optional 3D tilt, not a rendered 3D world.

## 2. Player journey and screens

| Screen | What it does | Status | Reference |
|---|---|---|---|
| Main menu | Ski-trail-sign menu (●/■/◆/◆◆ chips), over a live, slowly drifting, dimmed hillshade backdrop of Crystal Mountain; Continue is disabled when there are no saves | Shipped | [MainMenu][ui-menu], [MenuBackdrop][ui-backdrop] |
| New resort setup | One workspace: search a place (Nominatim), draw the site box (2–10 km square), "Prepare Resort Data" (download), name and enter. Backtracking keeps the selection | Shipped | [SetupWorkspace][ui-setup], [sitePicker][ui-sitepicker] |
| Loading | "Loading terrain…" veil until the map is idle, then eases into a tilt | Shipped | [ResortLoadingScreen][ui-loading] |
| Gameplay workspace | Full-width translucent docked simulation bar (38 px); compact floating game windows with connected tabs, dragging, keyboard movement, stacking and viewport clamping | Shipped | [GameplayWorkspace][ui-workspace], [GameWindow][ui-window] |
| Toolbox | Tabs: Lifts, Trails, Snowmaking, Infrastructure. Pinnable | Shipped | [GameplayWorkspace][ui-workspace] |
| Dashboards | Tabs: Trails Map (a trail-map style view), Snowmaking (network / pressure-and-flow), Guests, Weather. Dashboard camera locks orientation and restores it on exit | Shipped | [MountainDashboards][ui-dashboards] |
| Inspectors | Run and lift inspectors keyed by stable IDs; stay live when pinned; independent of the current selection. Tabs: Overview, Conditions/Operations, Profile | Shipped | [TrailDetail][ui-traildetail], [LiftDetail][ui-liftdetail] |
| Guest "vibe check" | A bounded, RollerCoaster Tycoon 2-style summary of aggregated guest thoughts | Shipped | [GuestVibeCheck][ui-vibe] |
| Load / library | Searchable resort library with separate load and confirmed delete | Shipped | [LoadGameModal][ui-load] |
| Settings | General / Controls / Data. Theme Light/Dark/System; units US/Metric; interface scale 50–150% in 5% steps; render quality Performance/Standard/High/Ultra; window Windowed/Borderless/Fullscreen; rebindable keys; downloaded-terrain library that protects packages referenced by saves | Shipped | [Settings][ui-settings], [MapManagement][ui-mapmgmt] |
| Credits | Data-provider attributions | Shipped | [CreditsPanel][ui-credits] |
| Developer console | Backquote or F10; `skip` is a clock-only jump that is never a player feature | Shipped (dev) | [DeveloperConsole][ui-console] |
| Graphics Lab, Weather Lab | Developer harnesses: side-by-side render-quality comparison; a standalone weather-model validation product | Shipped (dev) | [GraphicsLab][ui-glab], [WeatherLab][ui-wlab] |

**Default keys:** W/A/S/D pan, Q/E rotate, R/F tilt, N snap north, U toggle 2D/3D, 1 Trails dashboard, 2 Snowmaking dashboard. Escape is never bindable ([keybinds][d-keybinds]).

**Look and tokens** (to port, per the roadmap §11): accent `#155ab6`, text `#1e2a32`, surface `#fff`, app background `#f3f5f2`, border `#d6dfe1`, danger `#b33338`, success `#276748`, plus a dark set; radii 4/6/12 px; 14 px base text; outline icons; compact charcoal/light game windows ([ui.css][ui-css], [gameWindows.css][ui-gwcss]).

**UI rules the old game enforced** ([agent guide][agents]): the toolbar and status bar have a fixed set of divisions; no new persistent fixed panels without approval; dialog Escape stops propagation so construction tools never receive the same key press.

## 3. Site selection and terrain data

**Site:** a square box of 2–10 km drawn on a live map ([sitePicker][ui-sitepicker]). The downloaded extent, not the drawn square, becomes the boundary (see the alignment rule below).

**Resort package contents** (prepared once, then played offline; [terrainIngest][d-ingest], [terrainPackage][d-package]):

| Layer | Source | Resolution / extent | Notes |
|---|---|---|---|
| Core elevation | USGS 3DEP ImageServer `exportImage` | 500–2,000 px grid over the box (about 5 m on a 10 km site) | [elevation][d-elevation] |
| Surround ring | USGS 3DEP, same source | Box + 3 km, 1,024² (about 10 m), decimetre-rounded | Only elevation and hillshade outside the box; feathered 8% at the property line |
| Imagery | USGS NAIP ImageServer | Box only | Used for cover refinement and display |
| Land cover | ESA WorldCover 10 m (Terrascope WMTS), refined with NAIP | Box only | Four classes, see §5 |
| Vector context | OpenStreetMap via Overpass (three mirror endpoints) | Box | Roads, streams, lakes ([overpassConfig][d-overpass]) |
| Contours | Derived | 512² grid | Trail-design contour interval 20 ft (6.096 m) |
| Weather | Daymet (daily) + NASA POWER (hourly timing), built by a separate service | Site point | Stored as a separate, checksummed, content-addressed package ([weather-service][d-wservice]) |

**Doctrines:**
- **Trust the extent the provider returns, never the one you requested.** USGS `exportImage` silently expanded the latitude range to match the pixel aspect ratio; storing the requested box compressed terrain about 1.46× north–south and misaligned every layer. Package validation now rejects any layer more than 1e-6° off ([terrainPackage][d-package]).
- **Download a surround ring** so the property edge is not a cliff; camera bounds are the ring, not the box. No fog; the ring ends in a crisp floating edge.
- **Offline after download.** Gameplay never contacts a provider; weather is installed ahead of time.
- **Box outline = data extent.** The locked outline and exterior mask are drawn from the package bounds.

**Base map and display sources used live** (not in the package): OpenFreeMap (light) and CARTO Dark Matter (dark) basemaps, Esri World Imagery satellite tiles, AWS Terrarium elevation tiles for the menu and picker ([analysisLayers][ui-analysis], [SearchBox][ui-search]). **Licensing note:** Esri World Imagery and CARTO were used without keys; both restrict commercial use (see 0.3 §6).

## 4. Map and analysis layers

- **Layers:** hillshade, labelled contours, slope-angle bands, aspect (mutually exclusive with slope), four-class cover, snow depth/condition (mutually exclusive with slope and aspect), streams, lakes, roads, satellite imagery, 2D/3D toggle ([LayerPanel][ui-layers]).
- **Slope bands match trail difficulty:** green <16°, blue <24°, black <37°, expert ≥37°.
- **Bottom-to-top draw order:** analysis, site boundary, road, dam, pond, ski-node/path, trail, lift, building, snowmaking, guests.
- **Hit (pick) priority:** guests, snowmaking, building, lift, trail, dam, pond, road, stream, lake. The dam/pond inversion is deliberate: a click aims at the dam crest, not the pool ([mapContribution][ui-contrib]).
- **Render profiles:** Performance / Standard / High / Ultra set pixel ratio caps, terrain level of detail, cover mode, cache budgets, worker counts, contours, hillshade and effects ([renderProfile][ui-renderprofile]).

## 5. Ground cover

- **Four classes:** forest, alpine, grassland, water (+ nodata) ([fourClassCover][d-cover]).
- **Forest** is seeded from ESA WorldCover tree cover, then refined at the edges with NAIP NDVI and texture. **Water** is seeded from hydrography, never detected from imagery alone (dark roofs and shadow mimic water).
- **Lesson:** a USGS lidar canopy-height path was removed. It reported 7.5% forest where WorldCover showed 65% at the same site, and inflated "alpine" because treeline is inferred from forest.
- **Clearing:** confirming a lift clears a 55 ft (27.5 ft per side) corridor; confirming a trail clears its painted footprint, with a subtle treeline wobble on runs only. Clearing is best-effort and never rolls back committed infrastructure ([coverEdit][d-coveredit]).

## 6. Construction tools

**Shared flow, all tools:** choose tool → draw → the tool analyses in a background worker → review (stats, cost-like readouts, warnings) → confirm or cancel. Every entity has a construction status of `planning` or `complete`.

Rules:
- One construction confirmation at a time; a second one in the same tick is rejected.
- A result computed against a superseded terrain or topology revision is rejected, never merged.
- Changing tool cancels the previous tool synchronously.
- Only the active tool may override cursor, drag-pan or double-click zoom, and cleanup restores the prior state exactly once ([constructionLock][ui-lock], [mapInteractionLease][ui-lease]).

### 6.1 Lifts ([lifts][d-lifts], [liftControllerModel][ui-liftmodel])

| Category | Types (capacity pph, speed fpm) |
|---|---|
| Surface | Rope tow (700, 500), Magic carpet (1,000, 100), T-bar (1,200, 400) |
| Fixed-grip chair | Double (1,200, 400), Triple (1,800, 400), Quad (2,400, 400) |
| Detachable chair | Quad (2,400, 1,000), Six-pack (3,000, 1,000), Eight-pack (3,200, 1,000) |
| Detachable gondola | 8-person (2,400, 1,000), 10-person (2,800, 1,000), 12-person (3,000, 1,000) |
| Tram | 60- and 80-person, 2,000 fpm; capacity = cabin × 3,600 / (ride time + 240 s dwell) |

- A lift is exactly two points (a tuple, by explicit decision); multi-point lines were reserved for a future type. Click-click placement, not drag. Terminals auto-orient bottom-first by elevation.
- Capacity, speed and ride time derive from the type (schema 14). Older saves' user-chosen capacities migrate to the nearest type.
- Lift names render once per line, aligned with it.

### 6.2 Trails ([trails][d-trails], [trailCrossSection][ui-xsection], [terrainGradeEngine][ui-gradeengine])

- **Painting:** a brush 8–120 m wide (default 30 m) paints a polygon footprint; head and tail are then placed.
- **Difficulty:** band of `(3 × average slope + maximum slope) / 4`, so sustained pitch counts three times the steepest segment. Green <16°, blue <24°, black <37°, expert ≥37°. Symbols ● ■ ◆ ◆◆.
- **Grading doctrine:**
  - The running surface (bench) is **level across the section**, always, so contours run square to the centreline.
  - Cut and fill faces are capped at **45° (1:1)**. A hillside steeper than that simply cannot be benched; this falls out of the geometry and is not a separate check.
  - Minimum bench 8 m. Sections are solved every 8 m (at most 800 per part) and blended between stations.
  - **Grading never leaves the painted footprint.** Stations that cannot hold a bench keep natural ground, are marked red, and the ungraded length is reported. Grading is a tool, never a gate. An older "widen the run to fit" mechanic was deliberately removed.
  - The readout is cut / fill / balance in m³, computed once from the raster. The pending edit previews its changed contours in yellow.
  - Roads are the one exception: they may grade out to 3× the pavement width.
- **Profile view:** elevation profile per run in the inspector.

### 6.3 Ski nodes, connector paths and topology ([skiNodes][d-skinodes], [topology][d-topology], [network][d-network])

- Free-standing map pins ("nodes") and footpaths connect lifts, runs and each other. Confirming a path splits both trails and materializes two shared junctions in one transaction.
- Trails, nodes, paths and junctions form one revisioned document. Deleting a run prunes only junctions nothing else references (lift terminals excepted).
- **The ski network graph is derived, never persisted.** It is rebuilt from trails and lifts on every load and edit. Topology decisions use metres in a local frame; reported lengths use haversine.
- **Connectivity rule for guests:** an operating base lift must reach at least one open descent. The Guest Entrance marker shows green/red, and the connected lifts highlight ([guestConnectivity][ui-connectivity]).

### 6.4 Roads ([roads][d-roads])

- Two-lane road: 7 m pavement plus a 3 m clearing buffer each side. It is stored as a compact centreline; the pavement polygon is derived, not saved.
- Roads compile into the guest access graph: vehicle routes, parking and drop-off. That access simulation is **Legacy-only** (§7.4).

### 6.5 Ponds ([pondEarthwork][d-pond])

- The drawn ring is the **waterline at full pool**; the berm sits outboard. The design surface is `max(fill envelope(d), min(ground, cut envelope(d)))` over signed distance `d` from the ring. One continuous surface ties into the hillside with no uphill/downhill special cases.
- Constants: freeboard 0.6 m; crest 3.5 m; water face 3:1; outer face 2:1; cut 1:1.
- Suggested full pool is the mean ground elevation inside the ring. Excavation depth is the player's lever to balance cut and fill. Berms over 20 m, or that cannot daylight within reach, are refused ("that's the dam tool's job").

### 6.6 Dams ([damEarthwork][d-damearth], [damAnalysis][d-damanalysis])

- The player draws a crest alignment across a stream valley; the other bank snaps at equal height within 75 m. Full pool is the clicked contour.
- The reservoir is flood-filled twice: on natural ground, to find the basin and which side is wet, then on the graded surface, so the reported shoreline and capacity describe the finished pond.
- Earth-fill embankment with the same faces and freeboard as a pond berm. Maximum height 40 m; minimum length 5 m; minimum pond area 100 m².
- Water supply to snowmaking is a fixed gameplay figure, "deliberately not a hydraulic model".

### 6.7 Snowmaking network ([snowmakingHydraulics][d-hydraulics], [snowmakingGuns][d-guns], [snowmakingNetwork][d-snownet])

- **Nodes:** intake (at a pond, dam or lake), pump, junction, hydrant.
- **Pipes:** one editable route per pipe; nominal diameters 4–24 in in 2 in steps (default 8 in). New pumps are placed inline on a pipe segment with two opposing ports.
- **Hydraulics:**
  - Design and capacity analysis only.
  - Hazen–Williams friction with C = 120; pump head from horsepower and efficiency.
  - Each source tree is solved independently with a sparse nonlinear solver.
  - Results are shown per pipe, pump and gun on the "Pressure & flow" dashboard.
- **Guns:** one real product line was modelled by name: the HKD Impulse R5 low-energy tower gun.
  - Minimum water pressure 200 psi; air pressure 85 psi.
  - Five performance stages by wet-bulb temperature (28, 24, 19, 14, 9 °F; 18–58 gpm).
  - Variants: a sled, and 10, 20 and 30 ft towers (throw 30, 80 and 125 ft; catalog price $7,000–$9,000).
  - Hose reach 50 ft (15.24 m); guns reconcile one-to-one to hydrants.
- **Not built:** continuous operation. There is no water depletion, no snow production over time, and no schedules. The solver answers "can this network run?", not "what happened tonight?".

### 6.8 Buildings ([buildings][d-buildings], [buildingSiteAnalysis][d-bsite], [buildingMesh][d-bmesh])

- The only player building is the **pump house**, a procedural gable building that owns a reciprocal 1,000 hp / 85% pump. Removing it detaches pipe ends; terrain and cover are not restored.
- **Foundations:**
  - Flattened pad: median datum, 6 ft working apron, cut faces 1:1, fill faces 2:1.
  - Slope foundation: eight perimeter samples, 6 in clearance, terrain untouched.
- Cafés and services exist only as **virtual base-area facilities** attached to the Guest Entrance, because commercial buildings never received a save format.

### 6.9 Guest Entrance

- Placed on an actual lift-base node through the Infrastructure tools. It is the single portal through which guests arrive and leave; a default two-responder ski-patrol base is derived from it (**Legacy-only**).

## 7. Simulation

### 7.1 Time: the dual clock (Shipped, schema 17) ([dual-clock record][doc-dualclock], [dualClock/model][d-dcmodel])

- **Macro clock:** the authoritative calendar for accounting, weather, admissions, queues, ledgers and snow. 1× = 40 simulated seconds per real second. Operating hours 08:00–16:00. A winter is 24 weeks.
- **Micro clock:** the time of representative guests' movement, needs and personal visits, run at 1, 1.75 and 2.75 micro-seconds per real second at the first three presets. At 8× and above, guests show as aggregate trail flows and queue counts.
- **Speed presets:** 1×, 2×, 4×, 8×, 16×, 64×. The HUD always shows macro time. "Follow at 1×" slows the whole simulation.
- **Advance-to:** Next Opening, Day, Week, Month, Season, and Skip to Winter. Runs in the worker with progress and cancellation, finishes paused, and keeps an interrupted destination for explicit Resume.
- **Why it was built this way:** a compressed calendar makes individual motion unreadable. The earlier design record states the unavoidable trade-off: calendar speed, natural motion, exact individual position and continuity cannot all hold at once ([time and operations draft][doc-timeops] §2).
- **Legacy (schema 1–16):** a composite week, where one displayed 08:00–20:00 day stood for a week, and rates of 30/60/240/960 s/s.

### 7.2 Weather ([src/weather][d-weather], [weather-engine][d-wengine], [weather-service][d-wservice])

- An offline package built from **Daymet** daily constraints (precipitation totals) and **NASA POWER** (MERRA-2-based) hourly timing, stored as checksummed chunks.
- Chronological hourly weather per real calendar day (dual clock). Local calendar and DST handling. Deterministic analog events; storm, cold-snap, warm-up and dry-spell detection.
- **Terrain-resolved fields:** temperature with cold-air pooling during inversions, wet-bulb (Stull approximation), precipitation phase, snow ratio, radiation.
- **Forecasts** are issued at 05:00 and 17:00 resort time; a 7-day forecast and a weekly outlook. Forecasts stay forecasts: the true future is never shown.
- **Not built:** wind holds and weather-driven lift closures ("not fabricated from static design data").

### 7.3 Snow and wear ([snowSimulation][d-snowsim], [snow][d-snow], [dualClock/wear][d-wear])

- **Grid:** about 10 m cells, at most 512 per side. Depth to 40.95 m. Eleven surface classes (P, PP, MG, HP, IS, CO, FG, LG, SC, WG, WP: powder through wet powder).
- **Model v1**, sequential and hourly; many hours at once equals repeated one-hour steps. Parameters:
  - compaction 0.9985 per hour
  - melt 0.55 mm per positive degree-hour
  - rain melt 0.35 mm per mm of rain
  - radiation melt 1.2 µm per W/m²
  - visible threshold 2 cm
- **Skier wear:**
  - Completed traversals spread skier-distance over the trail footprint, independent of rendering.
  - Reference width 30 m and slope 20°; slope factor 0.5–2.
  - Loss is 0.005 mm per slope-weighted passage.
  - Surface becomes packed at 200 passages and hard at 800.
  - Fresh snow resets exposure.
- **Thin-cover warning:** raised when ≥20% of a trail's area is under 10 cm; cleared below 10%. Advisory only.
- **Not built:** grooming (a job contract exists; §7.6), moguls, snow density/layers, lateral transport.

### 7.4 Guests ([guestSimulation][d-guests], [dualClock/engine][d-dcengine])

| Area | Behaviour | Status |
|---|---|---|
| Identity | Guests with ability (continuous, plus beginner/intermediate/advanced/expert band), age band (child/teen/adult/senior), wealth, price sensitivity | Shipped |
| Parties | Individual, family, friends, club, school. The leader plans; the weakest member bounds route safety; boarding keeps parties together in fair consecutive chairs | Legacy-only (full); Partial in dual clock |
| Demand | Market size × seeded arrival shape × bounded multipliers for reputation (ref. 0.60, sensitivity 1.8), value (ref. 0.50, sensitivity 1.1), price, operations and conditions; capacity-limited daily plan; weekday profile | Shipped (cohort form) |
| Admissions and flow | Dual clock: capacity-constrained cohorts conserve admissions, active guests and departures; up to 3,000 representatives move individually | Shipped |
| Lifts | FIFO queues; rated capacity with fractional seat credits; unused seats are not banked | Shipped |
| Trail entry | First arrival enters immediately; later ones at least 2 micro-seconds apart. Speed = segment baseline × (0.6 + 0.8 × ability) × (1 + 0.15 Z), Z a keyed standard normal truncated to ±3 | Shipped |
| Route choice | Condition-aware scoring (grooming, snow quality, coverage, crowding, effective difficulty, comfort, terrain character); above-ability routes stay eligible with a strong penalty | Shipped |
| Experience | Versioned formulas for terrain suitability, crowding, expectation gaps, weighted satisfaction channels, keyed early departure, exact thought aggregation | Shipped |
| Needs and amenities | Hunger, thirst, warmth, restroom, fatigue (fixed-point); FIFO service queues, capacity, stock, integer-cent wallets | Shipped (virtual base café) |
| Injury and patrol | One keyed injury draw per run entry (coefficients are uncalibrated placeholders); patrol dispatch, response time, rescue | Legacy-only |
| Road access, parking, lodging, repeat visits | Vehicle routes with congestion, parking and drop-off, multi-day stays, visit memory | Legacy-only |
| Reputation | Event-sourced, idempotent ledger; order-independent | Shipped (legacy detail richer) |
| Presentation | 9 px dots with 8 px hit radius on distance-indexed trail centrelines; aggregate flow overlays at high speed; follow camera; Visit Completed card | Shipped |

### 7.5 Economy ([ticketFinance][d-tickets], [phase3Economy][d-economy])

- **Shipped:** prepaid day tickets recognized once at admission in integer cents, with a daily price lock and a staged next-day price; amenity spending; weekly and seasonal ledgers.
- **Display only:** snowgun catalog prices and cut/fill volumes. There is **no construction budget, no construction cost, no operating cost, no staff, no loans**. The "tycoon" economy is not built.

### 7.6 Operations (Seam only)

- A typed job contract exists (assignment, capabilities, schedules, prerequisites, reservations, consumption, typed effects) ([types/dualClock][d-dctypes]).
- Grooming, lift maintenance, avalanche control, staffing and deliveries were designed in outline ([time and operations draft][doc-timeops] §9) but not built.

## 8. Persistence

- **GameSave** schema history, 1 → 17: lifts; trails; roads; ponds, dams and snowmaking; pipe segment metadata (v12); compact snow snapshot (v13); one lift type discriminator (v14); pinned weather run and buildings (v15); a legacy clock (v16); precise dual-clock checkpoint (v17). Older saves hydrate through strict "sanitize" shields that drop malformed entities rather than trust them ([gameSaveSchema][d-saveschema], [types/gameSave][d-savetypes]).
- **Storage:** Electron filesystem through a narrow preload bridge; IndexedDB in the browser build. Terrain packages, weather sidecars and legacy guest sidecars are separate, content-addressed or hashed files.
- **Lessons:**
  - The save must observe one coherent committed state: terrain written first, sidecars before the save, and revision checks reject mixed states.
  - Loads resume paused.
  - Mutating the terrain package in place made package/save ordering a constant hazard.

## 9. Engineering doctrines worth keeping

1. **Derived data is never persisted.** Graphs, stats and display geometry are rebuilt from authored data.
2. **Revisioned documents.** Every commit names the revision it was built on; stale work is rejected without partial writes.
3. **Single-owner construction** with ticketed release, so an older operation cannot release a newer one's lock.
4. **Supersede by cancelling.** Workers owned by cancelled work are terminated; responses carry request identity and are validated.
5. **Keyed randomness.** Each draw is derived from world seed, entity, domain tag and ordinal; there is no shared mutable stream ([random][d-random]).
6. **Integer money and exactly-once ledgers** with stable transaction IDs.
7. **Conservation and invariance.** People, seats, money and water are conserved; results do not depend on playback speed, chunk size, rendering or viewport.
8. **Hydration shields.** Untrusted saves are sanitized entity by entity.
9. **Trust returned raster bounds.** All layers share one georeference.
10. **Versioned formulas.** Every behavioural formula carries a version, so a change is visible and needs a fixture update.

## 10. Performance lessons

From the [guest performance audit][doc-perf] and the [integrated benchmark][doc-bench]:
- Drawing 3,000 guest dots held about 60 FPS (frame p95 18 ms, RTX 3060 Ti, 1080p). **The stutter came from elsewhere:**
  - main-thread churn on every simulation publication: weather fields rebuilt, the whole map re-rendered twice, sources churned
  - per-guest, per-frame CPU work: terrain height queries, hit-index rebuilds, buffer reallocation
  - a simulation slower than real time on a realistic eight-lift resort
- **Frozen budgets:** frame p95 ≤20 ms, p99 ≤33.3 ms, <1% of frames over 50 ms; pause/selection p95 ≤100 ms; snow patch visible ≤250 ms; retained heap growth ≤10%.
- **Reference hardware:** Ryzen 5 5600X, about 16 GB RAM, RTX 3060 Ti.

## 11. Known gaps (not built)

Grooming and other operations; continuous snowmaking (water use, production, schedules); a construction and operating economy; staff; lodges and commercial buildings; lodging buildings; patrol buildings; multi-point lifts and angle stations; wind holds and weather closures; night skiing; summer; detailed guest avatars; a fully green browser test suite (several construction workflows were stale at archive time).

## 12. Third-party names and data terms to revisit

- **Real product names:** the HKD Impulse R5 snowgun is modelled by brand and model; lift speeds cite ANSI B77.1. Decide in 0.2 whether to keep real brands (permission needed) or use generic equipment.
- **Provider terms:** Esri World Imagery and CARTO basemaps were used keyless (commercial restrictions). OSM (ODbL), ESA WorldCover (CC BY 4.0), Daymet and NASA POWER (citation requested) and USGS (public domain) need attribution in credits. See 0.3 §6.

## 13. Question bank for 0.2

The archive's [simulation design questionnaire][doc-questions] holds numbered questions on pacing, speed controls, guest identity and motion, art density, business depth, weather realism, automation, grooming, hardware and compatibility. Many were answered by the dual-clock implementation; 0.2 should re-ask the ones that shape the new game.

<!-- Archive links (tag maplibre-final) -->
[agents]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/AGENTS.md
[doc-dualclock]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/dual-clock-implementation.md
[doc-timeops]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/simulation-time-and-operations.md
[doc-questions]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/simulation-design-questions.md
[doc-perf]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/plans/guest-performance-optimization.md
[doc-bench]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/docs/performance/integrated-benchmark.md
[ui-menu]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/MainMenu.tsx
[ui-backdrop]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/MenuBackdrop.tsx
[ui-setup]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/SetupWorkspace.tsx
[ui-sitepicker]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/sitePicker.ts
[ui-loading]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/ResortLoadingScreen.tsx
[ui-workspace]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/GameplayWorkspace.tsx
[ui-window]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/GameWindow.tsx
[ui-dashboards]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/MountainDashboards.tsx
[ui-traildetail]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/TrailDetail.tsx
[ui-liftdetail]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/LiftDetail.tsx
[ui-vibe]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/GuestVibeCheck.tsx
[ui-load]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/LoadGameModal.tsx
[ui-settings]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/Settings.tsx
[ui-mapmgmt]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/MapManagement.tsx
[ui-credits]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/CreditsPanel.tsx
[ui-console]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/DeveloperConsole.tsx
[ui-glab]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/GraphicsLab.tsx
[ui-wlab]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/WeatherLab.tsx
[ui-css]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/ui.css
[ui-gwcss]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/gameWindows.css
[ui-analysis]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/analysisLayers.ts
[ui-search]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/SearchBox.tsx
[ui-layers]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/LayerPanel.tsx
[ui-contrib]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/mapContribution.ts
[ui-renderprofile]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/renderProfile.ts
[ui-lock]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/constructionLock.ts
[ui-lease]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/mapInteractionLease.ts
[ui-liftmodel]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/liftControllerModel.ts
[ui-xsection]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/trailCrossSection.ts
[ui-gradeengine]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/terrainGradeEngine.ts
[ui-connectivity]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/app/guestConnectivity.ts
[d-keybinds]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/keybinds.ts
[d-ingest]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/terrainIngest.ts
[d-package]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/terrainPackage.ts
[d-elevation]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/elevation.ts
[d-overpass]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/overpassConfig.ts
[d-wservice]: https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/weather-service
[d-wengine]: https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/weather-engine
[d-weather]: https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/weather
[d-cover]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/fourClassCover.ts
[d-coveredit]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/coverEdit.ts
[d-lifts]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/lifts.ts
[d-trails]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/trails.ts
[d-skinodes]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/skiNodes.ts
[d-topology]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/topology.ts
[d-network]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/network.ts
[d-roads]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/roads.ts
[d-pond]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/pondEarthwork.ts
[d-damearth]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/damEarthwork.ts
[d-damanalysis]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/damAnalysis.ts
[d-hydraulics]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingHydraulics.ts
[d-guns]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingGuns.ts
[d-snownet]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowmakingNetwork.ts
[d-buildings]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/buildings.ts
[d-bsite]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/buildingSiteAnalysis.ts
[d-bmesh]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/buildingMesh.ts
[d-dcmodel]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/dualClock/model.ts
[d-dcengine]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/dualClock/engine.ts
[d-dctypes]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/types/dualClock.ts
[d-wear]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/dualClock/wear.ts
[d-snowsim]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snowSimulation.ts
[d-snow]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/snow.ts
[d-guests]: https://github.com/ljs294/ski-area-design-challenge/tree/maplibre-final/src/guestSimulation
[d-tickets]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/guestSimulation/ticketFinance.ts
[d-economy]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/guestSimulation/phase3Economy.ts
[d-random]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/guestSimulation/random.ts
[d-saveschema]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/gameSaveSchema.ts
[d-savetypes]: https://github.com/ljs294/ski-area-design-challenge/blob/maplibre-final/src/types/gameSave.ts
