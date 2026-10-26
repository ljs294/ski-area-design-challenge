# Phase 3 · 10 Network: the owner's findings

**Audience:** the project owner and coding agents.

**Status:**
- **Round 1** (findings 1–7): all fixed in the mockup, and in `NetworkReport` where the game needs it (plan approved
  2026-10-26; decisions NT10–NT12; commit cd13d6e1).
- **Round 2** (findings 8 on): open, **recording only**. Nothing is fixed until the owner says go.

The game port follows the approved mockup.

**Build reviewed:** `feature/p3-10-network`: first the mockup's Network view (`prototypes/ui-layout.html`, demo states
`#solo&demo=p2,net` and `p2,netpick`), later the game.

**Before this doc:** the first look (2026-10-25), "how is the network map any different from the trails for lift
overview? … a more specific layer where the background looks much different": the Network view became its own look, a
diagram over a dark grey relief with a key (mockup commit 950f0aae).

## Round 1 (2026-10-25): findings 1–7, fixed

| # | Finding | Where | Fix | Status |
|---|---|---|---|---|
| 1 | Make nodes between trails more prominent as a circle. Lift nodes can be circles too: every node is a circle (owner, 2026-10-25) | The Network view's diagram (mockup) | Every network node draws as a circle with a dark ring: junctions large and white, lift ends red, path ends small white, a trail's own head and tail open (amber when it leads nowhere). The key shows each; the `net` demo gained Catwalk, which leaves Cornice and crosses Bighorn, so two junctions show | Fixed in the mockup, for review |
| 2 | In the Network view, clicking a lift highlights every trail reachable from it; the same for a trail. The picked one and the reachable trails should look slightly different (owner, 2026-10-25) | The Network view: clicks on the map and rows in the list (mockup) | A click on a lift or trail (map or list) picks it. What a skier can reach downhill from it (a lift's top, a trail's head; never riding a lift) turns blue; the picked one is wider and white over a blue glow; the rest goes grey. Clicking it again clears the pick. The key shows Picked and Reached from it. For the game: `NetworkReport` gains each trail's reach, as it has each lift's | Fixed in the mockup, for review (demo states `p2,netpick`, `p2,netpicktrail`) |
| 3 | Trail and lift properties popups are blocked by the legend. Consider moving the legend, but don't make the screen too cluttered (owner, 2026-10-25) | The right-hand column: the lift or trail card and the legend (the Network key or an info layer's) share the top right, and the legend draws over the card | Fixed (NT12): the card stacks under the legend in one right-hand column; while a card is open the legend folds to its title line, with ▸ to unfold it. Applies to every legend (info layers too). In the Network view a click on a lift or trail picks it and opens its card (demo `p2,netcard`) | Fixed in the mockup, for review |
| 4 | Selecting a trail highlights trails below it that it only meets at a junction going downhill. From that junction a skier can't go up the other trail, but the whole trail is highlighted as reachable (owner, 2026-10-25) | The Network view's pick highlight (finding 2's mockup), e.g. Cornice lights all of Bighorn, though Catwalk joins Bighorn partway down | Fixed: the reach is kept per segment (NW-08), so only the stretch below the meeting lights; the rest of that trail stays grey (demo `p2,netpicktrail`: Bighorn lights only below Catwalk's junction). A trail's reach starts down the trail itself, not down other runs leaving the same head (a bug found while fixing: Bighorn and Cornice both leave Aspen Quad's top). `NetworkReport.ReachFrom` returns the reached segments | Fixed in the mockup and NetworkReport, for review |
| 5 | Clicking a trail should also show the lifts downhill that are reachable (owner, 2026-10-25) | The Network view's pick highlight: a picked trail lights only trails today; the lifts it leads to stay grey | Fixed (NT10): a lift whose bottom the descent reaches lights blue (line and bottom circle); its top isn't ridden. `NetworkReport.Reach.Lifts` lists them | Fixed in the mockup and NetworkReport, for review |
| 6 | No clear way to untoggle the selection of a lift or trail (owner, 2026-10-25) | The Network view's pick (finding 2): the only way out today is clicking the same lift or trail again, which nothing tells you | Fixed (NT11): Esc clears the pick and the card, a click on empty ground clears it, and the card's ✕ clears it too. The key's hint line says so | Fixed in the mockup, for review |
| 7 | Nodes should be selectable the same way (lighting what's reachable from them), but show no properties pane (owner, 2026-10-25) | The Network view: junctions, lift tops and bottoms, path ends and a run's own ends can't be picked today | Fixed: a click within about 8 px of a node picks it (nodes win over the lines through them): a blue glow ring, everything downhill of it lit by segment, no card. A lift's top lights like the lift; its bottom lights nothing. `NetworkReport.ReachFrom(node)` (demo `p2,netnode`) | Fixed in the mockup and NetworkReport, for review |

## Round 2 (2026-10-26): recording only

Reviewed: the mockup after round 1's fixes (cd13d6e1). Numbered as the owner gives them: R2-1, R2-2 and so on.

| # | Finding | Where | Fix | Status |
|---|---|---|---|---|
| R2-1 | When a lift lights because it's reachable, it should still light red, not blue. Trails and lifts should highlight in their own colours (owner, 2026-10-26) | The Network view's pick highlight (round 1's findings 2 and 5): everything reached turns one blue, lifts included | Not fixed yet. Idea: "reached" keeps each thing's own colour (lifts red; trails their colour) and shows as reached by being lit (a brighter, wider line with a glow), while what isn't reached goes grey; the picked one stays apart (white core over a glow). To ask when fixing: a trail's own colour is its rating colour (green, blue, black) or the diagram's pale run colour | Recorded |
