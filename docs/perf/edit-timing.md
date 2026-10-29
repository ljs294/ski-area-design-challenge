# Edit timing with the drawn resort (Phase 3 task 11)

How long a design edit takes with a whole resort loaded: the drawn resort on Jackson Hole 5 km (20 trails, 2 cat
tracks, 6 lifts, built by command as it opens; DB1–DB4). The budgets are task 08's (CF-09): **preview ≤100 ms and
commit ≤250 ms at p95** for a new graded trail and for grading a built run; removing a run, a new lift and undo are
reported, not gated (DB6). The report is `jackson-hole-5km-design-edits.json` (schema
`mountain-planner-design-edit-benchmark/2`).

```
Builds\Windows\SkiAreaDesignChallenge.exe -benchres 1920x1080 -quality high -drawnresort -designbench <out.json> -benchrounds 5
```

`demo.bat` 72c runs it in the measure lane. Each edit is timed at the ten `-trailbench` spots, five rounds after a
warm-up at every spot: the preview is the tool's whole analysis (apply, fold, contours, rating); the commit runs from
the submit until the ground and the built work are in and a frame has drawn. The settled time and the slowest frame
during each commit are reported beside it.

## Results (release player, High, 1920×1080, RTX 3060 Ti, Ryzen 5 5600X)

| Edit (p95 ms, preview / commit) | First timing (e0750447) | After the fix (0b6a5bf3) | Budget |
| --- | --- | --- | --- |
| New graded trail, 1,000 ft | 1428 / 1685 | **65 / 141** | pass |
| New graded trail, 1 km | 235 / 1747 | **139 / 259** | miss: preview 39 ms, commit 9 ms over |
| Grading a built natural run | 2347 / 2049 | **1023 / 383** | miss |
| Removing a run (reported) | 2148 / 1738 | 911 / 288 | — |
| A new fixed-grip quad (reported) | 1768 / 1694 | 1497 / 179 | — |
| Undo (reported) | 1916 | 312 | — |

p50 after the fix: 22 / 81, 76 / 140, 432 / 178, 304 / 165, 476 / 126, undo 123. The commit's settled time is p95
353 ms and its slowest frame p95 60 ms. All 50 previews of the probe (`DrawnResortTests`, `MP_PROBE_SPOTS=10`) equal a
fold from scratch byte for byte. The first timing was two rounds; the second, five.

## What changed (`docs/plans/p3-11-plan.md` §5)

- **The trail look and ratings** update only the blocks a change can reach (`TrailSurface`), and are worked out on a
  worker while the ground lands; only the changed blocks are packed and uploaded.
- **Builds match their trails by value**, so an edited trail's footprint isn't worked out twice.
- **The earthwork's memory follows undo and redo** (it also fixed a deleted-then-restored lift's trench coming back
  over new work), and **the fold regrades by 64 m tile**, not by one growing box; a lift is re-checked only when a
  change lands in the ground its pads, rope and trench read.
- **`TerrainEdits.With` is a sorted merge**; collected geometry and ordinals are cached.
- **The commit's ground pass** no longer waits a frame after every height tile (the brush keeps one tile a frame).
- **Brush PR C** (#114): a brush session's fold is seeded with the one before; it sits on the same tile rule.

## What still misses, and why

- **1 km trail:** the preview is the trail's own grade (40–75 ms) plus a lift re-check (22–36 ms) at 3 of 10 spots;
  the commit is the ground pass (cover and splat for every touched cache tile).
- **Grading a run:** the run's own grade (1.3–1.6 km, est. 150–180 ms) plus the 1–3 runs below it that share its
  tiles. The run alone is over the preview budget; it needs a faster grader, not more caching.
- **Removing a run, a new lift:** the same cascade, below the removed run or the new lift's pads.

These go to the owner (DB8) with this split.

## Other benches on the same build, against main (1e47da3a)

| Bench (p95 ms) | Jackson Hole 2 km, main → branch | Olympic Valley 4 km, main → branch |
| --- | --- | --- |
| `-trailbench` 1 km commit | 213 → 134 | 179 → 105 |
| `-trailbench` mixed with an island, commit | 221 → 111 | 183 → 96 |
| `-trailbench` 1,000 ft preview / commit | 24 / 79 → 22 / 63 | 25 / 85 → 26 / 68 |
| `-brushbench` 60 ft drag frame (two interleaved runs each) | 6.7, 7.0 → 7.1, 7.2 | 18.3, 17.3 → 17.6, 18.1 |
| `-brushbench` on let-go | 155–174 → 157–167 | 165–201 → 156–178 |

The brush is unchanged within run-to-run noise. Its Olympic Valley FAIL (the 60 ft drag's frames) and an access
violation when the brush bench quits (`PendingContourView.Clear` from `MountainViewer.OnDestroy`, after the report is
written) happen on main as well.
