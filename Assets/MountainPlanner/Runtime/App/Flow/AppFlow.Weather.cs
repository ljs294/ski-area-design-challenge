using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Get weather (task 4.05, PR A2): the weather for an area downloaded before it existed, or whose download finished
    /// without it, as a background job (<see cref="WeatherBackfill"/>). Two ways in: the Get weather button in Manage Areas,
    /// and an automatic try when the player opens such an area online (it opens at once; the weather comes in behind it, and
    /// a failure is only logged, to try again at the next open). The area keeps its id, so its saves stay where they are and
    /// it may stay open meanwhile. One area job at a time, with Get water and roads (which moves the area's folder).
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>The area the weather is being added to, or null.</summary>
        string _weatherJob;
        string _weatherText = "";

        LibraryViewModel MarkWeather(LibraryViewModel vm)
        {
            if (_weatherJob == null) return vm;
            foreach (var row in vm.Rows)
                if (row.Entry != null && SameFolder(row.Entry.Folder, _weatherJob))
                {
                    row.Busy = true;
                    row.WaterAndRoadsText = _weatherText;
                }
            return vm;
        }

        /// <summary>Get weather in Manage Areas: says why not when it can't run now.</summary>
        void GetWeather(LibraryRow r)
        {
            if (r?.Entry == null || !r.NeedsWeather) return;
            if (_weatherJob != null) { Screens.Toast("The weather is already being added to another area; one at a time."); Controller.RestoreDownload(); return; }
            if (_waterAndRoads != null) { Screens.Toast("Wait for Get water and roads to finish first: one area job at a time."); Controller.RestoreDownload(); return; }
            if (Downloads.Active) { Screens.Toast("Wait for the download to finish, or cancel it, first.", 5); return; }
            if (OfflineState.Mode == OfflineMode.Setting && WeatherBuild.ReplayFolder == null)
            {
                ShowOfflineMode($"Getting the weather for {r.Name} needs the internet.", () => GetWeather(r));
                return;
            }
            RunWeather(r.Entry.Folder, r.Name, manual: true, retry: () => GetWeather(r));
        }

        /// <summary>
        /// The automatic try (task 4.05, PR A2): once the area the player opened is on screen, an area with no weather yet gets
        /// it in the background, if the game is online and nothing else is running. Never the title's backdrop nor the demo.
        /// </summary>
        void WeatherWhenOpen(string packageFolder)
        {
            if (string.IsNullOrEmpty(packageFolder) || BundledDemo.Contains(packageFolder)) return;
            try { if (!WeatherBuild.Needed(ResortPackage.ReadManifest(packageFolder))) return; }
            catch (Exception e) when (e is IOException || e is InvalidDataException || e is Newtonsoft.Json.JsonException) { return; }
            StartCoroutine(WeatherOnceShown(packageFolder));
        }

        IEnumerator WeatherOnceShown(string packageFolder)
        {
            // Wait for the area to be built and shown: its terrain cache comes first, the weather's 20 s of one core after.
            float waited = 0;
            while ((_viewer == null || !_viewer.Ready || Screens.CoverUp) && waited < 120f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }
            if (_viewer == null || MountainViewer.TitleMode || !SameFolder(_viewer.OpenPackage ?? "", packageFolder)) yield break;   // left it already
            if (_weatherJob != null || _waterAndRoads != null || Downloads.Active) yield break;
            if (OfflineState.IsOffline && WeatherBuild.ReplayFolder == null) { Debug.Log($"[AppFlow] Weather for {packageFolder}: offline, next time"); yield break; }
            string name;
            try { name = ResortPackage.ReadManifest(packageFolder).Site.Name; }
            catch (Exception) { yield break; }
            RunWeather(packageFolder, name, manual: false, retry: null);
        }

        /// <summary>
        /// The job, on the download card (owner finding F1): opened when the player asked, the pill alone for the automatic try.
        /// <paramref name="retry"/> is the failed card's Retry (the player's own Get weather), or null.
        /// </summary>
        async void RunWeather(string folder, string name, bool manual, Action retry)
        {
            string cache = PipelineDownloader.CacheFolder(DataRoot);
            _weatherJob = folder;
            _weatherText = "Getting weather…";
            var job = BeginAreaJob(name, LibraryViewModel.GetWeather, "Getting weather", manual, retry);
            if (Controller.Screen == FlowScreen.Library) Screens.RenderLibrary(_library = BuildLibrary(), _mode);
            var progress = AreaJobProgress(job, percent =>
            {
                _weatherText = $"Getting weather · {percent}%";
                if (Controller.Screen == FlowScreen.Library) Screens.UpdateWaterAndRoads(folder, _weatherText);
            });
            string toast = null, why = "";
            Exception error = null;
            var ct = job.Cancel.Token;
            try
            {
                // Off the game's threads, on half the cores at most (the calibration is one core; the cap is for its neighbours).
                bool added = await Task.Run(() =>
                {
                    WorkerLimits.LimitThisFlow(WorkerLimits.Background);
                    return WeatherBackfill.RunAsync(folder, cache, progress, ct);
                });
                var w = ResortPackage.ReadManifest(folder).Weather;
                Debug.Log($"[AppFlow] Weather for {name} ({(manual ? "Get weather" : "on open")}): {(added ? "added" : "already there")}, " +
                          $"{w?.Verdict}, label years {w?.FirstLabelYear}-{w?.LastLabelYear}");
                if (added) toast = w != null && w.RowsFile.Length == 0 ? $"{name} is outside the weather records: it plays a typical mountain's weather."
                                 : $"Weather added for {name}.";
            }
            catch (Exception e)
            {
                error = e;
                why = WeatherWhyNot(name, e);
                Debug.LogWarning($"[AppFlow] Weather for {name} ({(manual ? "Get weather" : "on open")}): {e}");
            }
            finally
            {
                _weatherJob = null;
                _weatherText = "";
            }
            if (this == null) return;
            bool cancelled = EndAreaJob(job, error, why);
            if (cancelled) toast = "Get weather cancelled. Nothing was changed.";
            // A failure the player started stays on the card, which says why; the automatic try stays quiet (the next open tries again).
            if (toast != null) Screens.Toast(toast, 5);
            if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        /// <summary>Why Get weather didn't finish, in the player's words; the area is as it was.</summary>
        static string WeatherWhyNot(string name, Exception e)
        {
            if (e is FormatTooNewException) return $"{name} wasn't changed: {e.Message}";
            switch (DownloadErrors.Classify(e))
            {
                case ProblemKind.OfflineMode: return "Offline mode is on, so nothing was downloaded.";
                case ProblemKind.NoConnection: return "No internet connection: try again when you're back online. Nothing was changed.";
                case ProblemKind.ServerDown:
                case ProblemKind.ServiceBusy: return "The weather services aren't answering just now; try again later. Nothing was changed.";
            }
            if (e is IOException || e is UnauthorizedAccessException) return $"{name}'s files couldn't be changed: {e.Message}";
            return $"Get weather stopped: {e.Message}";
        }
    }
}
