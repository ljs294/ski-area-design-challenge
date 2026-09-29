# Lift asset pipeline (LP1-LP8)

Blender scripts build chairlift terminals and chairs from a dimension file, with no manual modelling. The same
spec always produces the same meshes (the build compares hashes). The first product is the fictional
**Sessellift FGQ-4**, a fixed-grip quad: a drive (top) terminal, a return (bottom) terminal and a quad chair.
Its review, decisions and retrospective are in
[lift-pilot-sessellift-fgq4.md](../../../docs/plans/lift-pilot-sessellift-fgq4.md).

| File | Purpose |
|---|---|
| `sessellift_fgq4.json` | Our dimensions in millimetres: `common` (rope elevation, line gauge, rope, sheaves, bullwheel), then one section per asset |
| `budgets.json` | Triangle budgets per LOD and LOD switch distances. The build and the Unity tests both read it |
| `build_lifts.py` | Builds every asset and LOD, bakes AO, exports one FBX per asset plus the textures, and checks budgets, pivots and sockets |
| `assets/*.py` | One module per asset: `drive_terminal`, `return_terminal`, `chair` |
| `liftkit/` | The shared kit: `frame` (lift frame to Blender), `prims` (boxes, beams, cylinders, prisms, lathes), `parts` (sheaves, sheave trains, railings, ladders), `mesh` (MeshBuilder, per-face data, flush-face separation), `palette`, `textures` (detail atlas), `ao`, `materials`, `export` |
| `render_review.py` | Review renders: orthographic views with dimensions (`gate1`), LOD lineup (`lods`), shader data (`data`), Cycles photos on snow (`photos`) |
| `build-lifts.bat` | Double-click: builds everything and opens the photos |

```sh
blender -b --factory-startup --python-exit-code 1 --python tools/assets/lifts/build_lifts.py -- --out tools/assets/lifts/out
# review renders: --render DIR --shots gate1,lods,data,photos   (LIFT_PHOTOS=hero,back renders only those photos)
# diagnostics: --assets drive,chair   --ao off   --detail off   (LIFT_PICK="x,y;x,y" names what the photo camera sees)
```

This needs **Blender 5.2 LTS** (free). `out/` isn't committed. The build takes about 10 s; the photo set takes
about 6 minutes. Then `demo.bat` 22 imports into Unity (`MountainPlanner.Editor.LiftImport.Import`) and builds
the Lift Lab. 23 opens it.

## The lift frame

Every dimension is in the **lift frame**. The origin is the terminal's mast centreline at grade (0.00). **u**
points along the line toward the other terminal (Unity +Z), **v** to the right (Unity +X) and **w** up. The rope
runs at w = 3039 mm, and the line gauge (bullwheel pitch diameter) is 4120 mm.

## What each FBX holds

- `<id>_LOD0..3`: the body (chair: LOD0..2).
- `<id>_<part>_LODn` under the empty `<id>_pivot_<part>`: moving parts (the bullwheel and every sheave), with the
  origin on the real axle.
- `<id>_socket_<name>` empties:
  - terminals: `line`, `rope_{left,right}_{bw,out}`, `chair_load` or `chair_unload`, `foundation_base`;
  - chair: `grip`, `seat_1..4`, `bar_hinge`.

LiftImport checks every pivot and socket against `out/lifts.json` to 1 mm.

| Channel | Meaning |
|---|---|
| **UV0** | Detail-atlas coordinates, box-mapped in metres. The atlas tile is in the integer part of x (`tile × 256 + metres + 128`) |
| **UV1 x** | Snow capacity of the face (the shader adds `_SnowLoad` on up-facing surfaces) |
| **UV1 y** | Livery mask: 1 on the drive hood panels, which take the player's colour |
| **UV2** | Palette swatch in `lift_palette.png`: albedo, with metallic and smoothness packed in alpha |
| **Vertex colour R** | Baked ambient occlusion (ray-cast, deterministic, floor 0.25) |
| **Submesh / slot** | `LiftStructure` (opaque), `LiftGlass` (tinted, drive LOD0-1), `LiftChair` |

`lift_trim.png` is the detail atlas: 4 × 4 tiles of 256 px, one metre each. The tiles are plain, paint,
galvanised, grating, checker plate, concrete formwork, hood panel, rubber and seat vinyl. Its channels are
RG normal, B cavity and A brightness, with 0.5 neutral. Faces pick a tile by palette class
(`textures.CLASS_TILE`) or by `Style.trim`.

## Budgets (enforced)

The build exits non-zero if an LOD is over budget, if totals don't decrease, if a moving part is off its
axle, or if a rope socket is off the rope.

| Asset | LOD0 | LOD1 | LOD2 | LOD3 |
|---|---|---|---|---|
| Terminal budget | 24,000 | 10,000 | 2,500 | 500 |
| Drive terminal | 13,914 | 6,794 | 1,788 | 254 |
| Return terminal | 12,944 | 5,884 | 1,612 | 292 |
| Chair budget | 800 | 250 | 60 | - |
| Quad chair | 768 | 236 | 60 | - |

## Modelling rules learned in the pilot

- **Orient faces explicitly.** `MeshBuilder.face` takes an outward direction; prisms and lathes use the
  outline's winding (shoelace sign), because a centroid test fails on concave outlines.
- **Flush parts are fine.** Parts are overlapping solids, so where two meet flush, two faces share a plane.
  `MeshBuilder.separate_flush` pulls the smaller face 2 mm into its own part (repeated until stable). Without
  it, those faces z-fight in Unity and render black in Cycles.
- **AO samples start inside the face** (inset toward its centre and lifted 1 cm). Corner samples that start
  inside a neighbouring part read as fully occluded.
- **Unity's linear colour space:** palette and livery colours are sRGB and converted like Unity does. A shader
  texture default must be `linearGrey`, not `grey`, which samples 0.21.
- **Review in photos, not just drawings.** The Cycles photo set (`--shots photos`) catches what orthographic
  views hide: colours, occlusion, glass and flush-face artefacts.

## Adding a lift

Copy `sessellift_fgq4.json` and an asset module, change the dimensions, and add the ids to `MODULES` in
`build_lifts.py`. Maker names are code names (Sessellift, Monta, Chairworks); never use a real maker, resort or
drawing name in this folder.
