# Phase 0 · 0.2 Game design and flow

**Audience:** the project owner and coding agents. **Status:** draft for review (2026-09-25). Builds on [0.1](phase0-0.1-reference-inventory.md), [0.3](phase0-0.3-technical-architecture.md) and the [decision record](phase0-decisions.md). Questions for you are in §8.

## 1. What the game is

**Long term:** a ski-resort design game on real mountains. You pick a real place, and you plan, build and eventually run a resort on its true terrain.

**Iteration 1: just the mountain.** A desktop app that turns any real US mountain into a beautiful, explorable, stylized 3D diorama:
- built from USGS 1 m lidar
- with its real forest, ground cover and a snowy winter look
- playable fully offline once downloaded

It is the foundation every later iteration builds on, and it should already be satisfying on its own: *"find a mountain you love and see it like never before."*

**Who it's for:** ski and mountain enthusiasts, people who daydream about resorts on their home hills, and, later, tycoon and city-builder players.

## 2. The core experience (iteration 1)

```
Discover ──► Download ──► Explore ──► Collect ──► Return
(picker)     (progress,    (camera,     (library of  (offline, any
              quality)      time, layers) mountains)   time)
```

1. **Discover:** browse the map, search a place, frame a 2–5 km square, name it.
2. **Download:** a one-time wait with honest progress and a clear quality result.
3. **Explore:** fly and orbit the mountain, scrub the time of day and date to watch the light move, toggle layers to read the landscape.
4. **Collect:** every downloaded mountain stays in your library; build a collection of favourite peaks.
5. **Return:** open any mountain instantly, offline.

**The emotional payoff is the look.** Iteration 1 succeeds if opening a mountain feels like opening a beautiful model of a real place. That puts 0.5 (art direction) and the not-blocky ground-cover rule (T6) at the centre of Phase 1.

## 3. Player journey

**Launch**
- The main menu, over a slowly moving view of a mountain. That mountain comes from the bundled demo (G2) or the last one opened, so the menu works offline.
- Entries: **Continue** (last mountain), **My Mountains** (library), **New Mountain** (picker), **Settings**, **Credits**, **Quit**.
- **First launch** with an empty library: *Continue* is hidden, and the menu invites *New Mountain* or *Open the demo mountain*.

**New Mountain: the picker pop-up** (T19)
1. Search a place (Enter to search), or pan and zoom.
2. Set the size with the slider (2–5 km); click to centre the square.
3. See the expected quality score, data sources and download size for that square before committing.
4. Enter a name (a suggestion is prefilled). *Download* is enabled once a name is set.
5. Offline: the picker explains that it needs internet; everything else still works.

**Download**
- A progress view with stages: terrain, forest, ground cover, water, building. It shows time remaining and a Cancel button.
- It can run while you use other screens.
- An interrupted download appears in the library as **Incomplete, resume**; it never needs to start over (T4).

**Quality result** (T18)
- A card with the score and the one-line data summary.
- Buttons: **Open mountain**, **Back to library**.
- Below 90, a short plain-language note, for example: *"Part of this area has only 10 m data, so terrain there is smoother."*

**Explore the mountain**, with a minimal HUD:
- **Camera:** orbit / RTS style by default, with free-fly as a toggle; bounded to the surround ring; "reset view" and "north up".
- **Time and date scrubber:** sun position and shadows. Snow stays at a flat 12 in (T8).
- **Layers panel:** Snow, Ground cover, Forest, Cover map (T17).
- **Info:** mountain name, a compass, a scale bar, elevation under the cursor, and the quality badge.
- **Menu (Esc):** Settings, Library, Main menu, Quit.
- The camera, view time and layer choices are remembered per mountain.

**My Mountains (library)**
- Cards with thumbnail, name, location, size, quality score and disk use.
- Actions: Open, Rename, Delete (confirmed), Resume (if incomplete).
- Shows total disk use and the data folder location.

**Settings**
- **Graphics:** preset (Performance / Standard / High / Ultra), resolution, window mode.
- **Interface:** UI scale 50–150%, units (US / metric), theme (light / dark / system).
- **Controls:** rebindable keys; the old game's defaults W/A/S/D, Q/E, R/F, N.
- **Data:** the data folder and storage use.

