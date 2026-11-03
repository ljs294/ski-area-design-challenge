# Phase 3 task 16: bug bash findings

Fixes for the owner's worst bugs after **v0.3.0** (tag `v0.3.0`, published to friends 2026-10-29). Blocking fixes ship
as **v0.3.1** ([phase3-tester-findings.md](phase3-tester-findings.md) TR7); the P3-12 thread cuts the tag and owns
`demo.bat` and `tools/release`. Branch: `feature/p3-16-bug-bash`.

Each finding is recorded first; the owner sets the severity and decides what gets fixed. Severity: **B** = blocking
(fixed here, ships in v0.3.1), **N** = next (ships in v0.3.2), **L** = later (backlog).

The follow-up plan (owner, 2026-11-02): PR C fixes the BB1 loop and BB4 for v0.3.1; BB5 and BB6 (PRs D0, D, E) and
BB7 and BB8 (PR F, its own session) ship in v0.3.2.

## Findings

| # | Source | What happened | Expected | Sev | Decision |
| --- | --- | --- | --- | --- | --- |
| BB1 | Owner screenshot, v0.3.0 | A new-area download hangs at 87%: "Resuming: Water and roads: downloading map features 1 of 3 · 0% · waiting for OpenStreetMap (Overpass) (66 s)". It never finishes | Water and roads are optional: if Overpass is down or slow, the area finishes without them | B | **Fix (owner, 2026-11-01): A1, B1, C1, D2.** PR A. Skip the layer unless every mirror failed to connect; one 75 s try per mirror; a line on the quality card; backfill later. **Merged #117 (82db0d56).** **Sent back by the v0.3.1 thread (2026-11-02, owner: "Hold, send it back"):** with Overpass blocked or unroutable no mirror ever accepts the connection; Windows says so with a `SocketException` after about 21 s, which counted as "every mirror failed to connect", so the download still waited and resumed at 87% for ever (20 loops in 30 minutes). The second half of A1, "and the other map services can't be reached either", was never built. **PR C:** when no mirror could be reached, ask the other map services (`Acquisition/IO/Reachability`: WorldCover, 3DEP, the S1M bucket; any HTTP answer counts); if any answers, Overpass is the problem and the layer is skipped; only if none does is it a lost connection that waits |
| BB4 | v0.3.1 thread, 2026-11-02 | `demo.bat`'s player build, Development build and Unity tests refuse to run while Unity Hub is open: the check matches any `Unity.exe`, and the Hub runs `unity.exe serve` | Only an editor with this project open blocks them | B | **Fix in PR C (owner, 2026-11-02):** use the existing `:projectfree` check (`Temp\UnityLockfile` held open) in `:buildplayer`, `:builddev` and `:unitytests` |
| BB5 | Owner request, 2026-11-02 | When a server holds a download up, the player isn't told which one; a stalled USGS, AWS or USDA server shows as "No connection"; it waits for ever, and the only choices are Try now and Cancel | Players are alerted when a server holds the download up and get choices | N | **Fix in v0.3.2 (owner, 2026-11-02):** PRs D0 and D. Alert after **2 minutes** held up (a toast naming the server, an amber "Held up" pill, the card says how long and how many tries); a server that stalls or is down is told apart from a lost connection; choices Try now, Pause for later, Cancel, and Skip water and roads when that is the step held up (the only layer with a stand-in) |
| BB6 | Owner request, 2026-11-02 | An area downloaded while OpenStreetMap was unavailable (or skipped) has no water and roads, and there's no way to get them later | A way to get water and roads later | N | **Build now, v0.3.2 (owner, 2026-11-02):** PR E. "Get water and roads" in Manage Areas; the area's id changes, so its saves and recent entry move to the new id with it, crash-safely; `tools/acquire refresh-osm` uses the same code (it adds only roads today) |
| BB7 | P3-16 benchmarks, 2026-11-01 | The player crashes as it quits: an access violation in `D3D12Core.dll`, 213 times since about Oct 23 in every build (the shipped v0.3.0 too), after the log and the crash marker have ended, so nobody is told; Windows keeps a dump of about 43 MB each time | Quitting exits cleanly | N | **Fix in v0.3.2 (owner, 2026-11-02):** PR F, its own session: a stack from a dump, a repro matrix, a bisect of the P3-09 lift commits, then a general fix; a project-setting fix goes back to the owner with numbers. Covers the `-brushbench` quit crash below |
| BB8 | P3-16 benchmarks, 2026-11-01 | `docs/perf/jackson-hole-5km-high-lifts40.json` is stale: `-liftbench 40` now mixes FGQ-4, D6 and Monta lifts (main p95 8.74 ms against the pinned 7.80 ms) | A baseline that matches the benchmark | N | **Fix in v0.3.2 (owner, 2026-11-02):** PR F re-pins it after the crash fix, by the `docs/perf/README.md` rules (the P3-09 plan asked for this) |
| BB2 | Owner request, 2026-10-31 | On lifts whose bullwheels are visible (today the Monta C4 and the SSL C4, in the owner's words), the bullwheels stand still | The visible bullwheels spin with the rope | B | **Fix in v0.3.1 (owner, 2026-11-01):** PR B. Only lifts whose wheel shows (Sessellift FGQ-4, Monta FG4; not the D6's tub), by a catalog flag. Bullwheels only, sheaves stay still |
| BB3 | Owner request, 2026-10-31 | The chairs look great but move stiffly along the line | More swing along the line, and a slight side-to-side sway every so often. Only at minimal cost (frame time, garbage) | B | **Fix in v0.3.1 (owner, 2026-11-01):** PR B, very lightweight. Swing along the direction of travel when the rope changes direction over a tower's sheaves; slight side sway. The owner sees a demo build (presets) before it goes into the game. **Held (owner, 2026-11-01, after the demo):** it doesn't add much, so it stays out of the game; BB2 ships alone in PR B. The demo's code (kicks at towers and ramps, gust sway, Off/Subtle/Medium presets) is kept on branch `held/p3-16-chair-swing` (c707c912) for a later look |

