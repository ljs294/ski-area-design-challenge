# Phase 0 · 0.5 Art direction and asset plan

**Audience:** the project owner and coding agents. **Status:** draft for review (2026-09-25). Builds on [0.2](phase0-0.2-game-design.md) (the look is the payoff) and [0.3](phase0-0.3-technical-architecture.md) (terrain, cover, forest, snow, lighting). Questions are in §9.

## 1. The look in one sentence

**A crisp, stylized winter diorama of a real mountain:**
- every ridge true to the lidar
- forests that read as individual trees up close and as soft textured masses from afar
- clean snow under clear light
- the calm, readable charm of a model railway or a Parkitect scene, not photorealism

**Pillars:**
1. **Truthful shape, stylized surface.** Geometry is never simplified beyond the data. Materials are simplified, with fewer, cleaner values.
2. **Readable at every distance.** Silhouettes and value contrast carry the scene: ridges against the sky, forest against snow.
3. **Light is the drama.** Time of day is the main mood control: blue dawn, bright noon, warm low sun.
4. **Calm.** No noise, no film grain, no heavy bloom; restrained effects.

## 2. Terrain and snow

- **Snow** (iteration 1: a flat 12 in everywhere, T8):
  - A bright, slightly cool white; shadows pushed to a soft blue; fine wind-texture detail in the normal map.
  - A subtle view-dependent sparkle in low sun. The sparkle can be turned off (Ultra only).
- **Terrain relief** comes from the lidar alone. There is no extra noise displacement, because the 1 m data *is* the detail.
- **Under the snow** (visible when the Snow layer is off, T17), five ground layers with height maps for natural blending (T6):

| Layer | Look |
|---|---|
| Forest floor | Dark umber needles and soil |
| Grass / meadow | Muted straw green (winter-dormant) |
| Rock / alpine | Cool grey granite with a strata hint |
| Developed | Neutral grey-brown, a paved/built texture |
| Water | Dark blue-green; frozen in iteration 1 (below) |

- **Frozen lakes (T8):** flat, snow-covered ice. The surface is slightly smoother and brighter than land snow, with a faint blue-grey rim of exposed ice and pressure cracks at the shoreline. Streams read as shallow snow-filled channels following OpenStreetMap lines.
- **The ring edge** (the outer limit of the downloaded surround): see A1 in §9. **Recommendation:** a **diorama base**. The terrain is cut cleanly at the ring edge, with side walls showing stylized rock strata down to a thin plinth. This is the "section-cut diorama" idea the owner wanted for the old game, which Unity makes easy.

## 3. Forest

**Five tree archetypes** cover the US mountain West and East in winter:

| Archetype | Stands in for | Where |
|---|---|---|
| Spire conifer | Subalpine fir, Engelmann spruce, Pacific silver fir | High montane, north-facing slopes |
| Broad conifer | Douglas-fir, western and eastern hemlock, white pine | Mid and low montane |
| Open pine | Ponderosa, lodgepole, whitebark pine | Dry and south-facing slopes, treeline |
| Bare deciduous | Aspen, birch, maple, oak (winter, leafless) | Low elevations, eastern forests |
| Krummholz | Stunted treeline shrubs and trees | The band just below local treeline |

**Choosing an archetype per tree** (deterministic, T12):
- Height above local treeline, aspect and canopy height (T7).
- A regional mix (West / East) from longitude and latitude.
- **Optionally** USGS NLCD's evergreen/deciduous/mixed forest classes (30 m, public domain), to place deciduous trees correctly. **Recommendation:** use it. It is a small download and fixes "all-conifer" eastern hills.

**Variation:**
- Each archetype has 3 mesh variants.
- Per instance: size from canopy height (±10%), rotation, a slight lean, and colour jitter from the palette (§5).
- Snow load on branches, scaled by archetype: spires hold snow on their tiers; deciduous trees get only a dusting on the branches.

**Style:**
- Faceted, low-poly-ish forms with smooth shading. No individual needles; clumped foliage masses.
- Canopy colour sits in a deliberately narrow value range, so distant forest reads as texture, not noise.

## 4. Lighting, sky and atmosphere

