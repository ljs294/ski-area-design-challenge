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
| Current benchmark | F1 — final structural and acceptance gate |
| Status | E5 is committed: the trail reducer, map input, painting, anchor/review, grading, atomic confirmation, editing, and contribution are controller-owned |
| Last green commit | E5 at `d39bdfd1a0562625cb6ab08793f8a98ff89950f9` |
| Next step | Extract cohesive cross-cutting MapView services and presentation composition until the approved line/import/effect budgets pass, without moving boot/save/load authority or changing behavior |
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
| C2 | Complete | Infrastructure/topology/trail/save models and type-only facade | Facade manifest, cycle, boundary, old/current fixture, and schema-11 tests pass |
| D1 | Complete | Tool coordinator and interaction lease | Unit tests cover synchronous cancellation, ownership, release, and exact restoration |
| D2 | Complete; remediated by R1 and reaccepted by R4 | Terrain document and topology ports | Tests cover revisions, stale commits, construction lock, atomic commands, and coherent snapshots |
| D3 | Complete; reaccepted by R2 and R4 | Map contribution registry with legacy contributions | Style reload, literal z-order/hit priority, visibility, hover, capture, and cleanup tests pass |
| D4 | Complete; remediated by R3 and reaccepted by R4 | Terrain-grade, cover-edit, dam-analysis, and trail-paint adapters | Request identity, abort, termination, stale response, validation, and disposal tests pass |
| R0 | Complete | Preserve D4 and independently rerun its deterministic gates | Backup branch points to the audited D4 state; full check and all browser workflows pass |
| R1 | Complete | Document immutability, atomic terrain/topology confirmation, and coherent persistence snapshots | Ownership/no-op/atomicity tests, full check, and all browser workflows pass |
| R2 | Complete | Full map-contribution lifecycle and road-family ownership | Unit/browser tests cover data, visibility, hover, style generations, order, capture, and cleanup |
| R3 | Complete | Worker post/response/cancellation hardening | Each adapter proves post failure, identity mismatch, supersession termination, and disposal |
| R4 | Complete | Cross-cutting browser gates and D2–D4 reacceptance | Construction locking, worker supersession, style/hit/capture, and save coherence pass in browser |
| E1 | Complete | Lift controller extraction | Feature workflow plus all cross-cutting coordinator/map/save gates pass |
| E2 | Complete | Road controller extraction | Feature workflow plus construction, cover-failure, capture, and save gates pass |
| E3 | Complete | Snowmaking façade with dam, pond, and node controllers | Feature workflows plus ordering, locking, capture, and save gates pass |
| E4 | Complete | Ski node/path controller extraction | Node/path/topology workflows plus ordering, cancellation, and save gates pass |
| E5 | Complete | Trail controller extraction | Paint, anchor/review, grading, failure retention, topology, cancellation, and save gates pass |
| F1 | Next | Final MapView structural budgets and architecture documentation | All deterministic browser projects, Electron smoke, both builds, fixtures, and one opt-in live New Game run pass |

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
| C2 | `dbf447e507c8d021a36b12ddf078f8f5a1eea221` | Passed | The complete acyclic model graph and 35-line exact facade landed. Architecture ownership/manifest gates, compile-time save compatibility, schema-v1/v11 hydration fixtures, 531 offline tests, both builds, and all deterministic browser workflows passed. |
| D1 | `bc9fb3790ce0958d090dc33f8b825a515df26860` | Passed | The seven-tool coordinator and exclusive map-interaction lease landed with centralized selection. Fourteen contract tests, 545 total offline tests, both builds, and deterministic browser switching/Layers workflows passed. |
| D2 | `1dfcf24d2031fcb5a286dc81386bc4ff5bb435dd` | Passed | Revisioned terrain and topology documents landed and were integrated across three green sub-checkpoints: contracts at `5ac586b02173afd5d941e00bde47534d83435dc9`, terrain integration at `6ccca2a00226b943e9ae9882dc22057082283f23`, topology integration at `1dfcf24d2031fcb5a286dc81386bc4ff5bb435dd`. Thirty-nine contract tests, 584 total offline tests, both builds, and all three deterministic browser workflows passed at each of the two integration checkpoints. |
| D3 | `88ff5bffd0b194006e0816bbc5d32222810cb35c` | Passed | The map contribution registry landed across three green sub-checkpoints: the declared orders and derived guards at `2c9a7389c19e71b2dc59b27aa9dfcdf15176a287`, the `MapView` traversal for style reload, hits, and capture at `743236d31fe662145c0504cb53fa181469d31e01`, and the browser evidence at `88ff5bffd0b194006e0816bbc5d32222810cb35c`. Thirteen contract tests, 597 total offline tests, both builds, and all five deterministic browser workflows passed. The restyle workflow asserts the nine family blocks, every guarded hit layer, hidden-layer persistence across a style reload, and map teardown; the hit workflow asserts a lift crossing a run picks the lift and the run alone still picks the run, and it fails when the guard derivation is neutered. |
| D4 | `35fd9469bc12a1c87330e26d30591e530310bda5` | Passed | The four worker adapters landed across three green sub-checkpoints: the shared session with the dam-analysis and cover-edit adapters at `a753d464e11af0268575a4ca81831cc314624233`, the shared grade preview at `bcdc00a08a246c1ac5e4de7471f957641228f7e8`, and the painting engine at `35fd9469bc12a1c87330e26d30591e530310bda5`. Twenty-nine contract tests, 624 total offline tests, both builds, and all six deterministic browser workflows passed. `MapView` now contains zero `new Worker` expressions. The new painting workflow paints a run from a lift terminal and fails when the ready-then-replay handshake is neutered. |
| R0 | `c6e6c2a6c2f837de3f9ee8c4fb1aa4ad6136b18c` | Passed | The D4 state was preserved at local branch `backup/refactor-d4-audit`. An independent `npm run check` passed 624 offline tests and both builds, and all six deterministic browser workflows passed before remediation began. |
| R1 | `e61821e3d84b7911048bf2fc89d489be36db9454` | Passed | Ownership/no-op revisions landed at `75449a9`, two-phase terrain/topology confirmation and five coordinator tests at `4dc06c5`, and synchronous persistence projection at `e61821e`. `npm run check` passed 631 offline tests and both builds; all six deterministic browser workflows passed. |
| R2 | `a7439ce1693bb758ac702675090e88b2447afa39` | Passed | The complete registry contract landed at `494adc9`, committed-road ownership at `af74dd1`, live registry traversal at `5a23c3b`, legacy-seam removal at `3e8a414`, and browser capture proof at `a7439ce`. `npm run check` passed 632 offline tests and both builds; all seven deterministic browser workflows passed. |
| R3 | `ede5ac6487523e88c14065d7092725d23ea8cbb8` | Passed | Worker transport retirement landed at `0be94b8`, explicit post-failure settlement at `94919c9`, response identity/envelope validation at `54c97b1`, and trail-grade toggle cancellation at `ede5ac6`. `npm run check` passed 642 offline tests and both builds; all seven deterministic browser workflows passed. |
| R4 | `a1e6cc3d1ba70a7774cbc4a1cadbd3f42d71d42b` | Passed | A real Worker-constructor probe proves trail-paint supersession and cancellation terminate each engine exactly once. The construction/save workflow dispatches two lift confirmations in one task, observes one lift, then patches topology and invokes Save in one task; the schema-v11 save contains the synchronous topology projection and matches the persisted terrain record. `npm run check` passed 642 offline tests and both builds; all eight deterministic browser workflows passed. |
| E1 | `5a6bfcc38adbb96dc3a7547acda3bd7074b06c79` | Passed | The pure lift model landed at `6ee033e`; the controller, contribution, and lift-specific capture proof landed at `5a6bfcc`. `MapView` fell by 165 lines, one state call, and two effects. `npm run check` passed 645 offline tests and both builds; all eight deterministic browser workflows passed, including same-tick double confirmation, save coherence, lift transient capture, style reload, and hit priority. |
| E2 | `62a5c68d0dc1c5e9ddad7d6619b3dab6116a5ecd` | Passed | The pure road model landed at `861d0c0`, the controller and feature proof at `4c49ff2`, and authoritative domain imports at `62a5c68`. `MapView` fell by 217 lines, one state call, two effects, and one import. `npm run check` passed 648 offline tests and both builds; all nine deterministic browser workflows passed. The road workflow proves same-task double confirmation builds once, schema-v11 save/terrain coherence, transient capture, and best-effort cover-worker failure. |
| E3 | `bdcaf4978b4efc234600699b12e29446725f6db0` | Passed | Dam and pond models landed at `7f19118`; the separate dam, pond, and snowmaking-node controllers plus their façade landed at `bdcaf49`. `MapView` fell by 379 lines, two state calls, six effects, and four imports. `npm run check` passed 651 offline tests and both builds; all ten deterministic browser workflows passed. The snowmaking workflow proves contribution data, pond-draft capture restoration, node rename/reconciliation, source deletion, and schema-v11 persistence. Dam analysis now also rejects a terrain revision that changes before review or confirmation. |
| E4 | `66f78c01deb0900fbde95377bb516c605ab0f1e6` | Passed | The pure node/path model landed at `c1031ea`; the controller and atomic connector workflow landed at `66f78c0`. `MapView` fell by 280 lines, three state calls, and four effects. `npm run check` passed 654 offline tests and both builds; all eleven deterministic browser workflows passed. The node/path workflow proves leased map input, draft capture restoration, atomic path-plus-junction publication, and schema-v11 persistence. |
| E5 | `d39bdfd1a0562625cb6ab08793f8a98ff89950f9` | Passed | The pure trail model landed at `98b4eaa`; the split trail controller/map-input workflow and expanded browser evidence landed at `d39bdfd`. `MapView` fell by 823 lines, five imports, two state calls, and four effects. `npm run check` passed 657 offline tests and both builds; all thirteen deterministic browser workflows passed. The workflows prove painter restart/cancellation, review and capture restoration, grading-failure retention, successful graded terrain/topology confirmation, same-task double-confirm rejection, schema-v11 persistence, and best-effort cover failure. |

