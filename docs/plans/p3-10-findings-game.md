# Phase 3 · 10 Network: the owner's findings in the game

**Audience:** the project owner and coding agents.

**Status:** recording only (owner, 2026-10-26: "log issues only, not fix"). Nothing is fixed until the owner says go.

**Build reviewed:** `feature/p3-10-network` at 301c2b8d, the Network view ported to the game: `demo.bat` 70 (the style
tile's trail and lift, a second lift, and a run beside it), captures from 70c in `test-results/p3-10-network`. The
mockup's findings are in [p3-10-findings.md](p3-10-findings.md).

| # | Finding | Where | Fix idea | Status |
|---|---|---|---|---|
| 1 | The key's "Reached, in its own colour" row should say "Reachable", and its blue swatch should be split into green, blue, black and red squares with no gaps between them, to show the trail (and lift) colours (owner, 2026-10-26) | The Network view's key (top right), its last swatch row (`MountainHud.Network.cs` ShowNetworkKey) | A swatch made of four touching squares (green #1f8a4c, blue #2f6bb0, black #1f1c19, red #e0483a) in place of the single blue one, the row's word "Reachable"; the mockup's key (netLegend) to match | Recorded |
| 2 | The key's ✕ doesn't work (owner, 2026-10-26) | The Network view's key, its ✕ at the top right | Likely cause: `MountainHud.Layers.cs` ShowLegend shows the legend whenever the Network view is on, ignoring the closed flag the ✕ sets. Idea: the ✕ closes the Network key too (until the view is next opened), as it does an info layer's legend | Recorded |
