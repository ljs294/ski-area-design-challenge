# Phase 1 · Task 01 data-spike report

**Audience:** the project owner and coding agents. **Status:** ready for owner review (review gate 1). **Date:** 2026-09-25. Plan: [0.7 task 01](phase0-0.7-phase1-plan.md). Decisions needed are in §5.

The spike is a small engine-free C# library, a command-line tool and offline tests in [`tools/data-spike/`](../../tools/data-spike/README.md). It read every planned data source live, for two sites:

| Site | Why | Size |
|---|---|---|
| **Jackson Hole, WY** (43.593, −110.848) | Proposed 2 km test terrain; inside S1M coverage | 2 km (ring 8 km) |
| **Crystal Mountain, WA** (46.93, −121.49) | The chosen demo mountain (G2); **not** in S1M yet | 5 km (ring 11 km) |

Raw results: [`jackson-hole-2km.json`](../../tools/data-spike/results/jackson-hole-2km.json), [`crystal-mountain-5km.json`](../../tools/data-spike/results/crystal-mountain-5km.json), [`s1m-coverage.json`](../../tools/data-spike/results/s1m-coverage.json).

## 1. Acceptance (0.7 task 01)

| Check | Result |
|---|---|
| A decoded S1M block has no NaNs and valid heights, and averages to the file's own 2 m overview within 0.05 m | ✅ **0 NaN; mean difference 0.022 m** (max 0.63 m on a single steep cell). Also passes offline in the unit tests on recorded bytes |
| Crystal Mountain 5 km returns a complete grid, with the source identified | ✅ Complete (0 gaps) through the fallback; sources identified per point (§3) |
| Canopy, WorldCover and species layers align to the S1M grid within 1 cell | ✅ **Elevation:** S1M matches the 3DEP service at shift (0, 0), mean difference 2.2 cm. **Canopy vs WorldCover:** best agreement at shift (0, 0) at Crystal Mountain (83%). The species layers use the same Albers projection (ESRI:102039), and their reported coordinates match ours within 2 m |
| The BIGMAP licence and area-download method are confirmed, or LANDFIRE is chosen | ✅ **Access:** confirmed (§2). ⚠️ **Licence:** the service states no restrictions, only a no-warranty disclaimer. It is USDA Forest Service data, which is normally public domain; §5 D5 asks you to accept that reading. LANDFIRE works as the fallback |

**Tests:** 14 offline unit tests pass (`dotnet test`): projection maths against the BIGMAP server's own coordinates, tile naming for S1M, canopy and WorldCover, both TIFF predictors, and the S1M directory, decode and overview check on recorded Jackson Hole bytes.

## 2. Measurements

| Data | Jackson Hole 2 km | Crystal Mountain 5 km | Notes |
|---|---|---|---|
| **Elevation core, S1M** | 13.8 MB, 7 requests, **1.1 s** | No S1M tile | Only the needed 512² blocks are read |
| **Elevation core, fallback** | — | 90 MB, 12.9 s | **A single 5 km request times out (HTTP 504);** 1 km pieces, 4 at a time, work. Responses are uncompressed float32 |
| **Surround ring, 2 m** | 44.7 MB, 5.1 s (8 km) | 112.6 MB, 24.6 s (11 km, fallback) | S1M's built-in 2 m copy works |
| **Canopy (Meta/WRI)** | 28.5 MB, 4.4 s | **143 MB, 10.9 s** | The files have no lower-resolution copies and store full-width pixel rows, so a site downloads whole rows. The heaviest item |
| **WorldCover** | 0.1 MB, 0.9 s | 0.2 MB, 1.2 s | Tiny |
| **BIGMAP species mix** (10×10 sample grid, one request) | 8.7 s | 20.4 s | A single-point query takes 37–63 s and can land on a ski run; the grid is reliable |
| **BIGMAP per-species export** (30 m) | ~1–2 s, 65 KB each | ~2–3 s, 257 KB each | One export per species present |
| **LANDFIRE vegetation type** (fallback) | 0.5 s, 33 KB | 0.6 s, 129 KB | Fast, 16-bit |
| **S1M coverage listing** | — | — | Full file listing: 7 s and 21 MB. **Folder names only: 14,340 tiles in 1.4 s and 1.5 MB** |

**Estimated first download for a 5 km site:** about 250–300 MB with S1M (core about 85 MB, ring about 75 MB, canopy 70–145 MB, the rest about 5 MB), and about 350 MB on the fallback path. That fits the 1 GB budget; the package on disk stays near the 0.3 §5 estimate. Crystal Mountain's whole run took **1 min 54 s** on this connection.

## 3. Findings

