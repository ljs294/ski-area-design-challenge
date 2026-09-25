# Guest simulation and rendering performance plan (MapLibre, preserved record)

**Audience:** the project owner and coding agents. **Baseline source:** commit `68750a0`. **Status:** preserved record, not scheduled.

The MapLibre game is archived on branch `archive/maplibre` (tag `maplibre-final`). Development continues as a from-scratch Unity rebuild; see [Unity rebuild roadmap](unity-rebuild-roadmap.md). Phase A below was proposed and approved in principle, but was **not executed**. It remains a ready plan if the archived game ever needs a performance release. Its lessons carry into the Unity rules (§9).

## 1. Summary

Guests stutter during play at every speed and while the camera moves. **The cause is not drawing the dots.** With the simulation paused, the custom WebGL guest layer already holds about 60 FPS with 3,000 guests. The stutter comes from three other sources, in order of impact:

1. **Main-thread churn on every simulation publication (about 10 Hz).** Each publication rebuilds weather fields, re-renders the whole `MapView` twice, and churns map sources. This happens regardless of guest count; a 32-guest run already misses the frame budget.
2. **Per-guest CPU work on every rendered frame** in `GuestGpuLayer.render`. This scales with guest count: terrain queries, hit-index rebuilds, and CPU interpolation with a full buffer reallocation.
3. **Worker throughput.** On a realistic eight-lift resort, the dual-clock engine runs slower than real time, so publications arrive late and dots stop and go.

## 2. Evidence

| Measurement | Result | Tier / scope |
|---|---|---|
| Guest renderer, 3,000 guests, sim **paused**, 10 Hz synthetic movement, RTX 3060 Ti, 1920×1080 | ~60 FPS; frame p95 18.2 / 18.1 / 18.1 ms at 1×/2×/4× | Opt-in GPU spec, not integrated (`docs/dual-clock-benchmarks.json`) |
| Integrated diagnostic `task2-full-browser-b15`, CI fixture, **32 representatives** (157–209 active guests), 30 s window | Frame p50 16.7 ms, **p95 33.4 ms**, max 66.7 ms; 1,536 of 1,802 frames delivered (85%); **12 long tasks of 50–116 ms**; **pause response 310 ms**; **JS heap 61 → 172 MB**; React publish→commit p50 20.6 ms, max 280 ms | Diagnostic, identity-invalid; not a qualification |
| Same run, snow tiles | 1,265 tile renders; each ~1 Hz snow revision re-requests ~44 tiles (~20 PNG tile encodes per second) | Diagnostic telemetry |
| Jackson fixture (8 lifts, 25 trails), 1,000 representatives at 1× | A 150 s-equivalent took **11 min 41 s**, about 4.7× slower than real time | Fixture preparation, not qualified ([integrated benchmark](../performance/integrated-benchmark.md)) |
| Compact-frame vertex kernel (Node) | 0.46 / 0.62 / 2.04 ms per frame at 1k / 3k / 10k guests | Entrance-fallback path only |

**Frozen budgets:**
- Frame p95 ≤20 ms, p99 ≤33.3 ms, fewer than 1% of frames over 50 ms.
- Selection/pause p95 ≤100 ms.
- Snow patch visible ≤250 ms.
- Retained heap growth ≤10%.

## 3. Pipeline at `68750a0` (schema-17 dual clock)

