# Phase 3 · 01 Carry-over polish and baselines: plan

**Audience:** the project owner and coding agents. **Status:** approved by the owner on 2026-10-16 (E15), with every
recommendation. Task row: [Phase 3 plan §3, row 01](phase2-14-phase3-plan.md#3-tasks). Branches
`feature/p3-01-baselines` (PR A) and `feature/p3-01-carry-over` (PR B); `demo.bat` 60.

## Owner decisions

| # | Question | Decision |
| --- | --- | --- |
| C1 | Split the baselines into their own PR, first? | **Yes**: P3-02 and every later task compare against them |
| C2 | Fix tester findings 3–5 now? | **Yes, all three** |
| C3 | Tree flicker (finding 6): capture on the laptop first? | It **happens on the main PC too** (owner), so it's captured and diagnosed there |
| C4 | Photo bar by keyboard | **Tab** hands the bar the keys, the arrows and Tab move inside it, **Esc** gives them back |
| C5 | Re-pin all 8 baseline files, or only demo 41's 5? | **All 8** |

## Items

| Item | Approach | Proof |
| --- | --- | --- |
| Baselines (E10) | Release and Development players built from clean main 6e803d3; all 8 runs in a row under one measure slot; a second High and Medium run checked with `compare.mjs`; [docs/perf/README.md](../perf/README.md); demo 41 also compares the Development runs | The JSON reports (SHA stamped), the confirmation compares |
| Finding 3: no-reading elevation unit | `UnitFormat.NoElevation`, read by the HUD bar (the m/ft pair was in the wrong order for `UnitSystem`) | Core test, both systems |
| Finding 4: sign focus | Hover slides the sign; keyboard focus shows the shared focus ring (only after a navigation key), so Continue answers the pointer when the title opens | EditMode test on Flow.uss, capture |
| Finding 5: title bob | The orbit target rode the ground under it every frame, overriding the drift's eased height; `ViewCamera.TargetRidesGround`, off for the drift | PlayMode test over bumpy hills, before/after title movie |
| Finding 6: tree flicker | Capture the title held still (`-titlehold`) and drifting on the main PC, measure what changes between frames, then fix or explain | Captures, a test where it fits |
| `acquire` on USGS 5xx | `NetworkFailure.RetryAsync`: wait 60 s on a busy service, 15 s on a lost connection (the game's waits), until Ctrl+C | Core tests with a fake delay |
| Photo bar by keys | `GameAction.PhotoBarKeys` (Tab, rebindable); `PhotoBar.TakeKeyboard`/`ReleaseKeyboard`; Esc releases before it leaves photo mode | PlayMode test |
| `gpu.ps1` Ctrl+C | A helper attaches to the run's own console and sends a real Ctrl+C | `tools/gpu/test-gpu.ps1` (4 checks) |
| Live window switch, 15 s keep/revert | `-displaycheck <folder>`: the Display page's own buttons, Keep, Go back and both time-outs, then the window as it started | report.txt and captures on the main PC; demo 60 |

No format or cache-version change.
