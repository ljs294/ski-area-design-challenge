using System;
using System.Collections;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// -trailsnowshots &lt;folder&gt; (task 4.07 B1 part 2a review), with -drawnresort -simto: once the season has skipped to
    /// its morning, the Snow conditions layer over the whole resort and over its two longest runs, each next to the same
    /// view without the layer. Every caption number comes from the worker's snapshot, logged beside its picture.
    /// </summary>
    public sealed partial class MountainViewer
    {
        void StartTrailSnowShots(string[] args)
        {
            int i = Array.IndexOf(args, "-trailsnowshots");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(TrailSnowShots(args[i + 1]));
        }

        IEnumerator TrailSnowShots(string dir)
        {
            Directory.CreateDirectory(dir);
            StartCoroutine(QuitAfter(1500));   // -simto first takes minutes; never hold the GPU past 25
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            float give = Time.unscaledTime + 120;
            while (!_simToRunning && Time.unscaledTime < give) yield return null;
            while (_simToRunning) yield return null;
            var snap = _simNow;
            if (snap == null || snap.TrailFineCells == 0 || _netGeometry == null) { Debug.LogError("[TrailSnowShots] no trail snow"); Application.Quit(1); yield break; }
            Lighting.SetTime(Lighting.Clock.Now.WithSecondOfDay(11 * 3600 + 30 * 60));
            if (Camera != null) Camera.InputEnabled = false;
            for (int i = 0; i < 60; i++) yield return null;
            Debug.Log($"[TrailSnowShots] {snap.TrailBlocks} trail squares, {snap.TrailBlocksGroomed} machine groomed, {snap.TrailBlocksPowder} powder; " +
                      $"the last night's pass worked {snap.TrailBlocksLastGroom} on {snap.TrailsLastGroom} trails; mean depth {snap.TrailDepthMm / 25.4:F0} in");

            var core = _resort.Core;
            var centre = core.center;
            float span = Mathf.Max(core.width, core.height);
            var runs = Design.Document.Trails.Where(t => t.Stage == WorkStage.Built && t.Settings.Kind == TrailKind.Run)
                             .OrderByDescending(t => _netGeometry.CentrelineLengthMm(t)).Take(2).ToArray();

            SetLayerForCapture(MapLayers.SnowConditions, true);
            LookAt(centre, span * 0.75f, 0, 55);
            yield return Shot(dir, "1-resort-conditions");
            SetLayerForCapture(MapLayers.SnowConditions, false);
            yield return Shot(dir, "2-resort-snow");

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
                float view = Mathf.Clamp(length / 1000f * 0.6f, 250, 900);
                string slug = new string((run.Name ?? "run").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
                Debug.Log($"[TrailSnowShots] run {run.Name}: {length / 1000} m, middle ({at.x:F0}, {at.y:F0}), view {view:F0} m");
                SetLayerForCapture(MapLayers.SnowConditions, true);
                LookAt(at, view, yaw, 45);
                yield return Shot(dir, $"{n++}-{slug}-conditions");
                SetLayerForCapture(MapLayers.SnowConditions, false);
                yield return Shot(dir, $"{n++}-{slug}-snow");
            }
            Debug.Log("[TrailSnowShots] done");
            Application.Quit(0);
        }
    }
}
