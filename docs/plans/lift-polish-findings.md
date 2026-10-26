# Lift polish: the D6 lessons on the older lifts

The Sessellift D6 review (`docs/plans/lift-d6-findings.md`) found problems that the older families share. This polish
applies those lessons to the Sessellift FGQ-4, the Monta FG4 and the Chairworks chairs. Owner findings are added here as
they're raised.

Status: **fixed** (built, checked and approved from photos), **open**, or **game** (needs a game task).

## Owner decisions (2026-10-23)

| # | Decision |
| --- | --- |
| P1 | Raise the `chair` budget: LOD2 60 → 120 triangles, and add a LOD3 of 30 triangles out to 800 m, so the chairs no longer vanish at 300 m. |
| P2 | Bake the FGQ-4 terminals' AO with a ground plane at their standard grade. |
| P3 | Before-and-after photos in game conditions approved on 2026-10-24. |

## Lessons applied

| # | Lesson | Assets | What was done | Status |
| --- | --- | --- | --- | --- |
| L1 | Seats turn white under the default Snow layer | quad chair, Chairworks | Seats and grips take no snow (the fixed grip has its own style); steel takes 0.1 | fixed |
| L2 | Chairs have no backrest at LOD2 and vanish past 300 m | the same | A LOD2 backrest, and a LOD3 speck out to 800 m (P1) | fixed |
| L3 | Faceted curves | FGQ-4 drive, Monta drive, chairs | Hood barrel and glass in one smoothing group; LOD1 tubes, wheels and springs smoothed | fixed |
| L4 | 1 m seam grid on the hoods | FGQ-4 drive, Monta drive | The gloss tile on the hood, end walls, band and ribs | fixed |
| L5 | Snow indoors and on ribs | FGQ-4 drive, Monta drive and return | No snow under the hoods (bullwheel plate, pier head, base frame, brackets, carriage); ribs 0.3; the Monta return's open wheel takes snow | fixed |
| L6 | The Monta drive's hood is open underneath | Monta drive | A dark soffit closes the underside | fixed |
| L7 | LOD3 drops the glass and spends triangles on hidden parts | FGQ-4 drive, Monta drive and return | Glass bands kept; the hidden wheels and the buried footings dropped; a real LOD3 for the Monta return | fixed |
| L8 | AO streaks across n-gon caps | FGQ-4 and Monta terminals, quad chair | Fan caps on the hubs, motor, legs, struts, booms, posts and hood ends. Not on the Chairworks detach chair, which is at 1,596 of its 1,600 LOD0 triangles | fixed |
| L9 | Yellow rungs only at LOD0 | FGQ-4 drive | Yellow rungs at every LOD | fixed |
| L10 | No contact AO at the FGQ-4 terminals' feet | FGQ-4 drive and return | Baked with ground at grade (P2) | fixed |
| L11 | No chair-vs-terminal check for fixed grips | all four fixed-grip terminals | `liftkit/fit.py` `fixed()` sends the chair round each bullwheel in the build. The FGQ-4 return's chair guide is a guide piece (`#chairguide`). All four pass | fixed |

## Checks

- **Build:** passes for all assets.
- **Identity:** only the seven polished assets differ from main (content hash); no pivot data changed.
- **Tests:** EditMode 1158 passed, 0 failed; every assembly compiles; repo checks pass.
- **Photos:** in game conditions, in `lift-reference\polish\` (local).
