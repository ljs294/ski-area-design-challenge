# Three.js migration progress

Plan revision: 6  
Branch / base / current SHA: `3js-edition` / `b915aef292da1bc31cd780b3555f3176a78ab5a7` / `1cca4e0808b96fa0fc43d48c4289a118b29b30ec`  
Dirty work to preserve: P0 files in `prototypes/three-terrain/` and these plan records; no pre-existing dirty work was present.

Completed/current task: P0-A/B/C implemented as a standalone prototype.  
Next task: P1-A — renderer-independent session ownership and failure-isolated committed notifications.  
Implementation/review: primary Codex agent, direct implementation; no sub-agent was used because repository instructions did not request delegation.

Changed decisions: select mixed display detail / active-neighborhood refinement because the 257² fixed display mesh measured 32.0 m maximum midpoint error on Doublehead. Proceed visually after fixing the core/surround color seam. Full evidence and budgets: [threejs-p0-evidence.md](threejs-p0-evidence.md).

Current entry points: `prototypes/three-terrain/src/{PrototypeViewport,terrainSurface,terrainPicking,terrainMesh}.ts(x)`; production session work starts at `src/app/committedDocumentTransaction.ts`, `terrainDocument.ts`, `topologyDocument.ts`, and their current `MapView.tsx` callers.

Checks:

- `cd prototypes/three-terrain && npm run typecheck` → pass, working tree.
- `cd prototypes/three-terrain && npm test` → pass, 3 tests, working tree.
- `cd prototypes/three-terrain && npm run build` → pass, working tree; expected 718 kB standalone prototype bundle warning.
- `npx eslint prototypes/three-terrain/src prototypes/three-terrain/vite.config.ts --max-warnings=0 --report-unused-disable-directives` → pass, working tree.
- Real-package headless run, Doublehead, 1920×1080 DPR 1 → 16 chunks, 131,072 triangles, 3.0 MiB buffers, pick p95 5.4–14.4 ms; headless frame timing unavailable.
- Root `npm run check` → baseline failure after 1,465 passing tests: Vitest reports no suite in `scripts/integratedBenchmarkConfiguration.test.mjs` and `scripts/runIntegratedBenchmark.test.mjs`; the gate stops before build steps.
- Root `npm run check:e2e-harness` → pass; the intentional negative control exited 1 and its server was released.

Unavailable checks: qualified physical-GPU FPS/timing, Electron prototype run, and full current/proposed matched camera capture. Existing current-game save preview and proposed near/typical/overhead captures were inspected; physical-GPU acceptance remains a P2/P4 requirement.

P1-A done condition: one renderer-independent committed design owner exposes narrow reads/commands/snapshots, retains coherent terrain/topology revisions, completes required dirty/revision notifications despite a throwing presentation consumer, and has characterization/failure tests without initializing simulation.
