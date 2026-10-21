# P3-09 · Monta findings and tower editing (owner review of PR #94)

The owner's findings from playing PR B (demo 68), logged as given, one at a time. No fixes until the plan that follows
the list is agreed. The Sessellift FGQ-4 lifts look right ("really liking what I am seeing"); these are the Monta's.
Screenshots are kept locally under `test-results/p3-09-monta/` (not in git).

| # | Finding | Where | Notes |
| --- | --- | --- | --- |
| M1 | **No hold-down tower as tower 1.** The Monta's bottom terminal has its hold-down tower built in, so the line shouldn't add one. | Bottom terminal (the Monta drive) and the first tower up the line: a sheave train on both sides of the rope stands just past the terminal's own hold-down row. `M1.webp` | The automatic placement puts TW-16 v3's bottom station tower after every bottom terminal; the Monta drive's own row already does that job. |
| M2 | **The tower sheaves don't sit on the rope.** On every Monta tower, wherever the lift is placed, the sheave trains are off the rope; the cable, tower placement and tower types look right. | Line towers, hold-downs and combination towers alike. `M2-1.webp` (a lit tower from above: the trains sit off the rope lines), `M2-2.webp` (a combination tower: the rope runs between and beside the trains), `M2-3.webp` (a support tower: the rope passes the trains instead of resting in them). | Seen on the Monta only; the Sessellift's towers sit right. To check when planning: the line gauge the rope is solved at against the tower heads' sheave lines (LT12 narrowed the Monta to the Sessellift's 4,120 mm), and the trains' posing on a Monta line. |
| M3 | **The terminal's piers stick out of the ground.** The bottom terminal should stand on the ground where it's placed (at the level set with Page Up / Page Down), and the ground directly under it should be flattened automatically. **The same flattening for the Sessellift.** | Monta bottom terminal (the drive): both piers' footings stand as blocks above the snow, on the slope's high and low sides alike. `M3-1.webp`, `M3-2.webp` | Today a terminal's pad only cuts (LF-12 v3, from the style tile), so on a slope the downhill footing stands proud, and a steep site raises the terminal on its pier (LF-08 v2). "Flatten" here asks for fill under the terminal too: that changes LF-12 v3, so it's a ruling for the plan. |
| M4 | **The Monta's top terminal foundations stop short of the ground: extend them down.** | Top terminal (the return): the column's and the strut's foundation blocks hang above the snow. `M4.webp` | The return's two foundations reach only 2.5 m below its 0.00 level (`monta_fg4.json` return pads, `-2500` mm), and the drive's piers 2.9 m. The Sessellift's model contract carries its piers 15 m down, so a terminal on lower ground still stands on them. |

## Features asked for (2026-10-20)

| # | Feature |
| --- | --- |
| F1 | When editing towers, Page Up / Page Down change the selected tower's height. |
| F2 | Towers can be added while editing (and while re-placing towers). |
| F3 | "Re-place towers" becomes **Edit towers**; the edit menu gets **Revert to auto**. |
| F4 | Placing towers by hand while building: choose the height and see the tower live before placing it, as with the terminals; once placed, the lift line updates to show what it does; placed towers can be edited while placing. |
| F5 | Lifts can be named: click the name in the New lift or Edit lift window to change it. |
| F6 | Clicking a lift on the map opens a lift properties panel. |

## What causes them (checked 2026-10-20)

A Monta check lift built in game (`-styletile -extralift 650,-300 -extramonta`) logs: *"15 towers (tower_d8 first, at 55 ft,
15 ft) … trains not posable (shipped models) … raised: bottom 30 ft (steep site 12 ft) … rope leaves the bottom at 18.0°
(OVER TW-16)"*. The Sessellift line on the same map logs *"trains posed (tangent within 0.18°)"*.

- **M1.** Automatic placement (TW-16 v3) puts a bottom station tower after every bottom terminal, aimed at its row. The
  Monta's bottom is its drive, whose hold-down row already holds the rope down (LT6, TW-16 v3's "each end is the model
  standing there"), so the station tower repeats the terminal's job: a 15 ft hold-down (d8) 55 ft out.
- **M2.** The view poses every train to the rope only when every train on the line can turn (`TrainPosing.Posable`: rocker
  pivots). The Monta's top return has a fixed deflection row with no pivots (LT6), so one fixed row turns posing off for the
  whole line. Every tower's trains then stay at rest and the rope is drawn straight between the half-gauge lines, beside or
  between the sheaves. The towers' heads are the Sessellift's, at the same 4,120 mm gauge (LT12), so the gauge isn't the fault.
