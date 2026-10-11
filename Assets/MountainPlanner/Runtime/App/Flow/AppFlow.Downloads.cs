using System;
using System.Linq;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Background downloads (G5), their dialogs and the offline states (S11): task P2-06.
    /// - One download at a time; it keeps going across scene loads while you explore another area.
    /// - A download paused by a quit or crash resumes by itself once the title is up (owner D1).
    /// - A lost connection makes it wait and try again (D2); any other failure says what happened and what to do.
    /// - The S4 pill shows on S1 and S2, in the picker's footer and in the game's HUD bar (D3).
    /// - With offline mode on, New Area and Resume explain why they can't go on, and offer to turn it off.
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>Command-line switches whose runs never resume a paused download by themselves (captures).</summary>
        static readonly string[] NoAutoResume = { "-flowcapture", "-uicapture", "-titlemovie", "-titleshots" };

        /// <summary>Resume the oldest paused download once the title is up (off in batch runs and captures; tests set it).</summary>
        public bool ResumeAtLaunch;
        /// <summary>A dialog waiting for the cover to lift (the flow's screens take no keys under it).</summary>
        Action _queuedDialog;
        PendingDownload _downloadArg;
        int _hudVersion = -2, _pickerVersion = -2;
        UI.MountainHud _wiredHud;

        void WireDownloads()
        {
            string[] args = Environment.GetCommandLineArgs();
            ResumeAtLaunch = !Application.isBatchMode && !NoAutoResume.Any(a => Array.IndexOf(args, a) >= 0);
            // -download <name> <latitude> <longitude> <km>: starts that download once the title is up (demo.bat 54's kill test).
            int i = Array.IndexOf(args, "-download");
            if (i >= 0 && i + 4 < args.Length
                && double.TryParse(args[i + 2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lat)
                && double.TryParse(args[i + 3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lon)
                && double.TryParse(args[i + 4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double km))
                _downloadArg = DownloadService.Request(PickedSite.Create(args[i + 1], Albers6350.Forward(new GeoPoint(lat, lon)), km, false, default),
                                                       DownloadService.UtcStamp(DateTime.UtcNow));

            Screens.ResumeChosen += r => StartDownload(r.Pending);
            Screens.DiscardChosen += r =>
            {
                if (Downloads.Active && Downloads.Current?.Id == r.Pending.Id) { Screens.Toast("That download is running; cancel it first."); return; }
                PendingDownloads.Remove(DataRoot, r.Pending);
                RefreshLibrary();
            };
            Screens.MinimiseChosen += Controller.MinimiseDownload;
            Screens.RestoreChosen += Controller.RestoreDownload;
            Screens.CancelConfirmed += keep => Downloads.Cancel(keep);
            Screens.RetryChosen += Retry;
            Screens.TryNowChosen += Downloads.TryNow;
            Screens.CloseChosen += Later;
            if (Picker != null)
            {
                Picker.LoadAreaChosen += () => { _mode = LibraryMode.Load; Controller.MyResorts(); };
                Picker.PillChosen += Controller.RestoreDownload;
            }
            Downloads.Finished += folder => Controller.DownloadFinished(folder);
            Downloads.Stopped += kept =>
            {
                Controller.DownloadStopped();
                Screens.Toast(kept ? "Download paused. Resume it from Manage Areas." : "Download discarded.");
                if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
            };
            Downloads.Failed += problem => WhenCoverIsDown(() => ShowProblem(problem));
            OfflineState.Changed += OnOfflineChanged;
            Screens.SettingsClearCacheConfirmed += ClearDownloadCache;
        }

        void ReleaseDownloads() => OfflineState.Changed -= OnOfflineChanged;

        /// <summary>Main thread, every frame: the download's progress to the card, the pills and the picker; queued dialogs; the launch resume.</summary>
        void UpdateDownloads()
        {
            Downloads.Pump(Time.unscaledDeltaTime);
            var view = Downloads.View;
            bool shown = Downloads.Current != null;
            if (shown) Screens.RenderDownload(view);

            // The game's pill is the HUD bar's (owner D3); the picker's sits in its footer, above the flow's.
            var hud = _viewer != null ? _viewer.Hud : null;
            if (hud != null && hud != _wiredHud)
            {
                _wiredHud = hud;   // a new scene's HUD
                _hudVersion = -2;
                hud.DownloadChosen += Controller.RestoreDownload;
            }
            int key = shown ? view.Version : -1;
            if (hud != null && key != _hudVersion)
            {
                _hudVersion = key;
                if (shown && !MountainViewer.TitleMode) hud.SetDownload(view.BarLabel, view.BarText, view.Fraction, FlowScreens.PillState(view.Phase));
                else hud.SetDownload(null, null, 0, "");
            }
            if (Picker != null && !Picker.IsOpen) _pickerVersion = -2;   // drawn afresh when it opens
            else if (Picker != null && key != _pickerVersion)
            {
                _pickerVersion = key;
                Picker.SetPill(shown ? view.Pill : null, view.Fraction, FlowScreens.PillState(view.Phase));
                Picker.SetBusy(Downloads.Active ? "One download at a time: wait for this one, or cancel it." : "");
            }

            if (_queuedDialog != null && !Screens.CoverUp && !Screens.ConfirmOpen && !Screens.PromptOpen)
            {
                var show = _queuedDialog;
                _queuedDialog = null;
                show();
            }
            if (ResumeAtLaunch && _viewer != null && !Screens.CoverUp && MountainViewer.TitleMode) ResumeOldest();
            if (_downloadArg != null && _viewer != null && !Screens.CoverUp)
            {
                var d = _downloadArg;
                _downloadArg = null;
                // The same request as a paused one resumes it (the id is the same): take the record's progress.
                d = PendingDownloads.List(DataRoot).FirstOrDefault(p => p.Id == d.Id) ?? d;
                if (!Downloads.Active) StartDownload(d);
            }
        }

        /// <summary>Owner D1: the oldest paused download continues by itself, as the pill. Offline, it waits there.</summary>
        void ResumeOldest()
        {
            ResumeAtLaunch = false;
            if (Downloads.Active || LibraryIndex.Refusal(DataRoot) != null) return;
            var d = PendingDownloads.List(DataRoot).FirstOrDefault();
            if (d == null) return;
            Debug.Log($"[Downloads] Resuming {d.Name} at {(d.LastStage.Length > 0 ? d.LastStage : "the start")} ({d.LastOverall:P0}), paused by a quit or crash");
            if (Downloads.Start(d)) Controller.DownloadStarted(openCard: false);
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        /// <summary>Shows a dialog now, or once the cover is down (under it the flow takes no keys).</summary>
        void WhenCoverIsDown(Action show)
        {
            if (Screens.CoverUp || Screens.ConfirmOpen || Screens.PromptOpen) _queuedDialog = show;
            else show();
        }

        /// <summary>Title and Library's New Area: with offline mode on, say so (and offer to turn it off) instead of an empty picker.</summary>
        void NewArea()
        {
            if (OfflineState.Mode == OfflineMode.Setting) ShowOfflineMode("Choosing a new area needs the internet, for the map, the search and the download.", Controller.NewResort);
            else Controller.NewResort();
        }

        void StartDownload(PendingDownload d)
        {
            if (Downloads.Active && Downloads.Current?.Id != d.Id)
            {
                var running = Downloads.View;
                Screens.Alert("One download at a time",
                              $"{running.Name} is downloading ({running.Percent}). Wait for it to finish, or cancel it, then start {d.Name}.",
                              "Show the download", Controller.RestoreDownload, "Close", error: false);
                return;
            }
            if (Downloads.Running) return;   // this one, already going
            if (OfflineState.Mode == OfflineMode.Setting)
            {
                ShowOfflineMode($"Downloading {d.Name} needs the internet.", () => StartDownload(d));
                return;
            }
            if (Downloads.Start(d)) Controller.DownloadStarted();
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        void Retry()
        {
            var d = Downloads.Current;
            if (d == null) return;
            if (OfflineState.Mode == OfflineMode.Setting) { ShowOfflineMode($"Downloading {d.Name} needs the internet.", Retry); return; }
            if (Downloads.Start(d)) Controller.DownloadStarted();
        }

        /// <summary>After a failure: put it away; its record stays, so Manage Areas offers Resume.</summary>
        void Later()
        {
            Downloads.Dismiss();
            Controller.DownloadStopped();
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        /// <summary>The S11 error dialog for a download that stopped: what happened, what to do, Try again.</summary>
        void ShowProblem(DownloadProblem problem)
        {
            if (Downloads.Current == null || Downloads.View.Phase != DownloadPhase.Failed) return;   // retried or put away meanwhile
            string name = Downloads.View.Name;
            bool data = problem.Kind == ProblemKind.DiskFull || problem.Kind == ProblemKind.AccessDenied;
            Screens.Alert(problem.Title, $"The download of {name} stopped. {problem.Text}", "Try again", Retry, "Later",
                          data ? "Settings › Data" : null,
                          data ? () => { Screens.ShowSettings(); Screens.Settings.ShowPage(SettingsWindow.DataPage); } : (Action)null);
        }

        /// <summary>The offline dialog when offline mode is the reason (S11): turn it off and carry on, or open an area already here.</summary>
        void ShowOfflineMode(string why, Action thenContinue)
        {
            bool forced = Array.IndexOf(Environment.GetCommandLineArgs(), "-offline") >= 0;
            Screens.Alert("Offline mode is on",
                          why + (forced ? " This run was started offline (-offline)." : " Your downloaded areas still open as usual."),
                          forced ? null : "Turn offline mode off",
                          forced ? (Action)null : () =>
                          {
                              DataPreferences.SetOffline(false);   // through Settings' own path: Http, OfflineState and the Data page follow
                              thenContinue?.Invoke();
                          },
                          "Close", "Load Area", () => { _mode = LibraryMode.Load; Controller.MyResorts(); }, error: false);
        }

        /// <summary>Why the download cache can't be cleared now, or null.</summary>
        string CacheLocked() => Downloads.Active ? "Wait for the download to finish, or cancel it, to clear it." : null;

        bool _clearing;

        /// <summary>
        /// Settings › Data › Download cache › Clear (owner D5), confirmed: the whole download and map-tile cache goes, on a
        /// worker thread. Files in use (a map tile being read) stay; the toast says what was freed.
        /// </summary>
        async void ClearDownloadCache()
        {
            if (_clearing || CacheLocked() != null) return;
            _clearing = true;
            if (Picker != null && !Picker.IsOpen) Picker.Services = null;   // made again, on an empty cache, next time it opens
            string folder = PipelineDownloader.CacheFolder(DataRoot);
            long freed = 0;
            try
            {
                freed = await System.Threading.Tasks.Task.Run(() =>
                {
                    long total = 0;
                    if (!System.IO.Directory.Exists(folder)) return 0L;
                    foreach (var f in new System.IO.DirectoryInfo(folder).EnumerateFiles("*", System.IO.SearchOption.AllDirectories))
                    {
                        try
                        {
                            long length = f.Length;
                            f.Delete();
                            total += length;
                        }
                        catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { }
                    }
                    return total;
                });
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException) { Debug.LogWarning($"[Downloads] Clearing the cache: {e.Message}"); }
            finally { _clearing = false; }
            if (this == null) return;
            Screens.Toast(freed > 0 ? $"{LibraryViewModel.Disk(freed)} of downloaded map data cleared." : "Nothing to clear.");
            if (Screens.OverlayOpen && Screens.Settings.Page == SettingsWindow.DataPage) ShowDataPage();
        }

        /// <summary>Offline mode went off (or the connection came back): a download waiting for it goes on at once.</summary>
        void OnOfflineChanged()
        {
            if (Downloads.Waiting && OfflineState.Mode != OfflineMode.Setting && Downloads.View.Problem?.Kind == ProblemKind.OfflineMode) Downloads.TryNow();
        }
    }
}
