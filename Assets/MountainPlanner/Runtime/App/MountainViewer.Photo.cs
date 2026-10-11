using System;
using System.IO;
using System.Threading.Tasks;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Photo;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// S10 photo mode (G3; task P2-07; the accepted mockup's #demo=p2,photo). P (or the menu) swaps the HUD for the thin
    /// photo bar:
    /// <list type="bullet">
    /// <item><b>Time</b> moves the sun (the clock stops while you frame); <b>Date</b> its path through the year;</item>
    /// <item><b>Lens</b> is the vertical field of view (the camera stops following the setting, P2-05's hook);</item>
    /// <item><b>Focus</b> is depth of field: Off, Natural or Miniature (<see cref="PhotoFocus"/>), focused on the ground
    /// in the middle of the picture;</item>
    /// <item><b>Grade</b> is the look (<see cref="LookStyle"/>), and <b>Size</b> saves at the screen's resolution or 2×.</item>
    /// </list>
    /// Space or F12 saves a PNG to Pictures\Ski Area Design Challenge (<see cref="PhotoCapture"/>); H hides the bar; Esc
    /// or P leaves, and the clock, time, grade, lens and info layers go back as they were (owner, 2026-10-08). The bar
    /// remembers Focus and Size for next time.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>The grades in the bar's order: Bluebird (the owner's), Natural (the game before the beauty pass), Soft, Postcard.</summary>
        static readonly int[] GradeOrder = { LookStyle.Default, 0, 2, 3 };
        static readonly string[] GradeNames = { "Bluebird", "Natural", "Soft", "Postcard" };

        bool _photo;
        PhotoBar _photoBar;
        PhotoFocus _focus;
        PhotoFocusMode _photoFocus;
        int _photoScale = 1, _photoGrade, _photoLens;
        bool _photoBarHidden;
        Task<Vector2Int> _saving;
        float _focusTarget;

        // What photo mode puts back on the way out.
        ViewTime _backTime;
        bool _backPaused, _backFollowFov;
        float _backFov;
        int _backStyle;
        string _backInfo;
        bool _backContours;

        /// <summary>Photo mode is open (tests).</summary>
        internal bool PhotoOpen => _photo;
        internal PhotoBar PhotoBarView => _photoBar;
        internal PhotoFocus Focus => _focus;

        void WirePhoto()
        {
            if (Hud == null || Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            _focus = new PhotoFocus(cam);
            _photoBar = PhotoBar.Create(transform, Hud.Document.panelSettings);
            _photoBar.TimeChosen += second => SetPhotoTime(second);
            _photoBar.DayStepped += StepPhotoDay;
            _photoBar.LensChosen += SetPhotoLens;
            _photoBar.FocusChosen += SetPhotoFocus;
            _photoBar.GradeStepped += d => SetPhotoGrade(_photoGrade + d);
            _photoBar.SizeChosen += scale => { _photoScale = scale; ShowPhotoBar(); };
            _photoBar.HideChosen += () => HidePhotoBar(true);
            _photoBar.CaptureChosen += CapturePhoto;
            _photoBar.ExitChosen += () => SetPhoto(false);
            Hud.PhotoChosen += () => SetPhoto(true);
        }

        void ReleasePhoto()
        {
            _focus?.Dispose();
            _focus = null;
        }

        /// <summary>Opens or leaves photo mode.</summary>
        internal void SetPhoto(bool on)
        {
            if (on == _photo || (on && (_resort == null || TitleMode || _photoBar == null))) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            if (on)
            {
                _backTime = Lighting != null ? Lighting.Clock.Now : default;
                _backPaused = _clock == null || _clock.Paused;
                _backStyle = Lighting != null ? Mathf.Max(0, LookStyle.IndexOf(Lighting.Style.Name)) : LookStyle.Default;
                _backFollowFov = Camera.FollowFovSetting;
                _backFov = cam.fieldOfView;
                _backInfo = _layers.InfoLayerId;
                _backContours = _layers.ContoursOn;
                // Info layers are display only, never in a photo; the clock stops so the light holds while you frame.
                if (_backInfo != null) _layers.Set(_backInfo, false);
                if (_backContours) _layers.Set(MapLayers.Contours, false);
                _clock?.SetPaused(true);
                Camera.FollowFovSetting = false;
                _photoLens = Mathf.RoundToInt(cam.fieldOfView);
                _photoGrade = Mathf.Max(0, Array.IndexOf(GradeOrder, _backStyle));
                _photoBarHidden = false;
                _photo = true;
                _focus?.SetMode(_photoFocus);
                _focusTarget = 0;
                ShowPhotoBar();
                _photoBar.SetVisible(true);
                _photoBar.SetBarHidden(false);
            }
            else
            {
                _photo = false;
                _photoBar.SetVisible(false);
                _focus?.SetMode(PhotoFocusMode.Off);
                if (Lighting != null)
                {
                    Lighting.SetTime(_backTime);
                    Lighting.SetStyle(_backStyle);
                }
                _clock?.Adopt(_backTime);
                _clock?.SetPaused(_backPaused);
                cam.fieldOfView = _backFov;
                Camera.FollowFovSetting = _backFollowFov;
                if (_backInfo != null) _layers.Set(_backInfo, true);
                if (_backContours) _layers.Set(MapLayers.Contours, true);
            }
        }

        /// <summary>
        /// Set by the app flow: shows a save error in its S11 dialog (FlowScreens.Alert, task P2-06). Without the flow (a
        /// viewer run on its own) the photo bar's toast says it.
        /// </summary>
        public static Action<string, string> PhotoErrorShown;

        void PhotoFailed(string what)
        {
            if (PhotoErrorShown != null) PhotoErrorShown("The photo wasn't saved", what);
            else _photoBar?.Toast("The photo wasn't saved. " + what, error: true);
        }

        /// <summary>For UI captures: the bar's Focus, time (null keeps it), grade (the bar's order) and Size.</summary>
        internal void SetPhotoForCapture(PhotoFocusMode focus, int? second, int grade, int scale)
        {
            SetPhotoFocus(focus);
            if (second.HasValue) SetPhotoTime(second.Value);
            SetPhotoGrade(grade);
            _photoScale = scale;
            ShowPhotoBar();
        }

        void HidePhotoBar(bool hidden)
        {
            _photoBarHidden = hidden;
            _photoBar.SetBarHidden(hidden);
        }

        /// <summary>Photo mode's keys (rebindable; Esc is the viewer's). The camera's own keys work as in the view.</summary>
        void HandlePhotoKeys(Keyboard keys, bool letters)
        {
            if (KeyBindings.Pressed(keys, GameAction.PhotoMode, letters)) { SetPhoto(false); return; }
            if (KeyBindings.Pressed(keys, GameAction.PhotoCapture, letters)) CapturePhoto();
            if (KeyBindings.Pressed(keys, GameAction.PhotoHideBar, letters)) HidePhotoBar(!_photoBarHidden);
            if (Lighting == null) return;
            int second = Lighting.Clock.Now.SecondOfDay;
            if (KeyBindings.Pressed(keys, GameAction.PhotoEarlier, letters)) SetPhotoTime(second - 15 * 60);
            if (KeyBindings.Pressed(keys, GameAction.PhotoLater, letters)) SetPhotoTime(second + 15 * 60);
            if (KeyBindings.Pressed(keys, GameAction.PhotoDayBack, letters)) StepPhotoDay(-1);
            if (KeyBindings.Pressed(keys, GameAction.PhotoDayOn, letters)) StepPhotoDay(1);
            if (KeyBindings.Pressed(keys, GameAction.FreeFly, letters) && Camera != null) Camera.ToggleMode();
            if (KeyBindings.Pressed(keys, GameAction.ResetView, letters)) HomeView();
        }

        /// <summary>The time of day, wrapping round midnight (the day stays).</summary>
        void SetPhotoTime(int second)
        {
            if (Lighting == null) return;
            second = ((second % ViewTime.SecondsPerDay) + ViewTime.SecondsPerDay) % ViewTime.SecondsPerDay;
            Lighting.SetTime(Lighting.Clock.Now.WithSecondOfDay(second));
            ShowPhotoBar();
        }

        /// <summary>A day back or on, wrapping round the year (the year stays: the sun's path repeats).</summary>
        void StepPhotoDay(int days)
        {
            if (Lighting == null) return;
            var now = Lighting.Clock.Now;
            int n = ViewTime.DaysInYear(now.Year);
            int day = ((now.DayOfYear - 1 + days) % n + n) % n + 1;
            Lighting.SetTime(new ViewTime(now.Year, day, now.SecondOfDay));
            ShowPhotoBar();
        }

        void SetPhotoLens(int degrees)
        {
            _photoLens = Mathf.Clamp(degrees, 20, 90);
            if (Camera != null) Camera.GetComponent<UnityEngine.Camera>().fieldOfView = _photoLens;
            ShowPhotoBar();
        }

        void SetPhotoFocus(PhotoFocusMode mode)
        {
            _photoFocus = mode;
            _focus?.SetMode(mode);
            ShowPhotoBar();
        }

        void SetPhotoGrade(int index)
        {
            _photoGrade = Mathf.Clamp(index, 0, GradeOrder.Length - 1);
            Lighting?.SetStyle(GradeOrder[_photoGrade]);
            ShowPhotoBar();
        }

        static readonly string[] DateText = new string[2 * 367];

        /// <summary>"Jan 15": made once per day of the year and kept.</summary>
        static string PhotoDate(ViewTime t)
        {
            int i = (ViewTime.IsLeapYear(t.Year) ? 367 : 0) + t.DayOfYear;
            if (DateText[i] != null) return DateText[i];
            var d = new DateTime(t.Year, 1, 1).AddDays(t.DayOfYear - 1);
            return DateText[i] = d.ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
        }

        int _sizeShownW, _sizeShownH;
        string _sizeScreen = "", _sizeDouble = "";

        /// <summary>The bar shows what the view now is.</summary>
        void ShowPhotoBar()
        {
            if (_photoBar == null) return;
            if (Lighting != null)
            {
                var now = Lighting.Clock.Now;
                _photoBar.SetTime(now.SecondOfDay);
                _photoBar.SetDate(PhotoDate(now));
            }
            _photoBar.SetLens(_photoLens);
            _photoBar.SetFocus(_photoFocus, _focus != null && _focus.Available);
            _photoBar.SetGrade(GradeNames[_photoGrade], _photoGrade > 0, _photoGrade < GradeOrder.Length - 1);
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (cam != null && (cam.pixelWidth != _sizeShownW || cam.pixelHeight != _sizeShownH))
            {
                _sizeShownW = cam.pixelWidth;
                _sizeShownH = cam.pixelHeight;
                var one = PhotoCapture.Size(_sizeShownW, _sizeShownH, 1);
                var two = PhotoCapture.Size(_sizeShownW, _sizeShownH, 2);
                _sizeScreen = $"Saves at {one.x} × {one.y}";
                _sizeDouble = $"Saves at {two.x} × {two.y}";
            }
            _photoBar.SetSize(_photoScale, _sizeScreen, _sizeDouble);
        }

        /// <summary>Each frame in photo mode: the focus follows the ground in the middle of the picture, and the bar follows the view.</summary>
        void UpdatePhoto()
        {
            if (!_photo || Camera == null) return;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            if (_focus != null && _focus.Mode != PhotoFocusMode.Off)
            {
                var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
                float hit = Camera.GroundAlong(ray, 30000);
                float target = float.IsNaN(hit) ? Vector3.Distance(cam.transform.position, Camera.Target) : hit;
                // Ease toward it, so the focus doesn't jump as the middle of the picture crosses a ridge.
                _focusTarget = _focusTarget <= 0 ? target : Mathf.Lerp(_focusTarget, target, 1f - Mathf.Exp(-6f * Time.unscaledDeltaTime));
                _focus.FocusDistance = _focusTarget;
            }
            ShowPhotoBar();
        }

        /// <summary>Space, F12 or the bar's Capture: one photo at a time.</summary>
        void CapturePhoto()
        {
            if (!_photo || _resort == null || Camera == null || (_saving != null && !_saving.IsCompleted)) return;
            var now = Lighting != null ? Lighting.Clock.Now : new ViewTime(SceneLighting.DefaultYear, SceneLighting.DefaultDay, 12 * 3600);
            var date = new DateTime(now.Year, 1, 1).AddDays(now.DayOfYear - 1);
            string path;
            try
            {
                Directory.CreateDirectory(PhotoFolder);
                path = PhotoCapture.FileName(PhotoFolder, ResortLibrary.DisplayName(_resort.PackageFolder, _resort.Manifest), now.Year, date.Month, date.Day, now.SecondOfDay);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                PhotoFailed("Pictures\Ski Area Design Challenge can't be written to. Check that the folder isn't read-only and the disk isn't full, then capture again.");
                Debug.LogWarning($"[MountainViewer] Photo folder: {e.Message}");
                return;
            }
            _photoBar.Flash();
            int scale = _photoScale;
            float before = Time.realtimeSinceStartup;
            _saving = PhotoCapture.Save(Camera.GetComponent<UnityEngine.Camera>(), scale, path);
            StartCoroutine(AfterSave(_saving, path, scale, before));
        }

        /// <summary>
        /// The last save's frames, in milliseconds (the 2× hitch check; -photoshot logs them): the frame that rendered the
        /// photo, and the longest of the frames after it while the readback, encode and write went on.
        /// </summary>
        internal float LastSaveCaptureFrameMs { get; private set; }
        internal float LastSaveLongestOtherFrameMs { get; private set; }
        /// <summary>How many frames of the last save went over 16.7 ms (60 fps).</summary>
        internal int LastSaveSlowFrames { get; private set; }

        System.Collections.IEnumerator AfterSave(Task<Vector2Int> saving, string path, int scale, float started)
        {
            float capture = 0, longest = 0;
            int frames = 0, slow = 0, longestAt = -1;
            while (!saving.IsCompleted)
            {
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000f;
                if (frames == 0) capture = ms;
                else if (ms > longest) { longest = ms; longestAt = frames; }
                if (ms > 16.7f) slow++;
                frames++;
            }
            LastSaveCaptureFrameMs = capture;
            LastSaveLongestOtherFrameMs = longest;
            LastSaveSlowFrames = slow;
            if (saving.IsFaulted)
            {
                var e = saving.Exception?.GetBaseException();
                Debug.LogWarning($"[MountainViewer] Photo not saved: {e?.Message}");
                PhotoFailed(e is IOException || e is UnauthorizedAccessException
                    ? "The file couldn't be written to Pictures\Ski Area Design Challenge. Check that the disk isn't full, then capture again."
                    : "The picture couldn't be read back from the graphics card. Capture again; at Screen size it needs less memory than 2×.");
                yield break;
            }
            var size = saving.Result;
            Debug.Log($"[MountainViewer] Photo saved to {path}: {size.x}×{size.y} ({scale}×) in {(Time.realtimeSinceStartup - started) * 1000f:F0} ms over {frames} frames; the capture frame {capture:F1} ms, the longest after it {longest:F1} ms (frame {longestAt}); {slow} over 16.7 ms");
            if (_photoBar != null) _photoBar.Toast($"Saved {Path.GetFileName(path)} · {size.x} × {size.y} · Pictures › Ski Area Design Challenge");
        }

        /// <summary>
        /// Unattended photo-mode runs: -photo [off|natural|miniature] opens photo mode once the area is in, with that
        /// focus; -photograde, -photolens and -photoscale set the bar; -photoshot &lt;n&gt; saves n photos a second apart and
        /// logs each save's longest frame, then quits (the 2× hitch check).
        /// </summary>
        System.Collections.IEnumerator RunPhotoArguments(string[] args)
        {
            int photo = Array.IndexOf(args, "-photo");
            if (photo < 0) yield break;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            for (int i = 0; i < 60; i++) yield return null;
            string mode = photo + 1 < args.Length ? args[photo + 1].ToLowerInvariant() : "off";
            _photoFocus = mode.StartsWith("nat") ? PhotoFocusMode.Natural : mode.StartsWith("min") ? PhotoFocusMode.Miniature : PhotoFocusMode.Off;
            int scale = Array.IndexOf(args, "-photoscale");
            if (scale >= 0 && scale + 1 < args.Length && int.TryParse(args[scale + 1], out int s)) _photoScale = Mathf.Clamp(s, 1, 2);
            SetPhoto(true);
            int grade = Array.IndexOf(args, "-photograde");
            if (grade >= 0 && grade + 1 < args.Length) SetPhotoGrade(Array.FindIndex(GradeNames, n => string.Equals(n, args[grade + 1], StringComparison.OrdinalIgnoreCase)));
            int lens = Array.IndexOf(args, "-photolens");
            if (lens >= 0 && lens + 1 < args.Length && int.TryParse(args[lens + 1], out int l)) SetPhotoLens(l);
            if (Array.IndexOf(args, "-photohidebar") >= 0) HidePhotoBar(true);
            int shot = Array.IndexOf(args, "-photoshot");
            if (shot < 0) yield break;
            int count = shot + 1 < args.Length && int.TryParse(args[shot + 1], out int c) ? Mathf.Max(1, c) : 1;
            Debug.Log($"[MountainViewer] Photo run: screen {Screen.width}×{Screen.height}, desktop {Screen.currentResolution.width}×{Screen.currentResolution.height}, focus {_photoFocus}, scale {_photoScale}×");
            for (int i = 0; i < 120; i++) yield return null;   // settle
            var frames = new float[120];
            for (int n = 0; n < count; n++)
            {
                for (int i = 0; i < frames.Length; i++) { yield return null; frames[i] = Time.unscaledDeltaTime * 1000f; }
                Array.Sort(frames);
                float median = frames[frames.Length / 2], p95 = frames[(int)(frames.Length * 0.95f)];
                CapturePhoto();
                while (_saving != null && !_saving.IsCompleted) yield return null;
                yield return null;
                Debug.Log($"[MountainViewer] Photo hitch {n + 1}/{count}: steady median {median:F1} ms, p95 {p95:F1} ms; capture frame {LastSaveCaptureFrameMs:F1} ms, longest frame after it {LastSaveLongestOtherFrameMs:F1} ms");
                for (int i = 0; i < 30; i++) yield return null;
            }
            Application.Quit();
        }
    }
}
