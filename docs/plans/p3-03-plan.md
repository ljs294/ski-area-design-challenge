# P3-03 Design document, commands and undo: the plan

**Status:** approved by the owner on 2026-10-16 ("Go. Yes to all four recommendations."). Decisions DD1–DD4 are in
[phase0-decisions.md](phase0-decisions.md#phase-3-task-03-design-document-decided-2026-10-16).
**Branch:** `feature/p3-03-design-document`, from origin/main 13715d0.
**Task:** row 03 of the [Phase 3 plan](phase2-14-phase3-plan.md#3-tasks) (T15, 0.3 §10).
**Rules implemented:** SH-04, SH-05, CF-03 to CF-06, TW-15, JN-04, NW-06 and NW-10 ([design rules
§10](p3-02-design-rules.md#10-who-implements-what)).

## Scope and files

- **The document, commands, history, IDs, stamps and network:** `Assets/MountainPlanner/Runtime/Domain/Design/**`,
  namespace `MountainPlanner.Domain.Design`.
- **Tests:** `Assets/MountainPlanner/Tests/Core/Design*Tests.cs`. They run in `tools/domain-tests` and in Unity.
- **The demo:** `demo.bat` 61.
- **No Persistence and no format change.** Task 04 writes the save file.

## The model

Everything is immutable. The document holds authored data only (SH-05, NW-06):

```
DesignDocument   ShapeVersion = 1, Seed, NextOrdinal, Trails[], Lifts[]   (each sorted by ID)
Trail    TrailId, Name, Stage (Plan | Built),
         Settings { Grading Graded|Natural, Kind Run|Glade|Zone, Clearing Clear|Thin|None }   TR-06
         Parts[] { Strokes[] { WidthMm, Erase, Points[] }, Cuts[] }                         TR-02, NW-10
         Head?, Tail?                                                                       TR-03
Lift     LiftId, Name, Stage, CatalogId, Bottom, Top, TowerMode (Automatic|ByHand), Stance (Plumb|Leaned),
         Towers[] { TowerId, AlongMm, HeightMm }                                             TW-15
```

- **Units:** integer millimetres in the package grid (SH-03). The rules' imperial steps are exact (5 ft = 1,524 mm),
  and integers give the same bytes on every machine.
- **IDs:** a keyed hash of (design seed, kind tag, ordinal) (SH-04). An ordinal whose hash is already taken is
  skipped, so IDs never collide.
  - Junction and path IDs hash the two trails they join plus the position to the metre (JN-04).
  - Lift-end and trail-end node IDs hash their entity's ID.
- **Derived data, never stored:** junctions, segments, paths, the graph, head types, forces, ratings and volumes.

## Commands

- **The contract:** `DesignCommand { BaseRevision }`, then `Apply(doc)`, which returns a new document or a rejection
  (Stale, NotFound, Invalid, NotAvailable).
  - Every check runs before anything is built, so nothing is partly applied (CF-05).
  - `Apply` alone dry-runs a command for a tool; `DesignHistory.Submit` adds the revision check and records undo.
- **Trails:** AddTrail, RepaintTrail, SetTrailEnds, RenameTrail, SetTrailSettings, DeleteTrail, SplitTrail and
  JoinTrails (NW-10).
- **Lifts:** AddLift, AddTower, EditTower (slide and re-height), DeleteTower, ReplaceTowers (Automatic, Re-place and
  Fill the rest, with an optional mode switch in the same step), SetTowerMode, SetTowerStance, RenameLift and
  DeleteLift.
- **Both:** BreakGround, which turns plans into built work, all of them or none (CF-06).
- **Commands are never saved,** so tasks 08 and 09 can add commands without touching the document format.
- **In-tool steps aren't commands:** a stroke's Ctrl+Z, the pending lift line and the tower in hand stay inside the
  tool until Confirm.

## Undo and revisions

- **A snapshot history:** undo and redo swap immutable documents, so a restore is exact.
  - 100 steps (DD1), for the session only, cleared by `Load`.
  - Each step has a label for the menu ("Undo Rename trail").
- **Revision numbers are never reused.** Every change, an undo or a redo included, takes the next number, so a result
  computed for an older revision can't pass as current after an undo and a redo (CF-03).
- **CF-04:** a second confirmation in the same tick names a revision that is already stale.
- **`AnalysisStamp { DesignRevision, TerrainRevision, InputSequence }`:** task 05 owns TerrainRevision; the tool owns
  InputSequence (its cursor or stroke count). `Stamped<T>.TryTake` drops a result whose stamp isn't current (CF-03).
- **Snapshots out:** the UI reads `DesignHistory.Current` and the `Changed` event, and submits commands; nothing else
  changes the design.

## Split and Join (DD2)

- **Split** adds a cut line (a point and the centreline's direction) to every part, keeping the side behind the line
  in the upper trail and the side ahead in the new lower trail.
  - Every cell goes to exactly one side.
  - The upper trail keeps its ID, name and head; the lower one is "⟨name⟩ lower".
- **Join** (the upper's tail within 30 ft of the lower's head) merges the parts that are the two sides of one split,
  removing the cut. Other parts sit side by side. A split followed by a join gives the original trail's bytes.

## The network

- **`DesignNetwork.Build(doc, ITrailGeometry)`** rebuilds the graph from the document (NW-01). Plans are left out
  (NW-02).
- **Built in this task:**
  - lift edges, bottom to top;
  - a trail end within 30 ft of a built lift's top or bottom attaches to it (TR-05). The attachment is derived, so
    moving or deleting a lift needs no fix-up (DD3).
  - segments cut at every junction and path end (NW-08).
- **Trail-to-trail junctions, overlaps and automatic paths** come from `ITrailGeometry`, which tasks 08 and 10
  implement.
  - Its results are normalised and sorted, so the order they're found in never reaches the graph.
  - Without geometry, each trail is one straight segment.
- **`NetworkGraph.Encode()`** gives canonical bytes for the goldens.

## The format (DD4)

- **Domain owns the document's shape:** `DesignDocument.ShapeVersion = 1`. `DesignBytes` is a canonical little-endian
  encoding in ID order, used for goldens and equality. `DesignBytes.Decode` refuses anything malformed or out of
  order.
- **Task 04 picks the save file's encoding** (likely JSON through `VersionedJson`) and freezes it.
  - `SH05_DocumentHoldsOnlyAuthoredData` lists every property of the shape, so a change can't slip in unnoticed.
  - After task 04 starts, a shape change goes through the coordinator.

## Tests and demo

- **Rule tests,** each citing its ID: SH04, SH05, CF03 to CF06, TW02, TW15, TR01, TR05, TR06, NW01, NW02, NW06, NW08,
  NW10, JN04 and PA06.
- **Property tests:** 40 keyed-random sequences of valid, invalid and stale commands, with undos and redos. After
  every step:
  - an undo returns the exact bytes, and a redo brings the change back exactly;
  - a rejection changes nothing, not even the revision;
  - encoding, then decoding, then encoding again gives the same bytes.
- **Goldens:** the SHA-256 of a scripted design's bytes, and of its graph with a stub geometry. The same commands
  give the same bytes.
- **demo.bat 61:** runs the design tests, then prints a walkthrough.

## For tasks 04–10

| Task | Uses |
|---|---|
| 04 Save file | `DesignDocument.From` / the entity constructors to read; the entity properties to write; `Seed` and `NextOrdinal` are saved; the undo history isn't (DD1) |
| 05 Terrain edits | `Trail.Parts` (strokes and cuts), `Settings.Grading`, `Stage` (plans change no ground); `AnalysisStamp.TerrainRevision` |
| 06 Toolbox | `DesignHistory.Submit`, `Undo`, `Redo`, `UndoLabel`, `Changed`; `Command.Apply` to dry-run |
| 08 Trails | `ITrailGeometry` (centrelines, junctions); new trail commands as needed |
| 09 Lifts | `Lift.Towers`, `ReplaceTowers` for automatic placement, `EditTower` for Page Up / Page Down; catalog values keyed by `CatalogId` |
| 10 Network | `ITrailGeometry.FindPaths`; connectivity and the NW-12 warnings over `NetworkGraph` |
