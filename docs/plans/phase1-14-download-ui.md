# Phase 1 · task 14: download UI, quality card, menu and library

**Status:** plan, for owner review. **Date:** 2026-10-02. **Branch:** `feature/p1-14-download-ui`.
**Spec:** [0.7 §3 row 14](phase0-0.7-phase1-plan.md); screens S1, S2, S4 and S5 in [0.4](phase0-0.4-ui-ux.md#4-screens); look from [game-ui-direction.md](game-ui-direction.md) (Trailhead, UI-6); keys from [controls-key-map.md](controls-key-map.md).

**Acceptance:** end to end, the player picks a site, downloads a new site that includes a fallback area, sees the quality card, then opens it offline.

## 1. What exists today

| Piece | Where | What it gives this task |
|---|---|---|
| `AcquisitionPipeline.RunAsync(SiteRequest, folder, IProgress<AcquisitionProgress>, ct)` | Acquisition | The whole download. It's resumable: every remote read goes through `DiskCache`, so a rerun continues where a killed run stopped |
| `AcquisitionProgress` | Acquisition/Progress.cs | Stage name, index and count, step, overall 0–1, bytes, speed, time left, and a ready-made detail line, at least 4 times a second |
| The 8 stages today | Pipeline constants | Terrain · Terrain surroundings · Forest · Ground cover · Tree species · Water and roads · Building · Preparing terrain |
| `PackageManifest.Quality` / `.Flora` | Persistence | Score, one-liner and source shares (T18). The quality card only reads them |
| `ResortLibrary.Scan / Add / Remove`, `PackageValidator` | Persistence/Library.cs | The S2 list (name, place, size, both scores, disk use, cache ready), moving a finished build in, and deleting with the space freed |
| `ResortOpener.OpenAsync` | App | Opens a package from disk with no network |
| `MountainViewer` scene | App (task 12 owns the .cs) | Opens one package chosen at start from `-package`, `-site` or the demo. There's no menu or library yet |
| `tools/acquire` CLI | tools | The same pipeline, with a progress line per stage ("Stage 3 of 8: Forest") |

Missing: any title, library, download or quality screen; a way to open a chosen mountain in-process; and a record of unfinished downloads that survives quitting.

## 2. Screen flow

```
Launch ─► S1 Title (signpost over the live demo mountain)
            ├─ Continue ────────────────────────────────► open last-opened mountain ─► S6
            ├─ New Resort ─► S3 Picker (task 13) ─► S4 Download ─► S5 Quality card ─┬─ Open mountain ─► S6
            │                     ▲ Cancel ─► back                                   └─ Back to library ─► S2
            ├─ My Resorts ─► S2 Library ─┬─ Open (Enter / double-click) ────────────► S6
            │                            ├─ Resume (unfinished download) ─► S4
            │                            ├─ New Resort ─► S3
            │                            └─ Delete (confirm, names the space freed)
            └─ Quit
S4 Minimise ─► a pill on S1 / S2 (and later S6); click it to restore. It finishes into S5 wherever you are.
Esc backs out one step (S5 → S2, S2 → S1, S4 → minimise, S1 → nothing).
```

- **One owner.** `AppFlow` (App, new) is the only thing that changes screens. The screens raise intents (`OpenRequested(entry)`, `NewResort`, and so on), and AppFlow decides.
- **Opening a mountain** sets `MountainViewer.RequestedPackage`, then reloads the MountainViewer scene; S1 uses `TitleMode` and `EnterGame()`. Task 12 adds these three hooks in its own commit (agreed through the coordinator), and I cherry-pick it. Opening touches only disk.
- **Downloads keep running across screens.** `DownloadService` (App, `DontDestroyOnLoad`) runs the pipeline on a worker thread and hands each snapshot to the main thread, so minimise just hides the card.
- **Resume across a quit or a crash.** On start, a download writes `<data>/Downloads/<slug>.json` (the request, its build folder and when it started); on success it deletes the file. S1 and S2 show anything left there as *Paused, resume*. Rerunning the same request reuses the disk cache, so it continues rather than starting over.
- **Scratch library.** `-data <folder>` points the whole flow at another data root. Demos and tests use it, so the shared `%LOCALAPPDATA%` library stays untouched.

## 3. Wireframes (functional, Trailhead)

**S1 Title.** It keeps the trail-sign identity. The signs sit on cedar posts at the left over the live, slowly orbiting demo mountain, with a soft scrim behind them. The three signs are plain shapes (●, ■, ◆) in sign colours, with bold Overpass text.
```
┌───────────────────────────────────────────────────────────────────────┐
│  SKI AREA DESIGN CHALLENGE                                            │
│   ║┌───────────────────────┐                                          │
│   ║│ ●  Continue           │  Jackson Hole · opened today   (green)   │
│   ║└───────────────────────┘                                          │
│   ║┌───────────────────────┐                                          │
│   ║│ ■  New Resort         │                                (blue)    │
│   ║└───────────────────────┘                                          │
│   ║┌───────────────────────┐                                          │
│   ║│ ◆  My Resorts         │                                (black)   │
│   ║└───────────────────────┘                                          │
│   ║   Quit                                                            │
│   ╨                                       [⬇ Crystal Mtn  42%  ▸]    │
└───────────────────────────────────────────────────────────────────────┘
```
On the first launch, with an empty library, Continue becomes *Open the demo: Jackson Hole*. Settings and Credits wait for their own tasks.

**S2 My Resorts.** A full-screen graphite panel with one row per mountain. It's a list rather than cards: there are no thumbnails yet, and rows read better as an instrument.
```
┌ My Resorts ─────────────────────────── 3 mountains · 2.1 GB on disk ── [ New Resort ]  ✕ ┐
│ Sort: Last opened ▾                                                                       │
│ ──────────────────────────────────────────────────────────────────────────────────────── │
│ Jackson Hole        43.588 N 110.828 W   5.0 km   Terrain  97  Flora 83   612 MB  today   │
│ Crystal Mountain    46.935 N 121.474 W   2.0 km   Terrain  71  Flora 80   188 MB  Sep 30  │
│ Sugarloaf           Paused at 38% ─────────────────────────────── [ Resume ] [ Discard ]  │
│ ──────────────────────────────────────────────────────────────────────────────────────── │
│ Enter open · Del delete · F2 rename (later)                         [ Open data folder ]  │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```
The scores show the band word on hover, and on the card (below). Figures are in Overpass Mono.

**S4 Download (docked card, bottom left).**
```
┌ Downloading Crystal Mountain · 2.0 km ───────────────── [ _ ] [ Cancel ] ┐
│ ✓ Terrain                                                                │
│ ✓ Terrain surroundings                                                   │
│ ▸ Forest                                                                 │
│   Ground cover                                                           │
│   …every stage the pipeline declares, in its order                       │
│ ███████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  41%   about 1 min 20 s left             │
│ Forest: downloading canopy tile 3 of 6 · 35%                             │
│ 182.4 MB · 6.1 MB/s                                                      │
└──────────────────────────────────────────────────────────────────────────┘
```
Cancel asks **Keep for resuming** or **Discard**. A failure shows the message, then **Retry** (it resumes) and **Close**. Minimised, it becomes `⬇ Crystal Mountain 41% ▸`.

**S5 Quality card (modal).** The layout is approved in U1.
```
┌─ Crystal Mountain is ready ─────────────────────────────┐
│ Terrain   71 / 100   Fair                               │
│ Parts of this area use coarser data.                    │
│ <manifest Quality.OneLiner>                             │
│ Flora     80 / 100   Good                               │
│ <manifest Flora.OneLiner>                               │
│ ┄ (reserved: further quality lines, data-driven) ┄      │
│ [ Open mountain ]   [ Back to library ]                 │
└─────────────────────────────────────────────────────────┘
```
The bands are 90+ Excellent, 75+ Good, 50+ Fair and below 50 Limited. The number and the word always appear together. The card is built from a list of `QualityLine`s, so a later line only adds an entry; the forest calibration line was dropped in the scope cut. The library can reopen the card for any mountain.

## 4. The picker hand-off (agreed with task 13 through the coordinator)

Task 13 owns `Domain/Geo/PickedSite.cs`, which holds:
- `Name`;
- `Square`, the exact EPSG:6350 square;
- `Centre`, the snapped centre in WGS84;
- `SizeKm`;
- `IncludeImagery`;
- `Estimate`.

The picker raises `SiteChosen(PickedSite)` and `Cancelled`. App converts it to a `SiteRequest {Name, Centre, SizeKm}` and asserts that `SiteSquare.Create(Centre, SizeKm) == Square`; task 13 adds the matching Core test. Task 13 adds no App code. Until its commit lands, I build against the agreed signatures and test with a stub picker.

## 5. Stages stay data-driven

- `AcquisitionProgress` gains `IReadOnlyList<string> Stages`, the stage names the pipeline declared, in order. It's filled in `ProgressTracker.Snapshot`, as an additive commit the coordinator approved; no provider, manifest or cache changes.
- `DownloadViewModel` builds the S4 list from `Stages`, and the ticks from `StageIndex`. No stage name appears in UI code, so a stage the pipeline adds or renames shows up without a UI change.
- Fallback: if `Stages` is empty, the list grows as new `Stage` names arrive and the heading shows "stage N of M".

## 6. Code layout (all new files)

- **UI** (view models are plain C# in the UI assembly; views on UI Toolkit):
  - `UI/Flow/`: `TitleScreen`, `LibraryScreen`, `DownloadCard`, `QualityCard`;
  - view models: `DownloadViewModel`, `LibraryViewModel`, `QualityCardViewModel` (plain C#, tested in EditMode, since UI is an engine assembly and may not reference Acquisition; App copies progress into the UI's own `DownloadStatus`);
  - `Art/UI/Flow/*.uxml` and `*.uss`, using the existing theme tokens.
- **App:**
  - `AppFlow`, the screen state machine;
  - `DownloadService`;
  - `PendingDownloads`, the resume record.
- **Persistence:** `PendingDownload`, the JSON record format (engine-free).
- **Scene:** an `AppFlow` object and UIDocument in `MountainViewer.unity`, so the title sits over the live mountain. The scene's other objects aren't touched.
- **Fonts:** Overpass and Overpass Mono (SIL OFL) come from task 13's font commit under `Art/UI/Fonts/`; this task adds no font files.

The view models update only on a progress snapshot (4 Hz). The detail line is a single text set, with no rebuild per tick.

## 7. Test plan

| Test | Kind | Proves |
|---|---|---|
| `DownloadViewModel`: stage list from `Stages`; ticks follow `StageIndex`; an unknown stage name appears; the fallback with no `Stages` | EditMode | Data-driven stages |
| `ProgressTracker` snapshot carries `Stages` in declared order | Core | The additive change |
| Band edges 49/50/74/75/89/90 (Domain `QualityBands`, Core); `QualityCardViewModel`: number and word together, a fallback manifest shows its caveat and one-liner (EditMode) | Core, EditMode | S5 |
| `LibraryViewModel`: sort orders; paused downloads listed first | EditMode | S2 |
| `PendingDownload` round trip; a leftover record shows as resumable | Core | Resume across a quit |
| `PickedSite` → `SiteRequest` round trip gives the same square | Core (once task 13's commit lands) | The hand-off |
| `AppFlow` state machine: every transition in §2 and Esc backs out one step, with fake services | EditMode | Flow |
| **Offline open:** copy the TestData Jackson Hole 2 km package into a scratch library, then go S2 → Open → resort opened, with `DownloadService` replaced by one that throws if touched | PlayMode | Opening needs no network |
| Resume: a fake pipeline is cancelled midway, the record is left behind, and resume runs it again into S5 | EditMode | Minimise, cancel, resume |
| **Manual acceptance (demo.bat):** with a scratch library: New Resort → pick Crystal Mountain 2 km (3DEP fallback) → kill the game midway → relaunch → Resume → S5 shows Fair and the fallback one-liner → Open. Then Wi-Fi off → relaunch → My Resorts → Open | Player (GPU booked) | The acceptance line |

Before merging:
- `dotnet test tools/domain-tests/Tests` passes;
- `node tools/repo-checks/check.mjs` passes;
- EditMode and PlayMode tests pass;
- demo.bat gets entries 37–39: play from the title, the same with the network off, and emptying the scratch library (all on a scratch library).

## 8. Questions for the owner

1. **Names.** The task brief says New Resort and My Resorts; 0.4 says New Mountain and My Mountains. I'll use **New Resort** and **My Resorts** (the newer brief) unless you say otherwise.
2. **S2 as rows, not cards with thumbnails.** Thumbnails need a capture per mountain, which I'd add later.

## 9. Status (2026-10-02)

- **Built:**
  - S1, S2, S4 (with the pill) and S5;
  - the App flow, with task 13's site picker (S3) for New Resort;
  - `-data`, `-offline` and `-flowcapture`.
- **Tests:**
  - Core 208 passed;
  - EditMode 395 passed, 0 failed;
  - PlayMode passed, including `AppFlowTests`, which opens a mountain from the library with the network off.
- **End to end in the player** (scratch library, `-flowcapture`): title → My Resorts → New Resort → a real Crystal Mountain 2 km download (100% 3DEP 10 m fallback) → quality card (Terrain 30 Limited, Flora 87 Good) → opened.
- **Left for later:**
  - an *Exit to title* entry in the in-game menu (the HUD is task 12's);
  - keeping clicks on the download card from reaching the camera in the game (`ViewCamera.PointerBlocked` is set by the viewer).

## 10. Owner changes (2026-10-03)

- **Title signs:** Continue (green), New Area (blue), Load Area (black) and Manage Areas (double black). They replace New Resort and My Resorts.
- **Signpost:** at the left, centred vertically, with short posts. The top plate is plain sign white with dark letters and no symbol.
- **Corner plates:** Credits, Settings and Quit sit as small white plates in the bottom-right corner, where the owner's sketch put them. The download pill moved to the bottom left.
- **Load Area and Manage Areas** are one list in two modes:
  - Load Area opens an area (Enter or double-click);
  - Manage Areas deletes areas, and resumes or discards paused downloads.
- **Settings (S8):** units only for now, using the shared display-units setting. **Credits (S9):** the attribution from every downloaded area's package, which works offline, then the fonts.
