# P2-06 plan: background downloads (G5), dialogs and offline states (S11)

Branch `feature/p2-06-downloads` from main 06e12b5. demo.bat 54. Status: 🟩 implementing. Owner decisions
2026-10-08: D1 A, D2 A, D3 A, D5 A; D4 and D6 not answered, so the recommendations (A) stand.

## 1. What exists (from task 14, P2-04 and P2-05)

| Part | Today | Gap against row 06 |
| --- | --- | --- |
| DownloadService | One download at a time on a worker thread; it survives scene reloads; Pump hands the newest snapshot to the card; download.json is written at start, refreshed every 2 s, deleted on finish | It runs on the shared thread pool at normal priority, and the Preparing stage can take every core, so frames could hitch. Nothing resumes on its own after a crash or quit: you have to find the paused row in Manage Areas |
| Resume | Every remote read goes through the download cache (atomic temp-then-move files), so a rerun fetches nothing it already has | A rerun replays every finished stage from the cache (decode and assemble) and always repeats Building and Preparing. The card starts again at 0%, so it doesn't *look* resumed |
| S4 card | Stages with ticks, bar, time left, detail line, MB and speed, Minimise, Cancel (keep or discard) | Errors show the raw exception ("Stopped: Network access is disabled."), against S11 |
| Pill | "Downloading Crystal Mountain · 41%", bottom left on the title and library, floating top centre in the game | It sits under the picker window (the picker is a higher document). In the game it floats over the 3D view, not in the HUD the mockup defines. There is no paused, waiting or failed look |
| Dialogs | `Confirm` (danger or green) and `Prompt` on UiFocus modals | No error dialog and no offline dialog; the toast does both jobs |
| Offline | `-offline` and Settings › Data › Offline mode set `Http.NetworkDisabled`; the picker has a "No connection" panel with Retry | The picker can't tell "offline mode is on" from "the cable is out". A download started while offline fails with a raw message. Credits has no offline helper for P2-07 |
| Cache clear | Free space removes old terrain-cache versions | The download and map-tile cache (`<data>/download-cache`, 4.4 GB here) can't be cleared from the game (backlog, D5) |

## 2. Background download design

- **One at a time; it keeps going while you explore.** Keep DownloadService's single slot. Starting a second
  download never queues and never needs a toast:
  - the picker's Download button turns into a disabled line, "Crystal Mountain is downloading (41%). One at a
    time.";
  - Resume in Manage Areas is greyed with the same reason.
- **Its own worker thread.** A dedicated `Thread` at `BelowNormal` priority replaces `Task.Run`. Its async
  continuations run on a small scheduler that is also below normal, and the parallel parts of Preparing are capped at
  `ProcessorCount − 2`. The render thread and the main thread always keep cores free (acceptance: frame p95).
- **Nothing allocates per frame.**
  - `Pump` only moves a reference.
  - The view model changes at most 4 times a second (snapshots arrive every 250 ms).
  - `RenderDownload` sets text only when its version number changes; today it runs every frame.
  - A test measures allocation over 300 Pump and Render frames with no new snapshot: 0 B.
- **Network loss mid-download (D2).** When a stage fails because the connection is gone (`HttpRequestException`,
  timeouts, DNS), the download doesn't stop. It goes into a **Waiting** phase:
  - the card says "Waiting for the connection · trying again in 15 s" and offers Try now;
  - the pill says "Crystal Mountain · waiting for connection".
  - The download cache keeps what arrived, so the retry continues from there.
  - Offline mode turned on mid-download pauses it the same way, with no retries until it is turned off.
- **Error classes, never raw exceptions (S11).** A small `DownloadErrors.Describe(Exception)` turns each failure into
  a title, what happened and what to do; the raw message goes to Player.log only.

  | Class | Title | What to do |
  | --- | --- | --- |
  | No connection | "No connection" | Waits and retries by itself (above) |
  | Offline mode | "Offline mode is on" | Turn off offline mode (button) |
  | Disk full | "Not enough disk space" | Needs about N MB more; Free space, or choose another library folder |
  | Access denied | "The library folder can't be written" | Open Settings › Data |
  | Service busy (429, 502, 503, 504) | "A map service is busy" | Waits and retries by itself every minute (added 2026-10-08, after USGS answered 502 mid-test) |
  | Server error (other statuses, bad data) | "A map service didn't answer properly" | Try again later; what was downloaded is kept |
  | No data here | "There's no elevation data for this square" | Choose another site (no retry) |
  | Anything else | "The download stopped" | Try again; the log has the details (Open log folder) |

