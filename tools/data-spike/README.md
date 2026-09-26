# Data spike (Phase 1, task 01)

Engine-free C# that proves the terrain data sources before the game is built on them. The findings are in [docs/plans/phase1-data-spike-report.md](../../docs/plans/phase1-data-spike-report.md).

| Project | Target | Purpose |
|---|---|---|
| `src/DataSpike.Core` | netstandard2.1, C# 9 (Unity's profile) | GeoTIFF / Cloud Optimized GeoTIFF reader (LZW, Deflate, predictors 2 and 3, tiles and strips, BigTIFF), HTTP range reads, EPSG:6350 and Web Mercator maths, provider clients. It moves into the `Acquisition` assembly in task 04 |
| `src/DataSpike.Cli` | net10.0 | `site` and `coverage` commands that measure live downloads and write JSON reports |
| `tests/DataSpike.Tests` | net10.0, NUnit | Offline tests, including recorded bytes of one S1M tile (Git LFS fixtures) |
| `research/forest_truth.py` | Python 3 | Scores the canopy map and WorldCover against a 3DEP lidar point cloud (report §6, D4) |

Requires the .NET 10 SDK. **Easiest:** double-click `demo.bat` at the repo root for a menu of the runs below; its reports go to `results/local/` (not committed).

```sh
dotnet test tests/DataSpike.Tests
dotnet run --project src/DataSpike.Cli -- site --name "Jackson Hole" --lat 43.593 --lon -110.848 --km 2 --out results/jh.json
dotnet run --project src/DataSpike.Cli -- site --name "Crystal Mountain" --lat 46.93 --lon -121.49 --km 5 --out results/crystal.json
dotnet run --project src/DataSpike.Cli -- coverage --out results/coverage.json
```

The `site` command contacts live providers: USGS, Meta/WRI on AWS, ESA WorldCover on AWS, the USFS BIGMAP service and LANDFIRE. `--record <dir>` re-captures the S1M test fixture. `--grids <dir>` writes the 10 m canopy and WorldCover grids for `research/forest_truth.py`, which needs `pip install numpy laspy[lazrs] pyproj`.
