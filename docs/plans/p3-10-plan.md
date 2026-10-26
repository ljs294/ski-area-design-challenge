# P3-10 plan: paths, junctions and the network

**Status:** approved by the owner on 2026-10-25 ("Go. Yes to all nine recommendations."): decisions NT1–NT9 in
[phase0-decisions](phase0-decisions.md). Branch `feature/p3-10-network`, `demo.bat` 70.

**Spec:** [Phase 3 plan](phase2-14-phase3-plan.md) row 10; rules PA, JN and NW in
[p3-02-design-rules.md](p3-02-design-rules.md) §6–§7.

## 1. Scope

- Automatic paths (PA-05 to PA-08), derived junctions (NW-07), Split and Join in the game (NW-10).
- The derived network (NW-01, NW-02), connectivity (NW-03) and the warnings (NW-12).
- The status bar's lift and trail counts (NW-04, NT5), Analysis › Network (NW-13, NT4), mockup first.
- Map label collisions for lift and trail names (from the #87 mockup backlog, NT7).
- **No save or format change.** The network is derived and never stored.

Starting point: `DesignNetwork.Build` and the Split/Join commands exist (P3-03), but nothing implemented
`ITrailGeometry`, so each trail was one straight edge; and the earthwork graded trail by trail, so a split would have
regraded the ground at the cut.

## 2. Files

| New | Edited (small, additive) |
|---|---|
| `Runtime/Domain/Design/Network/**` (geometry, reach, warnings, segment ratings) | `DesignNetwork.cs` (the null-geometry graph and its golden unchanged) |
| `Tests/Core/Network*Tests.cs` | `TrailEarthwork.cs` (split pairs graded as one, NT2) |
| `App/MountainViewer.Network.cs` | `TrailTool.cs` (made partial; Split and Join buttons) |
| `UI/Hud/MountainHud.Network.cs` | `MountainHud.Bar.cs` / `.Panels.cs` (counts, the tab) |
| `UI/Tools/TrailTool.Network.cs` | `LandmarkLabelOverlay.cs` (one hook) |
| `Presentation/Design/NetworkOverlay.cs` | `prototypes/ui-layout.html` (Analysis › Network, bar counts) |
| `UI/LabelPlacement.cs` | `demo.bat` 70 |

## 3. Approach

- **Geometry (`TrailNetworkGeometry : ITrailGeometry`).** Each built trail's footprint and centreline come from the trail
  alone (the same `TrailFootprint` and `TrailCentreline` code the earthwork runs, so a load and an edit agree), cached
  by the immutable `Trail` object. Pairs are tested only when their boxes come within 30 ft.
  - **Junctions (NW-07):** an end within 30 ft of another trail's centreline joins it at the nearest point (TR-05); each
    separate overlap patch of two footprints gives one junction, on each centreline nearest the patch's middle.
  - **Paths (PA-05):** where two footprints come within 30 ft without touching, one path at the closest point of each
    close stretch. **Direction (PA-06)** from the natural (package) ground, so it never races the grading; within
    3 ft both ways. **PA-07:** a path never crosses a lift terminal's footprint, a lake or a road (blockers passed in).
- **Graph:** `DesignNetwork.Build` as P3-03, with junctions and paths from the geometry. IDs are keyed hashes (JN-04).
  Deleting a trail drops only what it joined: everything is rebuilt.
- **Reach (NW-03):** from each built lift's top, follow trail segments and paths (downhill edges only); connected when
  it reaches a lift bottom.
- **Warnings (NW-12):** "No way down" (lift), "No lift serves this" (trail not reached from any lift top), "Leads nowhere"
  (the tail joins nothing, NT6). They never block Break ground.
- **Segment ratings (NW-08, NT9):** each segment rated on its own stations (TD-02); tested, not shown.
- **Split and Join (NW-10, NT1–NT3):** buttons in Edit trail. Split: hover shows the cut across the centreline,
  snapping to junctions; click = one `SplitTrail`. Join: the trails meeting this one end to end light up; click = one
  `JoinTrails`. A split pair is graded as one centreline, so Split never changes the ground.
- **The game:** the network rebuilds off the main thread after each commit (stamped with the design revision); the review
  checks the draft's pairs only. Measured with the edit-timing tools against ≤100 ms preview and ≤250 ms commit.
- **Analysis › Network (NT4):** a lift list (status, trails reachable), then trails with warnings; picking a lift
  highlights what its top reaches, with dashed path links (PA-08) and junction dots.
- **Labels (NT7):** greedy placement by priority (lifts, then trails by length, then ID); a trail name tries a few spots
  along its line before it hides; re-placed only after the camera moves.

## 4. Tests

Every test name cites its rule. The network golden: the same design gives the same graph bytes from commands and from a
save, in any order. Pruning on delete; a test resort with a disconnected lift; each warning; path directions; split then
join restores the trail; Split leaves the ground byte-identical. Existing design and graph goldens unchanged. Full
`dotnet test` with LFS, EditMode, PlayMode (`-force-gfx-mt` for the overlay), repo checks, compile-check.
