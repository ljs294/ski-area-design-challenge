using System;
using System.Text;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.05 PR B1: the developer's weather readout (demo 94) until 4.06 and 4.17 put the weather on screen. It reads
    /// the snapshot's weather block only: the hour at zref (state, pattern, storm, sky, wind), the base, mid and summit
    /// samples, and each built lift's top-station gust against the gust that holds it (LO-11). Shown in the F1 panel, and
    /// always on with <c>-weatherreadout</c>. The text is rebuilt four times a second, never per frame.
    /// </summary>
    public sealed partial class MountainViewer
    {
        static readonly bool WeatherReadoutAlways = Array.IndexOf(Environment.GetCommandLineArgs(), "-weatherreadout") >= 0;
        static readonly string[] StateWords = { "Fair", "Building", "Storm", "Clearing" };
        const float WeatherReadoutSeconds = 0.25f;

        readonly StringBuilder _wxText = new StringBuilder(1024);
        string _wxShown = "";
        float _wxBuiltAt = -1;
        GUIStyle _wxStyle;

        /// <summary>The readout's text, rebuilt at most four times a second from the newest snapshot.</summary>
        string WeatherReadoutText()
        {
            if (Time.unscaledTime - _wxBuiltAt < WeatherReadoutSeconds) return _wxShown;
            _wxBuiltAt = Time.unscaledTime;
            var s = _simNow;
            var t = _wxText.Clear();
            if (s == null) return _wxShown = "";
            if (!s.WeatherKnown)
            {
                t.Append(_simWeather == null ? "Weather: none in this install (the flat stand-in hour plays)"
                                             : "Weather: the clock hasn't run a face hour yet (press Play)");
                return _wxShown = t.ToString();
            }
            var h = s.WeatherHour;
            t.Append(s.WeatherGeneric
                ? (WeatherGeneric.East(_resort.Manifest.Site.Longitude) ? "Typical eastern weather" : "Typical western weather") + ": this area has no weather history yet"
                : $"{_resort.Manifest.Site.Name}'s own weather");
            if (s.WeatherWaiting) t.Append(" · its own weather plays from next October 1");
            t.Append('\n');
            t.Append($"Winter {h.LabelYear}–{(h.LabelYear + 1) % 100:D2} · face hour {h.HourOfYear} · {StateWords[Mathf.Clamp(h.State, 0, 3)]} · pattern {h.Pattern}");
            if (h.StormOrdinal >= 0) t.Append($" · storm {h.StormOrdinal + 1}, {h.StormTotal / 100.0:F2} in of water{(h.FrontPassed ? ", front through" : "")}");
            if (h.SeasonShare != 0) t.Append($" · this winter's character: precipitation ×{h.SeasonShare / 1000.0:F2}, {Signed(h.SeasonOffset)} °F");
            t.Append('\n');
            t.Append($"Sky {h.Cloud / 10}% cloud{(h.Night ? " (night)" : $", clearness {h.Clearness / 10}%")} · cloud base {h.CloudBaseFeet:N0} ft above the valley weather" +
                     $"{(h.Fog ? " · fog" : "")} · wind from {h.Direction}° · gusts ×{h.GustFactor / 1000.0:F2}");
            if (h.Pool > 0) t.Append($" · cold pool {h.Pool / 10.0:F1} °F");
            Sample(t, "Summit", s.WeatherSummit);
            Sample(t, "Mid", s.WeatherMid);
            Sample(t, "Base", s.WeatherBase);
            LiftGusts(t, s);
            CursorWeather(t, s);
            return _wxShown = t.ToString();
        }

        static string Signed(int tenths) => (tenths >= 0 ? "+" : "−") + (Math.Abs(tenths) / 10.0).ToString("F1");

        static void Sample(StringBuilder t, string name, in WeatherSample p)
        {
            t.Append($"\n{name} {p.Feet:N0} ft: {p.Temp / 10.0:F1} °F, wet bulb {p.WetBulb / 10.0:F1} °F, dew {p.Dew / 10.0:F1} °F");
            if (p.Liquid > 0)
                t.Append(p.SnowPermille >= 999 ? $" · snowing {p.SnowTenthsIn / 10.0:F1} in/h"
                         : p.SnowPermille <= 1 ? $" · raining {p.Liquid / 100.0:F2} in/h"
                         : $" · mixed {p.SnowPermille / 10}% snow, {p.SnowTenthsIn / 10.0:F1} in/h");
            t.Append($" · wind {p.Wind / 10} mph, gusts {p.Gust / 10} mph");
        }

        /// <summary>Each built lift's top-station gust against the gust that holds it (tows and carpets never hold).</summary>
        void LiftGusts(StringBuilder t, SimSnapshot s)
        {
            var design = _skierDesign;
            var doc = Design?.Current.Document;
            bool any = false;
            for (int i = 0; i < s.LiftCount; i++)
            {
                if (s.LiftStatus[i] == LiftOpStatus.Planned) continue;
                int limit = -1, top = 0;
                if (design != null)
                    foreach (var l in design.Lifts)
                        if (l.Id == s.LiftIds[i]) { limit = SimWorld.HoldLimitMph(Pacing.Default, l); top = l.TopFeet; break; }
                string name = "Lift";
                if (doc != null) foreach (var l in doc.Lifts) if (l.Id.Value == s.LiftIds[i]) { name = l.Name; break; }
                t.Append(any ? " · " : "\nTop gusts: ");
                any = true;
                t.Append($"{name} {s.LiftGustTenths[i] / 10}");
                if (limit > 0) t.Append($"/{limit} mph");
                else t.Append(" mph (never holds)");
                if (top > 0) t.Append($" at {top:N0} ft");
                if (s.LiftStatus[i] == LiftOpStatus.OnHold) t.Append(" ON HOLD");
                else if (limit > 0 && s.LiftGustTenths[i] >= limit * 10 && s.LiftStatus[i] == LiftOpStatus.Open) t.Append(" (gusting past its limit; its own wind check decides)");
            }
            if (!any) t.Append("\nTop gusts: no lift built yet");
        }

        static readonly string[] ExposureWords = { "deep valley", "valley", "low slope", "slope", "open slope", "upper slope", "ridge", "summit" };

        /// <summary>
        /// PR B2: the weather of the 8 m cell under the pointer, from the snapshot's tables (WX-02 v2): what 4.07's snow, 4.16's
        /// guns and 4.06's layers will read there.
        /// </summary>
        void CursorWeather(StringBuilder t, SimSnapshot s)
        {
            var fields = s.WeatherFields;
            var terrain = fields.Terrain;
            if (terrain == null || fields.Hour < 0)
            {
                t.Append(_resort?.WeatherTerrain == null ? "\nAt the cursor: the weather's map inputs are still building" : "\nAt the cursor: waiting for the first face hour");
                return;
            }
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (mouse == null || cam == null) return;
            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            float hit = Camera.GroundAlong(ray, 30000);
            if (float.IsNaN(hit)) { t.Append("\nAt the cursor: off the map"); return; }
            var g = ray.GetPoint(hit);
            int cell = terrain.CellAt((long)Math.Round(g.x * 1000.0), (long)Math.Round(g.z * 1000.0));
            fields.At(cell, out var c);
            if (!c.Known) { t.Append("\nAt the cursor: no heights here"); return; }
            t.Append($"\nAt the cursor: {c.Feet:N0} ft, {c.HvfFeet:N0} ft above the valley floor, {ExposureWords[c.Exposure]} (class {c.Exposure})");
            t.Append($"\n  {c.Temp / 10.0:F1} °F, wet bulb {c.WetBulb / 10.0:F1} °F, RH {c.Rh / 10}%");
            if (c.FreezingRain && c.Liquid > 0) t.Append($" · FREEZING RAIN {c.Liquid / 100.0:F2} in/h");
            else if (c.Liquid > 0)
                t.Append(c.SnowPermille >= 999 ? $" · snowing {c.SnowTenthsIn / 10.0:F1} in/h"
                         : c.SnowPermille <= 1 ? $" · raining {c.Liquid / 100.0:F2} in/h"
                         : $" · mixed {c.SnowPermille / 10}% snow, {c.SnowTenthsIn / 10.0:F1} in/h");
            t.Append($" · wind {c.Wind / 10} mph from {c.Direction}°, gusts {c.Gust / 10} mph · sun {c.Shortwave} W/m²");
        }

        /// <summary>The F1 panel's Weather rows.</summary>
        void DrawWeatherRows(ref float y, float row, Action<string> caption, Func<float, int, string, float> note)
        {
            if (_sim == null || _simNow == null) return;
            y += row;
            caption("Weather");
            y += note(y, 0, WeatherReadoutText());
        }

        /// <summary>
        /// <c>-weathershots &lt;folder&gt;</c> (with <c>-weatherreadout</c>): opens the mountain (a pre-season game skips to
        /// December first), then plays at 8×, taking a still two face hours after each opening and skipping each night to
        /// the morning, as a player does in the season; logs the readout's text with each still, then quits.
        /// </summary>
        internal void StartWeatherShots(string[] args)
        {
            int i = Array.IndexOf(args, "-weathershots");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(WeatherShots(System.IO.Path.GetFullPath(args[i + 1])));
        }

        const int WeatherShotCount = 6;

        System.Collections.IEnumerator WeatherShots(string folder)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _sim == null || _simNow == null) yield return null;
            System.IO.Directory.CreateDirectory(folder);
            for (int f = 0; f < 60; f++) yield return null;
            if (!_simNow.Open) SendSim(SimCommandKind.SetMountainOpen, 1);
            if (_simNow.Phase == SeasonPhase.PreSeason)
                for (int k = 0; k < 2; k++) yield return SkipAndWait(SimCommandKind.SkipToNextMonth);
            SendSim(SimCommandKind.SetSpeed, Pacing.Default.Speeds.Length);
            int shots = 0;
            long shotDay = -1, skippedDay = -1;
            float give = Time.unscaledTime + 30 * 60;
            while (shots < WeatherShotCount && Time.unscaledTime < give)
            {
                yield return null;
                var s = _simNow;
                if (s.Skip != SkipKind.None) continue;
                long day = s.FaceMs / SimWorld.DayMs;
                int second = (int)(s.FaceMs % SimWorld.DayMs / 1000);
                if (day != shotDay && second >= s.OpenSecond + 7200 && second < s.StopSecond)
                {
                    shotDay = day;
                    SendSim(SimCommandKind.Pause);
                    for (int f = 0; f < 10; f++) yield return null;
                    _wxBuiltAt = -1;
                    Debug.Log($"[Weather] shot {shots}: {_simClock.Now}\n{WeatherReadoutText()}");
                    ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(folder, $"weather-{shots:D2}.png"));
                    yield return null;
                    yield return null;
                    shots++;
                    SendSim(SimCommandKind.SetSpeed, Pacing.Default.Speeds.Length);
                }
                else if (day != skippedDay && second > s.StopSecond + 60)
                {
                    skippedDay = day;
                    SendSim(SimCommandKind.SkipToMorning);
                }
            }
            Debug.Log($"[Weather] {shots} shots saved to {folder}");
            Application.Quit();
        }

        System.Collections.IEnumerator SkipAndWait(SimCommandKind kind)
        {
            long before = _simNow.Tick;
            SendSim(kind);
            float give = Time.unscaledTime + 60;
            while ((_simNow.Tick == before || _simNow.Skip != SkipKind.None) && Time.unscaledTime < give) yield return null;
        }

        /// <summary><c>-weatherreadout</c>: the same text in a box on the right, under the HUD's bar, whatever else shows.</summary>
        void DrawWeatherReadout()
        {
            if (!WeatherReadoutAlways || _sim == null || _simNow == null || _photo) return;
            _wxStyle ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 14, wordWrap = true, normal = { textColor = Color.white } };
            string text = WeatherReadoutText();
            if (text.Length == 0) return;
            float width = Mathf.Min(620, Screen.width * 0.45f);
            var content = new GUIContent(text);
            float height = _wxStyle.CalcHeight(content, width);
            GUI.Box(new Rect(Screen.width - width - 20, 96, width, height), content, _wxStyle);
        }
    }
}
