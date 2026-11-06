using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Get water and roads in Manage Areas (task P3-16, BB6): OpenStreetMap water, developed land and roads for an area
    /// downloaded without them, as a background job (<see cref="OsmBackfill"/>). One at a time; while it runs the area can't
    /// be opened, renamed or deleted, and the library can't move or its download cache be cleared. The area's id changes,
    /// and its saves and recent entry move with it.
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>The area Get water and roads is running on, or null.</summary>
        string _waterAndRoads;
        string _waterAndRoadsText = "";

        bool WaterAndRoadsBusy(string folder)
        {
            if (_waterAndRoads == null || !SameFolder(folder, _waterAndRoads)) return false;
            Screens.Toast("Getting its water and roads; it opens again when that's done.");
            return true;
        }

        LibraryViewModel MarkWaterAndRoads(LibraryViewModel vm)
        {
            if (_waterAndRoads == null) return vm;
            foreach (var row in vm.Rows)
                if (row.Entry != null && SameFolder(row.Entry.Folder, _waterAndRoads))
                {
                    row.Busy = true;
                    row.WaterAndRoadsText = _waterAndRoadsText;
                }
            return vm;
        }

        async void GetWaterAndRoads(LibraryRow r)
        {
            if (r?.Entry == null || !r.NeedsWaterAndRoads) return;
            if (_waterAndRoads != null) { Screens.Toast("Get water and roads is already running for another area; one at a time."); return; }
            if (Downloads.Active) { Screens.Toast("Wait for the download to finish, or cancel it, first.", 5); return; }
            if (OfflineState.Mode == OfflineMode.Setting)
            {
                ShowOfflineMode($"Getting water and roads for {r.Name} needs the internet.", () => GetWaterAndRoads(r));
                return;
            }
            // The mountain behind the title holds its cache open: let go of it first, as Delete does.
            bool backdrop = _viewer != null && MountainViewer.TitleMode && SameFolder(_viewer.OpenPackage ?? MountainViewer.RequestedPackage, r.Entry.Folder);
            if (backdrop && (_viewer.OpenPackage == null || !_viewer.ReleaseCache()))
            {
                Screens.Toast($"{r.Name} is still opening behind the menu; try again in a moment.");
                return;
            }

            string folder = r.Entry.Folder, name = r.Name, root = DataRoot;
            _waterAndRoads = folder;
            _waterAndRoadsText = "Getting water and roads…";
            if (Controller.Screen == FlowScreen.Library) Screens.RenderLibrary(_library = BuildLibrary(), _mode);
            var progress = new Progress<AcquisitionProgress>(p =>
            {
                _waterAndRoadsText = $"Getting water and roads · {(int)Math.Floor(Math.Max(0, Math.Min(1, p.Overall)) * 100)}%";
                if (this != null && Controller.Screen == FlowScreen.Library) Screens.UpdateWaterAndRoads(folder, _waterAndRoadsText);
            });
            string toast;
            bool done = false;
            try
            {
                string id = await Task.Run(() => OsmBackfill.RunAsync(root, folder, PipelineDownloader.CacheFolder(root), progress, CancellationToken.None));
                Debug.Log($"[AppFlow] Got water and roads for {name}: {r.Entry.PackageId} -> {id}");
                toast = $"{name} now has its water and roads. Its terrain is prepared again the next time it opens.";
                done = true;
            }
            catch (Exception e)
            {
                toast = WhyNot(name, e);
                Debug.LogWarning($"[AppFlow] Get water and roads for {name}: {e}");
            }
            finally
            {
                _waterAndRoads = null;
                _waterAndRoadsText = "";
            }
            if (this == null) return;
            Screens.Toast(toast, done ? 5 : 8);
            if (backdrop) ReturnToTitle(FlowScreen.Library);   // the title reloads the area, now with its water and roads
            else if (Controller.Screen == FlowScreen.Library) RefreshLibrary();
        }

        /// <summary>Why Get water and roads didn't finish, in the player's words; the area is as it was.</summary>
        static string WhyNot(string name, Exception e)
        {
            if (e is AreaRefusedException || e is FormatTooNewException) return $"{name} wasn't changed: {e.Message}";
            if (e is OsmUnavailableException) return "OpenStreetMap is still unavailable; try again later. Nothing was changed.";
            switch (DownloadErrors.Classify(e))
            {
                case ProblemKind.OfflineMode: return "Offline mode is on, so nothing was downloaded.";
                case ProblemKind.NoConnection: return "No internet connection: try again when you're back online. Nothing was changed.";
                case ProblemKind.ServerDown:
                case ProblemKind.ServiceBusy: return "OpenStreetMap isn't answering just now; try again later. Nothing was changed.";
            }
            if (e is IOException || e is UnauthorizedAccessException) return $"{name}'s files couldn't be changed: {e.Message}";
            return $"Get water and roads stopped: {e.Message}";
        }
    }
}
