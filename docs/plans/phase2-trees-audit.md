# Phase 2 · Tree audit: realism, performance, and the new models against the game's

**Audience:** the project owner. **Status:** 🟨 for your review; the fixes wait for your approval. **Date:** 2026-10-01. **Part of:** task 09 phase 2. The evergreens' spiky tops have [their own page and plan](phase2-evergreen-tops.md).

You asked to *"audit the design of all trees for realism, performance, and against current models in game already"*. This covers all 18 models: the 15 in the game and the 3 New England models [in review](phase2-ne-trees-review.md).

## Verdict

- **Realism: two problems run through the whole library, and both show in the game.**
  - **Evergreens end in a dark, flat fin** (7 of 10). Planned separately: [the tops plan](phase2-evergreen-tops.md).
  - **Winter hardwoods are skeletons.** A bare broadleaf crown shows its main limbs and almost none of the fine twigs that make a real winter hardwood stand read as a soft grey-brown haze. From 300 m the hardwoods nearly vanish. This matters most in New England, where hardwoods are half the forest.
- **Performance: measured in the game for the first time per model.**
  - **The five heaviest models** (Douglas-fir, beech, western hemlock, Engelmann spruce, Pacific silver fir) would each take Sugarloaf's in-forest view to **28-30 ms** if a whole forest were made of them. The budget is 20 ms. The lightest models take it to 13-18 ms.
  - **What drives the cost:** triangles plus overdraw (a fit across the 14 models: R² 0.84-0.96).
  - **Draw calls** aren't a problem.
- **The three new models sit inside the existing ranges** on every measure. Predicted in-forest cost: white pine about 18 ms, red oak about 25, black cherry about 15. They share the library's two problems: the white pine has the fin, and the oak and cherry are winter skeletons. The fixes should cover them too.

## How it was measured

- **In the game.** A temporary build drew every one of Sugarloaf's 2.95 million trees as one model at a time: the same positions, heights and LOD choices, and krummholz left alone. It ran the benchmark's five views (RTX 3060 Ti, 1080p) once per model, 14 runs plus the real mix. The game's lineup photographed every model at every LOD. The hook was never committed.
- **In Blender:** the [audit tool](../../tools/assets/trees/audit_trees.py), whose game-shading numbers match Unity's within ±0.03, across all 18 models.
- **The three new models** can't go into the game before you approve them, so their GPU cost is **predicted** from the fit. Its worst miss on the 14 measured models is 4.5 ms in-forest; elsewhere it's about 1 ms.

## Findings

### High

**H1. Evergreens end in a dark, flat fin.**
- **Which:** 7 of 10 evergreens: subalpine fir, Engelmann spruce, Douglas-fir, lodgepole pine, Pacific silver fir, noble fir and the new white pine.
- **What it is:** upright leader cards stand 1-2 m above the crown and hold no snow.
- **Plan:** [its own page](phase2-evergreen-tops.md), with a measured prototype that fixes it at about 10 triangles a tree.

**H2. Winter hardwood crowns are skeletons.**
- **Measured:**
  - **Twig textures:** the winter twig cards cover 4.7-6.8% of their texture at full resolution, against 18-41% for conifer sprays, and 0-0.4% at mip 4.
  - **Crowns:** from the side, a bare broadleaf crown covers 5-10% of its frame; a conifer crown covers 12-35%.
- **In the game:**
  - **Up close:** each hardwood is a set of bare limbs (first image).
  - **Impostors:** the far LOD keeps only the main limbs (second image).
  - **From 300 m:** the hardwoods among the conifers are faint vertical lines (third image).
  - **Sugarloaf's in-forest view:** the birches and maples read as white poles, not a crown ([tops page](phase2-evergreen-tops.md), first image).
- **Real trees:** a leafless northern hardwood crown is a dense mesh of fine twigs. At any distance it reads as a soft grey-brown or purplish haze. A winter hardwood slope in New England looks grey-brown, not like white snow with sticks in it.
- **Which:** all eight broadleaves: aspen, paper and yellow birch, sugar and red maple and beech in the game, plus the new red oak and black cherry. Six more species share their models (NE5).

