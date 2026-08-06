# Current architecture

This document describes the repository as it is currently landed. Proposed refactors belong in the execution ledger, not here.

## Runtime surfaces

The supported product renderer starts at [`index.html`](../index.html), which loads [`src/app/main.tsx`](../src/app/main.tsx). That entry mounts the React application. [`src/app/App.tsx`](../src/app/App.tsx) owns the top-level screen state for the menu, new-game picker, active game, map management, and graphics lab. It also coordinates save loading, boot progress, settings, and close checkpoints.

[`src/app/MapView.tsx`](../src/app/MapView.tsx) is the main gameplay orchestrator. It currently owns MapLibre creation and style lifecycle, terrain preparation and committed terrain state, save/load snapshots, source refreshes, selection, construction workflows, capture composition, and most tool state. Feature presentation is split among React controls and panels under `src/app/`; layer installation and GeoJSON conversion are partly split into `*Layers.ts` modules.

The desktop shell starts at [`electron/main.ts`](../electron/main.ts). It creates the Electron window and registers terrain and game-save filesystem handlers. [`electron/preload.ts`](../electron/preload.ts) exposes a narrow context-bridge API; renderer code consumes it through [`src/desktopBridge.ts`](../src/desktopBridge.ts). The renderer has no direct Node access.

The web build uses [`vite.config.web.ts`](../vite.config.web.ts) and the same React renderer. The existing GitHub Pages workflow builds that static variant.

Two developer surfaces are supported:

- [`src/app/GraphicsLab.tsx`](../src/app/GraphicsLab.tsx) is a React graphics lab opened through `npm run dev:lab` or the application shortcut.
- [`spike.html`](../spike.html) with [`src/spike/spikeMain.ts`](../src/spike/spikeMain.ts) is a standalone MapLibre data-source spike served with the spike Vite configuration.

## Data and persistence

Authoritative models live under [`src/types/`](../src/types/), ordered from dependency-neutral geographic, construction, earthwork, anchor, and raw-vector types through cover and terrain, then lift, road, snowmaking, topology, trail, and finally `GameSave`. The graph is acyclic and model files do not import implementation or application modules. Snowmaking nodes and ski topology are defined with their persisted domains; `skiNodes.ts` and `snowmakingNodes.ts` contain behavior only. Root domain modules import authoritative models directly.

[`src/types.ts`](../src/types.ts) is a type-only compatibility facade for aggregate UI and IPC consumers. Its exact named-export manifest, lack of runtime exports, and 100-line budget are enforced by the architecture checker. [`src/gameSaveSchema.ts`](../src/gameSaveSchema.ts) owns the schema version written by new saves; compile-time compatibility tests preserve the `GameSave` field types and optionality while representative schema-v1 and schema-v11 fixtures exercise hydration.

Terrain preparation is orchestrated by [`src/terrainIngest.ts`](../src/terrainIngest.ts). It fetches elevation and surrounding elevation, NAIP data, and vector context; derives four-class cover and display assets; persists and verifies a `TerrainRecord`; and returns that persisted record directly. Browser-only WorldCover sampling enters through the required `ResortPreparationServices` port supplied by `MapView`; preparation progress and cancellation use a named options object. Browser storage fallback and Electron storage are accessed through [`src/terrainStorageClient.ts`](../src/terrainStorageClient.ts).

`GameSave` currently supports schema versions 1 through 11. New snowmaking-node saves use version 11, and the save client contains compatibility normalization for older schemas. Browser storage uses local storage while Electron delegates through the preload bridge to filesystem-backed IPC handlers.

## Map and worker ownership

Map feature ordering is declared once by the contribution registry described below, and every worker is owned by the adapter described below it. [`src/app/toolCoordinator.ts`](../src/app/toolCoordinator.ts) is the synchronous authority for the seven construction tools, dock state, and Layers-alongside-build behavior. Controllers register cancellation callbacks; changing tools cancels the prior owner before publishing the replacement. Selection stays outside that coordinator but enters through one centralized transition in `MapView`.

[`src/app/mapInteractionLease.ts`](../src/app/mapInteractionLease.ts) exclusively leases controller cursor, drag-pan, and double-click-zoom overrides and restores the exact prior map state once on release or disposal. The separate site-box gesture is not a construction controller. Escape remains in each existing feature listener.

## Committed documents

[`src/app/terrainDocument.ts`](../src/app/terrainDocument.ts) is the authority for the committed terrain package. Snapshots expose a recursively read-only record and carry a monotonic revision; a commit names the expected revision, the next `TerrainRecord`, and whether it dirties elevation or cover. A commit built on a superseded revision is rejected with no effect on the record, revision, React state, dirty flags, display caches, protocols, or map sources. One publication moves the height/cover-display/imagery caches, the resort protocols, the React projection with its dirty flag, and only the map sources the change invalidated. Initial load and package preparation use an explicit clean replacement path instead of masquerading as an edit, and `markPersisted` clears the dirty flag only when nothing landed while the write was in flight. `MapView` owns the document, re-binding its ports each render because the contour and source refreshes read the current unit setting; `terrainRecordRef` is a mirror written by the publication, never a second authority.

The same document owns construction ownership and cover-edit serialization. [`src/app/constructionLock.ts`](../src/app/constructionLock.ts) holds `ConstructionActivity` and takes ownership synchronously before a confirmed build reaches its first await, so a second confirmation in the same tick is rejected; release is ticketed, so an older operation cannot hand ownership away from a newer one, and it happens in `finally` on success, failure, and cancellation. Cover edits run one at a time against the snapshot current when each starts, and a cover edit that finishes against a superseded package is dropped rather than published over the newer one — still best effort, and never a rollback of committed infrastructure. `PreviewOwnership` issues the tokens that let a late terrain-grade worker response identify itself as superseded; the existing request-id and geometry/elevation-checksum protections are unchanged.