## BB1: the download hangs on OpenStreetMap (Overpass)

### Cause

Two faults in Acquisition combine:

1. **A dead mirror is mistaken for the player being offline.** `OsmFeatures.DownloadAsync` tries three Overpass mirrors
   (overpass-api.de, overpass.private.coffee, maps.mail.ru) in turn and wraps the last failure. When that failure is a
   timeout or a refused connection, `NetworkFailure.IsNoConnection` says yes (`TaskCanceledException`, the HttpClient
   timeout, counts as no connection; `IO/ByteSources.cs`). `AcquisitionPipeline.OsmAsync` deliberately lets
   no-connection failures through, so the optional step that should have been skipped stops the whole download.
   `DownloadService` then waits 15 s and resumes, the resume lands on the same step and the same dead mirror, and it
   loops for ever. The screenshot's "Resuming:" fits this.
2. **Each pass can take about an hour.** `Http.GetBytesAsync` makes 4 attempts per mirror with a 5-minute client
   timeout each (and the query asks the server for up to 180 s), so 3 mirrors can spend about 60 minutes on
   "waiting for OpenStreetMap" before the step even fails.

The rest of the design is already in place: the manifest records a missing OSM layer in provenance ("Unavailable during
this download; WorldCover water and built-up used instead"), so a package without the layer opens normally. No
`download.json` or save format change is needed.

### Options (owner chose A1, B1, C1 and D2 on 2026-11-01)

| Area | Option | What it does |
| --- | --- | --- |
| A. Skip or wait | **A1 (recommended)** | Wait only if every mirror failed to connect *and* the other map services can't be reached either (a real lost connection). A timeout, a busy status (429/504), an HTML error page or an error "remark" means Overpass is unavailable: skip the layer |
| | A2 | Always skip on any Overpass failure. A dropped connection would then quietly build the area without water and roads |
| B. Bound the time | **B1 (recommended)** | One attempt per mirror with a 60–90 s timeout (the query's server timeout to match) and a total limit of about 4 minutes for the step |
| | B2 | Staggered race: start the next mirror after 20 s of silence and take the first good answer. Faster, but more load on free public servers |
| C. What the player sees | **C1 (recommended)** | The step reads "Map features unavailable · skipped (using satellite land cover)" and the download finishes |
| | C2 | C1, plus a line on the area in Manage Areas |
| D. Backfill | D1 | A "Retry map features" action that adds water and roads to an existing area later. Bigger: adds manifest layers and rebuilds the terrain cache; may need the Master Planner's format OK |
| | **D2 (recommended)** | Later: log it in the backlog |

Every option is general: it holds for any area, mirror or network, with no site values or per-run flags.

### Tests and demo (planned)

- A replaceable fetch on `OsmFeatures`, so engine-free tests in `Tests/Core` can cover: every mirror times out →
  skipped, no wait; every mirror refuses the connection → waits; an error remark then a good answer → the good answer
  is used; the step stays within its time limit.
- Demo: a test-only `-overpass <url>` override pointing at an address that never answers, so the download visibly
  finishes without the layer. The P3-12 thread adds the `demo.bat` entry (or this thread, if the owner prefers).

### Files

`Acquisition/Providers/OsmFeatures.cs`, `Acquisition/AcquisitionPipeline.cs` (`OsmAsync`), `Acquisition/IO/ByteSources.cs`
(a small seam only), tests in `Tests/Core`.

## Candidates from the backlog (not yet in scope)

Offered to the owner for this bug bash, in no particular order; each becomes a finding row if the owner picks it.

- The `-brushbench` access violation on quit (`PendingContourView.Clear` from `MountainViewer.OnDestroy`, after the
  report is written).
- Remove trail removes only the clicked run (P3-10 #106 follow-up).
- Brush drag garbage, about 170 KB per frame against a 64 KB target (#111).
- Brush Confirm takes 255–265 ms against a 250 ms target; the close ask shows Confirm/Revert twice (#110).
- The trail look's 2048 m window covers only part of a big resort (P3-11).
- The picker header says "New Mountain"; the owner's naming is "New Area" (task 14).
- Low-preset tree flicker (no MSAA, so no alpha-to-coverage; P3-01).
