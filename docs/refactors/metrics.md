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
| A2 formal baseline | `7215da704c203b78ced0516502349cd9bf515804` | Yes | 131 / 29,968 | 64 / 7,978 | 5,077 / 78 / 60 / 44 / 4 | 585 / 74 | 21 / 65,351,258 | 65,231,155 | 10 / 16 | 73,224,519 / 73,224,497 |
| A3 Playwright gate | `a52b54ab99988f132c503759be1608c843738b02` | Yes | 131 / 29,968 | 71 / 8,309 | 5,077 / 78 / 60 / 44 / 4 | 585 / 74 | 21 / 65,351,258 | 65,231,155 | 10 / 17 | 73,224,519 / 73,224,497 |
| B1 resort service boundary | `f62dea0dd20aa6f8380d0920e172899e5de02c56` | Yes | 132 / 29,982 | 72 / 8,422 | 5,082 / 78 / 60 / 44 / 4 | 585 / 74 | 21 / 65,351,258 | 65,231,155 | 10 / 17 | 73,224,624 / 73,224,602 |
| B2 persisted terrain return | `7e3ba52d4e1c58a20a848f9f7f14562465d45ac0` | Yes | 132 / 29,982 | 73 / 8,534 | 5,082 / 78 / 60 / 44 / 4 | 585 / 74 | 21 / 65,351,258 | 65,231,155 | 10 / 17 | 73,222,104 / 73,222,082 |
| B3 obsolete vertical removed | `418e86049a32af01b4da0ed068d109bb64dcd5d1` | Yes | 122 / 27,664 | 73 / 8,530 | 5,082 / 78 / 60 / 44 / 4 | 570 / 68 | 0 / 0 | 0 | 8 / 17 | 7,990,761 / 7,990,739 |
| C1 foundational type models | `eef74944fda4da24cb81b8dbfe197639ee0b863d` | Yes | 128 / 27,623 | 73 / 8,530 | 5,082 / 78 / 60 / 44 / 4 | 217 / 52 | 0 / 0 | 0 | 8 / 17 | 7,990,761 / 7,990,739 |
| C2 acyclic domain models | `dbf447e507c8d021a36b12ddf078f8f5a1eea221` | Yes | 135 / 27,612 | 74 / 8,651 | 5,080 / 78 / 60 / 44 / 4 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 7,990,761 / 7,990,739 |
| D1 tool/interaction ownership | `bc9fb3790ce0958d090dc33f8b825a515df26860` | Yes | 137 / 27,859 | 76 / 8,839 | 5,062 / 80 / 59 / 44 / 4 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 7,993,914 / 7,993,892 |
| D2 terrain/topology documents | `1dfcf24d2031fcb5a286dc81386bc4ff5bb435dd` | Yes | 140 / 28,431 | 79 / 9,535 | 5,135 / 83 / 59 / 44 / 4 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 7,999,804 / 7,999,782 |
| D3 map contribution registry | `88ff5bffd0b194006e0816bbc5d32222810cb35c` | Yes | 141 / 28,571 | 81 / 9,929 | 5,165 / 84 / 59 / 44 / 4 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 7,999,749 / 7,999,727 |
| D4 worker adapters | `35fd9469bc12a1c87330e26d30591e530310bda5` | Yes | 145 / 28,928 | 87 / 10,756 | 5,126 / 85 / 59 / 44 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,000,880 / 8,000,857 |
| R1 document remediation | `e61821e3d84b7911048bf2fc89d489be36db9454` | Yes | 147 / 29,252 | 88 / 10,961 | 5,145 / 86 / 59 / 44 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,004,524 / 8,004,501 |
| R2 complete map registry | `a7439ce1693bb758ac702675090e88b2447afa39` | Yes | 147 / 29,495 | 88 / 11,177 | 5,085 / 86 / 59 / 42 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,009,037 / 8,009,014 |
| R3 worker hardening | `ede5ac6487523e88c14065d7092725d23ea8cbb8` | Yes | 147 / 29,626 | 88 / 11,362 | 5,086 / 86 / 59 / 42 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,011,563 / 8,011,540 |
| R4 browser reacceptance | `a1e6cc3d1ba70a7774cbc4a1cadbd3f42d71d42b` | Yes | 147 / 29,626 | 90 / 11,540 | 5,086 / 86 / 59 / 42 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,011,563 / 8,011,540 |
| E1 lift controller | `5a6bfcc38adbb96dc3a7547acda3bd7074b06c79` | Yes | 149 / 29,787 | 91 / 11,608 | 4,921 / 86 / 58 / 40 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,013,367 / 8,013,344 |
| E2 road controller | `62a5c68d0dc1c5e9ddad7d6619b3dab6116a5ecd` | Yes | 151 / 29,916 | 92 / 11,718 | 4,704 / 85 / 57 / 38 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,015,245 / 8,015,222 |
| E3 snowmaking controllers | `bdcaf4978b4efc234600699b12e29446725f6db0` | Yes | 157 / 30,277 | 94 / 11,847 | 4,325 / 81 / 55 / 32 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,020,936 / 8,020,913 |
| E4 node/path controller | `66f78c01deb0900fbde95377bb516c605ab0f1e6` | Yes | 159 / 30,328 | 96 / 11,940 | 4,045 / 81 / 52 / 28 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,023,656 / 8,023,633 |
| E5 trail controller | `d39bdfd1a0562625cb6ab08793f8a98ff89950f9` | Yes | 162 / 30,327 | 97 / 12,108 | 3,222 / 76 / 50 / 24 / 0 | 35 / 43 | 0 / 0 | 0 | 8 / 17 | 8,027,434 / 8,027,411 |
| F1 final structural gate | `f0489b30465c697a8d5c29adbde26edb8f157dbd` | Yes | 171 / 30,138 | 97 / 12,127 | 1,799 / 40 / 49 / 8 / 0 | 35 / 44 | 0 / 0 | 0 | 8 / 17 | 8,032,209 / 8,032,186 |
| F2 final acceptance | `d089a67b30e601711c718bf6bb3b661c513b5f5b` | Yes | 171 / 30,138 | 97 / 12,197 | 1,799 / 40 / 49 / 8 / 0 | 35 / 44 | 0 / 0 | 0 | 8 / 17 | 8,032,209 / 8,032,186 |

## Change from the A2 formal baseline through F1

| Metric | Net change |
| --- | ---: |
| Production physical lines | +170 (+0.6%) |
| Test physical lines | +4,219 (+52.9%) |
| MapView physical lines | -3,278 (-64.6%) |
| MapView direct imports | -38 (-48.7%) |
| MapView direct state/effect/worker calls | -11 / -36 / -4 |
| Type-facade physical lines | -550 (-94.0%) |
| Type-facade production importers | -31 (-41.9%) |
| Obsolete tracked bytes | -65,351,258 |
| Desktop production artifact bytes | -65,192,310 (-89.0%) |

Controller extraction alone brought `MapView` down to 3,222 lines. F1 then
separated presentation chrome, map runtime, sampling, compatibility backfill,
cover editing, worker ownership, and initial-save hydration. The resulting
1,799 lines, 40 imports, 8 local effects, and zero worker construction meet all
four approved structural budgets without an import barrel.