[`src/app/topologyDocument.ts`](../src/app/topologyDocument.ts) makes trails, ski nodes, connector paths, and junctions one revisioned document. Its owned snapshots are deeply frozen. A transaction takes a working copy, applies pure operations from [`src/topology.ts`](../src/topology.ts), and publishes every collection it moved in one change against the revision it began from; a stale transaction is rejected without partial writes, and identity comparison decides which collections publish, so missing targets and identical patches move nothing and do not advance the revision. Confirming a run materializes both anchor junctions and lands them with the run itself, and deleting a run prunes exactly the junctions nothing references any more, lift terminals excepted. The port owns junction materialization and publication only: `buildSkiNetwork`, coordinate frames, sanitizers, derived-network reads, and feature-controller behavior stay outside it. There is no runtime replacement command because opening another resort remounts the session, so the clean load is the document's construction seed.

[`src/app/committedDocumentTransaction.ts`](../src/app/committedDocumentTransaction.ts) coordinates the one command that changes both documents: confirming a graded trail. Both revisions validate first, both authoritative snapshots apply second, and only then do terrain and topology notify their observers. Save and exit checkpoints read the same synchronous committed topology projection, so React scheduling cannot produce a mixed trail/node/path/junction save.

Feature-controller extraction is still coordinated directly in `MapView`.

## Worker adapters

[`src/app/workerAdapter.ts`](../src/app/workerAdapter.ts) runs one worker at a time and binds handlers to the instance that was running when they were bound, so a retired worker cannot deliver into the live tool and terminating is the ordinary way to supersede work. A worker that raises an uncaught error is terminated before its owner is told, because an engine that threw has already lost whatever state the next request would be answered against. Request identity, response validation, and recovery belong to each protocol and stay in the adapter above the session.

`MapView` constructs no workers. It holds one adapter per protocol and disposes each on teardown; disposal abandons what is in flight without retiring the adapter, so a StrictMode remount stays usable. [`damAnalysisClient.ts`](../src/app/damAnalysisClient.ts) numbers each alignment and abandons the previous one, so a superseded pond can never reach review. [`terrainGradeClient.ts`](../src/app/terrainGradeClient.ts) is shared by the road and trail tools because one grade preview exists on the map; it checks the preview token, the elevation checksum, and the geometry key before a finished grade is applied, and reports a failure of any of them as superseded. [`trailPaintClient.ts`](../src/app/trailPaintClient.ts) drives the one stateful engine: requests are numbered so an out-of-order preview cannot repaint the canvas backwards, the watermark resets whenever a new engine starts, and one crash is recovered by starting a replacement and asking its owner to replay. [`coverEditClient.ts`](../src/app/coverEditClient.ts) settles one promise per edit — success, refusal, crash, timeout, or teardown — and terminates the worker on every ending.

Worker protocol, engine, and worker entry files live under `src/app/`.

## Map contributions

The required visual order is bottom-to-top: analysis, site boundary, road, dam, pond, ski-node/path, trail, lift, and snowmaking nodes. The required hit priority is snowmaking nodes, lift, trail, dam, pond, stream, and lake. [`src/app/mapContribution.ts`](../src/app/mapContribution.ts) declares both orders once and derives everything else from them. A family contributes an `install` step and, when it draws something in-progress, a capture transient; a hit family contributes the layers its clicks are delegated to. An incomplete or repeated set of contributions is refused rather than silently reordered.

`MapView` holds the legacy contributions — each closes over its refs until E1–E5 move them to controllers — and walks them in exactly one place for style (re)load, and one place for capture hide/restore. Every click handler yields to the layers of the families that pick ahead of it, and that guard is now derived from the declared priority instead of re-accumulated per handler; a run therefore yields to a lift crossing it. Pond paints above dam while dam picks ahead of pond: that inversion is deliberate, since a dam's crest is the structure a click is aimed at and the pool it impounds is not.

## Verification state

Vitest tests are colocated with source as `*.test.ts` and `*.test.tsx`. `npm test` and `npm run test:unit` are deterministic offline gates; live-provider cases use `*.integration.test.ts` and run only through `npm run test:integration:live`. Playwright Test uses a managed fixed-port preview, deterministic save/terrain fixtures, blocked external HTTP, one worker, and failure-only traces/screenshots. `npm run check:e2e-harness` proves nonzero failure propagation; `npm run test:e2e` runs the deterministic browser smoke and feature projects. Live-provider and packaged-Electron projects are opt-in. The older `scripts/verify*.mjs` files are not gates.

TypeScript runs with `strict` plus the existing unused-code and switch checks. Flat ESLint configuration enforces the React rules of hooks and treats unexplained or obsolete suppressions as errors. [`scripts/checkArchitecture.mjs`](../scripts/checkArchitecture.mjs) checks core-to-app boundaries and type-model isolation/cycles; there are no core-to-app exceptions.

`npm run check` is the canonical deterministic gate: agent-document checks, architecture checks, lint, strict typechecking, offline unit tests, desktop and web production builds, and an obsolete-vertical guard over source paths, package metadata, and built output. [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs `npm ci`, that aggregate command, the negative-control harness proof, and deterministic Playwright workflows for pull requests and protected development branches. Repository branch settings must make the workflow required; the workflow file cannot enforce that setting by itself.
