using System;
using System.Collections;
using System.IO;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Weather;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.06's unattended switches: <c>-wxlook name</c> pins a look (shots and the benchmark only: the HUD still tells
    /// the game's weather, so a toast says the look is pinned), <c>-nofog</c>, <c>-nocloudshadow</c>, <c>-noprecip</c>,
    /// <c>-nograde</c>, <c>-noglint</c>, <c>-nogust</c>, <c>-noskyclouds</c>, <c>-nowind</c>, <c>-wxoff</c> (no weather on screen at
    /// all), <c>-wxmapsize N</c> and <c>-wxmapevery N</c> (the clouds' shadow map) bisect its cost, and
    /// <c>-wxshots folder</c> takes the review shots: each look from the overview, two from near the ground, then quits.
    /// <c>-wxvolume off|rays|full</c> picks how the air is drawn (else by quality: Low off, Medium rays, High up full),
    /// <c>-wxdebug</c> shows the sun's reach through the air as grey, and <c>-wxrays folder</c> takes the low-sun shots
    /// (ridge shadows, shafts), then quits. Demo 108 (<c>-wxdemo</c>) adds two keys: F9 steps the way, F10 pins the next look.
    /// </summary>
    public sealed partial class MountainViewer
    {
        float BaseMetres => (_resort?.WeatherBaseFeet ?? 0) * 0.3048f;
        float SummitMetres => (_resort?.WeatherSummitFeet ?? 0) * 0.3048f;

        void StartWeatherLook(string[] args)
        {
            _weatherView.NoFog = Array.IndexOf(args, "-nofog") >= 0;
            _weatherView.NoCloudShadow = Array.IndexOf(args, "-nocloudshadow") >= 0;
            _weatherView.NoPrecipitation = Array.IndexOf(args, "-noprecip") >= 0;
            _weatherView.NoGrade = Array.IndexOf(args, "-nograde") >= 0;
            _weatherView.NoGlint = Array.IndexOf(args, "-noglint") >= 0;
            _weatherView.NoGust = Array.IndexOf(args, "-nogust") >= 0;
            _weatherView.NoSkyClouds = Array.IndexOf(args, "-noskyclouds") >= 0;
            _weatherView.NoWind = Array.IndexOf(args, "-nowind") >= 0;
            _wxOffSwitch = Array.IndexOf(args, "-wxoff") >= 0;   // the build with no weather on screen at all
            int m = Array.IndexOf(args, "-wxmapsize");
            if (m >= 0 && m + 1 < args.Length && int.TryParse(args[m + 1], out int size)) _weatherView.ShadowMapSize = size;
            m = Array.IndexOf(args, "-wxmapevery");
            if (m >= 0 && m + 1 < args.Length && int.TryParse(args[m + 1], out int every)) _weatherView.ShadowMapEvery = every;
            _weatherView.VolumeDebug = Array.IndexOf(args, "-wxdebug") >= 0;
            // Settings › Weather for this run only, never remembered (review shots): -wxceiling, -wxfog <0-100>.
            if (Array.IndexOf(args, "-wxceiling") >= 0) WeatherOptions.SetCeiling(true, remember: false);
            m = Array.IndexOf(args, "-wxfog");
            if (m >= 0 && m + 1 < args.Length && int.TryParse(args[m + 1], out int fog)) WeatherOptions.SetFogPercent(fog, remember: false);
            m = Array.IndexOf(args, "-wxvolume");   // off | rays | full: how the air is drawn (by quality without it)
            if (m >= 0 && m + 1 < args.Length && WeatherVolume.TryParseMode(args[m + 1], out var volume)) _weatherView.VolumeMode = volume;
            int i = Array.IndexOf(args, "-wxlook");
            if (i >= 0 && i + 1 < args.Length && PinLook(args[i + 1])) Toast($"Pinned weather look: {args[i + 1]} (-wxlook; the HUD tells the game's weather)");
            int rays = Array.IndexOf(args, "-wxrays");   // -wxrays <folder>: the low-sun review shots (rays, ridge shadows), then quits
            if (rays >= 0 && rays + 1 < args.Length) StartCoroutine(WeatherRayShots(Path.GetFullPath(args[rays + 1])));
            int shots = Array.IndexOf(args, "-wxshots");
            if (shots >= 0 && shots + 1 < args.Length) StartCoroutine(WeatherLookShots(Path.GetFullPath(args[shots + 1])));
            if (Array.IndexOf(args, "-wxdemo") >= 0) { _weatherDemoKeys = true; StartCoroutine(WeatherDemo()); }
        }

        // Demo 108's two keys, for comparing the ways the air is drawn on the same weather: F9 steps the way (the quality's
        // own, then today's look, god rays, the full volume), F10 pins the next look (and after the last, the game's weather).
        bool _weatherDemoKeys;
        int _demoWay = -1, _demoLook = -1;
        static readonly string[] DemoLooks = { "bluebird", "building", "clearing", "inversion", "coldsnap", "storm", "bigstorm", "gale" };
        static readonly string[] DemoWays = { "today's look (the surfaces draw the air)", "god rays over the flat clouds", "volumetric clouds and god rays" };

        void UpdateWeatherDemoKeys()
        {
            var keys = _weatherDemoKeys ? UnityEngine.InputSystem.Keyboard.current : null;
            if (keys == null) return;
            if (keys.f9Key.wasPressedThisFrame)
            {
                _demoWay = _demoWay >= 2 ? -1 : _demoWay + 1;
                _weatherView.VolumeMode = _demoWay < 0 ? (WeatherVolumeMode?)null : (WeatherVolumeMode)_demoWay;
                Toast(_demoWay < 0 ? $"The air: this quality's own way ({DemoWays[(int)WeatherVolume.ModeFor(MountainPlanner.Presentation.QualityPresets.Current)]})" : $"The air: {DemoWays[_demoWay]}");
            }
            if (keys.f10Key.wasPressedThisFrame)
            {
                _demoLook = _demoLook >= DemoLooks.Length - 1 ? -1 : _demoLook + 1;
                if (_demoLook < 0) { _weatherView.Unpin(); Toast("The game's own weather again"); }
                else if (PinLook(DemoLooks[_demoLook])) Toast($"Pinned look: {DemoLooks[_demoLook]} (F10 for the next)");
            }
        }

        bool PinLook(string name)
        {
            var (b, t) = MountainHeights();
            if (!WeatherLook.Preset(name, b, t, out var look, out var arc)) { Debug.LogWarning($"[WeatherLook] no look '{name}'"); return false; }
            _weatherView.Pin(look, arc);
            return true;
        }

        /// <summary>The base and summit (m): the weather's reference points, else the camera's ground.</summary>
        (float, float) MountainHeights()
        {
            float b = BaseMetres, t = SummitMetres;
            if (b <= 0 || t <= b) { b = 1900; t = 3200; }
            return (b, t);
        }

        /// <summary>Demo 108's season: a pinned seed, so the storm it finds is the same every time.</summary>
        public const long WeatherDemoSeed = 0x4D06_0108;

        /// <summary>
        /// <c>-wxdemo</c> (demo 108): the game's own weather, not a pinned look. Pins the season's seed, opens the mountain,
        /// skips to December, then skips mornings until the forecast names a big storm within two days, runs at 8× until it
        /// starts building and settles at 4×, so Building, Storm, Clearing and the powder morning play out with the HUD's
        /// forecast and toasts agreeing. The camera stays the player's.
        /// </summary>
        IEnumerator WeatherDemo()
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _sim == null || _simNow == null) yield return null;
            for (int f = 0; f < 30; f++) yield return null;
            SendSim(SimCommandKind.PinSeasonSeed, WeatherDemoSeed);
            if (!_simNow.Open) SendSim(SimCommandKind.SetMountainOpen, 1);
            for (int f = 0; f < 10; f++) yield return null;
            if (_simNow.Phase == SeasonPhase.PreSeason)
                for (int k = 0; k < 2; k++) yield return SkipAndWait(SimCommandKind.SkipToNextMonth);
            Toast("Demo 108: looking for a big storm in this season's forecast…");
            bool big = false;
            for (int k = 0; k < 40 && !(big = BigStormAhead()); k++) yield return SkipAndWait(SimCommandKind.SkipToMorning);
            SendSim(SimCommandKind.SetSpeed, Pacing.Default.Speeds.Length);
            float give = Time.unscaledTime + 600;
            while (_simNow.WeatherHour.State < 1 && _simNow.WeatherHour.Liquid <= 0 && Time.unscaledTime < give) yield return null;
            SendSim(SimCommandKind.SetSpeed, 3);
            Toast(big ? "A big storm is building: watch it cross the mountain at 4×" : "A storm is building (no big one this month): 4×");
        }

        bool BigStormAhead()
        {
            var f = _simNow?.Forecast;
            if (f == null || !f.Known) return false;
            for (int i = 0; i < Math.Min(2, f.DayCount); i++)
                if (f.Cards[i].Expects(WeatherEventKind.BigStorm) || f.Cards[i].Expects(WeatherEventKind.StormOfTheSeason)) return true;
            return false;
        }

        // The morning's clock moved on until the sun stands this high (degrees).
        IEnumerator SunAt(float elevation)
        {
            var now = Lighting.Clock.Now;
            for (int second = 5 * 3600; second < 13 * 3600; second += 120)
            {
                Lighting.SetTime(new ViewTime(now.Year, now.DayOfYear, second));
                yield return null;
                if (Lighting.CurrentLight.SunElevation >= elevation) break;
            }
        }

        IEnumerator WeatherRayShots(string dir)
        {
            BeginScriptedRun();
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || Forest == null) yield return null;
            PinLook("bluebird");
            while (!_weatherView.Ready) yield return null;
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 90; f++) yield return null;
            var home = Camera.Target;
            // look, the sun's height, the camera's distance and pitch, and whether it faces the sun (else across it)
            var list = new (string look, float sun, float distance, float pitch, bool toSun, string name)[]
            {
                ("bluebird", 5, 5000, 24, false, "bluebird-sunrise"),
                ("bluebird", 9, 2500, 8, true, "bluebird-into-sun"),
                ("inversion", 7, 5000, 24, false, "inversion-sunrise"),
                ("clearing", 22, 3000, 10, true, "clearing-shafts"),
                ("building", 14, 5000, 24, false, "building-morning"),
                ("coldsnap", 6, 1500, 6, true, "coldsnap-valley"),
            };
            foreach (var shot in list)
            {
                PinLook(shot.look);
                yield return SunAt(shot.sun);
                var toSun = (Vector3)Shader.GetGlobalVector("_SkySunDirection");
                float azimuth = Mathf.Atan2(toSun.x, toSun.z) * Mathf.Rad2Deg;
                Camera.Frame(home, shot.distance);
                Camera.SetAngles(shot.toSun ? azimuth + 20 : azimuth + 110, shot.pitch);
                for (int f = 0; f < 120; f++) yield return null;
                Debug.Log($"[WeatherLook] {shot.name}: sun {Lighting.CurrentLight.SunElevation:0.0} deg, azimuth {azimuth:0}, volume {(_weatherView.Volume != null && _weatherView.Volume.Active ? "on" : "off")}");
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{shot.name}.png"));
                yield return null; yield return null;
            }
            Debug.Log($"[WeatherLook] ray shots in {dir}");
            Application.Quit();
        }

        IEnumerator WeatherLookShots(string dir)
        {
            BeginScriptedRun();
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || Forest == null) yield return null;
            PinLook("building");
            while (!_weatherView.Ready) yield return null;   // the cloud field bakes on a worker thread
            Directory.CreateDirectory(dir);
            for (int f = 0; f < 90; f++) yield return null;
            var home = Camera.Target;
            float yaw = Camera.Yaw;
            string[] overview = { "building", "storm", "bigstorm", "clearing", "bluebird", "rain", "inversion", "coldsnap", "gale" };
            foreach (var name in overview)
            {
                PinLook(name);
                Camera.Frame(home, 5000);
                Camera.SetAngles(yaw, 32);
                for (int f = 0; f < 90; f++) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{name}-overview.png"));
                yield return null; yield return null;
            }
            foreach (var name in new[] { "bigstorm", "bluebird" })
            {
                PinLook(name);
                Camera.Frame(home, 260);
                Camera.SetAngles(yaw, 10);
                for (int f = 0; f < 90; f++) yield return null;
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, $"{name}-low.png"));
                yield return null; yield return null;
            }
            // A gale across a ridge, low and level, so the spindrift streams against the sky.
            PinLook("gale");
            for (int f = 0; f < 30; f++) yield return null;
            var ridges = _weatherView.SpindriftPoints;
            if (ridges.Length > 0)
            {
                float toward = _weatherView.Shown.Direction + 180f + (float)_resort.Manifest.Crs.GridConvergenceDegrees;
                Camera.Frame(ridges[0], 700);
                Camera.SetAngles(toward - 90f, 4);
                float until = Time.realtimeSinceStartup + 3;
                while (Time.realtimeSinceStartup < until) yield return null;
                var sd = _weatherView.Spindrift;
                float ground = Camera.Surface != null ? Camera.Surface.HeightAt(ridges[0].x, ridges[0].z) : float.NaN;
                Debug.Log($"[WeatherLook] gale-ridge: {sd.Count} spindrift emitters at {sd.Shown:0.00}, wind {_weatherView.Shown.Wind:0} mph, " +
                          $"first at {ridges[0]} (ground {ground:0.0} m), camera at {Camera.transform.position}");
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "gale-ridge.png"));
                yield return null; yield return null;
            }
            else Debug.LogWarning("[WeatherLook] no spindrift points: no gale-ridge shot");
            Debug.Log($"[WeatherLook] shots in {dir}");
            Application.Quit();
        }
    }
}
