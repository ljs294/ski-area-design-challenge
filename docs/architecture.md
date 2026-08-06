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

The repository also still contains an obsolete poster-style application at [`menu.html`](../menu.html) and [`src/main.ts`](../src/main.ts). It uses Leaflet, a GIS selector, a content manager, curated presets, and the legacy canvas renderer. It is not the supported `index.html` application, but it has not yet been deleted.

## Data and persistence

[`src/types.ts`](../src/types.ts) currently contains the aggregate persisted and runtime type model. It imports some types from domain modules, including elevation, vector features, ski nodes, and snowmaking nodes. Domain modules at the root of `src/` implement terrain, cover, lift, road, snowmaking, trail, topology, and network behavior. Several of those modules import through the aggregate type file, so the model is not yet split into an acyclic `src/types/` hierarchy.

Terrain preparation is orchestrated by [`src/terrainIngest.ts`](../src/terrainIngest.ts). It fetches elevation and surrounding elevation, WorldCover and NAIP data, and vector context; derives four-class cover and display assets; persists a `TerrainRecord`; verifies packaged terrain; and currently hydrates a larger `TerrainDB` runtime object. The live `MapView` consumes the returned record. Browser storage fallback and Electron storage are accessed through [`src/terrainStorageClient.ts`](../src/terrainStorageClient.ts).

`GameSave` currently supports schema versions 1 through 11. New snowmaking-node saves use version 11, and the save client contains compatibility normalization for older schemas. Browser storage uses local storage while Electron delegates through the preload bridge to filesystem-backed IPC handlers.

## Map and worker ownership

Map feature ordering is distributed across layer modules and `MapView`; there is no shared contribution registry yet. Tool arbitration, construction locking, terrain revisions, and topology changes are also coordinated directly in `MapView`, not through independent ports.

Cover editing already has a client abstraction in [`src/app/coverEditClient.ts`](../src/app/coverEditClient.ts). Dam analysis, terrain grading, and trail painting workers are still constructed directly from `MapView`. Worker protocol, engine, and worker entry files live under `src/app/`.

The required visual order is bottom-to-top: analysis, site boundary, road, dam, pond, ski-node/path, trail, lift, and snowmaking nodes. The required hit priority is snowmaking nodes, lift, trail, dam, pond, stream, and lake. Refactor work must characterize and preserve both orders.

## Verification state

Vitest tests are colocated with source as `*.test.ts` and `*.test.tsx`. `npm test` and `npm run test:unit` are deterministic offline gates; live-provider cases use `*.integration.test.ts` and run only through `npm run test:integration:live`. Playwright Test uses a managed fixed-port preview, deterministic save/terrain fixtures, blocked external HTTP, one worker, and failure-only traces/screenshots. `npm run check:e2e-harness` proves nonzero failure propagation; `npm run test:e2e` runs the deterministic browser smoke and feature projects. Live-provider and packaged-Electron projects are opt-in. The older `scripts/verify*.mjs` files are not gates.

TypeScript runs with `strict` plus the existing unused-code and switch checks. Flat ESLint configuration enforces the React rules of hooks and treats unexplained or obsolete suppressions as errors. [`scripts/checkArchitecture.mjs`](../scripts/checkArchitecture.mjs) checks core-to-app boundaries and type-model isolation/cycles; it reports one narrowly keyed temporary `terrainIngest` exception that benchmark B1 must remove.

`npm run check` is the canonical deterministic gate: agent-document checks, architecture checks, lint, strict typechecking, offline unit tests, and desktop and web production builds. [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) runs `npm ci`, that aggregate command, the negative-control harness proof, and deterministic Playwright workflows for pull requests and protected development branches. Repository branch settings must make the workflow required; the workflow file cannot enforce that setting by itself.
