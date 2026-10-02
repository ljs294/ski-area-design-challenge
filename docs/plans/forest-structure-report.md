# Forest structure against lidar (NE8)

**The problem:** in the game, dense conifer stands look like a plantation, because trees are evenly spaced and of similar height. Sugarloaf's in-forest view shows it most. The [New England species plan](new-england-species-plan.md) records the fix as decision NE8: more height spread and natural clumping, calibrated against lidar instead of guesswork.

**The result, in short:**
- Dense stands of shade-tolerant conifers (spruce, fir, hemlock) now grow an understory and clumps. Each cell keeps its calibrated number of trees.
- Treetop height spread now matches the lidar at Jackson Hole and closes most of the gap at Sugarloaf.
- The lidar also shows the bigger cause of the plantation look in New England: the game draws Sugarloaf's trees about 70% too tall. Bigger trees mean fewer, wider-spaced trees. That is the Jackson Hole calibration (D4) applied to the Northeast, and it needs its own fix ([§5](#5-what-this-doesnt-fix-new-englands-tree-size)).

## 1. Method

**Lidar truth.** The source is a 1 m canopy height model (CHM): the highest lidar return per square metre minus the lidar's own ground. The method is the same as the [D4 truth fixture](phase1-data-spike-report.md).

| Site | Lidar | Window (Albers) | Density |
|---|---|---|---|
| Jackson Hole | USGS 3DEP `WY_NConverse_5_2020`, the public EPT the truth fixture uses | The 2 km test core | 43 points/m² |
| Sugarloaf | USGS 3DEP `ME_Western_2016`, 6 tiles from the free COPC mirror on Microsoft Planetary Computer. USGS's own tile server timed out. | 3.4 × 1.6 km over the upper and middle mountain | 5.2 points/m² |

**The game's forest, seen the same way:**
- `acquire forest-dump` grows a package's forest with the game's own code and writes out each tree and each 10 m cell's classes.
- `tools/data-spike/research/forest_structure.py` draws those trees into a synthetic CHM: a cone per conifer and a dome per broadleaf, with the crown radius the game spaces them by.
- Both CHMs then go through one treetop detector: variable-window local maxima, 0.8 crown radii, trees from 3 m. The lidar can't see trees under the canopy, so the generated trees under the canopy are hidden in exactly the same way.

**Grouping.** Treetops are grouped by the game's own cell classes:
- **Conifer share:** from BIGMAP. "Conifer" is ≥ 80% conifers; "mixed conifer" is 50–80%.
- **Canopy share:** from the canopy map. "Dense" is ≥ 70% canopy.

**Measures, per class:**

| Measure | What it shows |
|---|---|
| Relative height of each treetop | Its height ÷ the cell's tallest |
| Height CV | Coefficient of variation of the treetop heights within a cell |
| Nearest-neighbour distances | How close the nearest treetop stands |
| Clark–Evans R | 1 is random, above 1 is regular |
| Variance/mean of counts in 5 m quadrats | 1 is random, below 1 is regular, above 1 is clumped |
| Gap share | Pixels below a third of the cell's tallest |

Reproduce with:

```
acquire forest-dump --package <folder> --out <dir> [--nostand 1 | --shortest S --skew K --floor F --lattice L --radius R]
python forest_structure.py chm --laz <tiles> | --ept <url>  --window W S E N --out site.npy
python forest_structure.py measure --chm site.npy --forest before=<dir> --forest after=<dir>
```

## 2. What the lidar shows

The game before the change ("before" is main at `ad7a388`):

| Dense stands | Treetops per cell | Height CV | Tops < 50% | 50–80% | ≥ 80% | NN p10 / p50 / p90 (m) | Clark–Evans R | Quadrat var/mean |
|---|---|---|---|---|---|---|---|---|
| **Sugarloaf conifer**, lidar | 4.41 | **0.223** | **12.6%** | 44.5% | 42.9% | 1.4 / 3.2 / 4.5 | 1.29 | 0.65 |
| game | 5.29 | 0.114 | 0.4% | 34.1% | 65.5% | 1.4 / 2.8 / 4.1 | 1.27 | 0.65 |
| **Sugarloaf mixed conifer**, lidar | 4.87 | **0.138** | **2.2%** | 39.6% | 58.2% | 1.4 / 2.8 / 4.2 | 1.28 | 0.65 |
| game | 3.56 | 0.105 | 0.3% | 28.7% | 71.0% | 2.0 / 3.2 / 5.0 | 1.33 | 0.69 |
| **Jackson Hole conifer**, lidar | 2.00 | 0.123 | 2.8% | 19.6% | 77.6% | 3.0 / 4.5 / 6.7 | 1.33 | 0.69 |
| game | 2.44 | 0.119 | 1.4% | 25.1% | 73.5% | 3.0 / 4.2 / 6.0 | 1.35 | 0.59 |

**What it means:**

