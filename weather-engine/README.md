# Standalone Weather Engine

The dependency-neutral engine is the authority for standalone climate compilation,
hourly simulation, forecasts, snapshots, canonical hashes, and comparison. The
standalone CLI and Web Worker both use the incremental API exported by `src/index.ts`.

## Run it

From the repository folder:

```powershell
npm run sim:weather
```

On Windows, `run-weather-engine.bat` launches that same standalone command.

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

Normal Jackson runs and all tests are offline. The committed development fixture is
implemented in `src/fixtures/jackson2019.ts`; live locations are prepared through the
Weather Lab service and never fall back to the fixture.

`contracts.ts`, `climate/`, `engine/`, and `validation/` contain the pure v2 core.
`scripts/weatherCli.ts` owns filesystem/stdout adaptation, while `weather-lab/` owns
React, Canvas, and its worker lifecycle. Gameplay weather and schema-15 saves remain
separate compatibility boundaries.
