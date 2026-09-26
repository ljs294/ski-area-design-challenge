# Phase 0 · 0.4 UI/UX spec and style guide

**Audience:** the project owner and coding agents. **Status:** approved 2026-09-25 (decisions in §9). Builds on [0.2](phase0-0.2-game-design.md) (journey) and [0.3](phase0-0.3-technical-architecture.md) (T17 layers, T18 quality score, T19 picker). Questions are in §9.

**Stack:** Unity UI Toolkit: UXML for layout, USS for style, TSS themes, runtime data binding. Icons are SVG vector images; text uses signed-distance-field fonts. The resolution-independent reasons are in roadmap §11.

## 1. Principles

1. **The mountain is the hero.** UI stays at the edges, is compact and translucent, and can be hidden entirely (photo mode, `H`).
2. **One thing at a time.** Pop-ups are modal and focused (picker, download result, settings); the in-game HUD is minimal.
3. **Honest data.** Quality, sources and progress are always visible and in plain language.
4. **Instant response.** Every control answers within 100 ms (T13). Long work shows progress and is cancellable.
5. **Native feel.** Menus and settings behave the way players expect from PC games made in Unity (G8).
6. **Readable everywhere.** UI scale 50–150%, full keyboard navigation, light and dark themes, colour never the only signal.

## 2. Screen inventory (iteration 1)

| # | Screen | Kind | Reached from |
|---|---|---|---|
| S1 | Main menu | Full screen over the live Jackson Hole scene | Launch; in-game menu |
| S2 | My Mountains (library) | Full-screen panel | Main menu |
| S3 | Site picker | Modal pop-up with a mini-map | Main menu (New Mountain); library |
| S4 | Download progress | Docked card; can be minimised to a status pill | After S3 |
| S5 | Quality result | Modal card | Download finished |
| S6 | Mountain view (HUD) | In-game overlay | Open a mountain |
| S7 | In-game menu | Modal (Esc) | S6 |
| S8 | Settings | Full-screen tabbed panel | S1, S7 |
| S9 | Credits | Full-screen panel | S1 |
| S10 | Photo mode | HUD replaced by a thin photo bar | S6 (`P`) |
| S11 | Dialogs | Confirm / error / offline | Anywhere |

## 3. Flow

```
                 ┌──────────── Continue ────────────┐
Launch ─► S1 Main menu ─► New Mountain ─► S3 Picker ─► S4 Download ─► S5 Quality ─► S6 Mountain
             │   │                              ▲            │ (minimise: keep using the game, G5)
             │   └─ My Mountains ─► S2 Library ─┘ Resume ────┘            │
             │                          └──────── Open ───────────────────┤
             ├─ Settings ─► S8                                  Esc ─► S7 ─┴─► S8 / S2 / S1 / Quit
             ├─ Credits ─► S9                                    P ─► S10 Photo mode
             └─ Quit
```

**First launch** (empty library): S1 hides *Continue* and highlights **Open the demo: Jackson Hole** and **New Mountain**.

**Offline:** S3 shows an offline panel with a Retry button. Everything else works; downloads pause and resume automatically when the connection returns.

## 4. Screens

### S1 Main menu

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  SKI AREA DESIGN CHALLENGE                                                    │
│                                                                               │
│  ● Continue            Crystal Mountain · last opened today                   │
│  ■ My Mountains        (live, slowly orbiting 3D view of Jackson Hole         │
│  ◆ New Mountain         behind a soft left-side scrim)                        │
│  ◆◆ Settings                                                                  │
│     Credits · Quit                                     ⬇ Downloading 42% ▸   │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Keeps the archive's **ski-trail-sign** identity (● ■ ◆ ◆◆ chips) with a left scrim over the live scene.
- The status pill (bottom right) shows a background download.

### S2 My Mountains

- A grid of cards: thumbnail, name, location, size (km), **quality badge** (for example *97*), disk use, last opened.
- **Actions:** Open (double-click or Enter), Rename, Delete (confirmation shows the disk space freed), Resume (incomplete downloads).
- **Header:** *New Mountain*; total disk use; data folder (open in Explorer).
- **Sort** by last opened, name or quality.

### S3 Site picker (T19)

```
┌─ New Mountain ──────────────────────────────────────────────────────── ✕ ─┐
│ [ Search a place…                         ] [Search]                      │
│ ┌───────────────────────────────────────────────────────────────────────┐ │
│ │  USGS map tiles (imagery / topo toggle)                                │ │
│ │  data-quality overlay: ■ 1 m S1M  ■ 1 m lidar  ■ ~3 m  ■ ~10 m         │ │
│ │                ┌───────────┐                                          │ │
│ │                │  4.0 km   │  ← click to centre; drag the map to pan   │ │
│ │                └───────────┘                                          │ │
│ └───────────────────────────────────────────────────────────────────────┘ │
│ Size  2 km ├──────────●────┤ 5 km   4.0 km                                │
│ Name  [ Crystal Mountain North      ]  (suggested)                       │
│ Expected quality 96 · 1 m lidar 100% · about 420 MB · ☐ Satellite imagery │
│                                              [ Cancel ]  [ Download ]     │
└───────────────────────────────────────────────────────────────────────────┘
```

