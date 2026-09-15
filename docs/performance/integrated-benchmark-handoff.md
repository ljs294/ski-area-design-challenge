# Integrated benchmark shutdown handoff

## Task 2 controlled snow and browser diagnostic checkpoint (2026-09-15)

The public MapLibre reducer isolated transparent coarse DEM fallback tiles as the first renderer
divergence. `src/app/resortTileEngine.ts` now pads DEM tiles whose true geographic tile bounds
intersect the terrain package while preserving real in-bounds elevations and leaving wholly outside
tiles transparent; `src/app/resortProtocols.ts` applies the same DEM-only behavior in the worker and
main-thread fallback. Focused boundary, coarse-tile, outside-source, and elevation regressions pass.
The pre-fix anchor is `test-results/snow-reproducer/attempt-1789502465693`; corrected reducer runs
include `attempt-1789502793394` and `attempt-1789502831048`.

Revision-correlated framebuffer proof uses `gl.readPixels` inside a MapLibre render callback. The
accepted focused artifact is
`test-results/integrated-benchmark/task2-snow-demand-focused-b11`: source revision 3, matching
generated covering tile, patch hash `e0cebea9` to `311489c0`, unchanged control `9062e03e`, and
matching protocol/grid color change. Temporary private MapLibre probes were removed. Qualification
timing does not include framebuffer probes; source publication latency is labelled separately and
visible latency remains unavailable outside the focused marker.

Accumulated condition-B partial runs have separately passed snow/style restoration, deterministic
shipped-worker cancellation, the measured window, exact GPU draw-to-React publication identity,
committed pause, and atomic terrain/snow/map readiness. This is not a final full-run success: b18
timed out at post-grade final readiness. Runs b16/b17 exposed a harness ordering error: grading happened
before `restoreSelectedCheckpoint`, which intentionally discarded the unsaved road to freeze the
original measurement workload. Their retained `roads: []` evidence is therefore correct checkpoint
restoration behavior, not a product persistence defect. Grading now runs once after measurement and
before final pause/readiness/save, so the strict save/reopen proof applies to a legitimately committed
post-window road without contaminating measurement. Product preview capture remains open for Task 4;
Playwright screenshots are not product capture evidence.

Current focused checks: scoped E2E TypeScript (`tsc -p tsconfig.e2e-performance.json --noEmit`)
passes; runner/configuration/analyzer Node tests pass 22/22; browser Playwright collection lists three
tests. Future Playwright auxiliary output is run-specific. The b17 launch used the old static output
directory and overwrote b16's static screenshot/error context; b16's owned JSON artifacts remain.
Current ownership remains with the single Sol Light implementation owner. Task 2 is not complete
until the reordered graded road survives save/reopen and the full browser diagnostic exits successfully.

Pause checkpoint: the reordered full run b18 is retained at
`test-results/integrated-benchmark/task2-full-browser-b18`. It passed the measured workload and
post-window grading, then timed out at the unchanged 30-second final readiness contract before save.
Its failure artifact is the run-specific
`diagnostic-browser-ci-diagnostic-telemetry-overhead-p1-b-attempt-1789510176009-r0.source-readiness.failure.json`;
the UI observation shows the graded road present. No product save assertion ran in b18.

The reduced post-grade App diagnostic is implemented behind
`INTEGRATED_POST_GRADE_DIAGNOSTIC=1` in the existing snow-demand path and retains atomic source flags,
`map.loaded`, protocol telemetry, and in-render framebuffer evidence on every poll. Attempts b19 and
b20 failed only because the fixed framebuffer ROI was outside the camera established by grading;
those artifacts are retained. The corrected fixed-camera attempt b21 passed in 20.7 seconds with
runner exit 0 at `test-results/integrated-benchmark/task2-post-grade-focused-b21`. It proves that a
paused App can grade, render real framebuffer pixels, and reach terrain/snow/map readiness within the
same 30-second bound. It does not yet reproduce the full run's play-through-grading then pause order.