## 3. Resume rules (no download.json bump)

1. **When:**
   - At launch, the oldest paused record (`PendingDownloads.List`) resumes by itself as soon as the title is up
     (D1).
   - With offline mode on, it waits in the pill as "paused · offline" instead of failing.
   - A record whose last run failed with a non-retryable class (No data here) is left for Manage Areas.
2. **Where it starts:** the same request rebuilds the same folder (the id is a hash of name and site).
   - *Download stages:* every finished request comes from the download cache, so the stages before the one reached
     finish with **0 bytes over the network** and run only decode and assemble.
   - *Building:* if `build/manifest.json` exists and matches the request (same site and size), the pipeline skips
     straight to Preparing, because the manifest is written last, atomically.
   - *Preparing:* if `TerrainCache.IsValid` passes on the build folder, it finishes at once.
   - This uses only files that already exist. Nothing new is written, so **no format changes**.
3. **What the card shows:** at Start, the card is seeded from the record's `LastStage` and `LastOverall` (v1 fields,
   kept "for display only"):
   - "Resuming at Forest · 41%" with the earlier stages ticked;
   - the real progress takes over once the pipeline passes that point;
   - the bar never moves backwards while the cached stages replay.
4. **Crash safety:** the record is written atomically (AtomicFile) every 2 s. A kill mid-write leaves the old record.
   A record that can't be read is skipped, never an error (this is already true).
5. **If replaying stages turns out slow** (I'll measure Jackson Hole at 5 km), the fallback is a per-stage checkpoint
   in the build folder. That would be a new internal file, not one of the five frozen formats, and I'd ask before
   doing it. **download.json stays v1** either way; the coordinator has been told.

## 4. The pill everywhere (S4)

One pill, same text and look on every screen. It has four states:

```
 ↓ Crystal Mountain · 41% ▮▮▮▮▯▯▯▯▯▯     (running: thin progress hairline under the text)
 ‖ Crystal Mountain · paused              (paused, offline)
 ⌁ Crystal Mountain · waiting for connection
 ! Crystal Mountain · stopped — details   (failed: opens the card with the error)
```

| Screen | Where |
| --- | --- |
| S1 title, S2 library | Bottom left as today, clear of P2-03's title region. If P2-03's new title wants that corner, the pill follows whatever corner P2-03 leaves free |
| S3 picker | In the picker's footer beside Download (moved to the picker's document, so it sits above the window, not under it) |
| S6 game (D3) | **In the HUD status bar** as a segment at its right end, mockup-first: I add it to `ui-layout.html` (`data-ui="dl-pill"`, a `demo=p2` state), then port it in a small, separated MountainHud/Hud.uxml block. The floating top-centre pill goes |

Clicking the pill, or pressing its key (D6), restores the card.

## 5. Dialogs and offline states (S11)

One modal component, `FlowDialog`, in the existing Confirm style (see `s11-confirm`), with three kinds:

```
┌ Confirm ──────────────────────────────┐   ┌ Error ────────────────────────────────┐
│ Discard the paused download of        │   │ Not enough disk space                  │
│ Crystal Mountain? Its 182 MB of       │   │ Crystal Mountain needs about 240 MB    │
│ partial files are deleted.            │   │ more on D:. What was downloaded is     │
│                                       │   │ kept.                                  │
│               [Cancel] [Discard]      │   │ [Open Settings › Data]   [Close][Retry]│
└───────────────────────────────────────┘   └────────────────────────────────────────┘
┌ Offline ──────────────────────────────┐
│ You're offline                        │   (or "Offline mode is on" with
│ Choosing a new area needs the         │    [Turn offline mode off] when it's
│ internet. Your downloaded areas still │    the setting, not the cable)
│ open as usual.                        │
│            [Load Area]  [Try again]   │
└───────────────────────────────────────┘
```

