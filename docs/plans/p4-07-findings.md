# Phase 4 · 07 Snow Model (PR A2, the live snow in the game): the owner's findings

**Audience:** the project owner and coding agents.

**Status:** findings round closed (2026-11-15): both findings fixed, the owner said "we are good here".

**Build reviewed:** `feature/p4-07-snow-a2` (uncommitted, player rebuilt 2026-11-15), `demo.bat` 111: the live snow,
the Snow depth and Snow conditions layers, and the 45-day time-lapse.

**Earlier (first demo, 2026-11-15):** wet granular showed on the Snow conditions layer where the ground looked bare.
Fixed at the owner's word ("Yes, fix it"): the layer fades each type to bare by the ground's cover, and the readout says
"Bare · traces of …" under about 3 in. This build carries the fix.

| # | Finding | Where | Fix idea | Status |
|---|---|---|---|---|
| 1 | Lakes always appear frozen despite the weather (owner, 2026-11-15, with a screenshot) | Jackson Hole, Pre-season, early October, weekday, 08:30 AM. The valley floor is green and bare, with no snow anywhere, yet every lake (the large one in the centre and the small ones round it) draws flat white-blue, like ice or snow on ice. Lake drawing and its winter state are task 4.09's (`LakeWinterFold`, PR B #167, merged into main after this branch forked) | Cause: the branch forked from main (`e0200bdd`) before 4.09 PR B (#167, `121754ef`), which draws each lake in its own state from the simulation. Without it the game had one shared look for all water, iteration 1's snow-covered ice, and nothing but the developer panel changed it. The simulation already froze and thawed each body here; nothing on screen read it. Fix (plan approved 2026-11-15): A2 committed locally, then origin/main merged (no rebase, no change to the snow model; the four approved re-pins unchanged). The lake readout key moved to kind 8 (the snow figures use 1–7), and the lake line shows under Snow conditions as well as Snow depth. My first in-game check (a 24-day time-lapse over the valley lakes) still drew them frozen: `-snowlapse` counts as a capture, and captures keep iteration 1's look unless `-lakesim`. The time-lapse now follows the simulation's lakes too (`MountainViewer.Lakes.cs`, one condition). The player's own game was never a capture run. The simulation's own report: the 28-acre valley lake freezes at face day 16 and opens at day 71 | Fixed (merge `9f1e2e80` and the time-lapse condition, local). Rechecked 2026-11-15 with a 24-day time-lapse over the valley lakes (`test-results/snow-a2/lakes-lapse2`): open water in early October; the small lakes frozen and the large one still open in mid-November; the large one frozen under snow by early December. Owner to check in game |

| 2 | The valley goes green too soon (owner, 2026-11-15, from my lake-check time-lapse) | Jackson Hole, the valley floor round the valley lakes, 2.8 km south of the centre. The valley is white in mid-November, green three face days later, and still green in early December after more snow, while the same snow on the lake ice draws white. The bar's "20° · 13°" is the temperature now (7:30 AM) at the base and summit, not the day's; the valley's afternoons ran about 37–45 °F, close to Jackson's November normals, so some melt is real | Cause (probe `SnowValleyProbeTests`, chart `tools/snow-a2/valley.mjs`): (1) the ground showed through the snow's brightness under 20 mm of water (about 8 in of new snow), so a fresh inch at the base melted within two face hours while it was still snowing at 30–34 °F under cloud; (2) the ground drew fully white only from 6 in, so an inch or two of settled snow drew green. Fix (plan approved 2026-11-15): SN-09 v4, thin snow bright down to 5 mm of water (`Pacing.SnowThinUm`); full white from 2 in (`SurfaceStates.FullCoverMetres`), "Bare" under about 1 in. All the pillow gates pass with lower daily error at all six (Tahoe City's, missed by today's code, passes again). Big piles (owner's ask): a 4 m test whale melts the same to the µm in the middle and tapers with no ring; the real cliff pile peaks 1 % wetter | Fixed (local); four re-pins approved 2026-11-15 (season 0x07E5A422674F5E17, golden weeks 0x2A26EA543F4B45B3, guest day 0xA346DC422932F936, lake winter 0xBAD79647827108D6); the time-lapse recheck (`test-results/snow-a2/lakes-lapse3`) shows the valley mostly white in late November and white in early December |

## Pictures

### 1. Lakes white in early October on bare green ground (Jackson Hole, 08:30 AM)
*(The owner's screenshot was pasted into chat; it isn't saved on disk yet. It goes here once saved as
`images/p4-07-findings/01-lakes-frozen-early-october.webp`.)*
