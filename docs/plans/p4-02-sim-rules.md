# Phase 4 · 02 Simulation rules

**Audience:** the project owner and coding agents. **Status:** ⛳ for the owner's approval. The time rules (§2) and the
pacing principle (SI-01) were decided in the design interview of 2026-11-07; everything else is *proposed* until the
decision rounds in §11 close. **Date:** 2026-11-07. **Plan:** [Phase 4 plan](phase3-13-phase4-plan.md), task 02.
**Sources:** the MapLibre game at `maplibre-final` (re-specified, never copied: [archive index](../reference/maplibre-archive.md)),
the owner's ideas doc, [snow rendering design](snow-rendering-design.md), [SLE snow guns](snow-guns-sle.md), and
[P3-02 design rules](p3-02-design-rules.md) for the network, lift and trail rules this builds on.

**⛳** marks a gate: the plan waits there for the owner's approval.

## 0. How to read this document

- **Every rule has an ID**, `PREFIX-NN`, and tests cite it: `LO03_QuadAtSixSecondsCarries2400`. IDs are never reused;
  a retired rule stays here marked *retired*.
- **A rule that holds a formula carries a version.** A behaviour change bumps it (`v2`) and re-pins its fixtures.
- **Numbers are imperial first, metric in brackets.** The game computes in metres, kilograms, seconds and newtons.
- **"(tuned at 4.NN)"** marks a value the named task may change without a rule change. Everything in the `Pacing`
  table (SI-01) is tuned by definition.
- **Which task implements what** is in §13. The MapLibre ledger (§10) says of every number from the old game
  whether it was **kept**, **changed** or **dropped**, and why.
- **Prefixes:** SI principles · TM time · WX weather inputs · SN snow · LK lakes · LO lift operations · GU guests ·
  WS water sources · PD ponds and dams · HY hydraulics · SG guns · SM making snow · BG budgets. None collides with a
  Phase 3 prefix.

## 1. SI · Principles

- **SI-01 v1, the `Pacing` table (owner, 2026-11-07).** Every pacing number is a named constant in one versioned
  `Pacing` table in `Simulation`, never a literal in code: day and night lengths, the chair-interval factor, the skier
  speed factor, the ride compression, the speed presets, the laps target, the day-type demand factors, the week shape
  and the days per season, the holiday calendar, the weather generator's per-third-of-month tunings, the draw and
  reputation constants, wear per pass, the gun loss factor. A dev switch (`-pacing <file>`) loads an override file for playtests. The
  table's version is part of every golden hash (SI-08), so tuning never silently breaks determinism. Rules below cite
  constants by name; changing one is a tuning, not a rule change. A Settings page later exposes the ones that are the
  player's.
