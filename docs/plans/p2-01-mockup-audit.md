# P2-01: the Unity UI against the accepted mockup

Task P2-01 (UI foundation) compared every Unity UI Toolkit screen with the accepted HUD mockup,
[prototypes/ui-layout.html](prototypes/ui-layout.html) (game-ui-direction.md UI-6 to UI-11). The owner asked for the mockup
to be followed closely (2026-10-06).
- Task 01 fixed what it owns: the shared look, type, components and the screens outside the HUD.
- Everything the HUD still lacks is listed here for task 02.

**How it was compared:**
- The mockup was rendered with headless Edge at 1920×1080 (`#solo&demo=…`, a fresh profile for each shot).
- Unity was captured with `-uicapture` (`demo.bat` 47) at the same size, in both themes.
- The captures stay out of git (`test-results/`).

## 1. Foundation: fixed in task 01

| Area | Before | Now |
| --- | --- | --- |
| Size | Mockup pixel values on a 1920×1080 base, so everything was about ⅔ of the mockup's size | Panels lay out on the mockup's own 1280×720 stage (`UiPanels`). **100% is the mockup's size** (owner); 50–150% scales from there |
| Colours | Each screen kept its own palette; light theme only in the picker | One palette per theme in `Theme-Dark.tss` / `Theme-Light.tss`, with every mockup token. `UiThemeTests` checks them value by value against the mockup |
| Panels | Mockup's 96% translucency (shows ~13% of the map in Unity's linear blending) | Solid, as the mockup reads in a browser |
| Light-theme text | Visibly thinner and paler than dark | A 0.3 px outline in each text's own colour (light theme only) matches the dark theme's weight. Gamma blending was tried and washed every colour out |
| Components | Per-screen buttons, segments and heads | The mockup's window, 38 px head, square ✕, green "go", outlined "ghost", segmented switch, ‹ value › stepper, settings rows and category list, once, in `Base.uss` (`mp-*`) |
| Settings | Small units-only dialog | The mockup's Settings window: categories on the left; Interface (Theme Dark/Light/Auto, Interface scale ‹ 100% ›), Units and time (Metric/Imperial); Restore defaults and Done; the resort's name in the head in game |
| Theme | Dark or light, HUD only | Dark, Light or **Auto** (light from sunrise to sunset), for every screen, remembered |
| Keyboard | Mouse first; a few screens focused a control | Every screen opens with a control focused; arrows move to the nearest control, Tab cycles, Enter presses, Esc backs out; modals keep focus and give it back; one focus ring, shown only after a key |
| Ultrawide | Full-screen panels stretched to the screen | Title, library and modals sit in a centred 16:9 column; cards and the HUD stay on the screen's edges |

## 2. The HUD: for task 02

Unity's HUD is still the Phase 1 style-tile layout. Task 02 builds the mockup's HUD on the foundation above
(`mp-*` components, theme tokens, `UiFocus`, `UiPreferences`). Everything below is in the mockup and not yet in
Unity:

1. **Status bar.** Full width at the bottom, docked or floating (`S.docked`). Left to right:
   - Toolbox (claw hammer) and Analysis;
   - the resort's name, with open/closed (style A: a line under the name);
   - pause and the 1–4 speed arrows;
   - Day N with the calendar date under it;
   - the clock with the weather line (symbol, temperature, next 24 hours' snowfall);
   - the bank balance with the day's change under it;
   - lifts open, trails open and guests (each opens its Analysis tab);
   - elevation under the pointer;
   - Save (lights only when there is something to save).
2. **Top right.** Three 40 px square buttons: sketch, map layers, menu. The Phase 1 top bar, its quality badge and
   "Orbit camera" go.
3. **Menu dropdown.** Quick switches (theme Dark/Light/Auto, status bar Docked/Floating, units), then Settings,
   Exit to title and Quit.
4. **Map layers dropdown.** Map layers, then info layers, keys shown only here. One legend card holds the info layer and
   contours together.
5. **Toolbox tray.** Tabs, a tool label and 64×60 tool tiles. Letters go to the tools while it's open.
6. **Analysis panel.** Tabs: Overview, Lifts, Trails, Snowmaking, Amenities, Guests, Weather (Outlook/Calendar),
   Finances (Week/Month/Season). Charts are hoverable.
7. **Floating panels.** Draggable `.pn` windows with a 38 px head for tools and selections.
8. **Resort stats.** The window that clicking the resort's name opens (summit, base, vertical, lifts, uphill
   capacity, trails, acreage, snowfall, snowmaking), with Rename in it.
9. **Time and sun.** The Phase 1 bottom time bar (Dawn/Noon/Golden hour/Night presets and slider), compass and scale
   bar have no place in the mockup. The clock in the bar and the speed arrows replace the presets; the compass and
   scale bar go or move to the map layers.
10. **The rest of Settings.** Status bar, map labels, tooltips, notifications, clock, bank balance format, Gameplay,
    Graphics, Audio and Controls pages. Add each as its setting starts to work.

## 3. Screens outside the mockup

The title, library, download card, pill, quality card, dialogs and the site picker predate the mockup's
HUD. Task 01 moved them onto the mockup's components and sizes. Their layouts are task 14's and task 13's,
accepted then, and unchanged.
