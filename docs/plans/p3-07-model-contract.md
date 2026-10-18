# Phase 3 · 07 Model contract: posable trains, grips, piers (round 2)

**Audience:** the model thread (`feature/p3-07-models`) and the style tile thread (`feature/p3-07-style-tile`).
**Status:** agreed in the round 2 plan, approved by the owner on 2026-10-17 (SK16–SK20); amended by the owner's answers in the model thread (§7). **Findings:**
[p3-07-findings.md](p3-07-findings.md) #1, #2, #3, #5, #6, #8, #9, #10.

The game poses the lift's moving parts once, at build, from the rope. It needs these parts, names and keys in the
models. Shapes don't change (the owner's rule on the tower heads: "DO NOT CHANGE THEM"); only how the meshes are
grouped and where they pivot.

## 1. Pipeline (M1)

- **`liftkit/export.py`:**
  - A pivot may name a `parent` pivot; its empty is parented to the parent's empty with a local offset.
  - Every part is a child of its own pivot's empty, as now.
- **`lifts.json`, per pivot:**
  - `pos`: root-space metres in Unity's frame, as now, so LiftImport's `Expect` check still works;
  - `axis`;
  - `parent` (a pivot name, or none);
  - `hinge` (true when the part isn't centred on its axis);
  - `range`: [min, max] degrees of safe rotation from the rest pose, from a clash sweep.
- **UVs:**
  - UV0 is box-mapped in the lift frame **before** a part is moved to its pivot, so textures don't shift.
  - `separate_flush` runs on the merged LOD before faces are split into parts.
- **Pivots at every LOD:**
  - LOD0–1 carry the full hierarchy.
  - At LOD2–3 each side's merged train is one part, filed under that side's top pivot. It stays a part, never folded
    into the body, so it turns with the whole train.
- **`Editor/LiftImport.cs`:** reads `parent`, `hinge` and `range` into new `LiftRig` arrays (`PivotParents`,
  `PivotHinged`, `PivotRange`), indexed like `Pivots`.
- **`Tests/EditMode/LiftAssetTests.cs`:** the centring test reads `PivotHinged` instead of `Kind == "snowgun"`, and
  checks only each pivot's direct child meshes.
- **New tests:**
  - turning a parent pivot moves its children rigidly;
  - nested pivots round-trip through FBX to 1 mm.

## 2. Pivot names

Per rope side `r` (right) and `l` (left). Indices count from the line's +u end, as the sheaves do today.

| Asset | Hierarchy (parent → children) |
|---|---|
| b8, d8 (8 sheaves) | `eq_r` (the main pin under the crossarm) → `yoke_r1`, `yoke_r2` → `rocker_r1`…`rocker_r4` → `sheave_r1`…`sheave_r8` |
| s6 (4 + 2) | `eq_r` → `yoke_r1` (→ `rocker_r1`, `rocker_r2`), `link_r1` (a lone 2-sheave rocker) → sheaves |
| s4 (4) | `yoke_r1` is the top pin (no equaliser) → `rocker_r1`, `rocker_r2` → sheaves |
| c8 (4 over 4) | `block_r` (the central pivot block, rigid with both rows' triangular frames) → `yoke_hold_r`, `yoke_sup_r` → rockers → sheaves |
| Return terminal row (8, hold-down) | as b8/d8, with the existing sheave names (`first=2`) |
| Drive terminal row (4, support) | `beam_r` (the beam pin through the plate) → `rocker_r1`, `rocker_r2` → sheaves |
| Sessellift chair | `grip` (origin on the rope, the grip's socket) → `hanger` (hinged at the hanger's top ring, axis across the rope). LOD0–1 split; LOD2 stays one part |

- **Chair:** a pin knuckle at the hinge, so that at ±30° between grip and hanger no gap shows.
- **Clash sweep:** sets each `range`. Rotate each pivot until any of its parts meets the crossarm, the lug plates or a
  neighbour.

## 3. Piers and footings (M4)

- **Terminal bodies:**
  - The drive's concrete pier and the return's pedestal leave the terminal body.
  - The body ends at a new socket `pier_top` (the pier's top centre).
  - Existing sockets keep their names and positions.
- **`sessellift_fgq4_pier_section`:** a 1 m concrete section, both terminals' section, tiling vertically. The game
  stacks sections down from `pier_top`, the last one partly buried.
- **`sessellift_fgq4_terminal_footing`:** each terminal's footing as its own prefab, origin at grade, placed by the
  game at the ground or pad. `foundation_base` moves to it.
- **Leaned tower bases** (`tower_base_l025`–`l150`): already built (pier parallel to the mast). Include them in the
  photo set.

## 4. Ambient occlusion (M5, #9)

- **Mast sections, ladder and pier sections:**
  - baked with `ground=False`;
  - with one cast-only ghost section above and one below, so both ends get the same light.
  - The build report checks the AO at a section's top and bottom corners agrees within 0.01.
- **Tower heads:** baked with `ground=False` too; the ground plane sits at the mast top today.

## 5. Head inner faces (#10)

The style tile thread checks first whether this is the lift shader's 4×4 detail atlas bleeding under anisotropic
filtering; if so, it's fixed in `LiftStructure.shader` and needs nothing here. If the thread finds the UVs at fault, it
asks for a fix here: each portal leg face projected along its own normal.

## 6. Gate (one round)

- **Photos** (Cycles, render lane):
  - before-and-after of every head at the same camera, captioned "grouping split only; shapes identical";
  - the heads posed at sample rope angles (s4, s6, b8, d8, c8, both terminal rows);
  - the chair at 0° and ±30°;
  - a terminal on a tall pier and its footing;
  - the leaned bases;
  - the mast.
- **Checks:**
  - geometry compared with the shipped heads before `separate_flush`, to 2.5 mm, with a report;
  - triangle budgets;
  - the name scan;
  - an **estimate of LFS growth**;
  - FBX files whose meshes didn't change restored from git (`cmp -l`).
- **Approval:** the owner approves in their own thread, then one LiftImport, the EditMode lift tests and the Lift Lab
  rebuild. The model thread merges into `feature/p3-07-style-tile` (no PR of its own).

## 7. Amendments (owner, in the model thread, 2026-10-17)

- **Piers:** no pier section and no stacking. "Piers should simply be really tall to allow them to adjust height as
  needed. We'll create off ramp models later."
  - Each terminal keeps its own pier, extended to about 15 m below its 0.00 level, so a raised terminal shows more pier
    and the rest is buried in its pad.
  - `pier_top` stays, for information.
  - One `sessellift_fgq4_terminal_footing` (kind `terminal_footing`, one LOD, origin at grade) carries `foundation_base`
    and `pier_seat`; the game places it at the ground under each terminal's pier.
  - Terminal bodies lose `foundation_base` and are baked without a ground plane.
- **Rope:** 1¾ in, 44.45 mm (`common.ropeDiameter`).
  - Tower heads' rope sockets rise 4.2 mm.
  - The terminals' rows and the drive's guide sheaves move 4.2 mm away from the rope, which stays at 3039.
  - The fixed grip's blades widen to fit.
- **Sheaves at LOD1:** each sheave's mesh rides inside its rocker part; `sheave_*` pivots still exist at every LOD.
  The chair's LOD1 budget is 260.
- **Names:**
  - Sheave 1 is at the −u end (today's numbering).
  - s6's `link_r1` holds sheaves 5 and 6 directly.
  - c8 rockers are `rocker_rs1/2` (support) and `rocker_rh1/2` (hold-down).
  - Drive: `beam_r` → `rocker_r1/2` → `sheave_r2..r5`.
  - Return: `eq_r` → `yoke_r1/2` → `rocker_r1..r4` → `sheave_r2..r9`.
  - The chair's seat and `bar_hinge` sockets ride under `hanger`; the hinge axis is Unity X; `lifts.json` gains
    `socketParents`.
