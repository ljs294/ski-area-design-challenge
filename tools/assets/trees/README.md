# Tree species pipeline (TR1, TR2, TR4)

A Blender script builds the stylized species library from parameters, with no manual modelling. It's free and repeatable: the same species and variant always produce the same mesh. The plan is in [0.5 §3 and §6](../../../docs/plans/phase0-0.5-art-direction.md).

| File | Purpose |
|---|---|
| `species.json` | One entry per species: height range, crown shape, branching, droop, preview colours, BIGMAP code |
| `build_trees.py` | Builds 3 variants × 3 LODs per species and exports one FBX per variant |
| `render_preview.py` | Review renders (Cycles): lineup, LODs, grove, and the snow and wind data |
| `build-trees.bat` | Double-click: builds everything and opens the renders |

```sh
blender -b --factory-startup --python tools/assets/trees/build_trees.py -- --out tools/assets/trees/out --render tools/assets/trees/out/renders
```

This needs **Blender 5.2 LTS** (free). The output in `out/` isn't committed; it is rebuilt from the script. About a minute on the reference PC, most of it rendering.

## What the tree shader reads

Each FBX holds `<species>_v<N>_LOD0`, `_LOD1` and `_LOD2`. Unity turns that naming into an LODGroup on import; the impostor comes from our own baker in Unity (TR2).

| Channel | Meaning |
|---|---|
| **Vertex colour R** | Trunk sway weight: 0 at the ground, 1 at the top (height²) |
| **Vertex colour G** | Branch flex: 0 at the trunk, 1 at the branch tip |
| **Vertex colour B** | Per-branch phase (0–1), so branches don't move in lockstep |
| **Vertex colour A** | Leaf and needle flutter: 0 on bark, up to 1 on foliage tips and leaves |
| **UV0** | Bark and foliage texture coordinates (for the shared atlas later) |
| **UV1 x** | **Snow mask:** 1 on ridges that carry the snow load, 0 on undersides and flanks. The shader multiplies it by the snow-load input (full in iteration 1) |
| **UV1 y** | **Seasonal leaf flag:** 1 on leaves that drop. In winter the shader discards them; seasons come later |
| **Submesh 0** | Bark |
| **Submesh 1** | Foliage (conifers) or leaves (deciduous), kept separate for seasons |

## Budgets (measured)

| Species | LOD0 | LOD1 | LOD2 |
|---|---|---|---|
| Subalpine fir | 5.5–6.2 k tris | 1.7–2.0 k | ≈290 |
| Mountain hemlock | 3.6–4.4 k | 1.2–1.3 k | ≈230 |
| Quaking aspen (bare) | 3.2–4.3 k | 1.2–1.6 k | ≈260 |

LOD0 is only for trees near the camera; beyond about 300 m the impostor takes over (0.3 §4.5).

## Adding a species

Add an entry to `species.json`, using `"form": "conifer"` or `"deciduous"`, and rebuild. New forms, such as whitebark pine's broad crown or krummholz, need a new builder function in `build_trees.py`.
