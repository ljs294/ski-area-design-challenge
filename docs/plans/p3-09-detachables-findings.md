# P3-09 · Detachable lifts: owner findings (PR C)

The owner's findings from playing the Sessellift D6 build on `feature/p3-09-detachables` (demo 68), logged as given, one
at a time. No fixes until the plan that follows the list is agreed. The plan this build carries out is
[p3-09-detachables-plan.md](p3-09-detachables-plan.md); its rulings are LT41.

Screenshots are kept locally under `test-results/p3-09-detachables/` (not in git).

| # | Finding | Where | Notes |
| --- | --- | --- | --- |
| D1 | **The game lags badly when zoomed far out.** Close in it's better, but far out it's a serious problem. | The whole view, zoomed far out (demo 68 build, D6 branch). | Not yet measured. To find: whether it's new on this branch (the D6's 20k-triangle terminals and wide heads, its glass, the chairs' track) or there on #94 and main too, and which lifts and how many were built. The fixed benchmark path doesn't fly that high. Owner: never seen lag before, even with many lifts, so it's new in this build. This build is the first demo 68 to carry main's latest work too: P3-08's PR #95 (run lines with per-point widths, labels hidden by the ground) and the D6 models' PR #96. So the cause may lie there as well as in PR C. The lift map lines' per-frame update was checked and is cheap. To measure: a high, far view on main, on #94 and on this branch, same lifts, with the profiler. Owner (follow-up): the lag stays with the camera still, and the clock visibly lags too. So it's a steady per-frame cost, not the zoom: that rules out the run lines' width updates. Owner suspects the D6 work, or the new glass from the lift-models pass (PR #96, which also changed the glass for the existing lifts). |
| D2 | **Tower placing takes forever.** The review sits on "Towers placing…". | New lift's review, a D6 7,227 ft long (6,926 ft across, 2,067 ft rise, 30%). `D2.png` | Not yet measured. Suspects: a long line, and LT34's search. Where the line placed at your levels isn't within the rules, the build now runs up to seven more full placements, only to work out the "raise it N ft" suggestion, then the hint searches on top. The D6's heavier rope and wider kit may add to it. |

## Known before review

These were found while building PR C; they're listed so the review can confirm or override them.

- **Accent colour not visible yet:** the shader takes an accent where a model's livery mask is 2, but no model marks
  accent parts yet (a model-pipeline follow-up).
- **Steep ground at a D6 bottom terminal:** the rope leaves the station level, so on a steep slope the first tower takes
  the whole climb and can come out too sharp (amber, drawn as the kit's b10).
- **The 40-lift benchmark** with a third D6s and a third Montas: frame p95 8.54 ms (budget 20), 9% over the quad-only
  baseline, chairs 0.63 ms a frame (was 0.44); the lifts baseline needs re-pinning.
- **A crash on quit** (access violation, 0xC0000005) after the benchmark had written its results; not yet traced.