- **SI-02, engine-free.** The simulation lives in the `Simulation` assembly (Domain only; no Unity APIs; the repo
  check's banned-API list applies). It runs on its own thread (0.3 §10).
- **SI-03 v2, snapshots out, commands in.** Presentation and UI read immutable snapshots (SI-16); nothing outside
  `Simulation` mutates or reads its working state. `TerrainEditPipeline`'s snowpack recompute (Presentation) moves
  behind a command (§13 Contradictions). Commands enter one ordered queue; the worker dequeues them at the start of
  a tick and **stamps each with that tick and a sequence number**: the stamped pair is what the replay log stores
  (the UI never guesses the worker's tick). A command may name the snapshot sequence it was issued against; one
  older than `Pacing.StaleCommandTicks = 20` is rejected and reported, as `DesignHistory` rejects a stale revision. A
  command issued while paused applies and republishes a snapshot without advancing time, within 100 ms (BG-07).
- **SI-04, the tick.** A fixed tick of **100 ms of real time at 1×** (TM-02). Speed presets run 1, 2, 4 or 8 ticks
  per real tick. Nothing in a tick reads wall-clock time.
- **SI-05 v2, the systems table and the phase order.** Systems are rows of an **ordered table in `Simulation`**
  (name, phase, the board columns and fields it reads and writes); the tick runs the table, and adding a system
  (economy, patrol, maintenance) adds a row and a test that its declared reads come before its writes in the order,
  never an edit to the loop. The Phase 4 table, in order: commands → the face-hour step when a face hour rolls
  (weather, snow, lakes, trail readiness; sliced, SI-19 flips the board once at the end) → lift holds and schedules →
  gun schedules and the solve request (HY-08) → gun deposit → skiers (arrive, choose, queue, board, ride, ski, leave)
  → wear counters → events → ledgers → snapshot publish. Events in a phase are ordered by (time ms, entity id,
  sequence). Never by dictionary order.
- **SI-06, keyed randomness.** Every draw is a keyed hash of (table version, seed, tag, entity, day, ordinal), never a
  running counter, never `System.Random`. `Simulation` keeps its own tag register, separate from `DesignKey`'s tags
  1–15, and a tag is never reused.
- **SI-07 v2, integer state and ledgers.** Fields carried between hours are stored quantised (**SWE in 0.001 mm as
  `int`**, which reaches 2 km, so a 0.005 mm wear term needs no carried remainder; density in kg/m³, ice in mm, water
  in gallons as `long`, money in cents); a remainder is carried only where a term is finer than its quantum. Ledgers
  are integer sums, so totals don't depend on evaluation order.
- **SI-08 v2, determinism and goldens.** The same replay header (SI-22) and (tick, command) log give the same
  snapshot hashes, at any speed preset and with any rendering. Goldens are pinned on the **Windows x64 player build
  with the project's scripting backend** (the editor is not the golden; Mono's and IL2CPP's maths libraries differ);
  the CoreCLR CI build checks invariants (conservation, ordering, gates) but not the byte-level hashes, because
  `Math.Pow`, `Exp` and the trig functions may differ across runtimes.
- **SI-09 v2, parallel work.** Only in fixed bands independent of core count, each band writing its own range, joined
  in band order, reductions in a fixed order. `Parallel.For` stays banned in engine-free code; `Simulation` owns a
  small band pool. A **Burst twin is allowed only for a kernel written in integer or fixed-point arithmetic with no
  transcendental calls** (as `PoissonForest`), and such twins must give identical bytes. Float kernels have one
  implementation (plain C#, banded) and no twin; a transcendental on a hot path (SG-03's Gaussian, HY-04's powers,
  SN-09's cosine) is evaluated once per face hour into a table over quantised inputs that the tick reads.
- **SI-10, conservation ledgers.** Three ledgers balance every day, to the quantum of SI-07:
  - **Snow (SWE):** snowfall + machine snow + retained rain = ∆stored + melt runoff + sublimation + edge outflow
    (wind past the ring) + shedding to the void (cliffs). Grooming and wind move snow; they create none.
  - **Water:** inflow + recharge = ∆pool + gun draw + spill + evaporation.
  - **Skiers:** target = arrived + turned away (GU-03; turned away is 0 until ticket caps exist); arrived = on the
    hill + left + went home from a line (GU-06).
  - **Operations (in kind, for the economy to price later, never recompute):** gun-hours, pump energy (hp × hours
    ÷ η, integer Wh), compressor-hours, groomer-hours, lift-hours and lift rides, water drawn, snow made; per day,
    integer sums. The day report (TM-15) shows them, so a player optimises gallons and gun-hours per foot of trail
    before money exists.
- **SI-11 v2, honest slowdown.** When the machine can't run the requested preset, the clock slows and the HUD shows
  the sustained rate. The simulation never skips ticks, aggregates skiers or coarsens physics **because of load**.
  Coarsening **by state** is allowed and deterministic: when no skier exists and no lift is running (nights,
  pre-season, summer), one tick advances `Pacing.IdleTickFactor = 10` motion ticks (gun deposit × 10; the integer
  sums are identical), because the rule reads the state, never the hardware or the preset (SI-08 holds).
- **SI-12 v2, saves.** Restoring a save gives the same future as not having saved. **Save is a command:** at the tick
  boundary the worker serialises its whole state (fields and fine tiles, the skier arrays, every queue and key
  ordinal, the weather generator's state, the hourly slice cursor and any half-built hourly tables, the design
  revision and the replay header SI-22) into an immutable blob handed to the host, which writes it off the worker;
  `save.json` records the blob's content hash. **The sidecar is a frozen format** like every other (0.3 §5): a
  `SidecarVersion` in its header, one migration step per bump, a `TestData/formats/v<N>-sim` fixture, and the format
  freeze tests cover it. A v10 (or older) save opens on bare ground at the clock's start (TM-11): nothing is
  converted.
- **SI-13, design edits while the simulation runs.** Undo is a forward command, never a rewind. A deleted lift
  unloads its riders at its bottom station; a deleted trail's skiers finish on the natural ground beneath (which keeps
  its snow); a removed pond's water is dropped from the ledger as a stated loss. Earthwork changes recompute the
  per-cell static inputs (SN-01) by dirty rectangle.
- **SI-14, construction.** In Phase 4 building is instant and free, at any time of the season (owner, 2026-11-07:
  "full Cities: Skylines"; pacing by money, unlocks and construction time comes with the economy, §13 Later).
- **SI-15, the season seed (owner, 2026-11-07).** Every season has one 64-bit seed, shown to the player and typeable
  when a season opens (a new one is drawn by keyed hash from the resort's seed and the season number unless the
  player pins one). The weather generator, the forecast noise, guest draws and every other SI-06 draw key off it.
  **The same seed on the same site gives the same weather, hour for hour, whatever the player builds (WX-08);** snow
  conditions, lines and money then follow from the weather plus the player's commands (SI-08), so they repeat only
  when the commands do. The seed is in the save (SI-12) and in the problem-report zip.

The following rules come from the architecture audit of 2026-11-07 (owner: "a back end that runs the data and a
front end that displays it"): they write down the boundary between the worker and everything else.

- **SI-16, the snapshot.** A snapshot is an **immutable record stamped with the tick it describes and a sequence
  number**: the clocks and label, per-lift state (line count, rope travel, hold, open), the entity board (SI-19),
  the per-skier render records (GU-16's six fields, ~16 B a skier, ≤ 48 KB; never the day record, GU-02), the
  ledgers, the event ring (SI-18) and the list of staged tiles (SI-17). The worker publishes latest-wins into a
  **triple buffer**; readers take the newest and hold it for one frame; a snapshot is never mutated after publish.
  The HUD reads the newest snapshot at most every 100 ms (TM-13), so 8× doesn't octuple UI work.
- **SI-17, fields cross as tiles, not as objects.** The 8 m fields and the fine tiles are owned by the worker. A
  tile dirtied by a gun, a skier or the hour step is **listed for staging at most once per `Pacing.TileFlushTicks =
  10`** (1 s real at 1×), from a backlog drained round-robin at ≤ `Pacing.StageTilesPerTick = 32` tiles a tick (so
  the hour flip's thousand dirty tiles take a few seconds to appear, which no one can see). The worker copies each
  staged tile's **render planes only** (depth R16, type and quality R8G8: 3 B a cell, 12 KB a 64 m tile), never the
  16 B record, into a presentation-owned **staging ring** (BG-11) and lists it in the snapshot; the main thread
  uploads textures from the staging ring only. `SnowDepthField`'s dirty rectangle becomes that ring; no main-thread
  code ever reads a worker-owned array.
- **SI-12 v2 and SI-17 as task 4.03 PR B built them.** The sidecar is `SimSidecar` v1 ("MPSM"): the replay header,
  then the world's fields in a fixed order, then the state hash the reader must reach (checked when the Pacing and the
  design match the save's). Commands staged for the next tick are applied before the state is taken (they would apply
  first anyway); the event ring and the command log aren't saved. A missing or damaged sidecar opens the design with the
  clock started over, said once. Time that passes after a save makes the design ask to be saved. Until 4.07 the snow
  is the ground's natural snowpack, which the presentation still works out and sends whole (then by changed rectangle)
  in a `SetSnowpack` command; the worker keeps it in whole mm and hashes it a 64-cell tile at a time. The staging ring's
  slots carry depth only (8 KB, 64 × 64 × 2 B) until 4.07 adds the type and quality planes. While paused the host still
  stages, so an edit's snow shows without the clock moving.
- **SI-18, the event ring.** Every state change a player could care about (boarded, unloaded, hold on/off, trail
  readiness change, gun start/stop/short, source level band, solve failed, skier arrived/left/balked, season opened)
  is appended as a typed event (stable type id, tick, entity, value) to a fixed-size ring in the snapshot. Alerts
  (TM-12), the day ledgers (LO-12, SI-10) and, later, ratings, reputation and the economy are **consumers of the
  ring, never second computations**. A `PauseRequested` event is how an alert asks the host to pause; the simulation
  never pauses itself.
- **SI-19, the entity board.** The face-hour step and open/close events write a **per-segment and per-lift board**:
  open, posted and true band, quality per band (SN-16 with the band's taste), width factor, capacity, whale-blocked
  (SM-05), reachable-descent bitset per band (LO-10), per-node `canReachBase[band]` (GU-07), hold state,
  top-station gust. Per-tick phases (lifts, skiers,
  wear) **read only the board and per-entity counters** (skiers on a segment, line length), never a cell field. The
  board flips once, when the hour's last slice finishes, so no tick sees half an hour. Any system may add a column;
  Guests read columns, not systems.
- **SI-20, the design is an input, delivered by command.** The simulation holds one immutable `DesignSnapshot`
  (revision, document, terrain edits) and replaces it only through a `SetDesign` command. Every `DesignHistory`
  change, undo and redo included, is submitted as one such command; the replay log therefore holds the design's whole
  history, and "the same design" in SI-08 means the first revision plus the log.
- **SI-21, derived inputs travel with the command.** Footprint shares (SN-19), fine-tile seeds (SN-28), static
  inputs (SN-01), catchments (WS-04), the hydraulic graph and pipe profile (HY-01, HY-03) are computed by the
  submitter on a background task from the immutable design and package and arrive **inside the command as immutable
  arrays**; the worker only swaps pointers and folds state (SN-28's conservation) in bounded work. `Simulation`
  never computes geometry from shapes, so a future tool (a lodge, a park, a patrol hut) teaches the sim nothing
  about geometry.
- **SI-22, the replay header.** Every log and every save carries: the resort seed, the season seed and number, the
  start tick, the package id and manifest version, the design revision hash, the `Pacing` contents hash and version
  (an override file, SI-01, is in the hash), a content hash over every catalog the simulation reads (lifts, guns
  `GunCatalog`, SN-16's table, SN-14's order), the game version and scripting backend. Goldens are keyed by the
  header; a header mismatch is reported, never silently replayed. Selection and camera commands are logged like any
  other. The header has a `Goals` block, empty in Phase 4 (TM-16).
- **SI-23, the headless harness.** `Tests/Core` holds a `SeasonRunner` that opens the 2 km fixture with the P3-11
  test design, a scripted command log and a seed, and runs N face days **with no Unity**. Every PR runs one face
  day with 500 skiers and 50 guns and asserts the SI-10 ledgers, the SM-06 balance, GU-05's gate and **0 B per tick
  on CoreCLR** (`GC.GetAllocatedBytesForCurrentThread`, which Unity lacks); a nightly run plays a full season and
  records tick times report-only, timed by the test host (the repo check's `Stopwatch` ban is lifted for
  `Tests/Core` harness code only, never for `Simulation`). Windows-player goldens stay in EditMode (SI-08).

## 2. TM · Time

**The design interview in one paragraph (owner, 2026-11-07).** The player is a general manager watching a living
mountain and tweaking it: half the time just looking. A chair leaving the station every six seconds is the vibe, so
chairs run at real speed and everything bends around that. A season must fit a few sittings, so a day is short and a
season is 42 of them, with weekends every third day and holidays as the big days. The calendar is a label the weather
engine and the holidays hang on. Snow, water and money are tuned to the game's own days: no cleverness. Cities: Skylines
2 takes the same liberty (a day is a "month" and nobody checks a commute against the clock); RollerCoaster Tycoon goes
further and has no day at all. Real-calendar models (150 days, or compressed weeks shown hour by hour) were reviewed
adversarially and dropped: see §10.

- **TM-01, two time bases, one truth.** There is a **motion clock**, on which skiers, chairs, groomers, snowmobiles
  and gun plumes move and on which the tick runs, and a **face clock**, which the HUD shows (8:30 → 4:30), which
  weather, snow, water, lakes, guns and ledgers step on, and on which hours and days are counted. Skiers and lifts are
  simulated only on the motion clock; nothing is reconciled between the two. MapLibre's dual clock failed because its
  guests lived on both; here they live on one.
- **TM-02 v1, the day.** At 1×, the **operating day (8:30 AM–4:30 PM on the face) is 12 real minutes**
  (`Pacing.DayMinutes = 12`), so the face runs 40× real by day: one face hour every 90 s. The **night (4:30 PM–8:30 AM)
  is 4 real minutes** (`Pacing.NightMinutes = 4`): one face hour every 15 s. One day-night cycle is 16 real minutes.
- **TM-02 v2, the day is the resort's hours (owner, 2026-11-08; task 4.03).** The day rate (one face hour every 90 s at
  1×: 4 face seconds a tick, `Pacing.DayHourRealSeconds = 90`) runs from the resort's opening to its stop on a season day
  (LO-07 v2); every other face hour runs at the night rate (one every 15 s: 24 face seconds a tick,
  `Pacing.NightHourRealSeconds = 15`). A step never crosses a whole hour, the opening, the stop or the hour before
  opening, so every speed and skip gives the same state.
- **TM-03, speeds.** Presets **pause, 1×, 2×, 4×, 8×** (`Pacing.Speeds`), each a whole number of ticks per real tick.
  A chair leaves every 6 / 3 / 1.5 / 0.75 s. Changing speed never changes any result (SI-08).
- **TM-04 v1, the motion constants.** Chairs and rope run at the catalog's real speed × `Pacing.ChairSpeedFactor = 1`.
  Skiers ski at the GU-10 speed × `Pacing.SkierSpeedFactor = 2`. A rider's **ride time is the catalog ride time ÷
  `Pacing.RideCompression = 8`** (LO-06), so a 13-min ride takes about 1.6 real minutes and a 1,000 fpm detachable
  still gets skiers up faster than a 450 fpm fixed grip.
- **TM-05, the laps target.** The constants above give a skier about **4 laps** a day on a 3-minute lap
  (`Pacing.LapsTarget = 4`, used by GU-03's Lift-capacity readout). (The owner's "half a dozen" is the upper end for short
  lifts.)
- **TM-06, the evening stop and the night.** At 4:30 the clock stops for planning (reports, grooming orders, snowmaking
  schedules, building); any command is allowed. "Run the night" plays the night per TM-02 with guns and groomers only
  (no skiers, GU-12); "Skip to morning" is allowed anywhere in it and finishes the night's face hours at full fidelity
  within BG-05.
- **TM-06 v2, no stop at closing (owner, 2026-11-08; task 4.03).** Nothing pauses at closing: the clock runs on into the
  night at the night rate, and the player pauses when they want. The day report still arrives at closing (TM-15), and
  `PauseRequested` is kept for alerts only (TM-12). **Skip to morning lands an hour before opening** (7:30 at the
  default hours; `Pacing.SkipLandsBeforeOpenMinutes = 60`); it keeps the player's speed, so a paused clock stays paused
  and a running one plays that hour (15 s at 1×) before the day rate starts. It is refused while the lifts run and
  between that landing point and the opening.
- **TM-07, the week.** Three face days make a week: **weekday, weekday, weekend day** (`Pacing.Week = [Weekday,
  Weekday, Weekend]`). Day types drive demand (GU-03) and lift schedules (LO-09).
- **TM-08 v1, the season and the calendar label.** The baseline season **December 1 to April 1 is 42 face days**
  (`Pacing.SeasonDays = 42`, 14 weeks). The calendar label advances about 2.9 calendar days per face day and shows
  **"Day 12 · mid-December · Weekend"**: months in thirds (early, mid, late) are the weather generator's tuning rows
  (WX-07) and how the player finds holidays. Opening and closing are the player's (TM-11): the game nudges to close
  when open trails fall below `Pacing.CloseNudgeShare = 25 %` of built trails (SN-20), and an early opening or late
  closing adds days at the same rate. The label runs through summer; in summer the only control is "next month".
- **TM-09, holidays.** From the calendar label (`Pacing.Holidays`): Thanksgiving weekend, Christmas week (two holiday
  days), New Year's, MLK weekend, Presidents' Day weekend, and the four spring-break weekends. A holiday day uses the
  Holiday day type (GU-03) whatever its slot in the week.
- **TM-10, the view's sun is not the simulation's clock.** F1's time slider, `[` `]`, `-time`/`-day` and photo mode
  move the lighting only; the simulation's face clock moves only forward, by ticks.
- **TM-11 v2, the clock's start and the season's (owner, 2026-11-07).** A new game starts paused on **October 1** of
  the label year on bare ground (no snow spin-up): the weather runs as the player builds, so by December the natural
  snow is whatever fell. There is no "start the season" button. The mountain has one switch, **Open / Closed**
  (default Closed); skiers come only while it is Open *and* at least one lift is open with a way down (LO-10, SN-20).
  **Day 1 of the season is the first face day on which both hold**: the season starts when the first lift serves the
  first open run. Switching to Closed (or no lift serving a run for `Pacing.SeasonEndDays = 7` face days after the
  melt-out nudge) ends the season; the season count, the seed (SI-15) and the summer label follow.
- **TM-14, days nobody skis.** Before the season opens, and in summer, a face day runs at the night rate (TM-02's 15 s
  per face hour, guns and groomers only) with "Skip to morning" and "Skip to the next third" allowed; every face hour
  is still simulated (SI-11). So October and November are the player's build time with the weather and the guns
  running, and opening early is a real choice, not a menu item.
- **TM-15, the day report.** At 4:30 the worker emits one `DayReport` event built from the ring and the ledgers:
  target, arrived, turned away, went home, stranded; per lift rides and **utilisation %** (LO-12), peak and mean
  wait, hold minutes; trails Ready / Marginal / Bare and open, mean quality; snow made (ft³), water drawn, gun-hours,
  pump energy, groomer-hours (SI-10's operations ledger); mean satisfaction and its three largest reason lines
  (GU-13); each beside the previous day of the same type. 4.17 draws it as the first thing the player sees when the
  clock stops; Cities: Skylines and Transport Fever both end a period with a sheet that says what changed and why.
  The simulation keeps a `SeasonHistory` (per face day: the report's fields, per lift LO-12; ≤ 42 × 64 B × entities,
  saved, SI-12) so the Guests tab can draw a trend and the exit review a velocity.
- **TM-16, a scenario seam.** The replay header (SI-22) plus a `Pacing` override and a starting design is a
  scenario; the header carries a `Goals` block (target day-report fields by day) that is empty in Phase 4, so
  challenges and difficulty settings later are data, not a mode.
- **TM-12, alerts.** Consumers of the event ring (SI-18), each with a severity (pause, notify, log), a stable id, a
  dedupe key and a cooldown: lift wind hold on/off, lift closed for no way down, trail opened/closed by cover, source at minimum pool,
  gun short of pressure, gun short of air, solver failed, storm forecast (≥ `Pacing.StormAlertIn = 6 in`), thaw
  forecast, melt-out nudge. Critical ones pause at 1×–8× alike. (Tuned at 4.17.)
- **TM-13, dashboards.** View models refresh from snapshots at ≤4 Hz; the HUD's clock, counts and alerts at ≤10 Hz;
  never per tick (roadmap §5).

## 3. WX · Weather inputs

Weather's design (sources, the generator, lapse, inversions, storms, forecasts, difficulty) is task **4.04**'s, on
its own session. These rules say only what the simulation needs from it and the contract it must meet.

- **WX-01, generated, not replayed (owner, 2026-11-07).** The season's weather is **generated for the game's face
  days** by a state machine (clear → building → storm → clearing; thaw; refreeze) whose transition and intensity
  tables are **calibrated per third-of-month from the site's own record** (Daymet daily, NASA POWER hourly, as the
  plan lists). Storm cycles last 2–3 face days ("cloudy, snow, bluebird"). The real hourly history is calibration
  data, not playback. The generator is keyed (SI-06) and part of the save (SI-12).
- **WX-02, the per-cell fields, every face hour.** Air temperature (°F), relative humidity (%), **wet-bulb
  temperature** (°F, from one shared function: Stull's approximation, kept from MapLibre), precipitation rate (in/h
  liquid equivalent) and phase (snow, mixed, rain, freezing rain), wind speed and gust (mph) and direction (°),
  cloud cover (%), shortwave on a horizontal surface (W/m²). Fields vary over the map by elevation lapse, aspect and
  the inversion rule 4.04 chooses. **No per-cell weather is stored:** the hour is a parameter set (site-wide values
  plus the lapse, aspect and inversion coefficients 4.04 chooses, on a coarse grid of ≥ 500 m if 4.04 wants spatial
  storms) and one shared pure function `WeatherAt(hourParams, staticCell)`. The function is evaluated **once per
  hour into tables over (elevation bin 10 m × aspect octant × canopy class)**, Stull's wet bulb included (its
  `atan` and `sqrt` never run per cell, SI-09); the cell step reads the tables, and guns, lifts and the HUD sample
  the function at points. The view (4.06) may ease sky, cloud and wind between hours for the eye; the simulation
  steps.
- **WX-03, phase (default, 4.04 may refine).** Snow at wet bulb ≤ 30.2 °F (−1 °C); mixed below 33.8 °F (1 °C); rain
  above; freezing rain when the air is below 32 °F and the wet bulb below 35.6 °F (kept from MapLibre).
- **WX-04, sun.** Solar elevation and azimuth per face hour from the site's latitude and the label date, computed
  in Domain (the lighting already does this: one shared function).
- **WX-05, the forecast contract.** The player sees a forecast for the next 7 face days whose error grows with lead
  time: temperature ±2 °F at a day out to ±8 °F at a week; precipitation as a chance and a range; the storm's day
  firm 3 days out, its size 1 day out. The forecast is a keyed noisy view of the generator's truth, never a leak of it
  (no "omniscient" mode in Phase 4). Checking it is the morning ritual.
- **WX-06, what the simulation guarantees back.** Snow, lakes and guns step once per face hour in order (SI-05),
  reading that hour's fields; they never read the forecast.
- **WX-07, calibration targets 4.04 must hit per third-of-month** (so the snow rules below produce a sensible
  winter): the site's mean temperature, snowfall total, number of storm days, number of thaw days (max > 40 °F) and
  rain days, scaled to the season's face-day count. The owner's "first to open / last to close" challenges (Later)
  will read the same rows.
- **WX-08, the weather is a function of (site, seed, face hour) and nothing else.** The generator never reads the
  design, the snowpack, the skiers or the clock speed, so the same seed replays the same season (SI-15) and the
  player can't farm a storm by rebuilding. Its state is advanced once per face hour in SI-05's first phase; 4.04
  owns everything inside it (states, tables, lapse, inversions, how many storm types).

## 4. SN · Snow

The data model is the [snow design doc](snow-rendering-design.md) §3 with one change: natural depth becomes SWE and
density, so water and snow balance (SI-10).

### 4.1 Fields

- **SN-01, static inputs per 8 m cell**, computed engine-free from the float heights (SH-02) when a mountain opens and
  by dirty rectangle after earthwork: elevation, slope (%), aspect (° true), the unit normal (3 × int16, for SN-09's
  sun factor), canopy share, ridge exposure (as
  Snowpack v0's 7×7 rise), water mask. Snowpack v0's `Mathf` path is replaced by this.
- **SN-02 v1, the natural snow field** per 8 m cell over the ring: **SWE** (in, stored per SI-07: 0.001 mm `int`) and **density**
  (lb/ft³, stored kg/m³). Depth = SWE ÷ density, derived for the texture and `HeightAt`. The field replaces
  `SnowDepthField`'s metres as the source; the depth texture upload (dirty rows) is unchanged.
- **SN-03 v2, the fine field (owner, 2026-11-07: "high def over painted trails and areas").** Sparse **1 m tiles**
  (64 m, as the snow doc §3) cover every trail footprint with a one-cell margin and every Area's station aprons; off
  them the 8 m field is the only state. Inside a fine tile **the whole state lives at 1 m**: SWE, density, machine
  snow, groomed shape, and the conditions record (SN-04); the 8 m cell beneath holds the tile's area-weighted roll-up
  so coverage, the ledger and the far view never read two sources. Machine snow is deposited at
  `Pacing.MachineSnowDensity = 25 lb/ft³` (400 kg/m³). Melt and wear act on each fine cell (SN-11 orders machine
  snow before natural).
- **SN-04, the conditions record** (the one map, owner 2026-10-02) per fine cell on the fine field and per 8 m cell
  elsewhere: type (SN-13), quality 0–1, moguls 0–1, age (face hours since new snow or grooming), passes (integer,
  since new snow or grooming). The spring-cycle state SN-14 rule 5 needs (cycles 0–3, last cycle day) lives **on the
  8 m cell only**; fine cells read the one beneath (nobody can tell per-metre corn from per-aspect corn).
- **SN-28, seeding and retiring fine tiles.** When a trail or Area is painted, its new fine cells are seeded by
  **bilinear interpolation of the 8 m field** (SWE, density, type and age from the nearest cell), conserving the
  tile's SWE total to the quantum; from then on the fine cells evolve on their own. When a trail is removed, the fine
  tiles fold back into the 8 m cells by area mean (conserving SWE) and are freed. A groomer pass, a gun plume and a
  skier's wear all write at 1 m, so a groomed lane, a whale and a mogul field have real edges.
- **SN-31, the Traffic info layer.** SN-24's band counters per segment per hour and the stations' line lengths are
  a free **Traffic** info layer (where they skied, where they waited): the most legible thing a lift-planning game
  can show, from counters that already exist. (An info layer per 12b: display only.)
- **SN-29, the map looks sharp (owner).** The depth and conditions info layers draw the fine tiles at 1 m with hard
  type edges and the 8 m field between them bilinearly filtered, so the picture reads as one free-flowing heat map
  that snaps into detail over the trails; no 8 m blocks show anywhere. The look is 4.08's; the two-resolution source
  is this rule.

### 4.2 Accumulation

- **SN-05 v1, new snow.** Per face hour, snowfall SWE = precipitation × phase share (snow 1, mixed 0.5, rain 0). New
  snow density = 1,000 ÷ SLR kg/m³ where the **snow-to-liquid ratio SLR = clamp(8 + 0.8 × (30 − wet bulb °F) ÷ 1.8,
  8, 18)** (MapLibre's rule in °F: 8:1 at a 30 °F wet bulb, 18:1 at about 8 °F). Mixed precipitation also leaves
  its rain share as retained water (SN-09).
- **SN-06 v1, retention on the ground.** New snow × canopy × shedding × ridge, all from Snowpack v0 (12b, owner):
  canopy keeps back up to 30 % under dense forest; shedding falls from 1 at 85 % grade to 0 at 175 %; ridges lose up
  to 30 % and hollows gain it (SN-07 moves it). MapLibre's slope-retention table (15–60°) is dropped for these.
- **SN-07 v1, wind moves snow.** When the hour's wind exceeds `Pacing.DriftWind = 20 mph`, a share
  min(0.3, (wind − 20) ÷ 60) of the hour's new snow on exposed cells (ridge exposure > 0) moves downwind into the
  first sheltered cells within `Pacing.DriftReach = 5` cells along the wind direction, split 50 / 30 / 20 % over
  three of them (else edge outflow), conserving SWE; what leaves the ring is ledgered. A one-cell hop would draw a
  stripe along every ridge. MapLibre's wind term deleted snow; this moves it.
- **SN-08 v1, compaction.** Density rises toward `Pacing.MaxDensity = 28 lb/ft³` (450 kg/m³): ρ += (ρmax − ρ) ×
  0.0015 per face hour (MapLibre's 0.9985/h on depth, now on density), faster when wet (×3 when the surface is WG/WP).
  SWE is unchanged by compaction.

### 4.3 Melt

- **SN-09 v1, melt per face hour** (SWE lost, inches): **0.012 in per °F-hour** above 32 °F of cell air temperature
  (MapLibre's 0.55 mm per °C-hour, kept), plus **0.35 in per inch of rain** on snow (kept), plus **shortwave melt =
  1.2e-6 m per W/m²-hour × the slope's sun factor**, where the sun factor is the cosine of the angle between the
  hour's sun and the cell's slope normal (a dot product of the cell's stored unit normal, 3 × int16 in SN-01, with
  the hour's sun vector: no cosine per cell; 0 when the sun is below the slope's horizon, canopy ×0.5). MapLibre's
  radiation term had no slope or aspect; this one gives the owner's north/south rule. Melt below `Pacing.MeltCrumb =
  0.1 in` of SWE removes the rest (no slush crumbs; 0.5 in would snap 5 in of fresh depth away in an hour).
- **SN-10, where melt water goes.** Melt and rain beyond the pack's holding capacity (5 % of SWE) run off (ledgered;
  into the streams body, LK-07). Below freezing, held water refreezes into the pack (density up, SN-08).
- **SN-11, managed snow melts and wears first** where it exists, then natural. A whale (SN-27) melts on its surface at
  the same rate per area but, being deep, lasts; that is the point of whales.
- **SN-12, sublimation.** 0.002 in SWE per face hour when RH < 50 % and wind > 10 mph (tuned at 4.07; small, for the
  ledger).

### 4.4 The conditions map

- **SN-13 v1, types (SR5, decided 2026-11-07: the ideas doc's 11).** Per cell: **P** powder, **PP** packed powder, **MG**
  machine groomed, **HP** hard packed, **IS** icy surface, **CO** corn, **FG** frozen granular, **LG** loose granular,
  **WG** wet granular, **WP** wet powder. **SC** spring conditions is a **trail-level label** (SN-21) when no type
  covers ≥ 50 % of the trail, never a cell type. The snow doc's *crud* and *wind-board* fold into quality and HP.
- **SN-14 v1, transitions**, evaluated per face hour in this order, first match wins (T is cell air temperature,
  "thaw" means the hour's melt > 0):
  1. new snow ≥ 0.2 in SWE this hour → **P** (or **WP** if T > 31 °F or phase mixed); age and passes reset.
  2. groomed this hour (SN-23) → **MG** (from FG, IS or LG → **LG** first, then MG the next grooming).
  3. thaw, or rain → **WG** (from P, PP: **WP**).
  4. refreeze: T < 28 °F for 2 hours and the type is WG/WP → **FG** (a crust); and IS if the thaw was ≥ 2 hours
     (a glaze): **IS**.
  5. spring cycle: the third thaw-refreeze in 5 days on FG/LG → **CO** while the daytime thaw holds; CO refreezes to
     **FG** overnight.
  6. traffic: passes ≥ `Pacing.PackedPasses = 200` on P → **PP**; passes ≥ `Pacing.HardPasses = 800` on PP/MG →
     **HP**; traffic on FG/IS → **LG** at 200 passes.
  7. groomed many times with no new snow (≥ 5 groomings) → **HP**.
- **SN-15 v1, the powder thresholds by aspect (owner's ideas doc).** For rule 3 on a **P** cell the thaw test uses
  the owner's by-day thresholds instead of the hour's melt: powder stays powder while the air is below **42 °F N,
  40 NE, 36 E, 30 SE, 28 S, 32 SW, 34 W, 38 NW** by day and 32 °F at night (interpolated by aspect). This is the
  rule the player feels on a sunny day; the SWE physics (SN-09) still melts what melts.
- **SN-16 v1, quality** per cell: the type's base quality × age and traffic terms: base **P .96, PP .90, MG .94,
  HP .68, IS .16, CO .84, FG .42, LG .64, WG .50, WP .78** (MapLibre's table, kept); × (1 − 0.004 per face hour of
  age, floor 0.6); × (1 − 0.0003 per pass, floor 0.5); moguls ×(1 − 0.3 × moguls) for the under-advanced bands only
  (GU-07 reads it by band).
- **SN-17 v1, moguls.** On an ungroomed trail cell with grade ≥ `Pacing.MogulGrade = 40 %`, moguls += passes ×
  0.0005 per face hour (so about 800 passes make a full field); grooming resets to 0; new snow ≥ 6 in halves them.
- **SN-18, age.** Face hours since the last new snow ≥ 0.2 in SWE or grooming.

### 4.5 Trails: coverage, open and closed, grooming, wear

- **SN-19 v1, footprints on cells.** Each trail's area share per 8 m cell is precomputed from its 250 mm mask when
  built (and per segment, NW-08); coverage, quality and wear use these shares.
- **SN-20 v2, readiness, and who opens a trail (OQ2, owner 2026-11-07: "the game knows, the player decides").** A
  trail cell is *covered* when total depth ≥ `Pacing.CoverGraded = 6 in` on a graded trail, 12 in on a natural one.
  The game rates every trail's **readiness** each face hour: **Ready** (≥ `Pacing.ReadyCover = 90 %` of the area
  covered), **Marginal** (≥ `Pacing.MarginalCover = 60 %`: the white ribbon, some walking), **Bare** (below: it can't
  be opened). The player sets each trail **Open / Closed / Auto** (default Auto: opens at Ready, closes below
  `Pacing.AutoClose = 80 %`); Open on a Marginal trail is allowed and is how a mountain opens early or hangs on late.
  Readiness changes raise TM-12 alerts; a trail that falls to Bare closes whatever its switch. On a Marginal trail
  skiers cross uncovered cells at walking speed (GU-10 × 0.2) and pay GU-17's penalty. A closed trail takes no skiers
  (GU-07). MapLibre's rule (2 cm on 20 % of the area) is changed: it opened grass.
- **SN-21 v1, the trail's conditions summary** (for the trail window and the rating 4.07 designs): the share of
  each type over the footprint, mean quality, ice share, mogul mean, coverage; **SC** when no type reaches 50 %.
  Per segment for the network (NW-08).
- **SN-22, the grooming setting (new trail attribute, NT29).** Per trail: *Groomed nightly*, *Groom when ordered*,
  *Never* (glades and zones default to Never). Saved in the design (a bump, §12). The groom direction is the
  trail's centreline direction (SH-05 derived).
- **SN-23 v2, grooming (owner, 2026-11-07: "grooming packs it down and evens it out, locally").** During the night
  (TM-06), each trail set to groom is groomed at a keyed hour within `Pacing.GroomWindow = 8 PM–6 AM` (later the
  groomer fleet's route; corduroy appearing everywhere at 10:00 sharp was the robot tell), in its fine tiles (SN-03):
  1. **Local smoothing, not a trail-wide average.** A pass is a rolling redistribution along the centreline: at each
     station, depth within a window of `Pacing.GroomReach = 50 ft` along the trail and the full width across it is
     moved toward the window's mean by `Pacing.GroomBlend = 70 %` of the difference, conserving SWE inside the window
     (a blade carries snow a few metres, not up the hill); computed with running sums along the centreline, O(cells). So the top of a trail stays deeper than its bottom and a
     sunny lower pitch still melts out first, but **at any one station the two sides of the trail end the night at
     about the same depth**; one pass doesn't erase a day's differential melt, nightly grooming nearly does. Whales
     and berms are spread by the same rule, so a whale feeds its own neighbourhood (SM-05), not the far end of the
     run. A thin trail opens sooner because its bare strips are filled from beside them (SN-20).
  2. **Packing:** every groomed cell's density rises toward `Pacing.GroomedDensity = 25 lb/ft³` (400 kg/m³) by
     `Pacing.GroomPack = 60 %` of the gap per pass, SWE unchanged, so **the depth reading drops** (fresh 8:1 snow loses
     about half its depth) while the water in the trail is the same.
  3. Cells go to **MG** with age 0, passes 0, moguls 0.
  A groomer can't push a whale that's still curing (SM-07) or snow deeper than `Pacing.GroomMaxDepth = 10 ft`.
  Animated groomers are Later; the night shows them driving (Presentation only).
- **SN-30, a packed base lasts (owner).** Two effects make groomed and skier-packed snow outlast loose snow under the
  same weather. (a) Melt removes SWE (SN-09), so **an inch of melt takes 2½ in of depth off a 400 kg/m³ base and 10 in
  off 100 kg/m³ powder**: this falls out of SN-02 with no extra rule. (b) A dense pack holds less air and sheds less to
  wind and sun: the SWE melt and sublimation of SN-09 and SN-12 are multiplied by `Pacing.PackedMeltFactor =
  clamp(1 − 0.25 × (ρ − 200) ÷ 200, 0.75, 1)` (1 at ≤ 200 kg/m³, 0.75 at ≥ 400). Without grooming or traffic every
  trail melts out at the same pace; with them the groomed base is the last snow standing. (Tuned at 4.07.)
- **SN-24 v2, wear.** A skier finishing a segment increments that **segment's hourly pass counter per lateral band**
  (`Pacing.WearBands = 8` across the width; the band is GU-16's lateral offset), O(1) per tick. At the face-hour step
  the snow system applies the hour's counts to the segment's cells in one banded pass: each cell gains passes by its
  band's count × area share × (segment width reference 100 ft ÷ actual width) and loses SWE **0.0002 in per pass ×
  clamp(grade ÷ 20 %, 0.5, 2)** (MapLibre's 0.005 mm, kept, with its slope factor). So a skied-in lane and a mogul
  field have 1 m edges from the band counts, while the per-tick work is O(segments finished) and the per-hour work
  O(fine cells) inside BG-02. Passes are integers (SI-07). Wear is per *visible* skier, tuned so a popular run shows
  it (owner, 2026-11-07).
- **SN-25, snow on water.** New snow on an ice cell goes to the lake's snow-on-ice (LK-05); on open water it is a
  ledgered sink.

### 4.6 Machine snow (the output of §8)

- **SN-26, deposit.** Guns write SWE into the fine field (SN-03) each tick (SM-03); the density is SN-03's; the cell's
  type becomes **LG** (loose granular, machine snow) until groomed, then **MG**.
- **SN-27 v1, whales.** Managed depth is capped at `Pacing.WhaleMax = 26 ft` (8 m, owner 2026-10-02); deposit above
  the cap spills to the lowest neighbouring cell. A whale cures for `Pacing.WhaleCure = 12 face hours` before a groomer
  may spread it (SN-23).

## 5. LK · Lakes

MapLibre had no lake model; these rules are new. They give task 4.09 "each lake freezes and thaws on its own dates".

- **LK-01, bodies.** Connected parts of the water raster (OSM water, WorldCover fallback) above
  `Pacing.LakeMinArea = 1 acre` are lakes with stable keyed IDs (SI-06, from the body's centroid cell); smaller
  water and all stream pixels form the **streams body**. Ponds (PD) join as new bodies when built. `WaterBodies`
  gains one entry per body, replacing `AllWater`.
- **LK-02 v1, depth.** Mean depth = median shoreline slope × equivalent radius ÷ 3, clamped to [1.6 ft, min(160 ft,
  radius ÷ 3)] (MapLibre's estimator, kept); the player may override it in the lake's window. Volume = area × depth
  (WS-02).
- **LK-03 v1, the fall lag.** A lake can't freeze until its heat is gone: ice starts once the running sum of
  freezing degree-days (°F-days below 32, hourly mean) since the last thaw-out exceeds `Pacing.LakeLagPerFoot = 15`
  × mean depth (ft) (a 20 ft pond needs about 10 days at 2 °F below freezing; a 100 ft lake most of December).
- **LK-04 v1, ice growth.** Thickness h (in) = `Pacing.IceAlpha = 0.8` × √(FDD) where FDD is the accumulated
  freezing degree-days (°F) since ice-on (Stefan's law, α ≈ 2 cm/√°C-day), reduced by snow on ice: α × 1 ÷ (1 + snow
  depth (in) ÷ 6).
- **LK-05, snow on ice.** New snow lands on the ice (SN-25) and insulates it (LK-04); it melts by SN-09 and its water
  runs off the body (ledger).
- **LK-06 v1, thaw.** Ice loses `Pacing.IceMeltPerDegDay = 0.1 in` per thawing degree-day (°F above 32, hourly) plus
  the shortwave term of SN-09 on the ice surface; at h ≤ 1 in the body is open water and its FDD resets.
- **LK-07, the streams body.** Freezes as a shallow lake of depth 1 ft, so it ices quickly and opens quickly; it
  receives runoff (SN-10) for the water ledger and feeds dam inflow (WS-04).
- **LK-08, states.** `WaterBodies` per body: OpenWater; Ice (h > 1 in, snow < 1 in); SnowCoveredIce (snow ≥ 1 in);
  thicknesses as today. The renderer needs no change (0.3 §4.6).
- **LK-09, as a source.** A frozen lake can still be drawn from through its intake (WS-05): the ice sits on the pool.
  Drawdown under ice lowers the water, not the ice.

## 6. LO · Lift operations

Every number comes from the catalog in `Domain/Design/LiftRope.cs` (LC-01…LC-08, LT41, LD2, the surface lifts'
D-rules and C-rules). These rules say how a lift *runs*. Riders are hidden while riding (E29) until 3D skiers exist;
the chairs carry **decorative riders** (LO-05).

- **LO-01, the interval.** A lift loads one carrier every `IntervalSeconds` of motion time at 1× (fixed grips 6 s;
  detachable six 7.2 s; eight 8 s; T-bar 6 s; carpets 2.4 s, from seats × 3,600 ÷ capacity; trams per LO-08).
  `Pacing.ChairSpeedFactor` scales it.
- **LO-02, the line.** Skiers queue in a line **N wide** where N is the carrier's seats (4 for a quad, 2 for a double,
  1 for a J-bar or carpet), FIFO by arrival tick. Each interval the front N board; the line steps up one row. A
  party (GU-02) boards together when it fits, else the next singles fill the carrier. A queued skier's render record
  (GU-16) is segment = the lift, entry = the join tick, exit = 0, lateral = its column; its rank is its order by
  entry tick among that lift's queued skiers, and 4.13 folds the line serpentine inside the station hull at
  `Pacing.LineRowFeet = 2.5` (a 300-deep line is 75 rows, which doesn't fit in a straight line); the same ranks
  drive a maze when skiers go 3D.
- **LO-03, capacity.** Rated capacity = seats × 3,600 ÷ interval (LC-03): a quad at 6 s is 2,400 an hour; a T-bar
  1,200; a single carpet 1,500. The **lift card shows the rated figure**; the **day ledger counts rides** (LO-12). The
  two are never shown against face hours (TM-01).
- **LO-04 v1, the wait readout.** Wait for a skier joining the line = (skiers ahead ÷ seats) × interval, in real
  seconds at the current preset, shown as m:ss and as the count in line: **"300 in line · 7:30"** at a quad at 1×
  (the owner's number). This is what the player will watch happen.
- **LO-05 v1, decorative riders.** Carriers leave with as many riders as boarded (LO-02) and arrive at the top with
  the same, unloading at the catalog's rate; chairs and rope move at the catalog's real speed. These riders are the
  lift's, not the skiers' (TM-04).
- **LO-06 v1, the ride.** A skier who boards is hidden ("riding") for the catalog ride time
  (`LiftType.RideSeconds`, LC-04 with the detachables' ramps) ÷ `Pacing.RideCompression` and is released at the top
  station at the **next carrier-arrival tick** after that, in groups of at most the carrier's seats (one interval of
  delay at most), so dots step off arriving chairs rather than appearing from nothing. A hold (LO-11) adds its ticks
  to every hidden rider's exit tick when the lift resumes (O(riders), once). Surface lifts and carpets are **not
  compressed**: the rider is on the snow for the whole tow (the
  T-bar at 2.5 m/s, the carpet at 160 fpm), so there is no hidden state; their catalog ride time applies as is.
  **Because of the compression a skier is never on a carrier:** riders on chairs are and stay decorative (LO-05) even
  when skiers become 3D, and "3D skiers" in §13 Later means skiers on snow. Putting real skiers on chairs later is a
  `RideCompression = 1` tuning plus a re-pinned laps target, not a code change.
- **LO-07, hours.** The resort's hours are **8:30 AM open, 4:15 PM last chair, 4:30 PM stop** on the face (the HUD
  mockup, UI-7), with per-lift overrides. At last chair the line closes to new skiers; the line drains and the last
  rider unloads before the lift stops; skiers still on the hill ski to a base (GU-11).
- **LO-07 v2, the player's hours within daylight (owner, 2026-11-08; question 10; design shape 12).** The resort's
  hours (opening, last chair, stop; whole minutes; default 8:30, 4:15, 4:30) are the player's, saved in the design, and
  each lift may keep its own (LO-09). Each face day they are cut to civil daylight at the site on the label date (the
  sun 6° below the horizon, dawn rounded up and dusk down to the minute): the resort never opens before dawn or runs
  after dusk, and the day says so (`HoursTrimmed`). Chairs run only while the lifts and the mountain are open.
- **LO-08, trams.** A tram runs as a jig-back: one cabin each way, departing when full or at the catalog interval,
  capacity per the catalog's own figure (500 / 750 an hour: MapLibre's cabin ÷ (ride + 4 min dwell) is kept as the
  derivation of those placeholders).
- **LO-09, schedules (owner, 2026-11-07).** Per lift: **Daily** or **Peak days only** (weekend and holiday day
  types, TM-07/09); a lift on Peak days only stands idle on weekdays (its chairs parked, no operating cost later).
  This is the mechanic behind "a cheap double beside the detachable".
- **LO-10 v1, a way down.** A lift opens only if a descent route of open trails (SN-20) leads from its top to a base
  station or to the bottom of another open lift (NW-02's directed graph; MapLibre's "viable return", kept). If it
  loses that mid-day (a trail closes), it closes to boarding and **downloads**: riders may board at the top and ride
  down (chairs and gondolas; not tows or carpets, which just close).
- **LO-11 v1, wind holds (OQ1, taken as recommended).** Per type, a lift goes on hold when the hour's gust at its top
  station × a keyed per-lift factor (1 ± `Pacing.GustJitter = 0.1`) exceeds the lift's limit, checked at a
  keyed tick offset within the hour per lift (so two lifts of a type don't stop in the same frame), and resumes when
  the gust has been 10 mph below it for a full face hour (hysteresis; v2 below: ten minutes).
  On hold the rope stops (RP-07 v2: a chair's position is a pure function of the rope's travel, which pauses), the line
  waits, riders in the air wait, and an alert fires (TM-12). Maintenance downtime is Later.
  **v2 (owner 2026-11-08, task 4.11):** the limit depends on the type and the carrier's size. Tows, T-bars, J-bars and
  carpets never hold; a fixed grip holds later than a detachable of the same size; a bigger, heavier carrier holds
  later than a smaller one. Limit = `Pacing.HoldGustMph[type]` + `Pacing.HoldGustPerSeatTenths[type]` × seats ÷ 10:
  fixed-grip double 40 mph, triple 43, quad 46; detachable quad 35, six 38, eight 41; gondolas 45; trams 55.
  A held lift reads the gust every 5 face minutes (`Pacing.HoldRecheckFaceMinutes`), not hourly, and resumes after
  the gust has been 10 mph below its limit for ten face minutes (`Pacing.HoldResumeFaceMinutes = 10`; owner: holds
  ended too slowly).
- **LO-12, the day ledger per lift.** Rides, **utilisation** (rides ÷ (12 min × 60 ÷ interval × seats), the figure
  the player compares, since 2,400 pph beside 480 rides invites a division nobody should do), peak line, mean wait,
  minutes on hold, minutes closed; a consumer of the event ring (SI-18), for the day report (TM-15), the Guests tab
  and the later economy.
- **LO-13, the throughput golden.** A scripted feeder (no guests) joining a quad's line at 20 a minute for a face
  hour gives 2,400 rides and a line that never exceeds 40; 300 queued at once gives a 7:30 wait at 1×.
- **LO-14, plates (task 4.11 PR B, owner 2026-11-08; save v13, design shape 13).** Every lift wears a plate beside its
  name: one to three letters or digits, unique among the lifts (letters compared without case). A new lift takes the
  lowest whole number no lift has; the player changes it on the card like the name (one undo step). An older save's lifts
  are numbered 1, 2, 3 … in ID order. The plate leads the name on the card, the Lifts list, the ride bar and the map label.
- **LO-15, ride a lift (task 4.11 PR B, owner 2026-11-08).** Display only: the card's Ride puts the camera on the next
  carrier to leave the bottom, every built type (a chair or gondola in its seat, a tow standing on the snow under the bar,
  a carpet standing on the belt), moving with the rope as drawn, so a hold stops it and a pause freezes it. The mouse looks
  about the seat, first person, the pointer locked and hidden; Esc leaves to the camera as it was; at the top the rider gets off by themself, to the top station with
  the card open. No riders are drawn and no skier boards. The card also shows the hold as one figure (LO-11 v2) and
  Today: on hold now, holds likely (4.05's forecast; until then the F1 gust) or none expected.

## 7. GU · Guests v1

- **GU-01, individual skiers, no aggregate mode (owner, 2026-11-07).** Every skier on the hill is simulated at every
  preset, in structure-of-arrays with no per-skier allocation (BG-01). Up to `Pacing.SkierCap = 3,000` on the hill at
  once. There is no cohort, flow or representative model; the dots you see are the skiers that exist, and the economy
  scales from them (GU-14).
- **GU-02, a skier.** Ability (0–1, GU-05), band, party id (1–4, boards together), arrival tick, planned stay, state
  (arriving, choosing, queued, riding, skiing, walking, leaving, gone), current segment and progress, laps, total
  wait, satisfaction 0–1, and a fixed-size **day record** of its last `Pacing.RecordSegments = 48` segments (entry
  and exit ticks), the same for every skier. The record **stays in the worker** (it is 1.2 MB across 3,000 skiers;
  it never rides in the snapshot): an `InspectSkier` command (logged, SI-22) makes the worker publish that skier's
  record as one event in the ring at most every 10 ticks until deselected, and the later ratings read records at
  day end in the worker. Selecting a skier changes no result.
- **GU-03 v4, the size of a day (OQ3, owner 2026-11-07: "skiers come based on the mountain's draw… location, rep
  (and within rep, archetype: who you cater to, conditions, amenities)… the user can control crowds with ticket
  prices… not a direct function of lifts, but how popular your mountain is", as Transport Fever's town demand).**
  The crowd is **demand, never capacity**: nothing about the lifts or the open acres sizes it. Skiers per face day,
  **per band b**, = **`Draw` × `Pacing.BandMix[b]` × reputation(b) × day type × conditions × price**, summed over the
  four bands and capped at `Pacing.SkierCap = 3,000` (proportionally), and the Guests tab shows the stack line by
  line (draw, each factor, the result) so the player sees what moved it. The lines:
  - **Draw** is the location's potential: the skiers who would come on a peak day to a mountain they love. In Phase
    4 it is a package constant, `Pacing.LocationDraw = 800` by default, set per demo package (the economy later
    derives it from the capture area and local-vs-destination pull, and marketing moves it; same line, new source).
  - **Reputation(b)** = `Pacing.RepFloor = 0.2` + 0.8 × r_b, where r_b is the band's reputation 0–1 (GU-18). This is
    the archetype: cater to experts and the expert line grows while the beginner line shrinks; the mountain's
    clientele is what the player made it.
  - **Day type** `Pacing.DayType[weekday 0.5, weekend 1.0, holiday 1.2]` (TM-07/08).
  - **Conditions** (GU-04), today's snow report as the morning's pull.
  - **Price** = 1.0 in Phase 4 (no money yet). The economy wires it as (ticket ÷ reference)^`Pacing.PriceElasticity`
    with MapLibre's −1.10 as the starting curve, so the player throttles the crowd with the ticket window, not the
    lift count. Passes, amenities and local-vs-destination pull arrive as further lines, never a new rule.

  **Infrastructure is the player's answer to demand, not its definition.** Beside the target the tab shows two
  read-only capacity lines, **Lift capacity** (Σ over open lifts of boardings per day ÷ `Pacing.LapsTarget`; a quad's
  480 boardings carry about 120 skiers at four laps) and **Terrain capacity** (Σ open-trail acres ×
  `Pacing.SkiersPerOpenAcre = 5`, a comfortable density), so the player can see "600 coming, lifts for 240" before
  8:30. The simulation never clamps to either: too few lifts means lines (LO-12's utilisation, GU-08's balking),
  too little terrain means crowded runs (GU-07's crowd term), both cost satisfaction with a reason (GU-13), the
  band's reputation follows (GU-18), and tomorrow fewer of that band come. That loop, demand → strain → reputation →
  demand, is the tycoon; the player strives for more guests and must build to hold them. Nobody is turned away in
  Phase 4 (the ledger's line stays at 0 until ticket caps and sell-outs exist). Visits are never inflated to
  real-resort numbers (owner): a day of 500 dots is a big day, and the economy scales per visible skier (GU-14).
- **GU-04 v1, conditions sell tickets.** The conditions factor = 0.5 + 0.5 × (share of built trails open) × (mean
  trail quality ÷ 0.9)^0.5, clamped 0.3–1.2, read at 6:00 AM from the snow report. Reputation is GU-18; local vs
  destination and the holiday's own pull are Later lines in GU-03's stack.
- **GU-05 v1, ability and bands (SR6, decided 2026-11-07).** Ability is a keyed draw 0–1; bands **beginner < 0.25,
  intermediate < 0.55, advanced < 0.82, expert** (MapLibre's legacy cut-offs, kept), drawn to the mix
  `Pacing.BandMix = 20 / 40 / 30 / 10 %`. Bands map to the four trail colours: a band may ski trails rated up to its
  own (beginner green; intermediate blue; advanced black; expert double black) and no higher: **the hard gate**.
  "A beginner never picks a black" is a test over a fixture.
- **GU-17, posted and true ratings (owner, 2026-11-07).** Every trail has two ratings. The **true rating** is the
  game's, **TD-02 v2: the band of the steepest sustained pitch**, the mean grade over the steepest
  `Pacing.SustainedPitch = 150 ft` window of stations (six 25 ft stations), not a weighted average: a flat trail with
  a cliff in it is the cliff's colour, because that is the pitch a beginner meets. The **posted rating** is the
  player's, a trail attribute defaulting to the true one (the sign on the trail, in the design, a bump in §12). The
  gate (GU-05) reads the **harder** of the two (skiers see the pitch and read the sign); *fit* in GU-07 reads the
  posted one (the sign sets expectations). A trail posted easier than it is costs every skier on it
  `Pacing.MisratePenalty = 0.1` satisfaction per lap. A trail opened Marginal (SN-20) costs `Pacing.MarginalPenalty =
  0.05` per lap × a season-phase factor `Pacing.MarginalPhase`: 1 in the first and last `Pacing.ShoulderDays = 6` face
  days of the season (opening early, hanging on late: the owner's "normal"), 2 in between (the white ribbon of death
  in February is a choice the mountain pays for). Both feed reputation later (GU-13 carries them).
  - *Settled by task 4.21 (owner R1–R6, 2026-11-08; TD-07):* there's **no default**: a trail must be posted before
    it's built, and the player picks the sign in the build and edit flow. The true rating is hidden from players.
    The window is the Domain's `TrailRating.SustainedStations = 6`, not a pacing value, so tuning never re-rates a
    trail. A built trail can be unposted only in an older save the player changed before the game posted it; how
    guests read one is 4.12's to plan (the suggestion: its true rating for both, with no misrate penalty).
    `TrailRating.Harder` is the gate's helper.
- **GU-06 v2, arrivals and the stay (rescaled to the 12-minute day; MapLibre's real-minute constants were inert in
  it).** Skiers arrive at **arrival nodes** (an entity; in Phase 4 one per base station, a lift-bottom station no
  trail leaves downhill, or the lowest lift bottom if none qualifies; parking, lodges and a village add nodes later,
  not a rewrite), split between nodes by their lifts' capacity, over the **seven face hours 8:30–3:30** with
  MapLibre's hourly weights **38 / 30 / 16 / 9 / 4 / 2 / 1 %** (kept). A skier's stay is **`Pacing.StayLaps`, a keyed
  3–5 laps** (so `LapsTarget = 4` is its mean, TM-05); it leaves after the lap that completes it or at 4:15,
  whichever is first. A skier **queued longer than `Pacing.LeaveWait = 4 min`** (real, at 1×) leaves from the line
  (a `WentHome` event, ledgered). MapLibre's 4–6 hour stay was 6–9 real minutes here, in which a 7:30 line was most
  of a visit.
- **GU-07 v1, choosing a trail or lift.** At a node a skier scores each outgoing edge it may take (GU-05's gate,
  SN-20's open state, LO-10's way down): **score = fit × snow × width × crowd**, where *fit* = 1 − |trail rating band −
  own band| ÷ 2 (the snow doc's table: beginners want gentle, experts want steep); *snow* = the segment's quality
  (SN-16) with the band's taste (experts ×1.2 on P and moguls, beginners ×0.5 on moguls and ice); *width* = clamp(mean
  width ÷ 100 ft, 0.5, 1.2) for beginners and intermediates, 1 for the rest; *crowd* = 1 ÷ (1 + skiers on the
  segment ÷ its capacity). For a lift edge the score also multiplies by 1 ÷ (1 + wait ÷ 5 min). **Any edge into a
  node from which this band cannot reach a base scores 0:** SI-19 carries a per-node `canReachBase[band]` bitset,
  rebuilt by a reverse search per band over open edges at every open/close/design event (4 × O(edges),
  microseconds), so a beginner never takes a green into a pod whose only ways out are blue; the one-step lookahead
  of v1 wasn't enough. The pick is a keyed weighted draw (SI-06). Weights are the snow doc §5.1's, tuned at 4.12.
- **GU-08, balking.** A skier whose best lift wait exceeds `Pacing.BalkWait = 3 min` (real, at 1×) with another lift
  reachable goes there; with none, waits and loses satisfaction (GU-13) until GU-06's `LeaveWait`.
- **GU-09, walking.** Inside an Area (NW-15) a skier walks between lift bottoms at 3 mph × `Pacing.SkierSpeedFactor`;
  there is no walking between Areas (the network has no such edge).
- **GU-10 v1, skiing speed.** Along a segment: **base speed by rating band (green 13, blue 20, black 25, double black
  27 mph) × (0.6 + 0.8 × ability) × (1 + 0.15 × Z)**, Z a keyed standard normal clipped to ±3 (MapLibre's formula and
  base speeds, kept), × `Pacing.SkierSpeedFactor`. Speed **varies along the segment**: the submitter (SI-21) ships
  per segment a cumulative-time table over its 25 ft stations from the band speed × clamp(station grade ÷ the
  band's design grade, 0.4, 1.3); the simulation takes the table's total for the exit tick (one lookup) and the
  renderer maps progress through the table, so a dot slows on the runout instead of crossing it at the headwall's
  speed. Skiers enter a trail at least 1 s apart per entry node (kept).
- **GU-11 v2, leaving.** At the end of a stay, or at 4:30, a skier routes to the nearest base by open trails and
  leaves. With GU-07's reachability rule no skier is routed into a dead end; the natural-ground fallback (ski the
  ground to the lowest base) remains only for a trail that closes under a skier mid-segment, and fires a `Stranded`
  event the Guests tab counts.
- **GU-12, the night.** No skiers exist between 4:30 PM and 8:30 AM; the night simulates snow, guns and grooming
  only (TM-06).
- **GU-13 v2, satisfaction, with reasons.** Starts 0.8; each lap: 0.9 × s + 0.1 × trail quality, less GU-17's
  penalties; each wait: **−0.05 per real minute over 2** (so the owner's 7:30 line costs 0.27 and reads; v1's −0.01
  over 5 made it 0.025 and the tab a flat line). **Every change is a typed `SatisfactionDelta` event** in the ring
  (reason id: wait, snow quality, misrated, marginal, crowd, stranded; value in 0.001), so the skier card, the
  Guests tab and the day report aggregate by reason (RollerCoaster Tycoon's thoughts, CS2's happiness factors) and
  the scalar is their sum. GU-18 reads the day's satisfaction at 4:30; nothing else consumes it in Phase 4.
- **GU-18 v1, reputation per band (owner, 2026-11-07: demand follows "rep, and within rep, archetype").** The
  mountain carries four reputations r_b, one per ability band (GU-05), 0–1 in 0.001 steps, starting at
  `Pacing.StartRep = 0.5`. At 4:30 each band with at least `Pacing.RepMinSample = 10` arrivals moves toward the day's
  verdict: r_b += (v_b − r_b) × `Pacing.RepRate = 0.1`, where v_b maps the band's mean end-of-day satisfaction
  (GU-13, counted at leaving, including those who went home from a line) linearly from `Pacing.RepSatLow = 0.5` → 0
  to `Pacing.RepSatHigh = 0.9` → 1. A band that did not come does not move. So a good week lifts a band by about
  0.3, a bad one drops it as fast, and a mountain with no green terrain loses its beginners over a fortnight rather
  than overnight. The headline **Reputation** on the tab is the `BandMix`-weighted mean. r_b is simulation state
  (saved, SI-12; in `SeasonHistory`, TM-15) and part of the golden hash. Amenities, lodging, marketing and the
  season's opening date feed the same four numbers Later; Phase 4 has satisfaction only. (A per-band reputation is
  the smallest thing that gives "who you cater to" without a separate archetype system.)
- **GU-14, the economy scales per visible skier (owner, 2026-11-07).** When tickets and spending arrive they are per
  skier simulated; no multiplier stands between the skiers on screen and the ledger. Lift costs stay real-world
  (LiftPrices); the economy is balanced around that later.
- **GU-15, scripted skiers.** A command spawns N skiers of a band at a base for the style tile (4.10) and tests.
- **GU-16 v2, the snapshot for 4.13 and the render clock.** Per skier: segment id, entry tick, exit tick, lateral
  offset (keyed), state, band; the renderer interpolates along the segment's cached centreline and lifts the dot by
  `HeightAt` (ground + snow) from precomputed samples. Presentation keeps the two newest snapshots (SI-16) and renders
  at **(newest tick − 1) × the preset's real tick**, interpolating skier progress from entry/exit ticks and rope
  travel between the two; it never extrapolates past the newest snapshot. A hold eases the rope over 0.5 s on screen;
  the simulation's travel stops at once. The same record carries the 3D skiers on snow later (LO-06).

## 8. Snowmaking

MapLibre's snowmaking was a one-off pressure analysis run from a button, with the wet bulb typed in; its guns made no
snow and used no water. Phase 4 makes it an operating system (owner, 2026-11-05 and 2026-11-07).

### 8.1 WS · Water sources

- **WS-01, sources.** Lakes (LK-01) the player selects, ponds and dams (PD). Each has a pool volume, a level, an
  intake (WS-05) and a ledger (SI-10).
- **WS-02 v1, volume and level.** Lake volume = area × mean depth (LK-02); pond and dam volume from the graded surface
  (PD-04, PD-09). Level falls with drawdown by the stage–storage curve of the graded bowl (ponds, dams) or as a
  prism (lakes).
- **WS-03 v1, inflow to a dam** from the dammed stream's width (OSM `width` or the defaults: stream 8 ft, river
  46 ft): **≤ 3 ft: 1 cfs; 6.5 ft: 3.5; 13 ft: 11; 26 ft: 35; 50 ft: 106; 100 ft: 353; 200 ft: 1,060; wider: 2,650 cfs**
  (MapLibre's table in m³/s, kept) × a season factor (`Pacing.InflowSeason`: Dec–Feb 0.6, Mar 1.0, Apr 1.5, from the
  site's record via WX-07; tuned at 4.14).
- **WS-04, recharge of a pond.** A pond with no stream gets runoff from its catchment (SN-10's runoff over the cells
  draining to it, a D8 fill of the static inputs) and nothing else; a spring-fed override (gpm) is the player's.
- **WS-05 v1, the intake and the minimum pool.** The intake sits at the source's full-pool elevation minus
  `Pacing.IntakeDepth = 6 ft`; its head is the **current** level (MapLibre's fixed full-pool head is changed). A
  source stops supplying at `Pacing.MinPool = 10 %` of volume (alert, TM-12). A natural lake may be drawn down at
  most `Pacing.LakeDrawdown = 3 ft` below its surface (a permit rule; alert at the limit). The source card shows
  **"days of making left"** at the current draw: on a lake the drawdown limit is the only water tension in Phase 4,
  so it must be visible.
- **WS-06, spill.** A full pond or dam spills inflow (ledgered) rather than overtopping.
- **WS-07, evaporation.** 0.01 in per day of open water (small; ledger).
- **WS-08, stream lines in the package.** Dams need the stream's line and width: the area package gains the OSM
  waterway vectors it already fetches but only rasterises (a format change, §12).

### 8.2 PD · Ponds and dams (earthwork)

MapLibre's constants, kept, in imperial (the [reference inventory](phase0-0.1-reference-inventory.md) records the old
pond and dam doctrine):

- **PD-01, the pond ring is the waterline** at full pool; the berm sits outboard; design = max(fill envelope, min(ground,
  cut envelope)) by signed distance from the ring.
- **PD-02 v1, constants.** Freeboard **2 ft** (0.6 m); crest **11.5 ft** (3.5 m) wide; water face **3:1**; outer face
  **2:1**; cut **1:1** (TB rules); maximum reach **820 ft** (250 m); excavation 0–**50 ft** (15 m); berm height ≤
  **65 ft** (20 m) ("dam a stream instead"); area ≥ **1,100 ft²** (100 m²); the ring simple and inside the core.
- **PD-03, the suggested pool** = mean ground inside the ring rounded up to 1.6 ft (0.5 m) + 3.3 ft (1 m).
- **PD-04, the pond's bill and capacity** from the graded raster (cut, fill, balance; capacity = Σ depth × cell area
  on the geodesic area), as the trail bill (TB-01).
- **PD-05, dams: the first click sets full pool**; the second end snaps to the same contour within 250 ft (75 m), at
  least 16 ft (5 m) away; the alignment crosses exactly one stream (WS-08).
- **PD-06 v1, dam constants.** Crest = full pool + 2 ft freeboard; deck 11.5 ft; upstream 3:1; downstream 2:1; height ≤
  **130 ft** (40 m); the flood is a 4-neighbour fill to full pool from the upstream side (median DEM 65/165/330 ft
  along the stream decides which side), refused if it reaches the raster edge; re-flooded on the graded surface.
- **PD-07, a spillway** (new, MapLibre had none): every dam has one at full pool, so WS-06 holds; no overtopping.
- **PD-08, ponds and dams are design entities**: authored shapes with derived earthwork (SH-05), undo restores heights
  exactly, core only (SH-06), saved (§12).
- **PD-09, volume on the graded ground** = Σ (full pool − graded height) × cell area over the flooded cells.

### 8.3 HY · Hydraulics

- **HY-01, the network.** Nodes: intake, pump, pump house (a building that owns one pump), junction, hydrant, valve
  (new: open/closed per pipe end, for isolating a branch). Pipes between nodes with a diameter from **4, 6, 8, 10, 12,
  14, 16, 18, 20, 22, 24 in** (default 8). Guns are not nodes: a gun hangs on one hydrant within a **50 ft** hose
  (kept), one gun per hydrant.
- **HY-02, connectivity.** Explicit only: a pipe connects where it ends on a node; crossings don't connect (kept).
  Junctions appear when a pipe snaps onto another. **Loops are allowed and solved as loops** (SR8, proposed:
  MapLibre drew loops but solved a derived tree).
- **HY-03 v1, the pipe profile.** Pipes are buried `Pacing.PipeBury = 6 ft` below the ground and sampled every
  **82 ft** (25 m) along their route (kept); the **highest point** of each pipe is a pressure check node (new: MapLibre
  checked only the ends).
- **HY-04 v1, head loss (owner, 2026-11-07: the US coefficient).** Hazen–Williams:
  **h_f (ft) = 10.44 × L (ft) × Q (gpm)^1.852 ÷ (C^1.852 × D (in)^4.87)**, C = **120** for every pipe (kept). Worked
  example 1 from the ideas doc: 1,000 gpm through 1,000 ft of 8 in gives **21.2 ft** of friction (the doc's 21.7 used
  the SI coefficient 10.67 with US units; changed). Static head = elevation rise; velocity head is zero (the ideas doc's
  assumption, kept); pressure (psi) = head (ft) ÷ 2.31.
- **HY-05 v1, pumps.** A pump has horsepower and efficiency (default 85 %, kept; the pump house's 1,000 hp fixed,
  kept) and adds head **H = 3,960 × hp × η ÷ Q** (ft, gpm: the ideas doc's and MapLibre's constant-power rule,
  kept), **capped at a shut-off head of 1.5 × the head at the pump's rated flow** (`Pacing.PumpShutoff`; new: MapLibre's
  head went to infinity at low flow). Rated flow is the player's setting (default 1,000 gpm). A pump has a suction and
  a discharge end; an off pump passes water freely (kept).
- **HY-06 v1, the solver.** The global gradient method (Todini's, a Newton scheme on heads and flows) with the
  continuation in pump power MapLibre used (0.1 → 1.0 in five stages), tolerances **0.01 gpm** and **0.01 ft**,
  100 iterations per stage, a halving line search; the derivative floor 0.01 gpm. MapLibre's non-positive-derivative
  guard, which returned from inside a loop and never aborted, is **fixed**: a non-positive derivative stops the stage.
  Guns are fixed demands per stage (SG-03), so the system is linear in the guns and the solve is well posed.
- **HY-07 v1, pressure limits.** A gun needs **200 psi** at its hydrant (kept). Pipes have a **1,000 psi** class
  (`Pacing.PipeClass`): a warning above 800, a refusal (the pump throttles to the limit) above 1,000 at any node or high
  point. Velocity above **15 ft/s** (Q ÷ (2.448 × D²)) warns. A high point below atmospheric (negative gauge) warns
  "air lock" and the branch past it carries nothing.
- **HY-08 v2, when it solves.** A solve is **requested, not run**, in the tick in which the network, a schedule, a
  valve, a source level (by ≥ 1 ft) or the wet-bulb stage of any gun changed; it runs as a job on the band pool
  across ticks and its result applies at request tick + `Pacing.SolveLatencyTicks = 5` (fixed, so replay is exact),
  the last good solution standing meanwhile; a job unfinished at its apply tick **stalls the tick** (SI-11), never
  applies late. Guns that can't get 200 psi are **shut off in order** (SG-07) and the network re-solved inside that
  one job, at most `Pacing.MaxResolves = 10` times; a failed job leaves the last good solution and raises an alert. (Every gun changes stage at the same face hour, so without this the hour roll would
  put ten solves in one tick.)
- **HY-09, diagnostics (numbered; every one has a test).** 1 no source selected; 2 gun not on a hydrant; 3 hydrant on
  no pipe; 4 no route from a source to this gun; 5 pump has an end with no direction; 6 pump needs an inlet and an
  outlet; 7 an operating pump faces away from the source; 8 unresolved elevation; 9 pump needs positive horsepower and
  an efficiency in (0, 1]; 10 the equations did not converge; 11 pump has no forward flow (reverse / below rated);
  12 gun short of pressure (shut off); 13 pipe over its pressure class; 14 velocity high; 15 air lock at a high point;
  16 source at minimum pool; 17 lake at its drawdown limit; 18 gun short of air (SG-06); 19 valve closed isolates
  these guns; 20 gun forced on while too warm (water drawn, no snow; SM-02). (1–11 are MapLibre's list, kept and
  renumbered; 12–20 are new.)
- **HY-10, the pressure overlay.** Pressure at every node and high point, coloured over the pipe, range at least
  10 psi (kept), as an info layer.

### 8.4 SG · Guns

The guns are the [SLE stick guns (10, 20, 30 ft) and the ground gun](snow-guns-sle.md). The ideas doc's gun table is
re-specified for them; its real maker name does not appear here or in code (A2).

- **SG-01 v1, the SLE stages by wet bulb** (the ideas doc's table, kept; water and air per gun):

  | Stage | Wet bulb ≤ | Water | Air | Conditions |
  |---|---|---|---|---|
  | 1 | 28 °F | 18 gpm | 56 cfm | Marginal |
  | 2 | 24 °F | 28 gpm | 56 cfm | Fair |
  | 3 | 19 °F | 38 gpm | 56 cfm | Good |
  | 4 | 14 °F | 48 gpm | 16 cfm | Excellent |
  | 5 | 9 °F | 58 gpm | 16 cfm | Optimal |

  A gun runs at the coldest stage its hydrant's wet bulb allows; above `Pacing.TooWarmF = 28 °F` it is **too warm**:
  on Auto it takes nothing; forced On (SM-02) it draws stage 1's water and air and makes no snow. The wet bulb is
  the gun's cell's (WX-02), not a typed number.
- **SG-02, pressure.** Minimum **200 psi** at the hydrant (kept); demand per stage is fixed and does not rise with
  pressure (kept: the ideas doc's "output is a direct function of water flow").
- **SG-03 v1, throw and footprint.** Throw **30 / 80 / 125 ft** for the 10 / 20 / 30 ft sticks (kept); the ground gun
  **30 ft**. The footprint is an elongated Gaussian down the gun's aim, length = throw, width = throw ÷ 3, bent
  downwind by the hour's wind: its centre moves wind (mph) × 2 ft, its width grows 20 % per 10 mph; above
  `Pacing.GunWindLimit = 25 mph` the gun stops (the snow blows off the trail). The footprint is a **stencil** (cell
  offsets and integer weights summing to one) recomputed only when the gun's aim, stage or the hour's wind bucket
  (`Pacing.WindBucketMph = 5`) changes, at most once per face hour; the tick adds rate × stencil with no
  transcendental call (SI-09). Gun models live in a `GunCatalog` beside the lift catalog: data, not a switch.
- **SG-04, mounts.** The stick guns are towers (permanent); the ground gun is the sled: movable to any hydrant within
  the hose. The ideas doc's sled/tower split and prices (7,000 / 7,000 / 8,000 / 9,000 $) are kept for the economy
  later; not charged in Phase 4 (SI-14).
- **SG-05 v1, output.** Snow made per hour = water (gpm) × 60 × (1 − `Pacing.GunLoss = 15 %`) gallons of SWE →
  managed snow at SN-03's density: at stage 4, 48 gpm makes about **820 ft³ of snow an hour**; a 16-hour night about
  **13,000 ft³**, which is 1.7 ft over a 50 × 150 ft footprint. The lost 15 % is ledgered (sublimation and drift).
- **SG-06 v1, air (SR7, decided 2026-11-07: compressor capacity, no air network).** The snowmaking system has every
  input it needs to run as a real one in Phase 4 (owner): water, pressure **and air**. The pump house has a compressor
  of `Pacing.CompressorCfm = 2,000 cfm` (a second one is an upgrade later) shared by every gun it feeds, with air
  lines assumed to run beside the water pipes (no air pipes, no air solver). Each running gun draws its stage's cfm
  (SG-01); when demand exceeds supply, guns drop out in SG-07's order until it fits (diagnostic 18), so air is a limit
  the player plans around, not a second network to draw.
- **SG-07 v1, drop-out order** when water, pressure or air runs short: by the trail's **priority** (a number the player
  sets per trail; SM-02), then lowest hydrant pressure first, then furthest from the source.

### 8.5 SM · Making snow

- **SM-01, guns belong to trails.** A gun is assigned to the trail whose footprint its own footprint overlaps most
  (changeable by the player). A trail's guns share its schedule and target.
- **SM-02 v3, schedules (SR9, owner 2026-11-07: "they operate as auto now", then "guns also have a manual on/off
  for testing").** Per trail one switch, **Make snow: on / off**, and a priority (1 high – 5 low). A trail switched
  on runs its guns **automatically whenever the weather allows**: they start when the hydrants' wet bulb is ≤
  `Pacing.AutoOn = 27 °F` and stop above `Pacing.AutoOff = 28 °F` (hysteresis), with a **30 min** minimum run (face
  time), in any hour of the day or night, until the trail reaches its target. Each hydrant reads the hour's wet bulb
  plus a keyed offset of ±`Pacing.WetBulbJitterF = 0.5 °F` and each gun starts after a keyed delay of
  0–`Pacing.GunStartJitterTicks = 300`, so plumes and stages come on across the hour instead of 500 in one frame (a
  mountain, not a robot; all keyed, SI-06). Default **aim** is perpendicular to the trail toward its centreline from
  the gun's side, 15° downhill (player-adjustable Later). The trail's **target** is `Pacing.TargetDepth = 24 in` over
  `Pacing.TargetShare = 75 %` of its footprint; the guns also stop when the source hits its minimum pool; switched
  off they never run.
  **Per gun a manual override, `Auto / On / Off`, default Auto** (the trail switch decides). **On** opens the gun's
  valve now, whatever the trail switch, wet bulb, target or minimum run say: it draws its stage's water and air, the
  solve runs (HY-08 trigger), and it keeps running until the player sets it back, the source hits its minimum pool
  or SG-07 drops it for pressure or air (priority still orders that). Above `Pacing.TooWarmF = 28 °F` (SG-01's
  too-warm line) a forced gun makes **no snow**: its water is debited and ledgered as "too warm" loss (SI-10), the
  plume shows, and diagnostic 20 (HY-09) names it — exactly what a tester wants to see, and a mistake a player
  can see too. **Off** closes the gun whatever the trail says. The override is a per-gun command (SI-03) and part
  of the design shape (§12, 4.15); it survives save and load; the day report (TM-15) counts gun-hours forced on
  separately from automatic ones. Timed schedules and a per-trail "run now" stay **Later**; task 4.16 fleshes out
  the automatic rules. Guns on an open trail by day throw snow onto skiers: allowed, a vibe, no penalty in Phase 4.
- **SM-03, the deposit each tick.** Each running gun writes its hour's output × (tick ÷ hour) of SWE into its footprint
  (SG-03) in the 1 m fine tiles, snow type LG (SN-26); the same tick debits its source (WS-05) and the water
  ledger. Overlapping footprints add. The touched tiles are staged per SI-17's backlog, not every tick (500 guns
  dirty a few hundred tiles a tick; nobody sees 0.02 mm land).
- **SM-04, whales and caps** per SN-27; off-trail deposit (a footprint past the trail edge) goes into the natural
  field at the cell under it, so a badly aimed gun wastes snow visibly.
- **SM-05, grooming spreads it** (SN-23) once cured (SN-27); until then the whale stands and skiers route round it
  (its cells count as closed for GU-07 when deeper than 3 ft above the trail's mean).
- **SM-06, the balance.** Water drawn × (1 − loss) = SWE deposited, every tick, to the quantum (SI-10): a test.
- **SM-07, opening earlier.** A night of guns at stage 3 on a 100 ft × 2,000 ft trail with 20 guns at 100 ft spacing
  puts about 2 ft of snow on it: that trail opens (SN-20) the next morning where the ungunned twin does not: the
  acceptance test of task 16.
- **SM-08, the dashboard.** Per source: level, draw, hours to minimum pool; per trail: guns running, stage, depth vs
  target; per pump: flow, head, status; the diagnostics list (HY-09). ≤4 Hz (TM-13).

## 9. BG · Budgets (SR10, taken as recommended)

On the reference PC with the benchmark resort (task 18: the drawn test design plus a snowmaking network), 3,000
skiers and 500 guns:

- **BG-01 v3, the tick:** ≤ **1.5 ms mean, ≤ 4 ms p99** at 1×, **0 B** allocated, with parts that add up: hourly
  slice ≤ 0.5 ms, gun stencils ≤ 0.3, skiers and lifts ≤ 0.3, tile staging ≤ 0.2, snapshot publish ≤ 0.1 (v2's parts
  summed to 1.9 ms inside a 1 ms whole, which 4.03 would have met by shrinking whichever it measured last). At 8×
  that is 120 ms of worker per real second, within the honest-slowdown rule (SI-11).
- **BG-02 v3, a face hour's fields** (table lookups for weather, snow, lakes, trail readiness, the hour's wear over
  ~2 M 8 m cells plus up to `Pacing.FineCellCap = 2 M` 1 m fine cells, about 500 acres of trails): ≤ 40 ms total on
  the reference PC, in slices of ≤ 0.5 ms by day and ≤ `Pacing.NightSliceMs = 2` by night, finishing within
  `Pacing.HourStepMaxTicks = 80` ticks of the roll (so the board lags the weather ≤ 8 s real by day, ≤ 40 ticks by
  night; the lag is now stated, v2 left it implicit), banded (SI-09). A 64 m tile with zero SWE, no precipitation
  this hour and no gun is **skipped by a tile bitmask**, so bare rock in summer costs nothing. Past the cap new
  trails get fine tiles only over their graded footprint and a warning in the report.
- **BG-03 v2, the hydraulic solve:** a 500-gun, 200-pipe network ≤ 20 ms **as a job off the tick** (HY-08), its ≤ 10
  shut-off re-solves inside it.
- **BG-04 v2, gun deposit:** 500 guns' stencils (SG-03) ≤ 0.5 ms per tick; uploads by dirty tile ≤ 0.2 ms per frame
  (snow doc §6).
- **BG-05 v3, skips:** skip to morning finishes the night's remaining face hours within 3 s; skip to the next third
  (TM-14, ~240 face hours) within 20 s on the reference PC with SI-11's idle factor (36,000 motion ticks at 1 ms would
  be 36 s without it), cancellable, with a progress bar.
- **BG-06, open:** a mountain opens with the simulation ready within 0.3 §8's 10 s (the static inputs, SN-01, are
  cached in the terrain cache, which isn't frozen).
- **BG-07, pause, cancel and a paused command:** ≤ 100 ms to the snapshot (0.6 §5).
- **BG-08 v2, memory:** skiers ≤ 2 MB (3,000 × ~600 B with the day record and key ordinals), the 8 m snow fields
  ≤ 40 MB (SWE, density, conditions over the ring), the fine tiles ≤ 32 MB at the cap (16 B per 1 m cell), no
  per-cell weather (WX-02), no growth over a season (a soak test).
- **BG-09, measured from App** or the headless harness (SI-23), never inside `Simulation` (`Stopwatch` is banned
  there); the benchmark forces a scenario (all guns running, a holiday day) by command.
- **BG-10, minimum spec:** the 2016 laptop is the **simulation's** floor: it runs 1× within budget with 1,500 skiers
  and 250 guns; the RTX 2060 check before release (E8) is the frame's floor (0.3 §8).
- **BG-11, the boundary:** snapshot publish ≤ 0.1 ms per tick (≤ 64 KB), staging copies (SI-17) ≤ 0.2 ms per tick (32
  tiles × 12 KB), texture uploads ≤ 0.2 ms per frame, a `SetDesign` fold (SI-21) ≤ 2 ms, measured by the harness.

## 10. The MapLibre ledger

**K** kept · **C** changed · **D** dropped.

| MapLibre | Verdict | Here | Why |
|---|---|---|---|
| Macro clock 40 game s per real s | C | Face clock 40× by day, 240× by night (TM-02) | Same day rate; the night is short |
| Presets 1/2/4/8/16/64 | C | pause, 1, 2, 4, 8 (TM-03) | No fast-forward: a season is played; chairs at 0.75 s are the limit of sense |
| Micro motion 1 / 1.75 / 2.75 micro s per real s | C | Chairs 1×, skiers 2× (TM-04) | The 6-s chair is the anchor; skiers at 2× get their laps |
| Aggregate cohorts above 4×; representative limit 3,000 | D | Individual skiers at every preset (GU-01) | Owner: real lines, real dots; aggregate mode was the thing that failed |
| Representative motion reconciled at events | D | Rides compressed ÷8 with decorative riders (LO-05/06) | One truth clock for skiers; the compression is explicit |
| 08:00–16:00 | C | 8:30 open, 4:15 last chair, 4:30 stop (LO-07) | The accepted HUD mockup |
| 24-week winter from the first Monday on/after 1 Nov | C | 42 face days, Dec 1–Apr 1 baseline, player opens/closes (TM-08) | A season in a few sittings; the label calendar |
| Nights simulated at the day rate | C | 4-minute night, skippable (TM-02/06) | Owner: nights are for adjustments |
| Jumps: Next Opening, Day, Week, Month, Season, Skip to Winter | D | Skip to morning; next month in summer (TM-06/08) | No skipping a played season |
| 1-minute accounting intervals, fractional seat credits | D | Chair-by-chair boarding (LO-02) | The line must step as a chair leaves |
| 8 ms worker slices, 10 Hz publish, snow ≤ 1 Hz | C | 100 ms tick, ≤10 Hz HUD, ≤4 Hz dashboards (SI-04, TM-13) | Same idea, fixed tick |
| New game 1 May; weather in 72 h chunks | C | 1 Oct start, generated weather (TM-11, WX-01) | |
| Weather replayed from MERRA-2/Daymet hours | C | Generated per face day, calibrated per third-of-month (WX-01) | Owner, 2026-11-07; the label calendar can't replay hours |
| Lapse −6.5 °C/km; cold pooling 1.5 °C when clear and calm | → 4.04 | WX-02 leaves these to the weather design | |
| Stull wet bulb | K | WX-02 | |
| Phase thresholds −1 / 1 / 2 °C wet bulb | K | WX-03 | |
| SLR clamp(8 − 0.8 WB°C, 8, 18) | K | SN-05 in °F | |
| Slope retention 15–60° table | D | Snowpack v0's shedding 85–175 % (SN-06) | The owner approved v0's factors (12b); one set |
| Wind retention max(0.55, 1 − (kph − 20)/140), site-wide | C | Wind moves snow downwind (SN-07) | Conservation |
| Settling depth × 0.9985/h | C | Density toward 28 lb/ft³ at 0.0015/h (SN-08) | SWE is conserved |
| Melt 0.55 mm per °C-h; rain 0.35 mm/mm | K | SN-09 in °F and inches | |
| Radiation 1.2e-6 m per W/m²-h, no slope/aspect | C | × the slope's sun factor (SN-09) | The owner's north/south rule |
| Depth cap 4.095 m; covered ≥ 2 cm | C | No cap on natural (SWE as long); covered per SN-20 | |
| Surface ladder: P → PP after one dry hour | D | SN-14 (traffic packs; powder by aspect thresholds SN-15) | The ideas doc's rules |
| 11 surface codes (6 never set) | K | SN-13, all reachable | |
| Quality table P .96 … WP .78 | K | SN-16 | |
| Wear 30 m width, 0.005 mm/pass, slope 0.5–2, 200/800 passes | K | SN-24, SN-14 rule 6 (100 ft reference) | |
| Thin warning 10 cm on 20 %, clear at 10 % | C | Open ≥ 90 % covered, close < 80 % (SN-20) | Auto open/close replaces the advisory |
| Edge usable at coverage ≥ 0.2 (2 cm) | C | SN-20 | It opened grass |
| Grooming quality fixed 0.5; MG never set | C | Grooming per trail nightly (SN-22/23) | |
| No moguls | C | SN-17 | Owner, 2026-10-02 |
| No lake ice | C | §5, new | |
| Lift catalog (rope tow 500 fpm/700 … tram 2000 fpm) | D | Unity's catalog (LC rules, LD2, D/C rules) | The catalog is the source |
| Tram = cabin × 3,600 ÷ (ride + 240 s) | K | LO-08 as the placeholder's derivation | |
| Wait = queued ÷ pph × 3,600 | K | LO-04, in the chair interval's terms | The owner's 300 → 7:30 |
| Lift viable only with a snow-covered return | K | LO-10 | |
| No wind closures | C | LO-11 | Owner wants redundancy to matter |
| Representative lift wait 6 s slot + ordinal stagger | D | LO-02 | |
| Ability: reps uniform 0.2–0.9; cohorts 0.2/0.4/0.6/0.8; no bands | C | 4 bands with a hard gate (GU-05) | Owner's "beginner never picks a black" |
| Legacy bands < .25 / .55 / .82 | K | GU-05 cut-offs | |
| Trail difficulty .2/.45/.7/.92; painted by (3 avg + max)/4 in degrees | C | SlopeBands in % and TD-02 (Phase 3) | |
| Route score: effective difficulty, comfort, compatibility… | C | GU-07's fit × snow × width × crowd | The snow doc's table; fewer terms |
| Hourly arrival weights .38 … 0 | K | GU-06 | |
| Demand Sun–Sat 1300/900…; price elasticity −1.10; 10,000¢ | C | Draw × reputation × day type × conditions × price (GU-03 v4); the −1.10 returns as the price line with the economy | Owner: demand is the mountain's draw, never the lifts; visits never inflated |
| Entrance 12/s within 75 m of a lift base | C | Base stations (GU-06) | NW-03: no guest entrance |
| Stay 4 h + hash % 2 h | K | GU-06 | |
| Speed (0.6 + 0.8 a)(1 + 0.15 Z); base 6/9/11/12 m/s; walk 1.4 m/s | K | GU-10, GU-09, × SkierSpeedFactor | |
| Trail entry 2 micro s apart | C | 1 s (GU-10) | |
| Rest, needs, café, amenities | D | — | E29: no needs |
| Injuries (legacy) | D | — | Ski Patrol is Later |
| Satisfaction 0.9 s + 0.1 q | K | GU-13 | |
| Snowmaking nodes intake/pump/junction/hydrant; 4–24 in; C 120; 50 ft hose; one gun per hydrant | K | HY-01, HY-04 | |
| Hazen–Williams coefficient 10.67 with ft/gpm/in | C | 10.44 (HY-04) | Owner: correct the error |
| Loops drawn, tree solved | C | Loops solved (HY-02) | |
| Elevation every 25 m, ends only checked | C | High points checked (HY-03) | |
| Newton–Raphson, Schur/PCG, tolerances 0.01, 5 stages, line search | K | HY-06 (global gradient) | |
| The non-aborting derivative guard | C | Fixed (HY-06) | A bug |
| Pump H = 3,960 hp η / Q, no shut-off; pump house 1,000 hp 85 % | C | Shut-off cap 1.5× rated (HY-05) | Infinite head at low flow |
| Only limit: 200 psi at the gun | C | + 1,000 psi class, velocity, air lock (HY-07) | |
| Diagnostics 1–11 | K | HY-09, + 12–19 | |
| Gun stages 28/24/19/14/9 °F, 18–58 gpm, 56/16 cfm; 200 psi; throw 30/80/125 | K | SG-01–03 | The owner's table |
| Air display-only | C | Compressor capacity (SG-06, SR7) | |
| Gun model named for a real maker | C | SLE (A2) | Code names only |
| No automation; wet bulb typed | C | Auto schedules by the cell's wet bulb, plus a manual On/Off per gun (SM-02) | |
| Guns made no snow, used no water | C | SM-03, SM-06 | Owner: the whole kahuna |
| Runtime = capacity ÷ draw; intake at full pool | C | Levels fall, intake follows (WS-02/05) | |
| Dam inflow by stream width table; ponds no inflow | K | WS-03, + season factor; ponds get runoff (WS-04) | |
| Lake depth estimator; volume = area × depth | K | LK-02, WS-02 | |
| Pond/dam constants (0.6 m, 3.5 m, 3:1, 2:1, 1:1, 250 m, 15 m, 20 m, 40 m, 100 m²) | K | PD-02, PD-06 in imperial | |
| No spillway | C | PD-07 | |
| Wet-bulb eligible-hours threshold −2 °C | D | SG-01's stages | |
| Benchmarks: a week in 1.7–2.7 s, a winter in 41–54 s (512² grid, 10,000/day) | — | BG | Not comparable: no jumps here |
| 9 px dots, 8 px hit radius | → 4.13 | | Presentation |

**Time models reviewed and dropped (2026-11-07):** a real 150-day calendar with one clock (3D skiers unreadable at play
speed; 80 h a season at a watchable rate); a real calendar with two clocks at ratio 2–4 (23–45 h; the lift card and
the day ledger disagree by the ratio); a composite week shown hour by hour (a sawtooth thermometer, five nights of guns
in one night, ×25 ledger bugs); a decoupled calendar with scripted weather (throws away the site's record). The chosen
model keeps the record as calibration (WX-01) and the clock as a label (TM-08).

## 11. Owner decisions ⛳ and open questions

Decided in the design interview of 2026-11-07: **SR1** the time model (TM-01, TM-04, TM-07, TM-08), **SR2** nights
(TM-02, TM-06), **SR3** the season (TM-08), **SR4** the day and the speeds (TM-02, TM-03), and the pacing principle
(SI-01), individual skiers (GU-01) and the economy per visible skier (GU-14).

Decided in round 1 of the doc review (owner, 2026-11-07, in text):
- **SR5 → the ideas doc's 11 types** (SN-13), labels as written.
- **SR6 → four bands matching the trail colours with a hard gate** (GU-05); and **two ratings per trail** (GU-17): the
  game's true rating is the **steepest sustained pitch** (TD-02 v2, reversing NT34's whole-length average: "a green
  with a cliff in it isn't a green"), the posted rating is the player's.
- **OQ2 → the game rates readiness, the player opens** (SN-20 v2: Ready / Marginal / Bare; Open / Closed / Auto per
  trail); opening Marginal is allowed and costs satisfaction, lightly at the season's shoulders and double
  mid-season (GU-17).
- **OQ3 → a target with a visible multiplier stack, sized by the mountain's draw** (GU-03 v4, Transport Fever's town
  demand). The owner ruled, after v3 proposed open acres with the lifts as a clamp: "the number of skiers who come to
  your mountain is not a direct function of lifts, but how popular your mountain is… location, rep (and within rep,
  archetype: who you cater to, conditions, amenities)… the user can control crowds with ticket prices… while also
  making sure they have the correct infrastructure." So: draw (location) × reputation per band (GU-18, the
  archetype) × day type × conditions × price (1.0 until the economy); lifts and acres are read-only capacity lines
  beside the target, never a clamp; strain costs satisfaction, satisfaction moves reputation, reputation moves
  tomorrow's crowd. This supersedes the interview's "sized to the visible lifts" (that sizing survives only as the
  Lift-capacity readout and as the scale the 3,000 cap and the dots were tuned for).
- Also ruled: **the season starts when the mountain opens** (TM-11 v2: an Open/Closed switch, Day 1 = the first lift
  serving an open run; no "start the season" button); **the season seed** (SI-15, WX-08); **1 m state over trails and
  Areas, 8 m elsewhere, seeded by interpolation when a trail is painted, and a sharp heat-map look** (SN-03 v2,
  SN-28, SN-29); **grooming evens and packs, and a packed base lasts** (SN-23 v2, SN-30).

**The architecture audit (2026-11-07).** At the owner's request ("a back end that runs the data and a front end that
displays it; make sure it can grow and perform") an independent adversarial review read this document against the
0.3 architecture, the roadmap's simulation principles and the existing code. Its verdict: the principles were right
but the worker's boundary with everything else was one sentence, under which the fields raced between threads, the
design had two owners, the replay log couldn't replay, and several per-tick rules couldn't meet their own budgets.
Every finding was folded in: **SI-16…23** (the snapshot, tiles as staged copies, the event ring, the entity board,
the design as a logged input with its derived geometry, the replay header, the headless harness), **SI-03/05/07/08/09/12
v2** (stamped commands, a systems table, SWE in micrometres, player-build goldens, no float Burst twins, save as a
command with a frozen sidecar), **WX-02** (weather as a function, not a stored grid), **SN-24 v2** (wear counted per
segment band, applied hourly), **HY-08 v2** and **SG-03** (the solve as a job with fixed latency; gun stencils),
**GU-02/GU-16 v2** (one record for every skier; the render clock), **LO-06** (skiers are never on carriers), and the
budgets **BG-01…05, 08, 10 v2 and BG-11**. Nothing in it changed a gameplay rule.

**The tycoon review (2026-11-07).** A second independent review, briefed as a lead designer of CS2 / Transport
Fever-class tycoons plus a performance engineer ("a tycoon with good graphics; best performance; the foundation for
the rest of the game"), found the architecture sound and the game not yet playing: demand was defined as lift
capacity so the capacity-vs-demand tension every tycoon runs on couldn't occur; MapLibre's guest constants had been
copied in real minutes into a 12-minute day so waits, balking and satisfaction were inert; and the 4:30 stop had
nothing to read. Folded in: **GU-03 v3** (base = open acres × 5, the lifts as a 1.5× clamp, turned away as a line,
per-band caps: the one change to a gameplay rule, flagged to the owner, who replaced it with **v4**, demand from the
mountain's draw and reputation with no clamp at all; see OQ3 above and GU-18), **GU-06 v2, GU-08, GU-13 v2** (the stay in
laps, leave-the-line at 4 min, balk at 3, satisfaction that moves and carries reasons), **GU-07/GU-11 v2** (per-band
reachability to a base, no stranded beginners), **TM-15/16** (the day report, season history, a scenario seam),
**SI-10** (an operations ledger in kind), **SI-11 v2** (coarsening by state, never by load), **SI-16/17, SM-03, GU-02,
BG-01/02/05/11 v3** (tile staging once a second with render planes only, the day record out of the snapshot, budgets
whose parts add up, the hour-step lag stated, idle tiles skipped), **WX-02** (weather tables by elevation bin, aspect
and canopy), **SM-02, SN-23, LO-11** (keyed jitter against lockstep guns, grooming and holds), **LO-02, LO-06, GU-10**
(queued render records, release at the next chair, speed along the segment), **SN-04/07/09** (spring state on the
8 m cell, drift over five cells, a 0.1 in crumb) and **SN-31** (the Traffic layer).

Decided in round 2 (owner, 2026-11-07, in text): **the snowmaking system has every input it needs to operate
normally in Phase 4 — water, pressure and air — and the guns run automatically** when the weather allows on the
trails the player has switched on. So **SR7 → compressor capacity in the pump house, no air pipes** (SG-06) and
**SR9 → automatic, plus a manual On/Off per gun** (SM-02 v3: one Make-snow switch and a priority per trail; each
gun `Auto / On / Off` so a tester, or a player, can open one valve by hand and watch the water, air and pressure
answer; timed schedules Later; 4.16 fleshes the automatic rules out — the owner added the manual switch in text
after the first ruling: "guns also have a manual on off for testing"). The owner did not rule on the three below; the
recommendation stands for each, in the same spirit (the simplest form that is real), and the owner may overrule at
the PR:

- **SR8, pipe loops or a tree.** (a) **Loops allowed and solved** (HY-02, HY-06), *taken*: the global gradient method
  handles both, so the only cost is a solver that doesn't assume a tree; loops are how real mountains keep pressure
  up, and refusing them is more UI work than solving them. (b) Tree only: simpler diagnostics, and a player who draws
  a loop is refused.
- **SR10, budgets.** (a) **§9 as written**, *taken*. (b) Looser (tick ≤ 2 ms, fields ≤ 80 ms): easier for 4.03, but 8×
  then stalls on the benchmark resort. (c) Tighter (tick ≤ 0.5 ms): not needed with no jumps.
- **OQ1, wind holds in Phase 4.** (a) **Yes, per-type gust thresholds with hysteresis** (LO-11), *taken*: it is the
  first reason to keep a fixed grip beside a detachable, which the owner wants, and it is one rule reading one board
  column. (b) Later, with maintenance downtime.
- **Open questions for later tasks, recorded here:** how a trail's conditions rating reads (4.07, owner at task 07);
  the choice weights (4.12); the per-type hold gusts (4.11); the inflow season factors (4.14); the sustained-pitch
  window and whether the Slope tool's chips (BR3) follow TD-02 v2 (the Domain change, see §13).

## 12. Formats to bump (for the coordinator to sequence; next free save is v13)

| Task | What grows | Format |
|---|---|---|
| 4.03 | Simulation state: tick, face clock, label calendar, the replay header (SI-22), key ordinals, the weather generator's state, the slice cursor, skier arrays, lift lines and holds | Save: `save.json` plus the **sim sidecar**, a new frozen binary format with its own `SidecarVersion`, migration steps and `v<N>-sim` fixtures (SI-12 v2) |
| 4.05 | The weather calibration rows per third-of-month | Package manifest (a weather chunk) |
| 4.07 | SWE, density and the conditions record at 8 m and in the 1 m fine tiles (SN-03); the trail grooming setting (SN-22) and the Open / Closed / Auto switch (SN-20) | Snow sidecar (binary, not JSON); the design shape |
| TD-02 v2 (before 4.12) | The posted rating per trail (GU-17); the true rating is derived, so the Domain change alone bumps nothing but the golden ratings | Design shape |
| 4.09 | Ice and snow per body; body IDs | Save |
| 4.11 | Lift schedules and hour overrides (the fields came early: design shape 12, save v12, task 4.03) | Design shape |
| 4.12 | The four band reputations r_b (GU-18), `SeasonHistory` (TM-15); `LocationDraw` per demo package (GU-03) | Sim sidecar; package manifest |
| 4.14 | Ponds, dams, selected lakes; stream vectors | Design shape; package (WS-08) |
| 4.15 | Pipes, nodes, pumps, hydrants, guns (each with its `Auto / On / Off` override, SM-02) | Design shape |
| 4.16 | Trail schedules, priorities, targets; machine snow in the fine tiles | Design shape; snow sidecar |
| 4.17 | — (view.json keeps its time for the sun only) | — |

Suggested batching: 03 + 11 together; 14 + 15 + 16 together; 07 + 09 together.

## 13. Contradictions, who implements, later

**Contradictions with earlier documents, to settle as noted:**
- **Phase 4 plan, task 03** lists jumps (next opening, day, week, season) and "chairs move on game time": replaced by
  TM-06 (skip to morning only) and TM-04 (chairs on the motion clock at real speed).
- **Task 04/05**: "how a game year is made (MapLibre's resampler, a generator, or a replay)" is decided: a generator
  calibrated on the record (WX-01); 05's "the same seed gives the same year" holds.
- **Task 12**: "aggregate flows at high speeds" is dropped (GU-01); "3,000 guests in structure-of-arrays" stands.
- **Task 17 and P2-02**: the four speed keys (60/180/600/1800 game s per real s, 1–4) become pause, 1, 2, 4, 8
  (TM-03); the key map changes; the HUD's "Day N from Sep 1" becomes the label of TM-08.
- **E28** ("start from the MapLibre dual clock"): honoured as the ledger shows (§10); the micro clock became the motion
  clock and the macro clock the face clock, with guests on one of them.
- **RP-07 v1** (a chair's position is a pure function of lift, clock and index): v2 makes it a function of the rope's
  travel, which stops on a hold (LO-11).
- **The snow doc §4.2–4.3** (7 looks, a 7-type legend): reworked at 4.08 under SR5.
- **CF-06** ("construction time comes with the simulation") and **BR19**: construction stays instant in Phase 4
  (SI-14); time and cost come with the economy (Later), as the plan's §8 says.
- **NT33** (trees regrow on removed trails): the exit review assigns it to Phase 4, the plan to Later; not in these
  rules; the coordinator decides.
- **`TerrainEditPipeline`** recomputes the snowpack in Presentation today: under SI-03 that becomes a command to the
  simulation (SI-13).
- **0.2 §5** ("winter only; the date moves the sun, not the season"): superseded by TM-08.
- **NT34 and TD-02 v1** ("a trail is rated over its whole length, (3 × mean + max) / 4"): the owner reverses it
  (2026-11-07) for **TD-02 v2, the steepest sustained pitch** (GU-17). This is a Domain change (`TrailRating`,
  `NetworkReport.SegmentRating`, TD-06's painting estimate, the profile strip's shading) that changes the colour of
  trails players have already drawn, so it needs its own small PR with the golden ratings re-pinned, sequenced by the
  coordinator before 4.12; BR3's Slope chips keep reading the band the trail is given.
- **TM-11 v1 and the plan's "Start the season"**: there is no such button; the mountain's Open/Closed switch and the
  first lift serving a run start the season (TM-11 v2).

**Who implements what:**

| Task | Rules |
|---|---|
| 4.03 Simulation core | SI-01…23 (the boundary, the systems table, the event ring, the board's shape, the headless harness), TM-01…16 (the day report and season history), WX-06, WX-08's call order, BG-01, BG-05…09, BG-11 |
| 4.04 Weather design | WX-01…05, WX-07, WX-08 |
| 4.05 Weather engine | WX-02…05 as built; BG-02's sampling |
| 4.07 Snow | SN-01…25, SN-28, SN-30, SN-31's counters, BG-02 |
| 4.08 Snow look | SN-03's atlas, SN-13's looks (SR5), SN-27's displacement, SN-29's two-resolution draw |
| TD-02 v2 (own PR) | GU-17's true rating in Domain; the posted rating attribute |
| 4.09 Lakes | LK-01…09 |
| 4.11 Lift operations | LO-01…13 |
| 4.12 Guests | GU-01…15, GU-17, GU-18 (reputation per band), SN-24's passes |
| 4.13 Guests on screen | GU-16 (the render clock), LO-02's queued records, GU-10's speed table, the skier card from GU-02's `InspectSkier` events, SN-31's Traffic layer |
| 4.14 Ponds, dams, water | WS-01…08, PD-01…09 |
| 4.15 Pipes, pumps, guns | HY-01…10 (HY-08's solve job), SG-01…07 (SG-03's stencils, `GunCatalog`) |
| 4.16 Making snow | SM-01…08, SN-26…27 |
| 4.17 Sim bar, tabs | TM-03's controls, TM-06's night, TM-12's alerts, TM-13, TM-15's day report and trend, GU-03's stack, GU-13's reasons, LO-04's readout, SM-08, WS-05's days left |
| 4.18 Benchmark | BG-01…11 |

**Later (not Phase 4):** 3D skiers on snow (GU-16 carries them; riders on chairs stay decorative, LO-06); the economy (tickets per skier, GU-14;
operating costs by lift type; gun and pump prices, SG-04); a second compressor (SG-06), timed gun schedules and a per-trail "run now" (SM-02); unlocks and the tech tree; construction time (CF-06) and
cost; maintenance downtime by lift type (the detachable's weakness beside the fixed grip); ticket price, passes,
amenities, lodging, marketing, local vs destination and holiday pull as further lines in GU-03's stack and GU-18's
inputs; ticket caps and sell-outs (the turned-away line); real skiers on chairs only as a `RideCompression = 1` tuning (LO-06); animated groomers and snowmobiles as agents; summer
operations; "first to open / last to close" challenges (WX-07's rows); Ski Patrol's reasons for closing (SN-20 gains
them); a sandbox mode (unlimited money, everything unlocked); an omniscient forecast option.
