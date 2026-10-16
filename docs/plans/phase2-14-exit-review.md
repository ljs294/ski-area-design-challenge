# Phase 2 · 14 Exit review: iteration 1 complete

**Audience:** the project owner. **Date:** 2026-10-16. **Status:** for your review (gate ⛳). **Branch:**
`docs/p2-14-exit-review`, from main at 679b4de. **Spec:** [Phase 2 plan row 14](phase1-16-phase2-plan.md#3-tasks),
against the exit criteria in [0.6 §3](phase0-0.6-milestones.md#3-phase-2-iteration-1-complete). **Next:** the detailed
Phase 3 plan, [phase2-14-phase3-plan.md](phase2-14-phase3-plan.md).

**Result:**
- **Two of the four criteria are met** (1 keyboard and scale, 3 clean install offline).
- **Two are recorded as your decisions:** criterion 2 (real RTX 2060-class hardware) was skipped with task 11
  (**E8**), and criterion 4 (a tester without help) is partly met, with the rest skipped at TB5 (**E9**).
- **The carried-over seam check (E1) is met:** S1M meets the 3DEP lidar at Palisades Tahoe with no step.
- **Phase 2 took 10 days** against an estimate of 14 task-weeks and a 3–5 week forecast. It shipped the first tester
  build, `v0.2.0`.

## 1. Exit criteria

The tests were re-run today on this branch (main at 679b4de plus docs).

| # | Criterion (0.6 §3) | Evidence | Re-checked today | Verdict |
|---|---|---|---|---|
| 1 | Every flow in 0.4 §3 works with keyboard only, at 50–150% UI scale | `UiKeyboardTests.EveryScreenWorksByKeyboardAlone` at 50, 100 and 150% walks the title (S1), Load and Manage Areas (S2) with the delete confirm (S11), the quality card (S5), Settings (S8, including the theme and scale by keyboard), Credits (S9) and the HUD (S6/S7), asserting that the focused control is wholly on screen. `NewerAreasWorkByKeyboardAlone` (50/100/150%) and `FirstLaunchOpensTheBuiltInDemoByKeyboard` cover the greyed rows and the first launch; `SitePickerTests` places the picker's square by keyboard (S3). Photo mode (S10) has keys for the bar, time, date, capture and exit, and Tab reaches its buttons. Every key is rebindable (ST4) | Unity batchmode PlayMode (render lane): **36/36 passed**, including `EveryScreenWorksByKeyboardAlone` and `NewerAreasWorkByKeyboardAlone` at 50, 100 and 150%, and `FirstLaunchOpensTheBuiltInDemoByKeyboard` | **Met.** One rough edge: in photo mode the arrow keys move the camera, so they don't walk the photo bar; Tab does (Phase 3 task 01) |
| 2 | Minimum spec at 30 FPS or better at 1080p Medium, verified on real hardware | Task 11 was skipped by you on 2026-10-12 ("not too worried about performance at this point"). Phase 1's stand-in stays: Medium p95 4.7 ms on the reference PC (criterion 5 of Phase 1). The 2016 laptop (Quadro) opened the demo and downloaded an area; its frame rate wasn't measured | — | **Not met: your decision (E8).** Verify on RTX 2060-class hardware before the first public release |
| 3 | A clean install on a second PC opens the demo mountain offline | [phase2-tester-findings.md](phase2-tester-findings.md): the v0.2.0 zip on your 2016 ThinkPad P40 Yoga, Wi-Fi off, reached the title and opened Jackson Hole. Windows blocked the unsigned exe until the zip was Unblocked (finding 2; README now says so) | Not re-run (a second PC) | **Met** |
| 4 | A tester can download, explore and photograph a mountain without help | Same file: you downloaded and explored on the laptop. The photo test and your brother's no-help session were skipped (TB5) | — | **Partly met: your decision (E9).** Phase 3's tester build (task 12) runs a full no-help session, photo included |
| E1 | (Carried from Phase 1) the data seam on real data shows no visible step | [p2-12-seam-check.md](p2-12-seam-check.md): Palisades Tahoe 5 km, S1M to the west, 3DEP 1 m lidar to the east; step ≤9 cm in the core, the datasets agree to 6 mm (mean) | Not re-run | **Met** |

Also re-run today:
- Repository checks passed.
- `dotnet test tools/domain-tests/Tests` with LFS checked out: **348/348 passed, 0 skipped** (the goldens included).
- Unity batchmode EditMode: **679 passed, 0 failed, 37 skipped** (the lift-asset checks that don't apply to a kind, as in Phase 1). The first run failed once, `TheTitlePictureLoadsAtFullSize`, because this worktree still held LFS pointers; after `git lfs pull` it passed.

**Doc fix:** 0.3 §4.2 and §12 called our fallback blend "slope-weighted"; the code (`HeightAssembler.Blend`) is
distance-weighted (a smoothstep over the distance from the gap). This branch corrects both. 0.3's line about USGS's own
void backfill is unchanged.

## 2. Velocity

The Phase 2 plan was approved with #63 on 2026-10-06, and the last task (#79) merged on 2026-10-16. **Phase 2 took
10 calendar days**, against 14 task-weeks estimated (12.5 for the tasks that ran, after skipping 11 and 13) and a
**3–5 week forecast** (E4). That is about 9× faster than the task-week estimate, the same ratio as Phase 1, and at
or under the low end of the calendar forecast: the waits the forecast padded for (hardware, testers) were skipped.

125 commits, 16 PRs (#64–#79). Merge dates are local time (Pacific).

| # | Task | Est. | PRs | Merged |
|---|---|---|---|---|
| 01 | UI foundation | 1 wk | #66 | 10-06 |
| 02 | HUD (Trailhead) | 1.5 wk | #68 | 10-06 |
| 03 | Title over the live demo; demo bundled | 1 wk | #73 | 10-08 |
| 03.1 | Loading screen and the Twin Peaks brand (added) | — | #77 | 10-12 |
| 04 | Library and cache housekeeping | 1 wk | #70 | 10-06 |
| 05 | Settings | 1.5 wk | #72 | 10-08 |
| 06 | Background downloads, dialogs, offline | 1 wk | #75 | 10-10 |
| 07 | Photo mode and credits | 1 wk | #74 | 10-11 |
| 08 | Formats frozen (+ follow-up) | 1 wk | #65, #67 | 10-06 |
| 09 | Forest look | 1 wk | #71 | 10-08 |
| 10 | Build and release (`v0.2.0`) | 1 wk | #78 | 10-12 |
| 11 | Real hardware | 0.5 wk | — | **skipped** (owner, 10-12) |
| 12 ⛳ | Tester build | 1 wk | #79 | 10-16 |
| 13 | NAIP imagery | 1 wk | — | **skipped** (owner, 10-12) |
| 14 ⛳ | Phase 2 exit | 0.5 wk | this PR | — |

**Work added beyond the plan:** the loading screen and brand (#77); the UI mockup's weather and layers (#64); the
benchmark's one-run window (`-benchres`, #69); the shared GPU lanes (`tools/gpu/gpu.ps1`, #76).

**What went well:**
- Up to four task threads at once, with the coordinator sequencing merges and format changes.
- **Mockup first:** the HUD, settings, photo bar and credits were approved as HTML, then ported and checked with the
  parity tool. Few look revisions after porting.
- **Frozen formats early** (task 08): every later task changed or added files under the migration rules (view.json
  v2 in task 04) without breaking a Phase 1 library.
- **The GPU lanes** replaced message hand-offs and stopped captures and benchmarks clashing.
- Every budget still holds with a wide margin; the HUD measured free (p95 10.92 → 10.17 ms at 2560×1080).

**What cost time:**
- **Shared files:** tasks 03, 05, 06 and 07 all edited FlowScreens, Flow.uxml and AppFlow; partial-class splits
  (Cover, Credits) came after the first conflicts.
- **Remote reviews:** while you are remote, Windows reports a 640×480 desktop, so captures and benchmarks taken then
  are invalid. Threads now check the desktop size first (`-RequireDesktop`).
- **Unity licensing:** exit code 198 (Hub not running or signed in) and stale test-result files looked like passes
  once. Threads now check the XML's timestamp.
- **Stale perf baselines:** since task 02 the benchmark runs at 10:30, so main no longer matches the stored
  baselines. Perf PRs compared against their own before-run instead. Phase 3 re-pins them (E10).

**For Phase 3's estimate:** drawing tools are new interaction and rules work, not ports of a finished design, and
each needs your look and feel approval. The plan keeps the 9× ratio for build work but pads the forecast for the
rule and look gates (E13).

## 3. Decisions for this gate

| ID | Decision |
|---|---|
| E7 | **Exit 1 is met** by the keyboard PlayMode tests at 50, 100 and 150%, plus the picker's and photo mode's keys. Photo mode's arrow keys move the camera, not the bar's focus; Tab reaches the bar (fixed in Phase 3 task 01) |
| E8 | **Exit 2 (real minimum-spec hardware) is waived for iteration 1** (task 11 skipped by the owner, 2026-10-12). Phase 1's stand-in (Medium p95 4.7 ms on the reference PC) stands. An RTX 2060-class check is required **before the first public release**, not in Phase 3 |
| E9 | **Exit 4 is accepted as partly met** (TB5: download and explore by the owner on the laptop; no photo test, no no-help tester). **Phase 3's tester build (task 12) runs the full no-help session, photo included** |
| E10 | **Perf baselines are re-pinned at the start of Phase 3** (task 01), on main at the 10:30 benchmark time; perf PRs compare against them again |
| E11 | **The exe stays unsigned** until a public release is planned; testers Unblock the zip (README). Code signing costs money and is decided then |
| E12 | **The "Alpine Labs" trademark search** (professional; may cost money) happens before the first public release, not in Phase 3 |
| E13 | **Phase 3 scope** is 0.6 §4 (drawing on the mountain: design document, undo, saves, trails, lifts, paths, network) plus the Phase 2 carry-over polish. Only fixed-grip chairlifts (the existing Sessellift FGQ-4 and Monta FG4 assets); detachables, ponds, dams, snowmaking, buildings, seasons and new species are later. Estimates as E4 |
| E14 | **Milestone:** the merge commit of this PR is tagged `unity-m2`, and `archive/unity` is fast-forwarded to it (fast-forward only, after checking it is an ancestor) |
