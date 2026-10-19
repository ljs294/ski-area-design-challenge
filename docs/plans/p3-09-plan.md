# Phase 3 · 09 Lifts: plan

**Status:** approved by the owner on 2026-10-19 ("Proceed"; questions 9–11 as recommended, LT9–LT11). Revision 1 was answered on 2026-10-19 (questions 1–8, decisions
LT1–LT8 below) and then reviewed by three adversarial reviewers (rules, engineering, player experience); their findings
are folded in (§11). Branch `feature/p3-09-lifts` from origin/main 738d5b4, `demo.bat` 68.
**Spec:** [Phase 3 plan](phase2-14-phase3-plan.md) row 09; rules LF, LC, RP, TW, CL-03/CL-09 and GR-11 v2 in
[p3-02-design-rules.md](p3-02-design-rules.md) §5, with P3-07's revisions (SK13–SK30).

## 1. Scope and the files I own

On main already (P3-03, 06, 07): the lift document and commands, `LiftBuild` (pads, a stand-in tower placement, TW-16
v3's station towers, the trench, clearing), `LiftRope`, the posed trains, `LiftView` (chairs drawn per lift) and the New
lift tool's steps 1–2. Task 09 makes it complete and real for anything a player draws.

**Owned:**
- Domain (`Runtime/Domain/Design/`): `Lift.cs`, `LiftCommands.cs`, new `LiftEditCommands.cs`; `LiftRope`, `LiftBuild`,
  `TowerPlacement`, `TerminalEnd`, `LineGround`, `LiftLineEnvelope`, `LiftClearing`, `TerminalPad`, `RopeOverSheaves`;
  new `LiftCatalog`, `TowerForces` (TW-04/05), `TowerInHand` (TW-09/11/13), `LiftEarthwork` (P3-08's replay hook),
  `LiftClearance` (for P3-08's TF-11).
- Persistence: the save v2 bump (`SaveFile`, `SaveJson`'s design and lift parts, the migration, `TestData/formats/v2-save`).
- World: `World/Lifts/LiftChairJob.cs` (Burst; World already references Burst, Presentation doesn't).
- Presentation: `Presentation/Lifts/**`, new `LiftChairs` (every lift's chairs, drawn instanced), new
  `Presentation/Design/LiftMapLines.cs` (built lifts on the map).
- UI: `UI/Tools/NewLiftTool.cs`, new `EditLiftTool.cs`, `RemoveLiftTool.cs`, `LiftProfile*.cs` (LF-10 strip, TW-11
  gauges); `UI/Hud/MountainHud.Lifts.cs` (the Lifts layer row and Analysis › Lifts).
- App: `MountainViewer.Built.cs` (the lift part), `-liftbench`, StyleTile's in-memory `TerminalLevels` removed.
- Tests: `Tests/Core/Lift*Tests.cs`, `TestData/lift-profiles/**`, PlayMode lift tool tests. Docs: this plan,
  `phase0-decisions.md` (LT), a `p3-02-design-rules.md` revision block for task 09, `demo.bat` 68.

**Shared with P3-08 (separate files or partials; interface changes agreed through you):** `ToolCatalog`, `ToolContext`
(§3's preview coalescing), `ReviewModel`, `DesignCommand`, `MountainHud.Layers.cs` (the Lifts row and the Labels row),
`DesignOverlay.cs` (it keeps plans only; built lifts move to `LiftMapLines`, built trails to P3-08), the Analysis tab,
`KeyBindings`, `ui-layout.html` (the lift regions only).

**Not mine:** trail grading, trail lines and names, models (a model change goes to a Blender thread with your sign-off).

## 2. Placement flow (LF-01 to LF-12)

**Both tower modes from day 1 (LT1):** Automatic (the game places every tower) or By hand (you place every tower).
The mode is per lift and already saved; no per-tower flag is needed.

1. **Bottom terminal** (LF-02): the ghost follows the pointer, green or red by LF-08 v2 (lakes, roads, core box; steep
   sites allowed, the terminal raised). Click 1 anchors it.
2. **Top terminal** (LF-03): click 2 fixes the line. Red with the rule's own reason: lower than the bottom; under 300 ft
   apart (LF-08); "Too much rise for this lift: tension at the top 112% of rating" (RP-05). In Automatic the towers
   appear live while aiming.
3. **Towers:**
   - Automatic shows the placed towers. **Re-place towers** re-runs it (LF-06).
   - By hand: the tower in hand slides up the line from the last tower, with the green band (TW-09), the head and load
     line (TW-13) and its gauge (TW-11). **Fill the rest** completes the line automatically (LF-04).
   - A placed tower can be selected, slid, re-heighted or deleted. **A red tower blocks Break ground** (LF-04).
   - The switch: to By hand keeps the towers; to Automatic re-places them, asking first if any were placed by hand
     (LF-01).
   - The **lift profile strip** (LF-10: ground, terminals, towers, loaded rope, the clearance track against a dashed
     15 ft line with failing spans red, the tower in hand, the green band, Fit/True scale) and the **tension bar**
     (RP-06) show while building and editing.
4. **Review** (LF-06): length, rise, ride time, capacity, towers, lowest chair, tension; Break ground, Save plan, Cancel.

**Keys** (every one rebindable through KeyBindings; terrain points stay mouse-only, P3-06 ruling 7): ← / → slide the
tower in hand or the selected tower 5 ft (Shift 50 ft); Page Up / Page Down its height ±5 ft (a selected terminal's
level ±1 ft); Tab / Shift+Tab select the next / previous tower or terminal; Enter places the tower in hand, or goes on
to Review; Delete removes the selected tower; Esc backs out one step.

**Orientation (your 2026-10-18 ruling):** each end is placed by where its bullwheels are: the Sessellift's drive at the
top, the **Monta FG4's drive at the bottom**. The catalog names the model at each end; the view stops assuming drive at
the top.

**Editing a built lift (LT2):** **Edit lift** moves towers (along the line and in height) and moves **terminals**; it also
holds the type switch (`SetLiftType`) and the terminal level (`SetTerminalLevel`, SK14). Changes are a **draft inside
the tool** (as P3-06 ruled), with in-tool undo; **Apply** commits one command with its rebuilt earthwork, so one Ctrl+Z
undoes the whole edit. The engineering detail (strip, gauges, tension) shows while editing and hides on the built
lift's card (LF-11). How terminals move is question 9.

**Remove lift:** removes the lift, its earthwork and its clearing in one undoable command, with a click-again confirm
(no money in Phase 3). Edit and Remove open from the Toolbox or from the lift's focus card (the mockup's path).

**Save plan** (CF-06, CL-06): a planned lift writes no earthwork or clearing; Break ground on it later builds them.

**Stale work** (CF-03, CF-05): every analysis result carries its `AnalysisStamp` (design and terrain revisions); a
superseded result is dropped, and a command against an old revision is rejected whole.

## 3. Tower engine and load cases

- **Catalog (LC-01/02):** one `LiftCatalog` entry per type with each end measured on its model. The Monta gets its own
  ends (bottom drive, top return). Its top return's single deflection sheaves are a fixed row with their angle range
  measured from the sheaves (LT6). Where the rope is tensioned on the Monta is question 11.
- **Forces (TW-04, TW-05):** at every tower and terminal row, per rope side, in both load cases (RP-04): the bend δ,
  the head load 2T·sin(δ/2) shared over its sheaves. **The bend one sheave may take is the smaller of 4.5° and
  2·asin(1,500 lbf ÷ 2T)**, so at the counterweight's 18,000 lbf it's 4.5°, and near the top of a 1,000 ft rise
  (about 46,000 lbf) about 1.9°: a b8 then turns about 15°. The group load stays ≥750 lbf (seated) on support groups,
  and in reverse on hold-down groups. The head is the smallest that carries it: s4, s6, b8, d8, or c8 where the sign
  flips or the group sits within 750 lbf of zero. Physics decides the mix (LT5).
- **TW-06:** a bend too sharp for one head gets a partner on the bend's side (or the other, round 4), else red with
  its reason.
- **TW-12 automatic placement** replaces the stand-in: 30–40 ft towers, fewest towers, even spacing on ties, 5 ft
  grid, never red; support on long steep spans and crests, and the small TW-01 shortfalls past a low zone (SK29). A line
  nothing fits stays red with a named reason, never a silent clearance failure.
- **Budgets first:** before building anything, I time today's `LiftBuild` over P3-07's 32-line fan (with a solve
  counter). TW-12's targets (a 1-mile line ≤50 ms, a tower move ≤0.5 ms, TW-09 ≤2 ms) are then checked against that.
  The live preview runs placement only (no pads or trench), with **one analysis in flight per tool plus the latest
  input**, and a cancel flag between attempts (`ToolContext` today starts a task per call and never cancels one).
- **TW-07 span rule (SK29):** no fixed number; spans fall out of clearance and head loads.
- **TW-14 leaned** uses the imported l025–l150 bases (wired at 07). A leaned tower moves only its head; its base stays
  at its stored position (what P3-08's TF-11 rounds read).
- **Rocking the trains (C10):** each train posed to the drawn empty rope when the lift is built; no per-frame cost.
- **Determinism:** everything engine-free in Domain, run in both Unity and `tools/domain-tests`; goldens pin integer
  outputs only (tower mm, edit tile bytes), never doubles. Proxies P1–P7 as written in §5.5.

## 4. Rope and chairs

- **Rope:** 1¾ in on both quads; drawn at the empty sag (RP-08); every check on both load cases. **The view draws the
  Domain's rope** (one solve from the stored towers and levels), so chairs, profile and checks agree (a test).
- **Chairs (RP-07):** each chair's place is a pure function of lift ID, index and the chair clock (real seconds while
  the game clock runs, in double, as today), at 450 fpm.
- **One chair system for every lift:** each rope loop flattened once, at build, into a shared `NativeArray` of
  arc-length samples; one Burst job a frame (in World) poses every chair in double before converting to float; per-lift
  bounds culled; **LOD per chair** (per lift would pop along a 1-mile line); drawn with the NativeArray overload of
  `RenderMeshInstanced` in batches; **shadows from LOD0 only**.
- **Allocations:** everything sized at build, grown only on build; the frame path allocates nothing. 40 one-mile lifts
  carry about 9,000 chairs.

## 5. Line clearing and earthwork

- CL-03's 55 ft corridor and CL-09 v3 round each terminal (footprint + 20 ft, + 50 ft in front, the pad and the trench)
  go into the lift's terrain edits in one `DesignHistory.Submit(command, TerrainEdits)` step. Undo removes the lift
  and restores the trees and ground exactly (test).
- **Earthwork for P3-08's replay (TL3), made order-proof.** A lift's earthwork becomes a pure function of the
  stored lift (towers, levels, type) and the **source** heights:
  - the build's search (raises, station steel, trench depth) runs once, at Break ground;
  - what it settles is stored (towers and levels) or re-derived from the source alone (the trench's floor from the
    rope, its 10 ft cap from the source ground);
  - pads and trench only cut, so they apply as a minimum over the ground, and the order with trails doesn't matter
    (P3-08 never fills a lift's envelope and keeps 15 ft round its towers, TF-11, so the rope can't move).
  - `LiftEarthwork` lists all of the lift's own tiles, so a replay touching one rebuilds the whole lift and never
    leaves a seam between old and new code.
  - Levels are rounded to whole mm inside the build, before grading, so a replay is byte-identical (golden).
- `LiftClearance.Failing(IHeightSource, Lift, LiftType)` returns the failing stretches for P3-08's TF-11 block.

## 6. Save v2

- **What changes:** each lift gains its two **terminal levels**, absolute whole mm in the package's height datum
  (LT3). Mode and stance are already in v1; heads, forces, leans and reveals stay derived (SH-05).
- **How (0.3 §5):** `save` v1 → v2 in `VersionedJson`; the step also rewrites the design's `Shape` 1 → 2 (today a shape
  other than 1 throws) and gives v1 lifts levels derived from the source heights (what their first build saw). A
  `TestData/formats/v2-save` fixture with one small lift, and a raised terminal; **the v1 fixture is unedited** and still
  opens (test). The design golden (a82d1aa8…) is re-pinned (LT3).
- **PR A carries the level plumbing too** (LiftBuild → the command → the save → the view; StyleTile's dictionary
  removed), so main never writes a v2 save without real levels.
- **Sequencing:** I tell Master Planner before starting the bump and before merging; P3-08 stays off the save.

## 7. Map lines, the Lifts layer and Analysis (mockup first)

**When lines are drawn on the map:**
- **The mockup:** lift and trail lines draw whenever their map layer is on (both on by default), at every zoom.
  Built lifts are red with tower ticks and a dot at each end; planned lifts and trails are dashed orange; built trails
  take their rating colour. Names are a separate **Labels** layer, centred on the line. Work being drawn always shows.
- **The game today:** only plans get a plain line. Once built, a lift shows only as its 3D model and a trail only as
  its groomed surface: no map line and no name.
- **Task 09 draws the built lifts' lines and names** (`LiftMapLines`: terrain-draped, red, tower ticks, end dots;
  names centred with the line broken behind them, colliding names moved apart, UI-16). **P3-08 draws trails' lines and
  names.** Whether lift lines fade out close up is question 10.

**The Lifts map layer** (physical, Shift+2, LT7) shows or hides the lift models, rope, chairs and the lift lines.
Names follow the Labels layer, which widens from peak names to lift and trail names (agreed with P3-08).

**Mockup first**, in `ui-layout.html`, before any port, captures in both themes: Edit lift (moving towers and terminals,
the type and terminal level), Remove lift (no money), the keys in §2, the profile strip and gauges in edit, the Lifts
key. Analysis › Lifts is in the mockup already (#87) and reuses P3-08's `AnalysisList`. Then the port and parity
(`demo.bat` 48, including the strip and the gauge). UI rules: theme tokens and `mp-*`; every `color:` with its outline
colour; solid plates; keyboard at 50–150%.

## 8. Tests and benchmark

- **TW-01 over recorded profiles** (acceptance): about 40 recorded lines, P3-07's 32-line Jackson Hole fan plus lines on
  a second site (fixes must be general). Each line is placed by TW-12 and checked every metre under each rope at the rope
  and both chair edges, to 0.1 ft (TW-08 v2): the uphill rope loaded and empty, the downhill rope empty. A line that
  can't be built must be red with a named reason. By hand fixtures check that a tower's red or green verdict matches the
  TW-01 check. The fixtures are 1-D profiles (1 m step, three heights a sample), about 0.7 MB in all, small files in LFS.
- **Forces:** hand-worked cases at low and top tension, a short station span (the 750 lbf group minimum on the empty
  side), and a Monta tension profile. Placement golden: the same line gives the same towers.
- **Commands:** each new command round-trips and undoes exactly; a stale one is rejected. Save plan writes no
  earthwork. Undo of a lift restores its clearing. Replay reproduces the first build's bytes, including with a trail
  built after the lift. Save v1 → v2 with the v1 fixture; the v2 fixture.
- **PlayMode:** the lift tools by keyboard and mouse at 50% and 150%; Edit, Remove and the focus-card path.
- **Benchmark:** demo 41 before (main) and after, High and Medium, on the measure lane with the PC idle. `-liftbench 40`
  (40 built lifts with moving chairs): the release build for frame p95 (≤20 ms, against the baselines), the Development
  build for 0 B a frame.
- Full `dotnet test` with LFS, EditMode, PlayMode, `check.mjs` and `compile-check.ps1` before every push.

## 9. Delivery

1. **The mockup**: Edit and Remove lift, keys and terminal level. Your approval comes first, while step 2 runs.
2. **PR A, the engine and save v2:** Domain, Persistence and the level plumbing; tests and fixtures; timing first.
3. **PR B, the game:** tools, profile strip, chairs, map lines, layer, Analysis, `demo.bat` 68.

Estimate: 2.5 weeks of plan time.

## 10. Decisions

| ID | Ruling (owner, 2026-10-19) |
|---|---|
| LT1 | Towers: both Automatic placement by the game and fully individual By hand placement from day 1 |
| LT2 | Editing a built lift = moving towers (along the line, height) and moving terminals |
| LT3 | Save v2 stores absolute terminal levels (mm); the design golden is re-pinned |
| LT4 | The mockup's stand-ins are dropped: measured footprints, TW-13's real reach, hold-downs from real forces |
| LT5 | Physics picks the heads (b8-heavy lines are fine); head counts shown on two sites |
| LT6 | The Monta's top return is a fixed row with its range measured from its sheaves; a Blender rebuild only if captures show trouble |
| LT7 | The Lifts map layer is Shift+2 (Trails Shift+4, P3-08) |
| LT8 | Two PRs: A engine and save v2, B the game |

Agreed with P3-08 (your rulings TL3, TL5, TL9 on its branch):
- **Earthwork replay:** `DesignEarthwork`'s `IEarthworkContributor.Collect(doc, site, into)`. Each item gives
  Key, Phase (lifts 1), Order, Reads, Tiles, SameAs and `Build(EditedHeights, EarthworkContext, out detail)`, which
  returns absolute mm writes. Row reads use `AtPointsRow`.
- **TF-11:** P3-08 calls `LiftClearance.Failing`. Tower bases sit at bottom + AlongMm; terminal footprints follow the
  line (`TerminalEnd`).
- **The shared table:** `UI/Hud/AnalysisList.cs`.

## 11. What the review changed

Three adversarial reviews (rules, engineering, player experience), 2026-10-19:
- **Earthwork:** replay was unsound. It re-ran a search over different ground. It's now order-proof (§5).
- **Save:** levels are rounded before grading. The migration rewrites the design shape. v1 lifts get source-derived
  levels. The level plumbing moved into PR A.
- **Forces:** the per-sheave limit at high tension (§3). The Monta's tensioning end (question 11).
- **Missing rules added:** Re-place towers, the mode switch's ask, red blocks Break ground, TW-11 gauges, RP-06 while
  building, LF-10's contents, LF-11's card, Save plan, stale stamps.
- **Tests:** chair edges at 0.1 ft, By hand fixtures, named reasons for unbuildable lines, replay goldens, the release
  build for p95, fixture sizes.
- **Player experience:** Edit and Remove flows, the keys, the Labels split, map lines and who draws them, and the edit
  undo model, all mocked first.
- **Performance:** chairs drawn from NativeArrays, LOD per chair, shadows at LOD0, Burst in World, preview coalescing,
  timing measured before budgets are promised.
