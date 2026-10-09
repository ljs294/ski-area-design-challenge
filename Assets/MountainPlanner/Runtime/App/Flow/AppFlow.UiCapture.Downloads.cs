using System.Collections;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.UI.Flow;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -uicapture's task P2-06 states, each a made-up download drawn by the real card, pill and dialogs (nothing is
    /// fetched): resuming, waiting for the connection, stopped; the pill waiting and stopped; the S11 error, offline
    /// and one-at-a-time dialogs; the picker with a download running, and the picker with no connection.
    /// </summary>
    public sealed partial class AppFlow
    {
        static readonly string[] CaptureStages = { "Terrain", "Terrain surroundings", "Forest", "Ground cover", "Tree species", "Water and roads", "Building", "Preparing terrain" };

        static DownloadViewModel CaptureDownload()
        {
            var vm = new DownloadViewModel();
            vm.Start("Crystal Mountain", 2);
            vm.Apply(new DownloadStatus
            {
                Name = "Crystal Mountain", SizeKm = 2, Stages = CaptureStages, StageIndex = 4, StageCount = CaptureStages.Length, Stage = CaptureStages[3],
                Overall = 0.41, Bytes = 182_400_000, BytesPerSecond = 6_100_000, SecondsRemaining = 80,
                Detail = "Ground cover: downloading tile 3 of 6 · 35%",
            });
            return vm;
        }

        IEnumerator DrawDownload(DownloadViewModel vm, bool open)
        {
            Screens.InvalidateDownload();
            Screens.ShowDownloadCard(open, true, false);
            Screens.RenderDownload(vm);
            yield return Wait(0.3f);
        }

        IEnumerator CaptureDownloadStates(string folder)
        {
            if (Wanted("s4-resuming"))
            {
                var vm = new DownloadViewModel();
                vm.Resume("Crystal Mountain", 2, "Ground cover", 0.62);
                vm.Apply(new DownloadStatus
                {
                    Name = "Crystal Mountain", SizeKm = 2, Stages = CaptureStages, StageIndex = 2, StageCount = CaptureStages.Length, Stage = CaptureStages[1],
                    Overall = 0.18, Bytes = 96_000_000, Detail = "Terrain surroundings: downloading sector 4 of 9 · 60%",
                });
                yield return DrawDownload(vm, true);
                yield return EachLook(folder, "s4-resuming");
            }
            if (Wanted("s4-waiting") || Wanted("s4-pill-waiting"))
            {
                var vm = CaptureDownload();
                vm.Waiting(DownloadProblem.Of(ProblemKind.NoConnection), 12);
                yield return DrawDownload(vm, true);
                if (Wanted("s4-waiting")) yield return EachLook(folder, "s4-waiting");
                yield return DrawDownload(vm, false);
                if (Wanted("s4-pill-waiting")) yield return EachLook(folder, "s4-pill-waiting");
            }
            if (Wanted("s4-failed") || Wanted("s4-pill-failed") || Wanted("s11-error"))
            {
                var vm = CaptureDownload();
                var problem = DownloadProblem.Of(ProblemKind.DiskFull, 240_000_000);
                vm.Failed(problem, "There is not enough space on the disk.");
                yield return DrawDownload(vm, true);
                if (Wanted("s4-failed")) yield return EachLook(folder, "s4-failed");
                yield return DrawDownload(vm, false);
                if (Wanted("s4-pill-failed")) yield return EachLook(folder, "s4-pill-failed");
                if (Wanted("s11-error"))
                {
                    Screens.Alert(problem.Title, $"The download of {vm.Name} stopped. {problem.Text}", "Try again", null, "Later", "Settings › Data", null);
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s11-error");
                    Screens.CloseConfirm();
                }
            }
            Screens.ShowDownloadCard(false, false, false);
            if (Wanted("s11-offline"))
            {
                ShowOfflineMode("Choosing a new area needs the internet, for the map, the search and the download.", null);
                yield return Wait(0.3f);
                yield return EachLook(folder, "s11-offline");
                Screens.CloseConfirm();
            }
            if (Wanted("s11-one-at-a-time"))
            {
                Screens.Alert("One download at a time", "Crystal Mountain is downloading (41%). Wait for it to finish, or cancel it, then start Sugarloaf.",
                              "Show the download", null, "Close", error: false);
                yield return Wait(0.3f);
                yield return EachLook(folder, "s11-one-at-a-time");
                Screens.CloseConfirm();
            }
            if (Wanted("s3-picker-busy") && Picker != null)
            {
                var vm = CaptureDownload();
                Controller.NewResort();
                yield return Wait(0.5f);
                Picker.PlaceAt(new MountainPlanner.Domain.Geo.GeoPoint(45.05, -70.31));
                Picker.SetPill(vm.Pill, vm.Fraction, "");
                Picker.SetBusy("Crystal Mountain is downloading. One download at a time: wait for it, or cancel it.");
                yield return Wait(6);
                yield return EachLook(folder, "s3-picker-busy");
                Picker.SetPill(null, 0, "");
                Picker.SetBusy("");
                Controller.PickerCancelled();
            }
            if (Wanted("s3-picker-offline") && Picker != null)
            {
                bool was = Http.NetworkDisabled;
                Http.NetworkDisabled = true;   // the cable out, as the map sees it (offline mode itself never opens the picker)
                Picker.Services = null;
                Controller.NewResort();
                yield return Wait(0.5f);
                Picker.PlaceAt(new MountainPlanner.Domain.Geo.GeoPoint(43.59, -110.83));
                yield return Wait(4);
                yield return EachLook(folder, "s3-picker-offline");
                Controller.PickerCancelled();
                Http.NetworkDisabled = was;
                Picker.Services = null;
                OfflineState.ReportConnection(true);
            }
        }
    }
}
