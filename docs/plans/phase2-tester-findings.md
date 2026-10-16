# Phase 2 task 12: tester findings

Build: **v0.2.0** (`SkiAreaDesignChallenge-0.2.0-win64.zip`, 357 MB, tag `v0.2.0`). Tester sheet:
[p2-12-tester-sheet.md](p2-12-tester-sheet.md). Plan and owner decisions: [p2-12-seam-check.md](p2-12-seam-check.md)
(seam) and the decisions table below.

## Decisions (owner, 2026-10-14)

| # | Decision |
| --- | --- |
| TB1 | No spare PC. The clean installs are the owner's 10-year-old laptop (NVIDIA Quadro) and the owner's brother's PC; the owner also plays on the main PC from an empty data folder (demo.bat 59) |
| TB2 | The tester ships **v0.2.0 as is**: its README.txt already covers install, offline use and reporting. v0.2.1 only after a blocking fix |
| TB3 | The E1 seam site is **Palisades Tahoe 5 km** |
| TB4 | Task 11 stays skipped: the laptop's frame rate is informative; failing to start or open the demo is blocking |

## Exit criteria

| Criterion | Evidence | Result |
| --- | --- | --- |
| 3. A clean install opens the demo offline | Laptop and brother's PC: first launch with the network off reaches the title | **Met on the laptop** (2026-10-16): Jackson Hole opened offline "great" (owner) |
| 4. The tester downloads, explores and photographs a mountain without help | Brother's session: a download, notes, a saved photo | _pending_ |
| E1. The seam capture shows no visible step, or a fix is planned | [p2-12-seam-check.md](p2-12-seam-check.md) | **Met:** no visible step; datasets agree to 6 mm (mean) |

## Machines

| | Owner's PC | Laptop | Brother's PC |
| --- | --- | --- | --- |
| Model | Desktop | Lenovo ThinkPad P40 Yoga (2016), Core i7 | |
| Windows | 11 Home | | |
| GPU | RTX 3060 Ti | NVIDIA Quadro (P40 Yoga's) | |
| Screen | 2560×1080 | | |
| First launch offline: title shown? | | Yes, Wi-Fi off; Jackson Hole opened | |
| Seconds to title | | Not timed | |
| Quality preset Auto chose | | | |
| Download (area, km, minutes) | | | |
| Photo saved? | | | |

## Findings

Severity: **B** = blocking (fixed in this task), **L** = later (Phase 3 backlog).

| # | Who / PC | What they tried | What happened | Expected | Sev | Log / photo | Decision |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Claude / owner PC | Download Palisades Tahoe 5 km with `acquire` (E1) | USGS elevation service 502 three times; the CLI stops after 4 tries and must be re-run (it resumes from the cache) | Waits and retries like the game (P2-06) | L | p2-12-seam-check.md | Dev tool only; the game waits and retries. Backlog |
| 2 | Owner / laptop | Run the unzipped exe | Windows blocked it as an unknown app with no Run anyway button; ticking **Unblock** in the zip's Properties before extracting fixed it | README.txt's "More info, Run anyway" works | L | — | Every tester will hit it. Now: README.txt (next release) and the tester sheet say to Unblock the zip first. Lasting fix: code-sign the exe (costs money; owner decision, Phase 3) |

## Backlog for Phase 3
_(the L findings, grouped)_
