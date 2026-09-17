# Three.js migration progress

Plan revision: 6  
Branch / base / current SHA: `3js-edition` / `b915aef292da1bc31cd780b3555f3176a78ab5a7` / standalone-app working tree on `862667619c4e`
Dirty work to preserve: the current P2-A host/interface implementation and these plan records.

Completed/current task: P2-A — dedicated Three.js host and interface integration.
Next task: P2-B — cumulative terrain deformation and coherent GPU activation.
Implementation/review: primary Codex agent, direct implementation; no sub-agent was used because repository instructions did not request delegation.

Changed decisions: select mixed display detail / active-neighborhood refinement because the 257² fixed display mesh measured 32.0 m maximum midpoint error on Doublehead. Proceed visually after fixing the core/surround color seam. Full evidence and budgets: [threejs-p0-evidence.md](threejs-p0-evidence.md).

Current entry points: the standalone P0 prototype remains under `prototypes/three-terrain/`. The original playable game remains at `index.html`. The P2 product fork is a separate Electron application under `threejs-game/`, started by `npm run dev:three` or `threejs-game/run-threejs.bat`; it uses the shared MapLibre component only for selection/preparation and mounts the dedicated Three.js gameplay host after the fork save commits. Direct fork loads bypass MapLibre. The standalone edition has its own application-data directory, package configuration, build output and eventual portable executable.

Checks:

- `cd prototypes/three-terrain && npm run typecheck` → pass, working tree.
- `cd prototypes/three-terrain && npm test` → pass, 3 tests, working tree.
- `cd prototypes/three-terrain && npm run build` → pass, working tree; expected 718 kB standalone prototype bundle warning.
- `npx eslint prototypes/three-terrain/src prototypes/three-terrain/vite.config.ts --max-warnings=0 --report-unused-disable-directives` → pass, working tree.
- Real-package headless run, Doublehead, 1920×1080 DPR 1 → 16 chunks, 131,072 triangles, 3.0 MiB buffers, pick p95 5.4–14.4 ms; headless frame timing unavailable.
- Root `npm run check` → baseline failure after 1,465 passing tests: Vitest reports no suite in `scripts/integratedBenchmarkConfiguration.test.mjs` and `scripts/runIntegratedBenchmark.test.mjs`; the gate stops before build steps.
- Root `npm run check:e2e-harness` → pass; the intentional negative control exited 1 and its server was released.
- P1-A `npm run typecheck` → pass.
- P1-A focused document/session/boot/dirty-state suite → pass, 65 tests.
- P1-A `npm run lint` → pass.
- P1-A `npm run build` → pass; existing large-chunk warnings remain.
- P1-A `npm test` → 235 files and 1,472 tests pass; the same two baseline empty-suite failures remain in `scripts/integratedBenchmarkConfiguration.test.mjs` and `scripts/runIntegratedBenchmark.test.mjs`.
- P1-A targeted `construction-save.spec.ts` → not green in this run: repeated browser runs were unusually slow (48–114 seconds); one cleared boot and then exhausted the 60-second test timeout waiting for the Infrastructure control, while retries exceeded the workflow's 15-second boot expectation. Playwright traces were retained under `test-results/e2e/`.
- P1-B architecture check, focused ESLint and root typecheck → pass.
- P1-B repository, desktop filesystem and session-snapshot suites → pass, 8 tests.
- P1-B desktop and static-web production builds → pass; existing large-chunk warnings remain.
- P1-B `npm test` → the same two baseline empty-suite failures remain in `scripts/integratedBenchmarkConfiguration.test.mjs` and `scripts/runIntegratedBenchmark.test.mjs`.
- P1-C focused save/session/terrain/preview/presentation suite → pass, 60 tests across 7 files.
- P1-C real Chromium IndexedDB interruption/atomicity/isolation workflow → pass.
- P1-C real Electron preload/IPC/filesystem recovery workflow → pass, including restart from a corrupted current head.
- P2-A `npm run typecheck` and `npm run lint` → pass.
- P2-A analytical production terrain/picking suite → pass, 3 tests.
- P2-A `npm run build:three`, `npm run build:desktop`, and `npm run build:web` → pass; existing large-chunk warnings remain.
- P2-A real Chromium direct-load smoke → pass: one Three.js canvas, no MapLibre gameplay canvas, design-only toolbar capability, contour overlay toggle, and no page errors.
- P2-A full unit attempt → 1,484 tests passed; the same two baseline empty-suite failures remained. Two weather tests timed out at 10 seconds while lint/typecheck ran concurrently, then passed 37/37 alone with a 30-second ceiling.
- Standalone-folder follow-up `npm run typecheck`, lint, agent-doc and architecture checks → pass.
- `npm --prefix threejs-game test` → pass, 3 analytical terrain/picking tests.
- Original `npm run build:desktop` and standalone `npm run build:three` → pass; existing large-chunk warnings remain.
- Dedicated Electron development application smoke → pass: standalone title, desktop preload bridge, menu and New Resort available with no page errors.
- Electron-builder unpacked Windows package → pass using the pinned local Electron distribution; the independently named packaged executable remained running in a launch smoke.

