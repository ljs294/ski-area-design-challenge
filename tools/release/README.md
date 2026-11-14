# Release tools (task P2-10)

One command builds the Windows zip a tester unpacks and runs. Decisions RL1–RL7 are in
`docs/plans/phase0-decisions.md`.

## Building a release

Close the Unity editor, then double-click `release.bat` at the repo root, or choose `demo.bat` 58. Either one runs
`release.ps1`:

1. **Checks:**
   - Unity 6000.3.25f1 is installed;
   - the project is closed in the editor;
   - the working tree is clean;
   - LFS files are real files. If some are still pointers it runs `git lfs checkout`, which reads the local LFS store
     and costs no download. It never runs `git lfs pull`.
2. **The version** comes from the git tag (below).
3. **EditMode tests** in batch mode, which also compile every assembly.
   - The result XML must be newer than this run, so a stale file never counts as a pass.
   - Unity exit 198 means the licence lapsed: refresh it in Unity Hub.
4. **The release build** (`ViewerSetup.BuildRelease`) into `Builds/Release/Player`, with the Jackson Hole 5 km demo and
   its prebuilt cache in StreamingAssets.
5. **The smoke test**, in the GPU render lane (`tools/gpu/gpu.ps1`). The exe starts with `-smoketest` as a fresh install
   (an empty library, offline). It passes when:
   - the title comes in from the built-in demo;
   - the forced exception and a worker-thread one reach the error log, which carries the version;
   - the exe quits with 0.
6. **The zip**, `Builds/Release/SkiAreaDesignChallenge-<version>-win64.zip`, with its `.sha256` and `release.json`
   (what was built and checked). The zip holds `Launcher.exe` too (task 4.23).
7. **The installer** (task 4.23), `Builds/Release/SkiAreaDesignChallenge-Setup.exe` and its `.sha256`: the launcher
   on its own (`tools/launcher`, built with the .NET SDK), the one file a friend downloads. See "Installer and
   updates" below.

Options:
- `-AllowDirty`: build with uncommitted changes. The version is marked `.dirty`.
- `-SkipTests`: run the compile check only.
- `-NoSmoke`: skip the smoke test.
- `-DemoPackage <folder>`: the demo package to bundle.

A tagged release refuses all of these except `-DemoPackage`.

The whole run takes about 15 minutes on the reference PC, plus about 20 minutes of import the first time a checkout
is opened in Unity.

## Where the demo comes from

The demo package isn't committed, because of the LFS quota. The build bundles `-DemoPackage <folder>`, or else the
newest Jackson Hole 5 km in this PC's library (`%LOCALAPPDATA%\SkiAreaDesignChallenge\Resorts`). The library lives
outside the repo, so a fresh clone on this PC finds it too. On a PC without it, run `demo.bat` 12 first: it downloads
the package from USGS in about a minute, with no LFS cost. A release build fails without the demo; a dev build only
warns.

## Versions and tags

- **Release tags are plain `vMAJOR.MINOR.PATCH`**, 0.x while the game is in development. 0.2 is Phase 2.
- **A build of a tagged, clean commit is that version**, `0.2.0`.
- **Any other build is a preview**, `0.2.0+dev.7.abc1234`: seven commits after v0.2.0, ending `.dirty` when there were
  uncommitted changes. Before the first tag it reads `0.0.0+dev.N.sha`.
- `v0.1`, from the MapLibre game, doesn't count.
- **The version goes into:**
  - `PlayerSettings.bundleVersion` (`Application.version`; the exe's file properties show Unity's own version) for that build only. The committed
    ProjectSettings keep `0.0.0`.
  - `Resources/BuildInfo.json`;
  - the title (bottom left) and Credits;
  - the first `[MountainPlanner]` line of Player.log;
  - every error log's header;
  - the zip's name.