```
dualClock.worker  (DualClockEngine)
  advanceTo() in 8 ms slices
  publication(): builds N point objects ─► structuredClone({..., points})   (clone thrown away)
                 packs DualMovementFrame (33 B/slot) + ids[] strings        (strings cloned)
  geometry(): full Record<routeId, PreparedRoute> whenever transfer routes churn
────────────── postMessage ~10 Hz ────────────────────────────────────────────────────
main thread
  onmessage ─► decodeDualMovement(): N new objects (spread + nested motion)
            ─► setPublication() ─► useResortSimulation
                                     └─ setLastPublication() DURING RENDER ─► MapView renders ×2
                                          ├─ useGameSimulation: `current` = new object every time
                                          │    ├─ createTerrainThermalModel(512² + 6 arrays)
                                          │    ├─ issueGameForecast(168 h, Intl per hour)
                                          │    └─ setLight/setSky, weather <canvas> torn down/rebuilt
                                          ├─ flow GeoJSON setData (every render, even when empty)
                                          └─ GuestGpuLayer.setPoints(): 2 Maps + arrays + bufferData
            ─► snow.replace() ~1 Hz ─► terminate + respawn tile workers ─► setTiles ─► ~44 PNG tiles
MapLibre frame (60 Hz)
  GuestGpuLayer.render(): per guest Map.get + routeLanePosition (new tuple) ─► bufferData(all)
                          every 50 ms per guest: queryTerrainElevation (coveringTiles, uncached)
                          rebuildHitIndex(): getBoundingClientRect + id string + frozen array per guest
```

## 4. Root causes (verified in code, ranked)

| # | Location | Defect | Rate | Phase A step |
|---|---|---|---|---|
| 1 | `src/app/useGameSimulation.ts:588-609, 599-603, 636-687` | `current` is memoized on `clock.calendarDate`. `weatherAtSession` resolves to a whole hour (`src/weather/weatherSession.ts:414`) but returns a new object, so the thermal model, forecast, map lighting and precipitation canvas all rebuild. | ~10 Hz | A1 |
| 2 | `src/app/useResortSimulation.ts:86` | `setState` during render: `MapView` (1,799 lines, one `memo` in all of `src/app`) renders twice per publication. | ~20 renders/s | A2 |
| 3 | `src/app/guestGpuLayer.ts:210-232, 524-528` | `queryTerrainElevation` per guest, hidden guests included. In MapLibre 5.24 each call recomputes `coveringTiles` uncached. | ~60k/s at 3k guests | A3 |
| 4 | `guestGpuLayer.ts:576-652` | Hit index rebuilt every frame: `getBoundingClientRect()` (forced layout), a `padStart` id string, a frozen `[lng,lat]` and a Map insert per guest. | per frame × N | A3 |
| 5 | `guestGpuLayer.ts:512-520, 654-659`; `src/dualClock/geometry.ts:188-199` | CPU route interpolation with a string-keyed lookup and a tuple allocation per guest, then a `bufferData` reallocation of the whole buffer. The shader `mix` is a no-op in this mode. | per frame × N | A3 |
| 6 | `src/dualClock/engine.ts:776-791`; `src/app/dualMovementPublication.ts:45` | Points are built, cloned and discarded in the worker, then rebuilt as objects on the main thread and routed through React. | ~10 Hz × N | A2, A3 |
| 7 | `src/app/snowProtocol.ts:53-72`; `src/app/snowTileWorkerClient.ts:22-46` | Each snow publication kills and respawns tile workers, clears the whole cache, and re-renders and re-encodes every visible tile. | ~1 Hz | A4 |
| 8 | `src/app/useMapGuestSimulationFeature.ts:108-113`; `src/app/useGuestPortalController.ts:79-104` | Unstable `options.dual` identity triggers a flow `setData` per render. An effect with no dependency list re-subscribes map listeners per render. | per render | A2 |
| 9 | `src/dualClock/engine.ts:716-759, 587-601, 544-556` | Every cohort deadline runs `macroMinute` (per-lift filter/sort, trail-signal scan, conditions refresh). Each step runs 3 O(N) `reconcileTrailQueues`, rebuilds the event heap from scratch, and does filter/Set churn. Promotion from aggregate is O(N²). Transfer-route churn forces full geometry re-sends. | worker, per step | A0, A5, A3 |

