# Agent-friendly architecture refactor ledger

This is the shared execution record for the approved refactor. Update it at every benchmark. Record only verified results and immutable commit SHAs; do not describe an uncommitted working tree as green.

## Guardrails

- Preserve intended behavior in the supported React/MapLibre application and preserve the `GameSave` schema.
- Treat removal of obsolete entrypoints, tooling, dependencies, and preset assets as deliberate scope, not incidental cleanup.
- Make stale-write rejection, atomic topology/terrain commits, cancellation, map ordering, and capture restoration explicit correctness invariants.
- Keep each benchmark independently reviewable and revertible. Never commit a failed gate.
- Use `git revert` in reverse dependency order for landed rollback; do not rewrite shared history.
- One integration owner controls `src/app/MapView.tsx`, `src/types.ts`, and `src/app/app.css`.

## Current state

| Field | Value |
| --- | --- |
| Approved scope | Shared checks; obsolete vertical removal after backup; acyclic types; MapView foundations; controller extraction; final structural gate |
| Current benchmark | B2 — return persisted terrain records and remove runtime hydration |
| Status | B1 is committed with clean metrics, 526 offline tests, and deterministic browser-smoke evidence |
| Last green commit | B1 at `f62dea0dd20aa6f8380d0920e172899e5de02c56` |
| Next step | Characterize old terrain-package compatibility, then remove only hydration work proven unreachable from the live app |
| Blocking issue | None for the backup prerequisite; the verified recovery reference is recorded in [`docs/history/legacy-poster-app.md`](../history/legacy-poster-app.md) |

## Commit benchmarks

Each row is a hard stop: run its gates, update this ledger, and commit before beginning the next row. If context or token budget runs low, stop at the latest green benchmark and report the next row.

| ID | Commit boundary | Required evidence before commit |
| --- | --- | --- |
| A1 | Shared guidance, current architecture, ledger, editor defaults, docs checker | Direct docs checker passes; guidance size and pairing pass; local links resolve |
| A2 | Strict TypeScript, flat ESLint, offline/live test split, aggregate `npm run check` | Strict typecheck, lint, offline unit tests, desktop build, and web build pass |
| A3 | Playwright Test harness and required PR CI | Negative control proves nonzero failure; deterministic smoke passes; CI workflow validates locally as far as possible |
| B1 | Canonical geographic bounds and injectable resort-preparation service | Characterization tests cover bounds, incomplete cover, cancellation, and service failure |
| B2 | `prepareResortPackage` returns `TerrainRecord`; runtime hydration is removed | Offline gate plus deterministic New Game/package workflow passes |
| B3 | Obsolete poster/preset vertical and Leaflet removed | Verified backup remains reachable; clean install/check; production bundle contains no preset payload |
| C1 | Dependency-neutral type models through terrain | Typecheck and offline suite pass; no type-model cycles introduced |
| C2 | Infrastructure/topology/trail/save models and type-only facade | Facade manifest, cycle, boundary, old/current fixture, and schema-11 tests pass |
| D1 | Tool coordinator and interaction lease | Unit tests cover synchronous cancellation, ownership, release, and exact restoration |
| D2 | Terrain document and topology ports | Tests cover revisions, stale commits, construction lock, atomic commands, and coherent snapshots |
| D3 | Map contribution registry with legacy contributions | Style reload, literal z-order/hit priority, visibility, hover, capture, and cleanup tests pass |
| D4 | Terrain-grade, cover-edit, dam-analysis, and trail-paint adapters | Request identity, abort, termination, stale response, validation, and disposal tests pass |
| E1 | Lift controller extraction | Feature workflow plus all cross-cutting coordinator/map/save gates pass |
| E2 | Road controller extraction | Feature workflow plus construction, cover-failure, capture, and save gates pass |
| E3 | Snowmaking façade with dam, pond, and node controllers | Feature workflows plus ordering, locking, capture, and save gates pass |
| E4 | Ski node/path controller extraction | Node/path/topology workflows plus ordering, cancellation, and save gates pass |
| E5 | Trail controller extraction | Paint, anchor/review, grading, failure retention, topology, cancellation, and save gates pass |
| F1 | Final MapView structural budgets and architecture documentation | All deterministic browser projects, Electron smoke, both builds, fixtures, and one opt-in live New Game run pass |

## Standard benchmark protocol

1. Start from the last recorded green commit and inspect the working tree for user changes.
2. Add or update characterization tests before moving production behavior.
3. Implement only the named benchmark; defer unrelated cleanup and optimization.
4. Run `npm run check` once available and the benchmark-specific deterministic Playwright project.
5. Confirm verification created no tracked artifacts.
6. Update this ledger with results and the commit candidate. Commit only after every required gate is green.
7. Replace the candidate with the immutable commit SHA after commit, then identify the next benchmark.

## Benchmark results

| ID | Commit SHA | Gate result | Notes |
| --- | --- | --- | --- |
| A1 | `574e68745e17083002588aca7c5f4884a887c310` | Passed | Shared guidance, architecture documentation, ledger, editor defaults, and documentation checker committed on 2026-08-06. |
| A2 | `7215da704c203b78ced0516502349cd9bf515804` | Passed | `npm run check`: documentation and architecture checks, zero-warning lint, strict TypeScript, 522 offline tests, and desktop/web builds passed on 2026-08-06. Clean metrics baseline recorded separately. |
| A3 | `a52b54ab99988f132c503759be1608c843738b02` | Passed | Negative control returned Playwright exit 1 and matched both sentinels; two browser-smoke tests and one feature-workflow test passed with deterministic local fixtures on 2026-08-06. |
| B1 | `f62dea0dd20aa6f8380d0920e172899e5de02c56` | Passed | Canonical bounds and the required preparation-service port landed; four characterization tests cover true-bounds forwarding, incomplete cover, cancellation, and service failure. Full check and browser smoke passed. |

## Open decisions and blockers

- The B3 backup prerequisite is satisfied by `refs/heads/legacy/v0.1` at `3f5eb2378342053906719c75650d785ea2249241`; creating or pushing a remote tag still requires separate user authorization.
- Repository settings must make the pull-request workflow required; a workflow file alone cannot enforce the branch rule.
- Live USGS, WorldCover, Overpass, GPU, and Electron release checks remain opt-in where the environment cannot supply their external prerequisites.
- `src/app/app.css` and `src/network.ts` remain follow-up candidates; they are not opportunistic refactor scope.
