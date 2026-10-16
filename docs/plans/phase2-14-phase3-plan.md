# Phase 2 · 14 Detailed Phase 3 plan: drawing on the mountain

**Audience:** the project owner and coding agents. **Status:** for your review (task 14 ⛳). This plan replaces the
Phase 3 section of [0.6 §4](phase0-0.6-milestones.md#4-phase-3-drawing-on-the-mountain-iteration-2): its goal and
exit criteria are kept, the scope is narrowed to fixed-grip chairlifts (E13), and the Phase 2 carry-over and a no-help
tester session are added (E9). The Phase 2 exit review is [phase2-14-exit-review.md](phase2-14-exit-review.md).
**Phase 3 starts when you approve this document.**

**Goal:** the player designs on the real terrain. Trails are painted, graded and cleared; fixed-grip chairlifts are
placed with towers, rope and moving chairs; paths join them into a network. A design saves, reloads and undoes, and the
mountain still opens and renders within budget.

## 1. How Phase 3 runs

- **Branches, reviews and gates:** as in Phase 2 ([Phase 2 plan §1](phase1-16-phase2-plan.md#1-how-phase-2-runs)).
  - One topic branch per task (`feature/p3-NN-…`), from origin/main.
  - **The owner approves every PR before it merges**, with demo steps and a new `demo.bat` entry.
  - The gates are 02 (design rules), 07 (style tile), 12 (tester build) and 13 (exit).
- **Every PR passes:**
  - `node tools/repo-checks/check.mjs`;
  - `dotnet test tools/domain-tests/Tests`;
  - EditMode and PlayMode in batchmode;
  - `tools/release/compile-check.ps1` (CI doesn't compile App, UI, Presentation or World);
  - and, for anything that renders, `demo.bat` 41 compared with the **re-pinned baselines** (E10).
- **Every task starts with a planning phase (E15).** Before writing any code, docs or assets, the thread posts a
  🟦 plan in its own session: scope, the files it owns, the approach, how it will be tested and demoed, and the
  questions for you, each with a recommendation. **It waits for your go before implementing.** If the scope changes
  mid-task, it stops and re-plans the same way. Updates are labelled 🟦 Planning, 🟩 Implementing, 🟨 Your review.
- **Threads:** each task runs in its own session.
  - It messages the coordinator only at start and finish ([thread protocol]).
  - **The GPU goes through `tools/gpu/gpu.ps1`:** `measure` (alone) for benchmarks, timing and review movies; `render`
    (two at once) for captures, PlayMode on site caches and Lift Lab; no lane for builds, EditMode and dotnet.
  - Each thread merges origin/main itself (merge, not rebase) and renumbers its `demo.bat` entries if they collide.
- **Frozen formats** (0.3 §5): the manifest, `library.json`, `recent.json`, `view.json` and `download.json` change only
  with a version bump, a `VersionedJson` migration and a `TestData/formats/v<N>` fixture. **The save file (task 04) is
  frozen from its first merge**, under the same rules. The coordinator sequences format changes: never two threads on
  the same format at once.
- **Golden tests stay local** (E2): a PR that changes cover, forest or grading runs the full `dotnet test` with LFS
  checked out (348 tests today, none skipped) and says so.
- **The archived game is reference only.** Its rules (0.1 §6, §9) and our doctrine notes are re-specified in task 02's
  document and implemented natively; no code is copied or transpiled.
- **Mockup first** for every new panel (Toolbox tools, the inspector, Analysis tabs): change
  [ui-layout.html](prototypes/ui-layout.html), get your approval, port, then run the parity check (`demo.bat` 48).
- **Free tools only.** The Splines package returns (0.3 §11) if task 08 or 09 needs it.

## 2. Task order

```
01 Carry-over polish + baselines (any time, first)

02 Design rules ⛳ ─► 03 Design document, commands, undo ─┬─► 04 Save file ───────────────────────────┐
                                                         ├─► 05 Terrain edit pipeline ─┐              │
                                                         └─► 06 Toolbox + placement ───┤              │
                                                                                       ▼              │
                                                                       07 Style tile ⛳ ─┬─► 08 Trails ┤
                                                                                        └─► 09 Lifts ─┤
                                                                                                      ▼
                                                    10 Paths, junctions, network ─► 11 Benchmark ─► 12 Tester build ⛳ ─► 13 Exit ⛳
```

- Task 01 runs first, in parallel with 02. It re-pins the baselines that every later perf comparison uses.
- Task 02 is a document only, and it gates every drawing task: no grading or lift code before you approve the rules.
- Tasks 04, 05 and 06 can run in parallel once 03 lands. Task 04 is the only thread touching Persistence formats then.
- Task 07 builds one trail and one lift on Jackson Hole by command (no tool UI), so the look is settled before the
  tools scale up, as Phase 1's style tile did.
- Tasks 08 and 09 run in parallel after 07; they share the Toolbox, so 06 defines the tool contract both implement.
- Task 10 needs both trails and lifts.

## 3. Tasks

| # | Task | Deliverables | Acceptance | Est. |
|---|---|---|---|---|
| 01 | **Carry-over polish and baselines** | Tester findings 3–6 ([phase2-tester-findings.md](phase2-tester-findings.md)): the elevation readout's no-reading unit, a focus look for the title's signs distinct from hover, the title drift's bob and the laptop's tree flicker (captured on the laptop first); `acquire` waits and retries on USGS 5xx like the game; photo bar reachable by arrow keys; the GPU script's Ctrl+C hand check; the untested live window switch and 15 s keep/revert; **re-pinned perf baselines** at the 10:30 benchmark (E10) | Each finding fixed with a test or a capture, or explained and moved to Later with your OK. `compare.mjs` passes on main against the new baselines | 0.5 wk |
| 02 ⛳ | **Design rules** | `docs/plans/p3-02-design-rules.md`, re-specified from 0.1 §6.1–6.3 and §9, and our doctrine: **trails** (brush widths, difficulty formula and bands, the level bench, 45° (1:1) cut and fill faces, grading never leaves the painted footprint, too steep stretches stay natural and are reported, cut/fill/balance m³, tree clearing inside the footprint); **lifts** (exactly two points, click-click, bottom-first terminals, the fixed-grip catalog with generic names, capacity and speed, tower spacing and clearance rules, rope sag); **paths and junctions**; **the network rule**; the **construction flow** (draw, analyse, review, confirm or cancel; stale results rejected); open questions for you | Your approval. Every rule has an ID that later tests cite. Contradictions with the shipped game (for example difficulty in degrees against the slope layer's 25/40/60% bands) are listed with a recommendation | 1 wk |
| 03 | **Design document, commands and undo (T15)** | An engine-free, revisioned design document (Domain): trails, lifts, paths, junctions; typed commands against a named revision (stale ones rejected, nothing partly applied); undo and redo; keyed IDs; the derived network graph rebuilt, never stored | Commands round-trip and undo exactly (property tests). A command against an old revision is rejected. Same commands in the same order give the same document bytes (golden). Runs in `tools/domain-tests` | 1.5 wk |
| 04 | **Save file** | A save per area (design plus sparse 64 m terrain and clearing delta tiles over the immutable package), written atomically; Save (Ctrl+S, now live), autosave, Continue opens the last save; Load Area lists saves under their area; frozen as v1 with a fixture (0.3 §5) | Save, quit, reopen gives an identical document and identical heights (test). A crash mid-save leaves the last good save. A future-version save is refused clearly. Phase 2 libraries open unchanged | 1 wk |
| 05 | **Terrain edit pipeline** | Height deltas applied over the float32 source heights; only the touched cache tiles rebuilt; the clearing mask driving forest density (trees removed, the forest floor and snow following); undo restoring heights and trees exactly; contours, slope and the cursor readout reading edited heights | A 300 m graded test edit previews in ≤100 ms and commits in ≤250 ms (exit criterion, on the reference PC). Undo restores byte-identical heights. Forest and cover goldens unchanged outside edited tiles. 0 B per frame when idle | 2 wk |
| 06 | **Toolbox and placement** | The Toolbox's Trail, Lift and Path tools made real (mockup first): one active tool at a time, picking on the terrain, snapping to terminals and junctions, a pending preview, the review panel (stats, warnings, Confirm or Cancel), tool keys inside the open tray (`LettersToTools` on), every key rebindable | Every tool works by keyboard and mouse at 50–150% scale (PlayMode test). Switching tools cancels the previous one cleanly. The camera keys keep working while a tool is armed | 1 wk |
| 07 ⛳ | **Style tile: one trail and one lift** | On Jackson Hole 5 km, built by command: one graded, cleared trail (groomed snow, cut and fill faces, the edge against the forest) and one Sessellift FGQ-4 with towers, rope and chairs; captures from the benchmark views, summer and winter, day and night | Your approval of the look. Benchmark within 10% of the re-pinned baselines | 1 wk |
| 08 | **Trails** | Paint with the brush, head and tail, difficulty symbol and colour, grading by the 02 rules with the yellow pending contours, red ungraded stretches, cut/fill/balance readout, clearing, a profile in the inspector; edit, delete and undo; the **Trails** map layer and the Analysis trail list | Grading matches 02's rules on recorded fixtures (golden heights). No grading outside the footprint (test). Preview and commit within the 05 budgets on a 1 km run | 2.5 wk |
| 09 | **Lifts** | Two-point placement with bottom-first terminals; the fixed-grip catalog (Sessellift FGQ-4, Monta FG4; generic names); towers placed along the profile by the 02 rules (support, breakover, hold-down, combination), editable; catenary rope; chairs moving at rope speed; line clearing; the **Lifts** map layer and the Analysis lift list | Towers keep the rope clear of the ground everywhere (test over recorded profiles). 40 moving lifts' worth of chairs cost 0 B per frame and stay within budget (benchmark). Undo removes a lift and restores its clearing | 2.5 wk |
| 10 | **Paths, junctions and the network** | Connector paths; junctions that split trails in one command; the derived ski network; the connectivity check (every base lift reaches a descent), with the status bar's lift and trail counts and an Analysis network view | Network rebuilt identically from any load (golden). Deleting a trail prunes only junctions nothing else uses. Connectivity flags a disconnected lift in a test resort | 1.5 wk |
| 11 | **Benchmark with a drawn resort** | A committed test design on Jackson Hole 5 km (about 20 trails, 6 lifts); the benchmark path extended over it; edit-timing runs (preview, commit, undo) | Frame budgets met at 1080p High and Medium with the design shown. Preview ≤100 ms and commit ≤250 ms at p95. Results in `docs/perf/` | 0.5 wk |
| 12 ⛳ | **Tester build (`v0.3.0`)** | A release from a tag; a no-help tester session (E9): download, explore, **photograph**, then draw, save and reload a small resort; a findings list; fixes for anything blocking | The tester finishes without help, or each block is fixed or decided. The clean install still opens the demo offline | 1 wk |
| 13 ⛳ | **Phase 3 exit** | Exit review against this plan's criteria (§4); velocity; the detailed Phase 4 plan; the `unity-m3` tag and the `archive/unity` fast-forward | All criteria met, or an explicit decision on each miss | 0.5 wk |

**Total:** about 16.5 task-weeks, inside 0.6's 3–5 months.

**Calendar forecast (E4):**
- Phases 1 and 2 both ran about 9× faster than their task-week estimates. At that ratio, Phase 3's build work is about
  **2–3 weeks**.
- Drawing tools are new design work, not ports of an approved mockup. The rules gate (02), the look gate (07), the
  tools' feel and the tester session wait on you. They don't compress.
- **Forecast: 4–6 weeks** of calendar time. Phase 4 (simulation foundations) follows.

## 4. Exit criteria

From [0.6 §4](phase0-0.6-milestones.md#4-phase-3-drawing-on-the-mountain-iteration-2), plus E9:
1. A resort with trails and lifts can be drawn, graded, saved, reloaded and undone.
2. Grading preview ≤100 ms; commit ≤250 ms (reference PC, p95).
3. Performance budgets still met with a drawn resort shown (task 11).
4. A tester draws, saves and reloads a small resort, and downloads, explores and photographs a mountain, without help
   (task 12).
5. Velocity is recorded, and the Phase 4 detailed plan is written.

## 5. Owner decision points

| When | What you decide |
|---|---|
| 01 | Whether each tester finding is fixed now or moved to Later |
| 02 ⛳ | The design rules, including: difficulty bands (0.1's degrees or the slope layer's percent), brush widths, the starting lift catalog's capacities, tower spacing and the clearance rule, whether grading costs anything yet (no economy until Phase 4) |
| 04 | Saves per area: one save or several named saves; the autosave interval |
| 06 | The Toolbox's tool panels and review panel (mockup first) |
| 07 ⛳ | The look of a groomed, graded trail and a lift on the mountain |
| 09 | Tower placement: automatic only, or automatic with manual moves |
| 12 ⛳ | The tester, and whether a finding blocks the release |

## 6. Test data and hardware

- **Unchanged:** the committed Jackson Hole 2 km test terrain, the local Jackson Hole 5 km demo (benchmark, title and
  style tile), Crystal Mountain (fallback), Sugarloaf (New England forest) and Palisades Tahoe (seam).
- **New:** recorded grading fixtures (a sidehill, a ridge, a gully, a too-steep face) and lift profiles, small and
  committed; the task 11 test design, committed; a v1 save fixture (04).
- **Hardware:** the reference PC (RTX 3060 Ti, 2560×1080 monitor) and the 2016 laptop. The RTX 2060-class check stays
  outside Phase 3 (E8).

## 7. Definition of done (every task)

As in the [Phase 2 plan §5](phase1-16-phase2-plan.md#5-definition-of-done-every-task), plus:
- The task's 🟦 plan was approved by you before implementation began (E15), and the PR links to it.
- Each rule implemented cites its 02 rule ID in a test.
- Each command undoes exactly, and stale commands are rejected (test).
- Each edit path is timed against the preview and commit budgets.
- Each new map or info layer fits the owner's taxonomy (map layers physical, info layers display only).

## 8. Later (not in Phase 3)

- **Before the first public release:** the RTX 2060-class check (E8), code signing (E11), the "Alpine Labs" trademark
  search (E12).
- **Infrastructure:** detachable chairs and gondolas, surface lifts, ponds and dams, the snowmaking network and guns
  (the SLE assets exist), buildings, roads drawn by the player.
- **Simulation (Phase 4):** the real clock, weather, the snow model and the snow conditions map, lakes that freeze and
  thaw, guests and the Guests tab.
- **Imagery:** NAIP (Phase 2 task 13, skipped).
- **Forest and trees:** seasons for deciduous trees, the next species, a height-only regional calibration, the
  broadleaf height spread, the beech and red oak LOD2 strips (a model rebuild), western_hemlock's material validation.
- **Engineering:** pooling the download pipeline's buffers (about 800 MB of garbage churn per download), a Unity
  compile step in CI (needs a licence on the runner), backdrop blur for HUD panels.
- **Picker nice-to-haves:** a blurred parent tile while map tiles load; the overlay's 1 m / ~3 m footprints
  overpromising against the elevation service.

[thread protocol]: phase1-16-phase2-plan.md#1-how-phase-2-runs