Exact latest checks before the requested pause:

- `npx tsc -p tsconfig.e2e-performance.json --noEmit`: passed.
- Browser Playwright `--list`: three tests collected.
- Runner/configuration/analyzer Node checks: 22/22 passed.
- Reduced post-grade diagnostic b21: one Playwright test passed, command exit 0.
- Full condition-B diagnostic b18: failed only at final source readiness; no qualification result.

No owned process is active. No commit or qualification SHA exists. Task 3 and Task 4 have not started;
product exit capture remains explicitly open for Task 4. The next bounded Task 2 step is to make the
reduced diagnostic match the full lifecycle exactly (play through grading, pause, then retain atomic
source/protocol/framebuffer readiness), which should distinguish live-source settling from a harness
state mismatch without another measured workflow. Estimated remaining Task 2 work is 2-4 engineering
hours if that reduced case identifies one harness correction, with higher risk if it exposes a real
post-grade source lifecycle defect. Tasks 3 and 4 remain separate, larger work items.

## Task 1 configuration and lifecycle checkpoint (2026-09-15)

The active integration owner separated fixture selection (`ci` or `jackson`) from run tier
(`diagnostic` or `qualification`). `scripts/integratedBenchmarkConfiguration.mjs` is now the shared
configuration consumed by the runner, Playwright configuration, and both browser/Electron runtime
specs. `scripts/integratedBenchmarkScenarios.json` remains authoritative for population, speed,
workload, profile, viewport, canvas, and DPR, and now includes the explicit 32-person
`ci-diagnostic` scenario. Jackson diagnostics load the locked Jackson fixture without becoming
qualification trials; CI qualification is rejected. Diagnostic trials may retain valid functional
results, while the comparison analyzer rejects every diagnostic-tier trial before paired analysis.

Shared runtime support in `tests/e2e/performance/support/integratedRuntime.ts` owns terminal loading
overlay handling (absent, or completed with non-intercepting pointer events), staged installation
failure retention around the existing IndexedDB/desktop-preload storage branch, checkpoint
restoration, independent terrain/snow/imagery source readiness plus a post-readiness render, named
failure stages, and bounded failure JSON retention. Both specs call the shared staged finalizer for
fixture load and persistence. The browser retains its actual page observations in `afterEach`.
Electron instead captures its native CDP window body/URL, bounded process stdout/stderr, exit code,
and runtime events inside its owned `try/catch/finally` before closing the release; teardown failure
is retained separately. The
named stages are `launch`, `fixture-installation`, `boot`, `source-readiness`, `workload-readiness`,
`interaction`, `measurement`, and `teardown`.

Focused verification for this checkpoint:

- `npx.cmd vitest run tests/e2e/performance/support/integratedRuntime.test.ts tests/e2e/performance/support/integratedFixtureTransport.test.ts --testTimeout 10000`: 2 files, 6 tests passed.
- `node --test scripts/integratedBenchmarkConfiguration.test.mjs scripts/analyzeIntegratedBenchmark.test.mjs scripts/integratedBenchmarkHardware.node-test.mjs`: 43 tests passed.
- Direct ESLint over all changed benchmark configuration, runner, analyzer, support, and runtime spec files: passed with zero warnings.
- Browser diagnostic and packaged Electron Playwright `--list` checks: 3 browser tests and 2 Electron tests collected.
- `npm.cmd run typecheck`: passed. Repository TypeScript configuration covers `src` and `electron`;
  E2E TypeScript transpilation/import validity is covered by the two Playwright collection checks.

CI diagnostics route through the browser runner without a Jackson fixture path. CI Electron is
explicitly rejected during runner/spec configuration before packaged executable launch. The full
benchmark matrix, Electron rebuild, and snow-demand workflow were intentionally not run in
Task 1. The next task is the controlled public MapLibre raster/snow/terrain reproducer and visible
snow-pixel evidence. Current ownership remains with the single Sol Light implementation owner; no
milestone commit or qualification SHA exists.

## Shutdown state

