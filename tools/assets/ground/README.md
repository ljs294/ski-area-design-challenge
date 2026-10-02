# Ground textures (task 12c)

The terrain's bare-ground look comes from CC0 photo textures from [Poly Haven](https://polyhaven.com), graded toward the art direction's palette ([0.5 §2](../../../docs/plans/phase0-0.5-art-direction.md)).

1. `node tools/assets/ground/fetch.mjs` downloads the 2k colour, normal and height maps listed in `sources.json` into `cache/`, which is git-ignored (about 127 MB).
2. `python tools/assets/ground/prepare.py` does three things to each texture:
   - halves it to 1024², keeping the tile seamless;
   - grades it (the targets are in `GRADES`);
   - writes colour + height and normal PNGs to `cache/prepared/`.
3. In Unity, choose **Mountain Planner > Generate Ground Textures**. It compresses the textures (BC7) into `Art/Terrain/GroundAlbedo.asset` and `GroundNormals.asset`, about 28 MB each in Git LFS.

Commit the arrays only once a look is approved: every committed version stays in LFS storage.

## Credits

All textures are CC0 (public domain, [licence](https://polyhaven.com/license)). Credit isn't required, but we give it:

| Texture | Author | Used as |
|---|---|---|
| `withered_grass` | Charlotte Baglioni | Valley grass |
| `sparse_grass` | Amal Kumar | Alpine meadow |
| `forest_leaves_04` | Rob Tuytel | Forest floor |
| `rock_face_03` | Dario Barresi, Rico Cilliers | Rock faces |
| `rocky_terrain_02` | Amal Kumar | Meadow from a distance |
| `dry_ground_rocks` | Rob Tuytel | Scree |
| `brown_mud_dry` | Rob Tuytel | Bare dirt |
| `aerial_asphalt_01` | Rob Tuytel | Developed land |
| `gravel_road` | Amal Kumar | Tracks (12d) |