1. **The game's heights are too even, and most of all at Sugarloaf.** Real dense stands have a tail of treetops well below the canopy that the game lacked: the game's 65–100% rule gives almost none under 50%. Sugarloaf's spruce–fir is the most layered: its height CV is double the game's.
2. **Real canopies are regular too, not random.** Seen from above, crowns compete for light, so real treetops are more evenly spaced than random: R ≈ 1.3 and quadrat variance/mean ≈ 0.65–0.69 at both sites. A plantation-style grid isn't wrong because it's regular. It's wrong because it's too uniform:
   - every gap is the same size;
   - there are no close pairs;
   - there are no wider openings.

   At Jackson Hole the game's spacings were too narrow in range: the 90th-percentile nearest neighbour was 6.0 m against 6.7 m, and quadrat variance/mean was 0.59 against 0.69. At Sugarloaf the game lacked close pairs: the 10th-percentile nearest neighbour was 2.0 m against 1.4 m.
3. **Sugarloaf and Jackson Hole differ for an ecological reason.** At the same number of treetops per cell, Sugarloaf has about 13 points more treetops below 80% of the tallest. For example, cells with 3 tops: 40% against 28%. Two candidate drivers don't explain this:
   - the cells' tallest tree;
   - the canopy map's own height spread per cell (correlation 0.18–0.26 with the lidar).

   The species do explain it:

   | Site | Main conifers in its dense cells | Shade tolerance |
   |---|---|---|
   | Sugarloaf | Balsam fir and red spruce | Tolerant |
   | Jackson Hole | Mostly Douglas-fir and lodgepole pine | Intermediate and intolerant |

   Tolerant species grow up in their own shade, which gives multi-storied stands.

## 3. The model

Dense stands of shade-tolerant conifers change; every other cell grows exactly as before.

- **Stand weight** per 10 m cell is BIGMAP's share of shade-tolerant conifers multiplied by the canopy share. Each factor runs through a smoothstep:
  - tolerant share: smoothstep from 0.3 to 0.7;
  - canopy share: smoothstep from 0.4 to 0.7.

  The shade-tolerant conifers are FIA firs 10–29, spruces 90–99, cedars 240–241 and hemlocks 260–269. Because the weight is graded, there are no hard edges between cells.
- **Heights:** in a full stand the share of the dominant height is 1 − 0.65 (1 − u)², shortest first. The table is written out with 17 integer quantiles, so no build depends on floating-point maths. Its shape:
  - about 40% of trees in the top fifth of the height range;
  - about 30% intermediate;
  - about 20% below half.

  Partial stands blend from the old 65–100% rule. Stand trees never go below 3 m.
- **Spacing floor:** each tree's minimum spacing now follows its own crown: the cell spacing × crown radius(its height) ÷ crown radius(dominant). A suppressed fir can stand under the edge of a tall one, but no two crowns get closer than their own floor. The spacing test passes with every Jackson Hole core cell forced to a full stand.
- **Clumps:** a clump field gives one clump centre per 14 m square, reaching 8 m.
  - A dart away from every clump is kept with only 96/256 probability.
  - The field is hashed from the site seed and frame position alone, so it runs on across cells and tiles without seams.
  - Stand cells throw up to 16 extra darts per tree, so they still reach their quota.

  The field's strength hardly moved the measured pattern (§4, variants E and F), because crown spacing sets most of the pattern at these densities. Its moderate setting was kept.
- **Tree count:** cell quotas, the D4 density calibration, are unchanged. The smaller understory crowns let crowded cells come closer to their quota, which the old forest missed by a few percent where crowns jammed:
  - Sugarloaf: 2,953,354 → 2,955,335 trees (+0.07%) of a 2,973,526 quota;
  - Jackson Hole: 811,704 → 812,671 trees (+0.12%) of 835,665.

## 4. Calibration and the result

**Shape sweep.** Five table shapes (shortest share, skew) were measured at both sites. Tables with more intermediate trees fit Sugarloaf's mixed conifer better, but overshoot Jackson Hole:

| Table | SL mixed CV | SL < 50% | SL ≥ 80% | JH CV | JH < 50% | JH ≥ 80% |
|---|---|---|---|---|---|---|
| lidar | 0.138 | 2.2% | 58% | 0.123 | 2.8% | 78% |
| before | 0.105 | 0.3% | 71% | 0.119 | 1.4% | 74% |
| 0.30, skew 3 | 0.104 | 1.6% | 78% | 0.130 | 2.3% | 75% |
| 0.30, skew 1.5 | 0.149 | 4.1% | 66% | 0.147 | 4.4% | 71% |
| **0.35, skew 2 (chosen)** | **0.125** | **2.3%** | 72% | **0.137** | **2.9%** | 73% |
| 0.40, skew 1.2 | 0.148 | 3.2% | 64% | 0.149 | 3.2% | 70% |

**Clump-field strength.** Measured with the chosen table:

