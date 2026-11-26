# Building asset pipeline (task 4.50)

Blender scripts build the resort's buildings from a dimension file, with no manual modelling, on top of the lift kit
([../lifts/README.md](../lifts/README.md)): the same MeshBuilder, primitives, palette, detail atlas, AO bake and FBX
export. The same spec always produces the same meshes (the build builds every level twice and compares hashes). The
roadmap's buildings, one phase each, every one through design gates before anything goes into Unity:

1. **Pump house** (`bldg_pumphouse`, the 4.15 snowmaking pump house): a plain insulated-metal-panel building on a
   concrete curb, 60 x 40 ft, 16 ft eave, 4:12 gable, with pad-mount transformers. A board-sided barn style may follow
   as an option.
2. **Booster** (`bldg_booster`, the game's Inline pump: 250 hp, no compressors): the pump house's family at 30 x 20 ft,
   12 ft eave, the main running straight through its gables.
3. **Lift huts** (`bldg_hut_large`, `bldg_hut_small`, `bldg_hut_mini`): factory operator huts to the maker's
   layouts, flat-roofed panel boxes on pipe posts with the glazed front and a deep canopy toward the chairs, the door
   round the corner toward the load or unload area, a window in each end wall (up and down the line); placed on
   the side where the chairs go up, the door and stair at the end away from the line (mirrored where a station needs it), the fascia and the wainscot in the lift's livery. The hut follows
   the drive, not top or bottom: the large (wider than deep: the operator along the glazed front, the drive's cabinets
   along the back wall) at a detachable's or gondola's drive station, the small at its return station and at a fixed grip's drive,
   the mini (on skids) at a fixed grip's return. Surface lifts use the T-bar's own hut and carpets theirs (lift kit). A
   rustic style may follow as an upgrade.
4. **Lift extras.** First the **unload ramp** (`bldg_ramp_quad`, `bldg_ramp_triple`, `bldg_ramp_double`) at a fixed
   grip's top return terminal whose unload stands 5 ft or more over the ground (a top drive gets none): a timber deck
   on posts, double wide (across both ropes, centred on the lift), level for 4.5 m at the unload, and one ski-off ramp
   ahead across the full width, falling at 30 % (the code's limit) and flaring 1 in 10 a side, with no rails or
   walls (the level deck has a 42 in timber guard rail round the hut bay and along the return side's outer edge, and a 2x4 lip marks every other edge: the ramp's two sides and the rear edge, but not the lane where the riders' skis come in; the bay ends flush with the hut's back wall). One model for every lift, whatever its maker and wherever it stands (owner, 2026-11-24): only the chair's
   width changes it, so three models, and clipping makes that work. Each is built for its tallest site (a 4 m raise,
   a 20 m ski-off, posts down past the ground), so the terrain clips the ramp and its posts; the lift's terminal
   stands through the plain deck (boards and joists clip round its columns), and only the post rows and beams keep
   clear of every terminal (the Sessellift, the Stern and the Monta, measured on the lift models).
   No ramp runs back down the lift line. Under 5 ft the game grades an earth mound of the same plan instead. A mat
   as wide as the chair and a deck bay where the hut stands in its own opening on its own steel posts (its skids,
   step, landing and stair hidden), with the operator's pedestal and phone (`bldg_op_pedestal`) beside it, and the
   operator's walkway up from the snow: the ski-off ramp carries on 1 m further on the hut side, at its slope, and
   that strip has cleats across it. No light pole and no signs. The stop gate copies the real ones (one per terminal style, `bldg_stop_gate_<terminal>`):
   a slim white post on the ramp past the unload, on the lift's centreline, and a 1 in bar from its top along the line,
   under the chair as it turns round the far end of the wheel, with a switch at the pivot; a rider still seated
   strikes it. Then a genset with its transfer switch, and the parking building.
5. **Groomer garage** (`bldg_garage`): the smallest garage, a two-bay maintenance shop in the pump house's family,
   60 x 40 ft, 20 ft eave, 4:12 gable. A service shop, not parking (groomers park outside): each 24 x 40 ft bay serves
   one machine at a time behind its 20 x 16 ft overhead door, the doors on their own pivots so the game can roll them
   up; a 12 ft shop end (office, parts room) with man doors on the yard side and the gable, the -u bay a wash bay with
   a high louvre. Styled like the pump house: dark doors, a row of small high windows along the back and on the
   wash-bay gable (eye-level ones only in the office), ridge vents. The fuel pad (`fuel` part) lies along the
   shop end: a 6,000 gal protected double-wall tank on saddles 5 ft off the wall, a dispenser on a curbed island
   beside it, the fuelling lane beyond, bollards and a pole light. The build checks that a groomer (blade folded,
   cab tilted) and a pickup pass each door and fit each bay with 0.3 m spare, and the tank's and the lane's
   distances to the wall.

| File | Purpose |
|---|---|
| `pumphouse.json` | The pump house's dimensions in millimetres: footprint, eave, pitch, roof, curb, doors, window and louvre rows, wall packs, door-hood lights, door sconces, the yard pole light, transformers and their pad, ridge vents, vaults, apron, snow guards, sockets, lights, and `review` (its Gate 1 close-ups and Gate 2 shots) |
| `booster.json` | The booster's, with the same keys |
| `garage.json` | The groomer garage's, with the same keys and the garage's own: gable doors' `at`, a door's `part`, `aprons` (one pad along several doors), sockets as a `pos`, `fuel` (the fuel pad: pad, tank, island, dispenser, bollards, pole, lane) and `fit` (the machines and the spare each door and bay must leave, and the roof over them); `review.machines` puts groomer and pickup scale boxes in the clay views, `review.mmPerPx` and `orthoHide` set the drawings' scale and leave the fuel pad out of the elevations. The builder still reads a door's `style`, `lightBand` (a translucent strip under the eave that glows) and `signs` (a board the game letters, gooseneck lights over it), the garage's flair before round 7 |
| `lifthut.json` | The three lift huts: `common` (floor height, roof height, fascia, canopy, wainscot, parapet, front window, side window, door and its lite, landing, stair, rails, how far posts run below the origin, sconce, downlight, lights) and per `variants` entry its id, footprint (`length` across the front, `width` front to back), posts or skids, the far end wall's window, the large hut's panes, door side, awning, stair along the wall and conduits, canopy downlights, sconces and `review` (its `ground` puts the Gate 1 ground below the origin, as at a top station) |
| `unloadramp.json` | The unload ramps: `common` (pier clearance, the level deck before and after the unload point and where the down side starts (`downBack`), `terminals` (per top return style, measured on the lift models: its bullwheel's axle past the unload point, the front face of
the column the stop gate's laser is clamped to, what stands through the deck and what lies just under it), the least distance from a post or beam to a terminal (`keepClear`), the slope and the most the code allows (`slopeMax`), the tallest site the model serves and the ski-off's run as modelled (`reach`), flare, how far the ramp's end must lie under its tallest site's ground (`clip`), deck, beams and posts, the mat's margin past the chair, the stop gate's bar (its drop under the chair, the stretch it covers round where the chair crosses the line, its least height over the surface), hut bay (the hut's footprint, floor and threshold, its steel posts, the bay's width to the hut's back wall), the level deck's guard rail (height, post, top and mid rail sizes, post pitch, its gap to the hut, `returnSide` for the run opposite the hut), the lip along the edges with no rail (`lip`, a 2x4 laid flat), the operator's walkway (its width on the ramp's hut side, the most slope allowed, its cleats' size and pitch and the most allowed), where the operator's pedestal stands, the rope's height over the deck and the chair's drop to its lowest point and to its seat, cost and the review's default `site`) and per `variants` entry its id, the lift's gauge, the width at the unload and the chair's width; `review.cases` are the Gate 1 sites (raise, ground falling each way, `terminal`, `mound` for one under 5 ft, `slope` to try another slope): the same model stands on each |
| `stopgate.json` | The stop gate: `common` (the bar's height, section and length, the post's section, height over the bar, base plate and the most its foot may bury, the brace, the switch) and per `variants` entry (one per terminal style) its id; `pick` sets the post's depth to the ramp's surface under it |
| `oppedestal.json` | The operator's outdoor pedestal with a phone: post, control head (stop and slow buttons), phone box |
| `budgets.json` | Triangle budgets per LOD and LOD switch distances; the far level is a box and a roof, never an impostor |
| `build_buildings.py` | Builds every building and LOD, bakes AO, exports one FBX per building with its sockets, and checks budgets, falling totals, a second build's hash, the walls' footprint and top (a gable's apex, a hut's panel band) to 1 mm, required sockets, a socket for every light, and no light's conduit across a window, door or louvre; a ramp's slope (30 % at most), its chair height (the seat pad's top) over the deck (0.56 m at most), the stop gate's bar over the unload level, no post or beam within `keepClear` of a terminal (for every terminal in the table), the stop gate's post clear of its column and its bar on the model for each, the post's foot on the ramp, the hut's opening inside the deck, the walkway's slope and cleat pitch, its end at least `clip` under its tallest site's ground, no timber above the deck but the hut bay's rail (42 in, its mid rail about halfway, clear of the chairs); writes `out/buildings.json` |
| `render_review.py` | Gate 1 review renders, Workbench only (back faces culled, as the game draws them): orthographic elevations and plan with a `_meta.json` each (mm per pixel, origin pixel, every socket and light in pixels), clay views with a 1.8 m figure, close-ups of the doors, a window and a wall pack, the LOD lineup |
| `render_ramp_cases.py` | Gate 1 ramp sites, Workbench only (`--cases DIR`): the one model of each `review.cases` entry's chair width stood on a solid block of ground for that case's raise and fall, which clips it, with the lift's own top return terminal and chairs from the committed lift models (`--terminals DIR`, default `Assets/MountainPlanner/Art/Lifts/Models`; a grey stand-in if they are not on disk), the mini hut in its opening with its legs part hidden, the operator's pedestal, the stop gate where `unload_ramp.gate_at` puts it, and a figure; a 3/4 view, the riders' approach, the front (from down the ski-off), the column through the deck, the stop gate and its post close, the hut, the walkway, the pedestal, plans from above with and without the lift, and an orthographic side, the LOD lineup on ground at its level, and `ramp_cases.json` (the model's dims and each site's report: run, width where it meets the ground, deck area, cost, the gate's place and the stretch its bar covers, the bar's least height over the surface); with `--photos DIR` (`--preview 1` in Workbench) the ramps' Gate 2 stills in Cycles from the same scenes: `review.photos.sets`, each a case (the quad, triple and double, each at another maker's terminal) and its shots [shot, view, day or snow] on a meadow at the site's level, the deck's planks from the detail atlas |
| `render_photos.py` | Gate 2 review pictures in Cycles (`--photos DIR`; `--preview 1` frames the same shots in Workbench): day views on a gravel pad by a pond (a hut: level ground at its review height, `review.pad`), close-ups, dusk with every light record placed as a lamp and the glow masks lit, and one in snow (a hut: dusk in snow too); a hut's `review.livery`, or a shot's eighth entry, paints it in a lift's colour. Demo 159 |
| `assets/*.py` | One module per building (`pumphouse`, `booster`, `garage`): its id and spec over the shared builder (the garage adds its fit check; `--assets garages` builds it); `lifthut` gives a handle per hut (`--assets lifthut` builds all three, or name one: `hut_large`), `unloadramp` one per ramp (`ramp_quad`) |
| `bldgkit/` | The building kit: `metal_building` (the metal-panel building from its spec, a gable roof or a single slope (`shed`: its high side), shared by the pump house, the booster and the garage; it checks every opening stays on its wall, under the eave and clear of the others), `fuel_pad` (the garage's fuel pad), `unload_ramp` (the unload ramp, one model per chair width: its double-wide deck with a framed opening round the hut, the ski-off ramp out to its reach, posts, beams, joists and bracing clear of every terminal, the hut's steel posts, the parts and the hut bay; `gate_at` places the stop gate past a terminal's wheel and gives the stretch its bar covers, and `site_report` gives a site's run, deck area and cost), `stop_gate` (the stop gate: a post on the ramp past the wheel and a bar along the line, one per terminal style), `lift_hut` (a factory operator hut: walls in three bands, a parapet roof, the canopy with its downlights, pipe posts or skids, a steel landing and a stair with rails, straight out from the door or down along the wall), `shell` (walls with openings and reveals, gable infills, gable roofs), `fixtures` (sectional overhead doors with a row of lites, man doors with frames, hinges, kick plates and hoods, framed windows with sills and drip caps, louvres, LED wall packs with conduit to the eave, downlights under door hoods, cage sconces beside doors, gooseneck sign lights, yard pole lights, transformers, pads, ridge caps and vents, trims, bollards), `op_pedestal` (the operator's outdoor pedestal with a phone), `palette` (the building classes, registered into the lift palette from swatch 128) |
| `build-buildings.bat` | Double-click: builds everything and opens the Gate 1 renders |

```sh
blender -b --factory-startup --python-exit-code 1 --python tools/assets/buildings/build_buildings.py -- --out tools/assets/buildings/out
# review renders: --render DIR   ramp sites: --cases DIR   diagnostics: --assets pumphouse   --ao off
```

This needs **Blender 5.2 LTS** (free). `out/` isn't committed. The build and its renders take about 30 s.

## The building frame
The lift frame (`liftkit/frame.py`) with its origin at the footprint's centre on the finished floor: `u` along the
long axis (Unity +Z, the game's bearing), `v` across it (Unity +X), `w` up. Specs are in millimetres, meshes in
metres. A wall is named by its outward normal (`+v` the pump house's yard side, `-v` its water side; a hut's `+v` is its
front, the glazed side that faces the chairs, and its door is in an end wall, `-u` or `+u` by its `doorSide`). A hut's origin is on the load or unload level,
its floor `floor` above it; its posts, stair and landing legs run on below the origin for ground that falls away (the
stair at its own pitch, down to the posts' depth, so the ground clips it at any height). Those posts, conduits,
stair, rails and landing legs are a separate part, `<id>_legs_LODn` (pivot at the origin), so the game can hide them
when the hut stands on a ramp's deck bay. A ramp's origin is the unload point on the deck top, under the up rope: `u`
toward the ski-off, `v` outboard toward the hut (the lift's centreline at `-gauge/2`, the deck centred on it), `w` up. A building is a
closed shell with no interior: every opening has reveals into the wall and a fixture filling its back, because the
game culls back faces. Windows sit in their own material slot, so detail behind the glass can come later without a
re-export.

## Materials and masks
Two slots: `BuildingStructure` and `BuildingWindow` (opaque dark glass). Both read the lift palette (UV2) and detail
atlas; the building classes take swatches 128 on (`bldgkit/palette.py`), so the one atlas serves lifts and buildings.
UV1.x is snow capacity, as on the lifts (roofs 0.6, pads and transformers 1.0). UV1.y is the glow mask: **3** a lamp's
lens, lit by its light's schedule; **4** window glass, glowing warm while the building runs. The huts' fascia,
canopy and wainscot carry **1**, the lift's livery, as the lift kit does.

## Lights
Every light is a `light_<name>` socket plus a record in `buildings.json` (`lights`, Unity frame): kind (`spot` or
`point`), position, aim, colour temperature (K), lumens, range (m), cone (degrees) and a schedule tag. The building's
window glow is its `glow` record (temperature and schedule). A light never sits on top of a door hood or over a
transformer: man doors get a downlight under their hood, and the pad is lit from a pole beside it (the build fails
otherwise). A hut's downlights are recessed under its canopy; a cage sconce sits beside each door. The hut's
downlights and windows are lit while its lift runs; its sconce stays on from dusk to dawn (owner 2026-11-25, task 4.50
slice 3). The game reads the records into each prefab's `BuildingLights` (BuildingImport). Schedule tags:

| Tag | On when |
|---|---|
| `dusk` | From dusk to dawn (exterior lights) |
| `running` | While the building works (a pump house's windows while its pumps run) |
| `lift_open` | While the lift is open (operator huts) |
| `night_shift` | While the night crew works (the garage) |

## Sockets
**Pump house:**
`intake` (the water side, at the pipe's depth: where the lake's intake line or a feeding pipe enters), `discharge` (the east gable, at the header's depth), `door_main`,
`door_yard`, `door_water` (each door's sill, on the wall line), `label` (the name over the ridge) and one per light.
**Booster:** `inlet` (the west gable) and `discharge` (the east gable), both at the main's depth, so the main runs
straight through; `door_main`, `door_man`, `label` and one per light.
**Lift huts:** `door` at its sill, `label` (the lift's name board on the canopy's
fascia), `view` (the operator's eye at the front window: the game turns the hut's `+v` toward the chairs) and one per
light.
**Unload ramps:** `chair_unload` (the rope over the unload point, matching the lift's own socket), `breakover` (the
middle of the ramp's top edge), `ramp_end` (the middle of the model's buried end), `hut` (the hut's origin in its
opening: its floor a threshold over the deck), `pedestal` (where the operator's pedestal stands) and `walkway` (the middle of the walkway's top edge, on the hut bay's front edge).
**Groomer garage:** `door_1`, `door_2` (the overhead doors' sills), `door_man` (the shop end's), `bay_1`, `bay_2` (the
middle of each bay's floor, where a machine stands while it is serviced), `fuel` (the fuelling lane beside the
dispenser), `fuel_fill` (the tank's fill box, for the delivery truck), `label` and one per light. Pivots: `door_1`, `door_2` (each leaf at its head, along the wall) and
`fuel` (the pad's centre).
**Stop gate:** `pivot` (the bar's root on the post) and `bar_end` (its tip); its origin is on the post's axis, on the
lift's centreline at the unload level (where `unload_ramp.gate_at` puts it), the bar pointing `+u` (out past the wheel).
**Operator pedestal:** `stop` (the stop button) and `phone` (the phone box's door); its head's face looks toward `+v`,
the side the operator stands on.
