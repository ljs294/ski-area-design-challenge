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
