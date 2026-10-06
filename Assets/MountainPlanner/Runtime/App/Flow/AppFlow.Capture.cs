using System;
using System.Collections;
using System.IO;
using MountainPlanner.Domain.Geo;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -flowcapture &lt;folder&gt;: walks the whole flow unattended and saves a picture of each screen, for review
    /// without a person at the keyboard: title, Load Area, Manage Areas, Credits, the site picker, the download card, the pill, the
    /// quality card after a real download (Crystal Mountain 2 km, 3DEP fallback terrain), then the opened mountain.
    /// Use it with -data on a scratch library.
    /// </summary>
    public sealed partial class AppFlow
    {
        void StartCaptureIfAsked()
        {
            string[] args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "-flowcapture");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(Capture(Path.GetFullPath(args[i + 1])));
            StartUiCaptureIfAsked(args);
        }

        IEnumerator Capture(string folder)
        {
            Directory.CreateDirectory(folder);
            yield return WaitForMountain(300);
            yield return Wait(4);   // ground cover and trees paint in
            yield return Shot(folder, "s1-title");

            _mode = UI.Flow.LibraryMode.Load;
            Controller.MyResorts();
            yield return Wait(0.5f);
            yield return Shot(folder, "s2-load-area");
            Controller.Escape();
            _mode = UI.Flow.LibraryMode.Manage;
            Controller.MyResorts();
            yield return Wait(0.5f);
            yield return Shot(folder, "s2-manage-areas");
            Controller.Escape();
            Screens.ShowCredits(Credits());
            yield return Wait(0.5f);
            yield return Shot(folder, "s9-credits");
            Screens.CloseOverlay();

            var crystal = new GeoPoint(46.93, -121.49);
            Controller.NewResort();
            yield return Wait(0.5f);
            Picker?.PlaceAt(crystal);
            yield return Wait(6);   // map tiles, coverage and the estimate come in
            yield return Shot(folder, "s3-picker");

            OnSiteChosen(PickedSite.Create("Crystal Mountain", Albers6350.Forward(crystal), 2, false, default));
            yield return Wait(6);
            yield return Shot(folder, "s4-download");
            Controller.MinimiseDownload();
            Controller.MyResorts();
            yield return Wait(1);
            yield return Shot(folder, "s4-pill-and-library");

            float until = Time.realtimeSinceStartup + 1800;
            while (Controller.Screen != FlowScreen.Quality && Time.realtimeSinceStartup < until)
            {
                if (Downloads.View.Phase == UI.Flow.DownloadPhase.Failed)
                {
                    Controller.RestoreDownload();
                    yield return Wait(0.5f);
                    yield return Shot(folder, "s4-failed");
                    Debug.LogError("[AppFlow] capture: the download failed: " + Downloads.View.Error);
                    Quit();
                    yield break;
                }
                yield return null;
            }
            yield return Wait(0.5f);
            yield return Shot(folder, "s5-quality");

            Controller.QualityOpen();
            yield return Wait(1);
            yield return WaitForMountain(600);
            yield return Wait(4);
            yield return Shot(folder, "s6-opened");
            Debug.Log("[AppFlow] capture finished: " + folder);
            Quit();
        }

        IEnumerator WaitForMountain(float seconds)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < until)
            {
                var viewer = FindAnyObjectByType<MountainViewer>();
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null) yield break;
                yield return null;
            }
            Debug.LogWarning("[AppFlow] capture: no mountain on screen yet; capturing anyway");
        }

        static IEnumerator Wait(float seconds) => new WaitForSecondsRealtime(seconds);

        static IEnumerator Shot(string folder, string name)
        {
            yield return new WaitForEndOfFrame();
            string path = Path.Combine(folder, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log("[AppFlow] capture: " + path);
        }
    }
}
