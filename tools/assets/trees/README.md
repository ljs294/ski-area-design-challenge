# Tree species pipeline (TR1, TR2, TR4)

A Blender script builds the species library from parameters, with no manual modelling. It's free and repeatable: the same species and variant always produce the same mesh and textures. The plan is in [0.5 §3 and §6](../../../docs/plans/phase0-0.5-art-direction.md), and the look is reviewed in [phase1-trees-first-look.md](../../../docs/plans/phase1-trees-first-look.md).

Each tree is a branching skeleton with textured bark, dressed with **alpha-textured cards**: needle sprays for conifers, and leaf clusters plus bare-twig silhouettes for deciduous trees. This is the same technique Tree It and SpeedTree trees use. The [tree realism review](../../../docs/plans/phase1-trees-review.md) explains the current LODs, textures and trunks.

- **LOD0** has one card per needle frond (conifers) or twig. **Conifer LOD1 and LOD2** use **branch-cluster cards**, a whole branch of fronds on one card: two per branch on LOD1 (one rolled up or down) and one on LOD2, rolled alternately, so mid-distance crowns stay full. Deciduous LOD1 uses one larger leaf card per twig, and LOD2 crossed twig cards on the main branches.
- **Needle textures are fronds**, styled per species (`FROND_STYLES` in `textures.py`): a tapering foliage body, side twigs and needles.
- **A card always shows its whole texture**, with the branch line along v = 0.5. The game's snow pattern (`SnowPattern` in `TreeCommon.hlsl`) and the impostor bake rely on that.
  - The first Pacific silver fir used a two-half atlas, top and underside, and lost its snow ([audit](../../../docs/plans/phase1-trees-audit.md)).
  - Its silvery underside is now a `sheen`: a share of the side needles, more toward the spray's edge, takes paler colours.
- **Trunks** have 12 sides on LOD0, a root flare of 3-5 buttress lobes (`trunk_rings`) and reach `BURIED` (1 m) underground, so they meet steep slopes. **Bark** is generated as a height field in one of eight styles. It becomes the albedo, with its furrows shaded, and a tangent-space normal map, `<species>_bark_normal.png`.

| File | Purpose |
|---|---|
| `species.json` | One entry per species: size, crown shape, branching, needle or leaf style, bark, and colours per season. `where` cites the BIGMAP share |
| `build_trees.py` | Builds 3 variants × 3 LODs per species and exports one FBX per variant, plus its textures |
| `textures.py` | Procedural textures (numpy): needle fronds and branch clusters, leaf clusters, twigs, bark and its normal map |
| `render_preview.py` | Review renders (Cycles): lineups, seasons, a close-up, groves, LODs and shader data |
| `audit_trees.py` | Adversarial audit: measures every model the way the game uses it (LOD and culling rule, snow pattern, wind, crown shading, impostor-style LOD fidelity) and renders comparison sheets. Its fidelity numbers match Unity's `fidelity.json` to within ±0.03 for conifers. See [the task 09 audit](../../../docs/plans/phase1-trees-audit.md) |
| `build-trees.bat` | Double-click: builds everything and opens the renders |

```sh
blender -b --factory-startup --python tools/assets/trees/build_trees.py -- --out tools/assets/trees/out --render tools/assets/trees/out/renders
# only some species: --species pacific_silver_fir,western_hemlock,noble_fir,krummholz
# only some shots: --shots conifers,deciduous,seasons,closeup,groves,lods,data   (a subset renders much faster)
# task 09 shots: compare,new,crystal,krummholz,textures (closeup and lods add the new trees' versions); they
#   render the species that are built, so build the look-alikes too (subalpine_fir, mountain_hemlock, and
#   douglas_fir and engelmann_spruce for the Crystal grove)
```

Use absolute paths for `--out` and `--render`: Blender resolves a relative render path somewhere else.

This needs **Blender 5.2 LTS** (free). The output in `out/` isn't committed; it is rebuilt from the script. The preview materials draw the game's snow: snow capacity × facing × `SnowPattern` on cards. So review renders show snow where the game will, not on whole cards. Building takes about 10 s; the full render set takes about 8 minutes on the reference PC.

