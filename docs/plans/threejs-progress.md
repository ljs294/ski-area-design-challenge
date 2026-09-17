# Three.js migration progress

Plan revision: 6  
Branch / base / current SHA: `3js-edition` / `b915aef292da1bc31cd780b3555f3176a78ab5a7` / `681b78877a509d1e7535def8516d88ce7c2e711f`
Dirty work to preserve: the current P1-C evidence implementation and these plan records.

Completed/current task: P1-C — failure-injection and real-adapter recovery evidence.
Next task: P2-A — dedicated Three.js host and interface integration.
Implementation/review: primary Codex agent, direct implementation; no sub-agent was used because repository instructions did not request delegation.

Changed decisions: select mixed display detail / active-neighborhood refinement because the 257² fixed display mesh measured 32.0 m maximum midpoint error on Doublehead. Proceed visually after fixing the core/surround color seam. Full evidence and budgets: [threejs-p0-evidence.md](threejs-p0-evidence.md).

Current entry points: the standalone P0 prototype remains under `prototypes/three-terrain/`. P1-A landed in the renderer-independent design session and current `MapView.tsx` composition. P1-B adds the isolated `mountain-planner-three-design` format through `designSaveClient.ts`, with browser IndexedDB and Electron `three-designs` adapters. P1-C adds a non-product real-browser/Electron evidence harness under `tests/p1c/`. The dedicated Three.js gameplay host will consume this boundary in P2.

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

Unavailable checks: qualified physical-GPU FPS/timing, Electron prototype run, and full current/proposed matched camera capture. Existing current-game save preview and proposed near/typical/overhead captures were inspected; physical-GPU acceptance remains a P2/P4 requirement.

P1-A result: `DesignSession` owns stable design/terrain identity, committed terrain/topology/lifts, revisions, derived network, capabilities, narrow read/command ports and persistence snapshots. Terrain buffers are copied on first ownership and retained by identity when unchanged. Accepted terrain/topology/lift commits remain authoritative when presentation consumers throw, with isolated failure status. Current design boot/edit/save no longer waits for weather readiness; operational simulation controls remain gated. Headless session tests do not initialize simulation.

P1-B result: fork saves use their own format, schema and storage namespace and do not alter `GameSave`. The repository captures and serializes each save-key snapshot, writes and verifies immutable terrain generations, then publishes a complete design manifest and authoritative head. Browser publication uses one IndexedDB transaction; desktop publication retains a recoverable previous head and atomically replaces files. Secondary index failure returns a warning without reversing the committed design, and listings reconstruct current summaries from authoritative heads. Save receipts identify the exact committed revisions.

P1-C result: injected failure after a new terrain generation but before head publication reloads the prior complete browser pair; a failed IndexedDB manifest add cannot move the head; stale/failed summaries reconstruct from the committed manifest. A real Electron launch saves through preload/IPC, restarts with a corrupted current head, and loads the previous complete design/terrain pair from the isolated namespace. Head replacement interruption restores the prior desktop head. Pending saves acknowledge only their captured revisions, detached worker staging leaves session-owned arrays intact, preview writes do not mutate authoritative saves, and accepted commits remain saveable when presentation consumers fail. No simulation engine is initialized by these fixtures.