- **M3.** The terminal doesn't stand where it's placed. The build raises it automatically: by LF-08 v2 on a steep site (12 ft
  here), then 1 ft at a time as a last resort while the rope leaves its row too steeply (to the 30 ft cap here, since the
  extra station tower made the exit too steep). Its pad only cuts (LF-12 v3), so nothing fills under the downhill side. The
  piers' footings stand at the ground under them, and a raised terminal leaves its piers standing out.

- **M4.** A model limit: the Monta's foundations are short blocks (the return's to 2.5 m below 0.00, the drive's piers to
  2.9 m). Wherever the ground under them is lower than that (a downhill side, a pad that only cuts), they hang in the air.
  The Sessellift's piers run 15 m down with a footing the game places at grade (`pier_top`), so it never shows.

## Plan

### 1. Monta: no station tower after a hold-down terminal (M1)
- **Rule (TW-16 v4):** a terminal whose row holds the rope down (the Monta's drive at the bottom) is its own station. No
  station tower is placed after it, automatically or as the first tower by hand. The first line tower must keep the rope's
  exit within the row's range (the Monta's 6–16°): automatic placement picks its place and height under that limit, and by
  hand the green band (TW-09) includes it.
- **Where:** `TowerPlacement.Place` (Domain), and `LiftBuild.Build`'s station-steel step, which today assumes `towers[0]` is a
  station tower. Also `TowerInHand.FirstAlong`, whose first slot is the station zone's today.
- The Sessellift (a support-row return at the bottom) keeps its station tower: its placements don't change (golden).
- The same rule applies to a top terminal with a hold-down row; neither model has one today.

### 2. Monta: the sheaves sit on the rope (M2)
- `LiftView.TryPosedLoop` reads a terminal train that can't turn as **rigid** (`TrainPosing.Read(..., rigid: true)`, or
  where the model gives no sheave pivots, the rope's own `rope_*_out` exit socket as a fixed point). It no longer gives up on
  the line, so every tower's trains rock to the rope as the Sessellift's do. **No model change.**
- Report in the build log what was rigid, so any future model without pivots can't silently switch posing off.
- **And the model (owner, 2026-10-20: "they should be able to rotate like the SSL"):** the Monta return's deflection row
  gets rocker pivots like the Sessellift's rows, in 3b's model session, so its trains rock to the rope too. LT6's fixed row
  ends there.

### 3. Terminals stand where they're placed, on flattened ground (M3, both makes)
- **Level (LF-12 v4):** a terminal stands at the ground at its origin plus the level set with Page Up / Page Down, nothing
  more. The build no longer raises it, whether for a steep site (LF-08 v2's raise) or as a last resort (the exit-angle,
  unload-ramp and trench-cap raises in `LiftBuild.Build`). Where the line needs a higher (or lower) terminal, the review says
  so with the amount: an amber or red line with its rule, e.g. *"The rope leaves the bottom terminal at 18°: raise it 6 ft
  (Page Up)"*. The player decides.
- **Flattening (LF-12 v4):** the terminal's pad is graded to its level across its footprint, cutting the high side and
  **filling the low side**, with the trail doctrine's 1:1 faces outside it. The piers' footings then sit on the pad, and the
  pier shows only as much as the level set by hand. Both makes, both ends.
- **Where:** `TerminalPad.Grade` (cut and fill), `LiftBuild.StartLevels` and `Build` (no raises), `LiftProblems` (the
  "raise it N ft" lines), the ghost's amber ("the ground under it varies N ft: it will be levelled").
- **Saves:** no format change; levels are already stored. A saved lift's pads regrade the next time the fold rebuilds them.

### 3b. The Monta's foundations reach the ground, and its return's trains turn (M4, M2: a model change)
- **Model contract, as the Sessellift's:** every Monta pier and foundation (the return's column and strut foundations, the
  drive's two piers) runs to 15 m below the terminal's 0.00 level, each with a `pier_top` socket at its top centre.
- **The return's deflection row** gets rocker pivots (`_pivot_rocker_<side>N`, its sheaves `_pivot_sheave_<side>N`) with
  stops from its real range, so the view poses it as it does the Sessellift's rows.
- **Game:** `LiftView.PlaceFooting` places a footing at grade under every `pier_top*` socket, not just the first, so a
  two-pier terminal gets two footings.
