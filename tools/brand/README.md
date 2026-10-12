# Brand tools

Tools for designing, checking and exporting the Alpine Labs and Ski Area Design Challenge marks (task P2-03.1). The
chosen brand is Twin Peaks: see `docs/brand/README.md`.

Everything here runs with Node 22 and Microsoft Edge, and needs no packages. The concept generators also need Python
with shapely.

| Tool | What it does |
| --- | --- |
| `edge.mjs` | A minimal headless-Edge client over the DevTools protocol, after `tools/ui-parity/parity.mjs`. It waits for a page's web fonts and reports any that failed to load. |
| `render.mjs <page> <prefix>` | Renders a board to `<prefix>-full.jpg` plus one 1080-wide still per `<section>`, for review on a phone. |
| `checks.mjs <svg...> [--brand studio\|game] [--icon]` | The brief's mechanical tests: T2 squint (IoU of a 4% blur ≥ 0.80, same parts and holes), T3 one colour (no strokes, opacity, gradients, masks or text), T4 small sizes (no ink or gap under 1 px at 32, or at an icon's own 16/24), and the colour rules (no red, orange or amber; game colours only; the studio accent at least 15 ΔE2000 from every Trailhead token). Writes `<name>.checks.json` and a `<name>-proof.jpg` strip. |
| `skyline.py <package> <out.json>` | The demo mountain's skyline from the title camera views, read from the bundled package's height grids. |
| `twinpeaks.py` | The chosen brand's geometry (Twin Peaks): the marks, the trail and lift, the peak As. Needs shapely. |
| `export.py` | Writes the masters (`docs/brand/masters/`, ink) and the game's white layers (`Art/UI/Brand/Resources/Brand/`), with the wordmarks outlined from `fonts/Overpass[wght].ttf` (OFL; `fonts/OFL.txt`). Needs fontTools and shapely. |
| `icon.mjs` | The app icon (task P2-10): the ink game mark on a sign-white tile at 16–1024 px, each size with its cut (plain pair at 16 and 24, small cut to 64), into `Art/UI/Brand/Icon/icon-<size>.png` (Git LFS), plus a proof on dark and light taskbars in `test-results/brand/icon-proof.png`. The build sets them (`Editor/AppIcon.cs`). |

Scratch output goes in `test-results/brand/`, which git ignores. Don't commit PNGs; they go to Git LFS.

The judgement tests (T1 memory, T5 literal reads, T6–T9) are done by reviewers, not by these tools.
