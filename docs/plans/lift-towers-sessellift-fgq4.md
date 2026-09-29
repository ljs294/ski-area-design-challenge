# Line towers · Sessellift FGQ-4

**Audience:** the project owner and coding agents. **Status:** approved by the owner (review rounds below); in
this PR. **Date:** 2026-09-29. **Decisions:** LP11-LP15 in [phase0-decisions.md](phase0-decisions.md). Follows
the [lift asset pilot](lift-pilot-sessellift-fgq4.md).

**What it is:** a kit of line towers for the Sessellift FGQ-4. The game can build any tower along a lift line
from three pieces, and choose one of five head types by what the rope does at that tower. The sheave assemblies
were rebuilt from the owner's own 3D reference model of a quad chairlift. The model stays local and unnamed; we
measured its mechanism and made our own design. The head is the drive terminal's approved entry head.

![The five tower head types](images/lift-towers-types.jpg)

## The kit

| Piece | Triangles (LOD0/1/2/3) | What it is |
|---|---|---|
| **Base** | 314 / 278 / 58 / 36 | Footing 2.5 m below grade, a 0.91 m square pier, a base plate with anchor nuts and gussets |
| **Mast section** | 144 / 88 / 40 / 12 | 1 m of the Ø610 mast with its ladder; rungs keep a 250 mm pitch across sections |
| **Head** | see below | A cap on the mast top carrying the drive's entry head: crossbeam, portal with lifting lugs and J-handrails, platform (kept inside the ropes, with a ladder hatch), a black number plate, and a sheave assembly on each rope |

The game stacks sections to reach the tower's height in 1 m steps; the Lift Lab's towers mode (`demo.bat` 23,
key 6) does the same, setting the base a little higher or lower to take up the remainder.

## Head types

| Type | Sheaves per rope | Arc | Triangles (LOD0/1/2/3) |
|---|---|---|---|
| **s4** general support | 4 | nearly flat (40 m) | 7,512 / 2,384 / 484 / 96 |
| **s6** general support | 6 | nearly flat | 10,720 / 3,248 / 620 / 96 |
| **b8** breakover | 8 | the reference arc (10.4 m) | 13,848 / 4,064 / 732 / 96 |
| **d8** hold-down | 8 | the same assembly flipped | 13,848 / 4,064 / 732 / 96 |
| **c8** combination | 4 hold-down over 4 support, aligned | flat | 13,792 / 4,008 / 732 / 120 |

**Sheave assemblies**, after the reference model:
- Ø432 sheaves in pairs on rockers: twin plates, one on each face, straight through both axles.
- Two rockers on a train yoke: twin bridge-shaped plates, legs down to the rocker pins.
- A cross tube from each yoke to a 163 mm square equaliser inboard of the sheaves.
- Our breakover assembly lands within 6 mm of the reference's sheave centres, which lie on one 10.4 m circle.
- Hold-down is the same assembly flipped upside down, the owner's observation.
- The combination is open on the outboard side so grips pass. On the tower side, each row's train frame is a
  triangular plate, and the two meet a central pivot block.

**Connection (LP13):** every assembly hangs from below the crossarm end. Two lug plates carry its pin 320 mm
under the crossarm, the connection the owner approved first on the return terminal's integrated tower. The rope's
height at the head therefore depends on the type. Measured from the crossarm's underside:
- support heads (s4, s6) carry it 0.05 m above, as the rope passes the drive's entry head;
- the breakover head carries it 0.11 m above;
- the hold-down head carries it 0.75 m below;
- the combination carries it 0.32 m below, at its pin.

The head records that height in its rope sockets, so the game sets the mast height from the rope's height at
the tower.

**Finish (LP14):**
- Towers are galvanised throughout.
- In every sheave row only the first and last sheave are red (lightning grounding); the rest are galvanised.
- The terminals' trains follow the same rule.

![Breakover head: the rope rides over the reference arc](images/lift-towers-breakover.jpg)

![Hold-down and combination heads](images/lift-towers-holddown-combo.jpg)

## The return terminal's integrated tower

The owner asked for the base terminal's integrated tower to be treated as a hold-down tower, and then for it
to look like a true hold-down, with the line going up out of the station:
- **Assembly:** each rope carries the towers' 8-sheave hold-down assembly, hung under the lifting frame's
  crossbeam end on the same lug plates. This replaces the 4-sheave trains of the pilot (LP9 → LP13).
- **Rope path:** the row is levelled at its first sheave (spec `"level": "first"`). The rope comes off the
  bullwheel level through the loading area, meets sheave 1 level, bends up through all eight sheaves and leaves
  the station climbing at 16°.
- **Arc:** the towers' reference arc (10.4 m) would bend the rope 20°, but the row's tilted equaliser would then
  cut 9 mm into the crossbeam. The rope's height and the crossbeam's both come from the drawings, so the return
  uses the tightest arc that clears it: 13 m, with a 40 mm gap. Its main pin hangs 143 mm under the crossbeam.
