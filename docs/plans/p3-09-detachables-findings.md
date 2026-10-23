# P3-09 · Detachable lifts: owner findings (PR C)

The owner's findings from playing the Sessellift D6 build on `feature/p3-09-detachables` (demo 68), logged as given, one
at a time. No fixes until the plan that follows the list is agreed. The plan this build carries out is
[p3-09-detachables-plan.md](p3-09-detachables-plan.md); its rulings are LT41.

Screenshots are kept locally under `test-results/p3-09-detachables/` (not in git).

| # | Finding | Where | Notes |
| --- | --- | --- | --- |
| D1 | **The game lags badly when zoomed far out.** Close in it's better, but far out it's a serious problem. | The whole view, zoomed far out (demo 68 build, D6 branch). | **Fixed** (measured below: the run names' sight test). To find was: whether it's new on this branch (the D6's 20k-triangle terminals and wide heads, its glass, the chairs' track) or there on #94 and main too, and which lifts and how many were built. The fixed benchmark path doesn't fly that high. Owner: never seen lag before, even with many lifts, so it's new in this build. This build is the first demo 68 to carry main's latest work too: P3-08's PR #95 (run lines with per-point widths, labels hidden by the ground) and the D6 models' PR #96. So the cause may lie there as well as in PR C. The lift map lines' per-frame update was checked and is cheap. To measure: a high, far view on main, on #94 and on this branch, same lifts, with the profiler. Owner (follow-up): the lag stays with the camera still, and the clock visibly lags too. So it's a steady per-frame cost, not the zoom: that rules out the run lines' width updates. Owner suspects the D6 work, or the new glass from the lift-models pass (PR #96, which also changed the glass for the existing lifts). |
| D2 | **Tower placing takes forever.** The review sits on "Towers placing…". | New lift's review, a D6 7,227 ft long (6,926 ft across, 2,067 ft rise, 30%). `D2.png` | **Fixed** (measured below: the same height cache, then the level search). Suspects were: a long line, and LT34's search. Where the line placed at your levels isn't within the rules, the build now runs up to seven more full placements, only to work out the "raise it N ft" suggestion, then the hint searches on top. The D6's heavier rope and wider kit may add to it. |

## Measurements and fixes (2026-10-23)

The plan for D1 and D2 measured first, on the owner's own save (Jackson Hole 5 km, 6 lifts, 6 trails) with a new
`-holdview <out.json>` switch: 600 frames held at a `-view`, once the save and its building work are in, at High,
1920×1080, on the measure lane. "Before" is a player with the label fix switched off; "after" is this branch.

**D1: the run names' sight test.** Owner, 2026-10-23: turning Labels off put the game back to normal. The run names
tested their sight against the trail layer's edited heights, which read the package's height grids from disk a chunk
at a time (6 kept per layer). Every few frames, each name on a long sight line read and inflated chunk after chunk,
with garbage collections, whether the camera moved or not. Fixed:
- every label now tests against the loaded terrain;
- only labels on screen are tested;
- only once the camera has moved (contour labels too).

| View (`-view` x,z,distance,yaw,pitch) | Before: frame p50 / p95 | After | Labels off |
| --- | --- | --- | --- |
| Far, facing the flats (0,0,5500,90,30) | 31 / 61 ms; 600 chunk reads, 360 collections | 5.2 / 6.1 ms; 0, 0 | 5.2 / 5.9 ms |
| Facing the flats (1400,-600,1500,90,15) | **251 / 418 ms**; 6,000 chunk reads, 3,600 collections | 4.4 / 5.4 ms; 0, 0 | 4.3 / 5.2 ms |
| Flats, wider (1200,-800,2500,110,20) | 30 / 34 ms; 450 chunk reads, 270 collections | 4.7 / 5.1 ms; 0, 0 | 4.8 / 6.2 ms |
| Close, the D6's bottom (1000,-860,150,270,20) | 256 / 363 ms; 3,900 chunk reads, 2,337 collections | 12.1 / 12.3 ms; 0, 0 | 12.6 / 12.9 ms |

The further review found nothing else new. With the camera still, the fixed build reads no height chunks, allocates
nothing and collects nothing. At the far view lifts off saves 0.04 ms; at the close view lifts off and trails off are
within noise. The close view's 12 ms is the forest's GPU time (2.6 ms with `-noforest`): there before this branch and
within the 20 ms budget. So the planned chair changes (a shorter far distance, the far levels' own shadow setting)
aren't made: the measurements don't justify them. The bisect and a profiler capture weren't needed: the after build
with labels on matches labels off.

**D2: the long line's review.** The same cache was behind it. A lift line crossing more 256-row bands than the six
kept read every band again on each pass along it. Measured by a new test (`D2_ALongLineBuildsQuickly`) on the committed
Jackson Hole package, read as the game reads it: a 7,888 ft D6 corner to corner.

| Stage | Before | Block cache | Plus the review in two steps |
| --- | --- | --- | --- |
| One tower placement | 10.4 s | 0.17 s | 0.17 s |
| The review's figures | 86 s (one build, with the level search) | 2.2 s | **1.1 s** (the lift as placed) |
| The "raise it N ft" hints | (in the above) | (in the above) | 3.2 s more, filled in when done |

- **The package heights keep blocks, not bands.** A chunk read is cut into 256 × 256 blocks, and the 160 most
  recently used are kept per layer (40 MB a layer; the six bands were 30 MB on a 5 km site). A line uses only the
  blocks it crosses.
- **The review shows the lift as placed first.** Where the line isn't within the rules, LT34's search for the levels
  that would fit runs as a second step and fills in its hints. It gives the same towers, levels and earthwork (tested).
  The review shows "Levels: working out what would fit…" and holds Break ground until then. A line within the rules
  needs no second step.
- The design's earthwork fold was already incremental (TL3), so it's unchanged.
- On made-up 30% ground (no disk) a 7,200 ft D6 was already 0.17 s: the time was the disk reads.

## Known before review

These were found while building PR C; they're listed so the review can confirm or override them.

- **Accent colour not visible yet:** the shader takes an accent where a model's livery mask is 2, but no model marks
  accent parts yet (a model-pipeline follow-up).
- **Steep ground at a D6 bottom terminal:** the rope leaves the station level, so on a steep slope the first tower takes
  the whole climb and can come out too sharp (amber, drawn as the kit's b10).
- **The 40-lift benchmark** with a third D6s and a third Montas: frame p95 8.54 ms (budget 20), 9% over the quad-only
  baseline, chairs 0.63 ms a frame (was 0.44); the lifts baseline needs re-pinning.
- **A crash on quit** (access violation, 0xC0000005) after the benchmark had written its results; not yet traced.
  It also ended about half of the `-holdview` runs, always after their results were written.
