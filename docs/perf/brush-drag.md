# Terrain brush drag (perf/brush-drag, 2026-10-28)

Problem: P3-15's brush (PR #102) ran a 400 ft drag at a **34 ms** frame p95, but the per-frame budget is 20 ms. This
note records the timings before and after the fix, measured with `-brushbench` (`demo.bat` 75, which also covers
73b): a 4 s drag at 2 m a tick with a 60 ft brush, then a 400 ft one, on Jackson Hole 2 km at High with
`-benchres 1920x1080`. Every run went through the GPU measure lane on the reference PC (RTX 3060 Ti, Ryzen 5 5600X).

## Where the frame went (main 55f64a3f + timers, 400 ft)

| Part (p95, ms) | Before | After |
| --- | --- | --- |
| **Frame** | **34.4** | **16.0** |
| `BrushRun.Preview` every frame | 18.9 | 0 (gone) |
| Live upload: reading heights / SetHeights / SyncHeightmap | 4.7 / 0.9 / 7.4 | 4.7 / 0.9 / 7.6 |
| Dabs | 1.9 | 2.1 |
| Bill (5 Hz: a spike every 6th frame) | 4.9 (max 6.2) | 0.2 (max 1.6) |
| Garbage per frame (Development game) | 1.34 MB, 24 collections | 22 KB, 0 collections |
| Letting go: the stroke's tiles built | 20.8 | 0.5 |
| Frames just after letting go (p95 / max) | 24.6 / 79.6 | 24.7 / 26.3 |
| Confirm to committed | 221 (review + confirm) | 227 (Confirm, no review page) |

Second spot (`-brushat -500,700`): 400 ft frame p95 34.7 → 14.9 ms. With `-force-gfx-mt`: 17.1 ms. The 60 ft
drag stays at 7–8 ms.

## What changed

- **No tiles built while you paint.** The live upload reads the stroke point by point (`BrushRun.Live`). Each height
  is still the source plus the change, so the heights are unchanged byte for byte. The tiles are built once, when
  you let go.
- **The bill and the cleared cells are kept as the stroke goes** (`BrushRun.Set`), so the pane and letting go never
  rescan the stroke.
- Tests prove both match the old full count: Core `BR17_TheLiveGroundIsThePreviewPointByPoint` and
  `BR14_TheRunningBillIsTheFullCount`. `BrushGolden` and TB14 are unchanged.

## Left as it is

- **`SyncHeightmap` (about 7 ms at 400 ft)** still runs every frame. The tile code relies on it to update the GPU
  heightmap, and the drag is within budget without changing it.
- **The frames after letting go (about 25 ms p95):** most of that is the trees, cover and snow catching up through
  P3-05's apply. It's unchanged by this work.
- **The remaining 22 KB a frame** is new 64 m patches loading as the brush reaches new tiles. It causes no
  collections.