This is a durable handoff, not a qualification report. Continuation remains
owned by one **Sol Light** implementation owner. Do not delegate or introduce
parallel editors; preserve the repository's single-owner rules.

The starting and current `HEAD` is
`b915aef292da1bc31cd780b3555f3176a78ab5a7` (`simulation`, subject `fixes save
load times`).  The tree is intentionally dirty, with the integrated harness,
fixture lock, telemetry, runner, packaging, and benchmark work uncommitted.
It has **no measured qualification** and the required gates are **not all
green**.  Do not reset, clean, stash, or otherwise discard this working tree.

## What is implemented

The dirty implementation contains:

- Jackson / Black Mountain integrated-fixture acquisition and preparation,
  immutable fixture locking, scenario definitions, checkpoint preparation, and
  fixture validation.
- Browser and packaged-Electron benchmark runners, hardware/preflight identity
  collection, CDP packaged-process control, comparison analysis, and invalid
  attempt retention.
- Integrated telemetry, React profiling hooks, collector smoke output, and
  E2E diagnostic/packaged benchmark configurations.
- Schema-17 save/load and dual-clock integration changes, including the
  benchmark input and snow protocol plumbing needed by the runner.

This inventory describes implementation evidence only.  It must not be treated
as a performance, hardware, or packaged-release result.

## Fixture and provenance anchors

The mutable local fixture is at
`test-results/integrated-fixtures/jackson`; its Git-side lock is
`tests/e2e/performance/fixtures/jackson.fixture-lock.json`.  The local
manifest's SHA-256 is
`418d9cf6e9ef0afe5de1ce3e91b29336e86417badf592c7a5db371b2dc7afec0`.
It describes a 2000 x 2000 DEM, 2998 x 3000 cover data, local NAIP imagery,
prepared Daymet/NASA POWER weather, locally pinned Noto glyphs, eight lifts,
25 interconnected trails, three reachable amenities, 135 nodes, 219
routes/lanes, 211 segments, 438 vertices, and a 512 x 512 snow grid with about
11.742 m cells.

The checkpoint SHA-256 values are:

| Checkpoint | SHA-256 |
| --- | --- |
| empty | `d3085e134c36c3fc0870d2745f396b7e6a51d15b2f049598ab175d4178d9270c` |
| 1,000 | `a1d9dce0aa206bd2f877730154e99040c3abab53bd2903c8ea849f2cfdc9c211` |
| 3,000 | `2759fb5bb88d5f5a9351594581c8b6230fb364e74cd4b01578290cbb67bc7ebd` |

The recorded dirty package diagnostic is
`test-results/integrated-benchmark/package-diagnostic-2026-09-14.json`; it
identifies the lockfile SHA-256
`f74c9ec310d0270b5b71c4975fde2c06d5731ba6c6da7cf7a538d272fbe5407d`.
The subsequent packaged executable evidence is identified by SHA-256 prefix
`9c8a36dd…`; retain the full accompanying artifact record with it.  The older
diagnostic record instead identifies unpacked
`release\\win-unpacked\\Ski Area Design Challenge.exe` as
`13bc8f8b131d9bedad76db1e1626ca110f36a250440ab21dc6eb9f3b6a29311e` and
portable `release\\Ski Area Design Challenge 0.0.0.exe` as
`ddd1e237e9972d2d53c5cd08cbf9ae060f3a647a564168bd0becb6e456ae3ebc`.
Neither record is a release qualification.

## Actual verification evidence

### Historical deterministic record (2026-09-15; superseded)

These aggregate gates predate the current Task 1/Task 2 edits. Preserve their artifacts as historical
evidence, but use the top pause checkpoint for the current verification state.

- `npm.cmd run check` **passed** in 1:06.817.  The main unit run recorded 233
  passing files and 1,460 passing tests, with 3 files/tests skipped (236/1,463
  total); the analyzer node suite was **12/12**, the integrated-hardware node
  suite was **27/27**, and the weather-lab unit run was 7 files / 43 tests.
  Full output is retained at
  `test-results/integrated-benchmark/npm-check-final-clean.log`.
