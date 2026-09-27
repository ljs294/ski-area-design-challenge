# Tree species pipeline (TR1, TR2, TR4)

A Blender script builds the species library from parameters, with no manual modelling. It's free and repeatable: the same species and variant always produce the same mesh and textures. The plan is in [0.5 §3 and §6](../../../docs/plans/phase0-0.5-art-direction.md), and the look is reviewed in [phase1-trees-first-look.md](../../../docs/plans/phase1-trees-first-look.md).

Each tree is a branching skeleton with textured bark, dressed with **alpha-textured cards**: needle sprays for conifers, and leaf clusters plus bare-twig silhouettes for deciduous trees. This is the same technique Tree It and SpeedTree trees use.

| File | Purpose |
|---|---|
| `species.json` | One entry per species: size, crown shape, branching, needle or leaf style, bark, and colours per season. `where` cites the BIGMAP share |
| `build_trees.py` | Builds 3 variants × 3 LODs per species and exports one FBX per variant, plus its textures |
| `textures.py` | Procedural textures (numpy): needle sprays, leaf clusters, twigs, bark |
| `render_preview.py` | Review renders (Cycles): lineups, seasons, a close-up, groves, LODs and shader data |
| `build-trees.bat` | Double-click: builds everything and opens the renders |

```sh
blender -b --factory-startup --python tools/assets/trees/build_trees.py -- --out tools/assets/trees/out --render tools/assets/trees/out/renders
# only some shots: --shots conifers,deciduous,seasons,closeup,groves,lods,data   (a subset renders much faster)
```

This needs **Blender 5.2 LTS** (free). The output in `out/` isn't committed; it is rebuilt from the script. Building takes about 10 s; the full render set takes about 8 minutes on the reference PC.

## Species (11)

| Conifers | Deciduous |
|---|---|
| Subalpine fir, Engelmann spruce, Douglas-fir, lodgepole pine, mountain hemlock | Quaking aspen, paper birch, yellow birch, sugar maple, red maple, American beech |

## What the tree shader reads

Each FBX holds `<species>_v<N>_LOD0`, `_LOD1` and `_LOD2`. Unity turns that naming into an LODGroup on import. Textures sit in `textures/`, next to the FBX files.

| Channel | Meaning |
|---|---|
| **Vertex colour R** | Trunk sway weight: 0 at the ground, 1 at the top (height²) |
| **Vertex colour G** | Branch flex: 0 at the trunk, 1 at the branch tip |
| **Vertex colour B** | Per-branch phase (0–1), so branches don't move in lockstep |
| **Vertex colour A** | Leaf and needle flutter: 0 on bark, up to 1 on foliage |
| **UV0** | Texture coordinates. Cards are alpha-tested: needle sprays, leaves and twigs |
| **UV1 x** | **Snow mask:** how much snow the up-facing side can hold. The shader multiplies it by the snow-load input and applies it to front faces only |
| **UV1 y** | **Season flag:** 1 = a leaf that drops in autumn; 0.5 = a leaf kept, dry, through winter (beech); 0 = permanent |
| **UV2 x** | **Season order:** a random value per leaf card, which sets when it comes out, turns colour and falls |
| **UV2 y** | **Crown height** of the card (0 = crown base, 1 = top), so the season can move up or down the crown |
| **Submesh 0** | Bark |
| **Submesh 1** | Needles (conifers) or leaves (deciduous; swap summer and autumn textures, hide in winter) |
| **Submesh 2** | Bare twigs (deciduous): the fine winter crown |
| **Submesh 3** | Leaves kept through winter (beech) |

## Performance budgets (enforced)

A 5 km site holds about 650,000 trees, so every build checks each variant against a per-tree budget and **exits non-zero if any tree is over**, like a failing test. The budgets are `BUDGET` in `build_trees.py`; the audit plan is 0.3 §8.1.

| Level | Used for (planned) | Budget | Current range |
|---|---|---|---|
| LOD0 | 0–30 m | ≤ 10,000 triangles | 2.3k–9.7k |
| LOD1 | 30–80 m | ≤ 2,500 | 0.7k–2.4k |
| LOD2 | 80–150 m | ≤ 500 | 190–470 |
| Impostor | beyond 150 m | 2 | baked in Unity |

`out/trees.json` records the triangles and the **alpha-card area** (m², a proxy for overdraw) of every LOD, plus any budget failures.

## Adding a species

Add an entry to `species.json`, using `"form": "conifer"` or `"deciduous"`, and rebuild. To find which species a real site has, run the data spike: `dotnet run --project tools/data-spike/src/DataSpike.Cli -- species --lat <lat> --lon <lon> --km 3`.