- **Sun** from the real solar position (T9).
- **Sky:** a gradient sky tied to sun elevation, with no clouds in iteration 1. Night shows a deep blue sky with a few stars and the snow in moonlight-blue, which stays readable (not black).
- **Colour grading:** one LUT per time-of-day band, blended: dawn cool pink-blue, day neutral-cool, golden hour warm, dusk violet.
- **Shadows:** soft, blue-tinted, with ambient occlusion under the forest.
- **Atmosphere:** **very light aerial perspective** (distance haze) beyond about 3 km, so ridges layer into depth. Nothing inside the site is hazed. The old game removed fog for crispness, so this stays subtle and has a toggle.
- **Effects:** mild bloom on sunlit snow at golden hour only; no lens flare, no chromatic aberration, no grain.

## 5. Palette

| Role | Light value | Notes |
|---|---|---|
| Snow lit | `#F4F7FA` | Never pure white, to keep headroom for sparkle |
| Snow shadow | `#9DB4CC` | Cool blue |
| Ice rim | `#BFD9E6` | Frozen lakes |
| Conifer dark / mid / light | `#233B2E` / `#2F4A3A` / `#3E5C45` | Jittered per instance |
| Deciduous bark | `#5A4A3F` | Bare trees |
| Rock | `#6E6A66` | Cliffs, strata walls |
| Forest floor | `#4A3B30` | Snow-off view |
| Meadow | `#8C8A5C` | Snow-off view, dormant |
| Sky zenith / horizon (day) | `#5E9BD6` / `#DCEBF7` | |
| Golden hour key light | `#F2B880` | |

The UI palette (0.4 §7) is separate; its accent blue `#155ab6` is chosen so that it doesn't clash with the snow-shadow blue.

## 6. Asset plan

| Asset | Count | Source | Budget (LOD0 / LOD1 / impostor) |
|---|---|---|---|
| Trees | 5 archetypes × 3 variants | **Procedural in Blender** (Geometry Nodes, scripted from `tools/assets/`), exported as glTF | ≤1,500 / ≤300 triangles / octahedral impostor (roadmap §12) |
| Tree textures | One shared atlas | Hand-painted / generated, palette-quantized | 1k |
| Terrain layers | 5 + snow | Painted and procedural, each with albedo, normal and height | 1k–2k per layer |
| Strata wall material | 1 | Procedural | 1k |
| Sky, LUTs | 1 sky shader, 4 LUTs | Authored in Unity | — |
| UI icons | ~30 | SVG, outline style (0.4 §7) | — |
| Menu scene | Crystal Mountain (G2) | The acquisition tool | — |

**Buy versus build (roadmap §9):**
- Trees are built, because the style must match and the archetypes are few.
- **Impostor baking and GPU-instanced rendering** are evaluated in Phase 1: an Asset Store package (for example Amplify Impostors, GPU Instancer or Nature Renderer) versus our own. The licence must allow redistribution in a commercial game.

**Generative 3D** (roadmap §12) is not needed in iteration 1.

## 7. The style tile: the first art milestone in Phase 1

The style tile is one Unity scene that locks the look before scaling up:
- A 1 km patch of the 2 km test terrain with every ground layer, all five tree archetypes, a frozen lake and a strata-wall edge.
- Four lighting presets (dawn, noon, golden hour, night) with their LUTs.
- The UI's S6 HUD overlaid (0.4), in both themes.
- **Captured and reviewed with you before the forest and terrain are scaled to full sites.** A slice with blocky cover or noisy forest fails (T6).

## 8. Performance guardrails for art

- Everything fits T13 on the minimum spec at Medium: forest impostors beyond about 300 m, and shadow casters only in the near cascade.
- Terrain uses one pass with five layers plus snow. No more than 8 texture samples per layer per pixel at Medium.
- No per-frame allocation from art systems. Wind is animated in the vertex shader.

## 9. Questions for you

Write your answer after each **Comment:**; "OK" accepts the recommendation.

**A1 · Ring edge.** **Recommendation:** a diorama base (clean cut, rock-strata walls, thin plinth). The alternative is the old game's "floating edge" with no walls.
**Comment:**

**A2 · Flat snow on cliffs.** With a flat 12 in everywhere, cliffs turn white too. Show bare rock on very steep faces (over about 55°) as a purely visual touch, or keep them literally all white?
**Comment:**

**A3 · Deciduous trees from NLCD.** Use the extra small NLCD download to place leafless deciduous trees correctly?
**Comment:**

**A4 · Night.** Include a readable, moonlit night in the time scrubber, or keep daylight only?
**Comment:**

**A5 · Reference feel.** Is "model railway / Parkitect diorama, not photoreal" the right target? Name any games or images you'd like it to feel like.
**Comment:**
