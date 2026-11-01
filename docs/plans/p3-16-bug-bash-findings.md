# Phase 3 task 16: bug bash findings

Fixes for the owner's worst bugs after **v0.3.0** (tag `v0.3.0`, published to friends 2026-10-29). Blocking fixes ship
as **v0.3.1** ([phase3-tester-findings.md](phase3-tester-findings.md) TR7); the P3-12 thread cuts the tag and owns
`demo.bat` and `tools/release`. Branch: `feature/p3-16-bug-bash`.

Each finding is recorded first; the owner sets the severity and decides what gets fixed. Severity: **B** = blocking
(fixed here, ships in v0.3.1), **L** = later (backlog).

## Findings

| # | Source | What happened | Expected | Sev | Decision |
| --- | --- | --- | --- | --- | --- |
| BB1 | Owner screenshot, v0.3.0 | A new-area download hangs at 87%: "Resuming: Water and roads: downloading map features 1 of 3 · 0% · waiting for OpenStreetMap (Overpass) (66 s)". It never finishes | Water and roads are optional: if Overpass is down or slow, the area finishes without them | B | Plan below, waiting for the owner's go |
| BB2 | Owner request, 2026-10-31 | On lifts whose bullwheels are visible (today the Monta C4 and the SSL C4, in the owner's words), the bullwheels stand still | The visible bullwheels spin with the rope | | |

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

### Options (owner to choose)

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
