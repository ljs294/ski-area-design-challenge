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
| `jackson-hole-5km-high-lifts40.json` | Release | `-quality high -liftbench 40` (40 built lifts: 14 Sessellift FGQ-4, 13 D6, 13 Monta FG4; the log's `[Chairs]` line gives their main-thread cost, 0.81 ms) | 8.34 ms | 8.38 ms |

`demo.bat` 41 runs High and Medium (Release and Development) and High with the HUD, and compares each with its file;
`demo.bat` 42 runs one preset. Run every benchmark through the GPU's measure lane (`tools/gpu/gpu.ps1 run -Lane
measure`), on the desktop itself (remote desktop reports 640×480), with nothing else busy on the machine.

**Low is CPU-bound and noisy.** Its frame time (about 2 ms of GPU work) moves with whatever else the CPU is doing:
four runs on the same machine, two of main and two of P3-01's branch, gave p95 3.10, 3.91, 3.37 and 3.31 ms with GPU
p95 2.05–2.13 ms throughout. The pinned 2.87 ms is from the fast end, so a Low failure in `compare.mjs` isn't a
regression until an A/B on the same machine, run back to back, says so. High, Medium and Ultra are GPU-bound and
steady (High p95 within 4% across runs).

## The drawn resort (Phase 3 task 11)

The same path with a resort built on it (`-drawnresort`: 20 trails, 2 cat tracks, 6 lifts, built by command as it
opens), and the same path, with its extra legs, over the bare mountain (`-drawnpathonly`), so the design's own cost
is the difference. These aren't baselines for `compare.mjs` against the eight above (the path has three more legs);
they're the reference for later drawn runs. Built at 0b6a5bf3, clean, one run after another under one measure slot.

| File | Player | Command | Frame p95 | GPU p95 |
| --- | --- | --- | --- | --- |
| `jackson-hole-5km-high-drawn.json` | Release | `-quality high -drawnresort` | 8.76 ms | 8.65 ms |
| `jackson-hole-5km-high-drawnpath.json` | Release | `-quality high -drawnpathonly` | 8.56 ms | 8.40 ms |
| `jackson-hole-5km-medium-drawn.json` | Release | `-quality medium -drawnresort` | 5.17 ms | 5.08 ms |
| `jackson-hole-5km-medium-drawnpath.json` | Release | `-quality medium -drawnpathonly` | 5.05 ms | 4.90 ms |
| `jackson-hole-5km-high-dev-drawn.json` | Development | `-quality high -laps 1 -drawnresort` (0 B garbage, 1,627 MB graphics memory) | 8.90 ms | 8.93 ms |

The resort costs +0.20 ms at High and +0.12 ms at Medium. `jackson-hole-5km-design-edits.json` is the edit timing
with the resort loaded (`-designbench`, five rounds); [edit-timing.md](edit-timing.md) has its tables, what changed
and what still misses. `demo.bat` 72 opens the resort, 72b runs the frame benchmark, 72c the edit timing; each writes
under `test-results\p3-11\` beside `demo.bat` itself, whichever folder it is started from.

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
- **2026-10-20, Phase 3 task 09** (commit after a314b7c): `jackson-hole-5km-high-lifts40.json` added. 40 lifts built by `-liftbench 40` (a grid of fixed-grip quads 600 m up the fall line, the game's own build path) cost +0.38 ms p95 at High against the same build with none (7.42 ms); chairs are 0.44 ms a frame of main-thread work with 0 B garbage, so the planned Burst chair job waits (LT29).
- **2026-10-29, Phase 3 task 11** (commit 0b6a5bf3): the drawn resort's five reports and its edit timing added (section above); the eight baselines are unchanged (DB5).
- **2026-11-03, Phase 3 task 16 BB8** (commit 92ac93f4, clean): `jackson-hole-5km-high-lifts40.json` re-pinned. Since P3-09 PR C (#97) `-liftbench 40` builds every third lift as each type, 14 Sessellift FGQ-4, 13 D6 and 13 Monta FG4, not 40 quads; the planned re-pin was never done, so main measured 8.74 / 8.60 ms p95 frame / GPU on 2026-11-01 against the pinned 7.80 / 7.57. New: 8.34 / 8.38 ms; two confirming runs in the same measure session passed `compare.mjs` (8.27 / 8.36, 8.24 / 8.35). The BB7 quit-crash fix in the same PR (the lift shader's accent colour moved to the end of its material block) costs nothing: main as it was (34a11eb8, crashing on quit both times) measured 9.10 / 8.48 and 8.24 / 8.37 in the same session, and main with the pre-LT41 shader 8.26 / 8.36 and 8.17 / 8.34.
- **2026-11-07, Phase 4 task 01** (v0.3.2 release zip, then branch `feature/p4-01-small-fixes`): v0.3.2 against the baselines, on an idle GPU (a first run on 2026-11-06 shared the GPU with another game and was discarded). High 7.77 / 7.58 ms p95 frame / GPU (pass, −2.2%); Medium 5.02 / 4.77 (+7.5%; valley +14%, inforest +12% by spot); 40 lifts 9.40 / 9.23 (+12.7%, fail), its `[Chairs]` line 2.45 ms a frame against 0.81. The cause: the rope sheaves that turn with the rope (#134) read every pivot's world position each frame to test its distance. The fix tests the contact cached at the pose and skips a lift whose sheaves are all past 300 m: 40 lifts 8.69 / 8.50 (+4.2%), `[Chairs]` 0.86 ms. Corbet, valley and slope stay 11–15% over by spot (+0.6–0.9 ms, CPU, GPU +1.5%), as Medium's valley does with no lifts; recorded, not chased. Baselines unchanged.
- **2026-11-07, Phase 4 task 01, Direct3D 11** (branch `feature/p4-01-d3d11`, owner's choice): the Windows player now starts on D3D11 (D3D12 stays second in the list, for `-force-d3d12`). D3D12 crashed as the game quit, an access violation in `D3D12Core.dll` as Unity tore the device down (v0.3.1 and v0.3.2 each 2 of 15 style-tile quits); D3D11 quit 30 of 30 clean. Against the baselines (p95 frame / GPU ms, baseline then D3D11): High 7.94 / 7.76 to 7.12 / 7.05; Medium 4.67 / 4.43 to 4.12 / 4.02; Low 2.87 / 2.05 to 2.30 / 2.14; Ultra 11.32 / 11.15 to 10.82 / 10.70; 40 lifts 8.34 / 8.38 to 7.72 / 7.63; drawn resort High 8.76 / 8.65 to 8.30 / 8.43, Medium 5.17 / 5.08 to 4.56 / 4.52. Every run passes. The style tile matches D3D12 within 26 of 255 on 0.17% of pixels; the 2 km trail shots differ on about 1% of pixels, single pixels at tree crowns' edges, not visible side by side. The baselines stay as they are (the switch isn't meant to change frame time; they are D3D12's, and D3D11 runs pass them).
