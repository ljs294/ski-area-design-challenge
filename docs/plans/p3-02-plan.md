# P3-02 design rules: the plan (rounds 1–8)

**Status:** approved by the owner on 2026-10-16: rounds 1–6 ("Yes to all. Lets proceed!"), then rounds 7 (trails) and 8 (network), with the lift mockup thread's rulings folded in (DR20). The rules are in [p3-02-design-rules.md](p3-02-design-rules.md). This task is docs only: no code,
no GPU, no demo.bat entry. Branch `docs/p3-02-design-rules`, from origin/main 6e803d3.

**Units:** every number is imperial first, with metric in brackets. The game stores metres and newtons; only what you
read is imperial, as the shipped `UnitFormat` does. Slopes are percent grades; rope and tower angles are degrees.

## 🟦 Round 7: trails (your answers folded in)

You asked for a trail round, then a network round, before the PR. Rounds 1–6 settled the trail rules (TR, TD, GR, CL)
but never how a trail is drawn. This round adds that as **TF** rules, now with your answers.

### Trails as painted areas (built to grow)

- **TF-01, a trail is a painted area:** a footprint mask on the 1 m grid, plus properties.
  - **The footprint** may be painted with different brush widths, and **may have islands**: unpainted holes inside
    it, where trees and ground stay as they are.
  - It must still be one connected piece.
- **TF-02, kinds (ready for later):** every painted area carries a **kind** and two settings.
  - **Kind:** Phase 3 builds **Run** and **Cat track** (round 8). The format already allows **Glade**, **Freeride
    area** and **Backcountry area**; these may have no single head and tail, and no grading.
  - **Grading:** Graded or Natural.
  - **Clearing:** Clear, Thin (glades) or None.

  Later kinds add rules; they don't change the format (task 04's save keeps the kind and settings).
- **TF-03, grading built to grow:**
  - The grading engine takes a footprint, a centreline, a bench width and an **envelope**: where cut and fill may
    reach.
  - Phase 3 runs use **envelope = footprint** (GR-06).
  - A later **cat track through steep terrain** keeps a fixed bench (for example 20 ft) and lets its 1:1 faces reach
    out past the painted width, up to a set limit. That's the road exception from 0.1, so a cat track can be cut
    across a slope a run could never bench.
  - Nothing in Phase 3 uses the wider envelope; it's a parameter, not a new engine.

### Trail construction flow

1. **TF-04, the trail menu:** **Toolbox › Trail** opens it.
   - **Steps:** **1 Paint, 2 Head and tail, 3 Review.**
   - **Options:**
     - brush width (25–400 ft, default 100 ft), set with **[ / ]** in 5 ft steps or a slider;
     - **Graded or Natural**;
     - the name, with "Run 1, Run 2, …" suggested and editable.
2. **TF-05, painting (step 1):**
   - The brush circle follows the pointer. **Left-drag paints; Shift-drag erases** (islands are erased holes).
   - Change the width between strokes; **Ctrl+Z** undoes the last stroke.
   - **After each stroke** (not during it), the footprint's area and an **estimated rating** update (TF-07).
   - A second, separate patch shows red, and Next is blocked until it's joined or erased.
3. **TF-06, head and tail (step 2):** a separate stage, after painting.
   - Click the head (top), then the tail (bottom). Each snaps to a lift's unload or load point within 30 ft (TR-05).
   - **The centreline is automatic** (TR-03): the path from head to tail that stays as **far from the footprint's
     edges as it can**, going around islands.
   - Then the full analysis runs:
     - the yellow pending contours;
     - red stretches too steep to grade;
     - the final rating (TD-02);
     - cut and fill;
     - the trail profile.
4. **TF-08, the trail profile:** a wide strip above the status bar, like the lift's.
   - **Axes:** x is the distance along the centreline, y elevation, in ft.
   - **Drawn:**
     - natural ground, dashed;
     - the graded surface, solid;
     - each stretch coloured by its band;
     - red ungraded stretches;
     - the steepest 75 ft window marked.
5. **TF-09, review (step 3):**
   - length, vertical, rating, mean and steepest grade;
   - cut, fill and balance in yd³;
   - the ungraded length;
   - trees cleared;
   - warnings.

   Then Break ground, Save plan or Cancel.
6. **TF-10, Natural trails:** no grading; trees are cleared (Clear). Thin, for glades, comes later.
7. **TF-11, editing:** select a built trail to repaint it, move its head or tail, rename, split, join (round 8) or
   delete it. Grading re-runs only on changed stations; delete restores ground and trees exactly.
