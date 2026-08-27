# Standalone Weather Engine

Version 2 is the dependency-neutral authority for standalone climate compilation,
hourly simulation, forecasts, snapshots, canonical hashes, and comparison. The
standalone CLI and Web Worker both use the incremental API exported by `src/index.ts`.
The legacy winter simulator remains characterized during migration and is not used by
the independent Weather Model Lab.

## Run it

From the repository folder:

```powershell
npm run sim:weather
```

On Windows, `run-weather-engine.bat` can also be double-clicked. The no-argument run
uses Black Mountain in Jackson, New Hampshire at 1,300 / 1,825 / 2,350 feet.

Generate Jackson 2019 through the same canonical path as the worker:

```powershell
npm run sim:weather -- --seed Historical
npm run sim:weather -- --seed Historical --difficulty severe --json
npm run sim:weather -- --output jackson-2019.json
```

Other locations are prepared through the additive `/v1/weather-lab/` service APIs.
Provider failures are explicit; no procedural or legacy analog series substitutes
for an observed comparison.

## Jackson fixture

Normal Jackson runs and all tests are offline. The committed fixture contains a
normalized source baseline derived from Daymet observations. To deliberately refresh
the legacy characterization fixture:

```powershell
npm run sim:weather:refresh-jackson
```

The fetched variables are daily minimum and maximum temperature, precipitation,
vapor pressure, snow-water equivalent, and day length. A refresh requires network
access and may change deterministic generated winters even when the seed is unchanged.

`contracts.ts`, `climate/`, `engine/`, and `validation/` contain the pure v2 core.
`scripts/weatherCli.ts` owns filesystem/stdout adaptation, while `weather-lab/` owns
React, Canvas, and its worker lifecycle. Gameplay weather and schema-15 saves remain
separate compatibility boundaries.
