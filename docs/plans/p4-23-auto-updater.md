# Task 4.23: the auto updater

Friends who test the game install it once from one small exe, then take each new GitHub release from the game or
its launcher, with no "Unblock" step. Owner decisions, 2026-11-13: option (a), a separate launcher exe (over an
in-game helper or Velopack); `release-assets.githubusercontent.com`, GitHub's download host, is allowed besides
api.github.com and github.com. Formats cleared with 0.0 the same day: no frozen format changes.

Revision 2 (owner, 2026-11-13, after the first demo): people open the game as usual, and the launcher only shows
itself when there is an update. Every start goes through its quick check; no update means the game just starts. An
update asks; yes does a full install that ends on "Launch Ski Area Design Challenge vX.Y.Z", with a version picker
and Check for updates. R1-R4 below; UP6 and UP7 are revised to match.

## Why one exe fixes the zip

A browser marks what it downloads (the Mark of the Web, a `Zone.Identifier` stream). Explorer copies the mark onto
every file it unzips, so a zip from the release page needed Properties > Unblock, or SmartScreen stopped the game. A
program that downloads a file over HTTP itself adds no mark. So the launcher is the only file a browser downloads:
SmartScreen asks once ("More info > Run anyway"), and nothing the launcher installs is marked. It never strips marks
either. Code signing would remove the one prompt; it is paid and not planned.

## Decisions

