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
| TB5 | (2026-10-16) **The rest of the tester session is skipped:** no photo test and no brother's session. The owner ran the laptop test; task 14 records criterion 4's missing parts (no-help tester, photo) as an owner decision |
| TB4 | Task 11 stays skipped: the laptop's frame rate is informative; failing to start or open the demo is blocking |

## Exit criteria

| Criterion | Evidence | Result |
| --- | --- | --- |
| 3. A clean install opens the demo offline | Laptop and brother's PC: first launch with the network off reaches the title | **Met on the laptop** (2026-10-16): Jackson Hole opened offline "great" (owner) |
| 4. The tester downloads, explores and photographs a mountain without help | Owner on the laptop | **Partly met:** download and exploring worked (owner, not a no-help tester); photo not tried. Rest skipped by the owner (TB5) |
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
| Download (area, km, minutes) | | Worked well (owner, 2026-10-16) | |
| Photo saved? | | | |

## Findings

Severity: **B** = blocking (fixed in this task), **L** = later (Phase 3 backlog).

| # | Who / PC | What they tried | What happened | Expected | Sev | Log / photo | Decision |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | Claude / owner PC | Download Palisades Tahoe 5 km with `acquire` (E1) | USGS elevation service 502 three times; the CLI stops after 4 tries and must be re-run (it resumes from the cache) | Waits and retries like the game (P2-06) | L | p2-12-seam-check.md | Dev tool only; the game waits and retries. Backlog |
| 2 | Owner / laptop | Run the unzipped exe | Windows blocked it as an unknown app with no Run anyway button; ticking **Unblock** in the zip's Properties before extracting fixed it | README.txt's "More info, Run anyway" works | L | — | Every tester will hit it. Now: README.txt (next release) and the tester sheet say to Unblock the zip first. Lasting fix: code-sign the exe (costs money; owner decision, Phase 3) |
| 3 | Owner / laptop | Move the pointer off the ground (sky) in Imperial | The bar's elevation shows "– – – m" in Imperial and "– – – ft" in Metric | The no-reading text matches the unit setting | L | — | Likely cause: `MountainHud.Bar.cs` `_noElevation` is ordered { m, ft } but `UnitSystem` is Imperial = 0, Metric = 1. Documented only (owner: no changes in this task) |
| 4 | Owner / laptop | Hover the title's signs | Continue doesn't move on hover; the other signs slide 4 px | Every sign responds to hover | L | — | Likely cause: Continue has keyboard focus when the title opens, and `.sign:focus` uses the same 4 px slide as `:hover`, so it already sits slid and hovering adds nothing. Needs a distinct focus look (design call). Documented only |
| 5 | Owner / laptop | Watch the title over Jackson Hole | The view seems to bob up and down as it turns | A steady postcard drift | L | — | Not yet diagnosed. `TitleDrift.Evaluate` is a smooth Catmull-Rom path through four views (target height, distance and pitch all eased), so the bob is likely the orbit camera's ground clearance or bounds pushing the eye up over ridges, or the eased pitch and target height adding up. Needs a capture of the drift. Documented only |
| 6 | Owner / laptop | Watch the title and the mountain | Trees flicker | Steady trees | L | — | Not yet diagnosed: candidates are LOD or impostor switches, shadow cascade edges, or alpha-tested cards shimmering at distance. Seen on the laptop (Quadro), likely at a low preset; check whether it also happens on the main PC. Documented only |

## Backlog for Phase 3

No finding blocked the test, so this task fixed nothing (owner: document only). For the Phase 3 plan (task 14):
- **Install:** code-sign the exe so Windows stops blocking it (finding 2; costs money, owner decision).
- **HUD and title polish:** the no-reading elevation unit (3, likely a one-line swap); a focus look for the title's
  signs distinct from hover (4).
- **Look on older hardware:** the title drift's bob (5) and tree flicker (6), both seen on the 2016 Quadro laptop.
  Capture them there first, then check the main PC.
- **Dev tools:** `acquire` should wait and retry on USGS 5xx like the game (1).
- **Docs:** 0.3 §4.2 calls the fallback blend "slope-weighted"; the code is distance-weighted (seam check).
