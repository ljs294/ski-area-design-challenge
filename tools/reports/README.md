# Problem reports (task P3-14)

Players send problems and suggestions from the game's **Send feedback** window. The game saves one zip and opens a
pre-filled GitHub issue; the player drags the zip into it. Nothing is uploaded by the game, and no key or token is in
the build (decisions PR1–PR10 in [phase0-decisions.md](../../docs/plans/phase0-decisions.md)).

## Where it opens

| From | Opens on |
| --- | --- |
| The menu (top right), **Send feedback** | Suggestion |
| The "A log was saved" notice, **Send logs** | Problem |
| Settings › Data › Logs, **Send logs…** | Problem |
| The title, after the game closed unexpectedly, **Send report** (once per crash; Settings › Data turns it off) | Problem, with the crash dump |

A suggestion starts with only the screenshot and the system ticked; a problem starts with everything.

## What a report holds

`<data>/Reports/report-<UTC stamp>.zip`; the newest 10 are kept.

| Path | What | Part |
| --- | --- | --- |
| `report.json` | "problem report" v1 (not frozen, versioned): kind, title, note, version, commit, the area's package id, name, centre, size and attribution, system, every setting (typed `i:`/`f:`/`s:`), the file list | always (system and settings only when ticked) |
| `screenshot.jpg` | The view just before the window opened | Screenshot |
| `logs/errors-*.log` | The game's error logs (task P2-10), newest 10 | Error logs |
| `logs/Player.log`, `logs/Player-prev.log` | Unity's logs, the last 4 MB of each | Game log |
| `crash/*` | Unity's newest crash folder (crash.dmp, error.log) when it is under 14 days old | Crash dump |
| `save/` | The open save (save.json and tiles/), byte for byte, read only | Your save |

The package itself never goes in. The player's Windows user and computer names are replaced by `%USERNAME%` and
`%COMPUTERNAME%` in every log, in the note and in report.json; the save goes in as it is. The dialog keeps a report
under GitHub's 25 MB attachment limit.

## Opening a report (demo.bat 71)

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/reports/open-report.ps1 -Issue 12
powershell -NoProfile -ExecutionPolicy Bypass -File tools/reports/open-report.ps1 -Report C:\path\report-20261025-143210.zip
```

With neither argument it opens the newest report in your own `Reports` folder. It works in
`D:\skiAreaDesignChallenge\reports\<issue-N or report name>\`, outside git:

1. an issue's zip is downloaded from its attachment link;
2. the zip is unpacked, refusing any path that would land outside its folder;
3. report.json is printed;
4. a scratch library (`data\`) gets the area, copied from your library (or the script names it, with its centre and
   size, to download in the game), and the save as `Saves\<package>\report-save`;
5. the game starts with `-data <scratch> -reportsettings report.json`. The player's settings apply for that session
   only, under the `MountainPlanner.Report.` prefix, never over yours. Open the save from Load Area.

`-NoLaunch` stops after step 4; `-Game <exe>` picks the build (default `Builds\Windows`, then `Builds\Release\Player`).

## Symbols

The build is Mono, so a release stack trace already names each method. For a crash dump, a tagged release keeps its
symbols in `D:\skiAreaDesignChallenge\symbols\<version>\` (release.ps1's symbols step; `-KeepSymbols` keeps a
preview's; never in git or the zip):

- `native\`: the shipped exe, UnityPlayer.dll and UnityCrashHandler64.exe, with Unity's matching player PDBs;
- `burst\`: the Burst debug information (the build's `_BurstDebugInformation_DoNotShip` folder);
- `managed\`: the game's DLLs, and their PDBs when they match the shipped DLLs byte for byte;
- `symbols.json`: version, commit, Unity version and a SHA-256 for every file.

To read a `crash.dmp`, open it in Visual Studio or WinDbg (both free) with the symbol path
`D:\skiAreaDesignChallenge\symbols\<version>\native;D:\skiAreaDesignChallenge\symbols\<version>\burst;srv*%TEMP%\symcache*https://symbolserver.unity3d.com`
(open-report.ps1 prints it). Unity's public symbol server covers the engine's own frames.

## Triage on GitHub

- Issues arrive through the **Problem** and **Suggestion** forms (`.github/ISSUE_TEMPLATE/`), labelled
  `problem` or `suggestion`, and `new`. `new` means nobody has looked yet: `label:new` is the inbox. Triage adds an
  area label, `confirmed` or `needs-info` and a milestone, then takes `new` off.
- Issues from other people: `is:issue is:open -author:@me`. The owner's own, and the threads' (they file through the
  owner's `gh`), are `author:@me`. A report the owner files for a player without GitHub gets `from-player`; both
  together: `is:issue is:open (-author:@me OR label:from-player)`.
- Area labels (terrain, trees, lifts, trails, interface, performance, downloads, saves) and `crash`, `needs-info`
  and `confirmed` are added by hand; each release has a milestone (`v0.3.0` first).
- A report zip in an issue is public. Never re-post logs or saves elsewhere, and never quote a player's email.
- A thread can then be told "fix #12": it reads the issue, opens the report with demo.bat 71 and says what it found.
