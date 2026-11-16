# Phase 4 · 07 Snow Model (PR A2, the live snow in the game): the owner's findings

**Audience:** the project owner and coding agents.

**Status:** findings round open (2026-11-15). New findings are recorded only until the owner says go.

**Build reviewed:** `feature/p4-07-snow-a2` (uncommitted, player rebuilt 2026-11-15), `demo.bat` 111: the live snow,
the Snow depth and Snow conditions layers, and the 45-day time-lapse.

**Earlier (first demo, 2026-11-15):** wet granular showed on the Snow conditions layer where the ground looked bare.
Fixed at the owner's word ("Yes, fix it"): the layer fades each type to bare by the ground's cover, and the readout says
"Bare · traces of …" under about 3 in. This build carries the fix.

| # | Finding | Where | Fix idea | Status |
|---|---|---|---|---|
| 1 | Lakes always appear frozen despite the weather (owner, 2026-11-15, with a screenshot) | Jackson Hole, Pre-season, early October, weekday, 08:30 AM. The valley floor is green and bare, with no snow anywhere, yet every lake (the large one in the centre and the small ones round it) draws flat white-blue, like ice or snow on ice. Lake drawing and its winter state are task 4.09's (`LakeWinterFold`, PR B #167, merged into main after this branch forked) | Cause: the branch forked from main (`e0200bdd`) before 4.09 PR B (#167, `121754ef`), which draws each lake in its own state from the simulation. Without it the game had one shared look for all water, iteration 1's snow-covered ice, and nothing but the developer panel changed it. The simulation already froze and thawed each body here; nothing on screen read it. Fix (plan approved 2026-11-15): A2 committed locally, then origin/main merged (no rebase, no change to the snow model; the four approved re-pins unchanged). The lake readout key moved to kind 8 (the snow figures use 1–7), and the lake line shows under Snow conditions as well as Snow depth. My first in-game check (a 24-day time-lapse over the valley lakes) still drew them frozen: `-snowlapse` counts as a capture, and captures keep iteration 1's look unless `-lakesim`. The time-lapse now follows the simulation's lakes too (`MountainViewer.Lakes.cs`, one condition). The player's own game was never a capture run. The simulation's own report: the 28-acre valley lake freezes at face day 16 and opens at day 71 | Fixed (merge `9f1e2e80` and the time-lapse condition, local); the time-lapse recheck waits for the GPU; owner to check in game |

## Pictures

### 1. Lakes white in early October on bare green ground (Jackson Hole, 08:30 AM)
*(The owner's screenshot was pasted into chat; it isn't saved on disk yet. It goes here once saved as
`images/p4-07-findings/01-lakes-frozen-early-october.webp`.)*
