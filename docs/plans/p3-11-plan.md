# Phase 3 · 11 Benchmark with a drawn resort: plan

**Task:** row 11 of the [Phase 3 plan](phase2-14-phase3-plan.md): a committed test design on Jackson Hole 5 km (about
20 trails, 6 lifts), the benchmark path extended over it, and edit-timing runs (preview, commit, undo). Acceptance:
frame budgets met at 1080p High and Medium with the design shown; preview ≤100 ms and commit ≤250 ms at p95; results in
`docs/perf/`. It is the evidence for exit criteria 2 and 3.

**Status:** plan posted 2026-10-26, held until task 10 was complete (2026-10-27), re-posted for save v8 and approved
by the owner on 2026-10-28 with every recommendation (DB1–DB8 in [phase0-decisions](phase0-decisions.md)).
Branch `feature/p3-11-drawn-benchmark`, `demo.bat` 72.

## 1. The test design (DB1–DB4)

- **Where:** six lift pods on the Jackson Hole 5 km mountain face, the valley floor to the south-east: each lift up
  the fall line, about three runs off its top down to its bottom, and two cat tracks joining pods.
- **What:** two Sessellift FGQ-4, two Monta FG4, two Sessellift D6; ten graded and ten natural runs, 60–200 ft wide,
  two with an island of trees; the two cat tracks are graded, 40 ft.
- **How it's authored:** the lines were planned once over the package's slope map (a least-cost route per run in its
  difficulty band, between the lift ends) and frozen in `Domain/Design/DrawnResort.cs` as local metres: the lift
  ends and each run's centreline. Nothing else is stored.
- **How it's built:** on every run, by the design's commands as the tools commit them: AddLift with LiftBuild's
  towers and levels (the review's level hints taken as a player would, LT34) and AddTrail with one painted stroke
  (`TrailShape.FromStrokes`), each with the earthwork fold's ground (TL3). No save fixture, no format change, no LFS,
  and it follows every rule change (fixes-must-be-general).
- **Tests:** `Tests/Core/DrawnResortTests`: the mix (DB3, DB4) and paintable lines in CI; on the local 5 km
  package (`MP_JH5_PACKAGE`, E2) all 26 accepted and two builds byte-identical.

## 2. The benchmark path (DB5, DB7)

- `-drawnresort` builds the resort before the benchmark starts and adds six legs, all worked out from its lines: the
  resort from below, the first lift's bottom station, along the first graded run, beside the second lift's line, the
  first cat track's head, the third lift's top.
- `-drawnpathonly` flies the same eleven legs over the bare mountain: the design's cost on one build.
- The eight existing baselines are untouched; the drawn runs get their own files (High, Medium, High Development).
- The network overlay, labels and stations (task 10) are on main, so they're in the measured view.

## 3. Edit timing (DB6)

`-designbench <out.json>` (with the resort built) times, at the ten `-trailbench` spots and two rounds plus a warm-up:

| Edit | Gated |
|---|---|
| New graded trail, 1,000 ft and 1 km at 100 ft, among the resort's | preview ≤100 ms, commit ≤250 ms at p95 |
| Grading a built natural run of the resort | same |
| Removing a run; a new fixed-grip quad 600 m up the fall line | reported only |
| Undo of each | reported only |

## 4. Results

`docs/perf/`: the drawn High, Medium and Development reports, the edit report, and a README section with the commands,
the numbers, and the design's cost against the bare path. A missed budget goes to the owner with its numbers (DB8).
Demo 72 (the drawn resort in the game), 72b (the frame benchmark and compare), 72c (the edit timing).

## 5. The edit-timing fix (DB8, approved 2026-10-29)

The first timing passed the frame budgets (High 9.1 ms, Medium 5.5 ms p95) but missed the edit budgets 5–20×: previews
0.2–2.3 s, commits and undo 1.7–2.0 s at p95. The causes, from the code and the run's log:

- **Commit and undo:** after every change the trail look (`TrailSurfaceMap`) was redrawn whole, with five full-size
  arrays per trail (about 3.4 GB a commit), every trail was rated again, and new or edited trails worked out their
  footprints a second time (the history applies the command again, so a reference check never matched). The ground
  pass itself is 100–230 ms. The lift models were already kept.
- **Preview:** the design's earthwork graded far more than an edit could reach: one growing box of change, a removed
  piece's box regrading the pieces before it, lifts tested by the box round their whole line, and a memory that undo
  and redo never moved, which also let `Foreign` lay a deleted-then-restored lift's old trench back over new work.

The fix, each step measured in the player before and after, with byte-identity tests on synthetic ground in CI:

1. **Measure:** split timings for the built work, the ground pass and the network; `-designbench`'s commit timer
   ends when the ground and built work are in, with the settled time and the slowest frame reported; five rounds.
2. **The earthwork's memory follows undo and redo,** keyed by the history's own edits, taken only from a matching
   accept, re-learnt in the background when nothing matches; reads never wait behind a fold.
3. **A build matches its trail by value** (and a split part's slice of line is never taken for its own).
4. **The trail look and ratings update only where something changed**, over the same window as before.
5. **The fold regrades only what can have changed:** tiles, not boxes; a removed piece counts from its own place in
   the order; a stroke follows its lift.
6. **Gate:** a budget still missed comes back to the owner with its split (the ground pass, a lift's regrade, the
   run's own grade) before any deeper change.

Not fixed here: the trail look's 2048 m window leaves about the south-east half of the resort without it; recorded
as a follow-up.

### Results (2026-10-29, 0b6a5bf3, with brush PR C #114)

Frames still pass: the resort costs +0.20 ms at High (8.76 against 8.56 ms p95 on the same path) and +0.12 ms at
Medium; the Development player sees 0 B garbage. Edits, five rounds, p95 preview / commit ms
([docs/perf/edit-timing.md](../perf/edit-timing.md)):

| Edit | Before | After | Budget |
|---|---|---|---|
| New 1,000 ft trail | 1428 / 1685 | 65 / 141 | pass |
| New 1 km trail | 235 / 1747 | 139 / 259 | miss (39 / 9 ms over) |
| Grading a run | 2347 / 2049 | 1023 / 383 | miss |
| Removing a run (reported) | 2148 / 1738 | 911 / 288 | — |
| New lift (reported) | 1768 / 1694 | 1497 / 179 | — |
| Undo (reported) | 1916 | 312 | — |

Every probe preview equals a fold from scratch. On a second site (Olympic Valley 4 km) `-trailbench` passes and its
1 km commit falls from 179 to 105 ms against main; the brush is unchanged within noise on both sites. What still
misses is the trail's own grade with a lift re-check (1 km preview), the ground pass (1 km commit) and the runs below
a graded run (grading), so it goes to the owner under DB8.

