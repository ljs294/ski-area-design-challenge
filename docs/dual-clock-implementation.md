# Dual-clock simulation implementation

## Delivery ownership

The Astra High (`gpt-6-astra`, high) coordinator delegates and reviews only. Terra High (`gpt-5.6-terra`, high) performs all implementation, testing, browser verification, and integration in three workstreams: dual-clock domain and persistence; console, lift read models, and guest rendering; and one shared-file integration owner for MapView, protocol/runtime wiring, and acceptance workflows.

This records the implemented working-tree architecture and its verification, against base commit `9988f4f774de92103c497788a69cf777c147d4a3`. It supersedes the earlier discussion drafts under `docs/plans/`. There is no implementation commit SHA yet.

## Player behavior

New games use schema 17 and independent macro and micro clocks. The existing schema 1–16 load path remains isolated and writes schema 16. New games are not a silent conversion of old resorts.

The macro clock runs at 40 seconds per real second at 1×, with 08:00–16:00 operating hours and a 24-week winter. Presets are 1×, 2×, 4×, 8×, 16× and 64×. Individual movement uses 1, 1.75 and 2.75 micro seconds per real second at the first three presets. Higher presets display aggregate trail flows and queue counts. Selected groups continue hidden detailed simulation at one micro second per 40 macro seconds. The official time display always uses the committed macro clock.

Click the date/time to select Next Opening, Day, Week, Month or Season and preview the destination. Skip to Winter advances applicable weather through summer. Advances execute in the persistent worker, expose progress and cancellation, and finish paused. Interrupted advances retain their destination for explicit Resume; Follow at 1× suspends an advance and slows the whole simulation.

Guests have representative personal visits, bounded histories and independent run/spending totals. A selected departed guest remains as a Visit Completed card until the player chooses another guest or the mountain overview. Auto-tracking is opt-in and camera following is separate. Promotion from aggregate flow explicitly records when detailed tracking began.

Trail entrances release representatives in FIFO order: the first eligible arrival releases immediately, and later arrivals receive individual slots at least two micro-clock seconds apart. A guest continuing through split segments of the same trail keeps moving without another entrance wait. Trail-entry state is removed when a guest, representation or route becomes invalid, and eligibility is checked again before release. Each representative keeps a stable keyed speed sample; trail duration uses the existing segment baseline multiplied by `0.6 + 0.8 × ability` and `1 + 0.15 × Z`, where `Z` is a deterministic standard-normal value truncated to ±3.

Thin-trail warnings appear in the requested utility dropdown. They are advisory and do not close trails or change speed. At least 20% of mapped area below 10 cm activates a warning; less than 10% clears it. Acknowledgment does not resolve it. An authoritative missing exit route can issue a critical pause. Wind holds, pond depletion and equipment failures are not fabricated from static design data.

## Ownership and integration

- [`useResortSimulation.ts`](../src/app/useResortSimulation.ts) selects legacy or dual-clock behavior. The legacy hook supplies prepared weather and projections without running another advancing clock for new games.
- [`useDualClockRuntime.ts`](../src/app/useDualClockRuntime.ts) owns the persistent worker, coalesces ordinary advancement and rejects stale publications after control commands or replacement. Pause, save, cancel and following invalidate pending weather preparation. Hidden ordinary play pauses without accumulating wall-time debt.
- [`dualClock.worker.ts`](../src/app/dualClock.worker.ts) owns [`DualClockEngine`](../src/dualClock/engine.ts). Headless and visible operation use the same domain kernels. Approximately 8 ms slices yield through MessageChannel; progress is limited to 10 Hz. Weather tiles are staged separately and committed with both clocks. Cancellation discards incomplete tiles.
- [`types/dualClock.ts`](../src/types/dualClock.ts) is the dependency-neutral contract boundary. Macro and micro positions have distinct branded types. The future operation-job interface includes assignment, capabilities, schedules, prerequisites, automation, reservations, consumption and typed effects. It does not dispatch grooming equipment yet.
- [`model.ts`](../src/dualClock/model.ts) owns the versioned designer defaults. Fixed presets are exposed to players; wear, population and clock tuning stay in configuration and checkpoints.

The aggregate model conserves admissions, active guests and departures. Cohorts retain travel, queue and amenity states. Lift seats use rated capacity and fractional credits; unused integer seats are not banked. Tickets are recognized once at admission in integer cents, with a daily price lock. Base café services reuse existing facility definitions, with access/service duration, opening hours, capacity, stock and affordability constraints. Personal sample spending never posts aggregate revenue.

