# GPU lanes

`gpu.ps1` shares the one GPU between threads, and between threads and the owner's own demo.bat runs. It
replaces the old `D:\skiAreaDesignChallenge\.gpu-lock` folder rule. It needs only Windows PowerShell 5.1.

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane measure -Name "<thread>" -- <exe> <args...>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 run -Lane render -Name "<thread>" -- <exe> <args...>
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/gpu.ps1 status [-Json]
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

Waiting runs take numbered tickets and start strictly in ticket order. A waiting measure run therefore
blocks render runs that queued after it, so it can't starve.

## Options

- `-TimeoutSec N`: give up after N seconds of waiting (exit 124).
- `-RequireDesktop 1920x1080`: fail with exit 125 when the desktop is smaller, as with the 640x480 remote
  desktop on which players misbehave. Add `-DesktopWarnOnly` to only warn.
- `-StateDir <folder>`: a separate state folder (tests). `MP_GPU_STATE` sets it too.
- The render slot count is `renderSlots` in `<state>\config.json` (default 2).

Exit codes: the command's own; 124 timed out or released while waiting; 125 desktop check; 126 the command
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

**Old lock:** while `D:\skiAreaDesignChallenge\.gpu-lock` exists, it counts as a measure hold. That lasts
while its `owner.txt` is under 2 hours old, or while a Unity, SkiAreaDesignChallenge, PickerLab, LiftLab or
Blender process runs. The script never deletes it.

## demo.bat

The benchmark entries (21, 26, 31, 41, 42) use the measure lane. The lineup and capture entries (20, 47, 48,
51, 53) use the render lane. They all run under the name `owner`. Interactive play and
the Lift Lab start without the scheduler.

## Tests

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools/gpu/test-gpu.ps1
```

The tests take about a minute and need no GPU. They use a scratch state folder and dummy PowerShell children
that log their start and end times. They check:
- exit codes pass through;
- render allows 2 at once while a third waits;
- measure excludes everything;
- a queued measure blocks later renders;
- tickets are served in order;
- killed holders are cleaned up;
- release, timeout and the old-lock compatibility work.

They aren't part of `tools/repo-checks/check.mjs`, because CI runs that on Ubuntu without Windows PowerShell.
Run them after changing `gpu.ps1`.