- **Who builds it:** a model session (Blender, `tools/assets/lifts`, `monta_fg4.json` and the `monta_return` /
  `monta_drive` modules). Renders and overlays go to you for sign-off before any LiftImport or Unity build (the model rule).
  It runs alongside the code work here, and this branch merges its result.
- With 3's flattening the pad meets the terminal at its level, so the deep piers show only beyond the pad's edge on very
  steep ground, as the Sessellift's do.

### 4. One tower editor for both tools (F1, F2, F4)
Today Edit lift has its own tower editing, and New lift's towers by hand can only take the last tower back. Both tools get
the same editor (`LiftDraftEditor`, UI/Tools) over a draft of the line: terminals, towers, levels, its own undo.
- **Selecting what you point at (F1):** a click selects the tower or terminal under the pointer in 3D, through the lift
  pick (LT23) extended to say which part was hit. Today the click uses the ground point under the pointer, which lands behind
  a tower you're pointing at, so clicking a tower rarely selects it. `[` and `]` still step through the parts. The selected
  tower is lit (white, UI-15).
- **Height (F1):** Page Up / Page Down change the selected tower 5 ft. The window shows it as the mockup's stepper
  (‹ 35 ft ›) with the tower's head, its load gauge (TW-11) and its status (TW-13: OK, or red with the reason).
- **Slide and remove:** `,` and `.` slide it 5 ft (Shift 50 ft); dragging it slides it; Delete removes it.
- **Adding (F2):** a click on the line where there's no tower, or **Add tower** in the window (keyboard: Insert), puts a
  tower in hand there. It follows the pointer between its neighbours with the green band (TW-09) and is placed with a click or
  Enter. Changing a tower by hand makes the lift By hand, as today.
- **Where it works:** New lift's Towers step and its Review (the mockup's "every tower stays editable"), and Edit lift.

### 5. Edit towers and Revert to auto (F3)
- **Edit towers** replaces "Re-place towers". On an automatic line (New lift's Towers and Review steps), it opens the placed
  towers for editing as in 4; the first change makes the lift By hand.
- **Revert to auto** (New lift and Edit lift, whenever the lift is By hand or its towers were changed) places every tower
  automatically again and sets the lift back to Automatic. It asks first when towers were placed or changed by hand (LF-01),
  and it's one step of the tool's undo.
- **Fill the rest** stays for towers by hand (automatic towers past the last one placed).

### 6. The line in 3D while you build it (F4)
- **The tower in hand** stands on the line: its place is the pointer projected onto the line, on the 5 ft grid, facing along
  the line and leaned per the stance. It's its own model, as the terminals are (LT22): base, mast and the head it would get, at the height
  set with Page Up / Page Down, see-through and tinted green where it can stand, red where it can't. **The game picks its
  head** from the rope's bend and loads (TW-04, LT5); the ghost and the window show it ("for now" until the span past it
  is strung). The player chooses where and how tall, not the head. New `TowerGhost` (Presentation/Lifts): made only when its height or head changes, moved each frame without
  allocating.
- **The line so far:** placed towers and the terminals are drawn as real models, with the rope strung through them and
  posed on their sheaves (2). The rope past the last placed tower runs see-through to the top terminal. It's redrawn after
  every placement or edit, never per frame. This is a draft `LiftView` with no chairs (the chairs come at Break ground).
  The profile strip updates with it, as today.
- **Edit lift shows the draft the same way:** the built lift is hidden while it's open and the draft drawn in its place, so
  a moved tower or terminal is seen before Apply. This replaces LT26's "keeps its old shape until Apply".

### 6b. Naming a lift (F5)
- The lift's name in the tool window's title ("New lift · Chair 1", "Edit lift · Chair 1") is a button: a click (or F2,
  as Manage Areas renames) turns it into a text field. Enter or a click elsewhere keeps the name; Esc puts the old one back.
  While you type, no key binding fires (P3-08's `KeyBindings.Typing`).
- **New lift:** the name goes into Break ground's `AddLift` (or Save plan's). The next lift's default stays "Chair N".
- **Edit lift:** the new name is part of Apply's one step: `EditLift` gains the name, or `RenameLift` goes in the same step.
- The name is checked as every design name is (`DesignRules.NameProblem`: not empty, at most the limit, no leading or
  trailing space); a bad one says why under the field and isn't kept.

### 6c. The lift properties panel (F6)
- With no tool armed, clicking a lift on the map (the lit one, LT23) opens its panel: the same card Analysis › Lifts opens.
  It sits at the right of the map, the lift stays lit while it's open, and Esc or ✕ closes it.