## D4 worker ownership

[`src/app/workerAdapter.ts`](../../src/app/workerAdapter.ts) runs one worker at
a time and binds handlers to the instance that was running when they were
bound. A retired worker cannot deliver into the live tool, terminating is the
ordinary way to supersede work, and a crashed worker is terminated before its
owner is told. Each protocol's adapter sits above that and owns request
identity, response validation, and recovery.

Three behaviors changed deliberately, each because the previous one was an
oversight rather than a decision:

- A cover edit in flight is now terminated on teardown. `MapView` held no
  handle on that worker, so leaving a resort mid-clearing left it running to
  completion.
- A superseded trail grade is now terminated. The trail tool queued the next
  grade behind a computation whose result was already destined to be discarded;
  the road tool already replaced its worker.
- A crashed worker is terminated rather than left assigned. Both grade paths
  and the painting engine previously kept the dead instance and posted to it.

Disposal abandons what is in flight without retiring the adapter, matching the
terrain document: a StrictMode remount must not permanently kill a feature.
That makes `dispose` the same abandonment as a tool cancel for the dam, grade,
and paint adapters, and it is documented as such at each one; the cover adapter
adds rejecting the promise its caller is awaiting.

## D3 declared map order

The paint order and hit priority are declared once in
[`src/app/mapContribution.ts`](../../src/app/mapContribution.ts). The registry
owns reactive map/style generations and all nine families' installation, data,
visibility, hit dispatch, hover targets, capture, and cleanup. An incomplete,
repeated, or incorrectly ranked contribution set is refused. Visibility state
survives style replacement, exclusive groups arbitrate centrally, and shared
descriptors merge: one Roads toggle controls both imported OSM and player roads.

