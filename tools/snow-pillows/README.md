# Snow pillow report (task 4.07 PR A1, demo 112)

How the snow energy (`Domain/Weather/SnowEnergy`, rules SN-09 v2 and WX-59…64) does against six western SNOTEL snow
pillows, with one set of numbers for every site. `SnowPillowTests` (explicit, Core, category `SnowReport`) plays each
pillow's winters three ways and writes `test-results/snow-pillows/<tag>.json`; `report.mjs` turns it into `<tag>.html`
beside it.

- **On the gauge:** the pillow's own precipitation, hourly, with 4.05's weather record lapsed to the pillow's height.
- **At the game's clock:** the same water, stepped in face hours of K = 121 ÷ 42 real hours. Each face hour takes the
  record's weather at its own hour of day on the real day it falls in, and all the precipitation of the real hours it
  stands for, each real hour's share split into snow and rain by that hour's own weather.
- **Melt only:** from the measured peak, with the snow the pillow gains after it; the melt's own test, free of the
  gauge's catch.

Pass marks, on the median over the winters: melt-out within ±10 days and the spring melt rate within ±25 % (melt
only); January 1 and April 1 water within ±15 % at Stevens Pass and Tahoe City; a daily error no worse than 4.05's
reference model; the game's clock within ±10 % of the hourly play on April 1. A missed mark fails the test and is
listed in the report.

```
dotnet test tools/domain-tests/Tests --filter "FullyQualifiedName~SnowPillowTests"
node tools/snow-pillows/report.mjs test-results/snow-pillows/run.json
```

`MP_SNOW_SITE=<site>` plays one site's pillows; `MP_SNOW_TAG=<name>` names the output (default `run`).

**The season report** (`SnowSeasonReportTests`, explicit, category `SnowReport`) plays every weather site's frozen
rows, as the game ships them, for 20 seasons at the game's clock on open flat ground at the site's base, mid and summit
heights, and writes `test-results/snow-pillows/season.json`. When that file sits beside the pillow data, `report.mjs`
adds the depth through the winter and the first day each height holds 6 in and 12 in (SN-20's cover). A report, not a
gate. Demo 112 runs all three: the rule tests, the pillows, then the season.

```
dotnet test tools/domain-tests/Tests --filter "FullyQualifiedName~SnowSeasonReportTests"
```

**The fix round** (owner 2026-11-13): `fix-round-a1.json` beside the script holds A1's fix round, before and after (4.05's
snow fidelity cells by site with the snow energy judging them, the six pillows, Winter Park's dew depression by hour, the
rain/snow ramp, the Christmas tail and every hypothesis tried). When it is there, `report.mjs` puts it first. It is a
record of that round, written once from the round's test logs; later runs don't change it.
