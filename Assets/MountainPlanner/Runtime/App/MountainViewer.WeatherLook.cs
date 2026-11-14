using System;
using System.Collections;
using System.IO;
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
            if (Array.IndexOf(args, "-wxoff") >= 0) _weatherView.Enabled = false;   // the build with no weather on screen at all
            int m = Array.IndexOf(args, "-wxmapsize");
            if (m >= 0 && m + 1 < args.Length && int.TryParse(args[m + 1], out int size)) _weatherView.ShadowMapSize = size;
            m = Array.IndexOf(args, "-wxmapevery");
            if (m >= 0 && m + 1 < args.Length && int.TryParse(args[m + 1], out int every)) _weatherView.ShadowMapEvery = every;
            int i = Array.IndexOf(args, "-wxlook");
            if (i >= 0 && i + 1 < args.Length && PinLook(args[i + 1])) Toast($"Pinned weather look: {args[i + 1]} (-wxlook; the HUD tells the game's weather)");
            int shots = Array.IndexOf(args, "-wxshots");
            if (shots >= 0 && shots + 1 < args.Length) StartCoroutine(WeatherLookShots(Path.GetFullPath(args[shots + 1])));
            if (Array.IndexOf(args, "-wxdemo") >= 0) StartCoroutine(WeatherDemo());
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
