# Three.js migration progress

Plan revision: 6  
Branch / base / current SHA: `3js-edition` / `b915aef292da1bc31cd780b3555f3176a78ab5a7` / `1cca4e0808b96fa0fc43d48c4289a118b29b30ec`  
Dirty work to preserve: P0 files in `prototypes/three-terrain/` and these plan records; no pre-existing dirty work was present.

Completed/current task: P1-B — recoverable fork storage and atomic design/terrain publication.
Next task: P1-C — failure-injection and real-adapter recovery evidence.
Implementation/review: primary Codex agent, direct implementation; no sub-agent was used because repository instructions did not request delegation.

Changed decisions: select mixed display detail / active-neighborhood refinement because the 257² fixed display mesh measured 32.0 m maximum midpoint error on Doublehead. Proceed visually after fixing the core/surround color seam. Full evidence and budgets: [threejs-p0-evidence.md](threejs-p0-evidence.md).

Current entry points: the standalone P0 prototype remains under `prototypes/three-terrain/`. P1-A landed in the renderer-independent design session and current `MapView.tsx` composition. P1-B adds the isolated `mountain-planner-three-design` format through `designSaveClient.ts`, with browser IndexedDB and Electron `three-designs` adapters. The dedicated Three.js gameplay host will consume this boundary in P2.

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

Unavailable checks: qualified physical-GPU FPS/timing, Electron prototype run, and full current/proposed matched camera capture. Existing current-game save preview and proposed near/typical/overhead captures were inspected; physical-GPU acceptance remains a P2/P4 requirement.

P1-A result: `DesignSession` owns stable design/terrain identity, committed terrain/topology/lifts, revisions, derived network, capabilities, narrow read/command ports and persistence snapshots. Terrain buffers are copied on first ownership and retained by identity when unchanged. Accepted terrain/topology/lift commits remain authoritative when presentation consumers throw, with isolated failure status. Current design boot/edit/save no longer waits for weather readiness; operational simulation controls remain gated. Headless session tests do not initialize simulation.

P1-B result: fork saves use their own format, schema and storage namespace and do not alter `GameSave`. The repository captures and serializes each save-key snapshot, writes and verifies immutable terrain generations, then publishes a complete design manifest and authoritative head. Browser publication uses one IndexedDB transaction; desktop publication retains a recoverable previous head and atomically replaces files. Secondary index failure returns a warning without reversing the committed design, and listings reconstruct current summaries from authoritative heads. Save receipts identify the exact committed revisions. P1-C retains responsibility for the exhaustive real-browser/preload failure matrix required by section 5.1.