## Species (14) and krummholz

| Conifers | Deciduous |
|---|---|
| Subalpine fir, Engelmann spruce, Douglas-fir, lodgepole pine, mountain hemlock, Pacific silver fir, western hemlock, noble fir | Quaking aspen, paper birch, yellow birch, sugar maple, red maple, American beech |

**Krummholz** (`"form": "krummholz"`) is a wind-shaped growth form, not a species, so it has no FIA code. Its three variants are three shapes (`forms` in `species.json`), with subalpine-fir needles, a darker palette and fir bark:

| Variant | Shape | Height |
|---|---|---|
| v0 | **Mat:** a low teardrop with a flat top, trailing downwind | about 0.8 m |
| v1 | **Flag tree:** branches only downwind, a dense skirt at the foot, a dead spike at the top | about 3.2 m |
| v2 | **Cushion:** a rounded dome, longer downwind than upwind | about 1.5 m |

**Orientation:** krummholz reaches toward Blender **-Y**. The FBX export (`axis_forward -Z`, `axis_up Y`, `bake_space_transform`) and Unity's import turn Blender (x, y, z) into Unity (-x, z, -y), as the lift pipeline measured (`tools/assets/lifts/liftkit/frame.py`). So Blender -Y is the prefab's local **+Z**, and the placement code turns +Z to face downwind. The mapping has determinant -1: it converts right-handed Blender to left-handed Unity without mirroring the model. `TreeImport` checks after import that each krummholz's foliage lies toward +Z.

Krummholz scales the trunk-sway and branch-flex wind weights (R and G) by `forms[].sway`, because it is stiff; a mat is stiffest. The shader moves branch tips by a fixed distance (12 cm at G = 1), which would make a 0.9 m mat flap.

Species options added for task 09 (all optional; the existing species don't use them, and their meshes and textures are bit-identical to before):
- `sheen`: paler colours for some side needles (Pacific silver fir's silvery undersides).
- `nodShape`: where the leader starts to bend, over what length and how far it reaches.
- `leaderCurve`: leader sprays that follow a drooping top.
- `roundTop`: the taller variants get a domed crown.
- `sprayLift`: sprays turned up at the tips (noble fir).
- `clusterSpan`: the size of the LOD1-2 branch-cluster cards.

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
| **UV2 x** | **Per-card random:** on leaf cards, when it comes out, turns colour and falls; on every card, where snow clumps |
| **UV2 y** | **Crown height** of the card (0 = crown base, 1 = top), so the season can move up or down the crown |
| **Submesh 0** | Bark |
| **Submesh 1** | Needles (conifers: fronds on LOD0, branch clusters on LOD1-2) or leaves (deciduous; swap summer and autumn textures, hide in winter) |
| **Submesh 2** | Bare twigs (deciduous): the fine winter crown |
| **Submesh 3** | Leaves kept through winter (beech) |

## Performance budgets (enforced)

A 5 km site holds about 650,000 trees, so every build checks each variant against a per-tree budget and **exits non-zero if any tree is over**, like a failing test. The budgets are `BUDGET` in `build_trees.py`; the audit plan is 0.3 §8.1.

| Level | Used for (planned) | Budget | Current range |
|---|---|---|---|
| LOD0 | 0–30 m | ≤ 10,000 triangles | 1.9k–9.9k |
| LOD1 | 30–80 m | ≤ 2,500 | 0.21k–2.3k |
| LOD2 | 80–150 m | ≤ 500 | 89–489 |
| Impostor | beyond 150 m | 2 | baked in Unity (64 views) |

`out/trees.json` records the triangles and the **alpha-card area** (m², a proxy for overdraw) of every LOD, plus any budget failures.

## Adding a species

Add an entry to `species.json`, using `"form": "conifer"` or `"deciduous"`, and rebuild. To find which species a real site has, run the data spike: `dotnet run --project tools/data-spike/src/DataSpike.Cli -- species --lat <lat> --lon <lon> --km 3`.