- **What it shows:** the name (click to rename, as in 6b, one undoable `RenameLift`), the model, length, vertical,
  capacity, ride time, towers and their heads ("14 · 6 s6, 4 s4, 2 b8, 1 d8, 1 c8"), the lowest chairs, the rise against the
  type's limit, and Status. Below them: **Edit** and **Remove**, which open those tools on this lift (as the card does today).
- A planned lift's dashed line opens it too (plans pick by their line, as Remove lift does).
- With Edit or Remove lift armed, a click keeps its tool meaning; with any other tool armed, clicks belong to that tool.

### 7. Tests and checks
- **Core:**
  - The Monta's first tower is a line tower and its exit stays within 6–16°, on the made-up slopes, the 32-line fan and
    Sugarloaf.
  - The Sessellift's placements are unchanged (golden).
  - A pad fills its low side to the level, with 1:1 faces.
  - A terminal's level is exactly ground plus the level set by hand.
  - A line that needs a raise says so and by how much.
- **EditMode:**
  - A Monta lift built in the view is posed (`Posed`, pose error under 1°).
  - The editor: select a tower by its pick, change its height, slide it, add one in a gap, delete one, undo.
  - Edit towers and Revert to auto in both tools.
  - The tower ghost and the draft view allocate nothing per frame while the hand moves.
  - Naming: New lift's name reaches `AddLift`; Edit lift's rename is in Apply's one step; a bad name is refused with its
    reason; keys don't fire while typing.
  - The properties panel: a click on a built lift with no tool armed opens it with that lift's figures; its rename is one
    undoable step; Edit and Remove open their tools on the lift.
- **PlayMode:** New lift by hand with the keyboard and the mouse: place three towers, raise one, add one, revert to auto.
  Break ground; Edit lift on it; Apply; one Ctrl+Z.
- **In game:** captures of M1–M3 before and after, the tower ghost green and red, the draft line after a placement, and
  Edit lift's draft; demo 68's notes; `-liftbench 40` with a third of them Montas.
- **Benchmark:** demo 41 High and Medium, and the 40-lift run; the draft view's rebuild time after a placement (target
  under 50 ms).
- **Goldens:** the lift earthwork goldens and the Sugarloaf fixture's expected pads change with LF-12 v4. That needs your
  OK (question 2).

### 8. Delivery
Everything goes on `feature/p3-09-lifts` into the open PR #94, in this order: M2, M1, M3, then 4–6; 3b's model session runs alongside and its branch merges in before the captures. Decisions LT32 onward
go in `phase0-decisions.md`, with revision blocks for TW-16 v4 and LF-12 v4 in `p3-02-design-rules.md`. The mockup's lift
window (`ui-layout.html`) gets Edit towers, Revert to auto, Add tower and the clickable name, and its lift card the
panel's figures, so the UI parity stays true. Estimate: about a day
for M1–M3, two to three for F1–F4, and half a day for F5–F6.

## Questions for you (all answered as recommended, owner 2026-10-20: "Yes to all recs")
1. **Automatic raises (M3):** should the game never raise a terminal, and say how much to raise it? Or should it keep
   raising, with you able to lower it again? *Recommended: never, and say how much (the player sets the level).*
2. **Flattening (M3):** fill under the low side with 1:1 faces outside the footprint (the trail doctrine), which re-pins
   the lift earthwork goldens? *Recommended: yes.*
3. **The Monta's top (related):** the same check lift's rope reaches the top return at 0.4°, flagged against its fixed
   row's range. Look at that after the fixes, or now? *Recommended: after; it may change once posing works.*
4. **Edit towers (F3):** I read it as in 5: Edit towers opens an automatic line's towers for editing, and Revert to auto
   places them all automatically again. Is that what you meant? *Recommended: yes.*
5. **The line while building (F4):** placed towers as real models, with only the tower in hand and the unstrung rope
   see-through? Or everything see-through until Break ground? *Recommended: real models.*
6. **Adding a tower (F2):** by clicking on the line where there's none, plus an Add tower button and Insert for the
   keyboard? *Recommended: yes.*
7. **The Monta's foundations (M4):** start the model session now, so it runs alongside the code fixes? *Recommended: yes;
   I'll offer it as a session card once you say go.*
8. **Renaming from the properties panel (F6):** the panel's name can be clicked to rename too, without opening Edit lift?
   *Recommended: yes, one undoable step.*