- `npm.cmd run check:e2e-harness` **passed** in 1.917 seconds.  Its deliberate
  inner negative control failed on `E2E_NEGATIVE_CONTROL_SENTINEL` at
  `tests/e2e/negative-control/expected-failure.spec.ts:4:49`, and the outer
  gate correctly observed that failure and released its server.  Full output is
  retained at
  `test-results/integrated-benchmark/check-e2e-harness-final.log`.
- `node scripts/runE2E.mjs --project=feature-workflows --grep "schema-17 save loading"`
  **passed 7**, skipped **2**, and failed **0** in 39.934 seconds.  The runner
  closed its server; full output is retained at
  `test-results/integrated-benchmark/schema17-save-loading-final.log`.
- The focused packaged-launch checks passed: `npm.cmd run
  test:integrated-benchmark-hardware` was **27/27**, and `npx.cmd vitest run
  electron/benchmarkArguments.test.ts --testTimeout 10000` was **2/2**.  These
  are deterministic launcher/argument checks, not packaged GPU qualification.
- The current unpacked executable remains SHA-256
  `9c8a36dd89a61c943f1e3f3773c8620f686275734d64592fab023b7cbdbee31c`.
  A same-hash CDP diagnostic retained at
  `test-results/integrated-benchmark/electron-packaged-gpu-disabled-diagnostic-2026-09-15/gpu-disabled-cdp.json`
  still saw GPU child exit `0xC0000135` (`-1073741515`) with `--disable-gpu`.
  The default attempt is retained under
  `test-results/integrated-benchmark/electron-packaged-fresh-a-retry`.  Package
  contents and the explicit cwd/argv environment audit did not identify an
  omitted application runtime dependency; the remaining evidenced blocker is
  Windows GPU-child DLL resolution or machine/driver runtime state.
- The exact snow-demand workflow is still incomplete.  Its latest diagnostic
  artifact is
  `test-results/integrated-benchmark/diagnostic-1789496376953.snow-demand-diagnostic.json`.
  At in-bounds zoom 17.0426 the compiled layer and public visibility were
  `visible`; its TileManager was present, `used`, `_updated`, and
  `sourceLoaded`, yet had zero tiles and empty manager/renderable tile ID lists.
  It emitted zero `snow-tile-requested` (and no other snow-tile) protocol
  events, while terrain DEM generation continued.  The exact assertion failure
  and trace are retained in `test-results/integrated-benchmark/snow-demand-manager.log`
  and `test-results/e2e/performance-integrated-ben-e3649-c-or-qualification-workflow-integrated-diagnostic/`.

### Historical verification record

- The earlier aggregate `npm run check` reached **1,459 passed / 3 skipped** unit
  tests and **12/12** analyzer tests, then failed at the stale runner deadline.
  It predates the current complete aggregate pass recorded above.
- The runner's focused checks subsequently passed **25/25**, then **26/26**.
  These are focused infrastructure checks, not the aggregate gate.
- The schema compatibility save-loading command
  `node scripts/runE2E.mjs --project=feature-workflows --grep "schema-17 save loading"`
  passed **7** and skipped **2** opt-in comparison cases.
- Browser empty and malformed-input/negative diagnostics passed, including real
  App/worker/MapLibre startup and zero physical empty occupancy.  The main
  snow-tile-demand workflow remains blocked, so these runs do not qualify
  detailed workload, timing, hardware, or memory.
- Collector smoke is non-qualifying but retained at
  `test-results/integrated-benchmark/collector-smoke/collector-smoke-2026-09-14T04-33-33-715Z.collector-smoke.json`.
  Its available artifact hashes are allocation profile
  `6e7bd8b674d7411bf8bde28652d7743c12f690f4bd45857d5f888b147187661e`, GC
  trace `6d16f4482ae9d1824ee2dcc474a0a01363a535f158d3659b99f4f5efb643e8d9`,
  heap snapshot `58dcd7a395865fd0baaea8407c9523672358fa294d8f95ef1fcdc3731865cc8a`,
  React profile `da746a3f276ee5cc4d65971953d633439de25b3ceda04ad545843ce0c3016f8c`,
  and diagnostic-manifest `5b2884c0a43ca0d35d5091d3d996b9836890d0973b4c9fac5aa2c8701d044416`.
