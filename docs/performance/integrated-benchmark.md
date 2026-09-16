# Integrated gameplay benchmark

This benchmark measures the supported React application, MapLibre map, shipped
dual-clock worker, guest presentation, snow processing, and persistence together.
The older CPU, worker-only, and direct-point GPU benchmarks remain separate
measurements. Their results are not an integrated gameplay baseline.

## Evidence and qualification

The implementation began from clean commit
`b915aef292da1bc31cd780b3555f3176a78ab5a7`. That commit is an **unmeasured starting
point**. The performance audit's `2f622c8720c9762402817ed634a5143cab87ff9e` is
historical context. Each measured result must identify its actual source commit,
working-tree status, dependency lockfile hash, and build hashes. Do not label a
later instrumented build with either earlier commit's identity.

There are separate deliverables:

| Deliverable | Required evidence |
| --- | --- |
| Deterministic correctness | Offline application workflows and deliberate invalid-workload controls |
| Complex fixture readiness | Validated, hash-pinned real terrain, infrastructure, weather, checkpoints, fonts, and replay |
| Browser hardware qualification | Valid trials on an identified physical adapter with the frozen display and workload settings |
| Packaged Windows qualification | The same workload in the identified release executable, including desktop save and capture |
| Other GPU class | Separate qualification on integrated/discrete hardware not covered by the first machine |

An unavailable fixture, unsupported measurement, or failed packaged launch does
not become a passed gate. Browser results cannot substitute for packaged results.
The current PC is the first qualification lane; its identity does not implicitly
define minimum supported hardware.

Initial system inventory identified an AMD Ryzen 5 5600X (6 cores / 12 threads),
NVIDIA GeForce RTX 3060 Ti with Windows driver `32.0.15.9186`, and Windows 11 Home
build `26200`. The desktop was 2560×1080 at 60 Hz. This is system inventory, not
a measurement or proof that a browser used that GPU. Each trial must still
verify its GL adapter and its own 1920×1080 canvas.

## Fixture and workload contract

The dedicated complex fixture uses the Jackson, New Hampshire / White Mountains
region. Large prepared assets remain in a local package; preparation code and
the immutable manifest belong in Git. Record source requests, acquisition dates,
true returned bounds, grid spacing, elevation range, source resolution, geometry
vertices, route/lane/segment counts, and asset byte sizes and SHA-256 hashes.

The complex resort requires at least eight lifts, 25 interconnected trails,
three reachable amenity destinations, queues, transfers, and a 512×512 snow grid.
The existing snow generator caps each dimension at 512; fixture preparation must
produce the required geometry without changing that production cap. USGS output
bounds must be read from the returned GeoTIFF. The acquisition helper explicitly
disables export aspect adjustment to preserve a square physical footprint.

Scenario demand and amenities are explicit benchmark inputs. They must reach
both initial hydration and subsequent committed resort updates; a checkpoint
alone does not persist daily demand or authored amenity definitions. Ordinary
gameplay defaults and the schema-17/legacy schema-16 save distinction remain
unchanged.

Checkpoints come from deterministic domain progression and checkpoint APIs.
Detailed checkpoints initially contain exactly the requested active population.
Validate occupancy across warm-up and measurement before freezing a fixture;
failed preparation cannot inject or replenish actors during a measured run.

| Scenario | Workload |
| --- | --- |
| Empty | Zero demand and representatives; live clock, weather, terrain, snow, and UI |
| Detailed | 10,000/day demand, separately 1,000 and 3,000 representatives, at 1×, 2×, and 4× |
| Aggregate | The same resort and demand at 8×, 16×, and 64×; advancing flow and the expected detailed-upload behavior |
| Interactive | Recorded pan/orbit/zoom, hover, click, follow, existing guest/snow panels, localized snow change, grading confirmation, and save |
| Visibility | A run with at least 90% of active representatives in view and a separate wide-resort culling run |
| Recovery | Pause/resume, 4×↔8×, cancel season advance, style reload, and exit/reopen |

Accepted detailed intervals retain at least 95% of target active population.
Report daily demand, physical flow, active representatives, visible actors, and
drawn actors separately, including observed minima, medians, and maxima.

Every trial must prove advancing clocks/publications, route changes, snow
revision changes, decoded local terrain tiles, and correct selection. Missing
assets and unexpected external requests invalidate an offline trial. Blocking
remote errors is not evidence that the required terrain or labels rendered.

## Measurement protocol