8. **TF-12, the built trail's card:** simple: name, rating, length and vertical.
9. **TF-13, keys:**
   - **[ / ]** brush width;
   - **Shift** erase;
   - **Enter** next step;
   - **Esc** back one step;
   - **Delete** the selected trail.

   All rebindable.

### TF-07, estimating the rating while you paint

Before the head and tail exist there's no centreline, so the estimate reads the painted area alone:
- It takes the slope of every 1 m cell in the footprint, on **natural ground** (grading comes in step 2), the same
  numbers the Slope layer shows.
- **Estimate = (3 × the mean + the 90th percentile) / 4.** It's TD-02's formula, with the 90th percentile standing in
  for "the steepest stretch". A percentile ignores a few steep cells at the edges, where a plain maximum would jump.
- Why not the mean alone? It hides a short steep pitch: a long green run-out with a black headwall would average to
  blue.
- **Cost:** one pass over the cells the stroke touched, kept as a running slope histogram. A 1,000 × 100 ft trail is
  about 9,300 cells, a fraction of a millisecond.
- After step 2, the **final rating (TD-02)** replaces the estimate. It uses the graded surface along the centreline,
  so the two can differ where grading tames a pitch.

### Trails meeting other things

- **TF-14, overlaps:**
  - Where a new footprint overlaps another trail, the network gets a **natural junction** there (round 8).
  - The overlap keeps the earlier trail's grading.
- **TF-15, lifts:** grading never changes the ground within 15 ft (4.6 m) of a tower base or a terminal. A trail that
  would push a built lift's clearance below 15 ft turns that stretch red and blocks Break ground.

### Your answers

1. Head and tail: you pick them in their own stage, after painting.
2. The centreline: automatic only, as central as possible.
3. Graded / Natural: yes.
4. Overlaps: they make natural junctions, and a trail can span several network segments yet draw as one line. This
   needs join and split tools (round 8).
5. The 15 ft clear zone and lift re-check: yes.
6. Shift-drag erases: yes.
7. Default names: yes.
8. The UI mockup thread gets the trail tool: yes, after this round.

## 🟦 Round 8: the network (for your review)

This ties lifts, trails and cat tracks into one ski network.

- **NW-06, what's authored and what's derived:**
  - **Authored** and saved: lifts, trails (footprint, kind, head, tail, name) and cat tracks.
  - **Derived** on every load and edit: junctions, segments and the graph. The same design gives the same graph
    (golden).
- **NW-07, natural junctions:** a junction appears wherever:
  - two footprints overlap;
  - a head or tail snaps to a lift point;
  - a cat track's end lies in a trail.

  It sits on each trail's centreline, at the point nearest the overlap's middle. Its ID is a keyed hash of what it
  joins (JN-04), so a reload gives the same junctions.
- **NW-08, segments:**
  - Each trail's centreline is cut at its junctions into **segments**, the network's edges, skied from head toward
    tail.
  - Each segment has its own rating (TD-02 on its stations), for guests later. The player sees one rating per trail.
- **NW-09, one trail, one line:** however many segments a trail has, the map draws it as **one line** with one name and
  one symbol.
