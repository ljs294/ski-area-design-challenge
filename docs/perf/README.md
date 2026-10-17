# Performance baselines

The stored benchmark runs that every rendering PR is compared with (0.3 §8.1; task 15). Each file is a game's own
`-benchmark` report (schema `mountain-planner-benchmark/1`) over the fixed camera path on the Jackson Hole 5 km demo,
on the reference PC (RTX 3060 Ti, Ryzen 5 5600X) at 1920×1080. `tools/perf/compare.mjs` fails a run that misses its
budget, or that is slower than its baseline by more than 10% and 0.5 ms (run-to-run noise).

```
node tools/perf/compare.mjs docs/perf/jackson-hole-5km-high.json <run.json>
```

## Files

| File | Player | Command (all with `-benchres 1920x1080`) | Frame p95 | GPU p95 |
| --- | --- | --- | --- | --- |
| `jackson-hole-5km-high.json` | Release | `-quality high` | 7.94 ms | 7.76 ms |
| `jackson-hole-5km-medium.json` | Release | `-quality medium` | 4.67 ms | 4.43 ms |
| `jackson-hole-5km-low.json` | Release | `-quality low` | 2.87 ms | 2.05 ms |
| `jackson-hole-5km-ultra.json` | Release | `-quality ultra` (no budget: reported, not checked) | 11.32 ms | 11.15 ms |
| `jackson-hole-5km-high-noinstancing.json` | Release | `-quality high -noinstancing` | 8.01 ms | 7.77 ms |
| `jackson-hole-5km-high-dev.json` | Development | `-quality high -laps 1` (exact garbage and memory) | 8.19 ms | 7.86 ms |
| `jackson-hole-5km-medium-dev.json` | Development | `-quality medium -laps 1` | 4.83 ms | 4.56 ms |
| `jackson-hole-5km-high-dev-hud.json` | Development | `-quality high -laps 1 -withhud` | 8.02 ms | 7.83 ms |

`demo.bat` 41 runs High and Medium (Release and Development) and High with the HUD, and compares each with its file;
`demo.bat` 42 runs one preset. Run every benchmark through the GPU's measure lane (`tools/gpu/gpu.ps1 run -Lane
measure`), on the desktop itself (remote desktop reports 640×480), with nothing else busy on the machine.

**Low is CPU-bound and noisy.** Its frame time (about 2 ms of GPU work) moves with whatever else the CPU is doing:
four runs on the same machine, two of main and two of P3-01's branch, gave p95 3.10, 3.91, 3.37 and 3.31 ms with GPU
p95 2.05–2.13 ms throughout. The pinned 2.87 ms is from the fast end, so a Low failure in `compare.mjs` isn't a
regression until an A/B on the same machine, run back to back, says so. High, Medium and Ultra are GPU-bound and
steady (High p95 within 4% across runs).

## History

- **2026-10-05, task 15** (commit 19d5e37): the first baselines, lit by the scene's default lighting preset (the clock didn't run yet).
- **2026-10-16, Phase 3 task 01** (commit 6e803d3, `unity-m2`; decision E10): all eight re-pinned. Since P2-02 (#68)
  the view's clock runs the sun and a benchmark opens at **10:30**, so the old files were measured under a different
  light and no longer described main. Since then P2-01's HUD theme, P2-09's forest look (+6.8% at High) and P2-05's
  settings have also landed. The new files were measured from clean players built at 6e803d3, one run after another
  under one measure slot; a second High and Medium run passed `compare.mjs` against them (both 4% faster at p95:
  High 7.63 ms, Medium 4.49 ms). Low was measured again on its own, because the first Low run shared the CPU with
  other work.

## Re-pinning

Re-pin only when a change is meant to cost or save frame time, or when the benchmark itself changes (its path, its
light, its site), and say why here. Build both players from a clean commit (the report records the SHA and whether the
tree was dirty), run all eight in a row under one measure slot, confirm with a second High and Medium run, and replace
the files in the same PR as the change.
