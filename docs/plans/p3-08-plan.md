# Phase 3 · 08 Trails: plan

**Status:** 🟦 for the owner's go (E15). **Branch:** `feature/p3-08-trails` from origin/main 738d5b4. **demo.bat:** 67.
**Rules:** [p3-02-design-rules.md](p3-02-design-rules.md) TR, TD, GR (GR-11 v2, GR-12 v2), TF (TF-11 v2), CL-01,
CL-02, CL-08; decisions SK1–SK30. **Row:** [phase2-14-phase3-plan.md](phase2-14-phase3-plan.md) §3 row 08.

## 1. Scope

In: painting on the 1 m grid, islands, one-piece check, TD-06 estimate; the automatic centreline (TR-03); grading on
the painted footprint (GR-01 to GR-12); red ungraded stretches; cut, fill and balance; clearing (CL-01, CL-02, CL-08);
the yellow pending contours; the rating and symbol (TD-01 to TD-04); the profile strip (TF-04); review (TF-05); Edit
and Remove trail, and undo (TF-07); the **Trails** map layer; **Analysis › Trails** with filters and focus cards.

Out: lifts, their clearing and trench (P3-09); paths, junction tools, Split and Join tools, NW-12 warnings (P3-10); any
save or format change; glades, zones, Thin clearing (later phases).

## 2. Files

| Area | Files |
|---|---|
| Domain (engine-free) | new `Domain/Design/TrailFootprint.cs`, `TrailCentreline.cs`, `TrailRating.cs`, `TrailAnalysis.cs`, `DesignEarthwork.cs`; `TrailGrader.cs` extended |
| UI tools | `UI/Tools/NewTrailTool.cs`, new `EditTrailTool.cs`, `RemoveTrailTool.cs`, `TrailProfileStrip.cs`; `FootprintRaster.cs` retired into the Domain footprint; one line each in `ToolCatalog` |
| Presentation | new `Presentation/Design/TrailMapView.cs` (the Trails layer), `TrailSurfaceMap.cs` (real centreline); `DesignOverlay.cs` trail part only |
| HUD | new partials `UI/Hud/MountainHud.Trails.cs` (Trails tab, layer row, focus card), shared `UI/Hud/AnalysisList.cs` (Q9) |
| Mockup | the Trails parts of `prototypes/ui-layout.html` |
| Tests, data | `Tests/Core/Trail*Tests.cs`, `Tests/PlayMode/Trail*Tests.cs`, `TestData/grading/**` |
| Docs | this plan, `phase0-decisions.md` (P3-08 section), demo.bat 67 / 67b |

## 3. Brush and paint

- The footprint becomes a Domain type on the **edit lattice's 1 m cells** (cell centre test, TR-02), replacing P3-06's
  5 ft UI raster, so painting, grading, clearing and the rating read one footprint. Strokes stamp in order (paint sets,
  erase clears), parts union, cuts clip. Bit grid over its bounding box; deterministic.
- Islands are the holes; the one-piece check stays (TF-02). The distance-to-edge field (exact Euclidean transform,
  islands counting as edge) is built once per footprint and feeds the centreline, grading and the ragged edge.
- After each stroke: area and the TD-06 estimate from a running slope histogram over the touched cells.

## 4. Centreline (TR-03)

- Cost path from head to tail over the footprint's cells (8-connected Dijkstra, ties by cell index): step length ×
  (1 + (W / clearance)²), where clearance is the distance to the nearest edge or island. It hugs the middle and goes
  round islands; W scales with the trail's own widest clearance, so it behaves the same at 25 ft and 400 ft.
- Then smoothed (moving average over about one brush width, pulled back inside wherever smoothing would leave the
  footprint), resampled to 25 ft stations (≤800).
- An end snapped outside the footprint (a lift end within 30 ft) joins the nearest footprint cell by a straight lead-in.
- `ICentrelineSnap` (P3-06's hook) is implemented from it, so later ends snap onto trails.

## 5. Grading

Builds on P3-07's `TrailGrader` (GR-12 v2's rounding, the 1:1 faces, the ragged edge), generalised from "centreline ±
half width" to the painted footprint:
- **GR-03 per side:** each station's budget on each side is the distance along the section to the footprint's edge or
  an island, so mixed widths and lopsided paint work.
- **Edge envelope (proposed GR-13):** after the sections, every cell is clamped between the 1:1 cones raised from the
  natural ground at the footprint's boundary (edges, islands, earlier trails, the 15 ft rounds of TF-11). Two chamfer
  passes on the 1 m grid. It guarantees the faces meet the ground at every edge, never a step, for any shape a player
  paints (fixes-must-be-general). Cells outside the footprint are never written (GR-06).