Secondary findings:
- `new Intl.DateTimeFormat` per toolbar render.
- `SimulationWarnings` sorts with `localeCompare` per render.
- Guest dot size uses `window.devicePixelRatio` rather than the map's pixel ratio.
- Hidden and departed guests are still uploaded and drawn, then discarded in the fragment shader.
- The camera-follow fallback does a linear `.find`.
- Legacy schema ≤16: a per-guest edge Map rebuild in `src/guestSimulation/guestRenderFrame.ts:89`, and per-event safety/patrol snapshot sorting.

## 5. Target pipeline (if Phase A were executed)

```
dualClock.worker
  advanceTo()  (exact-equivalence hot-spot fixes, golden-hash gated)
  writeMovement(columns)  ─ no point objects, no clone
  geometry deltas {added, removed} only when routes change
────────────── postMessage ~10 Hz (movement buffer transferred, then recycled) ─────────
main thread
  onmessage ─► dualPublicationStore.publish(publication, movement)
     ├─ imperative ─► guestRouteAtlas.apply(delta)          (flat typed arrays, Z profiles)
     ├─ imperative ─► GuestGpuLayer.setDualMovement(columns) (SoA copy, buffer recycled)
     ├─ React selectors: controls immediate · HUD per displayed minute · dashboards ≤4 Hz
     └─ weather keyed by hour index ─► thermal/forecast/lighting/canvas ≤ once per game hour
  snow patch ─► live tile workers patched ─► only tiles intersecting the dirty rect re-render
MapLibre frame
  GuestGpuLayer.render(): SoA loop · cached segment cursors · atlas Z · bufferSubData · 0 allocations
  hitTest(): lazy index, rebuilt at most once per drawn frame and only when queried
```

## 6. Phase A: proposed, not executed

Rules for every step:
- Characterize before moving behavior.
- No `GameSave` change.
- The schema ≤16 legacy path is untouched except A3's behavior-equal hoist.
- AGENTS.md map and interaction invariants are preserved.

**A0: Baseline and characterization.**
- Golden trajectory test `src/dualClock/trajectory.test.ts`: deterministic fixtures, visible 1× and 4×, aggregate 64× and a headless advance. Hashes `engine.checkpoint()` every macro-hour and at the end, plus a per-minute digest of clock, flow and representative state.
- Opt-in `npm run benchmark:dual-clock-throughput` (macro-s per wall-s, `--cpu-prof`), on the CI fixture and on Jackson when present.
- Integrated diagnostic baselines for Jackson `detailed-1000-1x`, `detailed-3000-4x` and `aggregate-64x`.

**A1: Weather keyed by hour (exact equivalence).**

```ts
export function weatherHourIndexAtSession(session: GameplayWeatherSession, cursor: string): number {
  return indexAtOrBefore(session.plan.hours, clampWeatherSessionCursor(session, cursor));
}
const hourIndex = session ? weatherHourIndexAtSession(session, clock.calendarDate) : -1;
const current = useMemo(() => /* resolveWeatherHour(session.plan.hours[hourIndex], session.midpoint) */,
  [session, compositeWeek, clock.season, compositeHourIndex, hourIndex]);
const thermalModel = useMemo(() => terrain ? createTerrainThermalModel(terrain) : null, [terrain]);
```

- The forecast memo is keyed on `forecastIssueAt`.
- The thermal field, forecast, `setLight`/`setSky`, the precipitation canvas and the weather overlay go from ~10 Hz to at most once per game hour.

**A2: Publication store.**

```ts
export interface DualPublicationStore {
  get(): DualPublication | null;
  publish(next: DualPublication, movement: DualMovementFrame | null): void;
  subscribe(listener: () => void): () => void;
  subscribeMovement(listener: (m: DualMovementFrame) => void): () => void;
}
export function useDualPublication<T>(store: DualPublicationStore,
  select: (p: DualPublication | null) => T, equal: (a: T, b: T) => boolean = Object.is,
  minIntervalMs = 0): T { /* useSyncExternalStore + cached selection + trailing throttle */ }
```

