# Phase 1 · Task 15: benchmark and budgets

**Audience:** the project owner. **Status:** for your review. **Date:** 2026-10-05. **Part of:** task 15 ([0.7](phase0-0.7-phase1-plan.md)), the budgets in [0.3 §8](phase0-0.3-technical-architecture.md#8-performance-budgets-and-hardware-t13) (T13), decisions B1–B5 in the [decision record](phase0-decisions.md).

**Result:** every budget holds at 1080p on the reference PC (RTX 3060 Ti), with room to spare.

- **High:** frame p95 **7.8 ms** against 20 ms.
- **Medium,** the minimum-spec stand-in: **4.7 ms** against 18 ms.
- **Garbage:** **0 bytes per frame**, with the HUD on and off.
- **Graphics memory:** **1.4 GB** against 7 GB.
- **Instanced terrain:** it now works and is on by default.
- **Baselines:** committed with their commit SHA, with a script to compare every later run against them.

## Try it

1. `demo.bat` → **18** (rebuild the game), then **43** (the Development game).
2. **41** runs the full benchmark, about 25 minutes:
   - High and Medium in the release game;
   - garbage and memory in the Development game, with the HUD off and on;
   - then the comparison with the baselines.
3. **42** benchmarks a single preset: Low, Medium, High or Ultra.
4. **21** is the quick High run. The game also takes `-quality low|medium|high|ultra` on its own.
5. Every benchmark entry passes `-benchres 1920x1080`: a 1920×1080 window for that run only, put back to your saved window
   mode before the game quits. Never use `-screen-*` instead; Unity saves it as the game's window mode (**50** resets it).

## Quality presets (B1)

One Unity quality level and URP asset per preset. You approved them from side-by-side captures (3 views × 4 presets).

| | Low | Medium | High | Ultra |
|---|---|---|---|---|
| Render scale | 0.8 | 1.0 | 1.0 | 1.0 |
| MSAA | off | 2× | 4× | 4× |
| Shadow distance, cascades | 60 m, 1 | 100 m, 2 | 150 m, 4 | 250 m, 4 |
| Shadow map, soft shadows | 1024, off | 2048, low | 2048, high | 4096, high |
| Tree LOD bias | 1.0 | 1.5 | 2.0 | 3.0 |
| Terrain detail | Low | Medium | High | Ultra |
| Terrain sky occlusion and distant shadows | off | on | on | on |

## The benchmark (B2)

- **The path:** a fixed camera path through the eight views the earlier benchmarks used (overview, Corbet's, valley, slope, forest, in the forest, cliffs, ring forest). Each leg is a 3 s flight and a 6 s turning hold, so the camera is always moving, and every run flies the same path.
- **Laps:** one warm-up lap, which takes each leg's screenshot, then two measured laps.
- **Recorded, without allocating:** frame time, GPU time, draw calls, memory and garbage, per leg and in total.
- **Output:** a JSON file stamped with the commit it was built from, checked against the preset's budget.
- **Comparison:** `tools/perf/compare.mjs` checks a run against the stored baseline. It fails a run that is over budget, or slower than the baseline by more than 10% and 0.5 ms (run-to-run noise is about ±0.5 ms at p95).

## Results (Jackson Hole 5 km, 622,823 trees, 1920×1080, RTX 3060 Ti, commit 19d5e37)

| Preset | Frame p50 | Frame p95 | Frame p99 | GPU p95 | Draw calls | Budget (p95) | |
|---|---|---|---|---|---|---|---|
| Low | 2.1 ms | 2.9 ms | 3.6 ms | 2.0 ms | 364 | 33.3 ms | pass |
| **Medium** | 3.2 ms | **4.7 ms** | 5.6 ms | 4.1 ms | 485 | **18 ms** | pass |
| **High** | 4.3 ms | **7.8 ms** | 10.6 ms | 7.3 ms | 732 | **20 ms** | pass |
| Ultra | 5.5 ms | 11.1 ms | 17.2 ms | 10.7 ms | 814 | not checked | — |

- No frame took over 50 ms in any run.
- The slowest leg is the ring forest: 11.3 ms at High, 18.6 ms at Ultra.
- At Low the game is CPU-bound (GPU 2.0 ms against 2.9 ms frames).
- Medium runs at about 210 FPS here. An RTX 2060 is about 55–60% as fast, which puts Medium on the minimum spec near 120 FPS, well above the 30 FPS of T13.

**Garbage and memory** come from the Development game (B4), which records them exactly. That build also includes the `-pathmovie` capture mode, which isn't used while measuring.

| Run | Garbage | Graphics memory | Process memory |
|---|---|---|---|
| High | **0 B** in 15,041 frames | **1,376 MB** (≤7,168) | 2.5 GB |
| Medium | **0 B** in 20,823 frames | **1,324 MB** (≤6,144) | 3.1 GB |
| High, HUD on | **0 B** in 14,646 frames | 1,377 MB | 3.2 GB |

The release game's own check agrees: no garbage collections, and no heap growth at High, Low and Ultra. At Medium the heap grew by a single 4 KB block over 44,138 frames.

## Garbage: what it took

Before this task, the game allocated **368 bytes every frame**, even with nothing on screen, plus about 120 bytes for each HUD refresh while the camera moved.

- **The viewer's IMGUI overlay:** Unity allocates every frame for any enabled `OnGUI`, even one that draws nothing. The overlay (loading, errors, toasts, photo mode, the F1 panel) is now its own component (`ViewerOverlay`), switched off when it has nothing to draw.
- **Landmark names:** these moved from IMGUI into the HUD's UI Toolkit panel (`LandmarkLabelOverlay`).
- **The elevation readout:** it changes almost every refresh while the camera moves. Its strings are now made once, when a mountain opens (`MountainHud.PrepareElevations`). The slope and snow readouts cache theirs.
- **Two measurement artifacts, both handled in `FrameStats` and documented there:**
  - Unity's garbage counter reports a frame or two late, so the first frames after measuring starts are skipped.
  - A new text's first draw is a one-off allocation; one in 10,000 frames is allowed (B4).

## Instanced terrain (B5)

- **The old problem:** since task 06, instanced terrain drew flat and untextured.
- **Two causes:**
  - Our terrain shader had no instanced vertex path.
  - Builds stripped its instancing variants, because no material enabled instancing.
- **The fix:**
  - The shader now reads each vertex's height and normal from the heightmap, as URP's own terrain shader does.
  - The terrain material enables instancing.
- **Rendering:** the same as before. In screenshot comparisons the mean pixel difference is 0.1–0.2 out of 255.
- **Measured at High in the same session** (`-noinstancing` against the default):

| | Draw calls | Frame p50 | Frame p95 | GPU p95 |
|---|---|---|---|---|
| Not instanced | 1,270 | 4.75 ms | 7.91 ms | 7.69 ms |
| **Instanced** | **732** | **4.33 ms** | **7.82 ms** | **7.32 ms** |

- **Gains:** draw calls fall by 42%, frame p50 by 0.4 ms and GPU p95 by 0.4 ms. Frame p95 moves within run-to-run noise.
- **Why it matters:** the CPU it frees is headroom for lifts, trails and guests.
- **Default:** on, at your call. `-noinstancing` turns it off.

## Performance Testing package

- **Installed:** `com.unity.test-framework.performance` 3.5.0.
- **The test:** `BenchmarkPathTests` flies the path inside a PlayMode test and records frame, GPU and garbage samples into `PerformanceTestResults.json`. It runs in the editor, and in a standalone player (`-testPlatform StandaloneWindows64`).
- **Its numbers are informational,** because the test framework allocates for itself every frame: about 80 MB a lap in the editor, and 11.6 MB in a player. That swamps the game's zero.
- **The measurements of record** are therefore the game's own `-benchmark` runs above.
- **Side fix:** the picker tests now guard their editor-only asset loading, so the PlayMode suite also builds into a player.

## Against the Phase 1 exit criteria (for task 16)

- **Criterion 4, met:** reference PC, frame p95 ≤20 ms at 1080p High on the Jackson Hole demo. Measured 7.8 ms.
- **Criterion 5, met:** the minimum-spec stand-in, about 55 FPS (p95 ≤18 ms) at Medium within 6 GB of graphics memory. Measured 4.7 ms and 1.3 GB.
- **Garbage, met:** 0 bytes per frame in steady state with the camera moving.

## Flagged for later

- **Ultra:** the ring forest at Ultra reaches 18.6 ms p95. That's fine, since Ultra is unbudgeted, but it's the first view to watch as forests grow.
- **Real minimum-spec hardware:** a real RTX 2060-class machine, the old laptop as a Low smoke test, and a one-hour soak for memory growth all belong to Phase 2 (0.6).
- **Instanced terrain normals:** these come from the heightmap in the vertex stage. Unity's per-pixel normal map isn't used.
