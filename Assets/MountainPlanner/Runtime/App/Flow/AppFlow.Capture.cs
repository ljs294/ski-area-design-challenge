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
            int hold = Array.IndexOf(args, "-titlehold");
            if (hold >= 0 && hold + 1 < args.Length) StartCoroutine(TitleHold(Path.GetFullPath(args[hold + 1])));
            int still = Array.IndexOf(args, "-titlestill");
            if (still >= 0 && still + 1 < args.Length) StartCoroutine(TitleStill(Path.GetFullPath(args[still + 1])));
            StartUiCaptureIfAsked(args);
            StartDisplayCheckIfAsked(args);
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

        /// <summary>
        /// -titlehold &lt;folder&gt; (task P3-01, the tree flicker of P2-12 finding 6): the title held still at its first
        /// view, then 60 frames in a row as PNG (no JPEG noise), so anything that changes between frames with the camera
        /// still (a LOD or impostor switch, a shadow, the wind) shows in a frame-to-frame difference. Then it quits.
        /// </summary>
        IEnumerator TitleHold(string folder)
        {
            Directory.CreateDirectory(folder);
            while (_viewer == null || !_viewer.Ready || _drift == null || Screens.CoverUp) yield return null;
            _driftHeld = true;
            _driftSeconds = 0;
            for (int i = 0; i < 180; i++) yield return null;   // LODs, shadows and ground cover settle
            for (int k = 0; k < 60; k++)
            {
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(folder, $"hold_{k:D2}.png"), shot.EncodeToPNG());
                Destroy(shot);
                yield return null;
            }
            Debug.Log($"[AppFlow] title hold: 60 frames in {folder}");
            Quit();
        }

        /// <summary>The loading screen's picture: 32:9, so scaled to the screen's height and centre-cropped it frames as the live title does at 60° on 16:9 to 32:9 screens.</summary>
        public const int TitleStillWidth = 7680, TitleStillHeight = 2160;

        /// <summary>
        /// -titlestill &lt;file.jpg&gt; (task P2-03.1): the loading screen's picture, Art/UI/Flow/Resources/MountainPlannerFlow/
        /// TitleStill.jpg. The title's own view at its first drift view, the scene alone (no UI), rendered off screen at
        /// <see cref="TitleStillWidth"/>×<see cref="TitleStillHeight"/>, JPEG quality 92. Then it quits. Re-capture it whenever
        /// the title's look changes (demo.bat 57), or the hand-off from picture to live view jumps.
        /// </summary>
        IEnumerator TitleStill(string file)
        {
            while (_viewer == null || !_viewer.Ready || _drift == null) yield return null;
            _driftHeld = true;
            _driftSeconds = 0;
            while (Screens.CoverUp) yield return null;   // the camera draws again once the loading screen lifts
            for (int i = 0; i < 120; i++) yield return null;   // LODs and shadows settle
            var cam = _viewer.Camera.GetComponent<UnityEngine.Camera>();
            var rt = new RenderTexture(TitleStillWidth, TitleStillHeight, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            cam.targetTexture = rt;
            for (int f = 0; f < 4; f++) yield return null;
            yield return new WaitForEndOfFrame();
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(TitleStillWidth, TitleStillHeight, TextureFormat.RGB24, false, false);
            tex.ReadPixels(new Rect(0, 0, TitleStillWidth, TitleStillHeight), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = was;
            cam.targetTexture = null;
            rt.Release();
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllBytes(file, tex.EncodeToJPG(92));
            Destroy(tex);
            Debug.Log($"[AppFlow] Title still saved to {file} ({TitleStillWidth}×{TitleStillHeight})");
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