| # | Decision |
|---|---|
| UP1 | **The launcher** is `tools/launcher`, one C# WinForms exe on .NET Framework 4.8, which Windows 10 and 11 include: about 100 KB, no runtime, no DLLs beside it. It compiles the game's own engine-free update code (`Runtime/Acquisition/Updates`) and `Persistence/ReleaseVersion.cs`, so the game, the launcher and the Core tests agree. Its JSON reader is hand-written (`MiniJson`) for the same reason. |
| UP2 | **The install** is `%LOCALAPPDATA%\Programs\SkiAreaDesignChallenge` (per user, no admin): `Launcher.exe` and `versions\<x.y.z>\`, one folder per version, the zip's top folder stripped. A version counts once its folder holds the empty `.complete` marker, written last. The newest plays, the one before is kept to roll back to, older ones are deleted. There is no state file: the folders are the state. |
| UP3 | **The first run** shows "Install Ski Area Design Challenge" with the version, size and folder, and a desktop-shortcut box. Install copies the exe in as `Launcher.exe`, adds a Start-menu folder (the game and "Uninstall"), installs the latest release and starts it. Uninstall removes the install and its shortcuts, never `%LOCALAPPDATA%\SkiAreaDesignChallenge`, and says so. No Add/Remove Programs entry. |
| UP4 | **The check** asks `api.github.com/repos/ljs294/ski-area-design-challenge/releases/latest` (no drafts, no pre-releases) with the game's User-Agent, no token, no cookies and nothing about the player or the PC. It needs a `vX.Y.Z` tag with the zip and its `.zip.sha256`. A preview build (`+dev`) is never offered a release. 5 s at most; any failure plays the installed version. |
| UP5 | **Installing a version:** the `.sha256` first, which must agree with GitHub's own `digest` when there is one; the zip to `downloads\<zip>.partial`, resumed with a Range request after a drop (a 60 s stall fails it); its SHA-256; free space checked; unzipped to `versions\<v>.partial\` with every entry kept inside it, then renamed and marked. Any failure leaves the installed version as it was. |
| UP6 | **A normal start** (revised by R2): no window while the launcher checks; then the game, or "0.3.3 is out" with Update, Play 0.3.2, What's new and Other versions. The launcher then waits out of sight; a game that closes with an error in its first minute offers the version before. The game gets `-launcher <path> -updatechecked`. |
| UP7 | **In the game:** Settings > Data > Check for updates (PlayerPrefs `UpdateCheck`, on; the launcher reads the same value, and Offline mode, from the registry) and Version with Check now. The title's own check, once per run, is now the fallback: it runs unless the launcher already checked, the switch is off, offline mode or `-offline` is on, or it is a capture or test run. The notice "0.3.3 is out (you have 0.3.2)" has Update, What's new and Not now, and only shows over the title. Update starts the launcher with `--update` and quits, after "Save before leaving?" in an area; the launcher installs at once and ends on the Launch screen (R3). A game with no launcher says Get the installer and opens the release page. A library folder inside the install is refused (an update would replace it). |
| R1 | **Every start goes through the launcher.** The Start-menu shortcut runs `Launcher.exe`. The game exe started directly (a pinned exe, or a copy unzipped by hand, which has the zip's `Launcher.exe` beside it) starts the launcher with `--game <exe> --gameversion <v>` and its own arguments, and quits before the scene loads (a brief flash of its window). Skipped, so the game just plays: started by the launcher (`-updatechecked`), offline mode or `-offline`, the switch off, a capture or test run, a preview build (unless `-asversion`), no connection, or no launcher found. Unity moves Player.log to Player-prev.log at each start, so the hand-off run keeps Player-prev.log as `Player-prev-kept.log` and the next run that plays puts it back: problem reports still send the last real session. Releases up to 0.3.2 don't have the hand-off; it starts with the next release. |
| R2 | **The quick check.** With `--game`, the launcher shows nothing while it asks GitHub (5 s at most). No update, GitHub unreachable, or the switch or offline mode on: it starts that exe again with `-updatechecked` and exits. An update: the "0.3.3 is out" window. A copy unzipped by hand is offered a proper install (into `%LOCALAPPDATA%\Programs\SkiAreaDesignChallenge`, with shortcuts); the copy itself is left alone. That copy is only a fallback (the zip has to be on the release page for the launcher): owner, 2026-11-13, keep the code but describe only Setup.exe, so the READMEs no longer explain unzipping by hand and the laptop test leaves it out. If another launcher holds the lock for over 3 s, the game just starts. |
| R3 | **Every install ends on the Launch screen:** "0.3.3 is installed" with **Launch Ski Area Design Challenge v0.3.3** (the default button), not a game that starts by itself. |
| R4 | **The version list and Check for updates**, on the Launch screen, behind Other versions, and with `--choose` or Shift: the installed versions, then every other release on GitHub with a zip and its `.sha256` (`/releases`, the newest 30; no drafts or pre-releases; asked once, not in offline mode). An installed one says Launch; one that isn't says Install, and asks first, naming the download size and the version removed to make room. Two versions stay installed: the newest and the one just picked (`VersionStore.Prune(picked)`). A picked version plays that once; nothing is remembered, so there is still no state file. Check for updates asks GitHub again and picks a newer release in the list. |
| UP8 | **The launcher updates itself:** each zip ships `Launcher.exe` (file version = the release number). After an install, a newer one replaces the installed launcher: the running exe renames itself to `Launcher.old.exe` (deleted next run) and the new one is copied in. |
| UP9 | **Release assets:** `release.ps1` builds the launcher, puts it in the zip and writes `SkiAreaDesignChallenge-Setup.exe` with its `.sha256`. The Setup name has no version, so `releases/latest/download/SkiAreaDesignChallenge-Setup.exe` is a permanent link. Uploading stays a step the owner approves. |
| UP10 | **Not in this task:** code signing (paid), delta updates, background downloads. Windows 11's Smart App Control, where it is on, blocks unsigned exes with no "Run anyway"; today's zip is blocked the same way. |

## Files

- `Runtime/Acquisition/Updates/`: `GameVersion`, `MiniJson`, `ReleaseInfo` (one release, or the `/releases` list),
  `ShaFile`, `ReleaseFeed` (GitHub and a local folder feed for demos and tests), `UpdateCheck`, `VersionStore`,
  `ReleaseInstaller`.
- `Tests/Core/UpdatesTests.cs` with `TestData/updates/latest-v0.3.2.json`, GitHub's real v0.3.2 answer (trimmed).
- `tools/launcher/`: `Program.cs` (options, log, the registry switch, shortcuts), `LauncherForm.cs` (the window),
  `make-icon.ps1` (the game's icon), `demo-check.ps1` (demo 114/115's read-only report).
- The game: `App/Flow/AppFlow.Updates.cs` (the hand-off, R1, called from `AppFlow.Boot`), `UI/Flow/FlowScreens.Updates.cs`, the `update-notice` in `Flow.uxml`,
  `DataPreferences.UpdateCheck`, the Settings > Data rows, the library-folder guard in `AppFlow.Settings.cs`.
- `tools/release/`: `release.ps1` step 4b and 7, `Build-Launcher` in `release-lib.ps1`, `test-release.ps1`, both
  READMEs.

## Checks

- `dotnet test tools/domain-tests/Tests --filter FullyQualifiedName~UpdatesTests`: versions, GitHub's answer, the
  `.sha256`, the check (an unreachable feed, a preview that never asks), installs that keep two versions, an older
  release picked from the list that stays installed, the releases list (no drafts, pre-releases or releases without
  their zip), the folder feed's list, a damaged download, a digest that disagrees, a resumed download, zip entries
  that point outside, the marker.
- `tools/release/test-release.ps1`: the zip must hold `Launcher.exe`; the launcher builds small with the right versions.
- End to end against GitHub (2026-11-13): the launcher installed v0.3.1 into a scratch root, then updated it to v0.3.2
  (about 18 s each); 0 of 1,348 installed files carried a mark.
- End to end with Revision 2: the game build as 0.3.1 handed over, the launcher asked, Update installed 0.3.2 and
  ended on the Launch screen; as 0.3.2 the game started again with no launcher window; with `-offline` it started
  directly.
- demo.bat 114 and 115, then the laptop test below.

## Laptop test (the owner's laptop)

1. In a browser, download `SkiAreaDesignChallenge-Setup.exe` from a pre-release (so Latest stays as it is) and run it.
   Count SmartScreen prompts: one "Run anyway" is expected.
2. Install. Play, save. In PowerShell, `Get-ChildItem -Recurse "$env:LOCALAPPDATA\Programs\SkiAreaDesignChallenge" |
   Get-Item -Stream Zone.Identifier -ErrorAction SilentlyContinue` lists nothing.
3. To see an update: install the older release with the hidden `--version 0.3.1` (from a terminal), then start from
   the Start menu: it offers the newest; Update ends on "Launch Ski Area Design Challenge v0.3.2"; the save and
   settings are still there; Other versions (or Shift) plays 0.3.1. With no update, a start shows no launcher window.
4. Wi-Fi off: the game starts with no wait. Check for updates off: no request (Player.log has
   `[Updates] No check at start`). Uninstall: the install and shortcuts go, `%LOCALAPPDATA%\SkiAreaDesignChallenge`
   stays.