Use Standard quality, a 60 Hz display, and a verified 1920×1080 physical canvas
for common comparisons. Record CSS viewport, device DPR, actual canvas dimensions,
profile settings, CPU, GPU, driver, OS, browser/Electron versions, actual GL
vendor/backend, refresh, power mode, and thermal/throttling observations. Other
profiles and high-DPR resizing are separate scenarios. A software renderer cannot
qualify a hardware lane.

Warm for 30 seconds, then measure 120 seconds. Keep cold loading separate from
warmed gameplay. Restore the same checkpoint and scenario cache state for every
trial. Run sequentially, with DevTools, video, automatic retries, and heavy traces
disabled during timing. Retain failed attempts and their reasons.

For comparisons, use at least five baseline/candidate pairs with the same seed
within each pair, alternating A/B and B/A order. Calculate percentiles per trial,
then paired changes with a 95% confidence interval across pairs. Frames are not
independent trial replicates. If results are inconclusive, collect five additional
pairs once, then retain the inconclusive result.

Collect independent rAF and map-render timestamps over the full wall-time window,
including the last sample-to-window-end gap. Report longest gaps, expected versus
delivered frames, and last drawn revision. An empty sample or stopped renderer
fails liveness. Where physical presentation timestamps are unavailable, label
measurements as frame-production proxies.

Worker generation, request identity, operation generation, committed document
revision, movement publication sequence, and snow revision remain independent
domains. Cross-thread latency requires aligned time origins or calibrated clocks.
Production runs use revision-correlated React commit markers; React duration
profiling and allocation/GC/heap tracing belong in separate diagnostic runs.

## Frozen budgets

These initial budgets were approved before candidate measurements. A failure is
reported against the original budget; changing a target is a separate explicit
decision that applies equally to both compared versions.

| Metric | Budget |
| --- | --- |
| Frame interval p95 | ≤20 ms |
| Frame interval p99 | ≤33.3 ms |
| Frames over 50 ms | <1% |
| Selection/pause response p95 | ≤100 ms |
| Worker cancellation | ≤250 ms |
| Snow patch to visible response p95 | ≤250 ms while camera motion continues |
| Instrumentation overhead | Frame p95 change ≤3% in enabled/disabled pairs on the same build |
| Retained memory | ≤10% growth after warm-up, with bounded object/cache/worker counts |
| Later optimization regression | No >5% regression in integrated frame or interaction p95 |

Run a 20-minute soak and ten exit/reopen cycles using repeatable quiescent memory
checkpoints. Keep process memory, renderer heap, worker heap, and GPU memory
separate, recording measurement method and availability. Unavailable values are
not zero; allocation estimates do not establish actual VRAM usage.

## Verification and source provenance

Start with the affected deterministic checks. Before a benchmark commit, run
`npm run check`, `npm run check:e2e-harness`, and the matching deterministic
browser workflows. Record gate outcomes and immutable measured SHAs in
`docs/refactors/agent-architecture.md` after the applicable commit. Architecture
documentation describes implemented behavior only.

### Implementation verification record

All implementation and review recorded for this integrated harness was performed
with the Sol Medium model after the execution plan changed. Earlier model
assignments are superseded and are not part of the operational qualification
identity.

The local Jackson asset lane uses these preparation entrypoints:

```text
node scripts/acquireIntegratedTerrain.mjs
npm run prepare:integrated-benchmark
```

The first command is a live-provider acquisition step and is not part of the
offline gate. The acquired local package currently contains a 2000×2000 USGS DEM,
2998×3000 cover data, local NAIP imagery, prepared Daymet/NASA POWER weather,
and pinned Noto glyph files and license. The finalized manifest has SHA-256
`418d9cf6e9ef0afe5de1ce3e91b29336e86417badf592c7a5db371b2dc7afec0`.
It records eight usable lifts, 25 geometry-distinct interconnected trails, three
distinct reachable amenities, 135 nodes, 219 routes/lanes, 211 trail segments,
438 vertices, and a 512×512 production-snow grid with approximately 11.742 m
cells. The empty, 1,000-person, and 3,000-person checkpoint hashes begin
`d3085e13`, `a1d9dce0`, and `2759fb5b`, respectively.

The large assets and generated manifest remain below the ignored
`test-results/integrated-fixtures/jackson` directory. The working tree now adds
`tests/e2e/performance/fixtures/jackson.fixture-lock.json`, which freezes the
manifest SHA, preparation-code SHA, sources, every artifact path/size/hash,
presentation assets, topology inventory, and snow/DEM dimensions. Qualification
still requires a clean immutable commit containing that lock and an exact local
fixture that passes it; the present dirty working tree cannot certify provenance.

