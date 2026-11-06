# Phase 3 task 12: tester findings

Build: **v0.3.0** (`SkiAreaDesignChallenge-0.3.0-win64.zip`, tag `v0.3.0`), published as a GitHub Release. Tester
sheet: [p3-12-tester-sheet.md](p3-12-tester-sheet.md). Release notes: [v0.3.0.md](../releases/v0.3.0.md). The
Phase 2 precedent is [phase2-tester-findings.md](phase2-tester-findings.md).

## Decisions (owner, 2026-10-29)

| # | Decision |
| --- | --- |
| TR1 | **Release tonight:** v0.3.0 is cut from a clean tagged commit on main with `release.bat`, after this docs PR |
| TR2 | **A full GitHub Release marked Latest**, titled as an early, unsigned test build, so the README's `/releases/latest` link works (a pre-release would leave it at 404). Later versions replace it as Latest |
| TR3 | **The testers are friends**, sent the Release link and this tester sheet; no help during the session. Reports come in through Send feedback (GitHub issues labelled `new`) or zips sent to the owner |
| TR4 | **The owner tries the zip first** (demo.bat 59: offline title, draw, save, quit, reopen) before the tag is pushed and the Release published |
| TR5 | **Not blocking** (P3-11 follow-ups): the `-brushbench` quit crash (a dev flag testers never pass), the ~1 s preview when grading a long run (slow but works), and the trail look's 2048 m window (only big resorts). A tester who hits one gets a finding row like any other |
| TR6 | **Out of this task:** the RTX 2060-class check and code signing (owner chores before a public release); the README and tester sheet cover Unblock and Run anyway |
| TR7 | **A blocking fix ships as v0.3.1** (tag asked first), through the same build, hands-on and publish steps |

## Close-out (owner, 2026-11-05, E19)

The friends played v0.3.0, liked it and sent no reports: no Send feedback zips and no GitHub issues. Which steps
each one finished wasn't recorded, and the owner's hands-on (TR4) was skipped. The round closes with **no tester
findings**. The one real-world finding came from the owner's screenshot: the Overpass hang (BB1), fixed in v0.3.1
([p3-16-bug-bash-findings.md](p3-16-bug-bash-findings.md)). The exit decision is
[phase3-13-exit-review.md](phase3-13-exit-review.md) E19.

## Exit criteria

| Criterion | Evidence | Result |
| --- | --- | --- |
| The tester finishes without help (download, explore, photograph, draw, save and reload), or each block is fixed or decided | Friends' reports below | |
| The clean install still opens the demo offline | The 2016 laptop, Wi-Fi off, reaches the title and opens Jackson Hole | |
| The release builds from a tag | `release.bat` on `v0.3.0`: EditMode tests, smoke test (fresh install, offline title), zip | |

## Machines

| | Owner's PC | Laptop | Tester 1 | Tester 2 |
| --- | --- | --- | --- | --- |
| Model | Desktop | Lenovo ThinkPad P40 Yoga (2016) | | |
| Windows | 11 Home | | | |
| GPU | RTX 3060 Ti | NVIDIA Quadro | | |
| Screen | 2560×1080 | | | |
| First launch offline: title shown? | | | | |
| Download (area, km, minutes) | | | | |
| Photo saved? | | | | |
| Resort drawn, saved and reopened? | | | | |

## Findings

Severity: **B** = blocking (fixed in this task, then v0.3.1), **L** = later (backlog). Each finding is recorded first;
the owner sets the severity and decides what gets fixed.

| # | Who / PC | What they tried | What happened | Expected | Sev | Report / issue | Decision |
| --- | --- | --- | --- | --- | --- | --- | --- |
