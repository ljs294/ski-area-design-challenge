# Live snow report (task 4.07 PR A2, demo 111)

The game's live snow (`Simulation/Snow`, rules SN-01 v2…SN-18 v2 in `docs/plans/p4-02-sim-rules.md` §4) run without
Unity for a season, from bare October 1 to April 30, on the 2 km round the centre of an area at 8 m a cell, under the
area's frozen weather rows. `SnowA2ReportTests` (explicit, Core, category `SnowReport`) writes
`test-results/snow-a2/report.json`; `report.mjs` turns it into `report.html` beside it.

- **Sites:** Jackson Hole always (the `TestData/jackson-hole-2km` package). A second area when `MP_SNOW_SECOND` names
  it as `Area name|weather rows` (for example `Olympic Valley|olympic-valley-4km`) and `MP_SNOW_AREAS` is a folder of
  downloaded areas to find it in (read only; demo 111 points it at your own downloads).
- **What it shows:** depth at the base, mid-mountain and the summit (the lowest, middle and highest tenth of the cells
  by height) each face day; the deepest cell against the mean (no cell piles up, SN-06 v2's run-out fan); the season's
  water ledger (every day balances to the µm); the depth and Snow conditions maps on five dates; how the mean sampler
  (SN-01 v2) moved the weather's terrain against one post a cell; the budgets.
- **The time-lapse:** pass a folder of `snow-*.png` stills and their `run.log` (from the game's `-snowlapse <folder>
  -snowdays N`) as the second argument and the report adds them as a flipbook, with the save size and the screen's
  tile upload time the run logged.

```
node tools/snow-a2/report.mjs test-results/snow-a2/report.json test-results/snow-a2/lapse
```

`SnowRunoutProbeTests` (explicit) is the probe behind the run-out fan: where the shed lands and the deepest cells, on
Jackson Hole or on `MP_PROBE_DIR` with `MP_PROBE_ROWS` until `MP_PROBE_UNTIL` (month/day).
