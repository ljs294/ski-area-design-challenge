# Skier and snowboarder models

Rigged low-poly guests for the mountain: one skier and one snowboarder, built by script in Blender 5.2 LTS in the
style of `tools/assets/lifts`. The look follows the owner's concept art ("low-poly + technical alpine shells"). This
folder holds the pipeline only. Review renders, audits and references stay outside the repository.

The game draws them (task 4.22 part 2): `build-skiers.bat` bakes both figures into one file, Unity's
**Mountain Planner > Import Skiers** checks it and copies it into Resources, and the game skins them in a shader.
There is no FBX and no Animator.

## What is built

- **One skeleton** for both figures: 27 bones for the skier (two skis, two poles), 24 for the snowboarder (one
  board). Bone lengths come from the MakeHuman base mesh (CC0, see `provenance.json`).
- **One jacket style**, coloured per guest from a keyed palette (`skierkit/outfit.py`). Colours are material ids,
  not textures.
- **Six LODs** per figure, with budgets and switch distances in `budgets.json`.
- **Poses and clips** written at a high level and solved by the rig (`skierkit/rig.py`): legs and arms by two-bone
  IK, so boots stay on the skis or the board and pole tips never go under the snow.

## Axes and units

Metres. `+X` is the figure's left, `-Y` is forward (the direction of travel), `+Z` is up. The origin is on the snow
between the boots (on the seat, under the hips, for the seated pose). The bake turns this into Unity's frame:
Blender `(x, y, z)` becomes Unity `(-x, z, -y)`, so the figure faces Unity's `+Z`.

## Files

| File | What it holds |
| --- | --- |
| `budgets.json` | Triangle budget, switch distance and bone influences for each LOD |
| `provenance.json` | Every outside source and its licence (CC0 only) |
| `skierkit/skeleton.py` | The rest skeleton and gear sizes (pole 1.20 m, stance 0.52 m) |
| `skierkit/mesh.py` | The mesh builder: lofts, tubes, staggered rings, cloth facets, material ids |
| `skierkit/body.py` | Jacket, trousers, gloves, boots, head, helmet and goggles, per LOD |
| `skierkit/gear.py` | Skis (1.70 m), bindings, poles, snowboard (1.56 m) and its bindings, per LOD |
| `skierkit/outfit.py` | The keyed outfit of one guest, with rules against clashing pairs |
| `skierkit/rig.py` | The armature, skinning and the pose solver |
| `skierkit/poses.py` | Still poses: stand, carve, seated on a chair |
| `skierkit/clips.py` | Turn cycles: four ski styles by ability and the snowboard heel and toe turns |
| `skierkit/build.py` | Builds a rigged figure with its LODs and materials in a Blender scene |
| `skierkit/bake.py` | The game's file: every LOD, every clip baked to bone matrices at four slopes, the outfits |
| `build_skiers.py`, `build-skiers.bat` | Runs the bake into `out/` (not in git) and checks it, then Blender's own armature against it |
| `render_review.py` | Review renders (Cycles): pose sheet, chair, LODs, colours, close-ups, clip strips, slope |

## The colour layer

Each face corner carries a float colour named `matid`:

- **Red** is the material id: `(id + 0.5) / 16`, with ids in the order of `mesh.MATS`.
- **Green** is a keyed tone for the face: 0.5 means no change, cloth varies by 8 percent and hard parts by 3. It gives
  the painted facets of the concept art without a texture.

## Clips

One clip is a linked pair of turns as a closed curve through eight key poses. Phase 0 is the change of edges into a
turn to the figure's left; phase 0.5 is the change into the turn to the right. The game moves and turns the root
along its path; a clip only moves the body over the skis or the board. A snowboarder (left foot leading) turns left
on the heel edge and right on the toe edge.

## The game's file

`out/skiers.bytes` ("MPSK" v1, little-endian; read by `Presentation/Skiers/SkierFigureData.cs`) and
`out/skiers.json` (its summary and reference points). Per figure: its LODs as triangle lists (position, four bone
indices and weights, material id, tone), its clips and their frames, each frame's bone matrices, and 1,024 keyed
outfits of 16 colours. The turns are baked at 0, 12, 24 and 36 degrees of slope (the body leans forward on the
hill, the head stays level); the game picks the nearest. Two travel clips (a skier's shuffle and a snowboarder's
one-foot push) move guests along lift lines and paths.

The build fails on any of these:

- a vertex with more than four influences, or weights that don't add up;
- a pole tip, boot or binding that drifts off its ski or board by more than 1 mm;
- a clip whose last frame doesn't meet its first;
- skis more than 5 cm, or a board more than 16 cm (it rides on edge), off the snow;
- Blender's own armature, posed with the same solver, putting a vertex more than 0.5 mm from the bake.

The import skins the reference points (vertices Blender posed) again in C# and refuses a file more than 1 mm off.
The EditMode test `SkierFigureTests` checks the shipped file the same way.

## In the game

`SkierFigures` (Presentation/Skiers) draws every guest nearer than 250 m as a figure: one draw per figure and LOD,
skinned in `SkierFigure.shader`, with shadows on the three nearest LODs. `SkierFigureJob` (Burst) picks for each
guest the figure (one in four rides a board, by serial), the LOD (budgets.json's distances, scaled by the camera's
field of view and window height, with 10 percent hysteresis), the clip (the band's turn on the slope's pitch, the
travel clip when moving on the flat, a keyed waiting pose when standing) and the outfit. `SkierChairRiders` seats
chair riders: rides are time-compressed, so a seated figure takes the chair that left the loading point when its
guest boarded and rides it to the top, in its own outfit. Farther than 250 m, and for the selected guest's marker,
the dots stay. `-dotsonly` draws only the dots, as before.

## Running

Bake and check (about 15 seconds), then import in Unity:

```
tools\assets\skiers\build-skiers.bat
<Unity.exe> -batchmode -projectPath . -executeMethod MountainPlanner.Editor.SkierImport.Import -quit
```

Review renders go through the GPU render lane (see `tools/gpu/README.md`):

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane render -Name "<thread>" -- "C:\Program Files\Blender Foundation\Blender 5.2\blender.exe" -b --factory-startup --python-exit-code 1 --python tools/assets/skiers/render_review.py -- --out <absolute folder> --shots sheet,chair,lods,colours,closeups,strips,tycoon
```

The chair, close-up and slope shots read `tools/assets/lifts/out/sessellift_fgq4_chair.fbx`, a build output of the
lift pipeline. `demo.bat` 107 runs the whole set into `test-results/p4-22` and builds the lift models first when
that file is missing.