One deliberate behavior change: a run now yields to a lift crossing it. The
seven hand-maintained guard arrays had drifted — the run handler yielded to
snowmaking nodes but not to lifts — so a click where a lift crossed a run
selected the run, against the stated priority. Deriving the guards corrects it.

One documented asymmetry is preserved rather than normalized: a standalone pond
paints above a dam, but a dam picks ahead of a pond. A dam's crest is the
structure a click is aimed at; the pool it impounds is not.

Committed player roads now have a dedicated source and layer installed by the
road family above the site boundary; analysis owns only imported OSM context.
The lift, road, three snowmaking, node/path, and trail controllers now create their contributions. `MapView`
creates only the cross-cutting analysis and site-boundary contributions and no longer owns
cross-family traversal.

## D2 migrated write paths

Terrain writes now reaching the document: the saved-package load (including the
schema-4 vector-cover upgrade), package preparation, the dam, pond, road, and
trail elevation commits, the cover-clear commit shared by lifts, trails, roads,
dams, and ponds, and the post-write dirty clear in `flushTerrain`. Construction
ownership covers all five confirmations; the shared road/trail grade preview
uses the document's preview ownership.

Topology writes now reaching the document: the offline centerline backfill,
add/remove graph node, remove legacy free-standing node, confirm/delete
connector path, path closed toggle, confirm trail, patch trail, and delete
trail.

One deliberate absence remains in D2:

- The topology port has no runtime replacement command. `App` remounts
  `MapView` on a save change, so the clean load is the document's construction
  seed. The terrain document does have `replace`, because a package really is
  swapped mid-session by preparation and repair.

One terrain-adjacent call stays outside the document by design:
`setActiveResortTerrain(null)` in picking mode clears the resort protocols when
no package exists, which is not a write to a committed record.

## R1 document remediation

Terrain and topology snapshots now expose recursively read-only contracts;
topology takes an owned deep-frozen copy while terrain owns and freezes its
record shell without duplicating its large immutable buffers. Missing targets,
identical patches, and other no-op topology commands do not advance revisions.

[`src/app/committedDocumentTransaction.ts`](../../src/app/committedDocumentTransaction.ts)
provides the trail confirmation's two-phase boundary. It validates both
revisions, applies both authoritative snapshots, and only then publishes either
projection. Stale terrain therefore cannot land topology, and stale topology
cannot leave an orphaned terrain grade. Save, exit checkpoint, capture, and
dirty comparison read the topology document's synchronously published
projection rather than four render-timed React refs.

## Open decisions and blockers

- The B3 backup prerequisite is satisfied by `refs/heads/legacy/v0.1` at `3f5eb2378342053906719c75650d785ea2249241`; creating or pushing a remote tag still requires separate user authorization.
- Repository settings must make the pull-request workflow required; a workflow file alone cannot enforce the branch rule.
- Live USGS, WorldCover, Overpass, GPU, and Electron release checks remain opt-in where the environment cannot supply their external prerequisites.
- `src/app/app.css` and `src/network.ts` remain follow-up candidates; they are not opportunistic refactor scope.
