# Controls: the key map

**Audience:** the project owner and coding agents. **Status:** settled (owner, 2026-10-01). **Applies to:** the mountain view (0.4 S6) and photo mode (S10); later tools add their own letters inside the Toolbox. **Supersedes:** the key table in [0.4 §6](phase0-0.4-ui-ux.md#6-default-controls) and U2 in [phase0-decisions.md](phase0-decisions.md), where they differ. The HUD's own keys come from the accepted layout in [game-ui-direction.md](game-ui-direction.md#accepted-for-now-owner-2026-10-01).

## Rules

- **These are the default keys.** Settings › Controls rebinds every one of them (task P2-05, owner 2026-10-08): two
  keys per action, with Shift, Ctrl or Alt if wanted. A key another action had swaps places with it, so no two actions
  that can be live at once share a key (Pause and photo mode's save share Space, because they never are). The HUD's
  captions follow the player's keys. Esc can't be bound; Enter only to a building key (P3-06); a modifier on its own only
  to Erase, which is held like one.

- **The HUD's gameplay keys win.** The camera works around them.
- **Letters belong to the tools while the Toolbox tray is open.** The camera then moves with the arrows, Page Up / Page Down, + / − and the mouse. R, F, P and C wait until the tray closes. (`ViewCamera.LettersToTools` follows the open tray since P3-06; owner ruling P3-06 Q3 kept this rule.)
- **The armed tool's keys win** (P3-06, LF-09). While a tool uses a key, any other action on the same key waits:
  Page Up / Page Down move a tower in hand instead of zooming, Ctrl+Z undoes a stroke instead of the last change, and
  Tab hands the tool window the keyboard instead of opening Analysis.
- **The same key does the same thing in both camera modes.** Free-fly turns about the camera instead of the focus point.
- **Review and debug switches have no keys.** They live in the F1 developer panel. Every command-line flag keeps working for captures.
- **Escape is never rebindable.** It backs out one step: photo mode, then a panel, then the menu.

## Camera (task 11)

| Action | Orbit (default) | Free-fly (C) |
|---|---|---|
| Move | W A S D or arrows: pan. Right-drag: drag the ground | W / S: fly along the view. A / D: strafe |
| Turn | Q / E: rotate. Middle-drag: rotate and tilt | Q / E: turn. Right- or middle-drag: look |
| Tilt | R / F | R / F: pitch up / down |
| Zoom | Wheel, + / −, Page Up / Page Down | Wheel, + / −: fly forward / back. Page Up / Page Down: rise / sink |
| Faster | Shift | Shift |
| Switch mode | C | C (the orbit point becomes the ground in view) |
| Reset the view | Home | Home |

**Bounds (owner, 2026-10-01):**
- The orbit point never leaves the ring.
- The orbit eye may swing out up to 2 km past the edge to see the diorama walls. It never goes below the plinth top.
- Free-fly stays over the ring.
- Over the terrain, the near plane stays at least 0.5 m above the 12 in of snow.
- The closest orbit distance is 2 m.

## View and layers

| Key | Action | Owner |
|---|---|---|
| H | Hide all UI | Task 11 |
| P | Photo mode: the photo bar replaces the HUD (see [Photo mode](#photo-mode)); Esc or P leaves and puts the view back | Task 11; the bar is P2-07 |
| Shift+1 | **Map layer:** Snow | Task 12 |
| Shift+3 | **Map layer:** Trees | Task 12 |
| Shift+2 | **Map layer:** Lifts (models, rope, chairs and map lines) | Task P3-09 |
| Shift+4, 5 | Free, for map layers still to come (trails and the rest as they're built) | — |
| Shift+6 | **Info layer:** Contours (combine with any info layer) | Task 12b |
| Shift+7 | **Info layer:** Slope angle | Task 12b |
| Shift+8 | **Info layer:** Slope exposure | Task 12b |
| Shift+9 | **Info layer:** Snow depth | Task 12b |
| Shift+0 | **Info layer:** Snow conditions (reserved for the snow simulation) | Task 12b |
| F1 | Developer panel: every key, plus snow, tree snow, wind, light preset, time and date, haze, lake state, distant shadows, camera mode and saved views (Home, Corbet's) | Task 11 |
| Esc | Back out one step; the menu | HUD |

**Map layers and info layers (owner, 2026-10-02).** Map layers show or hide physical things on the map (snow and trees now; lifts and the rest later), in any combination. Info layers only display information. Slope angle, Slope exposure, Snow depth and Snow conditions take turns: switching one on switches off the one that was on, and pressing its key again switches it off. Contours combine with any of them. The HUD's Layers panel has the same two groups, and the info layer that's on shows its legend card. Ground cover and Imagery are no longer layers, and the cover map is a developer view in the F1 panel.

## Photo mode

Built in task P2-07 (0.4 S10; owner, 2026-10-08). These keys work only in photo mode, so they can share keys with the
view's (H, Space). The camera keeps WASD, the arrows, Q/E, R/F, the zoom keys, C and Home. Every key is rebindable
(Settings › Controls).

| Key | Action |
|---|---|
| Space or F12 | Save a photo (PNG, at the screen's resolution or 2×) to `Pictures\Ski Area Design Challenge` |
| H | Hide the photo bar; H again shows it |
| [ and ] | Time: a quarter hour earlier or later (the sun) |
| Shift+[ and Shift+] | Date: a day back or on (the sun's path through the year) |
| Tab | Use the photo bar with the keys (task P3-01): Tab and Shift+Tab move between its controls, the arrows move a slider or between buttons, Enter or Space presses one; Esc gives the keys back to the camera |
| P, or Esc while the camera has the keys | Leave photo mode: the clock, time, grade, lens and info layers go back as they were |

## HUD (accepted layout; built in task P2-02)

| Key | Action |
|---|---|
| T | Toolbox (built in P2-02) |
| **Tab** | Analysis (moved from A, which pans; owner, 2026-10-01). Built in P2-02; inside the menu or a window Tab moves between its controls |
| U | Units: feet or metres, every figure together (imperial by default; also in the menu; remembered). Built in task 12b.2 |
| Space | Pause: the clock runs the sun (built in P2-02; in photo mode Space captures instead) |
| 1–4 | Game speed: 1, 3, 10 or 30 game minutes a second; choosing one starts the clock (built in P2-02) |
| Ctrl+S | Save the design (task P3-04): the first save asks for a name, then saves to it; rebindable (`GameAction.Save`) |
| Ctrl+Z / Ctrl+Y (or Ctrl+Shift+Z) | Undo / redo a change to the design, when no tool is armed (P3-06) |
| Inside the open Toolbox tray | Tool letters: N (New) / M (Edit) / X (Remove) on the Lifts and Trails tabs (P3-06; Edit moved off E, which rotates the camera, TL19). The camera keys work at all times, so a tool letter never uses W A S D, Q E, R F or C: the planned G / W / P and L / R / K pick new letters for W and R with their tools |

## Building tools (task P3-06; rules TF-09, LF-09, CF-07)

Live while a tool is armed, which happens only with the tray open. All rebindable (SH-07). The camera keys keep working throughout (owner, 2026-10-19: TL19); the brush and tower keys take [ ] and Page Up / Page Down only while the tool uses them.

| Key | Action |
|---|---|
| Enter (or Num Enter) | Next step |
| Ctrl+Enter | Break ground, from the review |
| [ / ] | Brush narrower / wider (5 ft; trails) |
| Shift (held) | Erase while painting |
| Ctrl+Z | Undo the last stroke, while painting |
| Page Up / Page Down | The tower in hand or the selected tower up or down 5 ft |
| Page Up / Page Down | The terminal in hand up or down 1 ft (P3-09, LT19) |
| , / . (Shift: 45°), Shift + wheel | Turn the bottom terminal in hand 5° (a wheel notch 15°; P3-09, LT21) |
| Enter | Remove lift: remove the selected lift (P3-09) |
| , / . (Shift: 50 ft) | Slide the tower in hand or the selected tower down or up the line 5 ft (P3-09, LF-04, LT17; claimed in turn with the terminal turn) |
| Enter | Towers by hand: place the tower in hand; with the line finished, go on to the review (P3-09) |
| Delete | Towers by hand: take the last tower back (P3-09) |
| [ / ] | Edit lift: the previous / next part (bottom terminal, towers, top terminal; P3-09) |
| Ctrl+Z | Edit lift: undo the last change inside the tool; Apply (Ctrl+Enter) commits the edit as one step (P3-09, LT2) |
| Delete | Delete the selected tower or trail |
| Insert | A line's towers (New lift's Towers and Review, Edit lift): a tower in hand to add between the others (P3-09, F2) |
| K | A tower selected (New lift, Edit lift): its kind, round Auto, support, hold-down and combination; the game sizes it, and a kind that can't carry the rope there is red (P3-09, LT44) |
| Tab | The tool window takes the keyboard: the arrows walk its controls, Enter or Space presses one; Esc gives the keys back |
| Esc | Back one step; at the first step the tool closes, asking first if anything is drawn (fixed) |
| Left-click / left-drag | Place, paint (the tool's own button; right-drag still pans, middle-drag still turns) |
| B / L / O / G | The Terrain tab's tools: Raise or lower, Flatten, Smooth, Slope (P3-15, TB-01; letters off W A S D, Q E, R F and C) |
| [ / ] | Terrain brushes: diameter 5 ft narrower / wider (10–400 ft, TB-02) |
| , / . | Terrain brushes: strength 5% weaker / stronger (TB-02) |
| Page Up / Page Down (Shift: 10 ft) | Flatten and Slope: the target elevation up or down 1 ft (TB-05, TB-07) |
| Right-click / right-drag | Terrain brushes own the right button while armed (owner BR4): Raise/Lower lowers, Flatten and Slope take the target under the pointer; WASD, the arrows and middle-drag still move the view |
| Enter | Terrain brushes: Break ground opens the review; in the review, Confirm (TB-08) |
| Ctrl+Z | Terrain brushes: take back the last pending stroke (TB-08) |

## Removed from the keyboard

These moved into the F1 panel, and each keeps a command-line flag:

| Was | Now | Flag |
|---|---|---|
| N: ground snow | F1 panel, Shift+1 | `-nosnow` |
| T: tree snow | F1 panel | `-baretrees` |
| B: wind | F1 panel | `-wind calm\|breeze\|strong` |
| L: light preset | F1 panel (sets the clock to the preset's time) | `-light dawn\|noon\|golden\|night`, `-time HH:MM`, `-day N` |
| M: haze | F1 panel | `-nohaze` |
| V: cover map | F1 panel (a developer view since task 12b) | `-covermap` |
| C: fly to Corbet's | F1 panel saved view (C is free-fly now) | `-landmark` |
| (new) lake state | F1 panel | `-lake snow\|ice\|open` |
| (new) distant shadows | F1 panel | `-nofarshadows` |

Layer flags for captures: `-nosnow`, `-noforest`, `-info slope|exposure|depth`, `-contours` and `-units metric|imperial` (for that run only).
