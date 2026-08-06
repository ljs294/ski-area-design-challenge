# Refactor metrics

Run [`scripts/refactorMetrics.mjs`](../../scripts/refactorMetrics.mjs) at each green benchmark:

```text
node scripts/refactorMetrics.mjs
```

The script prints stable, machine-readable JSON and intentionally omits a timestamp. It measures the working tree, reports the current Git `HEAD`, and sets `git.dirty` when tracked or untracked changes are present.

## Measurement definitions

- Production TypeScript is every `.ts` and `.tsx` file under `src/` and `electron/`, excluding declaration files, test/spec files, and `generated` or `__generated__` paths.
- Test TypeScript is every `.test.ts`, `.test.tsx`, `.spec.ts`, and `.spec.tsx` under `src/` or `electron/`, plus all `.ts` and `.tsx` files under `tests/`. Ad hoc `scripts/verify*.mjs` files are not counted as tests.
- Physical lines include blank and comment lines and count a final non-newline line.
- MapView syntax counts come from the TypeScript AST: top-level import declarations, direct `useState` and `useEffect` calls, and `new Worker` expressions.
- The type-facade importer count covers production files with a static import, re-export, import type, or dynamic import that resolves directly to `src/types.ts`.
- The obsolete vertical is the tracked, present poster entrypoint and its renderer/GIS/content modules, legacy layer helpers, preset catalog/downloader, and every tracked file below `public/presetTerrain/`.
- Dependency counts are the direct keys in `dependencies` and `devDependencies`; transitive packages are excluded.
- Artifact bytes recursively total regular files currently present under `dist/` and `dist-web/`. These directories are generated, so a trustworthy release snapshot must follow fresh builds.

## Pre-structural-refactor baseline candidate

This candidate was measured while `HEAD` was `574e68745e17083002588aca7c5f4884a887c310` (`574e687`). The production tree was unchanged from that A1 commit, but concurrent A2 test and toolchain changes made the working tree dirty. Consequently, the production/MapView/type/obsolete values are useful pre-structural baselines, while the test, dependency, and artifact values must be finalized at the clean A2 benchmark commit.

| Metric | Candidate value |
| --- | ---: |
| Git dirty | `true` |
| Production TypeScript files | 131 |
| Production physical lines | 29,964 |
| Test TypeScript files | 71 |
| Test physical lines | 8,309 |
| MapView physical lines | 5,076 |
| MapView import declarations | 78 |
| MapView `useState` calls | 60 |
| MapView `useEffect` calls | 44 |
| MapView `new Worker` expressions | 4 |
| `src/types.ts` physical lines | 585 |
| `src/types.ts` production importers | 74 |
| Obsolete vertical tracked files | 21 |
| Obsolete vertical bytes | 65,351,258 |
| `public/presetTerrain` bytes | 65,231,155 |
| Direct dependencies | 10 |
| Direct development dependencies | 16 |
| `dist` artifact bytes | 73,224,628 |
| `dist-web` artifact bytes | 73,224,606 |

## Benchmark snapshots

Append only green, committed snapshots. Capture the JSON after required tests and fresh builds, confirm `git.dirty` is `false`, then record the immutable commit SHA. The A2 row replaces the candidate as the formal baseline.

| Snapshot | Commit | Clean | Prod files/lines | Test files/lines | MapView lines/imports/state/effects/workers | Types lines/importers | Obsolete files/bytes | Preset bytes | Deps/dev deps | Dist/dist-web bytes |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| A2 formal baseline | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending |
| B3 obsolete vertical removed | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending |
| D4 foundations complete | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending |
| F1 final structural gate | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending | Pending |
