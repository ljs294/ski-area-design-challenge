# Phase 3 · 13 Detailed Phase 4 plan: the resort comes alive

**Audience:** the project owner and coding agents. **Status:** for your review (task 13 ⛳), in one PR with the [Phase 3 exit review](phase3-13-exit-review.md).

This plan replaces the Phase 4 section of [0.6 §5](phase0-0.6-milestones.md#5-phase-4-simulation-foundations-iteration-3):
- Its goal and exit criteria are kept.
- Your answers of 2026-11-05 widen it to the full weather simulation, the whole snowmaking system, and guests as dots. These are E-decisions, pending numbers in the exit review.
- Surface lifts are wired in their own thread, outside this plan.
- **Phase 4 starts when you approve this document.**

**⛳** marks a gate: the plan waits there for your approval.

**Goal:**
- Time runs. Weather comes from real history; snow builds, wears and melts; lakes freeze and thaw.
- 3,000 guests ride the lifts and ski the trails, choosing by the snow.
- The player builds a snowmaking system (water, ponds, dams, pipes, pumps, guns) that makes real snow.
- The same resort, seed and commands always give the same result.

## 1. How Phase 4 runs

- **As in Phase 3** ([Phase 3 plan §1](phase2-14-phase3-plan.md#1-how-phase-3-runs)):
  - one topic branch per task (`feature/p4-NN-…`) from origin/main;
  - your approval of every PR, with demo steps and a `demo.bat` entry;
  - every PR passes repo checks, `dotnet test`, EditMode and PlayMode, `compile-check.ps1`, and demo 41 against the baselines for anything that renders.
- **Plan first (E15):** every task posts a 🟦 plan in its own session and waits for your go. If the scope changes, it re-plans. Updates are labelled 🟦 Planning, 🟩 Implementing, 🟨 Your review.
- **Threads:** each task gets its own session and messages the coordinator only at start and finish.
  - The GPU goes through `tools/gpu/gpu.ps1` (`measure` alone; `render` two at once).
  - Each thread merges origin/main itself.
- **Weather gets its own design session on Fable** (your request): task 04 runs on `claude-fable-5-1` and ends in a design document you approve before any weather code.
- **Model builds** (the pump house, a compressor house if chosen) go to a forked model thread, owner-gated in the usual three gates. Makers by code name only (SLE for guns).
- **The simulation rules** (all engine-free, in `Simulation`):
  - a fixed tick;
  - keyed hash randomness only;
  - stable order;
  - no per-agent allocation (structure-of-arrays, pooled buffers);
  - snapshots out to Presentation and UI, commands in.
  Nothing outside `Simulation` changes its state.
- **Frozen formats (0.3 §5):**
  - The save grows: simulation state, ponds, dams, pipes and guns.
  - The package manifest grows a weather chunk.
  - Each change is a version bump, a `VersionedJson` migration and a fixture. **The coordinator sequences the bumps**, including the surface-lift thread's; never two threads on one format.
- **Mockup first** for every new panel (the Snowmaking tab, the sim bar's jumps, the Guests and Weather tabs, dashboards), then the parity check (demo 48).
- **Free tools and free data only.** Daymet and NASA POWER are free. Their terms are checked in task 05, as in 0.3 §6.

## 2. Task order

```
01 Carry-over (first, any time)

02 Simulation rules ⛳ ─► 03 Simulation core ─┬─► 04 Weather design ⛳ (Fable) ─► 05 Weather engine ─┬─► 06 Weather on screen
                                             │                                                   ├─► 07 Snow model ─► 08 Snow look ─┐
                                             │                                                   └─► 09 Lakes freeze/thaw ──────────┤
                                             │                                                                                     ▼
                                             │                                                               10 Style tile: a winter week ⛳
                                             ├─► 11 Lift operations ─► 12 Guests v1 ─► 13 Guests on screen ──────────┐
                                             └─► 14 Ponds, dams, water ─► 15 Pipes, pumps, guns ─► 16 Making snow ───┤
                                                                                                                     ▼
                                     17 Sim bar, Guests tab, dashboards ─► 18 Benchmark ─► 19 Tester build ⛳ ─► 20 Exit ⛳
```

- Three lines run in parallel once 03 lands: **weather and snow**, **lifts and guests**, **snowmaking**.
- 12 reads 07's trail ratings: it starts with a stub rating and switches when 07 lands.
- 16 needs 05 (wet bulb, wind) and 08 (the 1 m managed depth).
- 14 needs 09's water-body map.
- Lift operations (11) cover every lift type in the catalog, surface lifts included once their thread merges.

## 3. Tasks

| # | Task | Deliverables | Acceptance | Est. |
|---|---|---|---|---|
| 01 | **Carry-over** | Soil works (earthwork, clearing) as a lift cost line through `LiftCost.With`. The Guests tab tooltip bug (it shows the Network text). Backlog items you pick from the exit review (the trail look's 2 km window, brush garbage, the long-run grader) | Each item fixed with a test, or moved to Later with your OK | 1 wk |
| 02 ⛳ | **Simulation rules** | `docs/plans/p4-02-sim-rules.md`, re-specified from the MapLibre dual clock and operations design, your ideas doc and the snow design doc. Covers: **time** (game and animation clocks, speeds, operating hours, nights, the season, jumps); **snow** (accumulation, compaction, melt, conditions types, grooming, wear, moguls); **lakes**; **lift operations**; **guests v1**; **snowmaking** (sources, earthwork, hydraulics, guns against wet bulb, output to snow). Rule IDs; open questions for you | Your approval. Every rule has an ID later tests cite. Each MapLibre number kept, changed or dropped, with the reason | 2 wk |
| 03 | **Simulation core** | A `SimulationHost` on a worker thread. A fixed 1-minute game tick, hourly fields, an ordered event queue. Double-buffered snapshots out and a command queue in. The real `IGameClock` replacing `ViewClockRunner` as the sun's driver. Speeds and jumps (next opening, day, week, season), cancellable. Simulation state in the save (bump). Chairs move on game time | Same seed and commands give the same snapshot hashes over a simulated week (golden). A one-week jump is within 02's budget. 0 B per tick. Pause takes effect within 100 ms | 2 wk |
| 04 ⛳ | **Weather design (Fable session)** | `docs/plans/p4-04-weather-design.md`: sources (Daymet daily 1 km, NASA POWER hourly), blending, how a game year is made (MapLibre's resampler of real days, a generator, or a replay), elevation lapse and inversions, wind, wet bulb, storms and events, forecasts, difficulty, what the player sees | Your approval | 1 wk |
| 05 | **Weather data and engine** | An Acquisition provider for the chosen sources, with recorded fixtures. A weather chunk in the area package (manifest bump), plus backfill for areas already downloaded. The engine in `Simulation/Weather` per 04: hourly fields over the map, wet bulb, wind, precipitation type, forecasts | The same seed gives the same year (golden). Offline after download. Provider terms recorded. Fields recompute hourly, never per frame | 2.5 wk |
| 06 | **Weather on screen** | Cloud cover in the lighting, falling snow and rain, fog and visibility, wind in trees, gun plumes bending in the wind. The HUD's weather readout; the Weather Analysis tab (mockup first) | Within frame budget at High and Medium (demo 41). The readout matches the snapshot | 1.5 wk |
| 07 | **Snow model and the conditions map** | Natural depth (8 m) over time from the weather: accumulation, wind loading, compaction, melt by sun, aspect, temperature and rain. **The one 8 m conditions map**: type, quality, moguls, age; the type list per 02. Overnight grooming of groomed trails; traffic wear from guests (after 12). Trail conditions ratings | Golden depth and conditions over a recorded week. The Snow conditions info layer goes live and the cursor readout reads it. Dirty-rectangle uploads only | 2 wk |
| 08 | **Snow look** | The terrain shader reads the conditions map (powder, groomed corduroy, ice, spring, moguls). Displacement by depth if not already in. Managed depth: sparse 1 m tiles, atlas and page table, shape only. Whales painted by command for the style tile | Within the snow design doc's budgets (≤0.1 ms displacement, ≤0.2 ms uploads). Captures from the benchmark views | 1.5 wk |
| 09 | **Lakes that freeze and thaw** | A water-body ID map (one body per lake or pond, not `AllWater`). Ice growth and melt by degree-days, snow on ice, per-body state into `WaterBodies` | A recorded winter freezes and thaws each lake on its own dates (golden). The renderer needs no change | 1 wk |
| 10 ⛳ | **Style tile: a winter week** | On Jackson Hole 5 km, by command: a storm, a clear cold day, a thaw and refreeze. Conditions on the trails, a lake freezing, whales, a few hundred guest dots. Captures and one short review movie | Your approval of the look. Benchmark within 10% of baselines | 1 wk |
| 11 | **Lift operations** | Per lift: line speed, 6 s loading, carriers, capacity (pph), queue, wait time (queue ÷ pph), operating hours, open/close commands. Riders hidden while riding, out at the top after the ride time | A quad at 6 s gives 2,400 pph (test). 300 in line gives a 7:30 wait (test). Same day, same throughput (golden) | 1.5 wk |
| 12 | **Guests v1** | 3,000 guests in structure-of-arrays. Arrivals through the day, ability bands, no needs. Trail choice by conditions × steepness × width × ability over the directed network. Queue, ride, ski, leave. Aggregate flows at high speeds; the selected guest stays detailed. A per-guest day record kept for future ratings | 0 B per tick with 3,000 guests (test). Golden trajectories over a day. A beginner never picks a black (test over a fixture) | 3 wk |
| 13 | **Guests on screen** | Guests as 2D dots (one instanced draw, coloured by ability), heights from precomputed samples, smoothed between snapshots. Picking by a screen-space hash; a guest card | 3,000 dots within frame budget. Pause and selection p95 ≤100 ms | 1.5 wk |
| 14 | **Ponds, dams and water** | A Toolbox Snowmaking tab (mockup first). **Ponds:** the ring is the waterline, the berm is outboard, a signed-distance envelope drives grading and the cut/fill bill. **Dams:** across OSM streams. Lakes switchable as sources. Volume, inflow and recharge; drawdown over time. Saved (bump) | Earthwork matches 02's rules on recorded fixtures (golden heights). Volume measured on the graded ground. Undo restores heights exactly | 2.5 wk |
| 15 | **Pipes, pumps, hydrants and guns** | Pipe drawing (diameters), intakes, inline pumps, a pump house (the first building, from the model thread), hydrants by spacing. SLE stick guns (10, 20, 30 ft) and ground guns on hydrants; air per 02. A hydraulic solver (Hazen–Williams; loops per 02) with pressure at each gun and a pressure overlay. Saved (bump) | The solver matches hand-checked networks (tests). Diagnostics for every failure MapLibre listed. A 500-gun network solves within 02's budget | 2.5 wk |
| 16 | **Making snow** | Guns run in the simulation: output by wet bulb and pressure. Snow lands downwind into managed depth (whales up to 8 m). Water drains from sources. Schedules per 02. Overnight grooming spreads whales. The Snowmaking dashboard | A night of guns opens a trail on thin natural snow earlier than without (test). Water and snow balance (test). 500 guns running within budget | 2 wk |
| 17 | **Sim bar, Guests tab and dashboards** | The sim bar with speeds and jumps. The Guests tab (totals, where guests are, by ability); live open-lift, open-trail and guest counts in the status bar. One dashboard (mockup first) | Every control works by keyboard and mouse at 50–150% (PlayMode). UI updates at bounded rates, not per tick | 1.5 wk |
| 18 | **Benchmark: guests and snowmaking** | The drawn test design plus a snowmaking network; the path with 3,000 guests and 500 guns running; tick timing | Frame budgets met at 1080p High and Medium. Tick and jump budgets met. Results in `docs/perf/` | 0.5 wk |
| 19 ⛳ | **Tester build (`v0.4.0`)** | A tagged release; a no-help tester session: open, run a week, watch guests, build snowmaking and see it make snow. Findings, and fixes for blockers | The tester finishes without help, or each block is fixed or decided | 1 wk |
| 20 ⛳ | **Phase 4 exit** | Exit review, velocity, the Phase 5 plan, the `unity-m4` tag and the `archive/unity` fast-forward | All criteria met, or a decision on each miss | 0.5 wk |

**Total:** about **32 task-weeks**.
- That is above 0.6's 4–6 months (17–26 weeks), because the full weather and the whole snowmaking system were added (2026-11-05).
- Without the snowmaking tasks (14–16) it is about 25.

**Calendar forecast:**
- Phases 1, 2 and 3 each ran about **9×** faster than their task-week estimates. Phase 3 took 14 days to its tester build and 20 days in all against 17.5 task-weeks ([exit review §2](phase3-13-exit-review.md#2-velocity)). At that ratio, Phase 4's build work is about **3.5 weeks**.
- The gates wait on you and don't compress: the rules (02), the weather design (04), the style tile (10), the tester build (19) and the exit (20), plus the mockups and model gates.
- Simulation is new modelling work with more gates than Phase 3: the weather design session, the snowmaking models and the tester build. **Forecast: 5–8 weeks** of calendar time.

## 4. Exit criteria

From 0.6 §5, plus your additions:
1. The simulation tick is within budget, and golden trajectories are stable (same seed and commands, same snapshots).
2. Frame budgets are met with 3,000 guests visible and 500 guns running (task 18).
3. Pause and selection p95 ≤100 ms.
4. A winter plays from history: snow builds, wears and melts, conditions change, and lakes freeze and thaw.
5. A snowmaking system draws water from a pond or dam, solves pressure, and makes snow that opens a trail earlier.
6. A tester runs a week, watches guests and builds snowmaking without help (task 19).
7. Velocity is recorded, and the Phase 5 plan is written.

## 5. Owner decision points

| When | What you decide |
|---|---|
| 01 | Which backlog items ride along |
| 02 ⛳ | **Pace:** MapLibre's 40 game s per real s × 1/2/4/8/16/64, or today's 60/180/600/1800; whether nights fast-forward; the season's dates; the jumps. **Snow:** the conditions type list (your ideas doc's 11 or the snow doc's 7). **Guests:** ability bands. **Snowmaking:** compressed air or not, pipe loops or tree only, automatic or by-hand gun schedules. **Budgets:** tick and jump |
| 04 ⛳ | The weather design: sources, how a game year is made, forecasts, difficulty |
| 07 | How a trail's conditions rating reads |
| 10 ⛳ | The look of a simulated winter week |
| 12 | How guests choose trails (weights by ability) |
| 14–15 | The Snowmaking tab and the pond, dam and pipe tools (mockups first); the pump house model |
| 17 | The sim bar, Guests tab and dashboard (mockups first) |
| 19 ⛳ | The tester, and whether a finding blocks the release |

**Inbox triage (your ideas doc, confirmed 2026-11-05):**

| Idea | Where | Why |
|---|---|---|
| Ski Patrol | Later | Needs buildings, staff and the economy; Phase 4 gives it guests, weather and snow |
| Ski School | Later | Needs ability levels (Phase 4) and the carpets (surface lift thread) first |
| Lodges | Later | Needs a buildings tool, guest needs and the economy |
| Terrain parks | Later | Could start as a trail type with the terrain brush |
| Nordic | Later | A Nordic trail type and a new guest kind |
| Racing Academy | Parked | Needs school, race trails, buildings and reputation |
| Resort Infrastructure | Later | Roads, parking, village; guests arriving by road |
| Resort Metrics | Later | Phase 4 keeps the per-guest day record the ratings will need |

## 6. Test data and hardware

- **Unchanged:** Jackson Hole 2 km (committed), the Jackson Hole 5 km demo, Crystal Mountain, Sugarloaf, Palisades Tahoe; the Phase 3 task 11 test design.
- **New, small and committed:**
  - recorded weather for the test sites (Daymet and POWER responses);
  - a recorded winter for goldens;
  - hydraulic test networks with hand-checked answers;
  - pond and dam earthwork fixtures;
  - golden trajectories;
  - a snowmaking test design;
  - save and package fixtures for each bump.
- **Hardware:** the reference PC and the 2016 laptop. The RTX 2060-class check stays before the first public release (E8).

## 7. Definition of done (every task)

As in the [Phase 3 plan §7](phase2-14-phase3-plan.md#7-definition-of-done-every-task), plus:
- The 🟦 plan was approved before implementation (E15), and the PR links to it.
- Each rule implemented cites its 02 or 04 rule ID in a test.
- Simulation code is engine-free and changes state only through commands. A test shows 0 B per tick at full load.
- Randomness is keyed by hash, and a golden test pins the output.
- Every save or package change follows 0.3 §5 and was sequenced by the coordinator.
- Rendering PRs state before-and-after benchmark numbers, and simulation PRs state tick numbers.

## 8. Later (not in Phase 4)

- **Before the first public release:** the RTX 2060-class check (E8), code signing (E11), the "Alpine Labs" trademark search (E12).
- **From your Inbox:** Ski Patrol, Ski School, Lodges, Terrain parks, Nordic, Resort Infrastructure, Resort Metrics (ratings, reputation, fame). **Parked:** Racing Academy.
- **Economy:** tickets, wages, operating and build costs (lift costs exist), the unlock tree (NT21).
- **Guests:** needs (cold, hunger), guests seen on chairs, 3D guest models.
- **Snow and grooming:** animated groomers, and trees regrowing on removed trails (NT33).
- **Infrastructure:** gondola, tram and detachable-eight wiring; buildings beyond the pump house; summer operations.
- **Forest and trees:** as in the Phase 3 plan §8.