- **Search** runs on Enter; results show as a short list; picking one flies the map there.
- **Slider:** 2–5 km in 0.1 km steps. The keyboard's arrow keys nudge it; Shift+arrows move 1 km.
- **Click** centres the square; arrow keys nudge it when the map has focus.
- **Download** is enabled once a name is set and the square is placed.
- **Estimate line:** expected score, source mix, size, and the optional **satellite imagery** checkbox (G7), which adds its size to the estimate.

### S4 Download progress

- **Stages with ticks:** Terrain · Forest · Ground cover · Water · (Imagery) · Building.
- Overall bar, time remaining, **Minimise** and **Cancel** (confirmation: keep the partial download for resuming, or discard it).
- Minimised, it becomes a status pill on S1, S2 and S6; clicking restores it.

### S5 Quality result (T18)

```
┌─ Crystal Mountain North is ready ────────────────────┐
│   97 / 100   Excellent terrain detail                 │
│   96% USGS S1M 1 m lidar · 4% 3DEP 10 m               │
│   Forest: Meta/WRI canopy (2019) · Cover: ESA 2021    │
│   [ Open mountain ]   [ Back to library ]             │
└───────────────────────────────────────────────────────┘
```

**Bands:**
- 90–100 Excellent
- 75–89 Good
- 50–74 Fair: *"parts of this area use coarser data"*
- below 50 Limited

The number and the words always appear together.

### S6 Mountain view (HUD)

```
┌─ Crystal Mountain North · 97 ───────────────────────────────── ☰ ─┐
│                                                        ┌ Layers ─┐ │
│                                                        │☑ Snow    │ │
│                  (the mountain)                         │☑ Ground  │ │
│                                                        │☑ Forest  │ │
│                                                        │☐ Cover   │ │
│                                                        │☐ Imagery │ │
│  N ▲   0 ── 500 m     Elev 2,104 m                     └──────────┘ │
│ ┌ Time ─────────────────────────────────────────────────────────┐  │
│ │ ☀ 10:30  ├────●──────────────┤  Jan 15  ├──●──────────┤  ⟲    │  │
│ └───────────────────────────────────────────────────────────────┘  │
└────────────────────────────────────────────────────────────────────┘
```

- **Top bar:** name, quality badge, menu (☰ = Esc).
- **Layers panel** (right, collapsible): one row per layer (T17); *Imagery* only if it was downloaded. Rows show a key hint on hover.
- **Bottom-left:** compass (click = north up), scale bar, elevation under the cursor.
- **Bottom time bar:** time-of-day and date scrubbers, and ⟲ (reset to 10:30, Jan 15). Dragging updates the sun live.
- **Camera-mode toggle:** Orbit ↔ Free-fly (`C`), shown as a small chip.
- **Hide UI:** `H`. **Photo mode:** `P`.

### S7 In-game menu

Resume, Settings, My Mountains, Main menu, Quit to desktop.

### S8 Settings

**Tabs:** Graphics, Display, Interface, Controls, Audio (placeholder), Data.

**Graphics (G8): native Unity style**

| Setting | Options |
|---|---|
| Quality preset | Low, Medium, High, Ultra, Custom (one Unity quality level each; changing any option below switches to Custom) |
| Render scale | 50–200% |
| Anti-aliasing | Off, FXAA, SMAA, MSAA 2×/4× |
| Shadow quality | Off, Low, Medium, High |
| Shadow distance | slider |
| Texture quality | Quarter, Half, Full |
| Terrain detail | Low, Medium, High (terrain pixel error) |
| Forest density | 25–100% |
| Forest draw distance | slider |
| Ambient occlusion | Off, On |
| Frame-rate cap | 30, 60, 120, 144, Unlimited |

- An **Apply / Revert** bar appears when anything changes.
- Changes preview live on the menu background.
- **Low** = the old Performance tier. **Medium** = Standard, the minimum-spec target (T13).

**Display:** monitor, resolution, window mode (Fullscreen / Borderless / Windowed), V-Sync. A 15-second "Keep these settings?" countdown after a mode change.

**Interface:** UI scale 50–150% (5% steps), theme (Light / Dark / System), units (US / Metric), show key hints.

**Controls:** rebind list (§6), mouse sensitivity, invert Y, edge scroll on or off.

**Data:** data folder (Change…), disk use per mountain, clear terrain caches (they rebuild).

### S9 Credits

