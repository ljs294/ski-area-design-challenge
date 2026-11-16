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
| 1 | Lakes always appear frozen despite the weather (owner, 2026-11-15, with a screenshot) | Jackson Hole, Pre-season, early October, weekday, 08:30 AM. The valley floor is green and bare, with no snow anywhere, yet every lake (the large one in the centre and the small ones round it) draws flat white-blue, like ice or snow on ice. Lake drawing and its winter state are task 4.09's (`LakeWinterFold`, PR B #167, merged into main after this branch forked). Not looked into yet | — | Recorded |

## Pictures

### 1. Lakes white in early October on bare green ground (Jackson Hole, 08:30 AM)
*(The owner's screenshot was pasted into chat; it isn't saved on disk yet. It goes here once saved as
`images/p4-07-findings/01-lakes-frozen-early-october.webp`.)*