- A fresh packaged GPU child exited with `0xC0000135`.  The package-payload
  audit found no omitted application payload; this is a launch dependency
  failure, not evidence of a completed GPU trial.

## Historical failures and retained artifacts (superseded)

This section records earlier investigation and is superseded by the top Task 2 pause checkpoint.
Preserve the artifacts; do not treat the older blocker descriptions as the current diagnosis.

The current primary blocker is the main snow-tile-demand browser workflow: a
visible, loaded, used raster manager has no tile candidates and never reaches
the snow protocol.  Keep its retained failure artifact and diagnose that
concrete demand condition before attempting a qualification run.  The packaged
lane remains blocked by the same-hash fresh GPU child's `0xC0000135` failure,
including the disabled-GPU diagnostic; retain the package payload audit and the
CDP diagnostic directories under `test-results/integrated-benchmark/electron-packaged-*`.

Earlier packaged attempts are also retained: the original large fixture
transport caused a 4 GiB Node heap failure; selected-checkpoint streaming was
then introduced.  Later attempts reached persistence but lost renderer context.
Those attempts do not establish packaged workload completion.  The historical
stale-runner-deadline aggregate failure must remain visible until a new complete
aggregate gate is recorded.

## Next bounded steps

1. Inspect the retained snow-tile-demand failure artifact and repair only the
   runner/readiness or demand defect it demonstrates.  Re-run its focused test.
2. Run the focused runner suite and the save-loading command above after that
   repair, retaining new output alongside the existing artifacts.
3. Run `npm run check`, then `npm run check:e2e-harness`, and the matching
   deterministic browser workflow.  Record the exact command, source identity,
   outcome, and artifacts; do not claim green from the older partial check.
4. Resolve the fresh-child `0xC0000135` machine/DLL dependency before retrying
   CDP diagnostics.  Do not rebuild while the package, argv, cwd, and executable
   identity evidence remains unchanged.
5. Only after a clean committed source/lock identity and all deterministic gates
   are green, run the five-pair browser and packaged qualification workflows
   from `docs/performance/integrated-benchmark.md`, retaining invalid attempts
   and hardware identity.  Then separately run soak/reopen and the other GPU
   lane.

## Shutdown cleanup

No benchmark, build, test, process launch, package action, commit, reset, or
working-tree cleanup was performed for this handoff.  Before resuming, use the
handoffs and retained artifacts above, check for active `node`, Playwright,
Electron, and dev-server processes associated with the benchmark, and stop only
processes that can be identified as stale benchmark children.  Preserve all
fixture, lock, package-diagnostic, collector-smoke, failure, and CDP artifacts
until their replacements have been verified.

## Qualification status

No hardware qualification matrix is claimed.  There is no benchmark milestone
commit or immutable SHA because the deterministic snow-demand workflow is not
green.  The current deterministic gate records and diagnostic artifacts above
are handoff evidence only.

## Phase 1 Task 2 controlled snow reproducer checkpoint (2026-09-15)

The installed public MapLibre reducer is in
`tests/e2e/performance/snow-reproducer/`, with its Playwright spec/config and
`scripts/runSnowReproducer.mjs`.  The runner owns Vite programmatically, closes
it in `finally`, imposes a 240-second child deadline, and supports focused
`--case=<name>` runs.  Every run writes a unique attempt directory and updates
only `latest-attempt.json`; JSON retention precedes the separately guarded
screenshot.  `node scripts/runSnowReproducer.mjs --case=snow-flat --list`
exited 0 and collected one test.  The corrected focused flat run exited 0 with
one passing test in 5.5 seconds.  `npx vitest run
tests/e2e/performance/support/integratedRuntime.test.ts` passed 6/6; ESLint on
the reproducer spec/harness/config/runner and `node --check
scripts/runSnowReproducer.mjs` also exited 0.