Team and software licences, plus **data attributions generated from the installed mountains' manifests** (USGS, Meta/WRI, ESA WorldCover, OpenStreetMap contributors, Nominatim).

### S10 Photo mode (G3)

- The HUD hides. A thin bar shows: *Capture* (Space or F12), field of view, depth-of-field on or off, time-of-day and date scrubbers, a grid overlay, and *Exit* (Esc).
- Captures save as PNG to `Pictures/Ski Area Design Challenge/`, at the current resolution or 2× supersampled. A brief toast confirms each one.

### S11 Dialogs

- Destructive actions confirm and name the consequence (*"Delete Crystal Mountain North? Frees 612 MB."*).
- Errors say what happened and what to do; they never show a raw exception.

## 5. Components

Built as UI Toolkit custom controls and shared USS:

- **Chip button:** ski-sign menu items.
- **Card:** library item; quality result.
- **Quality badge:** number plus band colour plus word.
- **Slider with value field.**
- **Segmented control:** camera mode, theme.
- **Toggle row:** layers.
- **Modal window:** title, close button, focus trap, restores focus on close.
- **Status pill:** background download.
- **Toast:** photo saved, download complete.
- **Tabs:** settings, with keyboard navigation.
- **Map view:** the picker mini-map, a custom control; see 0.3 §6.1.
- **Time scrubber:** a dual slider for time and date.

## 6. Default controls

| Action | Key / mouse |
|---|---|
| Pan | W A S D, or drag with the right mouse button |
| Rotate | Q / E, or drag with the middle mouse button |
| Tilt | R / F |
| Zoom | Mouse wheel |
| North up | N |
| Camera mode (orbit ↔ free-fly) | C |
| Free-fly move / up / down | W A S D, Space, Ctrl; Shift = fast |
| Hide UI | H |
| Photo mode | P |
| Toggle layers | 1 Snow · 2 Ground cover · 3 Forest · 4 Cover map · 5 Imagery |
| Menu | Esc |

**Note:** the archive used R/F for tilt, so **free-fly toggle is C, not F**. Escape is never rebindable. The archive's 1/2 dashboard keys become layer toggles; iteration 1 has no dashboards.

## 7. Style guide

**Tokens** (ported from the archive's `ui.css` into `Theme-Light.tss` / `Theme-Dark.tss`):

| Token | Light | Dark |
|---|---|---|
| `--app-bg` | `#f3f5f2` | `#111b22` |
| `--surface` | `#ffffff` | `#1b2933` |
| `--panel-hover` | `#edf2f3` | `#263943` |
| `--text` | `#1e2a32` | `#eaf1f4` |
| `--text-muted` | `#526570` | `#b0c1ca` |
| `--border` | `#d6dfe1` | `#3b515e` |
| `--border-strong` | `#7c8e98` | `#78929f` |
| `--accent` | `#155ab6` | `#80bef2` |
| `--accent-contrast` | `#ffffff` | `#102536` |
| `--danger` | `#b33338` | `#ff9a9f` |
| `--success` | `#276748` | `#8cd5ac` |
| `--focus-ring` | `#155ab6` | `#80bef2` |

- **Quality-band colours:** Excellent `--success`, Good `--accent`, Fair amber `#b7791f` / `#f0c36d`, Limited `--danger`.
- **Shape:** radii 4 / 6 / 12 px. Base text 14 px, with headings at 18 / 24 / 32. HUD panels 85% opaque with a background blur where supported.
- **Font:** **Inter** (SIL Open Font License, redistributable), with tabular numbers for readouts. Segoe UI can't be shipped (roadmap §11).
- **Icons:** outline style, 1.5 px stroke at 24 px, SVG.
- **Motion:** 120–200 ms ease-out for panels; no motion for data changes. Respect a "reduce motion" setting.
- **Accessibility:**
  - Contrast of at least 4.5:1 for text.
  - Every control reachable by keyboard, with a visible focus ring.
  - Layer and quality colours always paired with text.

## 8. Acceptance for Phase 1

- Every screen in §2 exists in UXML with both themes, and works at 1280×720 through 3840×2160 and at 50–150% UI scale.
- Keyboard-only walkthrough of the whole §3 flow.
- Every control responds in ≤100 ms. HUD updates (elevation readout, time scrubber) allocate nothing per frame.
- Visual review screenshots of S1, S3, S5, S6 and S8 in both themes at 1080p and 1440p.

## 9. Decisions (owner approval, 2026-09-25)

| ID | Decision |
|---|---|
| U1 | The layouts for the picker (S3), quality card (S5) and mountain HUD (S6) are approved |
| U2 | Controls as in §6: free-fly on **C**, R/F tilt, 1–5 layer toggles |
| U3 | The graphics option list in S8 is approved |
| U4 | Font: **Inter** |
| U5 | The time scrubber resets to **10:30 on January 15** |