Unavailable checks: qualified physical-GPU FPS/timing and full current/proposed matched camera capture. Existing current-game save preview and proposed near/typical/overhead captures were inspected; physical-GPU acceptance remains a P2/P4 requirement.

P1-A result: `DesignSession` owns stable design/terrain identity, committed terrain/topology/lifts, revisions, derived network, capabilities, narrow read/command ports and persistence snapshots. Terrain buffers are copied on first ownership and retained by identity when unchanged. Accepted terrain/topology/lift commits remain authoritative when presentation consumers throw, with isolated failure status. Current design boot/edit/save no longer waits for weather readiness; operational simulation controls remain gated. Headless session tests do not initialize simulation.

P1-B result: fork saves use their own format, schema and storage namespace and do not alter `GameSave`. The repository captures and serializes each save-key snapshot, writes and verifies immutable terrain generations, then publishes a complete design manifest and authoritative head. Browser publication uses one IndexedDB transaction; desktop publication retains a recoverable previous head and atomically replaces files. Secondary index failure returns a warning without reversing the committed design, and listings reconstruct current summaries from authoritative heads. Save receipts identify the exact committed revisions.

P1-C result: injected failure after a new terrain generation but before head publication reloads the prior complete browser pair; a failed IndexedDB manifest add cannot move the head; stale/failed summaries reconstruct from the committed manifest. A real Electron launch saves through preload/IPC, restarts with a corrupted current head, and loads the previous complete design/terrain pair from the isolated namespace. Head replacement interruption restores the prior desktop head. Pending saves acknowledge only their captured revisions, detached worker staging leaves session-owned arrays intact, preview writes do not mutate authoritative saves, and accepted commits remain saveable when presentation consumers fail. No simulation engine is initialized by these fixtures.

P2-A result: the product fork now lives under top-level `threejs-game/` as a separately launchable Electron application with its own main process, renderer entrypoint, launcher, package/build output, application-data directory and portable Windows packaging target. The original MapLibre game remains independently launchable. Selection/preparation writes a coherent design fork and hands its key to the Three.js screen machine without entering MapLibre gameplay. The host directly restores fork saves, committed session data, camera pose, terrain/surround elevations, cover colors, matched imagery, contours, site boundary, lifts, trails, paths and node/junction handles. Analytical heightfield picking remains independent of display triangles; feature hits use explicit handle/path/lift/trail priority. Existing menu, settings, credits, draggable windows, interface scaling and the fixed toolbar divisions are retained. Simulation, weather and construction are clearly unavailable rather than initialized or imitated. Camera saves and unsaved-exit handling work in the design-only host, and renderer/session resources dispose on exit.