- GR-07: stations without a 25 ft bench stay natural, red on the map and profile; review shows the ungraded length.
- GR-08: cut, fill, balance summed over the 1 m design-minus-ground raster, shown in yd³.
- TF-06: Natural trails only clear.
- TF-10: overlap cells keep the earlier trail's ground; the new trail treats them as fixed boundary.
- TF-11: no change within 15 ft of a built tower base or terminal; no fill in a lift's envelope (from `LiftBuild` on
  main). The chair-clearance block waits on P3-09 (Q5).
- **Edit and delete (Q3):** the touched tiles are rebuilt by replaying the built work that reaches them, in order:
  trails by ID, then lifts' earthwork through a hook P3-09 fills. Undo stays DesignHistory's exact restore.
- `ToolContext.PendingTerrain` carries the full set; ToolHost's Break ground submits it.

## 6. Clearing

CL-01 over the footprint's cells; CL-02's 10 ft keyed-hash band measured from the distance field, so it also rings
islands; CL-08 islands keep their trees. Natural trails clear too. Remove regrows exactly (CL-07).

## 7. Preview, rating, profile, review

- Analysis off the main thread (CF-02, CF-03), stale results dropped. Yellow pending contours (PendingContours),
  dashed centreline, red ungraded stretches.
- Rating TD-01/TD-02 on the graded surface's fall-line slope, per-station median; TD-03 bands and symbol.
- **Profile strip (TF-04)** above the status bar while building and editing, ported from the mockup: ground dashed,
  graded solid, band colours, red stretches, the steepest 75 ft window.
- Review (TF-05) fills ReviewModel's **terrain** slot: cut, fill, balance, ungraded length, trees cleared.

## 8. Map layer and Analysis (mockup first)

- **Trails map layer:** trail-map lines along the centreline in the rating's colour, name centred, symbol at the
  head; plans orange; hover or focus adds the white casing. The graded ground itself stays when the layer is off.
  Replaces DesignOverlay's trail drawing.
- **Analysis › Trails:** the mockup's table: Trail (symbol + name), Length, Vertical, Grading, Status; Filter button
  with spreadsheet column menus, sortable; hover lights the trail; click opens the focus card (TF-08: name, rating,
  length, vertical, mean and steepest grade) with Edit and Remove.
- Mockup changes first (Q11), owner approval, port, parity (demo 48), captures in both themes, keyboard at 50–150%.

## 9. Tests and fixtures

- Rule tests citing IDs on analytic ground: GR01 level across, GR02 1:1 faces, GR03 budget × (1 − m) on a uniform
  sidehill, GR04/GR07 too steep stays natural, GR05 blend, **GR06 heights outside byte-identical**, GR08 volumes to
  0.1%, GR10 1 mm, GR12 rounding inside; TR02 islands and one piece, TR03 centreline inside and round islands, TR04;
  TD01–TD03, TD06; CL01, CL02, CL08; TF06, TF10, TF11; undo exact; edit and delete restore.
- **Recorded fixtures** (TestData/grading, LFS, about 0.3 MB each): a sidehill, a ridge, a gully and a too-steep face,
  cropped from the committed Jackson Hole 2 km package, each with its painted trail as JSON and golden SHA-256 heights
  plus spot heights.
- PlayMode: paint, ends, review, Break ground, Edit, Remove, undo, by keyboard and mouse at 50% and 150%; stale result
  dropped; Analysis rows and filters.
- Timing: `-trailbench` (demo 67b) paints a 1 km, 100 ft run and a 1 km mixed-width run with islands, preview p95 ≤100 ms,
  commit p95 ≤250 ms. 0 B per frame idle. Frame benchmark (demo 41, measure lane, PC idle) before and after.
- Full `dotnet test` with LFS (E2), EditMode, PlayMode, repo checks, compile check.

## 10. Timing

| Step | What | Owner checkpoint |
|---|---|---|
| A | Domain: footprint, centreline, grading, envelope, rating, clearing, earthwork rebuild, fixtures and tests | — |
| B | New trail tool wired: preview, red, review, profile; PlayMode tests | captures on two sites (JH 5 km and 2 km) |
| C | Mockup update | your OK |
| D | Edit, Remove, undo; Trails layer; Analysis › Trails; parity | — |
| E | Timing, benchmark, captures both themes, demo 67, PR | your review |

2.5 task-weeks estimated; at Phase 1–2 pace about 4–6 working days plus your reviews.

## 11. Questions

See the 🟦 post in the thread (Q1–Q12), each with a recommendation; answers go to phase0-decisions (TL1…).
