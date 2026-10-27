# Phase 3 · 10 Network: the owner's findings in the game

**Audience:** the project owner and coding agents.

**Status:** findings 1–3 fixed at the owner's word ("create a plan to fix all of the findings", plan approved 2026-10-26), for review. New findings are recorded only until the owner says go.

**Build reviewed:** `feature/p3-10-network` at 301c2b8d, the Network view ported to the game: `demo.bat` 70 (the style
tile's trail and lift, a second lift, and a run beside it), captures from 70c in `test-results/p3-10-network`. The
mockup's findings are in [p3-10-findings.md](p3-10-findings.md).

| # | Finding | Where | Fix idea | Status |
|---|---|---|---|---|
| 1 | The key's "Reached, in its own colour" row should say "Reachable", and its blue swatch should be split into green, blue, black and red squares with no gaps between them, to show the trail (and lift) colours (owner, 2026-10-26) | The Network view's key (top right), its last swatch row (`MountainHud.Network.cs` ShowNetworkKey) | Fixed: the row reads "Reachable"; its swatch is four touching squares (green #1f8a4c, blue #2f6bb0, black #1f1c19, red #e0483a; `LegendStripeRow`, `.lr__sw--stripes`), in the game and the mockup. On the dark panel the black square reads almost like a gap | Fixed, for review |
| 2 | The key's ✕ doesn't work (owner, 2026-10-26) | The Network view's key, its ✕ at the top right | Fixed: the ✕ closes the Network key on its own flag (`_networkKeyClosed`), and opening the Network view again brings it back; an info layer's legend closes as before. Mockup the same (`netkeyx`) | Fixed, for review |
| 3 | Try this on the existing Kearsarge map, built in a play test during `demo.bat` 68; make those saves accessible (owner, 2026-10-26) | The saves are in the game's data root, `%LOCALAPPDATA%\SkiAreaDesignChallenge` (`Saves\bbc9f6d2e978b8bc`, Kearsarge Mountain, save v3), but `demo.bat` 70 passes `-notitle -styletile`, so it skips the title and opens Jackson Hole's style tile | Fixed: `demo.bat` 70k starts this build at the title with no flags, so Continue (the latest save) or Load Area › Kearsarge Mountain opens the owner's own area; then Tab › Network. No code change: opening a save with terrain edits starts the edit session the network needs | Fixed, for review |
