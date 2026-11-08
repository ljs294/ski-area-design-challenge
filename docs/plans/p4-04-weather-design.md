# Phase 4 · 04 Weather design

**Audience:** the project owner and coding agents. **Status:** ⛳ for the owner's approval. Decided in the design
interview of 2026-11-08 (two rounds by voice, plus a tycoon-fun adversarial review folded in); §15 records every
ruling. **Date:** 2026-11-08. **Plan:** [Phase 4 plan](phase3-13-phase4-plan.md), task 04, as revised by
[P4-02 simulation rules](p4-02-sim-rules.md) §13. **Sources:** the site's record (Daymet, NASA POWER), the MapLibre
game at `maplibre-final` (re-specified, never copied: [archive index](../reference/maplibre-archive.md)), and
[snow rendering design](snow-rendering-design.md).

**⛳** marks a gate: the plan waits there for the owner's approval.

## 0. How to read this document

- **Rules carry IDs** in 4.02's `WX` prefix. 4.02 owns WX-01…08; this document refines the ones 4.02 left to it
  (WX-01 v2, WX-02 v2, WX-03 v2, WX-05 v2, WX-07 v2) and adds **WX-10 onward**. Tests cite the ID
  (`WX33_SameSeedSameYear`). A rule holding a formula has a version; a behaviour change bumps it.
- **Imperial first, metric in brackets.** The simulation computes in its own integer units (WX-31).
- **"(tuned at 4.NN)"** marks a value the named task may change without a rule change; every such value is a named
  `Pacing` constant (SI-01).
- **Who implements what** is §14. 4.05 builds the data and the engine, 4.06 the look and the Weather tab, 4.07 the
  snow that reads it, 4.12 the crowd that reads it, 4.16 the guns, 4.17 the HUD and the evening sheet.

**The design in one paragraph.** The game's weather is a small machine that plays the site's own climate, fast. At
package build the real record (20 years of Daymet days and NASA POWER hours) is boiled down to 36 rows of tuning,
one per third of a month: how often storms come and how big they are, how long the fair spells run, how warm and
how windy, which way storms arrive, how temperature falls with height. At play the machine rolls a season from
those rows and the season's seed, hour by hour, with **weather compressed exactly as time is**: a face day stands
for 2.88 calendar days, so storms come 2.88 times as often per face day, last a third as long and snow three times as
hard, and the season's totals, storm sizes and dry spells come out as the record's. Nothing is replayed and nothing
is quota'd: a dry January happens where the record has dry Januaries. The map shapes each hour (colder and snowier
up high, rain at the base, more snow on faces that meet the storm, cold pooling in the valley on calm clear
nights), from static gradients worked out once. The player sees weather, never the machine: named events (a powder
day, a rain-on-snow, a cold snap), a forecast issued each evening with the day report that firms as the day nears,
a crowd that reads the snow report, and a mountain whose operations can be set to react to the forecast.

## 1. Sources and their terms

- **WX-10, the sources.**
  - **Daymet V4, daily, 1 km** (ORNL DAAC, single-pixel extraction service; no account or key). Variables `tmax`,
    `tmin` (°C), `prcp` (mm/day), `vp` (Pa), `srad` (W/m²), `swe` (kg/m²), `dayl` (s). Coverage North America
    (about 14.5–52 °N, 53–131 °W); 1980 to the latest complete year. Its calendar has 365 days: in a leap year
    December 31 is dropped. Daymet is station-interpolated with elevation, so its daily totals and temperatures at a
    mountain pixel are the best free daily truth we can get. It has no hours and no wind.
  - **NASA POWER, hourly, point** (MERRA-2 based, about 0.5° × 0.625°; no account or key). Endpoint
    `/api/temporal/hourly/point`, at most 15 parameters a request, hours from 2001-01-01, in **local solar time**
    (`time-standard=LST`, the default). Parameters: `T2M`, `T2MDEW`, `RH2M`, `WS10M`, `WS50M`, `WD50M`,
    `PRECTOTCORR`, `ALLSKY_SFC_SW_DWN`, `CLRSKY_SFC_SW_DWN`, `PS` (10, under the cap). POWER owns what Daymet lacks:
    the shape of the day, the timing of precipitation within it, wind, humidity hour by hour and the sky. Its cell
    is far coarser than a ski area and smooths mountains flat, so its totals and temperatures are never used raw.
- **WX-11 v1, the record window (owner).** The **20 most recent complete label years** (October 1 to September 30)
  both sources cover: 20 of every third and 20 whole winters (WX-19), spanning 21 calendar years (Daymet lags POWER
  by a year or so; at a build in 2026 that is likely October 2003 to September 2023). The years are recorded in the
  package manifest; a rebuilt package recomputes. Older years are dropped on purpose: the player is skiing today's
  climate.
- **WX-12, terms and credits.** Both are free open data with attribution required; neither restricts commercial
  use as far as their pages state. The package records, and the Credits screen (P2-07) shows:
  - Daymet: *Thornton, M.M., R. Shrestha, Y. Wei, P.E. Thornton, S-C. Kao, and B.E. Wilson. Daymet: Daily Surface
    Weather Data on a 1-km Grid for North America, Version 4 R1. ORNL DAAC, Oak Ridge, Tennessee, USA*, with the
    DOI and the access date, plus the single-pixel tool's own citation (ORNL DAAC, DOI 10.3334/ORNLDAAC/2361).
  - POWER: *"These data were obtained from the NASA Langley Research Center POWER Project funded through the NASA
    Earth Science Directorate Applied Science Program"*, plus the data reference the project asks for, *"The data
    was obtained from the POWER Project's Hourly 2.x.x version on YYYY/MM/DD"*, with the version the API reports.
  - 4.05 confirms each DOI and version string at fetch and stores what the service returned, never a typed copy.
    POWER *asks* (does not require) to be told of redistribution and publications; whether to email them at release
    is the owner's call (§15 open question O1).