| Controlled evidence | Result | Retained attempt |
| --- | --- | --- |
| CI local known raster, terrain off, exact 1920x1080 / DPR 1 / center `[-121.495,46.902]` / zoom 17.0426 / pitch 35 | Visible red framebuffer | `attempt-1789501636097` |
| Actual CI resort-snow protocol, terrain off, same geometry | Passed in 5.5s; visible blue center `[41,95,154,255]`, 48 requests and 48 generations, no terrain | `attempt-1789501800462` |
| Actual snow plus CI DEM using the production dedicated Terrarium `terrain-dem`, production tile LOD, and `mountTerrain` | Currently fails terrain readiness: DEM protocol generates tiles, but public `queryTerrainElevation` remains `-32768`, so no snow visibility conclusion is valid yet | `attempt-1789501916459` |
| Production analysis composition with hidden-through-boot then public Snow reveal | Earlier `.905` camera run passed, but that camera did not match the App and is invalid for exact comparison; rerun waits on the DEM correction above | `attempt-1789501443820` (geometry-invalidated) |
| Normal App hydration plus Snow control | Visible/loaded/used manager produced zero tile IDs and zero snow protocol requests while DEM generation continued | `diagnostic-1789500513722.snow-demand-diagnostic.json` |

Two reducer errors invalidated earlier terrain comparisons and are now fixed:
the nominal flat case accidentally mounted terrain whenever a CI terrain record
existed, and the direct DEM omitted Terrarium encoding and the production
dedicated terrain source/LOD path.  The exact flat pass above is the corrected
case.  Earlier Jackson high-zoom behavior remains a separate observation.  The
first Jackson DEM failure was overwritten before unique attempt directories
were introduced; only its known `map idle deadline` transcript survives, not
its original observations.

Current ownership remains the single Phase 1 implementation owner.  The next
bounded step is to explain the reduced dedicated DEM's public `-32768`
elevation at the exact camera, correct only the demonstrated setup divergence,
then rerun case 3 and analysis composition before returning to the App.  No
Jackson matrix or Electron workflow has run in Task 2.
# Task 2 controlled snow evidence (2026-09-15)

| Attempt | Result | Evidence / limitation |
| --- | --- | --- |
| `task2-snow-demand-focused-b11` | focused pass, exit 0 | Valid synchronous WebGL framebuffer evidence: source revision 2→3, exact grid and encoded tile colors changed, patch changed while control stayed identical, matching generated tile preceded the correlated framebuffer render, and style reload restored loaded/visible Snow with an off/on canvas check. Diagnostic-only and qualification-ineligible. |
| `task2-snow-demand-focused-b12` | focused pass, exit 0 | Same focused proof after closing the console/dock with Escape; pre-style owner remained checked and public visibility remained visible. This disproves panel closure as the full-workflow style divergence. |
| `task2-full-browser-b3` | interaction failure | Localized framebuffer polling timed out. The main path did not yet retain the per-poll candidates, so this attempt cannot establish whether patch or control drifted. |
| `task2-full-browser-b4` | interaction failure | Localized step progressed, then style reload restored terrain and Snow sources/layer loaded but Snow visibility was `none`. |
| `task2-snow-demand-focused-b1` through `b10` | superseded characterization | Full-page and locator screenshots were invalid for canvas proof because DOM overlays can contaminate them. `b10` used framebuffer capture but preceded the final generated-tile marker contract. Retained only as development history. |

The product DEM repair edge-pads DEM pixels only for tiles whose true geographic tile bounds intersect the physical source, preserves real in-bounds elevations and transparent wholly outside tiles, and uses the same renderer for worker and main-thread DEM fallback. Non-DEM fallback behavior remains unchanged.