![The game's lineup: every model at LOD0. The broadleaves (middle) are bare limbs](images/phase2-trees-audit-game-lod0.jpg)

![The same row as impostors (the far LOD): the broadleaves keep only their main limbs](images/phase2-trees-audit-game-impostors.jpg)

![The game's 300 m stand: the hardwoods among the conifers are faint lines](images/phase2-trees-audit-game-stand300.jpg)

![Close up in the game: paper birch and aspen crowns have almost no fine twigs](images/phase2-trees-audit-game-close.jpg)

### Medium

**M1. The heaviest models cost 28-30 ms in a full in-forest view.**
- **Where the triangles go:** about 1,000 LOD0 trees, 0.4% of the 279,000 in view, carry 48-62% of the view's tree triangles.
- **The heaviest:** Douglas-fir (8,968-9,254 LOD0 triangles), Engelmann spruce (7,342-9,890), Pacific silver fir (7,244-9,816), western hemlock (6,864-8,946) and beech (7,911-9,509; it also has the most card area per height²).
- **Where it bites:** Sugarloaf's real mix is 23.4 ms. Engelmann spruce draws 17% of Sugarloaf's trees, for red spruce and the other spruces.
- **The model-side lever:** fewer, larger sprays at the same crown coverage, as the task 09 audit did for silver fir. Cutting LOD0 triangles about 30% on those models should save about 2.5 ms in-forest for a forest made of one of them, but only about 0.5 ms at Sugarloaf, where they are a smaller share. The forest thread owns the renderer-side fixes (LOD distances, culling); I'd agree this with them.

**M2. Wide broadleaves cost the most in the overview, where the trees are all impostors.**
- **Why:** an impostor's quad is as wide as the crown.
- **Measured:** beech, with a crown 0.90× its height, costs 11.1 ms there; the conifers cost 6.3-7.0 ms. The new red oak (0.89) will cost the same as beech.
- **Verdict:** inside the budget, so low priority. Trimming impostor quads to the visible crown would be a renderer change.

**M3. Bare spikes at the tops of broadleaves.** The leaders taper to bare points above the twigs (maples, oak, cherry), clear in [the New England stand](phase2-ne-trees-review.md#a-new-england-stand). It belongs with H2's fix: end leaders inside the twig crown.

**M4. Up close, conifer sprays are flat, straight-edged cards** that look paper-cut from low angles (Sugarloaf's in-forest view). Bending each near spray along its length would soften it, at more LOD0 triangles. Lower priority than H1 and H2; a later option.

### Low, and what passed

- **Colour, snow and LOD coverage:** every model sits within the library's ranges (table below).
- **Triangle budgets:** every model passes.
- **Draw calls:** 282-495 for a one-model forest and 932-1,111 for Sugarloaf's mix, with the GPU the limit.
- **Variety:** three variants per model, plus per-tree rotation, scale and tint, is enough at game distances.
- **Outside the models, for the forest thread:** Sugarloaf's in-forest view shows evenly spaced, similar-height conifers, which reads a little like a plantation. I'll pass it on.

## Every model

GPU p95 in ms, with **Sugarloaf drawn entirely as that model** (the real mix: 23.4 in-forest, 16.4 forest, 14.7 ringforest, 7.6 overview). "~" = predicted. Card area per height² is the overdraw measure; crown coverage is from the side.

| Model | LOD0 triangles | Card area / height² | Crown coverage | Winter L\* | LOD1 / LOD0 | In-forest | Forest | Ringforest | Overview | Issues |
|---|---|---|---|---|---|---|---|---|---|---|
| Subalpine fir | 4,398-5,550 | 1.68 | 0.29 | 61.8 | 0.92 | 20.4 | 15.0 | 13.5 | 6.7 | fin |
| Engelmann spruce | 7,342-9,890 | 1.73 | 0.35 | 56.9 | 0.89 | 28.6 | 19.1 | 16.5 | 6.5 | fin; heavy |
| Douglas-fir | 8,968-9,254 | 2.14 | 0.25 | 58.8 | 1.12 | 30.1 | 17.7 | 15.9 | 7.0 | fin; heaviest |
| Lodgepole pine | 2,448-2,832 | 1.24 | 0.19 | 52.1 | 0.86 | 12.8 | 8.3 | 8.7 | 6.4 | fin |
| Mountain hemlock | 4,836-5,644 | 1.98 | 0.31 | 61.7 | 0.96 | 22.0 | 15.5 | 13.7 | 6.9 | nodding hook |
| Quaking aspen | 4,810-6,358 | 0.41 | 0.06 | 55.6 | 0.95 | 16.8 | 12.5 | 11.8 | 7.0 | skeleton |
| Paper birch | 4,923-7,527 | 0.69 | 0.07 | 60.3 | 0.85 | 20.6 | 14.4 | 13.7 | 7.7 | skeleton |
| Yellow birch | 4,683-6,515 | 0.88 | 0.06 | 51.8 | 0.74 | 18.1 | 12.2 | 12.4 | 8.6 | skeleton |
| Sugar maple | 6,725-7,320 | 0.97 | 0.08 | 39.0 | 0.73 | 22.5 | 14.5 | 14.1 | 8.7 | skeleton; bare spikes |
| Red maple | 6,123-7,547 | 1.16 | 0.08 | 41.1 | 0.77 | 23.8 | 15.6 | 14.7 | 8.5 | skeleton; bare spikes |
| American beech | 7,911-9,509 | 3.14 | 0.09 | 50.6 | 0.73 | 29.2 | 17.7 | 17.1 | 11.1 | skeleton; heavy; wide |
| Pacific silver fir | 7,244-9,816 | 2.06 | 0.28 | 60.7 | 0.99 | 28.4 | 16.9 | 15.3 | 6.7 | fin; heavy |
| Western hemlock | 6,864-8,946 | 1.75 | 0.26 | 57.1 | 0.95 | 28.7 | 14.4 | 13.6 | 6.8 | heavy |
| Noble fir | 5,542-9,706 | 1.18 | 0.19 | 53.7 | 0.97 | 23.5 | 10.4 | 10.9 | 6.3 | fin |
| Krummholz | 1,668-2,788 | – | 0.29 | 61.7 | 0.90 | – | – | – | – | |
| **Eastern white pine** (new) | 3,110-5,764 | 2.16 | 0.12 | 50.0 | 1.08 | ~18 | ~9 | ~10 | ~7 | fin |
| **Northern red oak** (new) | 6,229-8,710 | 1.30 | 0.07 | 48.5 | 0.71 | ~25 | ~15 | ~14 | ~11 | skeleton; bare spikes; wide |
| **Black cherry** (new) | 3,382-4,054 | 0.53 | 0.05 | 33.1 | 0.78 | ~15 | ~10 | ~10 | ~9 | skeleton; bare spikes |

**What the fit says** (in-forest): GPU ms ≈ 3.8 + 1.04 × the view's tree triangles (millions) + 2.3 × overdraw. A million triangles in that view costs about 1 ms. The forest and ringforest views fit closely (R² 0.96 and 0.94).

## The new models against the game's

- **Look:** each new model's colour, snow and LOD coverage falls inside the range of the trees already in the game. White pine's winter L\* is 50.0 (lodgepole 52.1); oak's is 48.5 (beech 50.6); cherry's is 33.1, darker than the maples (39-41) as the real tree is.
- **Cost:** white pine and black cherry are among the cheapest models. Red oak sits with the maples: cheaper than beech in-forest, as costly as beech in the overview.
- **Shared problems:** H1 (the white pine's fin) and H2 and M3 (oak's and cherry's winter skeletons and bare spikes). Fixing them in the script fixes the new and the old trees together.

## Plan (for your approval)

1. **Evergreen tops (H1):** [the tops plan](phase2-evergreen-tops.md), awaiting your decision.
2. **Winter hardwood crowns (H2, M3):** one change to the script for all eight broadleaves:
   - denser, finer twig cards in each species' twig colour, aiming for about 15-25% twig cover per card instead of 5-7%;
   - a "twig haze" card on LOD1-2 so mid-distance crowns and impostors keep the mass;
   - leaders that end inside the twig crown.

   **Measured before you see it:** crown coverage from the side, a 300 m stand in the game's shading, and the cost predicted from the fit. Hardwoods are among the cheaper models today, so there's room. My target is at most +2 ms for an all-hardwood in-forest view. Blender prototype first, then your photo review, as always.
3. **Heavy models (M1):** with the forest thread, try fewer, larger sprays on Engelmann spruce first: of the heavy models, it has the biggest share at Sugarloaf (17%). Keep the change only if the look holds and the game measures the saving.
4. **One import for everything approved:**

   | Batch | Git LFS |
   |---|---|
   | Tops (six or seven conifers) | 50-59 MB |
   | Broadleaves (six) | 32 MB |
   | The three New England models | about 25 MB |
   | **Total** | **about 110-120 MB** |

   That's roughly 360-370 MB of the free 1 GB. Every untouched model keeps its files byte for byte.
5. **Later options:** bent near sprays (M4), impostor quads trimmed to the crown (M2, renderer), and a wind-swept old white pine.

## Decisions for you

1. Approve the hardwood-crown fix (2) for a Blender prototype and photo review?
2. Try the heavy-model change (3) with the forest thread, or leave performance to the renderer work?
3. Combine everything you approve into one import (4)?