- **NW-10, split and join:** two tools that change names, never ground.
  - **Split** a trail at a junction or a point on its centreline. The two parts become two trails ("Run 1" and "Run 1
    lower", renamable), and the footprint is divided by the cross-section line there.
  - **Join** two trails where one's tail meets the other's head. They become one trail and keep the upper one's name.
  - Both are single undoable commands.
- **NW-11, cat tracks** (replacing PA's separate "path"):
  - A cat track is the trail kind **Cat track**: drawn as a line of clicks, painted at a fixed **20 ft (6.1 m)**
    width, graded inside its footprint for now (TF-03 prepares the steep version).
  - Its ends snap within 30 ft to lift points or trails.
  - It counts as a way down.
- **NW-12, the checks:**
  - A lift is **connected** when a descent from its top reaches some lift's bottom (NW-03).
  - Warnings:
    - **"No way down"** for a lift that isn't connected;
    - **"No lift serves this"** for a trail that can't be reached from any lift's top;
    - **"Leads nowhere"** for a trail whose tail reaches no lift bottom and no other trail.
- **NW-13, seeing the network:** an **Analysis › Network** view lists each lift with its status. Selecting a lift
  highlights every trail reachable from its top. The status bar shows the lift and trail counts.

### Questions (my recommendation)

1. Are junctions always derived from geometry, never drawn by hand? → Yes.
2. Should paths become the Cat track trail kind, drawn as a line at a fixed 20 ft? → Yes.
3. Split and join as NW-10 describes, including the naming? → Yes.
4. The three warnings in NW-12? → Yes.
5. The Network view, with the reachable trails highlighted? → Yes.
6. One rating per trail for the player, per-segment ratings kept for guests later? → Yes.

### Your answers to round 8 (2026-10-16)

- **Snapping (NW-07):** heads and tails snap to any lift's top or bottom, or to any point along another trail.
- **Cat tracks (NW-11):** not a kind. A cat track is a narrow graded run, built for an easy way down on steep terrain.
  Trails are delineated as Natural or Graded, and as Run, Glade or Zone (TR-06).
- **Paths:** automatic where two trails come close enough to nearly overlap, so skiers can cross; you never draw one
  (PA-05).
- **Questions 1–6:** yes. Question 2 (cat tracks as a kind) is superseded by the cat track answer above.

All folded into [p3-02-design-rules.md](p3-02-design-rules.md) (DR22–DR27).

## What changed

**Round 6 (your notes on round 5):**
- The bottom terminal comes first, and the top terminal right after it, fixing the line. Towers then go up from the
  bottom (LF-02 to LF-04).
- Every span leaves its last sheave at exactly that sheave's tangent: the slopes match (RP-02).
- Chair spacing is a lift property, fixed for now. Capacity follows from it, so the capacity choice is gone (LC).
- Several breakovers or hold-downs close together where one can't take the bend (TW-06).
- A placement status bar in the lift menu, to help place the next tower (TW-13).
- Towers plumb or leaned with the rope (TW-14).
- A lift profile above the status bar while building (LF-10).
- Simple readouts once a lift is built (LF-11).
- Terminal siting rules, so the moving terminal knows when it's red (LF-08).
- Lift numbers checked against real fixed-grip quads (LC).

**Round 5:** a choice between automatic and hand-placed towers (TW-12).

**Round 4:**
- Towers placed by you.
- Head types from the rope's load.
- Line tension as the governing quantity.
- Span limits from the physics.
- Fast stand-ins (proxies).
- The UI mockup thread.

## Files I'll write

- `docs/plans/p3-02-design-rules.md` (new)
- `docs/plans/images/p3-02/`: small SVG diagrams of the bench section, a lift profile with towers, and the forces
  at a tower
- a new "Phase 3 task 02" section in `phase0-decisions.md`
- a status line in `phase2-14-phase3-plan.md`
- this plan, committed so the PR can link it
- the `p3-02-owner-decisions.md` memory note

## Lift construction flow (LF)

1. **LF-01, the lift menu:** **Toolbox › Lift** opens it.
   - You choose the type (Sessellift FGQ-4 or Monta FG4) and **Towers: Automatic or By hand**.
   - The switch remembers your last choice; the first time, it's Automatic. It can flip until Confirm. Switching to
     By hand keeps the towers; switching to Automatic re-places them, and asks first if any were placed by hand.
   - The menu shows the type's capacity; it isn't a choice for now (LC).
2. **LF-02, the bottom terminal:**
   - The whole terminal follows the pointer as a ghost, green or red by LF-08.
   - Click to anchor it at its origin, the centre of its bullwheel.
   - Moving the pointer then swings it to face the pointer, with a dashed line showing the length, rise and grade.
3. **LF-03, the top terminal:**
   - The second click places it, and fixes the line: straight, bullwheel to bullwheel. Both terminals turn to face
     along it.
   - It may not be lower than the bottom terminal; the ghost turns red with "The top terminal must be higher".
   - **Automatic:** towers appear live along the dashed line as you aim (TW-12), and you go on to review.
4. **LF-04, towers by hand, from the bottom up:**
   - The tower in hand slides along the line above the last tower placed (or the bottom terminal).
   - **Page Up / Page Down** raise and lower it in **5 ft** steps; click to place it.
   - It's red when it can't stand, with a one-line reason; the status bar (TW-13) shows what the next tower needs.
   - **Fill the rest** puts automatic towers (TW-12) from your last tower to the top terminal.
5. **LF-05, moving the top terminal:** until Confirm, you can slide it along the line, from the menu or by dragging
   it. Towers beyond it are dropped. (Open question 3.)
6. **LF-06, review:**
   - Length, rise, ride time, capacity, the towers, the lowest chair clearance, the tension range, and any
     warnings.
   - **Confirm or Cancel** (CF).
   - Any tower can still be slid, re-heighted, added or deleted, and **Re-place towers** re-runs automatic placement
     on the whole line.
7. **LF-07, carried over:**
   - exactly two terminals on one straight line, the bottom first;
   - 450 fpm fixed and chair spacing fixed (LC);
   - a 55 ft (16.8 m) clearing corridor confirmed with the lift;
   - everything undoable (task 03).
8. **LF-08, terminal siting:**
   - each terminal inside the area's core box, with no part in a lake or on a road;
   - the ground under its footprint varying by no more than 10 ft (3 m), because terminals aren't graded in Phase 3;
   - terminals at least 300 ft (91 m) apart.
9. **LF-09, keys:** every key is rebindable. While a tower is in hand or selected, Page Up / Page Down move it; zoom
   keeps the wheel and + / − (C8).
10. **LF-10, the lift profile (your request):** while building, a profile panel sits just above the status bar.
    - **Axes:** x is horizontal distance from the bottom terminal in ft; y is elevation in ft.
    - **Drawn:**
      - the ground;
      - the terminals and towers in simple outline;
      - the rope (loaded side) and the chairs' bottom against a dashed line 15 ft above the ground, with failing
        spans in red;
      - the tower in hand highlighted;
      - TW-09's green band.
    - The axes scale to fit the panel, with labelled gridlines. A long, flat lift is therefore stretched vertically;
      a "true scale" switch shows it 1:1 (open question 5).
    - The panel closes on Confirm.
11. **LF-11, after construction:** a built lift shows a simple card: name, type, capacity, length, rise, ride time and
    tower count. The engineering detail (tension, loads, clearances) comes back only while you edit the lift.

## RP: the rope

- **RP-01, in the terminals:** the rope runs level at 9.97 ft (3.039 m) above the load level. The station's own sheave
  train turns it onto the first span, within that train's limits; each lift model's terminals give them (the
  Sessellift return can climb to about 15.9°).
- **RP-02, spans and slope continuity (your boundary condition):**
  - Each span is a catenary, y = a·cosh((x − x₀)/a) + c, with a = H/w (horizontal tension over load per foot), so a
    varies from span to span.
  - **At each end, the span's slope (its derivative, sinh((x − x₀)/a)) equals the slope of the tangent leaving the
    last sheave**, to within 0.01°. The point where it leaves lies on that sheave's groove circle.
  - Across a sheave train, the rope runs straight from sheave to sheave, turning a little at each. For the forces,
    the train's bend is shared equally among its sheaves, as on real equalising trains.
- **RP-03, tension:** the counterweight sets the tension at the bottom terminal. Up the line, it grows by the load per
  foot × the height gained (frictionless sheaves), separately for the loaded uphill side and the empty downhill side.
- **RP-04, load cases:** two cases, the line empty and the uphill side full. Downloading and wind come later.
- **RP-05, tension range:** the highest tension (the top of the loaded side) must stay under the type's rated
  tension; too much rise for a type turns the whole lift red.
- **RP-06, tension readout:** while building, a bar shows the bottom and top tension as % of the rating, with lbf in
  the detail line.

## TW: the towers

- **TW-01, clearance:** every chair's bottom stays at least **15 ft (4.57 m) above bare ground** in both load cases.
  - The chair's bottom is measured from the chair model (LC).
  - The load and unload zones are exempt: from each station to the first point where the chairs reach 15 ft.
- **TW-02, heights:** a tower's height is **the rope's height above the ground at the tower**.
  - Heights run **25–100 ft (7.6–30.5 m) in 5 ft steps**.
  - The kit builds the mast from that height; the 1 m mast sections take up the remainder at the base, as the Lift
    Lab does.
- **TW-03, spacing and siting:** towers at least 50 ft (15.2 m) apart and from a terminal, not in a lake or on a road,
  and inside the core box.
- **TW-04, head types:** at each tower, the rope's load on the sheaves (tension × bend, split over the sheaves) is
  computed for both rope sides in both load cases.

  | What the rope does in every case | Head |
  |---|---|
  | Presses down | **Support:** s4, then s6, then b8 (breakover), the smallest that carries it |
  | Pulls up | **Hold-down:** d8 |
  | Flips between cases, or is near zero | **Combination:** c8, 4 hold-down over 4 support sheaves |

- **TW-05, limits per sheave:** a maximum and minimum load, and a maximum bend, per sheave (values in LC).
- **TW-06, several towers close together:** yes, as you asked. One tower can bend the rope only so far (TW-05), and
  less where the tension is higher.
  - **At a sharp crest,** the bend is shared by two or three breakovers close together.
  - **Before a sharp rise,** it's shared by two or three hold-downs.
  - The 50 ft minimum (TW-03) still allows that.
  - A tower that would need more turns red: "Too sharp for one hold-down: add one before the rise", or "…for one
    breakover: add one on the crest".
- **TW-07, span limits:** a longer span sags more, so it needs taller towers. It also bends the rope harder at both
  ends, so it needs bigger heads. The limits come from TW-01, TW-02 and TW-05, not from a fixed number.
- **TW-08, tolerance:** clearance is checked every 3.3 ft (1 m) against the float32 source heights, to within 0.1 ft
  (3 cm). The same inputs give the same towers and forces.
- **TW-09, where it can stand:** for the tower in hand at its height, the line is checked every 5 ft (1.5 m) and the
  places it could stand are marked (the green band). It's recomputed only when something changes.
- **TW-10, speed:** a tower move evaluates in ≤0.5 ms, and TW-09 in ≤2 ms, with 0 B garbage per frame. Task 09
  measures both.
- **TW-11, load gauge:** while building, each tower's gauge shows its per-sheave load in both cases as % of the limit,
  from "pulls up" to "presses down".
- **TW-12, automatic placement:**
  - It follows the same rules as by hand, and never leaves a red tower.
  - It aims for **modest towers, about 30–40 ft (9–12 m)**, going taller only to clear a dip. It uses the fewest
    towers within that, adds hold-downs and combinations where needed, and spaces them evenly when layouts tie.
  - The same line always gets the same towers.
  - When no layout fits, it says why and leaves the line red.
  - The preview updates ≥10 times a second while you aim, off the main thread, with stale results dropped (CF). The
    final placement of a 1-mile (1.6 km) line takes ≤50 ms.
- **TW-13, placement status bar (your request):** a line in the lift menu about the tower in hand.
  - **Status:** OK, or the red reason.
  - **Head and load:** the head it would get, and its load in both cases, as %.
  - **Clearance:** the lowest chair clearance in the span behind it.
  - **Next tower:** how far the next span can reach before chairs drop below 15 ft, at this height and at 100 ft
    ("Next tower within 420 ft, or 610 ft with a 100 ft tower"). It warns when the rope will pull up within that
    reach ("Hold-downs ahead").
- **TW-14, plumb or leaned (your question):**
  - Each lift has a setting, **Towers stand: Plumb or Leaned**.
  - **Leaned** tilts each mast along the line so it's square to the rope there, bisecting the rope's arriving and
    departing angles, up to a cap. That's square to the rope rather than to the ground, because the ground under one
    tower can be bumpy while the rope is smooth.
  - The head then sits up to height × sin(lean) along the line from the base, and the rules use the head's real
    position.
  - On a plumb tower on a steep line, part of the rope's load pushes the mast sideways. The game shows it now, and
    Phase 4 can price it.
  - The default and the cap wait for the real-world check (LC research).

## Lift type properties (LC)

Each catalog entry carries:
- rope diameter, weight and rated tension;
- counterweight tension;
- chair weight, empty and loaded, seats and **chair spacing (fixed for now)**;
- speed;
- gauge;
- sheave size and limits;
- the chair's drop from the rope to its bottom;
- each terminal's train limits.

**Capacity = seats × speed ÷ spacing.** It's shown, not chosen.

*The table below is being checked against real fixed-grip quads; verified values replace these placeholders before
you review.*

| Property | FGQ-4 placeholder |
|---|---|
| Haul rope | 1¾ in (44 mm), 5.2 lb/ft (7.7 kg/m) |
| Rated tension | 60,000 lbf (267 kN) |
| Counterweight tension, per side | 22,000 lbf (98 kN) |
| Chair | 450 lb (204 kg) empty; 1,150 lb (522 kg) with 4 riders |
| Spacing and capacity | 45 ft (13.7 m) at 450 fpm, so 2,400 pph |
| Per sheave | ≤1,500 lbf (6.7 kN), ≥110 lbf (0.5 kN), ≤4.5° bend |
| Rope to chair bottom | about 8.4 ft (2.55 m) |

## Proxies: keeping it fast

All of these are closed-form arithmetic in Domain (engine-free, deterministic):

| # | Stand-in | Instead of |
|---|---|---|
| P1 | The ground under the line sampled once, every 3.3 ft (1 m), when the line is fixed | Terrain queries on every move |
| P2 | Chairs spread as an even load: rope weight + chair weight ÷ spacing | Point loads per chair |
| P3 | Tension from height alone: bottom tension + load × height | A whole-line tension solve |
| P4 | Each span a catenary in 2–4 Newton steps | Elastic catenaries |
| P5 | Each train first a point, then 2–3 Newton steps so every span leaves its last sheave exactly on the tangent (RP-02) | Contact with every sheave |
| P6 | Two load cases plus fixed per-sheave margins | Dynamics, wind and stopping |
| P7 | A tower move re-solves only its two spans and three trains | Re-solving the whole line |

## Rule IDs for the mockup thread

| Mockup element | Rule IDs |
|---|---|
| The lift menu, the type and the Automatic / By hand switch | LF-01, LC |
| The bottom terminal ghost, the anchor and the swing | LF-02, LF-08 |
| The top terminal, which fixes the line | LF-03, LF-05 |
| The tower in hand: slide, Page Up/Down ±5 ft, red with a reason | LF-04, LF-09, TW-02 |
| Why a tower is red | TW-01, TW-03, TW-05, TW-06, RP-05 |
| Head type labels | TW-04 |
| The placement status bar | TW-13 |
| The lift profile above the status bar | LF-10, TW-01, TW-09 |
| The tension bar | RP-03, RP-05, RP-06 |
| The load gauge | TW-04, TW-05, TW-11, RP-04 |
| Plumb or Leaned | TW-14 |
| Fill the rest, Re-place towers | LF-04, LF-06, TW-12 |
| Review, and the built lift's card | LF-06, LF-11, CF |

## Contradictions and changes

- **C8:** Page Up / Page Down are camera zoom today. While a tower is in hand they move it; zoom keeps the wheel
  and + / − (LF-09).
- **C9:** 5 ft steps against 1 m mast sections: the base takes up the remainder. No model change.
- **C10:** the kit's flat support arcs (s4 2.8°, s6 4.2°) are smaller than the bends a loaded rope makes. Capacity
  comes from TW-05, and task 09 rocks the trains to the real bend.
- **C11:** superseded: automatic-only placement, the fixed rope constant, the 300 ft auto span, head types from
  angle thresholds, and choosing the capacity.
- **C12:** the tower kit is modelled plumb. Leaned towers (TW-14) need a tilted base plate on a level pier: a small
  change for the lift asset thread, behind your model approval.

## Rulings so far

| # | Topic | Ruling |
|---|---|---|
| 1 | Difficulty bands | Percent: green under 25%, blue 25–40%, black 40–60%, double black from 60% |
| 2 | Brush | 25–400 ft (7.6–122 m), default 100 ft (30.5 m); minimum bench 25 ft (7.6 m) |
| 3 | Rope speed | 450 fpm (2.29 m/s), fixed; a player setting comes later |
| 4 | Towers | Automatic or by hand per lift; 15 ft chair clearance over bare ground; up to 100 ft |
| 5 | Rope | A catenary with tension from the counterweight and height, and slopes matching at every sheave |
| 6 | Grading cost | None yet; cut, fill and balance tracked in yd³ |
| 7 | Network | No Guest Entrance; lifts are the backbone |
| 8 | Paths | 20 ft (6.1 m) connectors, snapping within 30 ft (9.1 m) |
| 9 | Review format | Markdown only |
| 10 | Order | The bottom terminal, then the top terminal (fixing the line), then towers from the bottom up |
| 11 | Chair spacing | A lift property, fixed for now, not editable |
| 12 | Automatic mode | Automatic by default, last choice remembered, modest 30–40 ft towers, Fill the rest in By hand |
| 13 | Readouts | % of rating with lbf in the detail line while building; simple once built |
| 14 | Load cases | The line empty, and the uphill side full |
| 15 | Numbers | Real-world values, tuned at the style tile |

Also unchanged: cut and fill faces both 1:1 (45°) with no widen-to-fit, and difficulty from the graded surface's
fall-line slope.

## Open questions (my recommendation)

1. **Page Up / Page Down** as in C8? → Yes.
2. **Siting:** TW-03 for towers and LF-08 for terminals? → Yes.
3. **Moving the top terminal** along the line before Confirm (LF-05)? → Yes.
4. **Plumb or Leaned (TW-14):** plumb by default, with the cap from the real-world check? → Yes.
5. **The profile's scale (LF-10):** fit to the panel, with a true-scale switch? → Yes.
