# Phase 3 · 08 Trails: the owner's in-game findings, painting and run lines

**Audience:** the project owner and coding agents.

**Status:** fixed on `fix/p3-08-paint-glitch` (plan approved 2026-10-20; decision TL26), for the owner's review.

**Build reviewed:** `main` at 49e16a2 (PR #93, P3-08 merged). The paint is TL25's brush:
- `TrailPaintMap`, with a growing 1 m window;
- CPU bytes uploaded by dirty rectangle through one 256² staging texture;
- `Redraw` replaying every stroke when a stroke is let go.

**Investigated and fixed on:** branch `fix/p3-08-paint-glitch`. The reproduction test, `Tests/EditMode/TrailPaintMapTests.cs`, is now the guard.

The screenshots came in chat on 2026-10-20 and aren't saved in the repo.

| # | Finding | Where | Cause | Status |
|---|---|---|---|---|
| 1 | While painting a trail, all the earlier paint sometimes vanishes. From there it gets glitchier and glitchier. The first time came right after letting go of the second click, the one that painted a second area | The New trail tool, Paint step, on the owner's site | See #2: the same upload fault. The whole trail goes up at once, and the screen shows the wrong bytes | **Fixed** with #2 |
| 2 | After the third stroke the paint is chaos (owner: "craziness"): bands of paint where nothing was painted, and parts of the painted run missing. The owner's reference shot shows the run's real line (white, from the head by Chair 4's bottom, across the top and down the east side); the paint sits off it in places | The same session, zoomed out over Chair 4 | **The cause, reproduced. Each frame's dirty rectangle is uploaded in 256 × 256 blocks through one staging texture. Per block, `Flush` writes the stage's CPU bytes, then calls `_stage.Apply()` and `Graphics.CopyTexture(stage → map)`.** <ul><li>The player renders on its own thread. There, `Apply` only queues the upload, and the render thread reads the stage's bytes later. By then the next block's bytes have overwritten them.</li><li>So every block in a frame lands with a later block's contents: copies of paint appear 256 m apart and the real paint goes missing.</li><li>A drag dirties only a small rectangle (one block), so painting looks fine. Letting go runs `Redraw`, which clears and replays every stroke, so the whole trail is dirty at once. Once the trail spans more than 256 m (a second or third area does it), the upload splits into blocks and breaks.</li><li>Every later redraw adds more misplaced blocks, which is why it gets glitchier.</li></ul> **Evidence:** <ul><li>`TrailPaintMapTests.TheGpuHoldsTheCpuPaintAfterEveryUpload` paints a first area, a second 400 m away, lets go (a full replay) and adds a third. After each upload it reads the GPU texture back and compares it with the CPU copy.</li><li>Single-threaded editor (`kGfxThreadingModeNonThreaded`): passes.</li><li>`-force-gfx-mt`, as the player runs: fails at "the second area let go", with 6,546 bytes different in a 640 × 576 map.</li></ul> The CPU paint itself is right; only the GPU copy is wrong. **Likely general fix (to plan):** <ul><li>a staging texture per block (or a ring of them);</li><li>or one `SetPixelData` + `Apply` on the map itself for a large rectangle;</li><li>keep the test, run under `-force-gfx-mt`, as the guard.</li></ul> | **Fixed:** <ul><li>The paint is a `RenderTexture`. Each frame the dirty rows go into a raw `GraphicsBuffer` by `SetData`, which copies at the call, and a compute kernel (`TrailPaintUpload.compute`) writes the dirty rectangle in one dispatch. There's no staging texture.</li><li>`TrailPaintMapTests` passes with and without `-force-gfx-mt`, and `release.ps1` runs it with a render thread.</li><li>The player check, `-paintcheck` (`demo.bat` 67c), paints three areas far apart with quick second strokes. The GPU paint matched the CPU paint in all 324 frames.</li></ul> |
| 3 | (Found while investigating.) A stroke started before the previous stroke's footprint is worked out loses what it has painted so far | `TrailTool.Restroke` and `TrailPaintMap.Redraw` | <ul><li>Letting go runs the footprint work in the background (`ToolContext.Analyse`).</li><li>Its done-callback calls `Paint.Redraw(Parts(), footprint)`, which clears the map and replays `Parts()`.</li><li>`Parts()` holds only finished strokes, not the one being painted. If the callback lands mid-drag, the live stroke's paint so far is wiped, and only the stamps from then on show. The stroke itself is stored whole, so the paint comes back at the next let-go.</li></ul> Not yet reproduced on screen; it's likeliest on quick successive strokes, and it would add to #1. **Likely fix:** <ul><li>replay the live stroke too;</li><li>or hold the redraw until the stroke ends.</li></ul> | **Fixed:** a result landing mid-stroke keeps its footprint but doesn't redraw; the stroke's own let-go does. `ToolboxTests.NewTrail_AStrokeBeingPaintedIsNotRedrawnAway` checks it |
| 4 | The run lines should disappear when up real close. Today, close to the ground, a run's line is a wide flat black ribbon across the snow (Run 6, a black run) | The Trails map layer (Shift+4), the camera low over Run 6 | **The cause:** <ul><li>`PendingPreview.Follow` sets one width for a whole line: 2.5 px at the camera's distance from the line's `Centre`, the average of its points (`PendingPreview.cs:124`).</li><li>On a long run the centre is far from a camera down close (say 1 km). That gives about 1 m per pixel, so the whole line is drawn 2–3 m wide, even right under the camera.</li><li>Black because Run 6 rates black (TD-03).</li></ul> **Likely fix (to plan):** <ul><li>fade the trail lines out as the camera nears the ground (the owner's ask);</li><li>and size a line by its nearest point, or per stretch, not its middle, so a far line isn't fat up close.</li></ul> Lifts and the other preview lines use the same `Follow` and would get the sizing fix too | **Fixed:** <ul><li>Every preview line has a 16-key width curve, each key 2.5 px at its own point's distance.</li><li>Run lines (and the focus casing) fade out with dither: gone within 40 m, fully drawn from 150 m. The owner chose ~100 m, run lines only.</li><li>A low capture over the owner's save shows the line gone under the camera and thin beyond.</li></ul> |

| 5 | Labels are visible through terrain right now; stop that (owner, 2026-10-20) | Run names (and the landmark names) on the map | `LandmarkLabelOverlay.Update` drew every label in front of the camera, with no ground test. The contour labels already had one | **Fixed:** <ul><li>The contour labels' sight test is shared, as `UI/LabelSight`: 16 ground samples on the sight line, hidden if the ground rises more than 2 m above it.</li><li>Run and landmark names now hide behind the ground. Each label is checked every 4th frame, and run names use the edited ground.</li><li>Lift names ("Chair 4") are drawn by task 09's code, which is told to use it.</li></ul> |

## Notes
- Paint style, brush feel and the footprint rules (TL25, TF-02) are not in question; only the upload (#1–#2), the
  redraw timing (#3) and the run lines' width (#4).
- The paint bench and movie (`-paintbench`, `-paintmovie`) didn't catch #2: their strokes stay within about 256 m, so
  their uploads never split into blocks.

## Measurements after the fixes (RTX 3060 Ti, 2026-10-20)
- **Paint bench** (`-paintbench`, two runs): 4.7–5.1 ms p95 while dragging; letting go to the footprint shown, 38–51 ms p95.
  #93 measured 4.5 and 33 ms. The machine was busy with other sessions' work, and the second run is the quieter one.
- **Frame benchmark:**
  - High passes against the baseline: 7.59 ms p95, garbage +0 B.
  - The Development run with the HUD passes: 8.20 ms p95, 3,504 B of garbage in 1 of 13,951 frames.
  - Medium flagged different views on each run against the stored baseline: 5.15 and about 5.2 ms p95, vs 4.67.
  - A same-machine A/B settles it: main's own build measured 7.61 ms p95 Medium against this branch's 5.16, run straight after. The path runs none of the changed code (no trails or paint, and labels are skipped with the HUD off), so the Medium flags are machine noise, not a regression.
- **Tests:**
  - EditMode passes, except the two known `LiftAssetTests` Monta failures.
  - `TrailPaintMapTests`, `LabelSightTests` and `ToolboxTests` also pass with `-force-gfx-mt`.
  - `dotnet test` 593/593; repo checks pass.
  - The player `-paintcheck` passes over 324 frames.
