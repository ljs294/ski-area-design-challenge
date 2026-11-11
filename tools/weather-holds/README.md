# Wind-hold report (task 4.05 PR B1, demo 94)

How often wind holds a lift in a season at each of the seven weather test sites, at three `Pacing.WindScale` values,
so the owner can choose the scale. `WeatherHoldReportTests` (explicit, Core) plays each site's frozen rows for five
winters with a base fixed-grip quad and a summit fixed-grip and detachable quad, and writes
`test-results/weather-holds/data.json`; `report.mjs` turns it into `report.html` beside it.

```
dotnet test tools/domain-tests/Tests --filter "FullyQualifiedName~WeatherHoldReportTests"
node tools/weather-holds/report.mjs
```
