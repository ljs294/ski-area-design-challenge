# Mountain Planner Time Engine

A standalone, deterministic calendar engine and terminal simulator. It does not
depend on React, MapLibre, Electron, or the rest of the game.

Run the interactive dashboard from the repository root:

```powershell
npm run sim:time
```

On Windows, you can instead double-click `run-time-engine.bat` in this folder.
The launcher automatically changes to the repository root before invoking npm.

The full repository path is:

```text
C:\Users\vermo\Documents\ski-area-deign-challenge\ski-area-design-challenge
```

The clock begins in summer. To start a visibly running winter:

```text
skip to winter
confirm
speed ultrafast
play
```

Useful diagnostic commands:

```powershell
npm run sim:time -- --skip winter --yes --step 1w --json
npm run sim:time -- --skip season --yes --json
```

## Structure

- `src/timeEngine.ts` — platform-independent clock, boundaries, events, and snapshots.
- `src/cli.ts` — Node terminal dashboard, command handling, and real-time scheduler.
- `test/timeEngine.test.ts` — deterministic calendar and persistence tests.

Future game integration should import the pure engine and wrap it in a Web
Worker. Gameplay systems such as weather and operations can consume its typed
time-boundary events.
