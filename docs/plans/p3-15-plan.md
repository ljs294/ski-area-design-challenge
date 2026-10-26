# Phase 3 task 15: terrain brush, plan (approved 2026-10-26)

**Status:** approved by the owner on 2026-10-26 (rev 3). Decisions BR1–BR12 in [phase0-decisions.md](phase0-decisions.md); rules TB-01–TB-14 and TW-02 v4 in [p3-02-design-rules.md](p3-02-design-rules.md).


## Context
The owner asked for this as TL14: a Cities: Skylines 2-style terrain tool for hand-grading cat tracks, junctions and the ground round lifts. Brush strokes are authored data that the earthwork replay (TL3) rebuilds byte for byte. Main already has save v3 (P3-09, LT44), so this is **save v4 / design Shape 4**.

**Branch:** `feature/p3-15-terrain-brush` (from 22b0d59e), demo **73**. The coordinator has the start message and agrees v4.

**Owner rulings so far (2026-10-26):**
- A **Terrain** tab in the Toolbox holds the CS2 set:
  - **Raise/Lower:** hold left to raise, hold right to lower.
  - **Flatten:** right click samples the target elevation under the pointer; left paints toward it.
  - **Smooth.**
  - **Slope:** right click sets the target; a left drag ramps toward it at a grade picked by rating chips, with a slider inside the band.
- Every brush pane looks nearly the same: **Diameter** and **Strength**, plus **Target elevation** and **Slope** where they apply.
- The tool owns the right mouse button while armed.
- **The ground moves live and instantly while you hold the mouse.**
- Strokes replay last, after trails and lifts.
- **There's a final confirmation (Break ground) that shows the cut and fill.** Volumes for now; money comes with the Phase 4 economy.
- **Terminals and other buildings are fixed.** The brush can't change their ground, and the ground may slope into them at **no more than 1:1**.
- **Lift clearance envelopes can't be infringed.** The brush never brings ground too close to the lift line.
- **Towers adjust their height automatically** to the ground change, and the head stays where it is, in the same orientation.
- When a tower reaches its height limits, **the brush stops** ("held by tower 7").
- The owner expects this to need a bigger structural change to lifts.

## Behaviour
- **Tools:** four tiles: **Raise/Lower (B)**, **Flatten (L)**, **Smooth (O)**, **Slope (G)**. None uses WASD, QE, RF or C (TL19).
- **Dabs:** 30 ticks a second at the pointer. A fast move fills in dabs at ¼-radius spacing that share the tick's weight, so mouse speed doesn't change the rate. Each dab is stored as (x, y, weight), with a fixed soft falloff.
- **The brushes:**
  - **Raise/Lower:** by time. 100% is about 8 ft a second at the centre, tuned at the mockup review.
  - **Flatten:** moves the ground toward the target by `strength × falloff` each tick.
  - **Smooth:** a two-pass snapshot per dab (a binomial 3×3 mean, then a blend), so the result doesn't depend on scan order.
  - **Slope:**
    - The start is the ground at the press.
    - Along the drag path, the target height runs from the start toward the target at the chosen grade, then holds at the target.
    - Each point takes its distance from its projection onto the path (a 2R window of segments), so the ramp is **level across** with no steps.
    - The rating chips are ● <25%, ■ 25–40%, ◆ 40–60% and ◆◆ 60%+, from `Domain/Measure/SlopeBands.cs`.
- **The session flow:**
  1. **Paint:** strokes are pending, and the ground moves live. The pane shows a running bill. Ctrl+Z undoes the last pending stroke.
  2. **Break ground** (Enter or the button) opens the review:
     - cut, fill and net import or export (yd³, or m³ in metric);
     - trees cleared;
     - **towers re-heighted** ("Tower 7: 45 → 52 ft");
     - places the brush was **held** ("by a terminal", "by the lift line", "by tower 7's limit").
  3. **Confirm:** one `AddTerrainStrokes` command (every pending stroke) = one undo step. The tool stays armed (P3-06 Q4).
  4. **Esc or ✕ with pending work** asks first (CF-06), then puts the ground back.
- **Pane, the same for all four tools:**
  - **Diameter** 10–400 ft (default 60), **Strength** 1–100% (default 50);
  - **Target elevation** "8,214 ft · right-click to set" (Flatten, Slope);
  - **Slope** chips plus "■ 32%";
  - the running bill.
- **Keys:**
  - [ ] diameter, and , . strength (only while claimed);
  - PgUp/PgDn target ±1 ft (Shift ±10);
  - Shift+, and Shift+. grade ±1%;
  - Enter: Break ground;
  - keyboard focus works at 50–150%;
  - everything goes through KeyBindings/GameAction plus controls-key-map.md.
