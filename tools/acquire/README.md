# acquire: download a mountain as a resort package

The command-line face of the game's downloader (Phase 1 task 04; [0.3 §6](../../docs/plans/phase0-0.3-technical-architecture.md)). It runs the same engine-free code the game does: `Assets/MountainPlanner/Runtime/Acquisition` and `Persistence`, built for plain .NET by `tools/domain-tests`.

```sh
dotnet run --project tools/acquire -- --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 --out tools/acquire/out/jh2
```

Or double-click `demo.bat` and choose 11–14.

## What it does (task 04a: terrain)
1. **Plans the download.** It opens the S1M tile directories and splits the site into 1,000 × 1,000-cell sectors (1 km in the core, 2 km in the ring), with the exact bytes each needs.
2. **Terrain:** downloads the 1 m core from S1M, sector by sector.
3. **Terrain surroundings:** the 3 km ring from S1M's 2 m overview.
4. **Fallback:** fills any gap S1M lacks from the USGS 3DEP service (4 requests at a time), and blends the seams over 50 m.
5. **Building:** writes the compressed grids and `manifest.json`, which records provenance, attribution, the quality score and one-liner (T18), and a content-hash package id.

## Progress (U6)
Progress is reported at least 4 times a second: stage, step (for example "downloading sector 5 of 19 · 35%"), overall percentage, megabytes downloaded, speed and time remaining. When USGS is still preparing a response, it says so, rather than sitting at 0%.

## Resume and idempotence
- **Resume:** every downloaded byte range is kept in a cache, `%LOCALAPPDATA%\SkiAreaDesignChallenge\download-cache` by default (`--cache` changes it). Stop a download at any time with Ctrl+C or by closing the window. Run it again and it continues where it stopped.
- **Idempotence:** the same site always gives the same package id. The id hashes the site and the layer values, not timestamps.

## Measured (2026-09-27, reference PC)

| Site | Quality | Downloaded | Time | Package |
|---|---|---|---|---|
| Jackson Hole 2 km | 100/100 (S1M) | 58 MB | 9 s | 35 MB |
| Jackson Hole 5 km | 100/100 (S1M) | 148 MB | about 30 s | 94 MB |
| Crystal Mountain 2 km | 30/100 (3DEP 10 m) | 84 MB | 15 s | 38 MB |

The committed test terrain, `TestData/jackson-hole-2km`, is built by this tool (Git LFS; CI doesn't fetch it).