**Credits:** data attributions generated from the downloaded mountains' manifests (USGS, Meta/WRI, ESA, OpenStreetMap contributors), plus software licences.

## 4. Keep, change or drop (against the archived game, 0.1)

Only the parts relevant to iteration 1 are listed.

| Archived feature | Decision | Why |
|---|---|---|
| Ski-trail-sign main menu over a live mountain | **Keep the idea**; restyle in 0.4/0.5 | Strong identity; works offline with the demo mountain |
| One setup workspace (location, boundary, prepare, name) | **Change** → the picker pop-up (T19) | Your design; simpler |
| Site box 2–10 km | **Change** → 2–5 km | 1 m data (T4) |
| Resort library with protected packages | **Keep**, as "My Mountains" | |
| Surround ring beyond the site | **Keep**, at 2 m | No cliff at the edge |
| 2D/3D toggle | **Drop** | Always 3D; a top-down camera preset covers the need |
| Satellite imagery layer (Esri) | **Drop** | Licensing (T10) |
| Analysis layers (hillshade, contours, slope, aspect) | **Later**, as 0.4 candidates | Iteration 1 keeps four layers |
| Render quality tiers | **Keep** the four names | Familiar; now also drive forest and shadows |
| Themes, UI scale 50–150%, units, rebindable keys | **Keep** | |
| Developer console | **Keep**, development builds only | |
| Graphics Lab | **Replace** with a benchmark scene | Performance budgets (T13) |
| Weather Lab | **Drop for now** | Weather is a future iteration |
| Everything in 0.1 §6 and §7 | **Future iterations** (§6 below) | Scope S2 |

## 5. Rules of the world (iteration 1)

- The mountain is **real and faithful:** terrain is never invented. Where data is coarser, the quality score says so.
- The **forest is real in extent and height** (canopy map), and procedural in individual tree placement.
- **Winter only:** a flat 12 in of snow and frozen lakes. The date moves the sun, not the season.
- **Nothing is built and nothing simulates.** The mountain is a place to look at and understand.

## 6. Future iterations (goals only; 0.6 plans them)

1. **Drawing on the mountain:** paint trails (with grading and tree clearing), place lifts, connector paths, and a network check. Drawn work is saved as small edit files over the downloaded mountain.
2. **More infrastructure:** roads, ponds, dams, snowmaking, buildings.
3. **Simulation:** time and weather, natural snow and grooming, frozen or open lakes, guests on lifts and trails.
4. **Tycoon:** an economy, costs, staff, reputation, goals and scenarios.

The rules in 0.1 §6 and §9 are the starting reference for each.

## 7. What "done" feels like for iteration 1

- Any US site can be downloaded; the quality score is honest.
- Opening a downloaded mountain takes ≤10 s and never touches the network.
- At every camera distance, the mountain reads as a beautiful model of the real place: no blocky cover, no seams, no cliff at the edge.
- The layers, time scrubber and camera feel immediate (T13 budgets).
- The minimum-spec PC runs it at 30 FPS or better.

## 8. Questions for you

Write your answer after each **Comment:**; "OK" accepts the recommendation.

**G1 · Product name.**
The repo is *ski-area-design-challenge*, the code is *MountainPlanner*, and the old game's title was *Ski Area Design Challenge*. **Recommendation:** keep *Mountain Planner* as the working title and decide the final name later.
**Comment:**

**G2 · Bundled demo mountain.**
**Recommendation:** ship the 2 km test site (T14) as a demo, so the first launch and the main menu work offline immediately.
**Comment:**

**G3 · Photo mode.**
Hide the UI, frame the shot, save a PNG. It fits the "admire" payoff and is cheap. **Recommendation:** include it in iteration 1.
**Comment:**

**G4 · Camera freedom.**
**Recommendation:** free-fly may go down to a few metres above the snow, with no walking mode, bounded to the surround ring.
**Comment:**

**G5 · Downloads in the background.**
**Recommendation:** you can browse the library or explore another mountain while a download runs, one download at a time.
**Comment:**

**G6 · Anything to add to the journey (§3) or to the done criteria (§7)?**
**Comment:**
