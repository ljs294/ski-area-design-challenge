# Phase 3 · 06 Toolbox and placement: plan

**Status:** approved by the owner on 2026-10-16 ("Go. Yes to all eight recommendations."), so every §8 recommendation is a ruling. **Branch:** `feature/p3-06-toolbox` from origin/main 3f05e1b. **Demo:** `demo.bat` 64.
**Spec:** [Phase 3 plan](phase2-14-phase3-plan.md) row 06. **Rules:** SH-07, CF-01, CF-02, CF-06 to CF-08, LF-01, LF-09
([design rules](p3-02-design-rules.md)). **Inputs:** task 03's `Domain.Design` ([p3-03 plan](p3-03-plan.md), "For tasks 04–10").

## 1. Scope

Task 06 builds the frame every construction tool runs in, and enough of the New lift and New trail tools to prove it
end to end: arm, pick, snap, preview, review, confirm into `DesignHistory`. Tasks 08 and 09 then fill in each tool's
real behaviour (grading, towers, rope, profiles) behind the same contract.

**In:**
- **One active tool at a time** (CF-01), a `ToolHost` that arms, disarms and routes input.
- **Terrain picking:** the pointer's ground point, reusing the cursor readout's ray (`ViewCamera.GroundAlong`).
- **Snapping** to lift terminals and trail heads and tails within 30 ft (TR-05). Snapping to any point on another
  trail's centreline plugs in once task 08's `ITrailGeometry` exists.
- **Pending preview:** a ghost layer for the tool's lines, dots and brush ring, cleared on cancel and on commit. The
  yellow contours (CF-08) get a slot here; task 05 and task 08 fill it.
- **The tool window and its review step:** steps, options, stats, warnings, then **Break ground**, **Save plan**, or
  **✕** (asks again if anything is drawn) (CF-02, CF-06, CF-07).
- **A placeholder drawing of committed work:** each lift as a line with a dot at each end, each trail's outline;
  orange for plans. Tasks 08 and 09 replace it.
- **Undo and redo of committed work** from the keyboard (see Q5).
- **Keys:** tool letters inside the open tray, the tools' own keys, all rebindable (SH-07).

**New lift in 06:** steps 1 and 2 (the bottom terminal, then the top; the line; LF-08's "top must be higher" and
300 ft minimum). Step 3, Towers, shows the Automatic/By hand switch (LF-01) but places no towers; task 09 fills it.
Review shows length, rise, grade and ride time. Break ground submits `AddLift` with no towers; the domain allows it.

**New trail in 06:** step 1 paints strokes (left-drag), Shift-drag erases, `[`/`]` set the width, Ctrl+Z undoes a
stroke. Step 2 takes head and tail clicks, which snap. Review shows the painted area and the head-to-tail drop. Break
ground submits `AddTrail`. Grading, the rating and the profile belong to task 08.

**Out:** grading, clearing and terrain changes (05, 08); towers, rope and chairs (09); the network and its warnings
(10); Edit and Remove tools (Q2); Save (04); snowmaking and infrastructure tools (Phase 4).

## 2. The tool contract (for 08, 09, 10)

New files under `Runtime/UI/Tools/` (the UI assembly, which can see Presentation, World and Domain):

```csharp
public interface IDesignTool
{
    string Id { get; }                         // "new-lift", matches the tray tile and the mockup's data-ui
    string Name { get; }                       // "New lift"
    IReadOnlyList<string> Steps { get; }       // "First terminal", "Other terminal", "Towers", "Review"
    int Step { get; }
    bool HasWork { get; }                      // anything drawn: ✕ and switching ask or warn
    void Arm(ToolContext context);             // history, picker, snapper, preview, window, keys
    void Tick(in ToolInput input);             // once per frame; allocates nothing
    BackOut Escape();                          // CF-07: Stepped, Closed or Ask
    ReviewModel Review { get; }                // rows, warnings, which commit buttons are live
    DesignCommand Commit(WorkStage stage, long baseRevision);   // Built = Break ground, Plan = Save plan
    void Disarm();                             // cleanup; the host calls it exactly once
}
```

- **`ToolInput`** (a struct, filled by the host each frame): the snapped ground point and what it snapped to, the
  pointer's screen point, whether the pointer is over a panel, the buttons (pressed, held, released, dragging), and the
  tool keys pressed this frame.