- **Other limits:**
  - inside the core only;
  - at most 40 ft of cut or fill against natural ground, and an existing deeper cut is never dragged back;
  - a cell moved 1 ft or more is cleared of trees;
  - snow and slope follow automatically, with no new ground look.

## Fixed ground: structures, envelopes, towers (the structural part)
**1. New Domain `FixedGround`** (`Domain/Design/FixedGround.cs`):
- It builds, per stroke session, a per-point height band [lo, hi] over the brushed tiles.
- Every dab clamps into it, and **never makes a point worse than it was** before the stroke.
- Sources:
  - **Structures** (`IStructureFootprint`: lift terminals now, future buildings later):
    - inside the pad (catalog footprint + 5 ft, the existing `TerminalPad` rectangle): no change;
    - outside it: |h − pad level| ≤ 1.0 × the distance from the pad's edge (the 1:1 cone).
    - This also gives the shared footprint outline helper the code lacks (the same rectangle is recomputed in TerminalPad, LiftClearing and TrailEarthwork.Keep).
  - **Lift envelope:** h ≤ the envelope floor at (s, d) = rope − chair drop − required clearance (15 ft, with the low zones; `ClearanceAt`), over the chair band and the corridor. It's built from `LiftClearance.Rope`, `LiftRope.RopeAt`, `ChairDropMetres` and the `LineGround` chair band. On pads, the terminal rules govern.
  - **Tower bases:** ground at the base stays inside what the fixed head allows. Line towers can be 25–100 ft (trams 250), station towers 15 ft and up, with each kind's foundation reveal.

**2. Fixed heads (TW-02 v4):**
- A built lift's heads and rope come from the **ground before hand grading**.
- `DesignEarthwork` keeps the edits at the phase-2 boundary (derived and cached, never saved). The rope solve (`LiftClearance.Rope`, `LiftView`, the card's lowest chairs) reads that ground.
- **As-built** tower height = head − final ground, split into 5 ft steel steps plus a foundation reveal (0–5 ft) on every tower. Station towers already have a reveal (`LiftRope.cs` 0–1.523 m).
- `LiftView` draws each mast from the fixed head down to the final ground. The lift card shows as-built heights.
- Stored `HeightMm` is unchanged (the design height over the pre-brush ground), so **towers need no save change**.
- With no strokes, everything is byte-identical to today, so the existing lift goldens hold.

**3. Lifts and trails built after strokes** still solve on the pre-brush ground (fold order). Their tools must preview on that ground too, and I'll check New/Edit lift does. A stroke under a newer lift line gets clamped to its envelope on replay (deterministic).

**4. Risks:**
- Line towers may have no foundation piece that can show 0–5 ft in the tower kit. If so, I'll ask you before any model work (model builds run in their own thread).
- Trail Analysis ratings don't see later hand grading (the rating is on the trail's own grade). I'll note it as known.

## Engine (engine-free Domain)
- **New files:**
  - `TerrainStroke.cs` (`BrushKind`, `BrushSettings`, `TerrainStroke`, `TerrainStrokeId`);
  - `TerrainBrush.cs` (`BrushRun`, the dab maths plus the FixedGround clamp);
  - `BrushEarthwork.cs` (the phase-2 contributor in creation order; Reads = the dab boxes + 2, clipped to the core; per-stroke item cache);
  - `TerrainStrokeCommands.cs`.
- **Settings:** each stroke stores every effective number, so tuning a default never moves saved ground.
- **Integer-safe maths:** doubles only for + − × ÷ and sqrt. Weights are quantised to /4096, then it's long arithmetic with half-away rounding to whole mm.
- **Preview equals commit:** the live run uses the same `BrushRun` over `History.Terrain`, and Development builds assert the confirmed writes equal it.
- **Fold perf fix in `DesignEarthwork`**, same bytes:
  - an incremental ground view built from stale tiles only;
  - `TerrainEdits.Replace`, a linear merge;
  - a list of changed rectangles instead of one box;
  - plus the phase-boundary snapshot.
  
  P3-10 may be editing here. **Before I touch it, I'll ask you to relay to P3-10.** Whichever merges second adapts.
- **Document:**
  - `DesignDocument` Shape 4 gains `Strokes`, in ID order, carried through every copy-on-write path;
  - `IdMint.Stroke()`; `DesignIds` TerrainStrokeTag = 12;
  - `DesignRules.StrokeProblem` caps a stroke at 4,096 dabs;
  - `DesignBytes` writes the strokes after the lifts.

## Save v4 (0.3 §5)
- `SaveFile.CurrentVersion = 4`, with migration `UpToFour`: Shape 4, plus `Strokes = []`.
- `SaveJson` adds the strokes: dabs as delta-encoded triplets, read strictly.
- New fixture `TestData/formats/v4-save`; v1, v2 and v3 stay unedited.
- The design golden re-pins; approving this plan is the OK.
- I'll message the coordinator before the bump and before merging, and the PR will mention that P3-11's DesignBytes test may move. Terrain cache stays v12.