- Control slices (paused, speed, advance, selected, signals) are immediate.
- The HUD updates only when the displayed minute changes; dashboards at ≤4 Hz.
- Movement and geometry bypass React.
- Remove the render-phase `setLastPublication`.
- Fix the flow `setData` and the listener effect.
- Acceptance: `MapView` no longer renders per publication; pause p95 ≤100 ms.

**A3: Allocation-free guest movement.**
- The worker writes movement columns directly and sends geometry deltas.
- A route atlas holds flat vertices, distances and a per-route Z profile at ≤15 m spacing, sampled from the *rendered* terrain once per profile point.
- The layer keeps SoA state:

```ts
for (let i = 0; i < n; i++) {
  const r = route[i];
  if (r < 0 || !sameLane(i)) { writeAuthoritative(i); continue; }
  const d = (p0[i] + (p1[i] - p0[i]) * t) * atlas.length[r];
  let s = cursor[i]; const last = atlas.start[r] + atlas.count[r] - 1;
  while (s < last - 1 && atlas.distance[s + 1] < d) s++;
  while (s > atlas.start[r] && atlas.distance[s] > d) s--;
  const span = atlas.distance[s + 1] - atlas.distance[s], f = span > 0 ? (d - atlas.distance[s]) / span : 0;
  const o = i * STRIDE;
  out[o] = mercX(atlas.lng[s] + (atlas.lng[s + 1] - atlas.lng[s]) * f);
  out[o + 1] = mercY(atlas.lat[s] + (atlas.lat[s + 1] - atlas.lat[s]) * f);
  out[o + 2] = zAt(r, d); cursor[i] = s;
}
gl.bufferSubData(gl.ARRAY_BUFFER, 0, out.subarray(0, n * STRIDE));
```

- The hit index is rebuilt lazily on query, at most once per drawn frame, with numeric ids and no layout reads in `render`.

**A4: Snow without churn.**
- Tile workers stay alive and are patched in place.
- A per-tile content revision means only tiles intersecting the dirty rectangle re-render.

**A5: Engine throughput, exact equivalence only**, driven by the profile:
- Early-exit `reconcileTrailQueues`.
- O(1) guest lookup instead of the O(N) `find`.
- A non-string event fingerprint.
- Indexed `trackCohort` promotion.
- Skip provably idempotent minute work at non-accounting boundaries.

**A6:** document the landed state.

## 7. Phase B: proposed, not executed

- **B1:** SharedArrayBuffer triple-buffered movement ring, which needs COOP/COEP through an Electron `app://` protocol handler.
- **B2:** GPU route interpolation, using a route atlas `RGBA32F` texture and a `texelFetch` binary search in the vertex shader.
- **B3:** SoA engine state, converted only at the checkpoint boundary.
- **B4:** a spatial hash, worker-side viewport culling (`selectGuestPublicationRows`) and LOD tiers (z15+ individual, z13–15 sampled, aggregate below).
- **B5:** snow as a draped canvas/image source.
- **B6:** engine behavior changes, which need approval:
  - Minute-only signal refresh.
  - A persistent event heap.
  - Capacity-aware representative lift queues.
  - Occupancy-aware route choice.

## 8. Verification (if executed)

- Focused Vitest, `npm run check`, `npm run check:e2e-harness`, and the dual-clock and schema-17 save workflows.
- Golden hashes unchanged.
- Before/after diagnostics for frame percentiles, long tasks, pause response, heap growth and tile renders per patch.
- Manual play on the production build via `launch-game.bat`.

## 9. What carries into the Unity rebuild

No code carries over; the rebuild is from scratch. These lessons carry over as rules in the Unity `AGENTS.md` and architecture:
- The simulation publishes snapshots, and the UI never rebuilds per tick.
- Per-frame paths allocate nothing per agent.
- Fields that change hourly are recomputed when they change, not every tick.
- Agent elevation comes from precomputed samples, never per-agent queries each frame.
- Textures update by dirty rectangle.
- Golden-trajectory determinism tests exist from day one.
- Performance budgets are frozen before features are built.