| Variant | Clump field | SL mixed: Clark–Evans R | SL mixed: quadrat var/mean | JH: Clark–Evans R | JH: quadrat var/mean |
|---|---|---|---|---|---|
| E (stronger) | 12 m squares, 7 m reach, 48/256 kept between clumps | 1.32 | 0.71 | 1.31 | 0.64 |
| F (weaker) | 160/256 kept between clumps | 1.32 | 0.70 | 1.32 | 0.65 |

**After the change**, against the same lidar:

| Dense stands | Height CV | Tops < 50% | 50–80% | ≥ 80% | NN p10 / p50 / p90 (m) | Clark–Evans R | Quadrat var/mean | Gap share |
|---|---|---|---|---|---|---|---|---|
| **Sugarloaf conifer**, lidar | 0.223 | 12.6% | 44.5% | 42.9% | 1.4 / 3.2 / 4.5 | 1.29 | 0.65 | 0.38 |
| before → after | 0.114 → **0.136** | 0.4% → **1.9%** | 34.1% → 29.7% | 65.5% → 68.4% | 1.4 / 2.8 / 4.1 → 1.4 / 3.0 / 4.2 | 1.27 → 1.28 | 0.65 → 0.58 | 0.28 → **0.37** |
| **Sugarloaf mixed conifer**, lidar | 0.138 | 2.2% | 39.6% | 58.2% | 1.4 / 2.8 / 4.2 | 1.28 | 0.65 | 0.15 |
| before → after | 0.105 → **0.125** | 0.3% → **2.3%** | 28.7% → 25.4% | 71.0% → 72.2% | 2.0 / 3.2 / 5.0 → 2.2 / 3.6 / 5.4 | 1.33 → 1.32 | 0.69 → 0.71 | 0.29 → 0.37 |
| **Jackson Hole conifer**, lidar | 0.123 | 2.8% | 19.6% | 77.6% | 3.0 / 4.5 / 6.7 | 1.33 | 0.69 | 0.28 |
| before → after | 0.119 → 0.137 | 1.4% → **2.9%** | 25.1% → 24.6% | 73.5% → 72.5% | 3.0 / 4.2 / 6.0 → 3.0 / 4.5 / **6.3** | 1.35 → **1.31** | 0.59 → **0.66** | 0.22 → **0.28** |

**Jackson Hole** now matches its lidar closely:
- the suppressed tail;
- the spread of spacings: the wider openings and the variance/mean of counts;
- the gap share.

Height CV is slightly above the lidar.

**Sugarloaf:**
- The suppressed tail now matches the lidar in mixed conifer.
- Height CV closes about 60% of the gap.
- The lidar's large share of intermediate treetops (40–45%) is not reached.

That last gap, and the missing close pairs, follow from the tree-size problem below. The game grows about a third fewer, larger trees per cell, and with fewer trees per cell the tallest of them is less far above the rest.

**Every tree, not only what lidar sees.** In full stands (weight ≥ 0.9) the generated trees are 27% (Sugarloaf) and 20% (Jackson Hole) below half the dominant height, against 0% before. Most of those are under the canopy, as in a real stand. They show inside the forest and in its openings.

**Broadleaf.** Dense broadleaf stands at Sugarloaf show a similar but smaller gap: height CV 0.113 in the lidar against 0.090 in the game. NE8 covers conifers only, so they are unchanged. They are a candidate follow-up.

## 5. What this doesn't fix: New England's tree size

In dense conifer cells:

| Site | Dominant height in the game | Tallest tree per cell in the lidar | Game too tall by |
|---|---|---|---|
| Sugarloaf | 19.8 m | 11.7 m | 70% |
| Jackson Hole | 33.0 m | 24.8 m | 33% |

The game's dominant height is the canopy map's tallest value × 2.2 (D4, Jackson Hole). Crown size and spacing follow height, so Sugarloaf gets far fewer, larger trees than it has:
- 3.1–3.6 detected treetops per cell against 4.9;
- crowns spaced 2.3 m apart instead of about 1.6 m.

That is a big part of the plantation look, and it is a density and height calibration question, so it is outside NE8. **Recommendation:** a New England lidar truth set, fitting the Northeast's own D4 factors. The Sugarloaf lidar measured here would serve as one, and the [New England species plan](new-england-species-plan.md) already lists this as follow-up 3.

## 6. Determinism and cache

- **Determinism:** integer maths and keyed hashes only, so plain C# and Burst give the same bytes. With every stand weight at 0 the forest is byte-identical to main.
- **Golden tree hashes (Jackson Hole 2 km):**
  - `t4_3` and `t4_4` (core tiles) changed;
  - `t0_0` (a ring tile, which never grows stands) is unchanged;
  - the tree count went from 256,797 to 256,917.
- **Ground cover hashes:** unchanged.
- **Terrain cache:** v9 → v10, so downloaded sites regrow their forests.
