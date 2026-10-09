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
            int movie = Array.IndexOf(args, "-titlemovie"), shots = Array.IndexOf(args, "-titleshots");
            if (movie >= 0 && movie + 1 < args.Length) StartCoroutine(TitleMovie(Path.GetFullPath(args[movie + 1]), stills: false));
            else if (shots >= 0 && shots + 1 < args.Length) StartCoroutine(TitleMovie(Path.GetFullPath(args[shots + 1]), stills: true));
            StartUiCaptureIfAsked(args);
        }

        /// <summary>
        /// The title's drift for review (task P2-03). -titlemovie &lt;folder&gt;: one whole loop at 15 fps as JPEG frames in
        /// &lt;folder&gt;/title (PickerLabSetup.EncodeMovies turns them into an MP4), on the drift's own clock so no frame is
        /// skipped. -titleshots &lt;folder&gt;: a still at each of the drift's views. Then it quits.
        /// </summary>
        IEnumerator TitleMovie(string folder, bool stills)
        {
            const int fps = 15;
            string frames = Path.Combine(folder, "title");
            Directory.CreateDirectory(stills ? folder : frames);
            if (stills)
            {
                yield return Wait(2);   // the cover, part way through opening
                yield return Shot(folder, "title-cover");
            }
            // The movie starts at launch: the cover while the mountain opens and its fade, at real speed.
            float t0 = Time.realtimeSinceStartup;
            int ms = 0;
            while (!stills && (Screens.CoverUp || _viewer == null || !_viewer.Ready || Time.realtimeSinceStartup - _coverLifted < UI.Flow.FlowScreens.CoverFadeSeconds + 0.5f))
            {
                if (Time.realtimeSinceStartup - t0 > 120) break;
                yield return new WaitForEndOfFrame();
                ms = (int)((Time.realtimeSinceStartup - t0) * 1000);
                var intro = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(frames, $"f_{ms:D6}.jpg"), intro.EncodeToJPG(85));
                Destroy(intro);
                yield return new WaitForSecondsRealtime(1f / fps);
            }
            while (_viewer == null || !_viewer.Ready || _drift == null) yield return null;
            _driftHeld = true;
            if (stills)
            {
                _driftSeconds = 0;
                for (int i = 0; i < 90; i++) yield return null;   // LODs and shadows settle
                for (int v = 0; v < _drift.Shots; v++)
                {
                    _driftSeconds = v * TitleDrift.SecondsPerShot;
                    for (int i = 0; i < 60; i++) yield return null;
                    yield return Shot(folder, $"title-view{v + 1}");
                }
            }
            else
            {
                // Then one whole loop on the drift's own clock, so no frame is skipped.
                float from = _driftSeconds;
                int count = Mathf.CeilToInt(_drift.LoopSeconds * fps);
                for (int k = 1; k <= count; k++)
                {
                    _driftSeconds = from + k / (float)fps;
                    yield return null;   // Update puts the camera there
                    yield return new WaitForEndOfFrame();
                    var shot = ScreenCapture.CaptureScreenshotAsTexture();
                    File.WriteAllBytes(Path.Combine(frames, $"f_{ms + k * 1000 / fps:D6}.jpg"), shot.EncodeToJPG(85));
                    Destroy(shot);
                }
                Debug.Log($"[AppFlow] title movie: cover {ms / 1000f:F1} s, then {count} drift frames, in {frames}");
            }
            Quit();
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
                if (viewer != null && viewer.Camera != null && viewer.Camera.Surface != null && !Screens.CoverUp) yield break;   // and the cover is gone
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
