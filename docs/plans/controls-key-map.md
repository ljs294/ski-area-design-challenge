# Controls: the key map

**Audience:** the project owner and coding agents. **Status:** settled (owner, 2026-10-01). **Applies to:** the mountain view (0.4 S6) and photo mode (S10); later tools add their own letters inside the Toolbox. **Supersedes:** the key table in [0.4 §6](phase0-0.4-ui-ux.md#6-default-controls) and U2 in [phase0-decisions.md](phase0-decisions.md), where they differ. The HUD's own keys come from the accepted layout in [game-ui-direction.md](game-ui-direction.md#accepted-for-now-owner-2026-10-01).

## Rules

- **The HUD's gameplay keys win.** The camera works around them.
- **Letters belong to the tools while the Toolbox tray is open.** The camera then moves with the arrows, Page Up / Page Down, + / − and the mouse. R, F, P and C wait until the tray closes. (`ViewCamera.LettersToTools` is the switch the HUD sets.)
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
| P | Photo mode: the HUD hides; F12 or Space saves a PNG to `Pictures\Ski Area Design Challenge`; Esc or P exits | Task 11 (the photo bar's other controls come with the UI) |
| Shift+1 | Snow layer | Task 11 binds; task 12 builds the layers |
| Shift+2 | Ground cover layer (reserved) | Task 12 |
| Shift+3 | Forest layer | Task 11 binds |
| Shift+4 | Cover map | Task 11 binds |
| Shift+5 | Imagery (reserved) | Task 12 |
| F1 | Developer panel: every key, plus snow, tree snow, wind, light preset, time and date, haze, lake state, distant shadows, camera mode and saved views (Home, Corbet's) | Task 11 |
| Esc | Back out one step; the menu | HUD |

## HUD (accepted layout; built by the UI thread)

| Key | Action |
|---|---|
| T | Toolbox |
| **Tab** | Analysis (moved from A, which pans; owner, 2026-10-01) |
| U | Units |
| Space | Pause (in photo mode Space captures instead) |
| 1–4 | Game speed |
| Ctrl+S | Save |
| Enter | Finish a line |
| Inside the open Toolbox tray | Tool letters: N / E / X, G / W / P, L / R / K |

## Removed from the keyboard

These moved into the F1 panel, and each keeps a command-line flag:

| Was | Now | Flag |
|---|---|---|
| N: ground snow | F1 panel, Shift+1 | `-nosnow` |
| T: tree snow | F1 panel | `-baretrees` |
| B: wind | F1 panel | `-wind calm\|breeze\|strong` |
| L: light preset | F1 panel (sets the clock to the preset's time) | `-light dawn\|noon\|golden\|night`, `-time HH:MM`, `-day N` |
| M: haze | F1 panel | `-nohaze` |
| V: cover map | F1 panel, Shift+4 | `-covermap` |
| C: fly to Corbet's | F1 panel saved view (C is free-fly now) | `-landmark` |
| (new) lake state | F1 panel | `-lake snow\|ice\|open` |
| (new) distant shadows | F1 panel | `-nofarshadows` |
