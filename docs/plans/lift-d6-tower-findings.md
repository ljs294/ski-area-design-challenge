# Sessellift D6 tower heads: owner findings

The owner's findings on the D6 wide line heads (s4, s6, s10, b10, d10, c8), in the order raised, with what was done. The
heads are measured on the owner's reference tower (kept locally, never in the repo) and built by
`tools/assets/lifts/assets/tower_wide.py` and `liftkit/wide_train.py` from `sessellift_d6.json` `towerWide`. New findings
are added at the end as the owner raises them.

Status: **fixed** (built, checked and shown to the owner), **open**, or **game** (needs a game task).

## Gate 1 (2026-10-21 to 10-22, approved at v7)

| # | Finding | What was done | Status |
| --- | --- | --- | --- |
| O1 | The towers are a mess; the owner's reference tower is the model | Head measured from it (Gate 0); heads rebuilt from the gauge | fixed |
| O2 | Check the reference head isn't a different scale; reuse our sheaves; match the six-pack's gauge | Sheaves 430 vs our 432; 5.6 m gauge; kit sheaves kept | fixed |
| O3 | Tower tubes not tapered; reuse our tower tubes and foundations | Kit straight mast sections and bases; only the head is new | fixed |
| O4 | c8 stays 4 hold-down over 4 support, as the quads | Kept | fixed |
| O5 | The sheave supports are blocky and boxy, not like the reference | Trains hang from a pin in twin cheek plates ~100 above the rope; the stub carries nothing; yoke, 6- and 4-beams, shaped rockers, every member carrying two children | fixed |
| O6 | The lifting frame doesn't match: the beam reads tapered, the diagonals are too thin | Tapered box legs; solid hoist-rail boxes replaced by a Ø30 bar on standoffs; beam end plates | fixed |
| O7 | Walkways only on trains of eight sheaves and up; b8 and d8 become ten-sheave trains | Head set s4, s6, s10, b10, d10, c8; walkways on s10, b10, d10 only | fixed |
| O8 | Lifting eyes match neither the reference nor our other tower | Plate across the beam, tongue to a round end, boss eye | fixed |
| O9 | Sheaves must pivot properly; platforms pivot with them | Every rocker and beam on its own pin, rockers stopped at ±5°; posed checks at line slopes to ±30° | fixed |
| O10 | Try without the rods from the lifting frame to the walkway ends | Removed | fixed |
| O11 | Reference sheave spacing; hold-down rows below the end block; c8 without walkway; treads fixed to the walkway | 550 / 730 pitch; d10 rope 620 below the support heads; c8 platform only | fixed |
| O12 | Remove the supports off every sheave rocker; one hanger and beam carry the walkway | One inner stringer with level stepped treads on one hanger post and beam | fixed |
| O13 | The platform support always hangs straight down (plumb) | Hanger links marked `plumb` in lifts.json (see O18 for the walkway itself) | fixed (game: G8) |
| O14 | Look again at how the sheave pairs attach to their rockers | Rocker plates as the reference's V; hub spacers to both plates; axle nuts | fixed |
| O15 | Extend the main work platform toward the sheaves, on every head | Platforms to 500 inboard of the rope, or to just inboard of the walkway with the side ladder at their end | fixed |

## Gate 2 (2026-10-22)

| # | Finding | What was done | Status |
| --- | --- | --- | --- |
| O16 | The c8 sits far too low under the tower; its sheave tops should line up like a hold-down head's | The hold-down row's beam moved between the end block's cheeks, so the c8 rises 130 mm (rope -570; hold-down sheave tops just under the end block) | fixed |
| O17 | The sheave-train walkways are too low; bring them up to the bottom of the ladder | Decks 400 below the rope (was 700); the side ladder moves 80 inboard, clear of the walkway's stringer, and reaches to 50 above the deck; those platforms stop at 1.60 m | fixed |
| O18 | The walkways don't rotate with the sheave trains; that looks awkward and is not correct | The walkway hangs from the yoke on two equal plumb links (a parallelogram): it turns with its train while the links hang straight down (pivots hanger_<side>1/2 "plumb", walkway_<side> "follow"); the links stand just inboard of the stringer on lugs off its inner face; the inner railing is open from -0.9 to +1.0 m (the crossarm, the side ladder) and around each link for its swing to 35 deg | fixed, to show (game: G8) |

## Open items raised in review (not owner findings)

- LOD1 to LOD2 (45 m, 90 m on the PC) still drops the cages, railings and ladders; LOD2 to LOD3 (150 m) drops the far
  walkway and the sheaves' red end discs.
- Walkway gratings take snow like a solid deck.
- The 4-beam's outboard bar is straight (the reference's is cranked at its pin); rocker pins carry a hex nut where the
  reference has a U retainer.
- s10 and b10 are at 99.5 % of the 24,000 LOD0 budget.

## Game handoff

- G6: head types S10, B10, D10 (head enum, the 8-sheave cap, prefab map, tests).
- G7: the rope height differs per head (hold-down -700, combination -570, support -80); read it from the rope sockets.
- G8: pivots marked `plumb` are turned back by their parents' turns so they hang straight down (the walkway links);
  a pivot marked `follow: <pivot>` is turned so its world angle equals that pivot's (the walkway, which then turns with
  its train). Neither is clamped to a range. Until the game does this, both stay rigid with their parents.
