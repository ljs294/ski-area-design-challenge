# Game UI direction

**Audience:** the project owner and coding agents. **Status:** the layout in [Accepted for now](#accepted-for-now-owner-2026-10-01) is accepted (owner, 2026-10-01); the rest is the 2026-09-29 draft, kept where it still applies. **Date:** 2026-10-01. **Applies to:** the whole game: iteration 1's mountain HUD now ([0.4 S6](phase0-0.4-ui-ux.md)), the drawing tools in Phase 3 and the simulation in Phase 4 ([0.6](phase0-0.6-milestones.md)). **Inputs:** your answers of 2026-09-29, the archived game's layout ([0.1 §2](phase0-0.1-reference-inventory.md#2-player-journey-and-screens)), and a study of Cities: Skylines II and Subway Builder (sources at the end).

**In one line:** a mountain you want to look at, with solid instrument panels in warm graphite or sign white; a full-width status bar at the bottom; a Toolbox whose tools each open one floating build panel; and Subway Builder's rhythm of **plan, build, operate, then learn from what the guests do**.

## Accepted for now (owner, 2026-10-01)

The owner's layout sketch (`Ski-Area-Design-Challenge UI.pdf`), built out over several rounds into a playable mockup, is the HUD to build from: [`prototypes/ui-layout.html`](prototypes/ui-layout.html), also published as a private page. Where the rest of this doc differs, this section wins.

| # | Decision |
|---|---|
| UI-6 | **Look: Trailhead.** Solid instrument panels with hairline rules: warm graphite in dark mode, sign white in light mode (not cream). Overpass for words and Overpass Mono for figures; one set of solid, sign-style symbols. No frosted glass |
| UI-7 | **Status bar** across the full width of the bottom, 52 px, docked flush to the edge by default or floating (a setting). Left: Toolbox, Analysis, the resort's name with its lift status, pause, the day, the time with the weather under it, speed. Right: the bank balance with today's change under it, lifts, skiers, the elevation under the pointer, save |
| UI-8 | **Toolbox** replaces the centred build bar: a tray above the status bar with tabs (Lifts, Trails, Snowmaking, Infrastructure) and symbol tiles. Each tool opens one floating build panel with live numbers, a cost breakdown, and Break ground or Save plan |
| UI-9 | **Analysis** opens one panel with tabs: Overview, Lifts, Trails, Snowmaking, Amenities, Finances |
| UI-10 | **Top right:** sketch, map layers and the menu. There are no camera buttons: the wheel and + − zoom, and Home resets the view |
| UI-11 | **Menu:** quick switches for theme (dark, light, or auto by the sun), status bar and units, then a Settings window: interface, units and time, gameplay, graphics, audio and controls |

Details that come with it:
- **Lift status** is a small line under the resort's name: *Closed* (no colour) opens 8:30 AM, *Open* (green) until 4:30 PM, *Last chair* (amber). Closed is calm because it's every night. The mockup keeps two alternatives (an hours track, a word after the name) for comparison.
- **Weather line:** the weather symbol, the temperature, and the snow forecast for the next 24 hours.
- **Save** turns white when there's something to save, and does nothing else (no dot).
- **Units** switch every figure together (m or ft, °C or °F, cm or in, m/s or ft/min, m³ or yd³); the clock is 12- or 24-hour.
- **Plans are surveyor's orange:** dashed lines with a dimension label on the mountain, and outlined orange lift plates. Built things are solid.
- **The palette** for both themes is the set of CSS variables at the top of the mockup's HUD styles.
- **Keys:** the full map, including Tab for Analysis, Shift+1–5 for layers and letters going to the tools while the Toolbox tray is open, is in [controls-key-map.md](controls-key-map.md). The HUD drives the camera through `ViewCamera.LettersToTools` and toggles layers through `ToggleLayer`.
- **Overpass draws its middle dot (·) off-centre.** The mockup sends that one character to a system font; the game needs the same fallback.

## 1. Decisions (owner, 2026-09-29)

| # | Decision |
|---|---|
| UI-1 | **Look:** Subway Builder's minimal frosted-glass panels in light and dark, with Cities: Skylines II's colourful category icons |
| UI-2 | **Build tools** sit in a bar at the bottom centre (Cities: Skylines II). Choosing a tool opens a **floating window** with its in-depth options (Subway Builder) |
| UI-3 | **Game flow** follows Subway Builder: blueprints first, then build and pay, then operate, then analyse and improve (§3) |
| UI-4 | **Windows float**, as in the archived game and Subway Builder: draggable, keyboard-movable, pinnable |
| UI-5 | **Not too heavy, and really nice to look at:** the weight rules in §5 keep it light |

UI-1 and UI-2 are superseded by UI-6 and UI-8 (2026-10-01); UI-3 to UI-5 still apply.

**This is a new game, not a port.** The archived MapLibre edition is a starting reference (its layout was itself modelled on Subway Builder), never a constraint: where Cities: Skylines II or Subway Builder does something better, we follow them. Two side-by-side mockups, one leaning on each game, are in [`prototypes/ui-style-mockups.html`](prototypes/ui-style-mockups.html).

You leaned towards the Subway Builder one ("more elegant and more simulator based"). Our first identity grown from it, [`Alpenglow`](prototypes/ui-alpenglow.html), read as a generic AI web dashboard.

Three art directions drawn from the resort's own world are in [`prototypes/ui-art-directions.html`](prototypes/ui-art-directions.html), for you to choose from:
- an ops console;
- Swiss signage;
- trail-map print.

They sit over a capture of our Unity mountain, with the status bar back at the bottom.

## 2. The screen

This first sketch is superseded by the accepted layout. Its info views, warnings and detail windows haven't been redesigned yet, so they still stand.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ (i) Slope angle ▾                                     ⚠ 2    ◧   ⚙   ☰      │
│ ┌ Lifts ───────────── ⇱ ✕ ┐                                                 │
│ │ Plan │ Cost              │                  ● top · $3.2M                 │
│ │ [Surface][Quad][Six][Gon]│                 ╱                              │
│ │ Length        1,420 m    │     1,420 m · +410 m · 16° avg                 │
│ │ Vertical        410 m    │               ╱                                │
│ │ Capacity     2,400 / h   │              ● bottom          ┌ Slope angle ┐ │
│ │ Blueprints: 2 · $4.1M    │                                │ ● < 14°      │ │
│ │ [Build blueprints]       │                                │ ■ 14–22°     │ │
│ └──────────────────────────┘                                │ ◆ 22–30°     │ │
│                                                             │ ◆◆ > 30°     │ │
│                                                             └──────────────┘ │
│        [Lifts][Trails][Snowmaking][Grooming][Lodges][Roads][Terrain][✖]  N + − │
│ ▶ ›› │ Jun 3 · Summer planning │ 18 °C │ 0 skiers │ $12.4M  −$4.1M │   Rating –   │
└─────────────────────────────────────────────────────────────────────────────┘
```

| Region | What it holds | From |
|---|---|---|
| **Status strip** (bottom, full width, slim) | Play and speed; date, time and season phase (with *Start the season* in summer); weather and snow (new, base); skiers today; money and today's change (green or red); resort rating | Subway Builder and the archived bar; Cities: Skylines II's weather and happiness |
| **Build bar** (bottom centre) | One colourful icon per category: Lifts, Trails, Snowmaking, Grooming, Lodges, Roads and parking, Terrain, Bulldoze | Cities: Skylines II |
| **Tool window** (floating, top-left by default) | The chosen tool's options, asset cards, live numbers of the thing being drawn, and the blueprint list with Build blueprints | Subway Builder's Construction window, with Cities: Skylines II's asset cards inside |
| **Detail windows** (floating, pinnable) | A selected lift, run, lodge or pond: key numbers first, depth behind tabs | Subway Builder's detail panels; the archived inspectors |
| **Info views** (top-left button) | Map overlays with a legend card bottom-right; the related view opens by itself with its tool | Cities: Skylines II's info views; Subway Builder's legends |
| **Top-right cluster** | Warnings chip, 2D/3D, settings, menu | All three |
| **Camera** (bottom-right) | Compass (north up), zoom | Subway Builder and the archived game |
| **In the world** | Live readouts while drawing (length, rise, pitch, difficulty, cost); data drawn on the mountain (lift-line bubbles, traffic, snow) | Cities: Skylines II's construction labels; Subway Builder's bubbles |

## 3. Game flow: Subway Builder's rhythm on a mountain

| Subway Builder | Here |
|---|---|
| Draw **blueprint** tracks and stations; drag nodes; the cost updates live | Draw blueprint **lifts and runs**; drag terminals and trail points; cost, length, vertical, pitch and difficulty update live |
| **Build blueprints:** pay from the budget; bonds when short | **Build blueprints:** pay from the budget; loans when short |
| **Routes:** name, colour, stops, frequency | **Operations:** lift speed and carriers (capacity), hours, open or closed; run grooming, open or closed; ticket price |
| Commuters choose by travel time, transfers and cost | Skiers choose by ability, lift lines, snow and crowding |
| Delays and overcrowding | Long lift lines, crowded runs, icy or thin snow |
| Demand bubbles, route and station details | Info views (lift lines as sized bubbles with minutes, skier traffic, snow depth), lift and run windows, guests' thoughts |
| Normal mode with a budget; sandbox mode | The same two modes |

The seasons give the loop its beat, as in the archived game: **summer is for planning and building, winter is for operating.**

```
 Summer: plan blueprints ─► build blueprints ─► set operations
                                                     │
 Winter: open the season ─► skiers arrive ─► watch lines, snow, money
                                                     │
 Next summer: fix what the winter showed ◄───────────┘
```

## 4. Core flows

**4.1 Plan a lift.** Lifts in the build bar opens the Lifts window: cards for surface lift, fixed-grip quad, detachable six and gondola, each with capacity and cost per metre. Click the bottom terminal, then the top; a blueprint appears with live length, rise, average pitch, ride time and cost. Drag either terminal to adjust. The blueprint joins the window's list; nothing is paid yet.

**4.2 Paint a run.** Trails opens the Trails window (width, grading on or off, tree clearing), and the **Slope angle** info view opens by itself. Click points down the fall line; the live readout shows length, vertical and the steepest pitch, and the difficulty symbol (● ■ ◆ ◆◆) changes as you go. Enter or a double-click finishes it.

**4.3 Build blueprints.** The tool window's Cost tab lists every blueprint with its cost, the total and the money left. **Build blueprints** pays and builds; **Clear blueprints** discards them. Short of money: a loan, in normal mode.

```
┌ Lifts ─────────────────────── ⇱ ✕ ┐
│ Plan │ Cost                        │
│ Lift 3 · quad · 1,420 m     $3.2M  │
│ Run 7 · ■ · 1,100 m         $0.9M  │
│ ───────────────────────────────    │
│ Total                       $4.1M  │
│ Money after                 $8.3M  │
│ [Build blueprints] [Clear]         │
└────────────────────────────────────┘
```

**4.4 Look at something.** Click a lift: its window opens with Overview (type, capacity, riders today, average wait), Operations (speed and carriers, hours, open or closed) and Profile (towers over the terrain). A run shows difficulty, its elevation profile, conditions (snow, groomed) and traffic. Pin a window to keep it while you look at another.

**4.5 Read the mountain.** The info views button opens a grid: Slope angle, Snow depth, Grooming, Lift lines, Skier traffic, Sun and shade, Wind, Tree cover. The world dims slightly so the data stands out, and the legend card explains it.

**Map layers and info layers (owner, 2026-10-02; built in task 12b).** The Layers panel has two groups:
- **Map layers** show or hide physical things on the map: Snow and Trees now; lifts and the rest join as they're built. Any combination.
- **Info layers** only display information. **Slope angle** (trail-difficulty bands: under 14°, 14–22°, 22–30°, and over 30° hatched as double black), **Slope exposure** (eight compass points from true north, cool to the north and warm to the south), **Snow depth** (from the natural snowpack until the snow simulation) and **Snow conditions** (reserved for the snow simulation) take turns, one at a time. **Contours** (every 10 m, heavier every 50 m) combine with any of them.

The info layer that's on shows a legend card under the panel with its swatches, words and figures. Keys are Shift+1 and Shift+3 for the map layers and Shift+6–0 for the info layers ([controls-key-map.md](controls-key-map.md)). The info views grid above becomes this group as more views arrive.

**4.6 Warnings.** The chip counts them (a long line at Lift 2, Run 7 has no lift access). Each has *Show me*, which flies the camera there and opens the window.

## 5. Look and weight

- **Panels:** solid (95–97% opaque) with hairline borders, 6 px corners and soft shadows at most, in dark (warm graphite) and light (sign white) themes. Frosted glass, the earlier plan, is dropped.
- **Symbols:** one solid, sign-style set on a 24 px grid, drawn in the text colour. Tool variants share a family symbol with a badge: + for new, a pencil for edit, × for remove.
- **Type:** Overpass for words and Overpass Mono for figures, 12.5–15 px, with tabular numbers.
- **Weight rules**, so it stays light:
  1. One tool window at a time; choosing another tool replaces it.
  2. At most three detail windows; the oldest unpinned one closes first.
  3. Key numbers first; charts and technical depth behind a tab.
  4. Nothing on screen all the time beyond the status bar and the top-right buttons.
  5. Data on the mountain before data in windows.
  6. Panels move in 120–200 ms with no bounce; only the money change animates.
- **Colour meanings:**
  - difficulty: green, blue and black; double black is black with two diamonds, as on trail signs;
  - money: green and red;
  - warnings: amber;
  - plans: surveyor's orange, used for nothing else;
  - lift-line waits: green, orange and red.

  Lift colours stay clear of all of these.
- **States in the world:** solid means built, dashed orange means planned, and a yellow glow means selected. The blueprint being dragged shows its handles and one readout card beside the pointer.
- **Buttons:** one filled button per window, the commit (Build). Toggles and tabs show selection with a tint or a white segment, never with the commit's fill. Each blueprint has its own remove (×).

## 6. What makes it fun

- **Every drag answers at once:** numbers and cost move with the pointer, so you design by feel.
- **The mountain shows the results:** lift-line bubbles grow, runs glow with traffic, snow thins where it's thin.
- **Small moments:** the first chair of the season, the first black run, opening day; a short toast and a sound, never a modal.
- **Guests talk:** the archived game's vibe check becomes short thought bubbles on the map in the Guests info view.

## 7. Iteration 1's HUD, aligned

The style-tile HUD mock ([S6](phase0-0.4-ui-ux.md)) moves into this frame when tasks 11 and 12 finish it:
- the time bar becomes the status strip (date, time, the lighting presets until the scrubbers arrive);
- the Layers panel holds the map layers and info layers with a legend card (task 12b; see 4.5);
- there are no camera buttons (the wheel and + − zoom), and the elevation readout sits in the status bar;
- the Toolbox appears in Phase 3; until then its button stays hidden.

## 8. Scope

This doc covers the interface only. Game mechanics (construction time, milestones, pricing, sound) belong to the Phase 3 and Phase 4 plans; the UI leaves room for them without deciding them.

## 9. Building it in Unity

- **UI Toolkit for all screen UI** (roadmap §11; the style-tile HUD already uses it): layout in UXML, style in USS with the accepted palette (the mockup's CSS variables) in `Theme-Light.tss` / `Theme-Dark.tss`, behaviour in small C# components in the UI assembly. For comparison, Cities: Skylines II draws its UI with HTML/CSS through Coherent Gameface (a commercial middleware the roadmap considered), and Subway Builder is a web app; UI Toolkit gives us the same CSS-like authoring natively and for free.
- **The prototype is the design reference.** USS is a subset of CSS built on flexbox, with no CSS grid, so the prototype's layouts carry over once its few grid blocks become flex rows.
- **Snapshots out, commands in** (AGENTS.md): the simulation publishes snapshots; view models (plain C# with `[CreateProperty]`, change tracking through `INotifyBindablePropertyChanged` or `IDataSourceViewHashProvider`) refresh from them at most ten times a second; views bind to them with runtime data binding. Views raise intents such as *build blueprints*, which the app turns into commands; the UI never changes game state directly.
- **Our own control library** (`[UxmlElement]` custom controls): `GameWindow` (drag, keyboard move, pin, clamp, the one-tool rule), build bar and tool buttons, asset cards, tabs, status strip, info-view menu and legend, toasts, warnings, the drawing readout. Key controls don't lean on the default theme's visuals (the style tile's checkbox collapsed to nothing).
- **Labels in the world** (lift names, the drawing readout, lift-line bubbles): screen-space elements moved each frame with the `DynamicTransform` usage hint, or world-space panels (available since Unity 6.2) where a sign should sit in 3D.
- **Performance** (Unity's UI Toolkit guide): hide with `display: none` rather than zero opacity; animate transforms, not layout; virtualized ListViews for long lists; sprite and dynamic atlases to keep batches together; `[GeneratePropertyBag]` and `[CreateProperty]` so bindings don't use reflection; profile with the UI Toolkit Debugger, Profiler and Frame Debugger. Budget: the whole HUD within 0.3 ms GPU with no per-frame allocations (the mock measures 0.02-0.2 ms).
- **No frosted glass**, so no blur pass is needed. If glass ever returns: Unity 6.6 adds a `backdrop-filter` USS property; on the pinned 6.3 LTS it needs our own URP blur pass and a UI Shader Graph material.
- **Symbols:** SVGs imported as vector images and tinted by the theme.
- **Fonts:** Overpass and Overpass Mono (SIL Open Font License) as font assets, with a fallback face for the middle dot (U+00B7).
- **Tests:** view models in EditMode; PlayMode tests open panels and send events; review screenshots through the player flags (`-theme`, `-light`, `-nohud`, `-withhud`).

## 10. Next

The accepted mockup, [`prototypes/ui-layout.html`](prototypes/ui-layout.html), is the reference for building the HUD in UI Toolkit, and this doc is the brief for the Phase 3 UI plan. Its numbers, weather and elevation are placeholders.

Still open:
- whether the game renders the mockup's bluebird sky (cumulus in `Sky.shader`, a deeper noon preset), or the sky stays mockup-only;
- the info views, warnings and detail windows, which keep the earlier draft until they're redesigned.

The earlier build-flow prototype, [`prototypes/game-ui-prototype.html`](prototypes/game-ui-prototype.html), stays for reference.

## Sources

Patterns only; no art or code is copied.
- Subway Builder: [official site and screenshots](https://www.subwaybuilder.com/), [Steam page](https://store.steampowered.com/app/4039140/Subway_Builder/), [Wikipedia](https://en.wikipedia.org/wiki/Subway_Builder), [The Punished Backlog review](https://punishedbacklog.com/subway-builder-review/).
- Cities: Skylines II: [Steam page and screenshots](https://store.steampowered.com/app/949230/Cities_Skylines_II/), [Info views (wiki)](https://cs2.paradoxwikis.com/Info_views), [UI modding (wiki)](https://cs2.paradoxwikis.com/UI_Modding).
- Unity: [backdrop filters (6.6)](https://docs.unity3d.com/6000.6/Documentation/Manual/ui-systems/backdrop-filter.html), [UI Shader Graph (6.3)](https://docs.unity3d.com/6000.3/Documentation/Manual/ui-systems/get-started-with-ui-shader-graph.html), [UI Toolkit performance](https://docs.unity3d.com/6000.4/Documentation/Manual/best-practice-guides/ui-toolkit-for-advanced-unity-developers/optimizing-performance.html), [data binding](https://docs.unity3d.com/6000.5/Documentation/Manual/best-practice-guides/ui-toolkit-for-advanced-unity-developers/data-binding.html), [world space UI](https://docs.unity3d.com/6000.3/Documentation/Manual/ui-systems/world-space-ui.html).
