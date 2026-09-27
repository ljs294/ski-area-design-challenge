# acquire: download a mountain as a resort package

The command-line face of the game's downloader (Phase 1 task 04; [0.3 §6](../../docs/plans/phase0-0.3-technical-architecture.md)). It runs the same engine-free code the game does: `Assets/MountainPlanner/Runtime/Acquisition` and `Persistence`, built for plain .NET by `tools/domain-tests`.

```sh
dotnet run --project tools/acquire -- --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2   # into your library
dotnet run --project tools/acquire -- library                                                      # list your mountains
dotnet run --project tools/acquire -- prepare --package <folder>                                   # rebuild a terrain cache
dotnet run --project tools/acquire -- validate --package <folder>                                  # check a package's files
```

Without `--out`, a download goes into the library at `%LOCALAPPDATA%\SkiAreaDesignChallenge\Resorts\<packageId>` (`--library` changes the root). Or double-click `demo.bat` and choose 11–16.

## What it does (tasks 04a and 04b)
1. **Plans the download.** It opens the S1M tile directories and splits the site into 1,000 × 1,000-cell sectors (1 km in the core, 2 km in the ring), with the exact bytes each needs.
2. **Terrain:** downloads the 1 m core from S1M, sector by sector.
3. **Terrain surroundings:** the 3 km ring from S1M's 2 m overview.
4. **Fallback:** fills any gap S1M lacks from the USGS 3DEP service (4 requests at a time), and blends the seams over 50 m.
5. **Forest:** Meta/WRI canopy height on the 1 m core (P7), reprojected from Web Mercator.
6. **Ground cover:** ESA WorldCover classes on a 10 m grid over the ring.
7. **Tree species:** lists the BIGMAP layers covering the ring, samples them in locked batches of 20 to find which species are present, downloads a 30 m map of each (up to 24), and keeps the top four per cell with their shares.
8. **Preparing terrain** (task 05): cuts the heights into 1,024 m Unity-ready tiles (1,025² in the core, 513² in the ring), which share their edges exactly, with detail like "preparing terrain tile 17 of 121".
9. **Building:** writes the compressed grids and `manifest.json`, which records provenance, attribution, the species table, the **terrain** quality score and one-liner (T18), the **flora** quality score and one-liner (F1), and a content-hash package id.

## Progress (U6)
Progress is reported at least 4 times a second: stage, step (for example "downloading sector 5 of 19 · 35%"), overall percentage, megabytes downloaded, speed and time remaining. When a server (USGS, the USDA Forest Service or AWS) is still preparing a response, it says which one, rather than sitting at 0%. Stages with several phases share their part of the bar by expected work, so the bar never moves backwards.

## Resume and idempotence
- **Resume:** every downloaded byte range is kept in a cache, `%LOCALAPPDATA%\SkiAreaDesignChallenge\download-cache` by default (`--cache` changes it). Stop a download at any time with Ctrl+C or by closing the window. Run it again and it continues where it stopped.
- **Idempotence:** the same site always gives the same package id. The id hashes the site and the layer values, not timestamps.

## Measured (2026-09-27, reference PC)

| Site | Terrain | Flora | Downloaded | Time | Package |
|---|---|---|---|---|---|
| Jackson Hole 2 km | 100/100 (S1M) | 81/100 | about 85 MB | about 40 s | 36 MB |
| Jackson Hole 5 km | 100/100 (S1M) | 83/100 | 252 MB | 69 s | 98 MB |
| Crystal Mountain 5 km | 48/100 (60% 10 m, 24% 3 m, 16% 1 m) | 75/100 (42% of forest modelled) | 484 MB | 101 s | 107 MB |

Most of the time goes on the species service (sampling and maps) and, where S1M is missing, on 3DEP generating exports.

The committed test terrain, `TestData/jackson-hole-2km`, is built by this tool (Git LFS; CI doesn't fetch it).