- **`ToolContext`:**
  - `DesignHistory`;
  - `TerrainPick` (screen to ground);
  - `DesignSnap` (snap targets);
  - `PendingPreview` (ghost lines, dots, rings; the contour slot);
  - `ToolWindow` (steps, option rows, the review step);
  - `ClaimKeys(...)`, which keeps camera keys a tool is using (Page Up and Page Down while a tower is in hand, LF-09)
    away from the camera;
  - `Analyse<T>(work, done)`: off the main thread, stamped with `history.Stamp(...)` and dropped if stale (CF-03).
- **`ReviewModel`:**
  - **Rows:** label, value, rule ID (the rule ID shows on hover, as in the mockup).
  - **Warnings:** text, and whether each one blocks Break ground.
  - **Two reserved slots:**
    - **Terrain:** cut, fill and balance; trees cleared; the ungraded length. Task 05/08 fills it.
    - **Network:** the NW-12 warnings. Task 10 fills it.
  - **Flags:** whether Break ground and Save plan are live.
- **`ToolHost`:**
  - **Choose:** choosing a tool disarms the current one at once (CF-01) and restores the cursor, the camera keys and
    the preview.
  - **Confirm:** Confirm calls `Commit`, then `DesignHistory.Submit`. One confirmation per tick (CF-04).
  - **Stale or rejected commands:** the tool stays in review with the reason shown.
  - **On accept:** the tool resets to step 1, ready for the next lift or trail. This is CS2's habit; see Q4.
- **Register a tool** with one line in a `ToolCatalog`. The tray's tile, letter and hint come from it, so 08, 09 and
  10 never edit the HUD files.

**`DesignSnap`** is engine-free, in `Domain/Design/DesignSnap.cs`, and tested in `Tests/Core`. Its targets are lift
tops and bottoms, trail heads and tails, and an optional centreline hook from `ITrailGeometry`. It takes the nearest
target within 30 ft, ties broken by ID (SH-04).

## 3. Input and picking

- **Picking:** `TerrainPick` (Presentation) turns the screen point into a ray, then calls `ViewCamera.GroundAlong` and
  converts the hit to package metres and then `PointMm`.
  - It runs only while a tool is armed, and only when the pointer moved or the camera moved.
  - It is skipped over panels (`PanelPointer`).
  - It allocates nothing.
- **Left mouse belongs to the tool.** Right-drag pan, middle-drag orbit and the wheel stay with the camera (TF-02). A
  left click on the map with no tool armed keeps today's `Hud.ClickedMap`.
- **Key order each frame:**
  1. the flow;
  2. Esc;
  3. the active tool's claimed keys;
  4. the tray letters;
  5. the HUD keys;
  6. the camera.
- **Esc (CF-07):**
  - It backs out one tool step. From step 1 it closes the tool, asking first if anything is drawn. Then it closes the
    tray.
  - It is inserted into `MountainViewer`'s existing chain after modals and before `Hud.BackOut`.
- **The tray rule stays as the owner set it (2026-10-01; see Q3):**
  - **Tray open:** letters go to the tools. The camera keeps the arrows, + and −, Home and the mouse. Page Up and Page
    Down stay with the camera unless a tool claims them.
  - **Hooks:** `LettersToTools` follows `Hud.ToolboxOpen`, replacing the forced `false` in `MountainViewer`.

## 4. The tool window and review panel (mockup first)

- **The mockup is the starting point.** It is the lift-mockup thread's: `docs/p3-lift-mockup`, 3 commits, lift and
  trail tools, nothing uncommitted.
- **The window:** a `HudWindow`, the same `.mp-win` plate as every other window, solid per the linear-blending note:
  - a numbered steps row (`.lsteps`, the ✓ for done steps);
  - option rows (type, the Towers switch, brush width, Graded or Natural);
  - the step's content;
  - the review rows and warnings;
  - **Break ground** (`.mp-go`) and **Save plan** (`.mp-ghost`).
- **The review step:** it is part of the same window, not a separate panel (the mockup does it this way), so the
  window doesn't jump.
- **Profile strip:** a reserved slot above the status bar for LF-10 and TF-04. It is empty in 06; 08 and 09 draw into
  it.
