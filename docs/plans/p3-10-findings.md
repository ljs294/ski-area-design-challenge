# Phase 3 · 10 Network: the owner's findings

**Audience:** the project owner and coding agents.

**Status:** recording only (owner, 2026-10-25: "you are ONLY recording findings, not making fixes yet"). Fixes wait for
the owner's go. Findings 1 and 2 were fixed in the mockup before that instruction (commits 0eae148f, ccc804b7).

**Build reviewed:** `feature/p3-10-network`: first the mockup's Network view (`prototypes/ui-layout.html`, demo states
`#solo&demo=p2,net` and `p2,netpick`), later the game.

**Earlier rounds, before this doc:** round 1 (2026-10-25), "how is the network map any different from the trails for
lift overview? … a more specific layer where the background looks much different": the Network view became its own
look, a diagram over a dark grey relief with a key (mockup commit 950f0aae).

| # | Finding | Where | Fix | Status |
|---|---|---|---|---|
| 1 | Make nodes between trails more prominent as a circle. Lift nodes can be circles too: every node is a circle (owner, 2026-10-25) | The Network view's diagram (mockup) | Every network node draws as a circle with a dark ring: junctions large and white, lift ends red, path ends small white, a trail's own head and tail open (amber when it leads nowhere). The key shows each; the `net` demo gained Catwalk, which leaves Cornice and crosses Bighorn, so two junctions show | Fixed in the mockup, for review |
| 2 | In the Network view, clicking a lift highlights every trail reachable from it; the same for a trail. The picked one and the reachable trails should look slightly different (owner, 2026-10-25) | The Network view: clicks on the map and rows in the list (mockup) | A click on a lift or trail (map or list) picks it. What a skier can reach downhill from it (a lift's top, a trail's head; never riding a lift) turns blue; the picked one is wider and white over a blue glow; the rest goes grey. Clicking it again clears the pick. The key shows Picked and Reached from it. For the game: `NetworkReport` gains each trail's reach, as it has each lift's | Fixed in the mockup, for review (demo states `p2,netpick`, `p2,netpicktrail`) |
| 3 | Trail and lift properties popups are blocked by the legend. Consider moving the legend, but don't make the screen too cluttered (owner, 2026-10-25) | The right-hand column: the lift or trail card and the legend (the Network key or an info layer's) share the top right, and the legend draws over the card | Not fixed yet. Idea to discuss: stack the card under the legend in one column, with the legend folded to its title line while a card is open | Recorded |
| 4 | Selecting a trail highlights trails below it that it only meets at a junction going downhill. From that junction a skier can't go up the other trail, but the whole trail is highlighted as reachable (owner, 2026-10-25) | The Network view's pick highlight (finding 2's mockup), e.g. Cornice lights all of Bighorn, though Catwalk joins Bighorn partway down | Not fixed yet. Cause: the reach is kept per trail, so any reached segment lights the whole trail. Idea: light only the segments reached (from the junction downhill, NW-08); the stretch above the junction stays grey. The same applies to a lift's reach | Recorded |
