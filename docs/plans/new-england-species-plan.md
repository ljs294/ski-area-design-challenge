# Task 09 phase 2: New England tree species

**Audience:** the project owner. **Status:** decided 2026-09-30 (below); runs in its own thread after task 09. **Date:** 2026-09-30. **Builds on:** the [species-model priority report](phase1-species-priority.md) (task 09) and its survey of every US ski area.

## Decided (owner, 2026-09-30)

| ID | Decision |
|---|---|
| NE1 | **All nine missing species are in scope**: the three waves below are one task, **task 09 phase 2** |
| NE2 | **The test mountain is Sugarloaf, Maine** (5 km): spruce-fir and fir krummholz on top, northern hardwoods below, and S1M lidar |
| NE3 | **A new model only where it looks different.** A species that looks largely like one we already have shares that model instead of getting its own. Diversity alone isn't a reason for a model |
| NE4 | It runs in its own thread, as tree work does (higher reasoning), after task 09 |

## Where New England stands

The survey found **93 New England ski areas** with forest: Vermont 29, New Hampshire 29, Maine 19, Massachusetts 11, Connecticut 4 and Rhode Island 1. Of those, 22 are the big destination resorts: Sugarloaf, Sunday River, Saddleback, Killington, Stowe, Sugarbush, Jay Peak, Loon, Cannon, Bretton Woods, Wildcat, Attitash, Black Mountain and the rest.

- **Already drawn as their real species:** red maple, sugar maple, yellow birch, American beech and paper birch. These are the Jackson, NH hardwoods kept from the first tree review (T8), plus quaking aspen. They make up **49%** of forest biomass at the average New England ski area and **66%** at the big resorts.
- **What's missing** (share of forest biomass at the average New England ski area; ski areas where it's at least 3%):

| Species | Share | Areas ≥3% | Where | Drawn today as |
|---|---|---|---|---|
| Northern red oak | 8.9% | 59 of 93 | Lower and southern hills (Gunstock 22%, King Pine 22%) | sugar maple |
| Eastern white pine | 8.4% | 63 | Lower slopes, southern NH and ME | lodgepole pine |
| Eastern hemlock | 7.9% | 78 | Valleys, ravines, everywhere | mountain hemlock (western hemlock after task 09) |
| White ash | 4.5% | 75 | Northern hardwood stands | quaking aspen |
| Red spruce | 4.3% | 43 | Upper slopes of the big mountains (Wildcat 17%, Waterville 16%) | Engelmann spruce |
| Balsam fir | 3.5% | 34 | The top of the big mountains (Wildcat 23%, Sugarloaf 21%) | subalpine fir |
| Sweet birch | 2.0% | 27 | Southern New England | paper birch |
| Black cherry | 1.7% | 10 | Massachusetts, southern Vermont | quaking aspen |
| Northern white-cedar | 0.7% | 5 | Maine swamps and limestone | mountain hemlock |

Elevation sorts these neatly. Oak, white pine and hemlock grow low and to the south. The northern hardwoods we already have fill the middle slopes. Red spruce and balsam fir cap the big mountains, down to fir krummholz at Sugarloaf's, Saddleback's and Wildcat's summits.

## The plan: look first, then model

**Step 1: decide which species need a model of their own (NE3).** Render each candidate's nearest existing model beside a description of the real tree in winter, the season the game shows (field-guide traits: silhouette, branching, bark, needle or bare-twig colour). Then sort each species into one of two groups:
- **Shares a model:** it looks largely alike at game distances. The species map then counts it as drawn as itself, so F1 credits it.
- **Needs its own model:** it doesn't.

You approve the sorting from the side-by-side renders. A first guess:

| Species | Nearest model we have | First guess | Why |
|---|---|---|---|
| Balsam fir | subalpine fir | **share** | Both are narrow, dark firs with flat sprays and smooth, blistered grey bark; balsam is a little broader |
| Red spruce | Engelmann spruce | **share**, maybe with its own palette | The same dense spruce cone; red spruce is shorter and yellower green |
| Eastern hemlock | western hemlock (task 09) | **share** | The same drooping leader and feathery sprays; eastern is smaller and broader |
| Sweet birch | yellow birch | **probably share** | The same birch form; its bark is darker |
| Black cherry | none close | own model, or share with yellow birch | Dark, flaky bark; an irregular crown. Close up it's distinctive, at a distance less so |
| White ash | none close | **own model** | Stout opposite twigs and a sparse, coarse winter crown; diamond-ridged bark |
| Northern red oak | none close | **own model** | A broad, rounded crown on heavy limbs, ridged bark; young trees keep dry leaves |
| Eastern white pine | none close | **own model** | New England's signature tree: tiered horizontal limbs and soft, long needles in tufts |
| Northern white-cedar | none close | **own model**, or share with western hemlock | Flat scale-leaf fans, narrow cone; small share (0.7%), so a share may do |

