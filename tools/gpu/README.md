# GPU lanes

`gpu.ps1` shares the one GPU between threads, and between threads and the owner's own demo.bat runs. It
replaces the old `D:\skiAreaDesignChallenge\.gpu-lock` folder rule. It needs only Windows PowerShell 5.1.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane measure -Name "<thread>" -- <exe> <args...>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane render -Name "<thread>" -- <exe> <args...>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 status [-Json]
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 history [-Since 24h|7d|<ISO time>] [-Name "<thread>"] [-Json]
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 clear-stale
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 release -Name "<thread>"
```

`run` waits for a slot, runs the command, passes its exit code through and releases the slot when the
command ends: normally, on an error, or on Ctrl+C (which also stops the command). Agents launch it with
`run_in_background` and get told when it finishes. Never loop on agent turns waiting for the GPU, and don't
hold a slot while waiting for the owner: the slot lasts exactly as long as the command.

## Lanes

| Lane | Rule | For |
| --- | --- | --- |
| `measure` | Alone: nothing else holds a slot | `-benchmark`, hitch and timing measurements, review movies |
| `render` | Up to 2 at once, none during a measure run | Players with `-screenshot`, `-uicapture` or `-lineup`; PlayMode runs that open site caches; Blender Cycles renders; Lift Lab captures |
| none | Run directly | Builds, EditMode tests, `-nographics` runs, dotnet tests |

The command runs with `MP_GPU_LANE` set to its lane. A timing test judges a player's budget only when it has the GPU
alone (`measure`, or no lane); in `render` it reports the time instead of failing on it.

Waiting runs take numbered tickets and start strictly in ticket order. A waiting measure run therefore
blocks render runs that queued after it, so it can't starve. The one exception: while a GPU-heavy process runs outside
the queue, render runs pass a waiting measure run (see Outside the queue).

## Time limit

Every run has a hard limit, counted from the moment its command starts (waiting doesn't count). The defaults are
**measure 60 min** and **render 120 min**. `-LimitMin N` overrides it for one run, and `-LimitMin 0` means no limit. A run
that reaches its limit is stopped with its whole process tree (`taskkill /T /F`), exits **124** and is recorded as
`time-limit`. This exists because a hung Development player once held the measure slot for 3 hours.

## Outside the queue

The owner's own play never waits. demo.bat's hands-on entries and the owner's games start directly, so they bypass the
queue. A measure run measured during that play reads 2 to 100 times slow. So:
- **A measure run waits** while a GPU-heavy process runs outside the queue. Status shows
  `waiting: GPU process(es) outside the queue: SkiAreaDesignChallenge (pid 1234)`. `-TimeoutSec` still applies.
- **Render runs pass a waiting measure run** while such a process runs. One long play session would otherwise stall
  every render run queued behind the measure run. The measure run starts in ticket order once the process quits.
- **A process that starts during a measure run** marks the run contaminated. gpu.ps1 warns on stderr and the history
  line lists the process. The run itself goes on, with its exit code unchanged. Render runs also record what they saw.
- **What counts:** a process named on the GPU-heavy list that is not inside a queued run's process tree and is not
  headless. A process is headless when its command line has `-batchmode`, `-nographics`, `-b` or `--background`.
  EditMode tests, `-nographics` runs and `blender -b` exports therefore never count.
- **The list:** `SkiAreaDesignChallenge`, `PickerLab`, `LiftLab`, `Unity` (the editor; batch mode doesn't count) and
  `blender` (its window; `-b` doesn't count).

## Options

- `-TimeoutSec N`: give up after N seconds of waiting (exit 124).
- `-LimitMin N`: this run's time limit in minutes; 0 means none (see Time limit).
- `-Priority`: start ahead of every waiting ticket, never ahead of a run that holds its slot. Only the release smoke
  test uses it (`release.ps1`, with a 30-minute wait), so a queue of renders can't time out a release.
- `-RequireDesktop 1920x1080`: fail with exit 125 when the desktop is smaller, as with the 640x480 remote
  desktop on which players misbehave. Add `-DesktopWarnOnly` to only warn.
- `-StateDir <folder>`: a separate state folder (tests). `MP_GPU_STATE` sets it too.
- `-HeavyProcesses a,b`: replaces the GPU-heavy list for this call (tests).
- `<state>\config.json` (all keys optional):
  ```
  { "renderSlots": 2, "limitMin": { "measure": 60, "render": 120 },
    "extraHeavyProcesses": ["cs2", "SomeOtherGame"] }
  ```
  - `extraHeavyProcesses` adds the owner's own games to the GPU-heavy list, by process name with or without `.exe`.
  - `heavyProcesses` replaces the list.

Exit codes: the command's own; 124 timed out or released while waiting, or hit the time limit; 125 desktop check; 126 the command
couldn't start; 64 usage. Arguments after `--` pass through as they are, except that PowerShell splits a
`-name:value` argument into two; use the `-name value` form.

## State

`D:\skiAreaDesignChallenge\.gpu\state.json`, outside the repo, guarded by the named mutex
`Global\MountainPlannerGpu` and written atomically. Each holder and waiter records its lane, name, ticket, the
script's process ID and start time, the command's process ID and start time, the command and the times.

An entry is stale when neither process is alive with its recorded start time, so a reused process ID doesn't
count. Every poll drops stale entries; `status` marks them and `clear-stale` removes them by hand. If only the
script is killed, the command keeps the slot until it exits too. `release -Name` drops a thread's entries
without stopping its processes.

## History

Every ticket appends one JSON line to `<state>\history.jsonl` when it ends. The line holds its lane, name, the
command (first 300 characters), the queued, started and ended times (UTC), the exit code, the outcome and the
processes seen outside the queue:
- **Outcomes:** `ok`, `failed`, `time-limit`, `gave-up-waiting`, `released`, `interrupted` (Ctrl+C),
  `could-not-start`, `desktop-check`, `stale`.
- **When the script is killed:** it can't write its own line, so the poll that drops its entry as stale writes
  one, with `ended` set to when the entry was found.
- **Order:** the line is written in the same locked step that frees the slot, so a ticket's `ended` always comes
  before the start of the run that follows it.
- **Rotation:** past 5 MB the file is renamed `history-<time>.jsonl`, and `history` reads them all.

`gpu.ps1 history` lists the runs in local time and then **Overlaps**. That section flags:
- any two runs that held the GPU together when either one was measure;
- more render runs at once than there are slots;
- measure runs that saw a process outside the queue.

`-Since 24h` (also `90m`, `7d` or an ISO time), `-Name` and `-Json` narrow or change the output.

Runs from gpu.ps1 copies older than task 0.5 (other checkouts) write no history and don't wait for play outside the
queue.

**Old lock:** while `D:\skiAreaDesignChallenge\.gpu-lock` exists, it counts as a measure hold. That lasts
while its `owner.txt` is under 2 hours old, or while a Unity, SkiAreaDesignChallenge, PickerLab, LiftLab or
Blender process runs. The script never deletes it.

## demo.bat

The benchmark entries (21, 26, 31, 41, 42) use the measure lane. The lineup and capture entries (20, 47, 48,
51, 53) use the render lane. They all run under the name `owner`. Interactive play and
the Lift Lab start without the scheduler and never wait; a queued measure run waits for them instead (see Outside the
queue). Entry 125 runs the tests, shows the last 7 days of history and lets you watch a measure run wait for the game.

## Tests

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/test-gpu.ps1
```

The tests take about three minutes and need no GPU. They use a scratch state folder and dummy PowerShell children
that log their start and end times. They check:
- exit codes pass through;
- the command sees its lane in `MP_GPU_LANE`;
- render allows 2 at once while a third waits;
- measure excludes everything;
- a queued measure blocks later renders;
- tickets are served in order;
- killed holders are cleaned up;
- Ctrl+C in the run's console stops the command and frees the slot (a helper sends the console a real Ctrl+C);
- release, timeout and the old-lock compatibility work;
- every ticket leaves exactly one history line with the right outcome, and `history` flags a written measure/render
  overlap, three render runs at once and a contaminated measure run;
- the time limit kills a run's whole tree with exit 124;
- a measure run waits for a GPU-heavy process outside the queue, render runs don't wait and pass it, headless processes
  and a run's own children don't count, and a player started mid-run is recorded.

The GPU-heavy checks use renamed copies of `ping.exe` and `cmd.exe`. Every other check names a process that never
runs, so the owner's play or another thread's player can't upset the tests.

They aren't part of `tools/repo-checks/check.mjs`, because CI runs that on Ubuntu without Windows PowerShell.
Run them after changing `gpu.ps1`.
