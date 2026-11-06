# Phase 3 · 13 Exit review: drawing on the mountain

**Audience:** the project owner. **Date:** 2026-11-05. **Status:** draft, for your review (gate ⛳). **Branch:**
`docs/p3-13-exit-review`, from main at eff5a0e9. **Spec:** [Phase 3 plan row 13](phase2-14-phase3-plan.md#3-tasks),
against the exit criteria in [§4](phase2-14-phase3-plan.md#4-exit-criteria) (from
[0.6 §4](phase0-0.6-milestones.md#4-phase-3-drawing-on-the-mountain-iteration-2), plus E9). **Next:** the detailed
Phase 4 plan, [phase3-13-phase4-plan.md](phase3-13-phase4-plan.md), written in its own thread and merged into this
branch.

**Result:**
- **Two of the five criteria are met** (1, drawn, saved and undone; 3, frame budgets with a drawn resort). The fifth,
  velocity and the Phase 4 plan, is met when the Phase 4 plan joins this branch.
- **Two are put to you as decisions:**
  - Criterion 2: the edit budgets. A 300 m test edit and a 1,000 ft trail pass. The 1 km trail misses narrowly, and
    grading a built run misses (DB8, **E17**).
  - Criterion 4: the no-help tester. Friends played v0.3.0 and liked it, with no reports; what each step gave isn't
    recorded (**E19**).
- **Phase 3 took 20 days** to v0.3.1 and main at eff5a0e9, against 17.5 task-weeks and a 4–6 week forecast. It
  shipped v0.3.0 and v0.3.1. v0.3.2 is held for surface lifts (**E23**).
- **The scope grew mid-phase,** in four parts:
  - the lift overhaul and five new lift families;
  - lift costs;
  - the terrain brush;
  - a download bug bash.

## 1. Exit criteria

The tests were re-run today:
- **Repository checks:** passed.
- **EditMode:** 2,307 passed, 0 failed, 933 skipped (the lift-asset checks that don't apply to a kind). Unity batch
  mode on c3359cac, the PR #129 head whose tree is main's eff5a0e9; the XML was checked fresh.
- **`dotnet test tools/domain-tests/Tests` on main:** see the line below the table.
- **Not re-run today:** the benchmarks. No rendering change has landed since the pins, except the glass shader
  define (#129), which only makes lift glass see-through.

| # | Criterion (§4) | Evidence | Verdict |
|---|---|---|---|
| 1 | A resort with trails and lifts can be drawn, graded, saved, reloaded and undone | Engine-free design document with typed commands and 100-step undo; round-trip and golden-bytes property tests (P3-03, #83). Atomic save, autosave and Continue (P3-04, #86), frozen with a fixture per version, v1 through v10, and every migration tested. Trails painted, graded, rated and edited (P3-08, #93). Fixed-grip and detachable lifts with automatic or by-hand towers (P3-09, #91–#97). The network and sections (P3-10, #103–#108). The terrain brush (P3-15, #102, #110–#114). All of it on the drawn-resort benchmark design (P3-11, #115), and by testers in v0.3.0 | **Met** |
| 2 | Grading preview ≤100 ms; commit ≤250 ms (reference PC, p95) | P3-05's 300 m graded test edit passes. With the whole drawn resort loaded ([edit-timing.md](../perf/edit-timing.md), 0b6a5bf3, five rounds), p95 preview / commit: **1,000 ft trail 65 / 141 ms (pass)**; **1 km trail 139 / 259 ms** (39 and 9 ms over); **grading a built run 1023 / 383 ms** (miss). Down from 0.2–2.3 s before the P3-11 fix. Recorded under DB8; the owner accepted the result on 2026-10-29 and shipped v0.3.0 with it (TR5) | **Partly met: your decision (E17)** |
| 3 | Performance budgets still met with a drawn resort shown | P3-11 (#115): High 8.76 ms and Medium p95 within budget with the drawn resort, +0.20 ms and +0.12 ms over the bare path, 0 B garbage in the Development player. The 40-lift baseline was re-pinned at 8.34 ms (#122, BB8). The budget is 20 ms | **Met** |
| 4 | A tester draws, saves and reloads a small resort, and downloads, explores and photographs a mountain, without help | v0.3.0 went to friends as a full GitHub Release with the tester sheet (TR1–TR3). They liked it and sent nothing: no Send feedback reports, and **no GitHub issues** at all (checked today). The owner's own hands-on (TR4) was skipped. Which of the six steps each tester finished isn't recorded, and the v0.3.0 laptop offline check wasn't recorded either. The one real-world finding came from your screenshot, the Overpass hang (BB1), fixed in v0.3.1 | **Partly met: your decision (E19)** |
| 5 | Velocity is recorded, and the Phase 4 detailed plan is written | §2 below; the Phase 4 plan is in its own thread, joining this branch before the PR | **Met once the plan lands (E20)** |

`dotnet test` on main eff5a0e9: _pending (filled in before the PR)_.

### Known misses and open items brought to this gate

| Item | Where it stands | Proposed decision |
|---|---|---|
| **P3-11 DB8:** the 1 km trail and the grade timing | Above, criterion 2. The rest of the cost is the trail's own grade with a lift re-check (1 km preview), the ground pass (1 km commit), and regrading the runs below a graded run | E17 |
| **Low-preset tree flicker** | Diagnosed in P3-01: Low has no MSAA, so no alpha-to-coverage. Moved to Later with your OK; offered again in the bug bash and not picked. The laptop retest is still pending | E21 |
| **The quit crash (BB7)** | Avoided, not root-fixed (#122). The fault is in the bundled D3D12 runtime as Unity tears the device down. Moving `_AccentColor` to the end of the material block stopped it: 26 of 26 quits were clean. A future shader or material change could set it off again; `demo.bat` 77 and the benchmark's exit code show it at once. Lift Lab players still crash on quit now and then | E22 |
| **The P3-12 tester round** | Open since 2026-10-30: the findings table is empty and the close-out wasn't written | E19 |

## 2. Velocity

The Phase 3 plan was approved with #80 (`unity-m2`) on 2026-10-16. v0.3.0 shipped on 2026-10-29, v0.3.1 on
2026-11-02, and main reached eff5a0e9 on 2026-11-05. **Phase 3 took 14 days to the tester build and 20 days to
today,** against 17.5 task-weeks and a **4–6 week forecast** (E4). The planned tasks ran about **9× faster** than
their estimates, the same ratio as Phases 1 and 2. The calendar landed under the forecast's low end, even with the
added work.

There were 505 commits (392 not merges) and 49 PRs (#81–#129). Merge dates are local time (Pacific).

| # | Task | Est. | PRs | Merged |
|---|---|---|---|---|
| 01 | Carry-over polish and baselines | 0.5 wk | #81, #84 | 10-17 |
| 02 ⛳ | Design rules | 1 wk | #82 | 10-17 |
| 03 | Design document, commands and undo | 1.5 wk | #83 | 10-17 |
| 04 | Save file | 1 wk | #86 | 10-17 |
| 05 | Terrain edit pipeline | 2 wk | #85, #89 | 10-17 |
| 06 | Toolbox and placement | 1 wk | #87 (mockup), #88 | 10-17 |
| 07 ⛳ | Style tile | 1 wk | #90 | 10-19 |
| 08 | Trails | 2.5 wk | #93, #95 | 10-20 – 10-21 |
| 09 | Lifts (fixed grip and the D6 detachable) | 2.5 wk | #91, #92 (mockup), #94, #96, #97, #98 | 10-20 – 10-26 |
| 10 | Paths, junctions and the network | 1.5 wk | #103–#106, #108 | 10-27 – 10-28 |
| 11 | Benchmark with a drawn resort | 0.5 wk | #115 | 10-29 |
| 14 | Problem reports and Send feedback (added 10-18) | 1 wk | #99 | 10-26 |
| 15 | Terrain brush (added 10-19) | 2 wk | #102, #110–#114 | 10-27 – 10-29 |
| 12 ⛳ | Tester build (`v0.3.0`) | 1 wk | #116 | 10-29 |
| 13 ⛳ | Phase 3 exit (and v0.3.2) | 0.5 wk | #129, this PR | — |

Tasks 01–06 merged the day after the plan because their threads ran in parallel while Phase 2 closed. The plan's
"task-weeks" are effort, not calendar.

**Scope added mid-phase**, beyond the plan's 17.5 task-weeks:
- **Detachables** (planned as Later in E13): the Sessellift D6 six-pack (#96, #97) and the lift polish (#98).
- **The lift overhaul** (#120): tower numbers, dragging towers on the profile, Auto-place, fewer and taller towers,
  lift-line clearing, and snow in clearings.
- **Doubles and triples** (#124): the Sessellift FGD-2 and FGT-3, the Monta FG2 and FG3, and the **Sessellift D4**
  detachable quad.
- **Drive at either end** (#127): every fixed-grip head, an automatic 4/6/8-sheave top row, the **Sessellift Stern**
  quad, triple and double, and save v10.
- **Lift costs** (#128): a price for every chairlift from its design.
- **Surface-lift models:** the Monta T-bar (on main since #127) and the Glidepath carpet (#129). Wiring them into the
  game is in progress for v0.3.2.
- **Brush perf** (#110–#114): the 400 ft drag from 34 ms to 10 ms a frame, trees and towers following the brush,
  and Clear trees (save v9).
- **The P3-16 bug bash**, in two parts:
  - for v0.3.1: the Overpass hang and its send-back (#117, #119), and spinning bullwheels (#118);
  - for v0.3.2: the quit crash avoided and the lifts40 re-pin (#122), the held-up download alerts (#123, #125), and
    Get water and roads (#126).
- **Engineering:** the player-first README (#100), the tower planner's search (#101), test and CI reliability
  (#107), and the GPU self-test (#109).

**What went well:**
- **Lift models in their own threads.** The Blender gates (photos, overlays, fit) ran beside the code threads, and
  each model family merged once approved. Five families arrived in under three weeks.
- **Frozen formats held.** The save went from v1 to v10 in 20 days, each bump with a migration, a fixture and goldens,
  and every older save still opens.
- **Mockup first, again.** The construction tools, the brush pane and the Send feedback dialog were approved as HTML
  before porting.
- **The tester build shipped as a real Release.** The one real-world bug, the Overpass hang, came back through it
  and was fixed in v0.3.1.

**What cost time:**
- **Edit budgets with a whole resort loaded:** P3-11's first timing missed by 5–20×. The fix (#115) needed a
  measured, step-by-step rework, and two budgets still miss (E17).
- **A silent shader failure:** from 2026-10-21 to 2026-11-05, every lift's glass was drawn opaque because a define
  had no value. Only the build log said so. A test now compiles the glass variants (#129).
- **The shared GPU lanes:** Blender batches held both render slots during the v0.3.1 build, and its smoke test
  timed out at 600 s. Until the lanes get a release priority, the Master Planner pauses the batches before a release.
- **Send-backs:** the first Overpass fix (#117) didn't cover an unroutable Overpass. The v0.3.1 thread caught it in
  hands-on testing, and #119 fixed it.

**For Phase 4's estimate:** simulation is new modelling work: snow, weather and guests. It has look and feel gates,
and agents at scale bring performance risk. The plan keeps the 9× ratio for build work and pads the calendar for the
gates, as E13 did.

## 3. What carries over

| Item | From | Where it goes |
|---|---|---|
| **Surface lifts in the game:** T-bar/J-bar (PR A, demo 81) and the magic carpet (PR B, demo 82), being wired by the Lifts thread; save v11 if needed | The lift overhaul | **v0.3.2** (E23) |
| **The rope tow:** not modelled | Surface lifts | Later; model gates first (E23) |
| **Soil works in the lift cost:** earthwork and clearing as a `LiftCost` line | #128 | Phase 4 economy, or Later (E24) |
| **A faster grader:** the 1 km trail and grading a run (DB8) | P3-11 | E17 |
| **The trail look's 2048 m window:** covers part of a big resort (6 of 10 benchmark spots) | P3-11, TL24 | Later (E24) |
| **Low-preset tree flicker:** TAA option or MSAA on Low, plus the laptop retest | P3-01 | E21 |
| **D3D12 quit fault:** a root fix, and the Lift Lab crashing on quit | BB7 | Later (E22) |
| **Brush polish:** drag garbage about 170 KB a frame (target 64 KB); Confirm 255–265 ms against 250; Confirm/Revert shown twice in the close ask | #110, #111 | Later (E24) |
| **Remove trail removes only the clicked run;** the NT37/NT38/NT40 session lists; NT34 whole-length rating | P3-10 #106 | Later (E24) |
| **NT33:** a removed trail keeps its earthwork, and trees regrow over time | P3-10 | Phase 4 (simulation) |
| **NT21:** a cost or unlock for base Areas, and a skills tree | P3-10 | The economy phase |
| **BB3 chair swing:** held by you ("doesn't add much"); code on `held/p3-16-chair-swing` | P3-16 | Later |
| **The picker says "New Mountain"** where your naming is "New Area" | Phase 2 task 14 | Later (E24) |
| **Release priority in the GPU lanes,** or a longer smoke wait | v0.3.1 | Later (E24) |
| **Timing tests** (TW10, TW12) flaky under CPU load; the Overpass header wait never tuned | P3-16 | Later |
| **Before the first public release:** the RTX 2060-class check (E8), code signing (E11), the "Alpine Labs" trademark search (E12) | Phase 2 | Unchanged |
| **The forest and trees backlog:** seasons, the next species, height-only regional calibration, broadleaf height spread, the beech and red oak LOD2 strips, western_hemlock's materials, back-face tint | Phases 1–2 | Unchanged (Later) |
| **Engineering backlog:** pooling the download pipeline's buffers; a Unity compile step in CI; backdrop blur; picker nice-to-haves | Phase 2 | Unchanged (Later) |
| **Owner action:** turn on the project board's Auto-add workflow (GitHub web UI) | P3-14 | You |

## 4. Decisions for this gate

These are proposed, each with its recommendation; your answers replace them. E16 continues the E-series from the
Phase 2 exit (E7–E15).

| ID | Decision (proposed) |
|---|---|
| E16 | **Exit 1 is met** by the design document's property and golden tests, the save fixtures v1–v10 with their migrations, the trails, lifts, network and brush tests, and the drawn-resort benchmark design |
| E17 | **Exit 2 is accepted as partly met** under DB8. The 300 m test edit and the 1,000 ft trail pass; the 1 km trail (139 / 259 ms) and grading a built run (1023 / 383 ms) miss. **A faster grader** (incremental regrade of the runs below, and the trail's own grade without the lift re-check) is offered to the Phase 4 plan as a candidate task, or Later; you choose |
| E18 | **Exit 3 is met** (P3-11: High 8.76 ms p95 with the drawn resort; 40 lifts 8.34 ms; budget 20 ms) |
| E19 | **Exit 4 is accepted as partly met.** The friends played v0.3.0, liked it, and filed no reports. Which steps they finished isn't recorded, and the owner's hands-on was skipped. **The P3-12 round closes with no tester findings;** BB1 (your screenshot) stands as its one real-world finding. A no-help session with written steps returns at the next tester build, if you want one |
| E20 | **Exit 5 is met** with §2 and the Phase 4 plan on this branch |
| E21 | **Low-preset tree flicker stays Later** (P3-01). It returns with a quality pass or a minimum-spec check (E8) |
| E22 | **The quit crash stays avoided, not root-fixed.** `demo.bat` 77 and the benchmark's exit code watch for it, and new material properties go last in the block. A root fix, and the Lift Lab's crash on quit, are Later |
| E23 | **v0.3.2 waits for surface lifts in the game** (owner, 2026-11-05): the T-bar/J-bar and the magic carpet from the Lifts thread, with everything else since v0.3.1. The rope tow comes later, after its model gates |
| E24 | **The carry-over in §3 goes to the Phase 4 plan's Later list,** except where a row says otherwise; soil works in the lift cost joins the economy work |
| E25 | **Milestone:** the merge commit of this PR is tagged `unity-m3`, and `archive/unity` is fast-forwarded to it (fast-forward only, after checking it is an ancestor; never forced) |
