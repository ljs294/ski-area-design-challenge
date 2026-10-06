# HUD parity: the mockup and the game

**Task:** P2-02 (the HUD in the Trailhead direction). **Owner rule (2026-10-06):** the HUD follows the accepted mockup,
[prototypes/ui-layout.html](prototypes/ui-layout.html), closely. The mockup stays the place to try HUD changes: prototype
there first, get the owner's OK, then port to Unity and check the port with the tools below.

## How the two are kept the same

| Piece | Where | What it does |
|---|---|---|
| Part names | `data-ui="…"` in the mockup; element `name` in [Hud.uxml](../../Assets/MountainPlanner/Art/UI/Hud.uxml) | One name per part on both sides (`bar-clock`, `menu-theme`, `layer-slope`…). `HudTests.EveryMockupPartHasItsElement` fails if the mockup names a part the game lacks |
| `#demo=p2` | the mockup's demo flags | The game as built in Phase 2: greyed dashes for figures with no data yet, greyed tools, Sketch, Save and Rename, each Analysis tab's empty-state line, the Guests tab greyed, no lifts or trails drawn, 15 January at 10:30, paused. It combines with every other flag (`p2,menu`, `p2,aweather`, `p2,light`). When a phase makes a part real, take its `p2` greying out of the mockup |
| States | [tools/ui-parity/states.mjs](../../tools/ui-parity/states.mjs) and `HudStates` in `AppFlow.UiCapture.cs` | Each state is a mockup flag set and a `-uicapture` shot `s6-<name>` set up the same way. Add a state to both |
| Measurements | `-uicapture` writes `<shot>.layout.json` beside each 1920×1080 picture at 100%; `parity.mjs` measures the mockup in headless Edge | Each named part's box on the 1280×720 stage, colours, type size and leaf text |
| Comparison | `node tools/ui-parity/parity.mjs` (`demo.bat` 48) | Differences beyond ±2 px, ±6/255 per colour channel or ±0.5 px type go to `test-results/ui-parity/report.md`, with side-by-side sheets outlining each differing part |
| Symbols | `node tools/ui-parity/extract-icons.mjs` | Remakes the game's icons from the mockup's `<symbol>` set (`Art/UI/Icons/Resources/HudIcons`, `HudIconLayers.g.cs`) |
| Colours | `UiThemeTests` (task P2-01) | Every theme token equals the mockup's `.hud` / `.hud.light` value |
| Review | a fresh, read-only reviewer at each checkpoint | Hunts for differences in looks and behaviour (every click, key and Esc step in the mockup's script) between the two; returns findings only. It judges fidelity, not taste: the owner decides |

## States

| State | Mockup flags | What it shows |
|---|---|---|
| `hud` | `p2` | The bar, the top-right buttons, the map credit |
| `float` | `p2,float` | The bar floating 10 px in |
| `menu` | `p2,menu` | The menu with its quick switches |
| `layers` | `p2,layers` | The map layers dropdown |
| `slope`, `exposure`, `depth`, `contours` | `p2,slope` … | Each legend card alone, and the bar's readout |
| `slope-contours` | `p2,layers,slope,contours` | The dropdown with the legend under it, info layer and contours together |
| `tray-lifts` … `tray-infra` | `p2,tray`, `p2,ttrails`, `p2,snow`, `p2,infra` | The Toolbox on each tab |
| `analysis`, `analysis-lifts`, `analysis-weather`, `analysis-finances` | `p2,aoverview` … | Analysis on a tab |
| `rstats` | `p2,rstats` | The resort's stats over the scrim |

## Known differences

Accepted with the plan (owner, 2026-10-06), or not measurable:
- **Quit to desktop** stays in the menu (the mockup's `p2` shows it too).
- **Place line:** the mockup's menu and stats show "Jackson Hole, Wyoming" under the name; packages don't store a place
  name, so the game shows where it is as coordinates ("43.59° N, 110.85° W").
- **The menu holds the keyboard** (task P2-01): Esc opens it with Resume focused, and the game keys wait until it closes.
  In the mockup the menu is a plain dropdown and the keys still work.
- **Line height 1.4** (legend notes, the stats footer): Unity has no line-height setting, so wrapped notes run a little
  tighter.
- **Contour labels** are the game's own (task 12b.2), not the mockup's stand-ins.
- **Shadows:** UI Toolkit on Unity 6.3 has no box shadows; the mockup's soft panel shadows are left out.
- **Live words:** the resort's name, the clock, the day and date, the elevation and the readout are compared by box and
  colour, not text (`NO_TEXT` in states.mjs). The mockup's calendar puts 15 January in 2027 (a Friday); the game's
  view year is 2026 (a Thursday).
- **Figures:** Overpass Mono (no kerning) sets the clock about 4 px wider than the browser over eight characters, so
  the cells after it sit a few pixels right. Words are kerned like the browser's (FontAssets.AddKerning, task P2-02).
- **Opacity and scale:** the game's panels are 95% opaque and its interface 85% by default (owner, 2026-10-06); the
  parity check measures at 100% and with solid panels (`-hudopacity 100`, the mockup's `scale100`).
- **Type:** Overpass ships here in two weights, so the mockup's 500/600 draw regular and 700/800 bold.