Normal-domain preparation observed 3,598 active guests, 1,842 completed runs,
5,398 lift boardings, $1,140 amenity revenue, and walking, skiing, lift-queue,
lift-ride, resting, and departed status diversity. Preparation took about 236
seconds after the assets were loaded. A separate attempted static 150-second
equivalent took 11 minutes 41 seconds for the 1,000-person 1× case, so it was not
used as a qualification substitute. The fixture is structurally validated, but
its 30-second warm-up plus 120-second shipped-worker occupancy and performance
windows remain **unqualified until the actual runner completes them**.

The operational runner and focused diagnostic entrypoints are:

```text
npm run test:e2e:integrated
npm run benchmark:integrated -- --runtime browser --scenario detailed-3000-1x --comparison telemetry-overhead --pairs 5 --fixture test-results/integrated-fixtures/jackson --output test-results/integrated-benchmark/browser --cache-state warm
$env:ELECTRON_RELEASE_PATH='<packaged-executable>'
npm run benchmark:integrated -- --runtime electron --scenario recovery --comparison telemetry-overhead --pairs 5 --fixture test-results/integrated-fixtures/jackson --output test-results/integrated-benchmark/electron --cache-state warm
npm run benchmark:integrated -- --runtime browser --scenario soak --comparison telemetry-overhead --pairs 5 --fixture test-results/integrated-fixtures/jackson --output test-results/integrated-benchmark/soak --cache-state warm --measure-ms 1200000 --reopen-cycles 10
npm run benchmark:integrated -- --runtime browser --scenario high-dpr --comparison telemetry-overhead --pairs 5 --fixture test-results/integrated-fixtures/jackson --output test-results/integrated-benchmark/high-dpr --cache-state warm
npm run benchmark:integrated -- --runtime browser --scenario interactive --comparison telemetry-overhead --tier diagnostic --pairs 1 --fixture test-results/integrated-fixtures/jackson --output test-results/integrated-benchmark/profiling --cache-state warm --profiling heavy
npm run analyze:integrated-benchmark -- --input <comparison-input.json> --output <comparison-report.json>
```

The qualification runner consumes `scripts/integratedBenchmarkScenarios.json`
as the single scenario/target/speed/workflow table. It records each invalid
attempt rather than silently retrying it. Electron trials require an actual
packaged executable and isolated user-data directory; a browser trial cannot
stand in for that lane.

The browser diagnostic executions to date are useful failure-propagation checks,
not performance qualifications. Early runs exposed, in sequence, a schema-17
conflict with the legacy guest hook, incomplete current-format weather
persistence, an ambiguous `status` locator, missing readiness waits after
checkpoint restore, accidental use of the terrain-disabling `flat` query, the
loading-screen force-reveal path, incorrect classification of a weather
acknowledgement as a guest publication, and a diagnostic-only bypass of
checkpoint restore. Later runs separately proved the malformed-input negative,
real App/worker/MapLibre startup, physical zero occupancy for the empty scenario,
and a localized snow update across 1,888 cells. They then failed on harness
assertions for paused aggregate transitions, empty-demand setup, locale-grouped
cell-count parsing, or bounded source/render readiness. Those assertions now use
revision-correlated publications, zero demand for the empty checkpoint,
comma-aware counts, explicit source-loaded waits, and an observed post-readiness
render. The full corrected diagnostic is still running; no short diagnostic
result certifies occupancy, timing, hardware, soak, memory, or packaged Electron
behavior.

The packaged release attempts have also failed before workload execution. The
initial spec module imported a helper that its support facade did not export;
after that import was corrected, Playwright's Electron launcher could not attach
to the packaged Windows GUI executable. The packaged harness now launches the
actual executable as an owned process with a dedicated loopback DevTools port,
connects Playwright over CDP, and uses the existing preload bridge for save and
exit. That path reached the packaged renderer, but the original fixture transport
expanded and duplicated large terrain, weather, imagery, and checkpoint graphs,
causing a 4 GiB Node heap failure. A selected-checkpoint streaming transport now
avoids that amplification. Its first two ordinary-heap attempts reached fixture
persistence and then lost the renderer execution context; staged native
terrain/weather/save persistence plus navigation/crash/exit evidence is still in
progress. It has not completed a packaged workload.