- **Changes I'd make to the mockup before porting (yours to approve):**
  - no money figure on Break ground (there's no economy in Phase 3; P3-02 ruled no grading cost);
  - the two reserved review slots shown greyed ("Grading comes with task 08", "Network checks come with task 10");
  - key captions on the steps and buttons drawn from the key map;
  - the Path decision (Q1).
- **Parity:** new `data-ui` names (`toolwin`, `toolwin-step-N`, `review-row-*`, `review-go`, `review-plan`) and
  `tools/ui-parity` states `s6-lift-*` and `s6-trail-*` at 50, 100 and 150%. Captures in both themes go through the
  render lane.

## 5. Keys (all rebindable; defaults go in `controls-key-map.md`)

| Action | Default | Scope |
|---|---|---|
| New / Edit / Remove (tray letters) | N / E / X | the tray, only while it's open (new `Tray` context, so they never swap with WASD) |
| Next step | Enter | while a tool is armed (TF-09); Enter leaves the reserved list for this one action |
| Break ground | Ctrl+Enter | in review |
| Brush narrower / wider | [ / ] | the trail tool (photo mode keeps its own [ ] in the Photo context) |
| Erase while painting | Shift (held) | the trail tool |
| Undo a stroke | Ctrl+Z | while painting |
| Tower up / down 5 ft | Page Up / Page Down | a tower in hand or selected (LF-09; 09 uses it, 06 wires the claim) |
| Delete selected | Delete | tools with a selection |
| Undo / Redo (committed work) | Ctrl+Z / Ctrl+Y | no tool armed (Q5) |
| Back out | Esc | fixed, not rebindable (as today) |

## 6. Files

**New:**
- `Runtime/UI/Tools/**`: the contract, `ToolHost`, `ToolCatalog`, `ToolWindow`, `ReviewModel`, `NewLiftTool`, and
  `NewTrailTool`;
- `Runtime/Presentation/Design/**`: `TerrainPick`, `PendingPreview`, `DesignOverlay` (the placeholder drawing of
  committed work);
- `Runtime/Domain/Design/DesignSnap.cs`;
- `MountainHud.Tools.cs` (partial);
- `Art/UI/HudTools.uss`;
- tests;
- this plan.

**Small edits:**
- `KeyBindings` (new actions, the `Tray` context, Enter);
- `ViewCamera` (`ClaimKeys`);
- `MountainViewer` (the key order, Esc, left click, `LettersToTools`);
- `MountainHud.Panels.cs` (tiles come from `ToolCatalog`);
- `Hud.uxml` (the window and strip slots);
- `UiKeyboardTests` (`LettersToTools` now true);
- `controls-key-map.md`;
- `tools/ui-parity` states;
- `demo.bat` 64;
- the Toolbox region of `ui-layout.html` (Q6).

**Not touched:** Persistence, formats, cache, Domain.Design's document shape, task 05's grading files.

## 7. Tests and demo

**PlayMode (`ToolboxTests`, at 50, 100 and 150%, with a virtual keyboard and mouse):**
- T opens the tray and N arms New lift. Two clicks on the terrain go to Review, and Ctrl+Enter submits. The document
  then has one lift, and the revision moves on. Ctrl+Z removes it.
- The same for a trail: drag, Shift-drag erase, `[ ]`, Enter, head and tail snapping to the lift's ends, Break ground.
- Switching tools mid-draw: the old tool is disarmed exactly once, its preview is gone, and the camera keys are back.
- The arrows move the camera while a tool is armed. Page Up doesn't zoom while a tool claims it.
- Rebinding N to M arms the tool with M, and the caption updates.
- The panel is reachable by keyboard alone: Tab or arrows within the window, then Enter on Break ground.

**EditMode and Core:**
- `DesignSnap` (TR-05; nearest and ties; nothing beyond 30 ft);
- the `ToolHost` state machine with a fake tool (CF-01, CF-04, CF-07);
- `KeyBindings`: the Tray context never swaps with the camera, and the Enter rule;
- an armed, idle tool allocates 0 B per frame (CF-09);
- each test cites its rule ID.

**Demo 64:** open the bundled demo, T, then New lift and New trail. It prints the steps and the keys.

**Before the PR:** merge origin/main, EditMode and PlayMode tests, `dotnet test`, repo checks, `compile-check.ps1`, and
captures in both themes.

## 8. Questions (each with my recommendation)

1. **The "Path tool" in the spec.** P3-02 retired drawn paths: paths are automatic (PA-05, DR27). The mockup has no
   Path tool either. **Recommend:** no Path tool. Task 10's Split and Join (NW-10) become Trails-tab tools on the same
   contract.
2. **Edit and Remove tiles.** **Recommend:** they stay greyed in 06. Task 09 makes Edit lift and Remove lift real, and
   task 08 does the same for trails, since both need the built-object rendering that 08 and 09 create.
3. **Tray letters against camera letters.** Today's rule gives every letter to the tools while the tray is open, so
   WASD, Q/E and R/F wait; the camera keeps the arrows, + and −, and the mouse. The alternative claims only the tab's
   own letters (N, E, X), but E is also rotate right, so rotation goes lopsided. **Recommend:** keep the owner's rule.
   The acceptance test's "camera keys" are the arrows, + and − and Home.
4. **After Break ground.** **Recommend:** the tool stays armed at step 1, ready for the next lift or trail, as in CS2.
   Esc or T closes it.
5. **Undo and redo of committed work** (Ctrl+Z / Ctrl+Y) with no tool armed. It isn't in any task's list, but it's
   the cheapest proof that confirm goes into `DesignHistory`. Heights follow once task 05 lands. **Recommend:** yes,
   in 06.
6. **The mockup.** The lift-mockup thread's branch has the lift and trail tools and is idle. **Recommend:**
   - it opens its own docs PR and merges first;
   - I then own the Toolbox region of `ui-layout.html`;
   - my small changes (§4) go in my branch first, with screenshots for you, before porting.

   The alternative is that I take its branch over.
7. **"Works by keyboard."** Picking a point on the mountain needs the pointer. **Recommend:**
   - every step, option and button works by key, and the terrain clicks use the mouse;
   - the PlayMode test drives both together.

   The alternative is a keyboard crosshair for placing points, which is extra work for little use.
8. **Switching tools with work drawn.** CF-01 says the current tool cancels at once, and CF-07 asks only on Esc.
   **Recommend:** follow the rules, with no question, but show a one-line notice ("New trail discarded") so it's never
   silent.

## 9. As built (notes for tasks 04, 05 and 08–10)

- **Design coordinates.** A `PointMm` is the area's **local frame** (x east, z north, metres from `LocalFrame.Origin`)
  × 1,000. Absolute EPSG:6350 metres would overflow the document's ±1,000 km range. Convert with
  `Presentation.Design.DesignFrame`. The origin comes from the package (the site's centre), so a save needs nothing more.
- **The design.** The tools submit to `MountainViewer.Design`, which is P3-04's `OpenDesign.Current.History` (the open
  save). Only a game started straight into an area without the app flow (`-package`, captures) has a session-only
  history instead. The placeholder overlay follows whichever is open.
- **Adding a tool (tasks 08–10).**
  - Implement `UI.Tools.IDesignTool`, then add one `ToolCatalog` line. The tray tile, its letter and the hint follow
    from that.
  - Draw through `ToolContext.Preview` (`IToolPreview`: numbered slots, no allocation).
  - Run heavy work through `ToolContext.Analyse` (stamped; results come back on the host's tick).
  - Fill the review with `ReviewModel`, including its reserved `terrain` and `network` slots.
  - Claim keys with `ToolContext.Claim`.
- **Snapping.**
  - `Domain.Design.DesignSnap.Nearest` finds lift ends and trail ends within 30 ft (TR-05).
  - **Task 08** plugs centreline snapping in through `ToolContext.Lines` (`ICentrelineSnap`).
- **The placeholder drawing.**
  - `Presentation.Design.DesignOverlay` draws committed lifts and trails as plain lines, with plans in orange.
  - **Tasks 08 and 09** replace it with the real trails and lifts.
  - `PendingPreview.Cleared` is the hook for the yellow pending contours (CF-08).
- **Keys.**
  - The new `GameAction`s are ToolNew, ToolEdit, ToolRemove, ToolNext, BreakGround, BrushNarrower, BrushWider, Erase,
    UndoStroke, TowerUp, TowerDown, DeleteSelected, ToolWindowKeys, Undo and Redo.
  - There's a new `KeyBindings.Context.Tray`.
  - `KeyBindings.Claim` lets an armed tool's key shadow any other action on the same key.
- **The profile strip.** `tool-strip` in Hud.uxml is the reserved slot for LF-10 and TF-04.

## 10. Owner rulings, round 2 (2026-10-17)

The lift-mockup thread handed over five questions; the owner said yes to all five recommendations.
1. **Enter goes to Review** from the lift's Towers step. It's the same Next step key the trail uses (TF-09).
2. **Moving a built lift's top terminal or changing its type** is task 09's, as additive commands with Edit lift.
3. **Work in progress stays in the tool** until Break ground or Save plan: nothing half-drawn goes in the document or
   the save.
4. **Snapping reaches 30 ft or 6 px on screen, whichever is larger,** so it still works zoomed far out. The snapped end
   lands exactly on its target, so NW-07's 30 ft attachment still holds.
5. **The tower in hand's clearance test** (the mockup's 200 ft or half a span, RP-01) is task 09's, with the real rope.