To release:
1. Merge to main.
2. Tag the merge commit locally: `git tag -a v0.2.0 -m "..."`.
3. Run `release.bat` on main.
4. Check the zip.
5. Push the tag (after the owner's OK).
6. Upload all four files to the GitHub release (after the owner's OK): the zip, `SkiAreaDesignChallenge-Setup.exe`
   and both `.sha256`. The launcher installs only a release whose zip and `.zip.sha256` are both there.

## Installer and updates (task 4.23)

Friends download one small exe, `SkiAreaDesignChallenge-Setup.exe`, and click SmartScreen's "More info > Run
anyway" once. A program that downloads a file itself over HTTP adds no Mark of the Web, so nothing it installs needs
"Unblock" (it never removes a mark either). Decisions UP1-UP10 are in `docs/plans/p4-23-auto-updater.md`.

- **The link:** the Setup asset has no version in its name, so
  `https://github.com/ljs294/ski-area-design-challenge/releases/latest/download/SkiAreaDesignChallenge-Setup.exe`
  always gives the newest.
- **The install:** `%LOCALAPPDATA%\Programs\SkiAreaDesignChallenge\` holds `Launcher.exe`, `launcher.log` (this run's
  log) and `versions\<x.y.z>\`, one folder per version. A version counts once its folder holds the empty `.complete`
  marker, written last. The newest plays; two versions stay installed (the newest, and the one before or the one
  just picked from the version list); older ones are deleted. There is no state file. Saves, areas, logs and settings
  live in `%LOCALAPPDATA%\SkiAreaDesignChallenge` and the registry, never in the install.
- **Every start checks (R1, R2):** the Start-menu shortcut runs `Launcher.exe`; the game exe started directly (a
  shortcut to it, or a copy unzipped by hand, which has `Launcher.exe` beside it) hands over to the launcher with
  `--game <exe> --gameversion <v>` and quits at once. The launcher checks out of sight: no update (or GitHub
  unreachable within 5 s) starts the game with `-updatechecked`, with no window. An update asks: Update, Play the
  current version, What's new, Other versions. The hand-off is skipped offline, with the switch off, for capture and
  test runs, preview builds and a game with no launcher. Player-prev.log, which problem reports send, is kept through
  the hand-off run (`Player-prev-kept.log`, put back by the next run that plays).
- **The Launch screen (R3, R4):** every install ends on "0.3.3 is installed" with **Launch Ski Area Design Challenge
  v0.3.3**, not a game that starts by itself. It has the version list (installed versions, then GitHub's other
  releases from `/releases`; picking one not installed says what it downloads and which version makes room) and
  Check for updates. `--choose`, or Shift as the launcher starts, opens the same screen. A picked version plays that
  once; nothing is remembered.
- **The check:** `GET https://api.github.com/repos/ljs294/ski-area-design-challenge/releases/latest` with the game's
  User-Agent, no token and nothing about the player. Downloads come from github.com, which hands them to
  `release-assets.githubusercontent.com`. Preview builds (`+dev`) are never offered releases. 5 s timeout; a failure
  just plays the installed version.
- **The install of a version:** the `.sha256` (and GitHub's own digest, when given: both must agree), the zip to
  `downloads\<zip>.partial` (a dropped download resumes), SHA-256, then unzip to `versions\<v>.partial\` (every entry
  kept inside) and rename. A failure leaves the installed version untouched.
- **In the game:** Settings > Data > Check for updates (on by default; the launcher reads the same switch, and
  Offline mode, from the registry) and Version > Check now. The title's own check is now the fallback for a start the
  launcher didn't check; Update starts the launcher with `--update`, which installs at once and ends on the Launch
  screen. A game with no launcher offers the release page instead. `-asversion <x.y.z>` makes a preview build compare
  as that release (demo 115).
- **The launcher's own update:** each zip ships `Launcher.exe`; a newer one replaces the installed launcher.
- **Options:** `--game EXE --gameversion V` (the game's hand-off); for demos and tests `--installroot DIR`,
  `--version X.Y.Z`, `--feed DIR` (a local folder in GitHub's shape), `--update`, `--choose` (or Shift),
  `--uninstall`, `--no-shortcuts`, `--no-start`, `--yes`, `-- <game args>`.
- **Code:** `Runtime/Acquisition/Updates/` is engine-free and shared by the game and the launcher (Core tests:
  `dotnet test tools/domain-tests/Tests --filter FullyQualifiedName~UpdatesTests`). `tools/launcher/` is the window.
- **Not signed:** SmartScreen's one "Run anyway" stays until the exe is code-signed (paid; not planned). Windows 11's
  Smart App Control, where it is on, blocks unsigned exes outright, the zip's too.

## Error logs

- **Where:** `%LOCALAPPDATA%\SkiAreaDesignChallenge\Logs\errors-<yyyyMMdd-HHmmss>.log`.
  - There's one file per session that had an error; clean sessions leave nothing.
  - The newest 10 are kept, each at most 5 MB.
  - Identical repeats are counted rather than written again.
- **The header:**
  - the version, commit and build time;
  - Windows, CPU, memory and GPU;
  - the display, quality level and command line;
  - the paths to Player.log and Unity's own crash folder, for hard crashes that C# can't catch.
- **The code:** `Persistence/ErrorLog.cs` is plain C# that `dotnet test` covers. `App/Diagnostics/ErrorReporter.cs`
  holds the Unity hooks.
- **In the game:**
  - the session's first error shows a notice, once, with Send logs and Open folder;
  - Settings › Data › Logs has Open and Send logs… (task P3-14; Copy report is gone).
- **Problem reports (task P3-14):** Send feedback saves `<data>/Reports/report-<stamp>.zip` (report.json "problem
  report" v1, the logs, the crash dump, the save, a screenshot), shows it in Explorer and opens a pre-filled GitHub
  issue. `tools/reports/open-report.ps1` opens one on the owner's PC (demo.bat 71); see `tools/reports/README.md`.
- **Flags:**
  - `-errorlogs <folder>` writes the logs elsewhere;
  - `-crashtest` forces a test exception once the title is in;
  - `-smoketest` does that, then quits with 0 or 3.

## The compile check (the CI gap)

CI's dotnet build compiles only Domain, Simulation, Persistence and Acquisition, so an error in App, UI,
Presentation, World or Editor passes CI. `compile-check.ps1` compiles everything in Unity batch mode. Exit codes:
- 0: everything compiled;
- 1: compile errors (they're listed);
- 2: the editor has the project open;
- 3: no fresh log;
- 198: the licence lapsed.

It runs inside the release command. To run it before every push that touches Unity C#, opt in once per clone:

```
git config core.hooksPath tools/git-hooks
```

The hook skips, with a message, when the editor has the project open or the checkout has never been opened in Unity.

## Self-test

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/release/test-release.ps1
```

It covers version names, fresh results and logs, licence lapses, the zip layout and the launcher build. It needs no
Unity or GPU.