Shared guest kernels provide condition-aware route scoring, price elasticity, needs, facility definitions and surface quality. The new cohort engine does not replace the existing Phase 1–7 engine used by legacy saves. Existing legacy injury/patrol, road-vehicle and lodging simulations are not running inside the new cohort engine; the initial dual-clock scope is admissions, mountain travel, queues, amenities, visits and snow effects.

## Geometry, snow and rendering

Prepared routes use the authoritative network edge's saved centerline as one distance-indexed route, preserving bends, direction and junction endpoints. Centerlines are validated in a translated local-meter frame against saved trail polygons, including holes; malformed or invalid centerlines produce a hidden empty route rather than a shortcut. Consecutive duplicate vertices are removed. Geometry edits retain snapshots for journeys already in progress. Drawing, interpolation and hit testing use the same route, while disconnected or sparse publications hold the authoritative point; camera following uses that same published position. Guest dots are 9 CSS pixels with the existing pixel-density handling and colors, and retain the existing eight-pixel hit radius.

The existing WebGL guest layer remains authoritative for rendering and hits. Route interpolation runs without per-frame polygon operations or repeated route-length calculation. Aggregate overlays occupy the existing guest contribution/order. Returning individual representations initialize while hidden and fade in. Ordinary headless frames contain no guest points. Movement uses transferred typed columns (33 bytes per allocated slot plus identity/route dictionaries), with a bounded returned-buffer pool. Main-thread decoding owns its points before transferring the buffer back. Rich inspection is bounded independently of movement publication. Snow publication is separate, at most once per second during ordinary play, using transferred dirty rectangles or a full replacement when appropriate.

Authoritative completed trail traversals allocate skier-distance across cached footprints. Surface exposure and depth loss do not depend on visible lanes, viewport or sample count. Defaults are 30 m reference width, 20° reference slope, a 0.5–2 slope factor, 0.005 mm loss per slope-weighted passage, and dry-surface thresholds of 200/800 passages. Wet and icy surfaces are preserved. Fresh-snow refresh resets exposure. Traffic loss and trace-cutoff removal have separate volume ledgers, and depth is clamped nonnegative.

Chronological hourly weather uses a persistent natural-snow stepper with cached terrain fields and resumable 2,048-cell tiles. Weather precedes traffic at coincident accounting boundaries. Updated snow feeds the heat map and shared guest surface-quality rules. Overlapping warning footprints are unioned before computing affected area.

## Persistence

Schema 17 stores the versioned checkpoint inline with the save: both clocks, configuration, optional guest-movement configuration, cohorts, residual credits, integer ledgers, representative state, optional trail-entry queue timing and waiting state, selected visit, precise snow and exposure, retained journey geometry, signals and any suspended destination. Earlier schema-17 checkpoints receive movement defaults and empty entry queues while existing in-progress deadlines are preserved; new speed calculations apply to subsequent journeys. Browser saves use IndexedDB; Electron uses the existing filesystem save IPC. Terrain is flushed first, and a topology revision check rejects a save assembled from mismatched design and simulation states.

The legacy centimeter snow encoding remains compatible. Authoritative schema-17 depth and exposure are stored separately at simulation precision, so repeated JSON save/load does not erase small wear. Invalid or missing schema-17 authority produces a recoverable error rather than generating a replacement simulation. Loads pause both clocks. Weather package garbage collection includes IndexedDB save references.

## Current approximations and release limits

These are material limits, not measured guarantees:

- Macro admissions and lift-service credits use canonical one-minute accounting intervals, subdivided at actual travel and amenity deadlines. Deadline precision is one millisecond; unused service seats cannot accumulate across intervals.
- A traversal's distance is distributed uniformly over its trail part's footprint when it completes. Split-edge distance is conserved, but the initial model does not localize wear to an individual sub-edge strip or a skier's lane.
- WebGL performs viewport clipping; ordinary actor simulation remains bounded by the representative limit. Main-thread movement decoding still allocates bounded point records for the existing renderer adapter.
- Very complex invalid centerlines are not guaranteed to be repairable. Detailed carving, collisions, equipment dispatch, continuous snowmaking and advanced moguls remain deferred as requested.
- Performance results below cover a pinned one-lift/one-trail CPU fixture and a separate production renderer fixture. They do not certify integrated large-resort or Electron performance. A three-winter retained-state soak passes, but a full weather/GPU multi-season soak and all requested edge-case combinations are not yet established.

