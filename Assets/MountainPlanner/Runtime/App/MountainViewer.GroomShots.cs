using System;
using System.Collections;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -groomshots &lt;folder&gt; (task 4.25 PR A review), with -drawnresort -simto and, for the groomed set, -groomers N: once
    /// the season has skipped to its morning, the resort and its two longest runs from far to close on the snow surface,
    /// each with and without the Snow conditions layer, then Analysis › Trails and the longest run's card. Run once with
    /// groomers and once without, the same views show what the night's grooming did. Every number in the log comes from
    /// the worker's snapshot.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartGroomShots(string[] args)
        {
            int i = Array.IndexOf(args, "-groomshots");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(GroomShots(args[i + 1]));
            int d = Array.IndexOf(args, "-groomdots");
            if (d >= 0 && d + 1 < args.Length) StartCoroutine(GroomDots(args[d + 1]));
            int gt = Array.IndexOf(args, "-groomtab");   // task 4.25 PR B
            if (gt >= 0 && gt + 1 < args.Length) StartCoroutine(GroomTab(args[gt + 1]));
        }

        /// <summary>The capture's groomer to show hovered (−1: the pointer's, as in play).</summary>
        int _groomerForceHover = -1;

        /// <summary>
        /// -groomdots &lt;folder&gt; (GO-13 review), with -drawnresort -simto -groomers N: the clock runs at 8× from the season
        /// skip until a groomer works in the dark, then pauses for the dots: the resort, the working groomer from far to close
        /// (with and without its hover label), then on to the morning for the groomers parked at the depot.
        /// </summary>
        IEnumerator GroomDots(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(1500));
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            float give = Time.unscaledTime + 120;
            while (!_simToRunning && Time.unscaledTime < give) yield return null;
            while (_simToRunning) yield return null;
            if (_simNow == null || _netGeometry == null) { Debug.LogError("[GroomDots] no world"); Application.Quit(1); yield break; }
            if (Camera != null) Camera.InputEnabled = false;
            _scriptedRun = true;
            SendSim(SimCommandKind.Resume);
            SendSim(SimCommandKind.SetSpeed, 4);
            int working = -1;
            float until = Time.unscaledTime + 900;
            while (Time.unscaledTime < until)
            {
                var g = _simNow.Grooming;
                working = -1;
                int at = 0;
                for (int i = 0; i < g.GroomerCount; i++)
                    if (g.AtJob[i] != GroomJob.None && g.AtTrailA[i] >= 0) { if (working < 0) working = i; at++; }
                // two at work in the full night (any one at work, late on)
                if (working >= 0 && Lighting != null && Lighting.CurrentLight.SunElevation < -12 && (at >= Math.Min(2, g.GroomerCount) || Time.unscaledTime > until - 300)) break;
                yield return null;
            }
            SendSim(SimCommandKind.Pause);
            for (int i = 0; i < 90; i++) yield return null;
            var v = _simNow.Grooming;
            int busy = 0;
            for (int i = 0; i < v.GroomerCount; i++) if (v.AtJob[i] != GroomJob.None) busy++;
            Debug.Log($"[GroomDots] {(working >= 0 ? "working" : "no groomer working")} at face day {_simNow.FaceMs / SimWorld.DayMs}, " +
                      $"{_simNow.FaceMs % SimWorld.DayMs / 60000 / 60:D2}:{_simNow.FaceMs % SimWorld.DayMs / 60000 % 60:D2}; {busy} of {v.GroomerCount} at work");
            for (int i = 0; i < v.GroomerCount; i++)
                Debug.Log($"[GroomDots] groomer {v.Number[i]}: A {v.AtTrailA[i]} {v.AtPpmA[i]} at {v.AtMinA[i]}, B {v.AtTrailB[i]} {v.AtPpmB[i]} at {v.AtMinB[i]}, {v.AtJob[i]}; " +
                          $"drawn at ({_groomerShown[i].x:F0}, {_groomerShown[i].y:F0}, {_groomerShown[i].z:F0})");
            var core = _resort.Core;
            float span = Mathf.Max(core.width, core.height);
            LookAt(core.center, span * 0.75f, 0, 55);
            yield return Shot(dir, "01-night-resort");
            if (working >= 0)
            {
                var at = new Vector2(_groomerShown[working].x, _groomerShown[working].z);
                foreach (var (dist, pitch, tag) in new[] { (400f, 45f, "far"), (150f, 32f, "near"), (60f, 45f, "close") })
                {
                    LookAt(at, dist, 30, pitch);
                    yield return Shot(dir, $"02-night-{tag}");
                }
                _groomerForceHover = working;
                LookAt(at, 150f, 30, 32);
                yield return Shot(dir, "03-night-hover");
                _groomerForceHover = -1;
            }
            // on to the morning: every groomer parked at the depot
            long before = _simNow.Tick;
            SendSim(SimCommandKind.Resume);
            SendSim(SimCommandKind.SkipToMorning);
            while (_simNow.Tick == before || _simNow.Skip != SkipKind.None) yield return null;
            SendSim(SimCommandKind.Pause);
            for (int i = 0; i < 120; i++) yield return null;
            LookAt(_depotAt, 120f, 30, 35);
            yield return Shot(dir, "04-morning-depot");
            _groomerForceHover = 0;
            yield return Shot(dir, "05-morning-depot-hover");
            _groomerForceHover = -1;
            Debug.Log("[GroomDots] done");
            Application.Quit(0);
        }

        IEnumerator GroomShots(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(1500));   // -simto first takes minutes; never hold the GPU past 25
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            float give = Time.unscaledTime + 120;
            while (!_simToRunning && Time.unscaledTime < give) yield return null;
            while (_simToRunning) yield return null;
            var snap = _simNow;
            if (snap == null || snap.TrailFineCells == 0 || _netGeometry == null) { Debug.LogError("[GroomShots] no trail snow"); Application.Quit(1); yield break; }
            // A night's grooming shows only on a morning it didn't snow on after the groom (an inch turns the surface back to
            // powder, SN-18 v2): skip to the next morning until a quarter of the trail squares read groomed (12 at most), or
            // exactly -groomskips N mornings, so the run without groomers lands on the same morning.
            var args = Environment.GetCommandLineArgs();
            int fi = Array.IndexOf(args, "-groomskips"), forced = -1;
            if (fi >= 0 && fi + 1 < args.Length) int.TryParse(args[fi + 1], out forced);
            int skips = 0;
            for (int tries = 0; tries < 12; tries++)
            {
                var s = _simNow;
                bool good = forced >= 0 ? skips >= forced : s.TrailBlocks > 0 && s.TrailBlocksGroomed * 4 >= s.TrailBlocks;
                Debug.Log($"[GroomShots] morning {s.FaceMs / SimWorld.DayMs}: {s.TrailBlocksGroomed} of {s.TrailBlocks} trail squares groomed, {s.TrailBlocksPowder} powder");
                if (good) break;
                long before = s.Tick;
                SendSim(SimCommandKind.Resume);
                SendSim(SimCommandKind.SkipToMorning);
                skips++;
                while (_simNow.Tick == before || _simNow.Skip != SkipKind.None) yield return null;
                SendSim(SimCommandKind.Pause);
                for (int i = 0; i < 30; i++) yield return null;
            }
            Debug.Log($"[GroomShots] {skips} skips to morning (pass -groomskips {skips} for the same morning)");
            for (int i = 0; i < 60; i++) yield return null;
            snap = _simNow;
            if (Camera != null) Camera.InputEnabled = false;
            for (int i = 0; i < 60; i++) yield return null;
            snap = _simNow;
            var g = snap.Grooming;
            int done = 0, notReached = 0, never = 0, tooBare = 0;
            for (int t = 0; t < g.LastState.Length; t++)
            {
                var s = g.LastState[t];
                if (s == GroomRowState.Done) done++;
                else if (s == GroomRowState.NotReached) notReached++;
                else if (s == GroomRowState.Never) never++;
                else if (s == GroomRowState.TooBare) tooBare++;
            }
            Debug.Log($"[GroomShots] {g.GroomerCount} groomers; last night: {done} groomed, {notReached} not reached, {tooBare} too bare, " +
                      $"{never} never; work {g.LastWorkMinutes} min, short {g.LastShortMinutes} min, ≈ ${g.LastCostUsd}; " +
                      $"tonight crews swing {g.CrewsSwing} night {g.CrewsNight}, want {g.WantMinutes} min");
            Debug.Log($"[GroomShots] {snap.TrailBlocks} trail squares, {snap.TrailBlocksGroomed} machine groomed, {snap.TrailBlocksPowder} powder; " +
                      $"last night's passes worked {snap.TrailBlocksLastGroom} on {snap.TrailsLastGroom} trails; mean depth {snap.TrailDepthMm / 25.4:F0} in");

            var core = _resort.Core;
            float span = Mathf.Max(core.width, core.height);
            var runs = Design.Document.Trails.Where(t => t.Stage == WorkStage.Built && t.Settings.Kind == TrailKind.Run)
                             .OrderByDescending(t => _netGeometry.CentrelineLengthMm(t)).Take(2).ToArray();

            SetLayerForCapture(MapLayers.SnowConditions, true);
            LookAt(core.center, span * 0.75f, 0, 55);
            yield return Shot(dir, "01-resort-conditions");
            SetLayerForCapture(MapLayers.SnowConditions, false);
            yield return Shot(dir, "02-resort-snow");

            int n = 3;
            foreach (var run in runs)
            {
                int length = Math.Max(1, _netGeometry.CentrelineLengthMm(run));
                var head = _netGeometry.PointAlong(run, 0);
                var tail = _netGeometry.PointAlong(run, length);
                var mid = _netGeometry.PointAlong(run, length / 2);
                var at = new Vector2((float)mid.XMetres, (float)mid.YMetres);
                var down = new Vector2((float)(tail.XMetres - head.XMetres), (float)(tail.YMetres - head.YMetres));
                float yaw = down.sqrMagnitude > 1 ? Yaw(-down.normalized) : 0;   // looking up the run from below
                float far = Mathf.Clamp(length / 1000f * 0.6f, 250, 900);
                string slug = new string((run.Name ?? "run").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
                Debug.Log($"[GroomShots] run {run.Name}: {length / 1000} m, middle ({at.x:F0}, {at.y:F0})");
                foreach (var (dist, pitch, tag) in new[] { (far, 45f, "far"), (120f, 28f, "near"), (40f, 18f, "surface") })
                {
                    SetLayerForCapture(MapLayers.SnowConditions, true);
                    LookAt(at, dist, yaw, pitch);
                    yield return Shot(dir, $"{n++:D2}-{slug}-{tag}-conditions");
                    SetLayerForCapture(MapLayers.SnowConditions, false);
                    yield return Shot(dir, $"{n++:D2}-{slug}-{tag}-snow");
                }
            }

            // The UI that shows grooming today (the Grooming tab is PR B): Analysis › Trails' Conditions column and a card.
            SetLayerForCapture(MapLayers.SnowConditions, true);
            LookAt(core.center, span * 0.75f, 0, 55);
            Hud.SetAnalysis(true);
            Hud.SetAnalysisTab("trails");
            yield return Shot(dir, $"{n++:D2}-analysis-trails");
            if (runs.Length > 0)
            {
                Hud.OpenTrailCardFor(TrailRowOf(runs[0].Id.Value, 0));
                yield return Shot(dir, $"{n++:D2}-trail-card");
                Hud.CloseTrailCardIfOpen();
            }
            Hud.SetAnalysis(false);
            Debug.Log("[GroomShots] done");
            Application.Quit(0);
        }
    }
}
