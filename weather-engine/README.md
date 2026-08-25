# Standalone Weather Engine

This module is a deterministic development simulator for Mountain Planner. It uses the
existing pure time engine but does not import React, Electron, or the game runtime.

## Run it

From the repository folder:

```powershell
npm run sim:weather
```

On Windows, `run-weather-engine.bat` can also be double-clicked. The no-argument run
uses Black Mountain in Jackson, New Hampshire at 1,300 / 1,825 / 2,350 feet.

Useful commands inside the simulator:

```text
play
speed 4
speed 8x
pause
step 1d
skip week
skip ahead week
skip to event
weather hourly
weather week
weather off
weather on
weather toggle
weather day 3
weather band summit
weather events
weather seed my-test-seed
```

For a compact non-interactive check:

```powershell
npm run sim:weather -- --step 1w --json
npm run sim:weather -- --skip event --json
```

Custom coordinates use the provider chain Daymet (North America), NASA POWER
(worldwide), then a deterministic procedural fallback:

```powershell
npm run sim:weather -- --lat 39.6061 --lon -106.355 --base 2476 --mid 3000 --summit 3527
```

## Jackson fixture

Normal Jackson runs and all tests are offline. The committed fixture contains a
52-bin normalized climate baseline derived only from NASA Daymet daily observations
for 2010 through 2019. To deliberately refresh it:

```powershell
npm run sim:weather:refresh-jackson
```

The fetched variables are daily minimum and maximum temperature, precipitation,
vapor pressure, snow-water equivalent, and day length. A refresh requires network
access and may change deterministic generated winters even when the seed is unchanged.

## Model boundary

`weatherEngine.ts` owns hidden truth, forecasts, weather events, random-stream state,
and snapshots. It contains no terminal, filesystem, timer, browser, React, or Electron
imports. `cli.ts` owns wall-clock scheduling, display, commands, and save files.

Storm starts and continuation come from Jackson's measured dry-to-wet and wet-to-wet
probabilities. Amounts use fitted weekly gamma distributions and local percentiles.
Freeze/thaw and flash-freeze events are detected from generated hourly temperature
and moisture; they are not independent random rolls.

Snowpack, snowmaking, operations, construction, finance, attendance, and reputation
are intentionally not implemented. They can later consume crossed weather hours and
typed events without changing the time engine.