## Verification and measured performance

The focused domain tests cover speed/headless/sample invariance, conservation, café limits, precise repeated restore, staged-weather cancellation, retained interrupts, selected-visit completion, immediate aggregate promotion, topology edits, centerline route geometry and a polygon hole. Wear tests include wider trails, partial cells, resolution conservation and fresh-snow exposure reset. UI tests cover the completed card independently of truncated history. The dedicated deterministic browser workflow exercises all presets, actual day advancement, warnings, precise IndexedDB save/reload, paused restoration and subsequent readiness, real-worker cancellation, transferred movement-buffer recycling, and the legacy schema-16 path. The final aggregate gate passes 1,282 unit tests; the two opt-in domain performance/soak tests were also run separately and passed.

Run the required gate with `npm.cmd run check`, and the dedicated browser workflow with:

```powershell
node scripts/runE2E.mjs --project=feature-workflows dual-clock.spec.ts
```

Hardware measurements on Ryzen 5 5600X, approximately 16 GiB RAM:

The [raw reference measurements](dual-clock-benchmarks.json) preserve the fixture reports alongside this document.

| Fixture | Result | Scope |
| --- | --- | --- |
| Prepared 512×512 snow grid, 10,000 daily demand, one lift/trail, two café services | Week 2.65 s; 24-week winter 53.94 s; initialization 110 ms | Node 22.20 CPU kernel; full hourly weather; no worker publication/UI overhead |
| Same resort in the shipped browser worker, hardware graphics | Cold week 2.31 s; warmed week 1.74 s; winter 40.92 s; initialization 185 ms | Chromium 149.0.7827.55; progress and final snow transfers included; 391 replies / about 5.7 MB; main-thread frame p95 16.8 ms |
| 3,000 guests, 1920×1080, RTX 3060 Ti, Chromium 149.0.7827.55 | About 60 FPS at 1×/2×/4×; p95 frame 18.2/18.1/18.1 ms; p99 19.2/18.5/18.5 ms | Production MapLibre custom renderer with 10 Hz synthetic movement; simulation paused; external map tiles blocked |

The CPU fixture admitted 1,680,000 guests, retained zero ordinary guests/cohorts at winter end and reported approximately 12.2 MiB heap. This is an endpoint measurement, not a heap-growth soak. A separate three-winter soak checks population conservation, retired cohorts, selected-group/history limits, bounded ledgers and stable serialized checkpoint size after the ledger window fills. The hardware browser workflow measured 8.5 ms from Cancel to the updated UI and 16.4 ms for separate worker cancellation acknowledgment at a coherent checkpoint. These are individual measurements, not latency percentiles. SwiftShader measured a 272 ms visual cancellation response in a diagnostic run; the CI workflow checks eventual correctness while the opt-in hardware workflow enforces the 100 ms visual budget.

A diagnostic browser run inheriting the test suite's SwiftShader software graphics took 10.98 s for the cold week and 51.39 s for winter; its cold week missed the five-second target. The hardware reference run above explicitly uses ANGLE Direct3D11. These results should not be generalized to software graphics or lower-end hardware.

Opt-in measurement commands write JSON under `test-results/dual-clock/`:

```powershell
$env:DUAL_CLOCK_BENCHMARK='1'
node node_modules/vitest/vitest.mjs run src/dualClock/performance.test.ts --testTimeout 180000
$env:DUAL_CLOCK_GPU='1'
node scripts/runE2E.mjs --project=feature-workflows dual-clock-performance.spec.ts
node scripts/runE2E.mjs --project=feature-workflows dual-clock.spec.ts
$env:DUAL_CLOCK_WORKER_BENCHMARK='1'
node scripts/runE2E.mjs --project=feature-workflows dual-clock-worker-performance.spec.ts
$env:DUAL_CLOCK_SOAK='1'
node node_modules/vitest/vitest.mjs run src/dualClock/soak.test.ts --testTimeout 180000
```

The broader existing browser suite is not green: construction workflows expect tools outside the current Toolbox, and the snow-layer workflow expects a Close button after the layer menu has already dismissed. The dedicated new/legacy workflows pass; this does not substitute for repairing and completing the full browser release gate.
