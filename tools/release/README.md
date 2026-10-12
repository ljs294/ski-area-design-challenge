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
   (what was built and checked).

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
  - `PlayerSettings.bundleVersion` (`Application.version`, and the exe's properties) for that build only. The committed
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
  - the session's first error shows a notice, once, with Open folder;
  - Settings › Data › Logs has Open and Copy report.
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

It covers version names, fresh results and logs, licence lapses, and the zip layout. It needs no Unity or GPU.
