# Phase 3 · 02 Design rules: trails, lifts, paths and the network

**Audience:** the project owner and coding agents.
**Status:** for your review (task 02 ⛳). It gates tasks 03–10: no grading, lift, path or network code until you
approve it.
**Date:** 2026-10-16.
**Decisions:** DR1–DR20 in [phase0-decisions.md](phase0-decisions.md#phase-3-task-02-design-rules-decided-2026-10-16).
**Approved plan:** [p3-02-plan.md](p3-02-plan.md).

**What this is:** the rules the drawing tools follow. They are re-specified from the archived game's
[reference inventory](phase0-0.1-reference-inventory.md) (§6.1–6.3 and §9) and our own doctrine, and changed where you
ruled differently. No archived code is copied; the rules are stated here as prose and numbers and implemented natively.

## 1. How to read this

- **Rule IDs.** Every rule has an ID such as `GR-03`. The prefix names the area:

  | Prefix | Area | § |
  |---|---|---|
  | SH | Shared rules | 2 |
  | CF | Construction flow | 3 |
  | TR | Trail painting | 4.1 |
  | TD | Trail difficulty | 4.2 |
  | GR | Grading | 4.3 |
  | CL | Tree clearing | 4.4 |
  | LF | Lift construction flow | 5.1 |
  | LC | Lift catalog | 5.2 |
  | RP | Rope | 5.3 |
  | TW | Towers | 5.4 |
  | PA | Paths | 6 |
  | JN | Junctions | 6 |
  | NW | Network | 7 |

  An ID is never reused. A retired rule stays in this document, marked *retired*, with the rule that replaced it.
- **Tests cite IDs.** Each test that checks a rule names its ID, for example `GR03_BenchIsLevelAcrossTheSection`
  (definition of done, Phase 3 plan §7). Task 03 picks the exact mechanism.
- **Versions.** A rule holding a formula carries a version, as in `TD-02 v1`. A change of behaviour bumps the version
  and updates the rule's fixtures in the same PR (0.1 §9.10).
- **Units.** Every number is imperial first, with metric in brackets. The game computes in metres, newtons and
  seconds; only what the player reads changes (SH-01).
- **Tolerances.** Where a rule says "to within", tests use that tolerance. Elsewhere, results are exact: the same inputs
  give the same bytes (SH-04).
- **Where it runs.** Engine-free code computes every rule in `MountainPlanner.Domain.Design` (the design document,
  commands, grading, clearing, rope, towers and the network), following 0.3's assembly chain:
  - **World** applies height deltas and the clearing mask.
  - **Presentation** draws the results.
  - **UI** shows panels.

  Nothing in World, Presentation or UI decides a rule.
- **Placeholders.** Numbers marked *(tuned at 07)* are real-world starting values that the style tile (task 07) may
  adjust with your approval. Every other number is a rule.

## 2. Shared rules (SH)

- **SH-01, units:**
  - Stored and computed in metres. Shown through `UnitFormat`: imperial by default, metric on request.
  - Slopes are grades in percent (`SlopeBands`).
  - Volumes in yd³ (m³ when metric), forces in lbf (kN), speeds in fpm (m/s).
  - Rope and tower angles in degrees.
- **SH-02, heights:**
  - Every rule reads the float32 source heights, plus committed edits, through one sampler: bilinear on the 1 m grid.
  - Never Unity's 16-bit terrain.
  - A pending edit is read only by its own preview.
- **SH-03, frame:**
  - Positions are metres in the package's native grid (EPSG:6350), which is equal-area and true to within 0.1% across
    a 5 km site.
  - Lengths are horizontal unless a rule says "slope length". Elevations are NAVD88.
- **SH-04, determinism:**
  - Randomness comes only from keyed hashes of the world seed, the entity ID, a domain tag and an ordinal.
  - No wall clock, no `System.Random`, no unordered parallelism.
  - Collections iterate in ID order.
  - The same design and terrain give byte-identical results (golden tests).
- **SH-05, derived data is never stored:**
  - The network graph, stats, rope curves, tower forces and head types, difficulty ratings and the volumes are all
    rebuilt from authored data.
  - Authored data includes tower positions, heights and stance.
- **SH-06, inside the area:** every trail, lift, path and junction lies inside the area's core box (the 1 m terrain),
  never in the surround ring.
- **SH-07, keys:** every key is rebindable through `KeyBindings`, and captions come from `KeyBindings.Caption`.

## 3. Construction flow (CF)

- **CF-01, one tool:** one construction tool is active at a time. Choosing another cancels the current one at once,
  and its cleanup restores the cursor, camera keys and map state exactly once (0.1 §6).
- **CF-02, the flow:**
  1. Choose the tool.
  2. Draw.
  3. The tool analyses off the main thread.
  4. Review: stats, warnings, Confirm or Cancel.
- **CF-03, revisions:**
  - Every analysis names the design revision and terrain revision it was computed against.
  - A result for a superseded revision, or an old cursor position, is dropped and never merged (0.1 §9.2, §9.4).
- **CF-04, one confirmation at a time:** a second confirmation in the same tick is rejected.
- **CF-05, commands:**
  - Confirming issues one typed command against a named revision.
  - A stale command is rejected whole; nothing is partly applied.
  - Undo and redo restore the document, heights and trees exactly (task 03, task 05).
- **CF-06, the words:** the review panel commits with the HUD's words.
  - **Break ground** builds the work: grading, clearing and the structures appear at once. Construction time comes
    with the simulation.
  - **Save plan** keeps it in the design as a *plan*. It's drawn as planned (orange), edits no terrain and clears no
    trees, and can be built later with Break ground.
  - **Cancel** is the panel's ✕, which asks again before throwing work away.
- **CF-07, Esc:** Esc backs out one step and never drops placed work. From the first step, it closes the tool, asking
  first if anything is drawn.
- **CF-08, pending preview:** a pending edit draws its changed contours in yellow. They are cleared on cancel, on
  unchecking, and once committed.
- **CF-09, speed:**
  - A 1,000 ft (300 m) graded edit previews in ≤100 ms and commits in ≤250 ms, at p95 on the reference PC (exit
    criterion 2).
  - The construction tools allocate 0 B per frame while idle.

## 4. Trails

### 4.1 Painting (TR)

- **TR-01, brush:** **25–400 ft (7.6–122 m) wide, default 100 ft (30.5 m)**, adjusted in 5 ft (1.5 m) steps (the keys
  are task 06's).
- **TR-02, footprint:**
  - The footprint is the union of the brush's discs along the stroke, rasterised on the 1 m grid.
  - More strokes may add to it before confirming.
  - A footprint must be one connected piece.
- **TR-03, head and tail:**
  - The player places the head (top) and the tail (bottom) inside the footprint; the head must be higher.
  - The centreline is the shortest path inside the footprint from head to tail, kept at least half the minimum bench
    (12.5 ft, 3.8 m) from the edge where the footprint allows, and smoothed.
  - Stations sit every **25 ft (7.62 m)** along it, at most 800 per trail (20,000 ft, 6.1 km).
- **TR-04, length:** a trail is at least 100 ft (30 m) long along its centreline.
- **TR-05, ends:** a head within 30 ft (9.1 m) of a lift's unload point, or a tail within 30 ft of a lift's load point,
  snaps to it and connects (NW).

### 4.2 Difficulty (TD)

- **TD-01, the slope it reads:** the graded surface's fall-line slope, the same measure the Slope angle info layer
  shows.
  - At each station, it's the **median** slope of the 1 m cells on that station's cross-section inside the footprint.
  - Ungraded stretches read natural ground.
- **TD-02 v1, rating value:** (3 × mean + max) / 4, in percent.
  - The mean is the station medians' length-weighted mean.
  - The max is the steepest 75 ft (23 m) window, three stations averaged, so one bumpy cell can't set the rating.
  - Sustained pitch counts three times the steepest stretch (0.1 §6.2).
- **TD-03, bands:** the slope layer's bands (`SlopeBands`).

  | Value | Rating | Symbol |
  |---|---|---|
  | Under 25% | Easiest | ● green |
  | 25–40% | More difficult | ■ blue |
  | 40–60% | Most difficult | ◆ black |
  | 60% and over | Experts only | ◆◆ double black |

- **TD-04, live:** the rating updates with every grading change and is shown in review and in the trail's inspector.
- **TD-05, not difficulty:** snow conditions, grooming and width don't change the rating. They feed a separate
  conditions rating with the simulation, from the one snow conditions map (Phase 4).

### 4.3 Grading (GR)

- **GR-01, level bench:**
  - At every station, the running surface is **level across the section**, so contours run square to the centreline.
  - The bench's elevation is the natural ground at the centreline station.
- **GR-02, faces:**
  - From each bench edge, a face runs to natural ground at **1:1 (45°)**, the same slope for cut and fill.
  - There is no widen-to-fit: the footprint is never enlarged to make a bench fit.
- **GR-03 v1, bench width:**
  - On each side, the bench edge is the widest offset b with b ≤ d − |g(d) − z| / s for some offset d inside the
    footprint. Here g is the ground, z the bench elevation and s = 1, the face slope.
  - It needs one outward scan per side.
  - On a uniform sidehill of grade m, this is budget × (1 − m / s): the bench vanishes at a 100% sidehill.
- **GR-04, minimum bench:** **25 ft (7.6 m)** across. A station that can't hold one keeps natural ground (GR-07).
- **GR-05, between stations:** the design surface blends linearly between neighbouring stations' sections.
- **GR-06, footprint:** grading **never leaves the painted footprint**. Heights outside it are byte-identical before
  and after (test).
- **GR-07, too steep:**
  - Stations that can't hold a bench keep natural ground and are drawn red.
  - Review reports the ungraded length in ft.
  - Grading is a tool, never a gate: Break ground is still allowed.
- **GR-08, volumes:**
  - Cut, fill and balance (cut − fill) are summed once over the 1 m cells of the design-minus-ground raster, not
    from section areas.
  - Shown in yd³, to within 0.1%.
  - **No cost in Phase 3**; volumes are tracked only.
- **GR-09, storage:** a confirmed grade is stored as height deltas in sparse 64 m tiles over the immutable package
  (tasks 04 and 05). Undo restores byte-identical heights.
- **GR-10, tolerance:** design heights are exact to within 1 mm (0.04 in) of GR-01 to GR-05.

![A level bench with 1:1 cut and fill faces inside the footprint](images/p3-02/bench-section.svg)

### 4.4 Tree clearing (CL)

- **CL-01, trails:** a built trail clears every tree whose trunk stands inside its footprint.
- **CL-02, ragged edge:**
  - Inside a 10 ft (3 m) band along the footprint's edge, each tree is kept or cleared by a keyed hash. The chance
    of clearing rises from 0 at the edge to 1 at the band's inner side.
  - The treeline reads natural, and nothing outside the footprint changes.
- **CL-03, lifts:** a built lift clears a **55 ft (16.8 m)** corridor centred on its line, from terminal to terminal,
  with straight edges.
- **CL-04, paths:** a built path clears its footprint, as CL-01 and CL-02.
- **CL-05, effects:** clearing writes the clearing mask that forest density reads (0.3 §4.5). The forest floor and snow
  follow it (task 05).
- **CL-06, plans:** a saved plan (CF-06) clears nothing.
- **CL-07, undo:** undo restores the trees exactly; the deterministic forest regrows from the mask.

## 5. Lifts

Phase 3 builds fixed-grip chairlifts only (E13), using the Sessellift FGQ-4 and Monta FG4 assets. Every name is a code
name (decision A2, [generic names][lift-maker-code-names]); real makers are never named.

### 5.1 Construction flow (LF)

- **LF-01, the lift menu:** **Toolbox › Lift** opens it.
  - Its steps are **1 First terminal, 2 Other terminal, 3 Towers, 4 Review**.
  - It holds the type (Sessellift FGQ-4 or Monta FG4) and **Towers: Automatic or By hand**.
  - The switch remembers your last choice; the first time, it's Automatic. It can flip until the work is committed:
    to By hand keeps the towers, and to Automatic re-places them, asking first if any were placed by hand.
  - The type's capacity is shown, not chosen (LC-03).
- **LF-02, the bottom terminal:**
  - The whole terminal follows the pointer as a ghost, green where it can sit and red where it can't (LF-08).
  - Click 1 anchors it at its origin, the centre of its bullwheel. It then swings to face the pointer, and a dashed
    line shows the length, rise and grade.
  - **The first terminal is always the bottom one.**
- **LF-03, the top terminal:**
  - Click 2 places it, and fixes the line: straight, bullwheel to bullwheel. The bottom terminal turns to face along
    it.
  - The top terminal can't be lower than the bottom one; its ghost is red with "The top terminal must be higher".
  - The placed line is drawn trail-map style, a line with a dot at each end.
  - **In Automatic,** the towers appear live along the dashed line while you aim (TW-12).
- **LF-04, towers by hand, from the bottom up:**
  - The tower in hand slides along the line above the last placed tower (or the bottom terminal).
  - **Page Up / Page Down** raise and lower it in **5 ft** steps; click to place it.
  - **A red tower can't be placed**; the status bar (TW-13) says why and what the next tower needs.
  - **Fill the rest** places automatic towers (TW-12) from the last tower to the top terminal.
  - A placed tower can be selected, slid, re-heighted or deleted (**Delete**). If a change elsewhere turns it red,
    it blocks Break ground until fixed.
- **LF-05, moving the top terminal:** until the lift is committed, the top terminal can slide along the fixed line,
  from the menu or by dragging it. Towers beyond it are dropped, after asking.
- **LF-06, review:**
  - The panel shows length (horizontal and slope), rise, ride time, capacity, tower count, the lowest chair clearance
    and the tension range, plus warnings.
  - Then Break ground, Save plan or Cancel (CF-06).
  - **Re-place towers** re-runs automatic placement on the whole line.
- **LF-07, the line:**
  - Exactly two terminals on one straight line, the bottom first.
  - Speed and chair spacing come from the type (LC).
  - Undoable whole (CF-05).
- **LF-08, terminal siting:**
  - No part of the terminal stands in a lake or on a road, and it lies inside the core box (SH-06).
  - The ground under its footprint varies by no more than **10 ft (3 m)**; terminals aren't graded in Phase 3.
  - The two terminals stand at least **300 ft (91 m)** apart.
- **LF-09, keys (C8):** while a tower is in hand or selected, Page Up / Page Down move it; camera zoom keeps the wheel
  and + / −. Delete removes the selected tower; Esc backs out one step (CF-07).
- **LF-10, the lift profile:** while a lift is being built, a wide strip sits at the bottom of the screen, just above
  the status bar.
  - **Axes:** x is horizontal distance from the bottom terminal in ft; y is elevation in ft.
  - **Drawn:**
    - the ground;
    - the terminals and towers in simple outline;
    - the rope's loaded side;
    - **a clearance track: the chairs' bottom against a dashed line 15 ft above the ground**, with failing spans in
      red;
    - the tower in hand highlighted;
    - TW-09's green band.
  - The axes scale to fit, with labelled gridlines; a **True scale** switch shows the profile 1:1.
  - It closes when the lift is committed.
- **LF-11, after building:** a built lift's card is simple: name, type, capacity, length, rise, ride time, tower count.
  The engineering detail (tension, loads, clearances) shows only while the lift is being edited.

### 5.2 Catalog (LC)

- **LC-01, entries:** each type carries:
  - seats;
  - rope speed;
  - chair spacing;
  - haul rope diameter, weight and rated tension;
  - counterweight tension;
  - empty chair weight and design rider weight;
  - the drop from the rope to the chair's bottom;
  - line gauge;
  - sheave diameter and per-sheave limits;
  - each terminal's train limits.

  Later types (doubles, triples, detachables) get their own entries.
- **LC-02, Phase 3 values** for both quads *(tuned at 07)*:

  | Property | Value | Basis |
  |---|---|---|
  | Seats | 4 | The models |
  | Rope speed | **450 fpm (2.29 m/s)**, fixed (your ruling) | ANSI B77.1's fixed-grip limits fall 50 fpm per seat (600, 550, 500 fpm for one to three seats), giving 450 for a quad. France caps 3–4 seat fixed grips at 2.3 m/s (453 fpm) |
  | Chair spacing | 45 ft (13.7 m), fixed for now (your ruling) | A 6.0 s interval; new fixed quads carry up to 2,400 pph |
  | Haul rope | **1⅝ in (41 mm)**, 6-strand fibre core, 4.44 lb/ft (6.61 kg/m), breaking strength 214,000 lbf (952 kN) | Fixed-grip ropes run 1⅜–1¾ in (35–45 mm). 1⅝ in keeps the models' 17 in (432 mm) sheaves at the required 10× the rope's diameter |
  | Rated tension | **42,800 lbf (190 kN)**: the breaking strength ÷ 5 | US tramway codes require a factor of safety of 5 on a new haul rope |
  | Counterweight tension, per side | **18,000 lbf (80 kN)** | A comparable fixed-grip double runs about 17,000 lbf per side |
  | Chair | **350 lb (159 kg)** empty; 1,030 lb (467 kg) with 4 riders at **170 lb (77 kg)** each | Fixed chairs weigh about 250–450 lb; US codes use a 170 lb design rider |
  | Load on the rope | 27.3 lb/ft (40.7 kg/m) loaded, 12.2 lb/ft (18.2 kg/m) empty | RP-03 |
  | Rise this allows | about **900 ft (275 m)** of vertical: (42,800 − 18,000) ÷ 27.3 | RP-05. Typical fixed quads climb 300–1,200 ft; longer lines need two lifts |
  | Rope to chair bottom | about 8.4 ft (2.55 m); task 09 measures the model | Real chairs: 8–10 ft |
  | Line gauge | From each model's spec (the Monta's is 16.06 ft, 4.895 m) | The models |
  | Rope height at loading | 9.97 ft (3.039 m) above the load level | The models; real chairs about 10 ft |
  | Sheave | 17 in (432 mm) | The models |
  | Per sheave | at most **1,500 lbf (6.7 kN)** and **4.5°** of bend: game rules, as no passenger-ropeway figure was found | — |
  | Per group (one rope's train) | a support group's load stays at least **½ of one sheave's limit, 750 lbf (3.3 kN)**, in every case, or it needs hold-down sheaves; the same applies in reverse to hold-down groups | US tramway code |

- **LC-03, capacity:** seats × speed ÷ spacing, shown and not chosen. At the values above that's **2,400 pph**.
  Spacing becomes editable later.
- **LC-04, ride time:** slope length ÷ speed, plus nothing for loading (fixed grip).
- **LC-05, sources:** these values were checked on 2026-10-16 against:
  - ANSI's B77.1 summary;
  - the Pennsylvania aerial tramway code (34 Pa. Code ch. 7);
  - Michigan's ski area rules, which adopt B77.1-2006;
  - France's ropeway safety authority (STRMTG);
  - a Canadian safety authority's equipment table for a fixed-grip double;
  - a 6x19 wire rope table.

  No maker is named, per A2.
- **LC-06, the clearance against the codes:** B77.1-2006 (as Michigan adopts it) asks for 13 ft from the deepest
  design snow to the top of the seat where people ski under the line, and 5 ft everywhere. TW-01's 15 ft from bare
  ground to the chair's bottom is your stricter, simpler rule, applied everywhere.

### 5.3 Rope (RP)

- **RP-01, in the terminals:**
  - The rope runs level through each station at 9.97 ft (3.039 m) above the load level.
  - The station's own sheave train turns it onto the first span, within that train's limits; each terminal model
    gives them (the Sessellift return can climb to about 15.9°).
- **RP-02 v1, spans and slope continuity:**
  - Each span is a catenary, y = a·cosh((x − x₀)/a) + c, with a = H/w: the horizontal tension over the load per
    unit length. So a differs from span to span.
  - **At each end, the span's slope, sinh((x − x₀)/a), equals the slope of the tangent leaving the last sheave**, to
    within 0.01°. The point where it leaves lies on that sheave's groove circle.
  - Across a train, the rope runs straight from sheave to sheave and turns a little at each. For the forces, the
    train's total bend is shared equally among its sheaves, as on real equalising trains.
- **RP-03 v1, tension:**
  - The counterweight sets the tension at the bottom terminal.
  - Up the line, tension grows by the load per unit length × the height gained (frictionless sheaves), separately
    for each side: T(h) = T₀ + w·h.
  - The loads: the loaded uphill side is rope + loaded chair ÷ spacing; the empty side is rope + empty chair ÷
    spacing.
- **RP-04, load cases:** every check runs for two cases, **the line empty** and **the uphill side full**. Downloading
  and wind come later.
- **RP-05, tension range:** the highest tension, at the top of the loaded side, must not exceed the type's rated
  tension. Too much rise for a type turns the whole lift red ("Too much rise for this lift: tension at the top 112%
  of rating").
- **RP-06, tension readout:** while building, a bar shows the bottom and top tension as % of the rating, with lbf in
  the detail line.
- **RP-07, chairs:** chairs hang plumb from the rope and travel at rope speed. Each chair's position is a pure
  function of the lift's ID, the clock time and its index.

### 5.4 Towers (TW)

- **TW-01, clearance:** every chair's bottom stays at least **15 ft (4.57 m) above bare ground**, in both load cases,
  along the whole line.
  - The load and unload zones are exempt: from each station to the first point where the chairs reach 15 ft.
  - A span that breaks the rule is red.
- **TW-02, heights:**
  - A tower's height is **the rope's height above the ground at the tower**.
  - Heights run **25–100 ft (7.6–30.5 m) in 5 ft steps**; 25 ft is the lowest step at which the chairs clear 15 ft
    at the tower.
  - The kit builds the mast from that height. The 1 m mast sections take up the remainder at the base, as the Lift
    Lab does.
- **TW-03, spacing and siting:**
  - towers at least **50 ft (15.2 m)** apart, and from a terminal;
  - not in a lake or on a road;
  - inside the core box.
- **TW-04, head types:** at each tower, the load on each sheave (2T sin(δ/2), split over the sheaves, where δ is the
  bend) is computed for both rope sides in both load cases.

  | What the rope does in every case | Head |
  |---|---|
  | Presses down | **Support:** s4, s6 or b8 (breakover), the smallest that carries it |
  | Pulls up | **Hold-down:** d8 |
  | Flips between cases, or the group load falls within 750 lbf of zero (LC-02) | **Combination:** c8, 4 hold-down over 4 support sheaves |

- **TW-05, limits per sheave and group:** LC-02's maximum load and maximum bend per sheave, and its minimum group load (so the rope stays seated), in
  every case.
- **TW-06, several towers close together:**
  - One tower can bend the rope only so far, and less where the tension is higher.
  - At a sharp crest the bend is shared by two or three breakovers close together; before a sharp rise, by two or
    three hold-downs. TW-03's 50 ft minimum allows that.
  - A tower that would need more is red: "Too sharp for one hold-down: add one before the rise", or "…for one
    breakover: add one on the crest".
- **TW-07, span limits:** no fixed number. A longer span sags more, so it needs taller towers (TW-01, TW-02), and bends
  the rope harder at both ends, so it needs bigger heads (TW-05).
- **TW-08, tolerance:** clearance is checked every 3.3 ft (1 m) along the line against SH-02's heights, to within
  0.1 ft (3 cm).
- **TW-09, where it can stand:** for the tower in hand at its height, the line is checked every 5 ft (1.5 m) and the
  places it could stand are marked (the green band). It's recomputed only when the height or a placed part changes.
- **TW-10, speed:** a tower move evaluates in ≤0.5 ms, and TW-09 in ≤2 ms, with 0 B garbage per frame (§5.5).
- **TW-11, load gauge:** while building, each tower's gauge shows its per-sheave load in both cases, from "pulls up"
  to "presses down", as % of the limits.
- **TW-12, automatic placement:**
  - It follows the same rules as by hand, and **never leaves a red tower**.
  - It aims for **modest towers, about 30–40 ft (9–12 m)**, taller only to clear a dip. It uses the fewest towers
    within that, adds hold-downs, breakovers and combinations where the rope needs them, and spaces them evenly when
    layouts tie.
  - Positions are on a 5 ft grid; the same line always gets the same towers.
  - When nothing fits (too much rise for the type, or a dip that 100 ft towers can't span), it says why and leaves
    the line red.
  - The preview updates at least **10 times a second** while you aim, off the main thread, with stale results
    dropped (CF-03).
  - The final placement of a 1-mile (1.6 km) line takes **≤50 ms**.
- **TW-13, placement status bar:** a line in the lift menu about the tower in hand.
  - **Status:** OK, or the red reason.
  - **Head and load:** the head it would get, and its load in both cases as %.
  - **Clearance:** the lowest chair clearance in the span behind it.
  - **Next tower:** how far the next span can reach before the chairs drop below 15 ft, at this height and at 100 ft
    ("Next tower within 420 ft, or 610 ft with a 100 ft tower"). A warning appears when the rope will pull up within
    that reach ("Hold-downs ahead").
- **TW-14, plumb or leaned:**
  - Each lift has a setting, **Towers stand: Plumb** (the default) **or Leaned**.
  - Leaned tilts each mast along the line, square to the rope there: it bisects the arriving and departing rope
    angles, capped at **15°** *(tuned at 07)*; real towers lean up to about the slope angle, 10–15°. That's square to the rope rather than the ground, because the rope is smooth
    where the ground under one tower may not be.
  - The head then sits height × sin(lean) along the line from the base, and every rule uses the head's real position.
  - On a plumb tower on a steep line, part of the rope's load pushes the mast sideways. It's shown now, and Phase 4
    can price it.
- **TW-15, stored:**
  - A tower's position along the line, its height and its stance are authored and saved.
  - Its head type, forces and rope geometry are derived (SH-05).

![A lift profile: hold-downs before a steep rise, breakovers on the crest](images/p3-02/lift-profile.svg)

### 5.5 Making it fast (proxies)

Real ropeway software models rope stretch, each chair as a point load, sheave friction, wind and stopping. The game
uses closed-form stand-ins, all in Domain and deterministic:

| # | Stand-in | Instead of |
|---|---|---|
| P1 | The ground under the line is sampled once, every 3.3 ft (1 m), when the line is fixed | Terrain queries on every move |
| P2 | Chairs spread as an even load: rope weight + chair weight ÷ spacing (RP-03) | Point loads per chair |
| P3 | Tension from height alone: T₀ + w·h | A whole-line tension solve |
| P4 | Each span a catenary in 2–4 Newton steps | Elastic catenaries |
| P5 | Each train first as a point, then 2–3 Newton steps so every span leaves its last sheave on the tangent (RP-02) | Contact with every sheave |
| P6 | Two load cases plus fixed per-sheave margins | Dynamics, wind and stopping |
| P7 | A tower move re-solves only its two spans and three trains | Re-solving the whole line |

## 6. Paths and junctions (PA, JN)

- **PA-01, what a path is:** a skiable connector, a cat track, **20 ft (6.1 m)** wide. It joins things that don't touch:
  a lift's top to a trail's head, or one trail to another. It's drawn as a polyline by clicking points, and Enter or
  a double-click ends it.
- **PA-02, ends:** both ends must connect. Each snaps to a lift's load or unload point, a trail's footprint, or a
  junction within **30 ft (9.1 m)**.
- **PA-03, grading and clearing:** a path is graded by the GR rules inside its own footprint (minimum bench 20 ft, the
  full width) and cleared by CL-04.
- **PA-04, direction:** a path is skied from its higher end to its lower end. Its difficulty is rated by TD; a path
  steeper than 25% gets a warning, since cat tracks are meant to be easy.
- **JN-01, splitting:** confirming a path that ends on a trail splits that trail at a shared junction, all in one
  command (0.1 §6.3).
- **JN-02, trails meeting:** a trail whose head or tail lies within another trail's footprint joins it at a junction.
- **JN-03, pruning:** deleting a trail or path removes only the junctions nothing else uses. Lift terminals always stay.
- **JN-04, IDs:** junction IDs are keyed hashes of the joined entities' IDs (SH-04), so a reload rebuilds the same
  junctions.

## 7. The network (NW)

- **NW-01, derived:** the ski network is rebuilt from lifts, trails, paths and junctions on every load and edit, and
  never stored. The same design gives the same graph bytes (golden).
- **NW-02, graph:**
  - **Nodes:** lift load and unload points, trail heads and tails, and junctions.
  - **Edges:** lifts go up; trails and paths go down.
  - Saved plans (CF-06) are left out.
- **NW-03, lifts are the backbone:** there is no Guest Entrance in Phase 3. A built lift is **connected** when a
  descent of trails and paths from its top reaches some lift's bottom, its own included.
- **NW-04, flags:**
  - Unconnected lifts are flagged in review, in the Analysis network view, and in the status bar's lift and trail
    counts.
  - A trail or path whose tail reaches no lift bottom and no other trail is a warning ("Leads nowhere").
- **NW-05, measuring:** topology uses metres in the local frame (SH-03); reported lengths are horizontal, in ft.

## 8. What the player already sees

The rules agree with what the shipped game shows:

| Shipped | Rule |
|---|---|
| The Slope angle layer: 25/40/60% bands, in percent | TD-01, TD-03: trails are rated with the same bands and the same measure |
| The cursor readout: slope % and band name, elevation in ft or m | SH-01: every new readout uses the same units and formatters |
| Contours from float32 heights | SH-02: rules read the same heights; edited heights feed contours and the readout (task 05) |
| The Snow conditions info layer (a placeholder) | TD-05: conditions don't change difficulty; they come with the simulation |
| Map layers (physical) and info layers (display) | The Trails and Lifts map layers are physical; any trail-rating view is an info layer |

## 9. Contradictions resolved

| # | Sources disagreed | Resolution |
|---|---|---|
| C1 | Difficulty bands: 0.1 used degrees (16/24/37°), the slope layer percent (25/40/60%) | Percent (DR1) |
| C2 | The fill face: an earlier handoff said 1.5:1 fill with widen-to-fit; the doctrine and 0.1 say 1:1 for both, with widen-to-fit removed | 1:1 for both, no widen-to-fit (DR3) |
| C3 | The slope that rates a run: along the centreline, or the fall line | The fall line, as the slope layer shows (DR2) |
| C4 | Capacity: chosen by the player (lift drawing v1) or by type (0.1) | By type, through fixed chair spacing (DR11) |
| C5 | Speed: 400 fpm (0.1) or 2.3 m/s (v1) | 450 fpm (DR6) |
| C6 | The lift clearing corridor | Kept at 55 ft (16.8 m) |
| C7 | Towers, spacing, clearance and sag: no source | New rules, RP and TW |
| C8 | Page Up / Page Down are camera zoom today | They move the tower in hand; zoom keeps the wheel and + / − (LF-09) |
| C9 | 5 ft tower steps against 1 m mast sections | The base takes up the remainder; no model change |
| C10 | The kit's flat support arcs (s4 turns 2.8° and s6 4.2° at full arc) are smaller than a loaded rope's 7–13° bends | Capacity from TW-05; task 09 rocks the trains to the real bend |
| C11 | Terminals oriented bottom-first by elevation (0.1), or placed by the player | The player places the bottom first (LF-02) |
| C12 | The tower kit is modelled plumb | Leaned towers (TW-14) need a tilted base plate on a level pier: a small change for the lift asset thread, after your model approval |

## 10. Who implements what

| Task | Rules |
|---|---|
| 03 Design document, commands, undo | SH-04, SH-05, CF-03 to CF-06, TW-15, JN-01, JN-04 |
| 04 Save file | SH-05 (what's saved), GR-09, CF-06 (plans saved) |
| 05 Terrain edits | SH-02, GR-09, GR-10, CL-05, CL-07, CF-09 |
| 06 Toolbox and placement | SH-07, CF-01, CF-02, CF-06 to CF-08, LF-01, LF-09 |
| 07 Style tile | the *(tuned at 07)* values, TW-14's cap |
| 08 Trails | TR, TD, GR, CL-01, CL-02 |
| 09 Lifts | LF, LC, RP, TW, CL-03 |
| 10 Paths and the network | PA, JN, NW, CL-04 |

## 11. Later

These are not in Phase 3:
- wind, downloading and dynamic load cases;
- an editable chair spacing and speed;
- cost for grading, towers and leaned towers;
- detachables, gondolas and surface lifts;
- graded terminal pads;
- grooming and the conditions rating;
- roads drawn by the player (0.1's road exception to GR-06).

[lift-maker-code-names]: phase0-decisions.md#01-reference-inventory-approved-2026-09-25
