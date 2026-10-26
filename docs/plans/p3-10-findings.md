# Phase 3 · 10 Network: the owner's findings

**Audience:** the project owner and coding agents.

**Status:** open; findings are added as the owner gives them, and each row says where it was fixed.

**Build reviewed:** `feature/p3-10-network`: first the mockup's Network view (`prototypes/ui-layout.html`, demo states
`#solo&demo=p2,net` and `p2,netpick`), later the game.

**Earlier rounds, before this doc:** round 1 (2026-10-25), "how is the network map any different from the trails for
lift overview? … a more specific layer where the background looks much different": the Network view became its own
look, a diagram over a dark grey relief with a key (mockup commit 950f0aae).

| # | Finding | Where | Fix | Status |
|---|---|---|---|---|
| 1 | Make nodes between trails more prominent as a circle. Lift nodes can be circles too: every node is a circle (owner, 2026-10-25) | The Network view's diagram (mockup) | Every network node draws as a circle with a dark ring: junctions large and white, lift ends red, path ends small white, a trail's own head and tail open (amber when it leads nowhere). The key shows each; the `net` demo gained Catwalk, which leaves Cornice and crosses Bighorn, so two junctions show | Fixed in the mockup, for review |
| 2 | In the Network view, clicking a lift highlights every trail reachable from it; the same for a trail. The picked one and the reachable trails should look slightly different (owner, 2026-10-25) | The Network view: clicks on the map and rows in the list (mockup) | A click on a lift or trail (map or list) picks it. What a skier can reach downhill from it (a lift's top, a trail's head; never riding a lift) turns blue; the picked one is wider and white over a blue glow; the rest goes grey. Clicking it again clears the pick. The key shows Picked and Reached from it. For the game: `NetworkReport` gains each trail's reach, as it has each lift's | Fixed in the mockup, for review (demo states `p2,netpick`, `p2,netpicktrail`) |