- **WX-13, outside Daymet (owner: US first).** Phase 4 serves US sites. A site outside Daymet still opens: its rows
  are built from POWER alone (POWER's daily sums and extremes as the anchor, lapse from WX-36's defaults) and the
  package is flagged **limited**, with one line on the Weather tab. **Later**, a better free anchor for the rest of
  the world: **TerraClimate** (monthly, about 4 km, 1958 to date, global, no account; its page states no licence, so
  4.05's successor must confirm terms before use) or **ERA5-Land** (hourly, 9 km, global, free but needs a Copernicus
  account). Since the record is calibration, not playback, a coarse monthly anchor is enough (owner: "the resolution
  of data can be a bit bigger").
- **WX-14, where we sample.** Daymet at the **reference pixel** (the one holding the box's centre; its elevation is
  the rows' reference elevation `zref`) and on a **1 km lattice over the box plus its 3 km surround** (up to 7 × 7 =
  49 pixels on a 2 km box, more on a 5 km box, capped at 81), for the elevation regressions (WX-17). POWER at one
  point, the box centre. The surround's 10 m heights (the offline surround buffer) give every pixel its own
  elevation check against Daymet's.

## 2. Blending: one daily-anchored hourly record

- **WX-15, the blended record** (built in Acquisition at package build, off the game's threads). For each real day
  of the window, at the reference pixel:
  - **Temperature:** POWER's hourly `T2M` gives the day's shape; it is stretched and shifted so that the day's
    maximum and minimum equal Daymet's `tmax` and `tmin`. (POWER's absolute level, set by its cell's smoothed
    elevation, is discarded; no lapse correction is needed because Daymet is already at the pixel.)
  - **Humidity:** POWER's hourly dew point, shifted so the day's mean vapour pressure equals Daymet's `vp`, and
    never above the hour's temperature.
  - **Precipitation:** Daymet's `prcp` is the day's total. POWER's hourly `PRECTOTCORR` gives the timing: the
    total is shared over POWER's wet hours in proportion. If Daymet has precipitation and POWER has none that day,
    the total is shared over the day's hours with RH ≥ 85 % (else evenly over 24). If Daymet has none, the day is
    dry whatever POWER says.
  - **Wind and sky:** POWER's `WS50M`/`WD50M` (the free wind above the canopy, nearer to a ridge than 10 m) and the
    clearness index `ALLSKY ÷ CLRSKY` (0–1), as they are.
  - **Snow on the ground:** Daymet's `swe` per day, kept only for the target curve (WX-07 v2).
  - The 365-day calendar (LabelCalendar has no leap day): in a leap year Daymet keeps February 29 and drops
    December 31, and POWER has both. Both sources are **aligned by calendar date, never by index**; February 29 is
    dropped from both, and Daymet's missing December 31 is filled from its December 30. A leap-year fixture tests it.
- **WX-55, blending checks** (4.05's fixture tests): the blended day's max and min equal Daymet's to 0.1 °F; its
  liquid total equals Daymet's to 0.01 in; its mean vapour pressure equals Daymet's to 1 %; no hour's dew point is
  above its temperature.
- **WX-16, missing stays missing.** A suspect or missing POWER hour (fill values) stays missing and is left out of
  every statistic; it never becomes zero rain or calm wind. A day without Daymet is dropped whole. The row records
  its sample counts; a row built from fewer than 60 % of its possible days is flagged in the manifest (4.05).

## 3. Calibration rows: history in, tycoon tuning out

The owner's rule for this section: **the algorithm takes the real weather history as its input and produces tycoon
tuning inputs as its output. There are no quotas.** Nothing forces a count of storms, thaws or dry spells: the rows
describe how this place's weather behaves, and the machine plays dice with them.

- **WX-17 v1, the row.** One row per third of a month (36 rows, October first; LabelCalendar's thirds), built from
  every day of the window inside the third plus 5 days each side (smoothing). All values integers in the units
  given; the bytes are the rows' truth (WX-31). About 0.5 KB a row, 18 KB a site.

  | Group | Fields (units) | Derived from |
  |---|---|---|
  | Climate | mean daily max and min at `zref` (0.1 °F); the 10 % quantile of daily max (0.1 °F, for Cold snap); the 24-hour diurnal shape for fair days and for storm days (‰ of the day's range, 24 + 24 values) | Blended record |
  | Temperature anomaly | its persistence per face hour, φ = φ_real^K, and its innovation spread × √(1 − φ²) (both Q16, 1/65,536, precomputed so the game takes no root or power); its stationary spread (0.1 °F, for the October 1 draw) | Daily mean minus the climate **and minus the state offsets** (residuals, so the variance isn't counted twice) |
  | Fair spells | duration quantiles (real hours: 5, 10, 25, 50, 75, 90, 95, 99 %) | Gaps between storms |
  | Building, Clearing | duration quantiles (real hours, same eight) | WX-18 |
  | Storms | duration quantiles; liquid-total quantiles (0.01 in, same eight); the rank correlation between duration and total (‰); the storm-hour temperature offset (mean and spread, 0.1 °F); the arrival direction as an 8-octant histogram (‰) | WX-18 |
  | Back-to-back | the share of storms that follow a clearing straight into building again (‰) | WX-18 |
  | Humidity | mean dew-point depression by state (0.1 °F) | Blended record |
  | Wind | mean free wind and its spread by state (0.1 mph); gust factor by state (‰); direction histograms for fair and clearing (‰) | POWER |
  | Sky | mean clearness by state (‰) | POWER |
  | Lapse | daytime and night-time temperature lapse (0.01 °F per 100 ft, from the regression of Daymet `tmax` and `tmin` on pixel elevation); dew-point lapse (same, from `vp`); precipitation gradient (‰ per 100 ft, from `prcp` on wet days) | Daymet lattice (WX-14) |
  | Targets | WX-07 v2's rows | Record |
  | Site constants (once per package, not per row) | the reference points' elevations (WX-35); `PowderDayIn` (WX-41) | Terrain; a calibration play (WX-41) |

- **WX-18, finding the states in the record.** A **storm** is a run of hours with ≥ 0.01 in/h liquid, allowing dry
  gaps of up to 3 hours, totalling ≥ 0.05 in (less is a flurry, credited to the state around it). **Building** is
  the run of hours before a storm in which clearness is below 600 ‰ or the wind is rising, capped at 24 real hours;
  **Clearing** the run after it until clearness is back above 600 ‰ and the wind below the third's mean, capped at
  36 real hours; **Fair** is everything else. These four are the only states (WX-23); thaws, refreezes, cold snaps
  and rain fall out of temperature and phase (WX-41). The archive's five air masses and per-month transition matrix
  are dropped (§13).
- **WX-19 v1, the season character table.** For each of the window's 20 winters (October–April, WX-11), two numbers: the
  winter's total liquid as a share of the 20-year mean (‰) and its mean temperature anomaly (0.1 °F). Twenty pairs,
  80 bytes. They keep the record's real spread and its real coupling (cold winters that are also snowy, or not) for
  WX-34. This is the record's year-to-year variety, not a replay: two numbers per year, never its days.
- **WX-20, between thirds.** The machine reads the row of the label date's third, blended linearly with the
  neighbouring third by the date's distance from the third's middle (integer weights), so January doesn't change
  climate at midnight on the 11th.
- **WX-07 v2, the targets (refines 4.02's WX-07).** Per third, for checking, never for forcing: mean temperature;
  total liquid and snowfall (at `zref`); storm count; thaw days (max > 40 °F) and rain days; plus **peak SWE and its
  date** from Daymet's `swe` (owner Q1: base depths and how long you can stay open must look real); and the
  **precipitation-by-elevation** of the Daymet lattice pixels. Acceptance (4.05, 4.07), at `WinterScale` typical:
  over 20 seeds on the fixture, each third's means land within ±15 % of the record for totals and counts and ±2 °F
  for temperature; the natural SWE at `zref` peaks within ±20 % of the record's peak and within one third of its
  date; each lattice pixel's season liquid is within ±15 % of Daymet's. Freezing rain is tracked, not fitted (the
  record can't see the warm layer aloft): on the fixture it happens on 0–3 face days a typical season. A miss is a
  calibration bug, fixed in the rows' derivation, never by a quota.

## 4. Weather runs on the calendar, compressed like time

The owner's ruling (Q17, superseding an earlier size-and-frequency split): *storm frequency over duration; use
really high snowfall rates to make storms feel bigger and more impactful, while not making them linger for days;
because we compress time, we compress weather events accordingly; it is a tycoon now, not a simulator.*

- **WX-21, the synoptic clock.** The face clock's season is 42 face days for 121 calendar days (TM-08), so one face
  hour stands for **K = 121 ÷ 42 ≈ 2.88 real hours** (`Pacing.SeasonCalendarDays ÷ Pacing.SeasonDays`, derived,
  never typed). The weather machine keeps a synoptic clock in integer units: each face hour advances it **121
  units**, and **a real hour is 42 units**, so every duration from the rows (real hours) is converted exactly with no
  rounding drift. States, storms and the temperature anomaly run on this clock; **the diurnal cycle alone runs on
  the face clock** (one night and one day per face day, the sun of WX-04).
  - So a real 36-hour, 14 in storm is a **12½ face-hour, 14 in storm, snowing about 1½ in an hour at its peak**
    (a short, wet 24 in dump reaches 3 in an hour and more); a real
    5-day fair spell is under 2 face days; a 3-week January drought, where the record has one, is a week of face
    days. Storm sizes, season totals and dry spells are the record's; only their speed changes.
  - The same K holds before and after the season (October, November, summer: TM-14's days nobody skis), so the
    weather year is one clock.
- **WX-22, what else runs at K (cross-task, supersedes 4.02 §2's "snow, water and money are tuned to the game's
  own days").** Anything that integrates weather over time must integrate over the same calendar time, or the
  season's base depth and melt-out go wrong. Per face hour, ×K: **compaction (SN-08), melt in all three terms
  (SN-09), refreeze and holding water (SN-10), sublimation (SN-12), age in the quality term (SN-16, SN-18), lake ice
  growth and melt (LK-*, 4.09), stream and catchment inflow (WS-03, WS-04), evaporation (SI-10's water ledger)**, and
  (owner's Q19 left as guessed) **gun output and the water it draws (SG-05, SM-03)**: a face hour of a gun is K real
  hours of its snow, so the player's cold hours buy the share of the mountain they buy in real life.
  `Pacing.SnowmakingTimeScale` (default K) is 4.16's to tune. **SI-10's operations ledger follows its source:**
  gun-hours, compressor-hours and pump energy go ×K with the guns and pumps, so snow per gallon, per gun-hour and
  per kWh in the day report read as real (a test checks they don't change with K); groomer-hours, lift-hours and
  lift rides stay ×1 (motion clock). **Not ×K:** anything on the motion clock (skier passes, wear, moguls, grooming
  passes, lift rides) and anything per event (a groomer pass per night). Each owner
  task re-versions its rule when it builds it (SN-09 v2, SG-05 v2, …); none is a format change. SM-07's acceptance
  numbers get re-derived by 4.16.

## 5. The generator

- **WX-01 v2, the states (refines WX-01).** Four states, cycling **Fair → Building → Storm → Clearing → Fair**,
  each held for a duration drawn from its row's quantiles (WX-17) on the synoptic clock. Two shortcuts come from the
  record: a Clearing goes straight to Building with the row's back-to-back share (storm trains), and a Building may
  be zero hours long (a storm out of a fair sky). 4.02's "thaw" and "refreeze" are no longer states: a thaw is a warm
  stretch and a refreeze its night (WX-25), and both are named events when they happen (WX-41). "Storm cycles
  2–3 face days" becomes "cycles as the record has them, compressed" (WX-21).
- **WX-23 v1, a state change.** When a state's synoptic units run out, the next state starts **with the leftover
  units carried** (a negative remainder shortens it), and its duration is drawn then; several changes may happen
  inside one face hour, and the hour reports the state it ends in plus any liquid from storm units inside it. So
  short states keep their real length on average and the storm count isn't biased by rounding (tested against WX-07
  v2's storm count). A **Fair** spell's duration is the record's own gap between storms for the third (WX-17), ÷
  √share of the season character (WX-34): long dry spells come only where the place has them. Durations are drawn in
  real hours and converted (WX-21).
- **WX-56, the start of the weather year.** At **00:00 on October 1** of every label year (and when a new game's
  clock starts, at October 1's opening), the generator is re-initialised from that season's seed alone: Fair, with a
  keyed duration; the anomaly drawn from the row's stationary spread; pool 0; the season character drawn (WX-34). No
  state carries from the previous September, so year N's weather never depends on year N − 1's seed. A new game
  starting at the opening runs the first hours of October 1 from midnight, so it gets the same hours a played year
  would. **A seed changed mid-year** (a dev pin, or the resort seed changing in `SetDesign`, SI-15) re-runs the
  site-level generator from that October 1 under the new seed to the current face hour (≈ 3,000 hours × < 0.01 ms),
  so the hour after the change is the hour that seed would have given.
- **WX-24 v1, a storm.** When Storm starts it draws, at once (so the forecast can know them, WX-05 v2):
  - its **duration** and **liquid total** from the row's quantiles, with the row's rank correlation (a long storm is
    usually a big one), times the season character (WX-34) and `Pacing.WinterScale`;
  - its **temperature offset** (warm storm, cold storm) from the row's storm offset and spread;
  - its **arrival octant** from the row's histogram, and a direction within it;
  - its **shape**: the liquid falls over the storm's face hours in a ramp (first quarter), a peak (middle half) and a
    tail (last quarter) in integer weights 1 : 2 : 1 per hour, so the peak is 4/3 of the storm's mean rate; a storm
    of 3 face hours or fewer falls evenly. The face-hour rate is K times the real rate (WX-21), capped at
    `Pacing.MaxLiquidPerFaceHour = 0.40 in` of liquid (3–7 in of snow by SN-05's ratios). Excess over the cap moves
    to the storm's lightest hour still under it, and if every hour is capped the storm is lengthened by whole face
    hours; the drawn total is always what falls, and is what the forecast talks about.
  - **Clearing** carries the record's flurries: the row's share of liquid that fell in clearing hours, spread evenly
    over them. Fair and Building are dry.
- **WX-25 v1, temperature at `zref`.** Each face hour: the third's climate (WX-20) on the diurnal shape for the
  state (fair days swing their full range; storm days a damped one) + the slow **anomaly** + the state's offset (a
  storm's own, WX-24; Building warms, Clearing cools, from the row) + the season's offset (WX-34). The anomaly is a
  persistent random walk stepped once per face hour (anomaly ← φ × anomaly + innovation × keyed normal draw, with
  the row's Q16 coefficients already raised to K, WX-17, so the game does no root or power). Thaws (a warm anomaly meeting a fair
  afternoon), cold snaps (a cold anomaly after a clearing) and January rain all come from here.
- **WX-26, dew point at `zref`:** temperature minus the state's depression (row), never above the temperature. Near
  saturation in storms, dry in fair weather.
- **WX-27, the sky.** Clearness (‰) by state from the row with a keyed hourly wobble; **cloud cover** (%) is
  1 − clearness, shaped; **cloud base** above `zref` = **228 ft per °F** of dew-point depression (the lifting
  condensation level, 125 m per °C; a test pins 10 °F → 2,280 ft) so a storm's deck sits on the summit and a clearing
  lifts it; **visibility** (mi) from the precipitation rate
  and phase (heavy snow ¼ mi and under) and fog (WX-29).
- **WX-28, the free wind.** Speed by state from the row with a keyed hourly draw; direction: a storm's own (WX-24),
  else the state's histogram (fair days light and variable, clearings from the record's clearing quadrant); the
  **gust factor** by state (stormier is gustier) × a keyed hourly ±10 %.
- **WX-29, the cold pool.** One number per hour, the **pool strength** (0.1 °F at the valley floor). It builds by
  `Pacing.PoolBuildPerHour` in each night hour that is clear (clearness > 700 ‰) and calm (free wind < 8 mph), up
  to `Pacing.PoolMax = 8 °F` (tuned at 4.05); after sunrise it burns off over 2 face hours; under cloud it fades
  slowly (half each 6 face hours), and it is scoured in an hour only when the **wind at the valley floor** (the free
  wind × the sheltered exposure factor, WX-39: 0.4) exceeds 15 mph, i.e. a free wind above about 37 mph. Because it fades slowly
  under cloud, a warm storm arriving after a cold calm spell rides over cold valley air: that is how freezing rain
  happens (WX-38). When the pool is strong and the depression is under 2 °F the valley floor has **fog** (for 4.06).
- **WX-30, the hour's parameter set** (the contract WX-02 needs; one per face hour, site-wide, about 40 bytes):
  label third weights, state, temperature and dew point at `zref`, the lapse coefficients for the hour (day or night,
  WX-36), pool strength, liquid rate at `zref`, the storm's arrival direction, free wind speed, direction and gust
  factor, clearness, cloud base, visibility, a fog flag, the season offset. Everything over the map (§6) is a pure
  function of this set and a cell's static inputs.
- **WX-31, integers and keyed draws (owner Q11).** The generator works entirely in integers: tenths of °F,
  hundredths of an inch of liquid, tenths of mph, per-mille shares, the synoptic units of WX-21. Every random number
  is a keyed hash (SI-06) of (table version, season seed, tag, face hour or storm ordinal, ordinal), turned into a
  draw by **inverse lookup in integer quantile tables** (the rows' quantiles, and one fixed 256-entry standard-normal
  table in the code); no `Math.Exp`, `Pow`, `Sqrt` or trig on the generator's path. Transcendental work happens once,
  at calibration, and is stored in the rows. New `Simulation` tags (registered in 4.05, never reused): weather state,
  storm, anomaly, wind, sky, forecast, season character, almanac. Display rounds to whole °F, snow to 0.1 in and
  liquid to 0.01 in (WX-49).
- **WX-32, the state and the save.** The generator's state is a few dozen integers: state, synoptic units left,
  the current storm's draws and its hour index, the anomaly, the pool strength, the season character, the label year.
  It is saved in the sim sidecar (SI-12; 4.05 takes the `SidecarVersion` bump through the coordinator). The forecast
  is not saved: the sidecar keeps the face hour of the last issue, and the issue is recomputed exactly by re-running
  the generator from October 1 to that hour (WX-56) and ahead; a test saves at 10 PM, reloads and finds the forecast
  fields byte-identical.
- **WX-33, same seed, same year (WX-08 kept).** The weather is a function of (the package's rows, the season seed,
  the face hour) and nothing else: never the design, the snow, the skiers, the speed or a skip. A golden in
  `Tests/Core` generates a whole label year of hour sets from fixture rows and a seed, from WX-56's reset, and pins
  the hash; being
  integer, **it is byte-identical on CoreCLR, Mono and IL2CPP** (unlike SI-08's field goldens). The rows' bytes come
  from the package, whose id and manifest version are in the replay header (SI-22).
- **WX-34 v1, the season's character.** At October 1 of each label year the season's seed (SI-15 v2) picks one of
  the record's 20 winters from WX-19 and adds a small keyed jitter (±10 % liquid, ±1 °F), the share clamped to the
  record's own lowest and highest: storm totals are × √share and Fair spells × 1/√share (so both size and frequency
  carry half of it; √share is a Q16 table value from calibration), and its anomaly is the season's temperature
  offset. So a lean winter and a big winter both happen, as often as the place has them,
  and a second season is a different winter. The character is never shown as a number; the Almanac hints at it
  (WX-48) and "season so far vs normal" reveals it (WX-46).
  - **`Pacing.WinterScale` (lean / typical / big)**, owner Q8: lean = liquid × 0.75 and +3 °F, big = × 1.25 and
    −3 °F (more of the precipitation falls as snow). **`Pacing.ForecastSkill` (sharp / normal / poor)** scales WX-05
    v2's errors × 0.6 / 1 / 1.6. **Both are scenario data and dev switches only (TM-16): no menu, no player control of
    the weather** (owner Q16). They apply on top of the character; WX-07 v2's acceptance is at typical only.

## 6. The weather over the map

All of this is the shared pure function `WeatherAt(hourParams, staticCell)` of WX-02, evaluated once per face hour
into tables, never stored per cell.

- **WX-35 v1, the static inputs, from the package's terrain only.** Computed once per package (at build, or at
  first open, cached in the terrain cache; engine-free) from the downloaded heights, **never from the player's
  earthwork**, so no design can move the weather (WX-08) and no pit can be dug to farm a warmer night:
  - the **reference points**: base = the 10th percentile of the box's elevations, summit = its 95th, mid = halfway,
    each with its `hvf` and exposure; named events, alerts, the crowd's reads and the forecast's base/mid/summit
    lines use these, never lift positions;
  - per 8 m cell, **height above the valley floor** (`hvf`): the cell's elevation minus the lowest within 1 km (a
    running-minimum filter), in 1 byte at 10 ft steps (0–2,550 ft, continuous for the pool's profile);
  - per cell, the **exposure class** (8 classes) from the package heights' ridge exposure (SN-01's 7 × 7 rise, on
    the untouched terrain) and `hvf`: valleys sheltered, ridges and summits exposed;
  - on a **coarse 500 m grid over the box and its surround** (WX-02's ≥ 500 m; ≤ 576 cells, 22 × 22 for a 5 km box),
    per arrival octant, the **orographic factor**: from the upwind terrain within 3 km (the surround buffer), cells
    the storm climbs into get more (up to +40 %), cells in the lee of a higher ridge less (down to −30 %), then each
    octant is **normalised so its mean over the Daymet lattice pixels is 1.0**: the elevation gradient (WX-38),
    regressed on the same pixels, carries "higher gets more", and this factor only moves snow from lee to windward
    faces, never adds any. This is the windward-face effect the owner asked for. A whole region's snow (a peak that
    catches every storm off a lake 50 km away) is already in Daymet's totals.
- **WX-36 v1, temperature at a cell.** `T(z) = T(zref) − lapse × (z − zref) − pool`, where the lapse is the row's
  daytime regression by day and its night-time one by night (a well-mixed storm uses the daytime lapse at any hour),
  and `pool = strength × (p(hvf) − p(hvf_ref))` with `p(h) = max(0, 1 − h ÷ Pacing.PoolDepth)` and
  `Pacing.PoolDepth = 500 ft`: measured against the reference pixel's own height above its floor, so the pixel Daymet
  describes keeps Daymet's temperature (Daymet's night-time regression already holds the average inversion; the
  pool adds the night's own), deeper cells are colder and cells above a pooled reference are warmer, as under an
  inversion. Base temperatures are checked by WX-07 v2. Defaults where a row has no lattice (a limited site): 3.6 °F per 1,000 ft by day (the standard
  atmosphere), 2.0 by night. Clamped to 1.0–5.5 °F per 1,000 ft.
- **WX-37, humidity and wet bulb.** Dew point at a cell = dew point at `zref` minus the row's dew-point lapse,
  never above the cell's temperature; RH from the two; **wet bulb by Stull's formula** (kept, WX-02) from T and RH,
  computed once per hour per table entry, so its `atan` and `sqrt` never run per cell (SI-09). Stull is fitted for
  −4 to 122 °F and RH 5–99 %; outside it the inputs are clamped (RH ≥ 99 % gives wet bulb = air; below −4 °F the wet
  bulb is the air temperature minus the depression Stull gives at −4 °F and the same RH, never above the air), and a
  test checks the function against psychrometric values at −10, 0, 20 and 30 °F. Stull ignores pressure, so at
  10,000 ft it reads up to about 1 °F warm in dry air: the guns are a touch conservative up high. A pressure-aware
  wet bulb is a Later line.
- **WX-38, precipitation at a cell.** `rate × (1 + gradient × (z − zref)) × orographic(octant, coarse cell)`, the
  gradient from the row (clamped 0–20 % per 1,000 ft), the orographic factor bilinear between coarse cells and blended
  between the two octants nearest the storm's direction.
  - **WX-03 v2, phase (refines WX-03):** by the cell's wet bulb, **snow at ≤ 30.2 °F, rain above 33.8 °F, mixed
    between with the snow share falling linearly**. **Freezing rain** when the cell's wet bulb *without* the pool is
    above 33.8 °F (it would rain) but its air *with* the pool is at or below 32 °F: warm air riding over cold valley
    air, the real mechanism. Then the whole hour's liquid is freezing rain (liquid that glazes on contact, for 4.07),
    whatever the pooled wet bulb says. (4.02's WX-03 test, air < 32 °F and wet bulb < 35.6 °F, overlapped the snow
    rule: since the wet bulb is never above the air, it fired on every sub-freezing hour.)
  - So the **snow line** is wherever the wet bulb crosses 30–34 °F on that hour: rain at the base and a foot on top
    is one storm, and investing in snowmaking low and running thinner up high (owner Q6) is the strategy it teaches.
- **WX-39, wind at a cell.** An integer product per cell: free wind × the exposure class factor (an 8-entry table,
  0.4 in a deep sheltered valley to 1.6 on an exposed summit; tuned at 4.05) × the coarse grid's lee factor for the
  hour's direction (lee cells × 0.7) × 0.6 under dense canopy (the canopy's only effect on the air); gust = speed ×
  the hour's gust factor. **Top-station gust** for LO-11 is the function sampled at each lift's top station.
  POWER's smoothed wind runs low in mountains; `Pacing.WindScale` (default 1.0) is 4.05's to tune against the
  owner's sense of how often a fixed-grip holds.
- **WX-40, shortwave at a cell.** Clear-sky shortwave from the sun's elevation (WX-04, one shared Domain function,
  float, once per hour) × the hour's clearness, onto a horizontal surface; SN-09's slope factor does the rest. Kept
  out of the integer generator because it has a sine in it.
- **WX-02 v2, the tables (refines WX-02).** Each face hour the field step fills tables over **(elevation bin ×
  pool band)** for temperature, dew point, RH, wet bulb and phase share: elevation bins of 10 m over the package's
  real range, up to 256 bins (coarser bins only past 2,560 m of relief), and 16 pool bands (`hvf` 0–500 ft in 32 ft
  steps, plus one for above: the pool is the only way `hvf` touches the air): ≤ 4,096 entries, so Stull runs at most
  4,096 times an hour. Precipitation and the lee factor come from a per-coarse-cell table (≤ 576 × 2 octants);
  wind is WX-39's per-cell product. **Aspect leaves the air tables** (4.02's WX-02 had an aspect octant): aspect
  changes the sun on the snow, not the air, so it enters only through SN-09's sun factor; canopy enters only the
  wind (WX-39) and SN-06/SN-09. Guns, lifts, the HUD and the forecast sample the same function at points.

## 7. Storms and events: what the player can name

The machine's states are hidden (owner Q4). What the player sees, hears about in alerts and reads in the day
report and season history are **named events**, each detected from the hour's fields at the **fixed reference
points** (base, mid and summit from the package's terrain, WX-35), never from the hidden state and never from lift
positions, so a name is always honest and the same seed names the same weather whatever the player builds (WX-08).
The lift windows and the HUD may also show the weather *at the player's lifts* (point samples of the same function),
but no event, alert, crowd line or automation reads those.

- **WX-41 v1, the named events.** Every window and threshold is in **face time**, chosen for the compressed weather
  (WX-21), and those that measure snow are set from the site's own record, not a fixed number. The weather step
  emits the site events; the snow step (4.07) emits the ones that need snow or trails, after SN-05 (SI-05's order).

  | Event | When (face time) | By | What it does to the game |
  |---|---|---|---|
  | **Storm** | ≥ 0.05 in liquid in 2 face hours at the summit, snow phase there | weather | Snow banner; visibility down |
  | **Big storm** | the storm's `zref` total drawn by WX-24 is above the row's 75 % storm-total quantile | weather | Alert; a storm the region talks about |
  | **Storm of the season** | the season's largest storm so far, when above the row's 95 % quantile | weather | Named in the season history |
  | **Powder day** | the overnight snow at mid-mountain (from yesterday's stop to the 7:30 landing) ≥ `PowderDayIn` | weather | The crowd line (WX-44); banner |
  | **Bluebird** | a day with clearness > 800 ‰ from opening to stop, within one face day of a storm's end | weather | The crowd line; the look |
  | **Rain** | rain or mixed phase at the base for ≥ 2 face hours | weather | Crowd line; alert from the forecast |
  | **Freezing rain** | WX-38's freezing rain at the base | weather | Alert |
  | **Rain on snow** | rain on snow-covered open trail cells | 4.07 | Trails go wet; the rain arc (WX-52) |
  | **Glaze** | freezing rain on open trail cells | 4.07 | Ice; the rain arc |
  | **Thaw** | mid-mountain air > 34 °F for ≥ 3 face hours, December to February | weather | Snow goes wet (4.07) |
  | **Refreeze** | after a thaw or rain, mid-mountain < 28 °F for 2 face hours | weather | "Icy" on the report; SN-14 rule 4 |
  | **Cold snap** | the day's `zref` high below the row's 10 % quantile of daily max | weather | Crowd line; guns at full stage |
  | **Wind event** | summit gust ≥ `Pacing.WindEventGust = 45 mph` | weather | Holds (LO-11); drift (SN-07) |
  | **Inversion** | base colder than mid by ≥ 4 °F | weather | A tag on the hourly strip (WX-46) |
  | **Corn morning** | SN-14 rule 5's spring cycle on a trail | 4.07 | Spring banner |

  **`PowderDayIn`** is a site constant set at package build (4.05): the generator plays the rows for 20 seeds and
  picks the overnight amount (never below 6 in) at which powder mornings come as often, as a share of season
  mornings, as the record's days with ≥ 6 in of new snow come as a share of its winter days. So a powder day is as
  special at a dry mountain as at a snowy one, and always at least six inches. Acceptance: over 20 seeds the share
  matches the record's within ±25 %.

- **WX-42, alerts (extends TM-12; tuned at 4.17).** From the *forecast* (WX-05 v2), on the morning or evening it
  first becomes firm (lead ≤ 2 face days): Big storm coming, Rain coming, Freezing rain possible, Thaw coming, Cold
  snap coming, Wind event coming (with the lifts above their hold line named). Each has TM-12's stable id, a dedupe
  key of **(alert type, target face day)** and a cooldown of one face day, so a storm the forecast keeps showing
  alerts once. 4.02's `Pacing.StormAlertIn = 6 in` is retired: "big" is relative to the site's own storms (a
  Jackson storm and a Vermont storm aren't the same size). From the *truth*, as it happens: Rain on snow, Glaze,
  Wind holds (LO-11), keyed (alert type, face day). Critical ones pause per TM-12.
- **WX-52, rain as a crisis arc.** Rain is the best crisis in the game, so it gets a beginning, a middle and an end:
  the alert two days out (time to push guns on the low trails tonight), the event on the day, and **attribution** on
  the trail window and the day report ("Icy since the rain on Day 18 · groomed once · one more grooming to Loose
  granular"), so the player can see the recovery. 4.07 and 4.17 build it: each trail keeps the id of the event that
  last changed its type, and the recovery line reads SN-14's transitions. Test: on the fixture, a scripted rain then
  refreeze leaves an open trail Icy with the rain's event id and day, and two groomings bring it to MG.

## 8. The forecast

- **WX-05 v2, the forecast (refines WX-05; owner Q7 and Q12).** **Issued once a face day at the resort's stop**
  (TM-02 v2, 4:30 by default; on days nobody skis, at 4:30), with the day report (TM-15): one **evening sheet** the
  player reads in the 4-minute night. It holds:
  - **tonight, hourly**: temperature and wet bulb at base, mid and summit, and the **gun window** (hours ≤ 27 °F
    wet bulb by elevation band, SM-02's AutoOn);
  - **tomorrow, hourly**: temperature, sky, snow and rain, wind at the summit;
  - **days 1–7**: high and low at base and summit, chance of precipitation, snow at summit and base as a range, the
    snow line's elevation, wind and gusts, the named events expected.
  - It is **refreshed silently at the morning landing** (TM-06 v2's 7:30) for the HUD; the refresh is the same
    forecast with the lead a little shorter, so it shifts a little, never jumps.
  - "Tonight" is from the stop to the next opening, so it follows the player's hours (LO-07 v2), but nothing the
    forecast *says* does: its truth and its errors are keyed by the **target face day and the lead in face hours from
    the issue**, never by the resort's hours, and every reference-point line uses WX-35's fixed points. On days
    nobody skis the issue is at 4:30 and the refresh at 7:30.
  - **How it is made:** the issue runs a copy of the generator ahead 7 face days from its current state (cheap: the
    site-level machine only) to get the truth, then adds keyed error. **Error grows with lead and shrinks as the day
    nears:** each target day draws one error once (by the target day's key), and the lead scales it, so tomorrow's
    forecast is the same story told more sharply each evening, never a new story. Temperature ±2 °F a day out to
    ±8 °F at a week (WX-05 kept). A storm's **day** is firm 3 days out (before that it may be shown a day early or
    late, drawn once per storm); its **size** carries an error drawn once per storm, ±60 % at a week, ±40 % at 3 days,
    ±20 % a day out (about one in twenty busts beyond that: a forecast can still be wrong the evening before).
    **Chance of precipitation**: about 60 % for a real storm a week out, **90 % at 3 days** (owner), 95 % the
    evening before. **Phantom storms** may appear at 4–7 days out (keyed, at a third of the row's storm rate) and
    fade by day 3. `Pacing.ForecastSkill` scales every error (WX-34). There is no omniscient mode.
  - Shown as **ranges** ("10–20 in", "Thursday or Friday", "high 18–26 °F"); chance of precipitation as a
    percentage on the day cards, as a weather report does.
  - The forecast's own errors are integer draws (WX-31), but its samples (wet bulb, gun window) go through WX-37's
    float function, so like the fields it follows SI-08 (player-build goldens), not WX-33's cross-runtime one. It
    runs in the night's first slice (BG-02's 2 ms night slice, with the hour step's own work in later slices).
- **WX-54, forecast skill acceptance** (4.05, over 50 seeds on the fixture, `ForecastSkill` normal): temperature
  error's spread 2 ± 0.5 °F a day out and 8 ± 1.5 °F at a week; chance of precipitation for real storms 60 ± 10 % at
  a week, 90 ± 5 % at 3 days, 95 ± 3 % the evening before; storm day wrong on ≤ 5 % of storms at 3 days; storm size
  outside ±20 % on 5 ± 3 % of storms the evening before; phantom storms none at ≤ 3 days.
- **WX-43, who reads the forecast (owner Q14: "all of these systems will need to read the actual weather and the
  forecast so the user can automate accordingly").** The physics never reads it (WX-06 kept: snow, lakes and guns
  step on the truth). The **player's automation** may, as a player would, because the forecast is part of the
  simulation's state and is itself a function of the seed (WX-08 holds). Phase 4 offers:
  - **Grooming (4.07, SN-22 v2):** a new setting, *Groom unless it's snowing tonight* (skip the night's grooming
    when tonight's forecast snow at the trail ≥ `Pacing.HoldForPowderIn = 4 in`), so powder lands untouched; the
    player can still order or hold a trail by hand for one night. (A design-shape bump, 4.07's, via the coordinator.)
  - **Guns (4.16):** the trail priority is the player's lever ahead of a thaw or rain; the automatic rules read
    tonight's gun window to stagger starts (SM-02's details are 4.16's). Guns still start and stop on the truth.
  - **Trails (4.07):** Auto opens by readiness; the trail window shows the forecast melt beside it so the player can
    hang on or close early by hand.
  - **Lifts (4.11):** the hold is physics (LO-11); the lift window shows tomorrow's forecast summit gusts against
    the lift's hold line. A per-lift "slow" or "close ahead of the gusts" is Later.
  - Each consumer reads **named forecast fields** from the snapshot (tonight's gun window by band, tonight's and
    tomorrow's snow by band, tomorrow's summit gust, the next thaw's day), never the generator.

## 9. The crowd reads the weather

- **WX-44, handed to 4.12 (GU-04 v2; owner Q13 left as guessed).** GU-03's conditions line grows two sub-lines,
  shown in the Guests tab's stack:
  - **Snow report:** the overnight snow at mid-mountain (WX-41's window, from yesterday's stop to the 7:30 landing;
    GU-04's 6:00 read moves there, so there is one morning number): × 1.0 at 0 in, rising to × 1.5 at twice
    `PowderDayIn` and flat above (`Pacing.PowderPull`, tuned at 4.12). Powder days fill the mountain the morning
    after the storm, as they do.
  - **Today's weather, as forecast:** read from the forecast the crowd saw the evening before, not the truth: rain or
    mixed at the base × 0.5, a storm with summit gusts above 40 mph × 0.7, bluebird × 1.1, a cold snap × 0.7 (tuned at
    4.12). So a busted forecast is drama: an empty mountain under ten inches of fresh, or a mob on a day that turned
    to rain.
  - The day report names the cause ("Powder day: 1,840 came on a weekday").

## 10. What the player sees

- **WX-45, the HUD weather bug (owner Q10; 4.17 builds it, mockup first).** The existing weather bug in the status
  bar shows: the sky icon and temperature now (base · summit, WX-35's reference points), **new snow overnight**
  (the snow report's number, WX-44), **snowmaking tonight** ("Good
  from 9 PM" / "Too warm below 8,000 ft" / "No window"), **wind** with a warning when any open lift's top gust is
  within 5 mph of its hold line, and the current named event as a banner. Humidity, shortwave, cloud % and
  confidence percentages stay off the HUD. **Clicking the bug opens the Weather tab** under Analysis.
- **WX-46, the Weather tab: the command centre (4.06 mockups it first, 4.17 wires it).**
  - **The evening sheet** (WX-05 v2): tonight and tomorrow hourly (base, mid and summit lines, with an *Inversion*
    tag where base is colder than mid), and the 7-day cards.
  - **Season so far**: snowfall to date at summit and base against the normal for the date, the named events of the
    season, and the past days (from SeasonHistory, TM-15).
  - **Climate (owner Q15: "monthly temps and expected precip and that's it; keep 'em guessing")**: per month, the
    normal high and low and the normal precipitation (liquid and its snowfall at `zref`). Nothing else: no snow-line
    table, no wind rose, no hold-hour estimates. The player learns the mountain by living on it.
  - The Almanac card (WX-48) in summer; the "limited data" line on a WX-13 site.
- **WX-47, two weather info layers (owner Q18; 12b's rules: display only, exclusive).** **Temperature**: a heat map
  of the hour's air temperature over the map, its ramp breaking at 32 °F so the freezing line shows by itself.
  **Wind**: speed as a heat map with direction arrows on a coarse lattice. Both are drawn from the static inputs
  (elevation, `hvf`, exposure, as textures) and the hour's parameter set in a shader, refreshed once per face hour:
  no per-frame CPU and no per-cell weather (WX-02). 4.06 builds them.
- **WX-48, the Almanac (owner Q16).** Each summer month of the label year, an in-game almanac (a fictional title,
  never a real almanac's name, as with lift makers) prints one line about the coming winter in almanac voice ("a
  long, white winter, with a thaw in the new year"), never a number. The next season's seed is already known in
  summer: the almanac reads `DrawSeasonSeed(resort seed, season number + 1)` (SI-15, as 4.03 built it) and that
  seed's WX-34 character, with a keyed noise: **right about two times in three** (owner's Q20 left as guessed), so it
  is worth reading and still keeps them guessing. A dev pin (SI-15 v2) is ignored by the almanac, which may then be
  wrong about a pinned winter; players never pin. Lines may disagree from month to month; that is the charm. Test:
  over 300 resort seeds the line matches the drawn character's class 67 ± 5 % of the time.
- **WX-49, units.** Display: whole °F, wind in whole mph, snow to 0.1 in, liquid to 0.01 in, elevations to 10 ft;
  metric in brackets where the unit setting asks. Internals per WX-31.
- **SI-15 v2, the season seed (owner Q16: no player control of the weather).** Each season draws its own seed (a
  keyed hash of the resort seed and the season number, as 4.03 built it). The seed is **not shown or typeable in the
  player's UI** and there is no "replay this winter": it stays in the save, the replay header and the problem-report
  zip, and a dev switch (`-seasonseed <n>`, and 4.03's `PinSeasonSeed` command) pins it for tests and tuning.

## 11. The storm as the player lives it (the brief for 4.06)

The weather has to look and feel like weather: the build-up, being in the storm and the clearing are the most
exciting thing the mountain does, and the player should feel them (owner). 4.06 owns the shaders, particles and
sound; this section is what the weather *is*, so the picture can be true to it. The snapshot carries the hour's
parameter set (WX-30) and the samples at base, mid and summit; the view eases between hours for the eye (WX-02),
the simulation steps.

- **WX-50, what the eye gets each hour** (in the snapshot, SI-16): cloud cover and **cloud base** (ft), **visibility** (mi), precipitation
  rate and phase at base, mid and summit (so the snow line shows as falling snow turning to rain partway down),
  wind speed, gust and direction (trees, plumes, blowing snow off ridges when > 20 mph and fresh snow lies), fog in
  the valley (WX-29), clearness (light and shadows), the named event, and fresh-snow age from 4.07 (sparkle).
- **WX-53, the arc, per event** (4.06's acceptance: the style tile's winter week, task 10, shows each of these
  from a scripted seed, and the owner signs the look):
  - **Before a storm (Building):** high cloud thickening, the light going flat and grey, the wind rising and turning
    to the storm's direction, the deck lowering toward the summit. The evening sheet has said it's coming.
  - **Storm:** snow streaming in the wind, heavy enough at the peak (1–3 in an hour in a big storm, more in a short dump; WX-24) that the far ridge goes
    and the lift towers fade into it; gun plumes bent flat; at the snow line, flakes turning to rain lower down.
  - **Big storm:** the same, harder, longer; the banner and the alert make it an occasion.
  - **Clearing:** the deck breaking from the upwind side, shafts of sun, snow blowing off the ridges.
  - **Bluebird / Powder day:** deep blue sky, hard shadows, the fresh snow sparkling, the crowd arriving.
  - **Thaw and rain:** wet grey light, a low ceiling, dripping trees, the snow going dull; rain hissing on the base.
  - **Cold snap:** crisp, sharp and still; gun plumes standing tall; sea smoke over open water.
  - **Inversion night:** fog pooled in the valley under clear stars, the summit above it.
- Budgets are 4.06's (the frame's, demo 41); nothing here asks for per-frame simulation work.

## 12. Budgets and determinism

- **WX-51, budgets** (inside BG-02's 40 ms hour step and BG-08's memory, measured by the harness, SI-23):
  generator step < 0.01 ms per face hour; WX-02 v2's tables (≤ 4,096 entries + ≤ 576 coarse cells) < 1 ms, the
  first slice of the hour step; the forecast issue (168 face hours of the site machine, samples at the 3 reference
  points and, for display only, up to 64 lift tops) < 2 ms once a face day, in the night's first slice; a mid-year
  seed re-run (WX-56) < 50 ms; static inputs (WX-35) < 1 s per package, cached. Memory: rows 18 KB, `hvf` and
  exposure 1 B each per 8 m cell (≤ 4 MB), the orographic grid 8 × 576 B; 4.05 adds these static inputs to BG-08
  (v3) beside the snow fields. **No per-cell weather is stored.** 0 B allocated per tick or per hour (preallocated tables).
- **Determinism:** the generator is integer (WX-31) and its golden is cross-runtime (WX-33); the tables and
  everything downstream follow SI-08 (player-build goldens). Weather never reads the design (WX-08), so building
  can't farm a storm. Skips run every face hour (SI-11), so a skipped night has the same weather as a played one.

## 13. The MapLibre ledger

The archived game (`weather-engine/`, `weather-service/` at `maplibre-final`) compiled a month-level climate and
simulated hours from it. Each number or idea: **K** kept, **C** changed, **D** dropped.

| MapLibre | | Here | Why |
|---|---|---|---|
| Daymet daily totals as anchors, POWER (MERRA-2) for hourly structure | K | WX-15 | The right split |
| Missing hours stay missing | K | WX-16 | |
| Climate compiled per month (12 models) | C | Per third of a month, 36 rows, blended (WX-17, WX-20) | 4.02's thirds; no steps |
| Five macro air masses (arctic, continental-polar, maritime-polar, warm-wet, frontal) with a Markov matrix | D | Four states with record durations (WX-01 v2, WX-18) | Cold, warm and rain come from temperature; the smallest machine that emits the events |
| Gamma-distributed hourly precipitation | C | Storms drawn whole: duration, total, shape (WX-24) | A storm is the unit the player and the forecast talk about |
| Storm detection with 3-hour dry gaps | K | WX-18 | |
| Real-time weather (one hour per hour) | C | Compressed with the calendar, K ≈ 2.88 (WX-21) | Owner: frequency over duration |
| Lapse 6.5 °C/km (3.6 °F/1,000 ft) everywhere | C | Per third, day and night, from the Daymet lattice; 3.6 is the default (WX-36) | The site's own lapse |
| Cold pooling 1.5 °C when clear and calm | C | Pool strength state, up to 8 °F, by height above the valley floor (WX-29, WX-36) | Matters to guns; cheap |
| Stull wet bulb | K | WX-37 | |
| Phase thresholds −1 / 1 / 2 °C wet bulb | C | WX-03 v2: snow / mixed / rain kept; freezing rain by the pool | The old test could never fire |
| Fixed snow ratio 10 | D | SN-05's SLR by wet bulb | 4.02 |
| Gust factor per month ± 0.08 noise | C | By state, ± 10 % keyed (WX-28) | |
| High wind hazard 40 kph / 60 kph gust | C | Wind event at 45 mph summit gust (WX-41) | Ties to the fixed-grip hold |
| Nine difficulty multipliers | C | Two: Winter, Forecast skill, as data only (WX-34) | Owner: no player control |
| 168-hour forecast, hourly noise per lead | C | 7 days, error drawn once per target day and scaled by lead (WX-05 v2) | Forecasts firm up instead of jittering |
| Historical replay seed | D | Generated only (WX-01) | 4.02 |
| A live weather service | D | Built into the package at download (4.05) | Offline after download |

## 14. Who implements what, contradictions, formats, Later

| Task | Rules |
|---|---|
| 4.05 Weather data and engine | WX-10…20, WX-07 v2's check, WX-21, WX-23…40, WX-54…56, WX-02 v2, WX-03 v2, WX-05 v2's engine, WX-41's weather events, WX-42's triggers, WX-51; the Acquisition provider and fixtures, the weather chunk (manifest v2, already reserved), the sidecar bump (WX-32) |
| 4.06 Weather on screen | WX-50, WX-53, WX-47's layers, the Weather tab mockup (WX-46) |
| 4.07 Snow | WX-22's ×K in SN-08…18, SN-22 v2's hold-for-powder, WX-41's Rain on snow, Glaze and Corn morning, WX-52's attribution |
| 4.09 Lakes | WX-22's ×K for ice |
| 4.11 Lift operations | WX-39's top-station gust into LO-11; the forecast gust on the lift window |
| 4.12 Guests | WX-44 (GU-04 v2) |
| 4.15/4.16 Water and guns | WX-22's ×K for inflow, gun output and draw; tonight's gun window |
| 4.17 Sim bar, tabs | WX-45, WX-46's wiring, WX-42's alerts, the evening sheet with the day report, WX-48's Almanac card, SI-15 v2's UI |

- **Contradictions with 4.02, resolved here:** WX-01's thaw and refreeze states and "2–3 face-day cycles" (WX-01
  v2); WX-03's freezing-rain test, which overlapped the snow rule (WX-03 v2); WX-02's aspect octant leaves the air
  tables (WX-02 v2); WX-41's events come from fixed terrain points, not lifts (WX-35); WX-05's "morning ritual" becomes the evening sheet (WX-05
  v2); §2's "snow, water and money tuned to the game's own days" yields to WX-22 for weather-integrated processes;
  SI-15's "shown to the player and typeable" (SI-15 v2); TM-12's fixed `StormAlertIn` (WX-42); GU-04's 6:00 read
  moves to 7:30 (WX-44).
- **Formats (for the coordinator; this document bumps nothing):** the package manifest's weather chunk (v2,
  reserved for 4.05); the sim sidecar's `SidecarVersion` for WX-32's state (4.05); the design shape for SN-22 v2's
  grooming setting (4.07). Saved formats follow 0.3 §5.
- **Later:** non-US anchors (WX-13); a pressure-aware wet bulb (WX-37); a moving-storm grid finer than 500 m for
  10 km maps; freezing rain icing lifts (rime, holds); a per-lift slow or close-ahead command; a precipitation info
  layer; "first to open / last to close" challenges reading WX-07's rows; difficulty settings exposed through
  scenarios (TM-16); an omniscient forecast for a sandbox.

## 15. Owner decisions ⛳ and open questions

Decided in the design interview of 2026-11-08:

- **Q1 → real totals** (base depths, how long you can stay open), and **Q17 → frequency over duration**: weather
  compresses with the calendar (WX-21, WX-22), superseding a first idea of doubling storm sizes.
- **Q2 → the latest 20 complete years** (WX-11). **Q3 → US first**, a free non-US source documented (WX-13).
- **Q4 → the states are internal drivers** for the snow conditions, never shown by name and never one per third
  (WX-01 v2); **no quotas anywhere: history in, tycoon tuning out** (§3).
- **Q5 and Q18 → cold pooling kept, cheap** (WX-29), with a temperature and a wind info layer (WX-47).
- **Q6 → spatial storms by precomputed gradients**: more snow up high, rain at the base, the windward face (WX-35,
  WX-38).
- **Q7 and Q12 → one forecast a day, at closing, with the day report**, refreshed at 7:30; firmer as the day nears,
  ~90 % at 3 days (WX-05 v2).
- **Q8 and Q16 → Winter and Forecast knobs as data only; each season its own seed; no player control or replay;
  hints by an almanac through the summer** (WX-34, WX-48, SI-15 v2).
- **Q9 → wind as proposed** (WX-28, WX-39). **Q10 → the HUD bug opens the Weather tab** (WX-45, WX-46).
- **Q11 → integer generator**, displayed rounded (WX-31, WX-49).
- **Q13 (crowd reads the weather), Q19 (guns ×K), Q20 (almanac right two in three)**: no comment, taken as guessed.
- **Q14 → every operating system reads the weather and the forecast** so the player can automate (WX-43).
- **Q15 → the climate readout is monthly temperatures and precipitation only** (WX-46).
- The tycoon-fun adversarial review (2026-11-08) added the named events, the rain arc, relative alert thresholds,
  the season character and the decision-aid HUD.
- The technical adversarial review of the draft (2026-11-08, 23 findings, all folded) made the events, forecast and
  crowd reads use fixed terrain reference points instead of lifts and moved trail events to 4.07 (WX-35, WX-41);
  reset the generator each October 1 (WX-56); carried sub-hour remainders (WX-23); fixed the storm peak arithmetic,
  the cap and the cloud base units (WX-24, WX-27); set powder days from the record (`PowderDayIn`); made freezing
  rain reachable and correctly stated why 4.02's rule failed (WX-03 v2, WX-29); measured the pool against the
  reference pixel and stored `hvf` continuously (WX-36); took 20 label years for 20 winters (WX-11); dropped
  February 29 from both sources (WX-15); listed the energy ledger under ×K (WX-22); precomputed the anomaly's
  coefficients per face hour (WX-17); kept the static inputs off the player's earthwork (WX-35); clamped Stull
  (WX-37); normalised the orographic factor on the lattice (WX-35); sized the tables to the terrain (WX-02 v2,
  WX-51); and gave the forecast, the rain arc, the blend and the almanac measurable acceptance (WX-52…55, WX-48).

Open:

- **O1.** Whether to email the POWER project at release (a request, not a requirement; WX-12).
