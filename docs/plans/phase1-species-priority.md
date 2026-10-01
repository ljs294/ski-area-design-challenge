# Phase 1 · Species-model priority report

**Audience:** the project owner. **Status:** for your review. **Date:** 2026-09-30. **Part of:** task 09 ([0.7](phase0-0.7-phase1-plan.md)); the score it serves is F1, the flora quality score ([0.3 §4.2](phase0-0.3-technical-architecture.md#42-elevation-extent-and-resolution-t4)).

**Question:** which tree species should we model next, so that the most mountains players download show their real trees?

**Answer:** **northern red oak**, then **eastern white pine**, **ponderosa pine** and **eastern hemlock**. Those four would add about 3.6 flora points to the average US ski area, ten times what this task's three Cascades species add. Oaks as a group are the largest gap: all oaks together are worth 3.7 points a mountain, and today every oak is drawn as a sugar maple.

## How it was measured

- **The mountains:** every operating downhill ski area with runs and lifts in the contiguous US, from OpenSkiMap (built from OpenStreetMap; ODbL). That's 478 areas, each counted once. We have no download statistics yet, so a small hill counts as much as a destination resort.
- **The species:** for each area, a 5 km site as the game would download it, and BIGMAP's species biomass sampled exactly as the downloader samples it: a 20 × 20 grid over the 11 km surround, every species layer, in locked batches of 20. 477 areas have forest species; one has none.
- **The gain:** F1's species fidelity is the share of forest biomass drawn as its real species, and it is worth 25 of the 100 points. Modelling a species therefore adds 25 × its share of biomass to a mountain's flora score. The table ranks species by that gain, averaged over all areas.
- **Reproduce it:** `demo.bat` → **28** (about 5 hours; it resumes where it stopped), or `acquire species-survey` and `acquire species-report` (tools/acquire). The survey made 5,251 requests to the Forest Service, three areas at a time, and every response is cached.

## Where we stand

- Across the 477 forested ski areas, **45.5%** of forest biomass is drawn as its real species with the 11 species we had before this task.
- This task adds Pacific silver fir, western hemlock and noble fir: **46.9%** on average. That's +0.4 flora points across the US, but **+5.5 points across the 30 ski areas in Washington and Oregon**, and about +11 at Crystal Mountain (flora 75 to about 87).
- The library was chosen for Jackson Hole and Crystal Mountain (D3) and for Jackson, NH. It fits the Rockies and the Cascades. It doesn't fit the Northeast, Upper Midwest and Mid-Atlantic, where most US ski areas are and where oaks, pines and northern hardwoods dominate.

## The ranking, after this task

The species not modelled once this task is done, by the flora points a model would add to the average ski area. "Drawn today as" is the look-alike the species map uses until then.

| Rank | Species (FIA code) | Drawn today as | Flora points per area | Areas where it's ≥3% of biomass | Biggest shares |
|---|---|---|---|---|---|
| 1 | Northern red oak (833) | sugar maple | 1.43 | 241 | King Pine (NH) 22%; Gunstock Recreation Area (NH) 22%; Camden Snow Bowl (ME) 22% |
| 2 | Eastern white pine (129) | lodgepole pine | 0.78 | 155 | Powderhouse Hill Ski Area (ME) 26%; Abenaki Ski Area (NH) 26%; Nashoba Valley Ski Area (MA) 25% |
| 3 | Ponderosa pine (122) | lodgepole pine | 0.70 | 78 | Ski Mystic Deer Mountain (SD) 87%; Terry Peak Ski Area (SD) 85%; Cottonwood Butte Ski Hill (ID) 52% |
| 4 | Eastern hemlock (261) | mountain hemlock | 0.65 | 136 | Dynamite Hill (NY) 20%; Double H Ranch (Hidden Valley) (NY) 18%; Ridin-Hy (NY) 17% |
| 5 | Black cherry (762) | quaking aspen | 0.55 | 114 | Holimont (NY) 19%; Holiday Valley (NY) 18%; Timberline Mountain (WV) 18% |
| 6 | White fir (15) | subalpine fir | 0.55 | 71 | Cedar Pass (CA) 35%; Coppervale (CA) 33%; Granlibakken Ski Resort (CA) 33% |
| 7 | White ash (541) | quaking aspen | 0.54 | 148 | Greek Peak Mountain Resort (NY) 13%; Snow Ridge (NY) 11%; Swain Ski & Snowboard Resort (NY) 10% |
| 8 | White oak (802) | sugar maple | 0.54 | 128 | Hidden Valley (MO) 27%; Heiliger Huegel Ski Club (WI) 15%; Wilmot Mountain (WI) 14% |
| 9 | Black oak (837) | sugar maple | 0.35 | 77 | Pine Knob Ski & Snowboard Resort (MI) 14%; Mt. Holly Ski & Snowboard Resort (MI) 13%; Mt Brighton (MI) 13% |
| 10 | Chestnut oak (832) | sugar maple | 0.32 | 55 | Massanutten Resort (VA) 29%; Wintergreen Ski Resort (VA) 27%; Bryce Resort (VA) 24% |
| 11 | Bur oak (823) | sugar maple | 0.31 | 51 | Frost Fire Park (ND) 36%; Bottineau Winter Park (ND) 34%; Detroit Mountain (MN) 31% |
| 12 | Sweet birch (372) | paper birch | 0.29 | 83 | Eagle Rock Ski Resort (PA) 10%; Montage Mountain Ski Area (PA) 9%; Mount Southington Ski Area (CT) 9% |
| 13 | Grand fir (17) | subalpine fir | 0.29 | 34 | Bluewood (WA) 51%; Bald Mountain Ski Area (ID) 51%; Dixie Summit (OR) 29% |
| 14 | California red fir (20) | subalpine fir | 0.28 | 25 | China Peak Mountain Resort (CA) 34%; Kirkwood Mountain Resort (CA) 32%; Palisades Tahoe Olympic Valley (CA) 29% |
| 15 | Red spruce (97) | Engelmann spruce | 0.26 | 56 | Wildcat Mountain (NH) 17%; Waterville Valley Resort (NH) 16%; Sugarloaf (ME) 15% |
| 16 | Balsam fir (12) | subalpine fir | 0.26 | 55 | Wildcat Mountain (NH) 23%; Sugarloaf (ME) 21%; Saddleback Mountain (ME) 20% |
| 17 | Jeffrey pine (116) | lodgepole pine | 0.25 | 31 | Snow Summit (CA) 23%; Mount Waterman (CA) 23%; Bear Mountain (CA) 23% |
| 18 | Yellow-poplar (621) | quaking aspen | 0.24 | 47 | Big Snow American Dream (NJ) 16%; Wintergreen Ski Resort (VA) 16%; Spring Mountain Ski Area (PA) 15% |
| 19 | American basswood (951) | quaking aspen | 0.21 | 44 | Detroit Mountain (MN) 10%; Andes Tower Hills (MN) 8%; Christie Mountain Ski Area (WI) 7% |
| 20 | Red pine (125) | lodgepole pine | 0.21 | 35 | Mt. McSauba (MI) 18%; Nordic Mountain (WI) 18%; Otsego Club (MI) 12% |

**By genus**, all species of each group together (points per area; one model could stand in for a whole group, though F1 only counts the exact species): oaks 3.7, pines 2.4, true firs 1.5, ashes 0.8, hemlocks 0.7, spruces 0.4, hickories 0.4, poplars 0.4, birches 0.3, maples 0.2.

## Recommendation

1. **Next four models (Phase 2 or when convenient):**
   - northern red oak (241 ski areas at 3% or more; the Northeast and Midwest);
   - eastern white pine (155; New England);
   - ponderosa pine (78; the Black Hills, Idaho, the Sierra, the Southwest);
   - eastern hemlock (136; New York and New England).

   Together they're about 3.6 points a mountain. The pipeline builds a species from a parameter block, so each is mostly a day of tuning plus your photo review.
2. **Then:**
   - the northern hardwood pair black cherry and white ash;
   - white fir and red fir for California;
   - the Maine and New Hampshire spruce-fir: balsam fir and red spruce, at Sugarloaf, Saddleback, Wildcat and Waterville Valley.
3. **Oaks as a family:** once red oak exists, white, black, bur and chestnut oak could share its model with different bark and crown parameters, cheaply. That's worth about 1.5 more points a mountain.
4. **When the game has players,** weight this by what they actually download. The survey tool already takes any list of mountains.

## Caveats

- The species come from BIGMAP (2018, 30 m), whose own accuracy varies by region.
- Scores measure fidelity to the species list, not looks. Some look-alikes are close (blue spruce as Engelmann spruce); others are poor (every oak as a sugar maple, every broadleaf as an aspen).

Data: ski areas © OpenStreetMap contributors via OpenSkiMap (ODbL); tree species: USDA Forest Service, FIA BIGMAP 2018 (public domain).
