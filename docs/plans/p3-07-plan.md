# Phase 3 · 07 Style tile: one trail and one lift (plan)

**Audience:** the project owner and coding agents. **Status:** approved by the owner on 2026-10-17, with changes
(decisions SK1–SK15 in [phase0-decisions.md](phase0-decisions.md#phase-3-task-07-style-tile-decided-2026-10-17)).
**Task:** [Phase 3 plan](phase2-14-phase3-plan.md) row 07 ⛳. **Branch:** `feature/p3-07-style-tile`. **Demo:** `demo.bat`
65, 65s, 65c, 65b.

## 1. Scope

On Jackson Hole 5 km, built by command with no tool UI (`-styletile`): one graded, cleared trail (groomed snow, cut and
fill faces, the edge against the forest) and one Sessellift FGQ-4 with towers, rope and chairs; captures from the
benchmark views and close views, summer and winter, day and night; the benchmark within 10% of P3-01's baselines.
Everything goes through task 03's commands and task 05's edit pipeline, as tasks 08 and 09's tools will, and the design
saves and loads through save v1 unchanged.

**Owned files:**
- Domain: `Design/TrailGrader.cs` (GR-01 to GR-08, GR-12, CL-01, CL-02), `Design/LiftRope.cs` (LC-02, RP-03, RP-05,
  TW-01, a simple TW-04, TW-14 v2, TW-16), `Design/LiftClearing.cs` (CL-03, CL-09), a `DesignKey` tag;
- Presentation: `Design/TrailSurfaceMap.cs`, `Lifts/LiftView.cs`, `Art/Shaders/LiftCable.shader`, the trail layer of
  `MountainTerrain.shader`, `DesignOverlay.ShowBuilt`;
- App: `StyleTile/StyleTile.cs`, `MountainViewer.Built.cs`, the `-styletile` and `-season summer` flags, the style tile
  movie route;
- the leaned tower bases in `tools/assets/lifts` (tower.py `base_l025`–`base_l150`, the `lean_photos` shot);
- `tools/style-tile/captures.ps1`, `demo.bat` 65, tests in `Tests/Core/StyleTileTests.cs`, this plan, the report and
  the decisions.

**Left to tasks 08 and 09:** painting and the automatic centreline, RP-02's tangent solve and TW-04's per-sheave
forces, live tower placement (TW-12), the tools, the Trails and Lifts map layers, the terminal elevation and ramps (SK14).

## 2. The owner's answers (2026-10-17)

| # | Question | Answer |
|---|---|---|
| 1 | The grader in Domain, extended by 08 | Yes; Graded or Natural stays the player's choice per trail |
| 2 | Rounded tie-ins (GR-12) if the captures need them | Yes (they did: 10 ft, tuned at 07) |
| 3 | A runtime trail map for the look | Yes; **corduroy tighter than 1 ft** (about 1 in, plus groomer-pass seams) |
| 4 | `-season summer` | Yes |
| 5 | CL-02's ragged edge and CL-03's corridor now | Yes |
| 6 | A simple rope | "Should be a cable, but it can be very very simple" |
| 7 | Plumb towers only | **No: leaned towers are needed** for hold-downs and breakovers (SK7, SK13: lean where needed; the foundation in line with the mast) |
| 8 | LC-02's tuned values | Yes |
| 9 | #87's stand-ins | Yes |
| 10 | Site shown first | Yes |
| 11 | Built work as models; the overlay draws plans | Yes |
| 12 | A review movie | Yes, plus screenshots and the style tile live in the game |

Raised by the owner while building: terminals need clear space (CL-09) and adjustable elevation and ramps (SK14, task
09); terminals are heads, with a station breakover at each end (TW-16, SK15); a too-steep bottom terminal stands higher
rather than moving.
