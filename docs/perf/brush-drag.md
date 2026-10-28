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

## Round 2: let-go, Revert and Confirm (owner BR21, 2026-10-28)

While you paint, the ground, its cover and its snow are live: the cover and snow catch up five times a second, and
trees ride the ground. Trees come out and towers re-height only at Confirm. The bench adds a Confirm pass and a
Revert pass, and runs `TerrainEditPipeline.CheckShown`: what's on the tiles and in the forest must equal a fresh apply
of the committed ground.

| 400 ft, Jackson Hole (ms) | Round 1 | Round 2 |
| --- | --- | --- |
| Frames after let-go (p95 / max) | 24.3 / 30.8 | 7.8 / 17.6 |
| Frames after Confirm (p95 / max) | 4.2 / 8.3 (trees came out at let-go then) | 4.4 / 17.7 |
| Frames after Revert (p95 / max) | 19.5 / 19.7 | 4.9 / 7.3 |
| Drag frame p95 | 16.0 | 16.7 |
| Lift rebuild when towers re-height | 16–17 | 4.2 (in place) |
| Shown vs fresh after Confirm / Revert | not checked | 0 / 0 |
| Confirm to committed | 227 | 259 |

- **Second spot:** after let-go 18.1 max, after Confirm 16.7, after Revert 8.7, drag 18.3.
- **With `-force-gfx-mt`:** 13.7 / 13.8 / 7.9, drag 17.7.
- **Development game:** drag 18.4. Garbage is 546 KB a frame while dragging 400 ft (the catch-ups' planning), with 7
  collections and no hitch.

### What changed
- **Shown and committed are separate states in the pipeline.** Each change moves from what's shown, uploads heights
  only where the tiles don't hold them yet, and spreads its main-thread work over frames (6 ms a frame, and one tile's
  height sync per frame).
- **The crown map** is redrawn only when trees come out.
- **Contour labels** go out only when they change.
- **Towers re-height in place:** under fixed heads, the rope and chairs don't change, so only the towers rebuild.
- **Snow:** worked out at most twice a second, from reused buffers, and the changed rectangle is found on the worker.
- **Planning threads:** live catch-ups plan on one thread, and Ctrl+Z and Revert on two, so the frame being drawn keeps
  the CPU. That was the 23–29 ms Revert frame.

### Left as it is
- **Confirm to committed is 255–265 ms against 250.** The session's fold now shares the CPU with the last snow
  compute. Nothing on screen waits for it: the trees come out when it lands.
- **Garbage while dragging 400 ft** is up from 22 KB to about 0.5 MB a frame (the catch-ups' tiles and cover), with 7–9
  collections and no frame over budget from them.
