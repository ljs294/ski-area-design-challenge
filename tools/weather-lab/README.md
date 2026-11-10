# Weather Lab

The owner's page for looking at the weather engine's seasons, and at how a change moves them (task 4.05,
2026-11-09). One page, all seven fixture sites: pick a site, one of 20 sample seasons and an elevation, and read it
week by week (new snow, rain, base depth, temperatures, gusts, a one-line report), against the range of 200 game
seasons and, at the reference height, the site's real winters.

Run it from `demo.bat` (entry 97), or by hand from the repo root:

```bash
dotnet test tools/domain-tests/Tests --filter "FullyQualifiedName~WeatherLabTests"
node tools/weather-lab/build.mjs
```

The first command writes `test-results/weather-lab/data.json` (`Tests/Core/Weather/WeatherLabTests.cs`, an explicit test
that no suite runs); the second fills `lab.html` and writes `test-results/weather-lab/index.html`.

**Before and after.** The first build pins itself as the baseline (`test-results/weather-lab/baseline.json`). Every later
build draws the baseline's season as a dotted line and shows "before" under each tile, so a calibration or generator
change shows season by season. `node tools/weather-lab/build.mjs --pin` makes the current run the new baseline;
deleting `baseline.json` does the same at the next build. Nothing here is committed: `test-results/` is ignored. (Not `Temp/`: Unity empties it whenever it opens or
closes the project, baseline and all.)

**What it is and isn't.**
- The weather is the engine exactly as the game plays it: the frozen rows (`TestData/weather/<site>/weather.rows`),
  `WeatherTuning.Default`, seeds `0x5341_4D50_0000_0000 + n`.
- The base depth is an illustration, not the game's snow (4.07 owns the snowpack): the fidelity tests' reference
  pack (snowpack temperature, a melt factor that follows the sun's season) plus a settling density (0.30 cold, 0.38
  near freezing, 0.45 wet). No grooming, wind loading, skier packing, snowmaking or terrain shade.
- Gusts are the engine's free air at the reference height in lift hours. Exposed summits, lift top stations and wind
  holds arrive with the engine in the game.
- A game week is three face days (weekday, weekday, weekend), about 8.6 calendar days; the season is Day 1 (Dec 1)
  to Day 42 (Apr 1).
