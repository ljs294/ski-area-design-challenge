# Snow rendering: depth, piles, grooming and conditions

**Audience:** the project owner and coding agents. **Status:** design for review. The direction was endorsed by the owner on 2026-10-02 ("noted, this is how I want it to behave"); nothing here is built yet beyond what §1 lists. **Date:** 2026-10-02. **Applies to:** snowmaking and grooming (Phase 3) and the snow simulation (Phase 4, [0.6](phase0-0.6-milestones.md)). It builds on task 10's seams ([0.3 §4.6](phase0-0.3-technical-architecture.md)) and task 12b's snowpack.

**In one line:** snow becomes a real layer of the world. Its depth lifts the surface you see and stand on, snow guns pile whales that groomers push out into runs, and a surface-state map says what the snow is like, which both its look and the Snow conditions info layer read.

## 1. Where we are

| Piece | Today | Where |
|---|---|---|
| Snow cover | A splat weight per texel; the shader lays snow over the land share and thin snow lets the ground show | `SplatTexels`, `MountainTerrain.shader` |
| Snow depth | `SnowDepthField`: metres per 8 m cell over the ring, filled at open by the natural snowpack (elevation, sun, wind, canopy, shedding) | `Domain/Snow`, task 12b |
| Upload | Dirty rows into an RFloat texture, once a frame at most | `SurfaceStates.Sync` |
| What depth does | Coverage only: from 0 to 15 cm the ground shows through. **Snow has no thickness**: the surface is the bare-earth lidar | |
| Info layers | Snow depth (live), Snow conditions (reserved) | `MapLayers`, `InfoLayers.hlsl` |
| Snow on things | Trees, lift structures and cliff ledges have a 0–1 snow load | Tree and lift shaders |

The lidar was flown without snow, so a 2 m mid-winter snowpack, a 6 m whale and a groomed run all sit on ground that renders as if they weren't there. Everything below fixes that without a new terrain system.

## 2. Goals

1. **Snow has thickness.** What you see, what the camera and skiers stand on, and where lift towers meet the ground all include the snow.
2. **Snowmaking shows.** Guns build whales (long mounds 2–8 m high) over nights, visible on the mountain and in the Snow depth layer.
3. **Grooming shows.** Groomers push whales across the run and leave corduroy; skier traffic wears it into chop and, on steep pitches, moguls.
4. **Conditions are visible.** Powder, packed, groomed, ice, crud and spring snow each look different, and the Snow conditions info layer maps them.
5. **Cheap and incremental.** Only changed areas re-upload (dirty rectangles), and nothing allocates per frame (AGENTS.md).
6. **Deterministic.** The simulation owns the fields: snapshots out, commands in. The same inputs give the same snow.

## 3. Data: two depth fields and a surface state

| Field | Cells | Covers | Written by | Why |
|---|---|---|---|---|
| **Natural depth** (exists) | 8 m | The whole ring | Snowpack v0 now; weather (Phase 4) later | Big-scale snowpack: elevation, aspect, wind, canopy |
| **Managed depth** (new) | 1 m, in 64 m sparse tiles | Only where snow is made, groomed or skied (runs and their edges) | Snowmaking, grooming, skier wear | Whales and run edges need metre detail; 1 m over a 10 km ring would be 100 M cells, but runs are a few percent of that |
| **Surface state** (new) | 1 m, with the same tiles as managed depth, plus 8 m for the rest | Same | Grooming, traffic, weather, sun | What the top layer is like: type, age, compaction, grooming direction |

**Total depth = natural + managed.** Managed depth can be negative where a groomer pulls snow away; it never takes the total below zero.

The surface state is packed per cell, about 4 bytes:
- type: powder, packed, groomed, ice, crud, spring or wind-board;
- age (hours since it was groomed or since new snow);
- moguls (0–1);
- groom direction (an angle, for the corduroy).

It's the data the **Snow conditions** info layer reserves.

**Sparse tiles:**
- A tile exists only once something writes to it. A drawn run allocates tiles along its corridor.
- Memory: a 64 m tile at 1 m is 4,096 cells. Depth (2 bytes) plus state (4 bytes) is about 24 KB per tile. A big resort's runs, say 60 km at 60 m wide, come to about 1,000 tiles, or 24 MB.
- On the GPU, the tiles live in a **tile atlas** (an RG16/RGBA8 texture array) with a page table. Changed tiles re-upload by dirty rectangle.

## 4. Rendering

### 4.1 Snow with thickness: displacement

The terrain's vertex shader lifts each vertex by the total depth there: natural from the 8 m texture, managed from the atlas through the page table.
- **Unity Terrain** draws its own mesh, but our custom terrain shader controls the vertex stage, so this works with today's tiles. Near the camera, tiles are 1 m, matching the managed field. At distance, the coarser LOD averages the depth naturally.
- **Seams:** neighbouring tiles read the same depth texture at the same world positions, so their edges stay matched, as the heights already do.
- **Shadows, depth and picking** use the same displaced position, so the ShadowCaster and DepthOnly passes include it.
- **Gameplay heights:** `ITerrainSurface.HeightAt` becomes ground plus depth. A `GroundAt` stays for things that sit on the earth, such as lift tower footings. Camera bounds, skiers, labels and the info readout all use `HeightAt`.

