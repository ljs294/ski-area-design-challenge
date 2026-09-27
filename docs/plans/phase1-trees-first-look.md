# Phase 1 · Trees, first look

**Audience:** the project owner. **Status:** round 2, ready for owner review. **Date:** 2026-09-27. **Part of:** task 08, the style tile ([0.7](phase0-0.7-phase1-plan.md)); the look is set in [0.5 §3](phase0-0.5-art-direction.md).

These trees are built by a Blender script ([`tools/assets/trees/`](../../tools/assets/trees/README.md)), not modelled by hand, so each new species costs a parameter block rather than an artist's day. These are **Blender renders, not the game**. In Unity the tree shader adds wind, per-tree colour jitter and the real snow material.

## Round 1 (2026-09-26): your answers
- **T1 (look):** *"While they look good, I want them to look a tad more realistic. But this is an excellent start. Can I also see some deciduous trees that might be found around Jackson NH?"* This is addressed below.
- **T2 (Tree It):** *"Did we demo Tree It at all? Should we? Right now I am liking Blender pending getting them more realistic similar to the Tree It demo."* Answered below (R2).
- **T3 FBX, T4 Blender 5.2 LTS, T5 aspen bark colour:** OK.

## Round 2: what changed
**How realistic game trees are made.** Tree It, SpeedTree and most games build trees from a real branching skeleton with bark textures, dressed with **alpha-textured cards**: needle sprays, leaf clusters and fine twig silhouettes. Round 1 used solid shapes instead. The script now uses the card technique and generates every texture itself, so it stays free and repeatable:
- needle sprays in five styles (fir, spruce, hemlock, Douglas-fir, pine tufts);
- leaf clusters (maple, birch, beech, aspen) in summer and autumn colours;
- bare-twig silhouettes for winter;
- six bark types, including birch with black marks, Douglas-fir furrows and yellow-birch curls.

**Seasons now work.** Deciduous trees swap leaves (summer and autumn) and drop them for winter, leaving a fine twig crown. **American beech keeps its dry tan leaves through winter**, as it does in New Hampshire.

**The species come from the corrected BIGMAP data** (spike report §8; PR #29). Your Jackson, NH question uncovered a bug that had skewed the species lists.
- **Rockies and Cascades conifers:** subalpine fir, Engelmann spruce, Douglas-fir, lodgepole pine and mountain hemlock.
- **Jackson, NH hardwoods:** red maple (19% of biomass there), yellow birch (14%), sugar maple (9%), American beech (8%) and paper birch (4%), plus quaking aspen (19% at Jackson Hole).

## Conifers, winter
Two variants of each. The red figure is a 1.8 m skier.

![Subalpine fir, Engelmann spruce, Douglas-fir, lodgepole pine and mountain hemlock with snow](images/phase1-trees-conifers.jpg)

**Close up**, to judge needles and bark:

![Close-up of fir, spruce and Douglas-fir](images/phase1-trees-closeup.jpg)

## Jackson, NH hardwoods (and aspen)
**Autumn:**

![Aspen, paper birch, yellow birch, sugar maple, red maple and beech in autumn colour](images/phase1-trees-deciduous-autumn.jpg)

**Winter**, bare. Note the beech's retained leaves:

![The same trees bare in winter](images/phase1-trees-deciduous-winter.jpg)

**Seasons:** sugar maple and paper birch in winter, summer and autumn.

![Winter, summer and autumn](images/phase1-trees-seasons.jpg)

## Groves, from a game-like camera
**Jackson Hole mix** (by biomass): Douglas-fir, Engelmann spruce, aspen, subalpine fir and lodgepole pine.

![Jackson Hole grove in winter](images/phase1-trees-grove-rockies.jpg)

**Jackson, NH mix**, in autumn and in winter:

![Jackson, NH grove in autumn](images/phase1-trees-grove-newengland-autumn.jpg)

![Jackson, NH grove in winter](images/phase1-trees-grove-newengland.jpg)

## Levels of detail and shader data
![LOD0, LOD1 and LOD2 with triangle counts](images/phase1-trees-lods.jpg)

Snow mask (white = full load) and wind weights (red = trunk sway, green = branch flex, blue = flutter):

![Snow mask](images/phase1-trees-data-snow.jpg)
![Wind weights](images/phase1-trees-data-wind.jpg)

**Triangle budgets.** LOD0 is now 1.6k–17k triangles (the multi-stem paper birch is the most). LOD0 is only drawn close to the camera, and LOD1 and LOD2 are 0.6–4.7k and 160–900. Task 15 measures whether the forest fits the frame budget. If it doesn't, the LOD0 card counts come down first.

## Questions for you (round 2)

Write your answer after each **Comment:**; "OK" accepts the recommendation.

**R1 · Realism.** Is this closer to what you want? What still looks off? Examples: needle density, snow amount, crown shapes, colours, a particular species. These are all parameters.
**Comment:**

**R2 · Tree It: should we demo it?** We haven't. It has only a point-and-click interface, so I can't run it unattended, and it crashed once here.
- The Blender pipeline now uses the same technique Tree It does (branches plus textured cards).
- **Recommendation:** try Tree It yourself for 15 minutes on one species, and export it as FBX. I'll put it through the same finishing steps (LODs, wind and snow data) and render it beside ours, so you can compare them directly. If Tree It clearly wins for some species, we use it for those. Or skip this if round 2 already looks right to you.

**Comment:**

**R3 · Snow on conifers.** The previews show a heavy load, which is typical after a Jackson Hole storm. In the game the snow load is a single shader input, so it can follow the weather later. **Recommendation:** start the game at a medium load, and judge it in the style tile.
**Comment:**

**R4 · New England trees in the library.** Phase 1 only needs the Jackson Hole and Crystal species. **Recommendation:** keep the five Jackson, NH hardwoods in the library now, because they're done. They're ready for any New England mountain a player downloads, such as Black Mountain, Wildcat or Attitash near Jackson, NH.
**Comment:**

## Next
After your comments:
1. Tune the look.
2. Import into Unity with the tree shader (wind, snow load, season hook) in the style tile.
3. Build the remaining Phase 1 species (task 09): Pacific silver fir, western hemlock, noble fir and krummholz.