If the first guess holds, that's **four or five new models instead of nine**: oak, white pine, ash, white-cedar, and maybe black cherry.

**Step 2: build the models that are needed** with the free Blender pipeline:
- 3 variants each, within budget;
- your photo review before any import;
- only the new models imported, so existing tree files stay byte-identical.

**Step 3: switch them on.**
- In the species map, give each species its own model or its approved shared one.
- Re-run the survey report (`demo.bat` 28, from the cache).
- Download Sugarloaf and fly it.

**What all nine species bring** (if each were drawn as itself; the sorting decides how many models it takes):

| | Species | Average NE area | Big NE resorts | All US |
|---|---|---|---|---|
| Today (after task 09) | | 49% | 66% | 47% |
| Spruce-fir and hemlock | red spruce, balsam fir, eastern hemlock | 65% | 88% | 52% |
| Lower hills | northern red oak, eastern white pine, white ash | 87% | 95% | 63% |
| The rest | sweet birch, black cherry, northern white-cedar | **91%** | **97%** | **67%** |

*Species fidelity: the share of forest biomass drawn as its real species (or an approved shared model, NE3). Each 10% is 2.5 flora points (F1). Sugarloaf goes from 56% to about 96%.*

**What each new model needs from the Blender pipeline** (if it's needed):
- **Northern red oak:** a new oak leaf style (lobed, bristle-tipped; summer and red-brown autumn), a broad, rounded crown on a few heavy limbs, and ridged bark with flat-topped stripes. Young oaks hold dry leaves through winter like beech, through the existing "kept leaves" season flag.
- **Eastern white pine:** a new soft, long-needle tuft style (needles in fives). The form is the hard part: tiered horizontal branches, an irregular crown when old, often swept to one side by the wind.
- **White ash:** a new compound-leaf style, opposite branching with stout twigs, and diamond-patterned bark.
- **Black cherry:** dark, flaky bark and an irregular, open crown.
- **Northern white-cedar:** a new flat scale-fan foliage style and a narrow conical crown.
- **Shared species:** they need no build. If you'd like a shared species tinted differently, that's a later option: per-species colour in the shader, not a new model.

**Cost:**
- **Time:** about a day per new model plus your reviews; the sorting itself is a day.
- **Git LFS:** about 7 MB per new model, so 30–40 MB. That's about 250 MB of the free 1 GB after task 09.
- **Performance:** a site draws only the species it has (task 09), so these cost nothing at Jackson Hole or Crystal Mountain. Shared models even reduce Sugarloaf's draws.

## Also worth doing for New England

1. **Better look-alikes now, for free:**
   - eastern hemlock → western hemlock;
   - red spruce → Engelmann spruce, and balsam fir → subalpine fir (both already);
   - oaks → American beech rather than sugar maple: similar bark, and young oaks keep their dry leaves too;
   - ash, cherry and basswood → yellow birch rather than aspen.

   I'll make the hemlock and fir switches in task 09. The broadleaf ones are a quick follow-up if you like them.
2. **The test mountain is Sugarloaf (NE2):** a 5 km download (Sugarloaf, Maine; about 45.04° N, 70.31° W), the region's demo as Crystal Mountain is the Cascades'. Benchmark it like the other two: frame p95, VRAM and draw calls.
3. **Calibrate the forest for the region:** tree density and height still use Jackson Hole's factors (D4). New England's closed hardwood canopy differs. A lidar truth set from New Hampshire's public 3DEP point clouds, made the way Jackson Hole's was, would fit the Northeast's own factors. That's the "per-region calibration" still on the list.
4. **Seasons:** New England is the place players will want autumn colour. All the hardwoods already carry summer and autumn leaves; the season inputs in the tree shader are a separate task (TR4, T9). Oak, ash and cherry would ship with their autumn textures.

## Still open (in phase 2)

- **The sorting (step 1):** which species share a model. Decided from the side-by-side renders.
- **The look-alike switches** for species still without their own model once phase 2 is done. The first guesses are oaks → American beech, and ash, cherry and basswood → yellow birch. Decided with the renders too.
