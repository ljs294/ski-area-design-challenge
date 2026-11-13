# Forecast report (task 4.05 PR C, demo 93)

How good the forecast is at each of the seven weather test sites (WX-54 v2) at the design's week lean (0.65) and at
0.85, which events a winter names (WX-41), and how often the crowd's two weather lines move a morning (WX-44).
`WeatherForecastReportTests` (explicit, Core) measures 50 seasons of evening sheets a site and plays three whole winters
a site, and writes `test-results/weather-forecast/data.json`; `report.mjs` turns it into `report.html` beside it.

```
dotnet test tools/domain-tests/Tests -c Release --filter "FullyQualifiedName~WeatherForecastReportTests"
node tools/weather-forecast/report.mjs
```
