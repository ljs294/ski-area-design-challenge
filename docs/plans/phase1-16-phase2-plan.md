# Phase 1 · 16 Detailed Phase 2 plan: iteration 1 complete

**Audience:** the project owner and coding agents. **Status:** for your review (task 16 ⛳). This plan replaces the
Phase 2 section of [0.6 §3](phase0-0.6-milestones.md#3-phase-2-iteration-1-complete): its scope and four exit criteria
are unchanged, and the ship-quality backlog items are added (decision E3). The Phase 1 exit review is
[phase1-16-exit-review.md](phase1-16-exit-review.md). **Phase 2 starts when you approve this document.**

**Goal:** turn the slice into a finished, shareable "just the mountain" game. That means every screen, the polish a
tester would notice, the minimum spec checked on real hardware, and a build that installs on another PC.

## 1. How Phase 2 runs

- **Branches, reviews and gates:** as in Phase 1 ([0.7 §1](phase0-0.7-phase1-plan.md#1-how-phase-1-runs)).
  - One topic branch per task (`feature/p2-NN-…`).
  - **The owner approves every PR before it merges**, and each PR includes demo steps and a new `demo.bat` entry.
  - The gates are 12 (tester session) and 14 (exit).
- **Every PR passes:**
  - `node tools/repo-checks/check.mjs`;
  - `dotnet test tools/domain-tests/Tests`;
  - EditMode and PlayMode in batchmode;
  - and, for anything that renders, `demo.bat` 41 compared with the stored baselines (`tools/perf/compare.mjs`).
- **Threads:** each task runs in its own session.
  - It messages the coordinator only at start and finish.
  - Players, benchmarks and captures claim `D:\skiAreaDesignChallenge\.gpu-lock` first.
  - Each thread merges origin/main itself (merge, not rebase).
- **Golden tests stay local** (E2). CI fetches only the small LFS fixtures. So a PR that changes cover or forest
  states that it ran the goldens locally (the full `dotnet test` with LFS checked out, 266 tests, none skipped).
- **Formats freeze at task 08.** From then on, every package, library or view-state change ships with a migration and
  a test (T11). The cache stays a rebuildable cache: its version can still bump, but task 04's housekeeping stops
  versions deleting each other.
- **Free tools only.** A rented GPU for task 11 needs your OK on the cost first.

## 2. Task order

```
01 UI foundation ─┬─► 02 HUD (Trailhead) ─────────────────────────┐
                  ├─► 03 Main menu + bundled demo ─┐               │
08 Formats frozen ┼─► 04 Library + cache housekeeping              │
                  ├─► 05 Settings (graphics, Auto tree detail)     ├─► 10 Build and release ─► 11 Hardware ─► 12 Tester build ⛳ ─► 14 Exit ⛳
                  ├─► 06 Background downloads, dialogs, offline    │
                  └─► 07 Photo mode + credits ─────────────────────┘
09 Forest look (any time; owns the tree and terrain shaders)       13 NAIP imagery (optional, last, cut first)
```

- Tasks 01 and 08 come first, and can run together.
- Tasks 02–07 can run in parallel once 01 lands. Tasks 04 and 06 also need 08.
- Task 09 runs at any time, alongside the others.
- Task 01 runs in its own thread (`feature/p2-01-ui-foundation`), not the UI mockup thread, which keeps
  `docs/plans/prototypes/`. Task 02 starts when 01 lands. Task 01's audit against the accepted mockup lists what
  task 02 has to build: [p2-01-mockup-audit.md](p2-01-mockup-audit.md). Owner rulings during task 01
  (2026-10-06): 100% UI scale is the mockup's size (its 1280×720 stage), the theme has Auto (by the sun), and the
  mockup is followed closely.

## 3. Tasks

| # | Task | Deliverables | Acceptance | Est. |
|---|---|---|---|---|
| 01 | **UI foundation** | Shared text settings that fix light-theme text reading thin (Unity's linear-space blending); the mockup's translucent panels ported as solid colours; theme tokens for light and dark; a UI-scale setting from 50% to 150%; keyboard focus and navigation for every UI Toolkit screen; **ultrawide layout (E6)**: every screen anchored to the edges, with full-screen panels and modals held to a centred 16:9 column, so nothing stretches or drifts on 21:9 or 32:9 | Light and dark text match in weight in side-by-side captures. Every existing screen works by keyboard alone at 50%, 100% and 150% (PlayMode test plus captures). Every screen is captured at 1920×1080, 2560×1080, 3440×1440 and 5120×1440, with nothing clipped, stretched or off-centre. No per-frame UI allocation (0 B, `demo.bat` 43) | 1 wk |
| 02 | **HUD (S6, S7) in the Trailhead direction** | The accepted layout ([ui-layout.html](prototypes/ui-layout.html), the [game-ui-direction.md](game-ui-direction.md) "built in S6" checklist): the full-width bar, time bar, map and info layers, the legend and cursor readout, the Toolbox and Analysis panels as placeholders for later phases (the bar counts and the Guests tab are greyed out), and the in-game menu | Matches the mockup in captures at 1080p, 1440p and ultrawide 3440×1440, in both themes. On ultrawide the bar spans the width, and the panels stay at the edges, not stretched. The key map ([controls-key-map.md](controls-key-map.md)) is unchanged unless you approve a change. View models update at a bounded rate, with 0 B per frame | 1.5 wk |
| 03 | **Main menu over the live demo (S1), demo bundled (G2, D1)** | The final title over the live Jackson Hole 5 km scene, with a slow camera drift; the demo package built into the game (StreamingAssets), opened in place; first launch with an empty library | A fresh install with no library, offline, shows the title over Jackson Hole in ≤10 s and opens it from "Open the demo". The demo isn't committed (LFS quota): the build step copies it from a local package | 1 wk |
| 04 | **Full library (S2) and cache housekeeping** | Rename, delete (with confirmation), resume, disk use per area and in total, sort; `TerrainCache.Build` keeps the newest two cache versions and never deletes a folder that's open; a "Free space" action | Library actions round-trip in tests, and survive a restart. Two players on different cache versions no longer rebuild each other's caches (test). Disk use matches the folder sizes | 1 wk |
| 05 | **Settings (S8)** | The Unity-native graphics menu (U3) over the B1 presets, plus per-option overrides; **Auto tree detail** (it picks the tree LOD bias from a short timing at first open); display (resolution, including 21:9 and 32:9 modes, window mode, vsync, frame cap, and a field-of-view slider; the camera keeps its vertical FOV, so ultrawide shows more to the sides, not less above and below); interface (theme, UI scale, units); controls (the key list, with rebinding if cheap; otherwise view only); data (library folder, cache size, offline mode). All saved and restored | Each setting takes effect without a restart, except where Unity requires one (labelled). Auto picks Medium-or-better on the reference PC and keeps p95 within budget (benchmark). Settings persist across launches (test). The title's camera, the benchmark path and photo mode frame correctly at 3440×1440 | 1.5 wk |
| 06 | **Background downloads (G5), dialogs and offline states (S11)** | One download at a time, continuing while you explore another area; resume after a crash or quit; the S4 pill everywhere; confirm, error and offline dialogs; every flow's offline state (picker, download, credits) | Killing the game mid-download and restarting resumes from the stage it reached (test, plus `demo.bat` 38). Frame p95 stays within budget while a download runs (benchmark with a download in progress). Every S11 state is captured in both themes | 1 wk |
| 07 | **Photo mode (S10, G3) and credits (S9)** | The thin photo bar: time and date, sun, depth of field off or on, grade, hide UI, save at screen resolution or 2×; credits generated from every package's manifest attribution, plus the game's own | Photos save to the user's Pictures folder, with no hitch over one frame at 2×. Credits list every source of every area in the library (test against the manifests) | 1 wk |
| 08 | **Formats frozen (T11)** | Version numbers and migrations for the package manifest, library index, recent list and view state; a migration test fixture per format (the Phase 1 files as v1); rules written into 0.3 §5 | Phase 1 packages and libraries open unchanged after the freeze (fixture tests). A future-version file is refused with a clear message, never misread | 1 wk |
| 09 | **Forest look** | Far impostors that stop reading as grey round blobs from overhead (core and ring); forest floor blended into the surrounding ground, with darkness only directly under the crowns | Before-and-after captures from the benchmark's eight views and from overhead, approved by you. Benchmark within 10% of the baselines. Golden hashes unchanged, unless you approve a change | 1 wk |
| 10 | **Build and release** | A one-command Windows build (release), a version number (shown on the title and in logs), crash and error logging to a local file the tester can send, and a zip (an installer only if one is free and simple) | A clean build from a fresh clone on this PC, including `git lfs checkout`, produces the zip. A forced exception writes a crash log. The version matches the tag | 1 wk |
| 11 | **Real hardware (M1)** | Medium on an RTX 2060-class PC: a tester's PC first, otherwise a few hours on a rented cloud GPU (cost approved first); a Low smoke test on the 10-year-old laptop; a one-hour soak on this PC | RTX 2060 class: 30 FPS or better at 1080p Medium on the benchmark path (exit criterion 2). The laptop starts, opens the demo and runs at Low. Memory grows ≤10% over the hour. A benchmark at 3440×1440 High on this PC is recorded alongside the 1080p baseline | 0.5 wk |
| 12 ⛳ | **Tester build** | The zip on a second PC, offline; a tester session with no help; a findings list; fixes for anything blocking. **Includes the real data-seam check (E1):** one downloaded area that straddles the edge of S1M and the 3DEP fallback, captured at the join | Exit criteria 3 and 4: a clean install opens the demo offline; the tester downloads, explores and photographs a mountain without help. The seam capture shows no visible step, or a fix is planned | 1 wk |
| 13 | **NAIP satellite imagery (G7)** (optional) | An imagery map layer from USGS NAIP, downloaded with the package and blended in place of the ground look | The layer toggles within one frame. Download size and time are on the estimate line. **Cut first** if time is short | 1 wk |
| 14 ⛳ | **Phase 2 exit** | Exit review against 0.6 §3; velocity; the **detailed Phase 3 plan**; the `unity-m2` tag and the `archive/unity` fast-forward | All four exit criteria met, or an explicit decision on each miss | 0.5 wk |

**Total:** about 14 weeks of task time, or 13 without imagery. That is at the top of 0.6's 2–3 months, because the
ship-quality fixes were added.

**Calendar forecast (E4):**
- Phase 1 ran about 9× faster than its task-week estimate: 14 task-weeks in 11 days. At that ratio, Phase 2's build
  work is about **2 weeks**.
- Tasks 11 and 12 depend on people and hardware: a tester's PC, a tester's time, and your reviews. They don't
  compress.
- **Forecast: 3–5 weeks** of calendar time. Phase 3 (drawing trails and lifts) follows.

## 4. Test data and hardware

- The Phase 1 sets are unchanged:
  - the committed Jackson Hole 2 km test terrain;
  - the local Jackson Hole 5 km demo (benchmark and menu);
  - Crystal Mountain (fallback);
  - Sugarloaf (New England forest).
- **The bundled demo** is built into the game from the local Jackson Hole 5 km package. It's never committed.
- **Format fixtures (08):** small Phase 1 manifest, library and view-state files, committed.
- **Ultrawide:** the owner plays on an ultrawide monitor. Captures and a benchmark run at 3440×1440 High are part of
  tasks 01, 02, 05 and 11. The budget at 3440×1440 is measured and recorded; 1080p stays the budget of record.
- **Hardware:**
  - this RTX 3060 Ti PC is the reference and the soak machine;
  - an RTX 2060-class tester PC, or a rented GPU (11);
  - the 10-year-old laptop (11);
  - the owner's ultrawide monitor (01, 02, 05, 11);
  - a second PC for the clean install (12).

## 5. Definition of done (every task)

As in [0.7 §5](phase0-0.7-phase1-plan.md#5-definition-of-done-every-task), plus:
- Each new screen or state is captured in both themes, at 100% and 150% UI scale.
- Each flow works by keyboard alone.
- Each screen is checked at 16:9 and ultrawide (21:9 and 32:9).
- Each change to a frozen format adds a migration and a fixture test (from task 08).

## 6. Decisions

To be filled in when you approve this plan. The exit review's E1–E4 already record its scope, the estimate style and
the carried-over checks.

## 7. Later (not in Phase 2)

Backlog items kept for after iteration 1, or for a phase where they fit:
- **Seasons:** deciduous spring, summer and autumn variants driven by the date scrubber. The grass-tint hook is ready
  (`GroundLayers.SetGrassTint`).
- **The next species,** from [phase1-species-priority.md](phase1-species-priority.md): ponderosa pine, white fir, and the
  white, black, chestnut and bur oaks.
- **Forest structure:**
  - a height-only regional calibration (needs a perf run);
  - the broadleaf height-spread gap ([forest-structure-report.md](forest-structure-report.md) §4, §9).
- **Tree details:**
  - a back-face tint for the silvery undersides of firs;
  - western_hemlock's URP material validation (no visible effect);
  - checking in the game the brown strips that beech and red oak showed at LOD2 in Blender.
- **Picker nice-to-haves:**
  - a blurred parent tile while map tiles load;
  - the overlay's 1 m / ~3 m footprints overpromising against the elevation service.