### 4.2 The look of the surface

- The **snow layer** in the terrain shader picks its albedo, normal and roughness from the surface state:
  - groomed: corduroy ridges about 0.3 m apart along the groom direction, fading with age and traffic;
  - powder: soft and bright with sparkle;
  - packed and crud: rougher, with tracks;
  - ice: smoother, greyer and specular;
  - spring: wetter and darker, with sun cups;
  - wind-board: flat with sastrugi ridges.
- **Moguls:** a procedural bump field (moguls about 3–5 m apart, offset by the fall line) adds both normal and a little displacement, scaled by the mogul value.
- **Whales:** displacement alone shows them. A lighter tone in fresh machine snow fades as it ages.
- **Run edges:** where managed depth meets natural depth, a soft berm forms by itself from the 1 m field.

### 4.3 Info layers

- **Snow depth** (exists) reads total depth: natural and managed together.
- **Snow conditions** (reserved) colours each surface type, with a legend (powder, packed, groomed, ice, crud, spring, wind-board) and the cursor readout: *Groomed 3 h ago · Packed*.

## 5. Simulation (Phase 3–4, for scale)

- **Snowmaking:** each gun deposits snow in a footprint downwind of its head. The footprint is an elongated Gaussian about 30–60 m long, shaped by the wind direction and the gun's throw, at a rate set by water flow, temperature and humidity (the old game's hydraulics are reference). Overnight that builds whales several metres high. Writes go into managed depth (1 m), dirty-rectangle.
- **Grooming:** a groomer drives a path. Its blade pushes snow toward lower spots in a 5–6 m swath, flattening along its track (a transport step plus a diffusion step), then stamps that swath as groomed with its direction and age 0.
- **Skier traffic:** packs powder to packed, turns groomed into crud as the day goes on, builds moguls on ungroomed steep pitches, and scrapes ice on busy steep spots. Traffic comes from the guest simulation.
- **Weather:** snowfall adds to natural depth and marks new powder; sun and warmth turn snow to spring or ice; wind scours ridges and loads lee slopes, as the snowpack v0 does but over time.
- Everything runs in Simulation with keyed randomness and a stable order. The renderer only copies dirty tiles.

## 6. Performance budgets

| Item | Budget |
|---|---|
| Vertex displacement | Two texture reads per vertex (natural, managed via the page table). Under 0.1 ms at 1080p |
| Surface look | Only on snow pixels. Corduroy and moguls are procedural, so no new textures beyond a small atlas (about 16 MB) |
| Uploads | Dirty tiles only. A gun or groomer touches a few tiles per simulated hour; budget 0.2 ms a frame |
| Memory | About 24 MB of CPU state for a big resort's runs, plus the GPU atlas |

## 7. Phasing

| Step | What | When |
|---|---|---|
| S1 | Displacement from natural depth; `HeightAt` includes snow; `GroundAt` for footings | Early Phase 3 (needs nothing else) |
| S2 | Managed depth (sparse 1 m tiles, atlas and page table) with a debug brush to paint whales | With the snowmaking tools |
| S3 | Snowmaking deposits whales | Snowmaking (Phase 3) |
| S4 | Surface state; groomed corduroy; the Snow conditions info layer goes live | Grooming (Phase 3) |
| S5 | Traffic wear, moguls and ice; weather over time | Simulation (Phase 4) |

## 8. Questions for the owner

1. **How tall should whales get?** Real ones are commonly 3–6 m, and up to 10 m at big resorts. I'd suggest **up to 8 m**.
2. **Should grooming be animated** (you watch the groomer push a whale), or happen between turns at night? I'd suggest **at night**, with a morning reveal.
3. **Should moguls form by themselves on ungroomed steep runs?** I'd suggest **yes**, as a visible result of your grooming plan.

## Appendix: 12d handoff scope, ground types from OpenStreetMap

12d is queued in the task 12 thread after tasks 13 and 14 merge, because it changes the download and cache code.
- **New ground types** from OpenStreetMap, by tag:
  - paved road (`highway` on asphalt or concrete surfaces, or the major classes);
  - gravel or dirt track (`highway=track`, or an unpaved `surface`);
  - building (`building=*`);
  - parking (`amenity=parking`);
  - **ski run** (`piste:type=downhill`): mown meadow in summer, groomed snow in winter.
- **Data path:**
  - the OSM download and rasterising (Acquisition);
  - new cover bands;
  - the splat's two free channels plus a third splat texture;
  - **TerrainCache v11** (a rebuild; golden hashes re-pinned with the owner's approval).
- **Look:** asphalt and gravel already exist (12c). Buildings get a roof-grey, and ski runs a mown-grass texture (one more CC0 asset).
- **Info layers:** the F1 cover map gains the new types.
