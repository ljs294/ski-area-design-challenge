# Lift asset pipeline (LP1-LP8)

Blender scripts build chairlift terminals and chairs from a dimension file, with no manual modelling. The same
spec always produces the same meshes (the build compares hashes). The first product is the fictional
**Sessellift FGQ-4**, a fixed-grip quad: a drive (top) terminal, a return (bottom) terminal, a quad chair and
a kit of line towers.
Its review, decisions and retrospective are in
[lift-pilot-sessellift-fgq4.md](../../../docs/plans/lift-pilot-sessellift-fgq4.md). The same pipeline builds
the **SLE snow guns** ([snow-guns-sle.md](../../../docs/plans/snow-guns-sle.md)): a stick gun on a 10, 20 or 30 ft
lance and a ground gun on a tripod; the **Monta FG4** terminals ([lift-monta-fg4.md](../../../docs/plans/lift-monta-fg4.md)),
which carry the Sessellift chair; and the **Chairworks quad chair** with either grip
([chairs-chairworks.md](../../../docs/plans/chairs-chairworks.md)).

| File | Purpose |
|---|---|
| `sessellift_fgq4.json` | Our dimensions in millimetres: `common` (rope elevation, line gauge, rope, sheaves, bullwheel), then one section per asset |
| `sle_guns.json` | The snow guns' dimensions (`stickGun`, `groundGun`, `tripod`) and their `catalog` names; an asset module names its spec with `SPEC` |
| `monta_fg4.json` | The Monta terminals' dimensions (`common`, `drive`, `return`) and their `catalog` |
| `chairworks_chair.json` | The Chairworks chair's dimensions (the body, both grips) and its `catalog` |
| `fixed_grip.json` | The fixed grip every fixed-grip chair carries (LP22) |
| `budgets.json` | Triangle budgets per LOD and LOD switch distances. The build and the Unity tests both read it |
| `build_lifts.py` | Builds every asset and LOD, bakes AO, sweeps the pivots for clashes, exports one FBX per asset plus the textures, and checks budgets, pivots, sockets and AO tiling |
| `audit_lifts.py` | Game-rule audit before a model review: re-imports each FBX and checks the pivots' places, the nesting LiftImport will build, far trains kept as parts, ranges and AO tiling |
| `assets/*.py` | One module per asset: `drive_terminal`, `return_terminal`, `chair`, `tower` (the line tower kit, see below), `sle_stick_gun` (three lance lengths from one module), `sle_ground_gun`, `monta_drive`, `monta_return`, `chairworks_chair` (two grips from one module) and `terminal_footing` |
| `liftkit/` | The shared kit: `frame` (lift frame to Blender), `prims` (boxes, beams, cylinders, prisms, lathes), `parts` (sheaves, sheave trains, line assemblies, railings, ladders), `heads` (the entry head shared by the drive terminal and the towers), `mesh` (MeshBuilder, per-face data and part tags, flush-face separation), `palette`, `textures` (detail atlas), `ao`, `materials`, `export`, `clash` (the pivots' safe ranges), `pose` (posing for the review photos) |
| `render_review.py` | Review renders: orthographic views with dimensions (`gate1`), LOD lineup (`lods`), shader data (`data`), Cycles photos on snow (`photos`, `terminal_photos`, `tower_photos`; the snow guns' photos, line-up and both heads at one scale come with `photos`), leaned bases (`lean_photos`), posed heads, chair and raised terminals (`pose_photos`), the tower kit in clay (`towers`) |
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
  gun's hinged lance or gun, the posable trains and the chair's grip and hanger, below), with the origin on the
  real axle or hinge pin.
- `<id>_socket_<name>` empties:
  - terminals: `line`, `rope_{left,right}_{bw,out}`, `chair_load` or `chair_unload`, and `pier_top` (the Monta's
    two piers: `pier_top`, `pier_top_2`);
  - the Sessellift terminal footing: `foundation_base`, `pier_seat`;
  - chair: `grip`, `seat_1..4`, `bar_hinge` (the seats and the bar's hinge ride with the hanger);
  - snow guns: `base` (the origin, at grade), `nozzle`, and the hose couplers (`hose_side`, and `hose_bottom` or
    `hose_rear`).

LiftImport checks every pivot and socket against `out/lifts.json` to 1 mm, and nests the pivots and the riding sockets
as `lifts.json` says (see Posable parts). When an asset's spec has a `catalog`,
`lifts.json` carries its maker and the name the game shows (`LiftRig.Maker`, `LiftRig.CatalogName`).

| Channel | Meaning |
|---|---|
| **UV0** | Detail-atlas coordinates, box-mapped in metres. The atlas tile is in the integer part of x (`tile × 256 + metres + 128`) |
| **UV1 x** | Snow capacity of the face (the shader adds `_SnowLoad` on up-facing surfaces) |
| **UV1 y** | Livery mask: 1 on the drive hood panels, which take the player's colour |
| **UV2** | Palette swatch in `lift_palette.png`: albedo, with metallic and smoothness packed in alpha |
| **Vertex colour R** | Baked ambient occlusion (ray-cast, deterministic, floor 0.25; the ground plane only under what stands at its own 0.00: bases and snow guns, not heads, Sessellift terminals or mast sections) |
| **Submesh / slot** | `LiftStructure` (opaque), `LiftGlass` (tinted, drive LOD0-1), `LiftChair` |

`lift_trim.png` is the detail atlas: 4 × 4 tiles of 256 px, one metre each. The tiles are plain, paint,
galvanised, grating, checker plate, concrete formwork, hood panel, rubber and seat vinyl. Its channels are
RG normal, B cavity and A brightness, with 0.5 neutral. Faces pick a tile by palette class
(`textures.CLASS_TILE`) or by `Style.trim`.

## Posable parts (task P3-07)

The game poses the lift's moving parts once, at build, from the rope (the model contract,
`docs/plans/p3-07-model-contract.md`). Shapes are unchanged: only how the faces are grouped and where they pivot.

- **Part tags.** A builder draws as before and tags faces with their pivot (`MeshBuilder.grouped`); `separate_flush`
  runs once on the whole merged LOD, then `MeshBuilder.split` cuts the tagged faces out into parts. Pins, axles and
  tubes are tagged `<part>#pin` (one piece of their part; `#pin` alone is the body's). UV0 is box-mapped in the
  lift frame before a part moves onto its pivot, so the texture stays put. On posable assets a corner shared by two
  pulled faces of one plane moves 2 mm once, not twice.
- **Pivots** in `lifts.json`: `pos`, `axis`, `parent` (a pivot name or null; parents come first), `hinge` (true when
  a part sits off its axle at any LOD), `range` ([min, max] degrees for `Quaternion.AngleAxis(angle, axis)` from the
  rest pose, from `liftkit/clash.py`; wheels get the whole turn) with `rangeBy` (what stops it), and per asset
  `socketParents`. The FBX keeps the empties flat: Blender's FBX export with `bake_space_transform` misplaces a third
  level of nesting, so LiftImport builds the nesting in the prefab (`LiftRig.PivotParents`, `PivotHinged`,
  `PivotRange`).
- **Trains** (per rope side `l`/`r`; numbers count along +u from the same end as the sheaves):

| Asset | Hierarchy |
|---|---|
| `b8`, `d8`, the return's row | `eq_r` (the main pin under the crossarm) → `yoke_r1`, `yoke_r2` → `rocker_r1..4` → sheaves (the return's are `sheave_r2..9`) |
| `s6` | `eq_r` → `yoke_r1` (→ `rocker_r1`, `rocker_r2`) and `link_r1` (the lone rocker's link, on its top pin, its rocker plates rigid with it) → sheaves |
| `s4` | `yoke_r1` (the top pin, no equaliser) → `rocker_r1`, `rocker_r2` → sheaves |
| `c8` | `block_r` (rigid with both triangular frames) → `yoke_sup_r`, `yoke_hold_r` (overlapping the frames: range 0) → `rocker_rs1/2`, `rocker_rh1/2` → `sheave_rs1..4`, `sheave_rh1..4` |
| the drive's row | `beam_r` (the beam's pin through the plate) → `rocker_r1`, `rocker_r2` → `sheave_r2..5` |
| the Monta drive's exit row | `eq_l` (the long carrier, on the pin through its clamp under the front cross tube) → `yoke_l1` (on a hanger block) and `eq_l2` (the link bar, on the other block) → `yoke_l2`, `yoke_l3` → `rocker_l1..6` → `sheave_l1..12`; on the right rope the reference has it end for end: `eq_r` → `yoke_r3` and `eq_r2` → `yoke_r1`, `yoke_r2` |

  The drive's row equalises a little (owner): stops of ±10° on `beam_r` and ±5° on its rockers (spec
  `drive.entryTrains.pinStops`; the build clamps the clash range to them). Sharing the turn equally up to the row's
  18° limit takes 8.6° and 4.7°, and the rope then comes in from the bullwheel at most 0.81° off level. The return's
  row keeps a level entry at the bullwheel's height (its first sheave held) for exits of about 8.3° to 29.9°.
  The Monta drive's exit rows are the reference's own equaliser tree, with stops of ±10° on the carrier, link bar and
  yokes and ±5° on the rockers (spec `drive.exit.pivots.pinStops`). The reference's rows step 0°, 5° and 10°, so a
  rope leaving at its 10° cut up to 58 mm into the middle sheaves (owner): the build settles each row to that rope
  (`restExitDeg`, the turn shared equally, the first sheave held at the bullwheel's height; `settle_rows`), which moves
  sheaves up to 74 mm, and the game's station-rope arc now runs within 6 mm under every sheave. From that rest
  (`pose.band`) every sheave stays on the rope for exits of 5° to 16° on the left rope and 6° to 16° on the right,
  inside the clash ranges. Its plates are spread across the rope's plane so the parts can turn. Its guide sheaves and
  the return's deflection sheaves stay single fixed wheels (owner, confirmed at P3-09): the return is a fixed row,
  which the game takes as rigid (LT33).
  Sheaves are their own parts at LOD0 and ride in their rocker at LOD1; at LOD2-3 each side's whole train is one
  part under its top pivot, never the body. The main pin and the lug plates stay in the body.
- **The Sessellift chair:** `grip` (the origin, on the rope) → `hanger`, which turns about the grip's own stack axis
  (the cast arm's axis, reaching across to the rope, 8 mm under it), with no geometry added (owner). The hanger's
  socket rides with the hanger and turns inside the housing, which is turned round that axis, so no gap opens. LOD0-1
  are split; LOD2 stays one body.
- **Piers and footings:** every terminal's piers are really tall (owner): they run to 15 m below the 0.00 level, so a
  raised or lowered terminal still stands on them; `pier_top` marks a pier's top centre, and a terminal with more
  piers numbers the rest `pier_top_2`, ... (the Monta: the drive's left and right piers; the return's strut
  foundation, then its column's, whose top is 0.3 m below grade, P3-09 LT36). `sessellift_fgq4_terminal_footing`
  (kind `terminal_footing`) is the footing, placed by the game at grade under each.
- **Rope:** 1¾ in (`common.ropeDiameter` 44.45), the Monta's too (owner: rope sizes aligned; its sheaves sit 1.2 mm
  further from the rope than with its old 42 mm). The line heads' rows hang from fixed pins, so their sockets move with
  the rope; the terminal rows and the c8 rows sit 4.2 mm further from the rope than with the old 36 mm.
- **Ambient occlusion:** heads and Sessellift terminals are baked without the ground plane; a mast section between
  a cast-only ghost section above and one below, with every ray also cast mirrored, so its top and bottom corners
  agree (the build fails above 0.01). The scene is updated before the bake, so parts are baked where they hang.

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

## Monta FG4 (LP20)

| Asset | Origin | What it is |
|---|---|---|
| `monta_fg4_drive` | grade, on the bullwheel's axle | the bottom (loading) station: twin tubular booms, a ribbed rounded hood with a window band over the bullwheel, inclined legs on two piers, the Sessellift sheave trains; the rope leaves climbing over a hold-down row |
| `monta_fg4_return` | grade, on the bullwheel's axle | the top station: an exposed bullwheel on an H-section column and a diagonal strut, with a cross arm and deflection sheaves |

- **Gauge and rope:** the line gauge is the Sessellift's 4,120 mm (owner, P3-09), so the Monta uses the Sessellift
  tower heads. The spec's positions are the reference's, at its 4,895 mm (`common.referenceGauge`); the build scales
  the bullwheel's rim to the line gauge and moves every sheave in with its mounts (`monta_drive.gauge_shift`). Both terminals are lowered to the Sessellift chair's rope height
  (3,039 mm, `common.ropeElevation`), so the lift carries that chair unchanged.
- **Sockets:** as the Sessellift terminals (`line`, `rope_*_bw`, `rope_*_out`, the drive's `rope_*_hold`,
  `pier_top`, and `pier_top_2` for the second pier), but the drive has `chair_load` and the return `chair_unload`: the drive is at the bottom.
- **Spec:** `monta_fg4.json` is generated locally from the reference; its `_adjust` notes list every deliberate
  change (the lowering, the rope line, every pier carried down to 15 m below grade, `common.pierBottom`, with the
  drive's buried footing slabs left to the game's footing).

## Chairworks quad chair and the fixed grip (LP21-LP23)

| Asset | Origin | What it is |
|---|---|---|
| `chairworks_chair_detach` | the grip, on the rope | the body with the detachable grip, part by part: the jaw block, the carriage (arched top plate, webs, axle bar) and running wheels, two coil springs, the lever, the arm and the roller; the hanger narrows into the arm through a reducer |
| `chairworks_chair_fixed` | the grip, on the rope | the same body with the fixed grip (LP22); no safety bar for now (LP23) |

- **The body:** the hanger's dogleg into a clamp on the top bar; two inboard side frames of bent tube (300 mm
  knees, 120 mm corners, `prims.fillet`); a looped seat frame; a bench tilted up to the front and a low backrest.
  Tubes as the Sessellift chair's: hanger Ø80, frame Ø60, seat rails Ø52.
- **The fixed grip** (`fixed_grip.json`, `parts.fixed_grip`): one design for every fixed-grip chair, the
  Sessellift chair included. The hanger flows out of the grip's cast housing through a socket that shares the
  tube's first ring (`tube_path` returns its rings), so a chair builds its hanger before its grip.
- **Sockets:** `grip` (the origin) and `seat_1..4`.

## Budgets (enforced)

The build exits non-zero if an LOD is over budget, if totals don't decrease, if a wheel is off its axle,
if a rope socket is off the rope, or if a mast section's AO doesn't tile.

| Asset | LOD0 | LOD1 | LOD2 | LOD3 |
|---|---|---|---|---|
| Terminal budget | 24,000 | 10,000 | 2,500 | 500 |
| Drive terminal | 13,902 | 6,782 | 1,776 | 242 |
| Return terminal | 19,852 | 7,016 | 1,824 | 304 |
| Terminal footing (budget 12, one level) | 10 | - | - | - |
| Monta drive terminal | 20,338 | 9,562 | 2,050 | 394 |
| Monta return terminal | 3,636 | 1,820 | 944 | 360 |
| Chair budget | 1,600 | 250 | 60 | - |
| Sessellift quad chair | 984 | 246 | 60 | - |
| Chairworks chair, detachable grip | 1,596 | 248 | 56 | - |
| Chairworks chair, fixed grip | 752 | 186 | 56 | - |
| Tower head budget | 16,000 | 5,000 | 1,000 | 200 |
| Head `s4` | 7,512 | 2,384 | 484 | 96 |
| Head `s6` | 10,720 | 3,248 | 620 | 96 |
| Heads `b8`, `d8` | 13,848 | 4,064 | 732 | 96 |
| Head `c8` | 13,792 | 4,008 | 732 | 120 |
| Mast section budget | 150 | 100 | 50 | 16 |
| Mast section | 144 | 88 | 40 | 12 |
| Tower base budget | 400 | 300 | 60 | 40 |
| Tower base | 314 | 278 | 58 | 36 |
| Leaned bases (`l025`-`l150`) | 308 | 272 | 56 | 34 |
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