## Live ground and the tool (Presentation, App, UI)
- **`TerrainEditPipeline.Live.cs`:** `ApplyLive(preview, dirty)` sets heights for the frame's dirty rectangle only, every frame while held. Trees, splat and snow catch up at about 4 Hz, and the full `ApplyAsync` runs after Confirm.
  - Built lifts redraw their as-built masts live.
  - The pointer's surface reads the live ground.
  - Cancel restores `History.Terrain`.
- **Input:** `ToolInput` gains `RightClick`, `RightDrag` and `RightRelease`, with a second latch in `MountainViewer.ReadToolInput` and the scripted input paths updated. New `ViewCamera.RightButtonClaimed` makes the camera skip right-drag pan; the host sets it and clears it in Disarm.
- **Tool:** `UI/Tools/TerrainBrushTool*.cs` (`IDesignTool`). Painting follows `TrailTool.TickPaint`'s stamp pattern. The cursor is the trail brush's ground-drawn cursor, or `Preview.Ring`.
- **The review:** uses the ReviewModel terrain slot plus the towers and held rows.
- **HUD:**
  - `toolbox-tab-terrain` in Hud.uxml;
  - `ToolboxTabs` and `Tools` rows (`MountainHud.Panels.cs`), plus `ToolFor`/`ToolAction`/`ToolHint` (`MountainHud.Tools.cs`);
  - `ToolCatalog` entries;
  - an `s-terrain` glyph.
  - I'll check five tabs fit the tray.
- **ToolWindow:** gains a mouse-hint line and a chip row, if `AddSwitch` doesn't already cover the chips.

## Budgets
- **While dragging:** every frame, live ≤8 ms at a 60 ft brush and ≤16 ms at 400 ft (p95). The FixedGround band is built once per session per tile, so dabs stay cheap. No garbage per frame.
- **Confirm** ≤250 ms p95; idle 0 B.
- **Stress:** 500 strokes with a trail edited under them.
- **Measured** with `-brushbench` (player, Mono, `-force-gfx-mt`) and demo 41 before and after.

## Order and PRs
1. **Mockup:** ui-layout.html gets the Terrain tab, its four tiles, the shared pane in four states, and the Break ground review with tower and held rows. Captures in both themes go to you for your OK.
2. **PR A, engine and save v4:** strokes, BrushRun, FixedGround, the contributor, the Fold fix, the migration and fixture.
3. **PR B, fixed-head lifts:** the phase-boundary ground, the rope from it, as-built heights and foundation reveals, LiftView masts and the card.
4. **PR C, the tool:** the tool, live ground, review, HUD tab, keys and demo 73.

I'll ask you before each push, PR and merge.

## Docs
- `docs/plans/p3-15-plan.md`.
- Task 15 in `phase2-14-phase3-plan.md`: the row, the graph (after 08 and 09, **before 12**) and a §5 decision row.
- In `p3-02-design-rules.md`: a new §4.6 with TB-01… (the brushes, the session, the limits, the 1:1 into structures, the envelope, tower holds), plus TW-02 v4 (fixed heads).
- The rulings go in `phase0-decisions.md` and in memory `p3-15-owner-decisions.md` with a line in MEMORY.md.
- "Restore to natural" stays Later; so do money figures (Phase 4).

## Verification
- **Core tests:**
  - **The brushes:** each brush's maths (slope: zero cross slope, ≤1 mm second difference along the path); the limits, core and clearing at 305 mm but not 304.
  - **Fixed ground:** no change inside a pad, the 1:1 cone, never above the envelope floor (checked with `LiftClearance.Failing`, which stays empty), tower holds at 100/25/15 ft, and "never worse than before".
  - **Fixed heads:** the head is unchanged under raise and lower; as-built height = head − ground; with no strokes, the bytes equal today's.
  - **Determinism and undo:** a stroke golden; chunked versus one-shot versus replay; 30 strokes undone and redone come back byte-identical.
  - **Formats:** the Fold fix keeps the existing goldens; v1/v2/v3→v4 migrations, the v4 fixture and FormatFreeze; the same golden in dotnet and EditMode.
- **EditMode:** the tool contract and live-upload tests with `-force-gfx-mt`.
- **PlayMode:** keyboard and mouse at 50, 100 and 150%; switching tools cancels and restores; the right button pans again after Disarm.
- **Also:** the full `dotnet test tools/domain-tests/Tests` (count reported), repo checks and `compile-check.ps1`. I'll merge origin/main before each push.
- **A second site:** a cat track to a terminal and a junction under a lift line on another Jackson Hole spot.
- **For your phone:** captures and a short movie per brush (`-brushmovie`), including towers growing under Lower and the brush held at the envelope.
- **demo.bat:** 73, 73b (`-brushbench`), 73m (movie).