- Confirm keeps today's API and look (`Confirm(..., danger:)`); Error and Offline are new. All three take the
  keyboard (Esc = the cancel action, Enter = the default), and are tested at 50–150% in UiKeyboardTests.

**Offline states, flow by flow:**

| Flow | Offline mode on | Cable out |
| --- | --- | --- |
| Title › New Area | Offline dialog (setting kind) instead of an empty picker | The picker opens; its panel says "No connection" with Retry (today), plus Load Area |
| Picker | Can't be reached (above) | Panel as today, wording per S11; the pill stays visible |
| Download start | Offline dialog (setting kind) | Starts, then shows **Waiting** (§2) |
| Download running | Pauses: "paused · offline" | Waiting, retrying by itself |
| Launch with a paused record | Pill "paused · offline", no auto-resume | Auto-resume goes straight to Waiting |
| Credits (P2-07) | `OfflineState` helper | `OfflineState` helper |

**For P2-07:** a static `MountainPlanner.UI.Flow.OfflineState` with:
- `Mode` (None, Setting, NoConnection);
- `Changed`;
- `Describe()` for the credits line.

The connection kind comes from the last network result (no polling). It lands in an early commit, so P2-07 can merge
it.

## 6. Tests and evidence

- **EditMode** (fake downloader):
  - the resume rules: a killed download (the task is abandoned and a new DownloadService opens the same `-data`)
    resumes at the recorded stage, earlier stages tick at once, and the bar never goes backwards;
  - the error classes;
  - the Waiting phase and its retry timer (fake clock);
  - one-at-a-time;
  - the offline helper;
  - FlowController transitions;
  - zero allocation per frame.
- **Core (.NET too):** the pipeline resume. A recording byte source fails at stage N; on the second run it sees no
  requests for stages before N, and Building is skipped when a matching manifest exists.
- **PlayMode:**
  - a download continues across a scene reload and while another area is opened;
  - the pill is visible on every screen, including over the picker;
  - dialogs work with the keyboard.
- **Real kill test (acceptance):** demo.bat 54 and a script on a scratch `-data`:
  1. start a real download;
  2. `taskkill /F` it in the Forest stage;
  3. relaunch;
  4. check Player.log for `resumed at Forest` and 0 network bytes for Terrain.

  demo.bat 38 keeps its tip, and now resumes by itself.
- **Benchmark:**
  - before: demo 41 on main 06e12b5;
  - after: the same run with a new `-benchdownload <site>` flag, which starts a real download into a scratch library
    during the camera path;
  - `compare.mjs before after`; the High p95 must stay ≤20 ms (the budget) and close to before.
- **Captures:** `-uicapture` adds these states, in dark and light (and at 50% and 150%):
  - s4-resuming, s4-waiting, s4-failed;
  - the pill in each of its 4 states on title, picker and HUD;
  - s11-confirm, s11-error, s11-offline-setting, s11-offline-cable;
  - s3-picker-offline.
- Plus the usual: `dotnet test`, repo checks, an adversarial non-author review of looks and function.

## 7. Decisions for the owner

| # | Question | Options | Recommended |
| --- | --- | --- | --- |
| D1 | After a crash or quit, how does a paused download come back? | A: resumes by itself at launch (pill shows it). B: asks with a dialog at launch. C: only from Manage Areas (today) | **A** |
| D2 | The connection drops mid-download | A: wait and retry by itself every 15 s (Waiting state). B: stop with an error and a Retry button | **A** |
| D3 | The pill in the game | A: a segment in the HUD status bar, mockup first. B: keep it floating top centre | **A** |
| D4 | Is "resumes from the stage it reached" met by: cached stages replay with 0 network bytes, Building is skipped once its manifest exists, and the card resumes at the saved stage and percent? | A: yes (no format change). B: no, skip finished stages outright (a per-stage checkpoint file) | **A**, unless my measurement shows the replay is slow |
| D5 | Clear the download and map-tile cache (4.4 GB here) | A: a "Download cache · 4.4 GB · Clear" row in Settings › Data (with a confirm; never while a download runs, and paused downloads stay resumable but will refetch). B: later | **A** |
| D6 | A key to restore the download card | A: none, click only. B: a rebindable key, e.g. J | **A** (fewer keys) |
