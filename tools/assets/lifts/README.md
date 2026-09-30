# Lift asset pipeline (LP1-LP8)

Blender scripts build chairlift terminals and chairs from a dimension file, with no manual modelling. The same
spec always produces the same meshes (the build compares hashes). The first product is the fictional
**Sessellift FGQ-4**, a fixed-grip quad: a drive (top) terminal, a return (bottom) terminal, a quad chair and
a kit of line towers.
Its review, decisions and retrospective are in
[lift-pilot-sessellift-fgq4.md](../../../docs/plans/lift-pilot-sessellift-fgq4.md). The same pipeline builds
the **SLE snow guns** ([snow-guns-sle.md](../../../docs/plans/snow-guns-sle.md)): a stick gun on a 10, 20 or 30 ft
lance and a ground gun on a tripod.

| File | Purpose |
|---|---|
| `sessellift_fgq4.json` | Our dimensions in millimetres: `common` (rope elevation, line gauge, rope, sheaves, bullwheel), then one section per asset |
| `sle_guns.json` | The snow guns' dimensions (`stickGun`, `groundGun`, `tripod`) and their `catalog` names; an asset module names its spec with `SPEC` |
| `budgets.json` | Triangle budgets per LOD and LOD switch distances. The build and the Unity tests both read it |
| `build_lifts.py` | Builds every asset and LOD, bakes AO, exports one FBX per asset plus the textures, and checks budgets, pivots and sockets |
| `assets/*.py` | One module per asset: `drive_terminal`, `return_terminal`, `chair`, `tower` (the line tower kit, see below), `sle_stick_gun` (three lance lengths from one module) and `sle_ground_gun` |
| `liftkit/` | The shared kit: `frame` (lift frame to Blender), `prims` (boxes, beams, cylinders, prisms, lathes), `parts` (sheaves, sheave trains, line assemblies, railings, ladders), `heads` (the entry head shared by the drive terminal and the towers), `mesh` (MeshBuilder, per-face data, flush-face separation), `palette`, `textures` (detail atlas), `ao`, `materials`, `export` |
| `render_review.py` | Review renders: orthographic views with dimensions (`gate1`), LOD lineup (`lods`), shader data (`data`), Cycles photos on snow (`photos`, `terminal_photos`, `tower_photos`; the snow guns' photos, line-up and both heads at one scale come with `photos`), the tower kit in clay (`towers`) |
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
- `<id>_<part>_LODn` under the empty `<id>_pivot_<part>`: moving parts (the bullwheel and every sheave, a snow
  gun's hinged lance or gun), with the origin on the real axle or hinge pin.
- `<id>_socket_<name>` empties:
  - terminals: `line`, `rope_{left,right}_{bw,out}`, `chair_load` or `chair_unload`, `foundation_base`;
  - chair: `grip`, `seat_1..4`, `bar_hinge`;
  - snow guns: `base` (the origin, at grade), `nozzle`, and the hose couplers (`hose_side`, and `hose_bottom` or
    `hose_rear`).

LiftImport checks every pivot and socket against `out/lifts.json` to 1 mm. When an asset's spec has a `catalog`,
`lifts.json` carries its maker and the name the game shows (`LiftRig.Maker`, `LiftRig.CatalogName`).

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

## Line towers (LP11)

The tower kit is modular, so the game can build a tower of any height in 1 m steps:

| Piece | Origin | What it is |
|---|---|---|
| `tower_base` | grade, on the mast centreline | footing 2.5 m below grade, square pier, base plate with anchor nuts and gussets; socket `mast_foot` |
| `tower_mast` | the section's foot | 1 m of the Ø610 mast with its ladder (rungs on a continuous 250 mm pitch); socket `top` |
| `tower_<type>` | the mast top | the head: the drive terminal's entry head (`liftkit.heads`) on a mast cap, with a sheave assembly on each rope; sockets `rope_left`/`rope_right`, `mast_top`, `number_plate` |

**Head types** (`tower.heads`), with the sheave assemblies rebuilt from the owner's reference model
(`parts.line_assembly`, `parts.combo_assembly`): sheaves Ø432 in pairs on rockers, two rockers on a train yoke,
trains on a 163 mm square equaliser, sheaves on an arc.

| Type | Sheaves per rope | Arc |
|---|---|---|
| `s4`, `s6` general support | 4 or 6 | flat (40 m) |
| `b8` breakover | 8 | the reference arc (10.385 m) |
| `d8` hold-down | 8 | the same assembly flipped |
| `c8` combination | 4 hold-down over 4 support, aligned, on triangular plates | flat |

**Connection:** every assembly hangs from below the crossbeam end. Two lug plates under the end carry its pin,
320 mm below the crossbeam, as on the return terminal's integrated tower (itself a hold-down tower). The rope
height at the head therefore depends on the type, and the head records it in its rope sockets. At the return,
the row is levelled at its first sheave (`"level": "first"` on the 13 m `station` arc): the rope runs level
through the loading area and leaves climbing at about 16°, and the sockets `rope_*_hold` and `rope_*_out` mark
where it meets and leaves the row.

**Finish:** towers are galvanised throughout; the number plate is black. In every sheave row the first and last
sheave are red (lightning grounding) and the rest galvanised, on the terminals too (`parts.row_face`). Sheaves
spin only at LOD0 on towers; from LOD1 a head is one mesh.

## SLE snow guns (LP16-LP19)

| Asset | Origin | What it is |
|---|---|---|
| `sle_stick_gun_10`, `_20`, `_30` | grade, on the base mast's axis | a 4 in base mast (1.08 m above grade, 0.45 m below) with a base plate and ears; the lance (10, 20 or 30 ft of pipe on a U-channel) pinned 1.15 m up, held by a stay from a clevis on the mast; the head on the pipe's end: a Y block carrying the fan block, the barrel and the nucleator cap |
| `sle_ground_gun` | grade, under the tripod's pivot | the same head on a black valve body with the hose block, valve paddle, couplers and gauge, on an aluminium tripod (an inverted-U arch, a crossbar, a rear leg, a quadrant disc and T-pin) |

- **Frame:** +Z (u) is the way the gun fires, +Y up. The stick gun's spec is measured in the reference's frame,
  whose zero is its snow line; `grade` sets the asset's origin 845 mm below it, so the mast stands on the ground.
- **Aim:** one hinged moving part, the lance (`pivot_lance`) or the ground gun's gun (`pivot_gun`), tilted about
  X on its pivot; the build doesn't check a hinged part for centring on its axle (`"hinge": true`). The stay rides
  with the lance, so keep its tilt within a few degrees. Turning the whole gun about +Y is the placement's yaw.
- **One head:** both guns carry the ground gun's head dimensions (`make_gun_spec.py` keeps them in one place): a
  fan block with a 100 × 100 mm, 12-nozzle face, a 2 in barrel 305 mm long and a 74 mm cap with an octagonal nose.
- **Lance lengths:** one module builds all three (`MODULES` maps each id to the module and its variant); the head
  rides on the pipe's end, and the channel stops 5 ft short of it.

## Budgets (enforced)

The build exits non-zero if an LOD is over budget, if totals don't decrease, if a moving part is off its
axle, or if a rope socket is off the rope.

| Asset | LOD0 | LOD1 | LOD2 | LOD3 |
|---|---|---|---|---|
| Terminal budget | 24,000 | 10,000 | 2,500 | 500 |
| Drive terminal | 13,914 | 6,794 | 1,788 | 254 |
| Return terminal | 19,864 | 7,028 | 1,836 | 316 |
| Chair budget | 1,600 | 250 | 60 | - |
| Quad chair | 954 | 246 | 60 | - |
| Tower head budget | 16,000 | 5,000 | 1,000 | 200 |
| Head `s4` | 7,512 | 2,384 | 484 | 96 |
| Head `s6` | 10,720 | 3,248 | 620 | 96 |
| Heads `b8`, `d8` | 13,848 | 4,064 | 732 | 96 |
| Head `c8` | 13,792 | 4,008 | 732 | 120 |
| Mast section budget | 150 | 100 | 50 | 16 |
| Mast section | 144 | 88 | 40 | 12 |
| Tower base budget | 400 | 300 | 60 | 40 |
| Tower base | 314 | 278 | 58 | 36 |
| Snow gun budget | 600 | 160 | 40 | 12 |
| Stick gun (10, 20 or 30 ft) | 598 | 154 | 38 | 8 |
| Ground gun | 534 | 156 | 30 | 12 |

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
`build_lifts.py` (and to `ALL`, to build them by default). A new spec names its maker and each asset in a
`catalog` section. Maker names are code names (Sessellift, Monta, Chairworks, SLE); never use a real maker, resort
or drawing name in this folder.