The matching schema compatibility workflow ran with:

```text
node scripts/runE2E.mjs --project=feature-workflows --grep "schema-17 save loading"
```

It passed seven tests in 1.0 minute and skipped two opt-in comparison cases. The
workflow covers schema-17 restore, weather readiness, save/reopen, and absence of
the legacy worker for schema-17 games. Its portal-bearing schema-16 case uses the
normal Guest Entrance and prepared-weather transition, observes a legacy worker
publication, observes no dual-clock worker, and retains the schema-16 write path.

Both Playwright configurations currently collect their intended source tests:

```text
node ./node_modules/@playwright/test/cli.js test --config=playwright.config.ts --project=integrated-diagnostic --list
node ./node_modules/@playwright/test/cli.js test --config=playwright.integratedBenchmark.config.ts --project=integrated-electron --list
```

The first command listed three browser diagnostic tests in one source file; the
second listed two packaged Electron tests in one source file. Generated copies
under `release/` are excluded from the Vitest source suite. A prior aggregate
attempt discovered three failures only in copied Weather Lab tests beneath that
generated directory; those were not source-test failures and the attempt was not
a successful aggregate gate.

The initial scenario/checkpoint slice passed the following focused checks:

```text
node ./node_modules/vitest/vitest.mjs run src/dualClock/engine.test.ts src/integratedBenchmarkScenario.test.ts src/app/resortSimulationInput.test.ts --testTimeout 10000
node ./node_modules/typescript/bin/tsc --noEmit --pretty false
```

The test run passed 36 tests in three files. It exercised exact population
preparation, failed-preparation rollback, scenario isolation, restored
publication counts, and the 150-second-equivalent population envelope at 1×,
2×, and 4× on diagnostic terrain. These results do not validate the real Jackson
fixture or hardware timing. Later cross-cutting changes require the final gates
in addition to this slice's evidence.

The comparison analyzer passed its 12 focused Node tests,
plus its direct ESLint and syntax checks:

```text
node --test scripts/analyzeIntegratedBenchmark.test.mjs
npx eslint scripts/analyzeIntegratedBenchmark.mjs scripts/analyzeIntegratedBenchmark.test.mjs --max-warnings=0
node --check scripts/analyzeIntegratedBenchmark.mjs
```

Those tests cover deterministic percentile and bootstrap calculations, contiguous
alternating pair order, comparison identity rejection including CSS viewport and
actual GL identity, retention of early invalid attempts without samples,
measurement-window and sample minimums, per-condition and per-trial absolute
gates, separate fixed 3% instrumentation and 5% optimization comparisons, the
single five-pair extension, and the command-line report path. Its output separates
the paired comparison conclusion from condition A and B qualification, so a
historical failing baseline remains visible without disqualifying an improved
candidate. This is implementation evidence only; the analyzer has not produced a
qualification result.

The dedicated runner and hardware preflight passed 15 focused Node tests:

```text
node --test scripts/integratedBenchmarkHardware.node-test.mjs
```

They cover hardware availability, deterministic build hashes, five-pair AB/BA
planning, telemetry A-off/B-on semantics, every shared scenario mapping,
population-versus-speed separation, immutable checkpoint seeds and clean source
identity, qualification windows, software-GL and framebuffer rejection, runner
identity preservation, raw clock/occupancy/revision summaries, and nonzero exit
with a retained invalid artifact on packaged-launch preflight failure. This is
runner infrastructure evidence; it does not execute a qualification workload.

Keep attribution with prepared assets:

- [USGS NAIP imagery](https://www.usgs.gov/centers/eros/science/usgs-eros-archive-aerial-photography-national-agriculture-imagery-program-naip) is identified by USGS as public-domain material; retain the actual scene IDs, year, and agency.
- [ESA WorldCover 2021 v200](https://esa-worldcover.org/en/data-access) uses CC BY 4.0. Retain its dataset citation and attribution: © ESA WorldCover project 2021 / Contains modified Copernicus Sentinel data (2021) processed by ESA WorldCover consortium.
- Retain the [Noto font license](https://raw.githubusercontent.com/notofonts/latin-greek-cyrillic/main/OFL.txt) alongside locally prepared glyph files and record each original glyph URL and hash.
- Record the weather package's actual provider policy, source window, quality,
  content identity, and terrain binding. Deterministic development weather must
  remain labeled as such; it is not provider-observed weather.
