using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Settings › Data (task P2-05): the library folder (moved at the title, never during a download, and fixed by
    /// -data for the run), the disk the library uses with Free space, and offline mode.
    /// </summary>
    public sealed partial class AppFlow
    {
        bool _measuringDisk, _libraryMoved;

        /// <summary>True when -data chose the library for this run (captures and tests): Settings shows it but can't move it.</summary>
        static bool DataFromCommandLine => Array.IndexOf(Environment.GetCommandLineArgs(), "-data") >= 0;

        void WireSettings()
        {
            // Through FlowScreens' own events: the window itself is built when the screens wake, which may be after this.
            Screens.SettingsLibraryFolderChosen += ChangeLibraryFolder;
            Screens.SettingsDataShown += ShowDataPage;
            Screens.SettingsFreeSpaceConfirmed += FreeSpace;   // which redraws the Data page when it's done
            DataPreferences.OfflineChanged += ApplyOffline;
            Screens.SettingsClosed += () =>
            {
                if (!_libraryMoved) return;
                _libraryMoved = false;
                if (Controller.Screen == FlowScreen.Title && MountainViewer.TitleMode) ShowScreen(FlowScreen.Title);
            };
        }

        void ReleaseSettings() => DataPreferences.OfflineChanged -= ApplyOffline;

        /// <summary>Offline mode: every network call fails at once, as with -offline (which stays on for its run).</summary>
        void ApplyOffline()
        {
            Http.NetworkDisabled = DataPreferences.OfflineNow || Array.IndexOf(Environment.GetCommandLineArgs(), "-offline") >= 0;
            if (Picker != null && Picker.IsOpen) Screens.Toast(Http.NetworkDisabled ? "Offline: no map tiles or downloads" : "Back online");
        }

        void OpenDataFolder()
        {
            Directory.CreateDirectory(DataRoot);
            Application.OpenURL(new Uri(DataRoot).AbsoluteUri);
        }

        /// <summary>Why the folder can't move now, or null when it can.</summary>
        string LibraryLocked() =>
            DataFromCommandLine ? "This run's library was chosen with -data."
            : Downloads.Running ? "Wait for the download to finish, or cancel it."
            : !MountainViewer.TitleMode ? "Return to the title to move the library."
            : null;

        void ShowDataPage()
        {
            string why = LibraryLocked();
            Screens.Settings.ShowLibrary(DataRoot, why == null, why ?? "");
            MeasureDisk();
        }

        /// <summary>Measures the library off the main thread, then shows it on the Data page.</summary>
        async void MeasureDisk()
        {
            if (_measuringDisk) return;
            _measuringDisk = true;
            string root = DataRoot;
            long areas = 0, caches = 0, older = 0, downloads = 0;
            int count = 0;
            try
            {
                await Task.Run(() =>
                {
                    var entries = ResortLibrary.Scan(root, measure: true);
                    count = entries.Count;
                    foreach (var e in entries.Where(e => e.Measured))
                    {
                        areas += e.Disk.Package;
                        caches += e.Disk.Cache + e.Disk.NewerCaches;
                        older += e.Disk.OlderCaches;
                    }
                    older += ResortLibrary.LeftoverBytes(root);
                    string cache = PipelineDownloader.CacheFolder(root);
                    if (Directory.Exists(cache)) downloads = new DirectoryInfo(cache).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[AppFlow] Disk use: {e.Message}");
            }
            finally { _measuringDisk = false; }
            if (this == null || root != DataRoot) return;
            string text = $"{count} {(count == 1 ? "area" : "areas")}: {LibraryViewModel.Disk(areas)}, terrain caches {LibraryViewModel.Disk(caches)}" +
                          (older > 0 ? $", older versions' caches {LibraryViewModel.Disk(older)}" : "") +
                          (downloads > 0 ? $". Download and map cache {LibraryViewModel.Disk(downloads)}." : ".");
            Screens.Settings.ShowDisk(text, older);
        }

        /// <summary>
        /// Moves the library to a new folder (Settings › Data): remembered, and in use at once at the title. The
        /// areas in the old folder stay there.
        /// </summary>
        void ChangeLibraryFolder(string folder)
        {
            string why = LibraryLocked();
            if (why != null) { Screens.Toast(why, 5); return; }
            string full;
            try
            {
                full = Path.GetFullPath(folder);
                Directory.CreateDirectory(full);
                string probe = Path.Combine(full, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is NotSupportedException)
            {
                Screens.Toast($"Can't use that folder: {e.Message}", 6);
                return;
            }
            DataPreferences.SetLibraryFolder(SameFolder(full, MountainViewer.DataRoot) ? "" : full);
            UseLibrary(full);
            Screens.Toast("Library folder changed");
            ShowDataPage();
        }

        /// <summary>Points the flow, its downloads and the picker's tile cache at another library folder.</summary>
        void UseLibrary(string folder)
        {
            DataRoot = folder;
            Downloads.Retarget(folder, new PipelineDownloader(folder));
            if (Picker != null) Picker.Services = null;   // the tile cache lives in the library; made again on next use
            _sizes.Clear();
            _libraryMoved = true;   // the title's Continue sign is redrawn when Settings closes (redrawing now would take its focus)
        }
    }
}
