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
| Current benchmark | C2 — infrastructure, topology, trail, and save models plus the final type-only facade |
| Status | C1 is committed: seven acyclic foundational model files are authoritative and the compatibility facade fell from 570 to 217 lines |
| Last green commit | C1 at `eef74944fda4da24cb81b8dbfe197639ee0b863d` |
| Next step | Land lift, road, snowmaking, topology, trail, and save models; finish the facade and compatibility checks |
| Blocking issue | None for the backup prerequisite; the verified recovery reference is recorded in [`docs/history/legacy-poster-app.md`](../history/legacy-poster-app.md) |

## Commit benchmarks

Each row is a hard stop: run its gates, update this ledger, and commit before beginning the next row. If context or token budget runs low, stop at the latest green benchmark and report the next row.

| ID | Status | Commit boundary | Required evidence before commit |
| --- | --- | --- | --- |
| A1 | Complete | Shared guidance, current architecture, ledger, editor defaults, docs checker | Direct docs checker passes; guidance size and pairing pass; local links resolve |
| A2 | Complete | Strict TypeScript, flat ESLint, offline/live test split, aggregate `npm run check` | Strict typecheck, lint, offline unit tests, desktop build, and web build pass |
| A3 | Complete | Playwright Test harness and required PR CI | Negative control proves nonzero failure; deterministic smoke passes; CI workflow validates locally as far as possible |
| B1 | Complete | Canonical geographic bounds and injectable resort-preparation service | Characterization tests cover bounds, incomplete cover, cancellation, and service failure |
| B2 | Complete | `prepareResortPackage` returns `TerrainRecord`; live-path runtime hydration is removed | Offline gate plus deterministic New Game/package workflow passes |
| B3 | Complete | Obsolete poster/preset vertical, remaining dead hydration, and Leaflet removed | Verified backup remains reachable; clean install/check; production bundle contains no preset payload |
| C1 | Complete | Dependency-neutral type models through terrain | Typecheck and offline suite pass; no type-model cycles introduced |
| C2 | In progress | Infrastructure/topology/trail/save models and type-only facade | Facade manifest, cycle, boundary, old/current fixture, and schema-11 tests pass |
| D1 | Not started | Tool coordinator and interaction lease | Unit tests cover synchronous cancellation, ownership, release, and exact restoration |
| D2 | Not started | Terrain document and topology ports | Tests cover revisions, stale commits, construction lock, atomic commands, and coherent snapshots |
| D3 | Not started | Map contribution registry with legacy contributions | Style reload, literal z-order/hit priority, visibility, hover, capture, and cleanup tests pass |
| D4 | Not started | Terrain-grade, cover-edit, dam-analysis, and trail-paint adapters | Request identity, abort, termination, stale response, validation, and disposal tests pass |
| E1 | Not started | Lift controller extraction | Feature workflow plus all cross-cutting coordinator/map/save gates pass |
| E2 | Not started | Road controller extraction | Feature workflow plus construction, cover-failure, capture, and save gates pass |
| E3 | Not started | Snowmaking façade with dam, pond, and node controllers | Feature workflows plus ordering, locking, capture, and save gates pass |
| E4 | Not started | Ski node/path controller extraction | Node/path/topology workflows plus ordering, cancellation, and save gates pass |
| E5 | Not started | Trail controller extraction | Paint, anchor/review, grading, failure retention, topology, cancellation, and save gates pass |
| F1 | Not started | Final MapView structural budgets and architecture documentation | All deterministic browser projects, Electron smoke, both builds, fixtures, and one opt-in live New Game run pass |

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
| B2 | `7e3ba52d4e1c58a20a848f9f7f14562465d45ac0` | Passed | The supported preparation/save path returns its verified persisted `TerrainRecord` without allocating legacy display grids or projected caches; one contract test, full check, and browser smoke passed. |
| B3 | `418e86049a32af01b4da0ed068d109bb64dcd5d1` | Passed | Backup ref re-verified; isolated `npm ci`, 527 offline tests, both builds, negative control, browser smoke, and live-provider picker passed. Obsolete guard found zero paths/dependencies/payload; production outputs fell to about 7.99 MB each. |
| C1 | `eef74944fda4da24cb81b8dbfe197639ee0b863d` | Passed | Seven dependency-safe foundational model files landed. Full check (527 offline tests and both builds), architecture/cycle checks, and all deterministic browser workflows passed; `src/types.ts` fell from 570 to 217 lines. |

## Open decisions and blockers

- The B3 backup prerequisite is satisfied by `refs/heads/legacy/v0.1` at `3f5eb2378342053906719c75650d785ea2249241`; creating or pushing a remote tag still requires separate user authorization.
- Repository settings must make the pull-request workflow required; a workflow file alone cannot enforce the branch rule.
- Live USGS, WorldCover, Overpass, GPU, and Electron release checks remain opt-in where the environment cannot supply their external prerequisites.
- `src/app/app.css` and `src/network.ts` remain follow-up candidates; they are not opportunistic refactor scope.
