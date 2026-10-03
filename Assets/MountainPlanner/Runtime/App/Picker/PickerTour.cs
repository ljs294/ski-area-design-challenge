using System;
using System.Collections;
using System.Globalization;
using System.IO;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.UI.Picker;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Picker
{
    /// <summary>
    /// Scripted review movies of the site picker (Picker Lab only; <c>-tour name -record folder</c>). Each tour
    /// drives the real picker the way a player would: typed text, real key events, a drawn pointer for
    /// clicks, with a caption and the keys shown on screen. <see cref="FrameRecorder"/> saves the frames, and
    /// <c>PickerLabSetup.EncodeMovies</c> turns them into MP4s with Unity's own encoder.
    /// Tours: search, place, themes, offline.
    /// </summary>
    public sealed class PickerTour
    {
        static readonly GeoPoint JacksonHole = new GeoPoint(43.593, -110.848);

        readonly SitePicker _picker;
        readonly FrameRecorder _recorder;
        readonly Label _caption;
        readonly VisualElement _keys, _pointer, _ripple;
        Vector2 _at;

        public PickerTour(SitePicker picker, FrameRecorder recorder)
        {
            _picker = picker;
            _recorder = recorder;
            var root = picker.Document.rootVisualElement;
            var overlay = new VisualElement { pickingMode = PickingMode.Ignore };
            overlay.style.position = Position.Absolute;
            overlay.style.left = 0; overlay.style.top = 0; overlay.style.right = 0; overlay.style.bottom = 0;
            root.Add(overlay);

            var captionRow = Row(overlay, top: 22);
            _caption = new Label { pickingMode = PickingMode.Ignore };
            Plate(_caption);
            _caption.style.fontSize = 17;
            _caption.style.paddingLeft = 16; _caption.style.paddingRight = 16;
            _caption.style.paddingTop = 9; _caption.style.paddingBottom = 9;
            _caption.style.opacity = 0;
            captionRow.Add(_caption);

            _keys = Row(overlay, bottom: 28);
            _keys.style.flexDirection = FlexDirection.Row;

            _ripple = new VisualElement { pickingMode = PickingMode.Ignore };
            _ripple.style.position = Position.Absolute;
            _ripple.style.width = 30; _ripple.style.height = 30;
            _ripple.style.borderTopLeftRadius = 15; _ripple.style.borderTopRightRadius = 15;
            _ripple.style.borderBottomLeftRadius = 15; _ripple.style.borderBottomRightRadius = 15;
            foreach (var side in new Action<StyleFloat>[] { w => _ripple.style.borderTopWidth = w, w => _ripple.style.borderBottomWidth = w, w => _ripple.style.borderLeftWidth = w, w => _ripple.style.borderRightWidth = w })
                side(2.5f);
            var ring = new Color(1f, 1f, 1f, 0.95f);
            _ripple.style.borderTopColor = ring; _ripple.style.borderBottomColor = ring; _ripple.style.borderLeftColor = ring; _ripple.style.borderRightColor = ring;
            _ripple.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
            _ripple.style.opacity = 0;
            overlay.Add(_ripple);

            _pointer = new VisualElement { pickingMode = PickingMode.Ignore };
            _pointer.style.position = Position.Absolute;
            _pointer.style.width = 22; _pointer.style.height = 26;
            _pointer.generateVisualContent += DrawPointer;
            _pointer.style.opacity = 0;
            overlay.Add(_pointer);
        }

        public IEnumerator Run(string tour)
        {
            switch (tour)
            {
                case "search": return Search();
                case "place": return Place();
                case "themes": return Themes();
                case "offline": return Offline();
                default: throw new ArgumentException("Tours: search, place, themes, offline.", nameof(tour));
            }
        }

        // ---- the tours ----

        IEnumerator Search()
        {
            yield return Settle(3f);
            _recorder?.Begin();
            yield return Caption("Search runs only when you press Enter: no search-as-you-type.", 1.6f);
            yield return Type(Q<TextField>("search"), "Crystal Mountain");
            yield return Press(KeyCode.Return, "Enter");
            yield return Wait(2.2f);
            yield return Caption("Down moves into the results; Enter picks one.", 1.2f);
            yield return Press(KeyCode.DownArrow, "↓");
            yield return Wait(0.9f);
            yield return Press(KeyCode.DownArrow, "↓");
            yield return Wait(0.9f);
            yield return Press(KeyCode.UpArrow, "↑");
            yield return Wait(0.9f);
            yield return Submit("Enter");
            yield return Caption("The map flies there, places the exact square and suggests the name.", 4f);
            yield return Caption("The estimate is a number with its word, and it warns in words when a site isn't all 1 m.", 4.5f);
            yield return Caption("Download is the window's one filled button, and carries the download size.", 0.2f);
            yield return PointTo(Q<Button>("download").worldBound.center + new Vector2(-30, 6), 1f);
            yield return Wait(2.4f);
            _recorder?.End();
        }

        IEnumerator Place()
        {
            _picker.Map.SetCentre(JacksonHole, 12);
            yield return Settle(4f);
            _recorder?.Begin();
            yield return Caption("Click the map to place the square, or press Enter to place it at the centre.", 0.8f);
            var map = _picker.Map.worldBound;
            _at = new Vector2(map.xMax - 120, map.yMax - 90);
            yield return PointTo(map.center + new Vector2(-40, 30), 1.1f);
            yield return Click();
            _picker.PlaceAt(_picker.Map.GeoAt(_picker.Map.WorldToLocal(_at)));
            yield return Wait(2.6f);
            yield return Caption("Plans are dashed surveyor's orange, with a dimension line, as in the HUD mockup.", 2.8f);
            HidePointer();
            yield return Caption("With the map focused, the arrows nudge the square 100 m, and Shift moves it 1 km.", 0.8f);
            _picker.Map.Focus();
            for (int i = 0; i < 3; i++) { yield return Press(KeyCode.RightArrow, "→"); yield return Wait(0.45f); }
            yield return Press(KeyCode.UpArrow, "↑", shift: true);
            yield return Wait(1.6f);
            yield return Caption("The size steps 0.1 km, and each step reads differently in miles.", 0.8f);
            Q<Slider>("size").Focus();
            for (int i = 0; i < 4; i++) { yield return Press(KeyCode.RightArrow, "→"); yield return Wait(0.55f); }
            yield return Press(KeyCode.LeftArrow, "←", shift: true);
            yield return Wait(1.8f);
            yield return Caption("Page Up and Page Down zoom; Home comes back to the square. No camera buttons.", 0.8f);
            _picker.Map.Focus();
            yield return Press(KeyCode.PageDown, "Page Down");
            yield return Wait(1.3f);
            yield return Press(KeyCode.PageDown, "Page Down");
            yield return Wait(1.6f);
            yield return Press(KeyCode.Home, "Home");
            yield return Wait(2.4f);
            yield return Caption("Topo or imagery, and the terrain-data overlay.", 0.3f);
            yield return PointTo(Q<Button>("base-imagery").worldBound.center, 0.9f);
            yield return Click();
            _picker.SetImagery(true);
            yield return Wait(2.6f);
            yield return PointTo(Q<Button>("coverage-toggle").worldBound.center, 0.9f);
            yield return Click();
            Press(Q<Button>("coverage-toggle"));
            yield return Wait(1.6f);
            yield return Click();
            Press(Q<Button>("coverage-toggle"));
            yield return Wait(1.6f);
            yield return Caption("Download needs a name; until there is one, the picker says so.", 0.3f);
            var name = Q<TextField>("name");
            yield return PointTo(name.worldBound.center, 0.8f);
            yield return Click();
            name.Focus();
            HidePointer();
            yield return Erase(name);
            yield return Wait(1.8f);
            yield return Type(name, "Rendezvous Bowl");
            yield return Wait(2.2f);
            _recorder?.End();
        }

        IEnumerator Themes()
        {
            yield return OpenCrystal();
            _recorder?.Begin();
            yield return Caption("Dark: warm graphite, the accepted Trailhead look.", 2.6f);
            _picker.SetTheme(false);
            yield return Caption("Light: sign white, from the mockup's light set.", 3f);
            DisplayUnits.Set(UnitSystem.Metric, remember: false);
            yield return Caption("Units follow the game's setting (U): kilometres here, and the square stays exact in metres.", 3.4f);
            DisplayUnits.Set(UnitSystem.Imperial, remember: false);
            _picker.SetTheme(true);
            yield return Caption("Back to miles and the dark panel.", 2.4f);
            _recorder?.End();
        }

        IEnumerator Offline()
        {
            yield return OpenCrystal();
            _recorder?.Begin();
            yield return Caption("If search can't reach its server, it says so under the field, and the map keeps working.", 0.6f);
            Http.NetworkDisabled = true;
            var search = Q<TextField>("search");
            search.Focus();
            yield return Erase(search);
            yield return Type(search, "Snowbird");
            yield return Press(KeyCode.Return, "Enter");
            yield return Wait(3f);
            yield return Press(KeyCode.Escape, "Esc");
            yield return Wait(0.8f);
            yield return Caption("With no connection at all, the map becomes this panel. Download waits.", 0.6f);
            _picker.Map.Focus();
            yield return Press(KeyCode.PageDown, "Page Down");
            yield return Wait(3.4f);
            yield return Caption("When the connection is back, Retry brings the map back.", 0.6f);
            Http.NetworkDisabled = false;
            yield return PointTo(Q<Button>("retry").worldBound.center, 1f);
            yield return Click();
            Press(Q<Button>("retry"));
            yield return Wait(3.4f);
            _recorder?.End();
        }

        IEnumerator OpenCrystal()
        {
            Q<TextField>("search").value = "Crystal Mountain Washington";
            _picker.RunSearch();
            yield return Settle(6f);
        }

        // ---- the parts ----

        T Q<T>(string name) where T : VisualElement => _picker.Document.rootVisualElement.Q<T>(name);

        static VisualElement Row(VisualElement parent, float? top = null, float? bottom = null)
        {
            var row = new VisualElement { pickingMode = PickingMode.Ignore };
            row.style.position = Position.Absolute;
            row.style.left = 0; row.style.right = 0;
            if (top.HasValue) row.style.top = top.Value;
            if (bottom.HasValue) row.style.bottom = bottom.Value;
            row.style.alignItems = Align.Center;
            row.style.justifyContent = Justify.Center;
            parent.Add(row);
            return row;
        }

        static void Plate(VisualElement e)
        {
            e.style.backgroundColor = (Color)new Color32(28, 25, 22, 255);
            var hair = new Color32(0x4a, 0x44, 0x3e, 255);
            e.style.borderTopColor = (Color)hair; e.style.borderBottomColor = (Color)hair; e.style.borderLeftColor = (Color)hair; e.style.borderRightColor = (Color)hair;
            e.style.borderTopWidth = 1; e.style.borderBottomWidth = 1; e.style.borderLeftWidth = 1; e.style.borderRightWidth = 1;
            e.style.borderTopLeftRadius = 6; e.style.borderTopRightRadius = 6; e.style.borderBottomLeftRadius = 6; e.style.borderBottomRightRadius = 6;
            e.style.color = (Color)new Color32(0xef, 0xe9, 0xdf, 255);
        }

        IEnumerator Caption(string text, float hold)
        {
            if (_caption.text != text)
            {
                for (float t = 0; t < 0.15f; t += Time.unscaledDeltaTime) { _caption.style.opacity = 1 - t / 0.15f; yield return null; }
                _caption.text = text;
                for (float t = 0; t < 0.15f; t += Time.unscaledDeltaTime) { _caption.style.opacity = t / 0.15f; yield return null; }
                _caption.style.opacity = 1;
            }
            yield return Wait(hold);
        }

        /// <summary>Shows the key, then sends it to whatever has focus, as the keyboard would.</summary>
        IEnumerator Press(KeyCode key, string label, bool shift = false)
        {
            ShowKeys(shift ? new[] { "Shift", label } : new[] { label });
            yield return Wait(0.25f);
            var target = _picker.Document.rootVisualElement.panel.focusController.focusedElement as VisualElement;
            if (target != null)
                using (var e = KeyDownEvent.GetPooled('\0', key, shift ? EventModifiers.Shift : EventModifiers.None))
                {
                    e.target = target;
                    target.SendEvent(e);
                }
        }

        /// <summary>Enter on a focused button: the submit the keyboard sends with it.</summary>
        IEnumerator Submit(string label)
        {
            ShowKeys(new[] { label });
            yield return Wait(0.25f);
            if (_picker.Document.rootVisualElement.panel.focusController.focusedElement is Button b) Press(b);
        }

        static void Press(Button b)
        {
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = b; b.SendEvent(e); }
        }

        void ShowKeys(string[] keys)
        {
            _keys.Clear();
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0) { var plus = new Label("+") { pickingMode = PickingMode.Ignore }; plus.style.color = (Color)new Color32(0xcf, 0xc7, 0xbb, 255); plus.style.fontSize = 15; plus.style.marginLeft = 6; plus.style.marginRight = 6; _keys.Add(plus); }
                var cap = new Label(keys[i]) { pickingMode = PickingMode.Ignore };
                Plate(cap);
                cap.style.backgroundColor = (Color)new Color32(0x2d, 0x29, 0x26, 255);
                cap.style.borderBottomWidth = 3;
                cap.style.borderTopLeftRadius = 5; cap.style.borderTopRightRadius = 5; cap.style.borderBottomLeftRadius = 5; cap.style.borderBottomRightRadius = 5;
                cap.style.minWidth = 34;
                cap.style.unityTextAlign = TextAnchor.MiddleCenter;
                cap.style.fontSize = 16;
                cap.style.unityFontStyleAndWeight = FontStyle.Bold;
                cap.style.paddingLeft = 10; cap.style.paddingRight = 10; cap.style.paddingTop = 4; cap.style.paddingBottom = 4;
                _keys.Add(cap);
            }
            _keys.style.opacity = 1;
            _keys.schedule.Execute(() => _keys.style.opacity = 0).StartingIn(1100);
        }

        IEnumerator Type(TextField field, string text)
        {
            field.Focus();
            string start = field.value ?? "";
            for (int i = 1; i <= text.Length; i++)
            {
                field.value = start + text.Substring(0, i);
                field.SelectRange(field.value.Length, field.value.Length);   // a caret at the end, as typing leaves it
                yield return Wait(0.085f);
            }
        }

        IEnumerator Erase(TextField field)
        {
            while (!string.IsNullOrEmpty(field.value))
            {
                field.value = field.value.Substring(0, field.value.Length - 1);
                field.SelectRange(field.value.Length, field.value.Length);
                yield return Wait(0.05f);
            }
        }

        IEnumerator PointTo(Vector2 target, float seconds)
        {
            if (_pointer.style.opacity.value < 0.5f)
            {
                if (_at == default) _at = target + new Vector2(140, 90);
                _pointer.style.opacity = 1;
            }
            Vector2 from = _at;
            for (float t = 0; t < seconds; t += Time.unscaledDeltaTime)
            {
                float s = Mathf.SmoothStep(0, 1, t / seconds);
                MovePointer(Vector2.Lerp(from, target, s));
                yield return null;
            }
            MovePointer(target);
        }

        void MovePointer(Vector2 at)
        {
            _at = at;
            _pointer.style.left = at.x;
            _pointer.style.top = at.y;
        }

        void HidePointer() => _pointer.style.opacity = 0;

        IEnumerator Click()
        {
            _ripple.style.left = _at.x;
            _ripple.style.top = _at.y;
            for (float t = 0; t < 0.35f; t += Time.unscaledDeltaTime)
            {
                float k = t / 0.35f;
                _ripple.style.scale = new Scale(Vector3.one * (0.4f + k));
                _ripple.style.opacity = 1 - k;
                yield return null;
            }
            _ripple.style.opacity = 0;
        }

        static void DrawPointer(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            p.lineWidth = 1.4f;
            p.strokeColor = new Color(0.05f, 0.05f, 0.05f);
            p.fillColor = Color.white;
            p.lineJoin = LineJoin.Round;
            p.BeginPath();
            p.MoveTo(new Vector2(1, 1));
            p.LineTo(new Vector2(1, 19));
            p.LineTo(new Vector2(5.5f, 14.8f));
            p.LineTo(new Vector2(8.6f, 22));
            p.LineTo(new Vector2(11.6f, 20.7f));
            p.LineTo(new Vector2(8.6f, 13.7f));
            p.LineTo(new Vector2(14.6f, 13.7f));
            p.ClosePath();
            p.Fill();
            p.Stroke();
        }

        /// <summary>Real-time waits: the map, search and estimate work against real servers.</summary>
        static IEnumerator Wait(float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds);
        }

        /// <summary>Before recording: give the tiles and the overlay time to arrive.</summary>
        static IEnumerator Settle(float seconds) => Wait(seconds);
    }

    /// <summary>
    /// Saves the screen as JPEG frames about 15 times a second, each named by its time in milliseconds since
    /// <see cref="Begin"/>, so the encoder can lay them on an exact 15 fps timeline.
    /// </summary>
    public sealed class FrameRecorder : MonoBehaviour
    {
        public const float FramesPerSecond = 15;
        public string Folder;
        bool _recording;
        float _start, _next;

        public void Begin()
        {
            Directory.CreateDirectory(Folder);
            foreach (string old in Directory.GetFiles(Folder, "f_*.jpg")) File.Delete(old);
            _start = _next = Time.unscaledTime;
            _recording = true;
            StartCoroutine(Capture());
        }

        public void End() => _recording = false;

        IEnumerator Capture()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (_recording)
            {
                yield return endOfFrame;
                float now = Time.unscaledTime;
                if (now < _next) continue;
                _next += 1 / FramesPerSecond;
                if (_next < now) _next = now;   // fell behind: carry on from here
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                byte[] jpg = shot.EncodeToJPG(92);
                Destroy(shot);
                int ms = Mathf.RoundToInt((now - _start) * 1000);
                File.WriteAllBytes(Path.Combine(Folder, "f_" + ms.ToString("D6", CultureInfo.InvariantCulture) + ".jpg"), jpg);
            }
        }
    }
}