1. **S1M works as designed.** Reading only the needed blocks is fast and cheap, the 2 m overview is accurate, and the grid matches USGS's own service to 2 cm. The data-reading plan in 0.3 §4.3 holds.
2. **⚠️ The demo mountain has poor terrain data.** Crystal Mountain scores **48/100**:
   - 60% of the site is 10 m data, 24% is 3 m (a 2008 Mount Rainier survey) and 16% is 1 m lidar (Eastern Cascades 2019).
   - S1M has not reached Washington: none of its tile folders exist yet.
   - The demo and the menu background would therefore show the game's terrain at its least detailed. **Decision D1.**
3. **S1M already covers many famous mountains:** Jackson Hole, Big Sky, Taos, Killington, Stowe, Whiteface, Sugarloaf and Snowshoe. Vail, Breckenridge, Snowbird and Crystal Mountain aren't covered yet.
4. **Fallback requests must be chunked.** 1 km pieces work; this matches the per-1 km-tile fallback in 0.3 §4.2. The service ignored the compression request, so fallback downloads are about 4 bytes per cell.
5. **The canopy download is heavy.** It will usually be the largest item, and it can't be trimmed because of how the files are stored. It's still well within budget; it's a one-time download.
6. **Canopy and WorldCover disagree on the amount of forest at Jackson Hole:**
   - Canopy: 34% of cells have ≥10% tree cover. WorldCover: 68% tree cover. At Crystal Mountain they agree at 83%.
   - It isn't an alignment problem: shifting the grids doesn't help.
   - Jackson Hole's upper mountain is a mix of ski runs, meadows and short subalpine trees (median canopy 7 m). There, WorldCover's 10 m cells call mixed ground "trees", and the canopy map may miss short trees.
   - **Which one looks right can only be judged visually.** Proposal: decide the forest rule in the style tile (task 08) by comparing both against satellite imagery (**D4**).
7. **Species reality differs from the planned Phase 1 set** (TR1):
   - Jackson Hole: subalpine fir 55%, Engelmann spruce 32%, whitebark pine 9%, limber pine 3%.
   - Crystal Mountain: mountain hemlock 56%, western hemlock 32%, Shasta red fir 7%, Alaska yellow-cedar 4%, subalpine larch 1%.
   - BIGMAP is modelled data and sometimes names implausible species (Shasta red fir lives in California and Oregon). Species under about 3% of biomass should be dropped or merged. **D3.**
8. **No SQLite needed for coverage.** Listing folder names is enough for the picker, refreshed once a day in the background.
9. **For task 04:** when this code moves into Unity, `System.Text.Json` becomes Newtonsoft (T11). HTTP retries, the identifying User-Agent and parallel range reads are already in place.

## 4. What changes in the plan

- **0.3 §4.2:** fallback requests are 1 km pieces (already the design); expect about 4 bytes per cell from the service.
- **0.3 §6.1 / T19:** the coverage overlay comes from folder listings, not the GeoPackage.
- **0.3 §4.4:** the forest rule is finalised in task 08 (D4).
- **TR1:** the Phase 1 species set follows the actual demo and test sites (D3).

## 5. Decisions for you (review gate 1)

Write your answer after each **Comment:**; "OK" accepts the recommendation.

**D1 · Demo mountain.** Crystal Mountain scores 48/100 today.

Options:
- **(a)** Keep Crystal Mountain; it honestly shows the fallback path, and it will improve when S1M reaches Washington.
- **(b)** Switch the demo and menu background to an S1M-covered resort. **Big Sky** (big, dramatic peak) or **Jackson Hole** (famous, steep) are the best fits for a showcase.
- **(c)** Keep Crystal Mountain as a regular download, and make the bundled demo an S1M site.

**Recommendation: (b) or (c) with Jackson Hole**, so the first impression shows 1 m lidar.

**Comment:**

**D2 · Test terrain.** A 2 km Jackson Hole site (100/100, all five data sources work) as the checked-in test terrain.
**Comment:**

**D3 · Phase 1 species set.** Replace the planned set with the species the test and demo sites actually have.
- For Jackson Hole: **subalpine fir, Engelmann spruce, whitebark pine, limber pine**, plus the **krummholz** form.
- Keep **quaking aspen** as the deciduous test species only if a wider sample (for example the full 5 km mountain) shows it. This 2 km sample didn't include it.
- If Crystal Mountain stays in Phase 1, add **mountain hemlock** and **western hemlock**.
- Species under 3% are dropped or merged.

**Comment:**

**D4 · Forest rule.** Decide in the style tile (task 08) how canopy and WorldCover combine, by comparing both against satellite imagery at Jackson Hole.
**Comment:**

**D5 · BIGMAP licence.** Treat BIGMAP as public-domain federal data: credited in the game, no restrictions stated. The service shows only a no-warranty disclaimer.
**Comment:**

## 6. How to reproduce

```sh
cd tools/data-spike
dotnet test tests/DataSpike.Tests          # offline, ~1 s
dotnet run --project src/DataSpike.Cli -- site --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 --out results/jh.json
dotnet run --project src/DataSpike.Cli -- coverage --out results/coverage.json
```
