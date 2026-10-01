# New England tree species: plan

**Audience:** the project owner. **Status:** 🟦 draft for your review. **Date:** 2026-09-30. **Builds on:** the [species-model priority report](phase1-species-priority.md) (task 09) and its survey of every US ski area. Phase 1 covers Jackson Hole and Crystal Mountain (D3); this plans the Northeast for whenever you'd like it, Phase 2 at the earliest unless you say otherwise.

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

## The plan: three waves of three species

| Wave | Species | Average NE ski area | Big NE resorts | All US ski areas |
|---|---|---|---|---|
| Today | (after task 09) | 49% | 66% | 47% |
| **1: the big mountains** | red spruce, balsam fir, eastern hemlock | 65% | **88%** | 52% |
| **2: the lower hills** | northern red oak, eastern white pine, white ash | **87%** | 95% | **63%** |
| **3: the rest** | sweet birch, black cherry, northern white-cedar | 91% | 97% | 67% |

*Species fidelity: the share of forest biomass drawn as its real species. Each 10% of fidelity is 2.5 flora points (F1).*

- **Recommended order:** wave 1 first. It completes the destination mountains: Wildcat goes from 53% to 95%, Sugarloaf 56% to 92%, Cannon 58% to 89%. Those are the mountains players will download first. Starting with oak and pine instead helps the average ski area sooner (49% to 75%), but the big resorts barely move (66% to 74%). After wave 2 both orders end in the same place.
- **What each species needs from the Blender pipeline:** a parameter block and a palette. A new needle or leaf style is listed where one is needed.
  - **Red spruce:** derived from Engelmann spruce: shorter (20 m), a narrower cone, yellower green needles, scaly red-brown bark.
  - **Balsam fir:** derived from subalpine fir: a dense, dark spire with flat sprays, smooth grey bark with resin blisters. The existing krummholz model is fir-based already, so New England's summits get their krummholz for free.
  - **Eastern hemlock:** derived from western hemlock: smaller (20–25 m), a broader, irregular crown, the drooping leader, very fine flat sprays.
  - **Northern red oak:** a new oak leaf style (lobed, bristle-tipped; summer and red-brown autumn), a broad rounded crown on a few heavy limbs, and ridged bark with flat-topped stripes. Young oaks hold dry leaves through winter like beech, through the existing "kept leaves" season flag.
  - **Eastern white pine:** a new soft long-needle tuft style (needles in fives). The form is the hard part and the most recognisable tree in New England: tiered horizontal branches, an irregular crown when old, often swept to one side by the wind.
  - **White ash:** a new compound-leaf style, opposite branching with stout twigs (it reads differently from maple bare in winter), and diamond-patterned bark.
  - **Wave 3:** sweet birch (dark, cherry-like bark), black cherry (dark, flaky "burnt chip" bark), and northern white-cedar (a new flat scale-fan foliage style, a narrow conical crown).
- **How each wave runs**, as the task 09 trees did:
  1. A higher-reasoning tree thread builds the three species.
  2. You approve them from Cycles photos.
  3. Only the new models are imported.
  4. The species map switches them on.
  5. The survey and report re-run. That's `demo.bat` 28; it reads from the cache, so it takes about a minute.
- **Cost:**
  - **Time:** about a day per species plus your review, so a week per wave.
  - **Git LFS:** about 7 MB per species, 63 MB for all nine. That's about 280 MB of the free 1 GB after task 09.
  - **Performance:** a site draws only the species it has (task 09), so New England models cost nothing at Jackson Hole or Crystal Mountain.

## Also worth doing for New England

1. **Better look-alikes now, for free:**
   - eastern hemlock → western hemlock;
   - red spruce → Engelmann spruce, and balsam fir → subalpine fir (both already);
   - oaks → American beech rather than sugar maple: similar bark, and young oaks keep their dry leaves too;
   - ash, cherry and basswood → yellow birch rather than aspen.

   I'll make the hemlock and fir switches in task 09. The broadleaf ones are a quick follow-up if you like them.
2. **A New England test mountain:** a 5 km download of **Wildcat** (spruce-fir on top, hardwoods below, next to Jackson, NH) or **Black Mountain, NH** (hardwoods and hemlock). It would be the region's demo, like Crystal Mountain is for the Cascades. Wildcat is the stronger test of wave 1.
3. **Calibrate the forest for the region:** tree density and height still use Jackson Hole's factors (D4). New England's closed hardwood canopy differs. A lidar truth set from New Hampshire's public 3DEP point clouds, made the way Jackson Hole's was, would fit the Northeast's own factors. That's the "per-region calibration" still on the list.
4. **Seasons:** New England is the place players will want autumn colour. All the hardwoods already carry summer and autumn leaves; the season inputs in the tree shader are a separate task (TR4, T9). Oak, ash and cherry would ship with their autumn textures.

## Decisions for you

1. **Order:** wave 1 first, the big mountains (recommended), or the lower hills first?
2. **When:** right after task 09, or in Phase 2?
3. **Test mountain:** Wildcat (recommended), Black Mountain, or another?
4. **Look-alikes:** switch the broadleaf look-alikes now (oaks → beech; ash, cherry and basswood → yellow birch)? Recommended: yes.
