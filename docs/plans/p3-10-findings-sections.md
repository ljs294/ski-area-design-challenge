# Phase 3 · 10 Trails made of sections: the owner's findings in the game

**Audience:** the project owner and coding agents.

**Status:** recording only. No fix or plan until the owner says go.

**Build reviewed:** `feature/p3-10-segments` at 31adba18 (PR #106, B2): Edit sections in the game, played with
`demo.bat` 70s (the owner's Kearsarge save). The captures are from 70sc in `test-results/p3-10-sections`, and the
Run 20 + Run 26 join is in `test-results/p3-10-join-20-26`. Earlier findings are in
[p3-10-findings.md](p3-10-findings.md) (mockup) and [p3-10-findings-game.md](p3-10-findings-game.md) (Network view).

| # | Finding | Where | Fix idea | Status |
|---|---|---|---|---|
| 1 | After trails have been reworked into different sections, the selection screen still shows the original trails (owner, 2026-10-27) | Toolbox › Trails › Edit, picking a trail (the selection before and in Edit sections); which display shows the old trails (outlines, names or the pane) is to be confirmed | Not planned yet | Recorded |
| 2 | The trail edit menu offers Graded or Natural as a choice. Make it a checkbox, so a natural trail can be graded after the fact; grading is a permanent decision (owner, 2026-10-27) | Toolbox › Trails › Edit, the trail's pane: the Graded/Natural switch (`TrailTool`) | Not planned yet | Recorded |
| 3 | Rename should not be an option (a button); renaming should always be available by clicking the name in the pane (owner, 2026-10-27, picture: Edit trail on Run 21) | Toolbox › Trails › Edit, the pane's Repaint / Move ends / Rename row and its Name field (`TrailTool`) | Not planned yet | Recorded |
| 4 | Trails should be selectable in the main game screen, the way lifts are (owner, 2026-10-27) | The main view with no tool armed: a click on a trail (as a click on a lift does today) | Not planned yet | Recorded |
| 5 | When trails have their sections swapped, the trail lines should be redrawn. In the owner's picture Run 30 and Upper Run 6 don't overlap, but their lines look as if they do. Lines should make it clear which trail goes where; maybe some slight smoothing is needed (owner, 2026-10-27, picture: Run 30, Upper Run 6, Run 18 and Run 21 above Chair 5) | The map's trail lines (`TrailMapView`, drawn per painted run, not per resolved trail) where reworked trails meet or cross | Not planned yet | Recorded |
| 6 | Trail lines should stop short at each end, by about 50 ft, to leave space where the trail starts and ends; it looks cleaner (owner, 2026-10-27) | The map's trail lines (`TrailMapView`), at each trail's top and bottom | Not planned yet | Recorded |