- **Sockets:** `rope_*_hold` marks where the level rope meets the row, and `rope_*_out` where it leaves, 0.5 m
  higher. The Lift Lab's return mode (key 2) draws the rope through them and 100 m up the line.
- **Triangles:** 19,864 / 7,028 / 1,836 / 316, within the terminal budget.

![The return terminal: the pilot's trains, and the hold-down row with the line leaving climbing](images/lift-towers-return.jpg)

## Budgets and performance

**Budgets (LP15):** head ≤16,000 / 5,000 / 1,000 / 200 triangles, switching at 15, 45, 150 and 800 m (doubled
on PC). A lift has many towers but only the nearest one or two are ever at full detail. Sheaves spin only at
LOD0; from LOD1 a head is a single mesh.

**Performance:** the pilot's Lift Lab benchmark, the median of three release runs at 1920 × 1080 on the
reference PC (an NVIDIA GeForce RTX 3060 Ti). The stress scene now has two towers in each of its 20 lift lines,
40 towers in all, with the five head types in turn.

| Scene | p50 | p95 | p99 | Batches | SetPass | Triangles |
|---|---|---|---|---|---|---|
| Empty (snow plane, sky) | 0.59 ms | 0.79 ms | 0.98 ms | 8 | 9 | 2,085 |
| Stress in the pilot (40 terminals, 500 chairs) | 0.76 ms | 1.07 ms | 1.26 ms | 389 | 24 | 116,823 |
| **Stress with towers** (plus 40 towers) | 0.85 ms | **1.22 ms** | 1.82 ms | 714 | 27 | 235,285 |

- The towers, with the return's larger trains, add about 0.15 ms at p95. All the lifts together cost 0.4 ms
  over the empty scene, against the 2 ms soft target and the 20 ms frame budget. The three runs' p95 ranged
  from 1.17 to 2.07 ms.
- The stress scene's ground is flat, so its lines run level from the return's exit to the drive; the towers
  carry the rope where that line passes them.
- Batches rise from 389 to 714. Each tower is drawn as a base, its mast sections and a head, and a head at LOD0
  draws each sheave separately. When the game places towers along a line, it can merge each tower's static
  pieces (future work, with the instanced chair path in Phase 3).
- A Development build measured 0 B of garbage per frame on average. One frame in the stress run allocated
  2.1 KB, as in the pilot.

![Towers and the return in the Lift Lab (Unity URP)](images/lift-towers-ingame.jpg)

## Review record

The owner reviewed each round. Each row is a round and what came of it:

| Round | Owner direction | Result |
|---|---|---|
| 1 | Build towers from the reference model, modular, with support, hold-down and combination heads | First kit; heads were our own crossarm design |
| 2 | Match the head to the drive station's; match the sheave trains to the reference | Head shared with the drive terminal; assemblies rebuilt from the reference, within 6 mm |
| 3 | Tower types and rules: 8 sheaves on hold-downs and breakovers, 4-6 on general support; aligned combinations with triangular supports; galvanised; red first/last | Five head types; colour rule on towers and terminals; budgets proposed |
| 4 | Attach trains directly to the crossarm; don't change the head; no platforms along the sheaves | Extra baskets and brackets removed; assemblies pinned at the crossarm ends |
| 5 | The base terminal's integrated tower is a hold-down tower; sheaves hang below the crossarm | Return rebuilt; approved as "accurate and correct" |
| 6 | Apply that connection to every tower head type | All types hang from lug plates 320 mm under the crossarm; approved with "red sheaves 1 and 8 only" |
| 7 | Make the return's sheaves a true hold-down, with the line going up out of the station | Row levelled at its first sheave on a 13 m arc, the rope leaving at 16°; photos on a hillside; approved ("Amazing") |

## Retrospective

- **The reference model saved the day.** Photos showed what a combination or hold-down looks like. The owner's
  3D model gave exact dimensions and the mechanism. Reading it (local Collada parsing, colour-coded part
  renders, fitting a circle to the sheave centres) was quicker than guessing from photos.
- **Owner knowledge set the rules.** It covered sheave counts per type, red end sheaves for grounding, aligned
  combinations, and where assemblies attach. None of it is in the reference geometry. Each round was short
  because the owner answered with a rule or a photo, not a description.
- **Over-reach cost a round.** Round 3 added maintenance baskets and changed the crossbeam from photos of other
  lifts; the owner rolled that back ("do not change the heads"). Keeping to the stated scope, and asking before
  adding, would have saved it.
- **Measure before rendering.** A geometry check (the mesh's edges under the crossbeam) showed the reference
  arc's tilted equaliser cutting 9 mm into the return's crossbeam before any photo was made. It also exposed
  that the far LODs of curved rows pinned 9 cm off the close ones; they now pin at the axles' centroid.
- **Reuse:** the kit's pieces (`line_assembly`, `combo_assembly`, the shared head, the tower photo scene)
  carry over to other Sessellift models. A detachable or six-seat lift is mostly a new spec.
