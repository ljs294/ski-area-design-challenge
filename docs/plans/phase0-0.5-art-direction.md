# Phase 0 · 0.5 Art direction and asset plan

**Audience:** the project owner and coding agents. **Status:** approved 2026-09-25 (decisions in §9). Builds on [0.2](phase0-0.2-game-design.md) (the look is the payoff) and [0.3](phase0-0.3-technical-architecture.md) (terrain, cover, forest, snow, lighting). Questions are in §9.

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

- **Snow** (iteration 1: a flat 12 in everywhere, T8; bare rock shows on faces over about 55°, A2):
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

**A species library, not generic types (TR1).** Trees are real species, placed where they actually grow (from BIGMAP, 0.3 §4.5), with recognizable silhouettes, bark and foliage colours. They are rendered in the stylized diorama look (A5): simplified materials, no photoreal detail.

**Phase 1 set** (Jackson Hole, Crystal Mountain and the style tile; D3, from the species BIGMAP reports at each site):

| Species | Form | Where |
|---|---|---|
| Engelmann spruce | Dense spire, drooping branch tips | Jackson Hole (47%) |
| Subalpine fir | Narrow spire; holds snow on its tiers | Jackson Hole (43%) |
| Whitebark pine | Broad, often multi-stemmed crown | Jackson Hole ridges (6%) |
| Limber pine | Open, irregular crown | Jackson Hole's exposed slopes (2–3%) |
| Mountain hemlock | Drooping-top conifer | Crystal Mountain's upper forest (56%) |
| Western hemlock | Tall, feathery, drooping leader | Crystal Mountain's lower slopes (32%) |
| Noble fir | Tall, stiff, blue-green | Crystal Mountain (stands in for BIGMAP's "Shasta red fir", 7%) |
| Alaska yellow-cedar | Weeping, drooping sprays | Crystal Mountain (4%) |
| Quaking aspen | Bare white-barked deciduous | Jackson Hole's lower slopes; tests the season path (TR4) |
| Krummholz | Stunted, wind-shaped forms | Just below the local treeline |

The library grows in later phases, prioritized by how often each species appears on the mountains players download.

**Mapping:** a `species-map` file links BIGMAP species codes to models. Unmapped species fall back to the nearest look-alike (same genus first, then the same form).

**Variation:**
- Each species has 3 mesh variants.
- Per instance: size from canopy height (±10%), rotation, a slight lean, and colour jitter from the palette (§5).
- **Snow load** comes from the tree shader (TR4): spires and firs hold snow on their tiers; bare deciduous trees get a dusting on their branches.

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
| Trees | 7 Phase 1 species × 3 variants, growing later | **Free tools (TR2):** Tree It, EZ-Tree (MIT) or Blender Sapling / Geometry Nodes, then the Blender finishing script | ≤1,500 / ≤300 triangles / octahedral impostor (roadmap §12) |
| Tree textures | One shared atlas | Hand-painted / generated, palette-quantized | 1k |
| Terrain layers | 5 + snow | Painted and procedural, each with albedo, normal and height | 1k–2k per layer |
| Strata wall material | 1 | Procedural | 1k |
| Sky, LUTs | 1 sky shader, 4 LUTs | Authored in Unity | — |
| UI icons | ~30 | SVG, outline style (0.4 §7) | — |
| Menu scene | Jackson Hole (G2, D1) | The acquisition tool | — |

**Free tree pipeline (TR2):**
1. **Shape** the species in Tree It (free; its exports are free for any engine), EZ-Tree (MIT) or Blender Sapling / Geometry Nodes.
2. **Finish** it with a Blender script in `tools/assets/`: build LODs, bake wind weights into vertex colours, split leaf / branch / bark materials, add a snow mask, and export glTF.
3. **Import** into Unity, where our own **impostor baker** makes the distant version.

**No subscriptions or paid tree tools.** Paid renderers or impostor tools need the owner's explicit OK; the free Nature Renderer 6 may be evaluated.

**Generative 3D** (roadmap §12) is not needed in iteration 1.

## 7. The style tile: the first art milestone in Phase 1

The style tile is one Unity scene that locks the look before scaling up:
- A 1 km patch of the 2 km test terrain with every ground layer, three species (subalpine fir, mountain hemlock, bare aspen) built with the free pipeline and the tree shader, a frozen lake and a strata-wall edge.
- Four lighting presets (dawn, noon, golden hour, night) with their LUTs.
- The UI's S6 HUD overlaid (0.4), in both themes.
- **Captured and reviewed with you before the forest and terrain are scaled to full sites.** A slice with blocky cover or noisy forest fails (T6).

## 8. Performance guardrails for art

- Everything fits T13 on the minimum spec at Medium: forest impostors beyond about 300 m, and shadow casters only in the near cascade.
- Terrain uses one pass with five layers plus snow. No more than 8 texture samples per layer per pixel at Medium.
- No per-frame allocation from art systems. Wind is animated in the vertex shader.

## 9. Decisions (owner approval, 2026-09-25)

| ID | Decision |
|---|---|
| A1 | Ring edge: a **diorama base** (clean cut, rock-strata walls, thin plinth) |
| A2 | Very steep faces (over about 55°) show **bare rock** as a visual touch; the snow depth texture stays a flat 12 in |
| A3 | Superseded by TR3: **BIGMAP species data** places deciduous trees along with every other species |
| A4 | Include a readable, **moonlit night** in the time scrubber |
| A5 | Target: a **model-railway / Parkitect-style diorama**, not photoreal |
