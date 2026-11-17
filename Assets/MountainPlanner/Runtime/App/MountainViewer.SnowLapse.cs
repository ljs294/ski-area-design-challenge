using System;
using System.Collections;
using System.IO;

using MountainPlanner.Simulation;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 PR A2: <c>-snowlapse &lt;folder&gt; [-snowdays N]</c>, the snow's time-lapse. A new game from the bare October
    /// ground (no seed): each face day it skips to the morning, pauses, sets the sun to 12:30 (the view's own time; the clock
    /// stays where it is) and takes a still from the view the run opened with (<c>-view</c>), logging the snow over the core
    /// with it, then quits after <c>N</c> face days (45 by default: into February). The review-movie tool joins the stills.
    /// </summary>
    public sealed partial class MountainViewer
    {
        internal void StartSnowLapse(string[] args)
        {
            int i = Array.IndexOf(args, "-snowlapse");
            if (i < 0 || i + 1 >= args.Length) return;
            int d = Array.IndexOf(args, "-snowdays"), days = 45;
            if (d >= 0 && d + 1 < args.Length && int.TryParse(args[d + 1], out int n)) days = Mathf.Clamp(n, 1, 400);
            StartCoroutine(SnowLapse(Path.GetFullPath(args[i + 1]), days));
        }

        IEnumerator SnowLapse(string folder, int days)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _sim == null || _simNow == null) yield return null;
            float give = Time.unscaledTime + 120;
            while ((_simNow.Held || !_simNow.SnowLive) && Time.unscaledTime < give) yield return null;
            Debug.Log($"[SnowLapse] the worker's snow is {(_simNow.SnowLive ? "live" : "not live")}, {(_simNow.Held ? "still held" : "released")}");
            Directory.CreateDirectory(folder);
            for (int f = 0; f < 60; f++) yield return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var skips = new double[Math.Max(1, days - 1)];
            for (int shot = 0; shot < days; shot++)
            {
                if (shot > 0)
                {
                    // BG-02 v4: a skip to morning takes ≤ 5 s; timed from the command to the worker's snapshot after it.
                    var skip = System.Diagnostics.Stopwatch.StartNew();
                    yield return SkipAndWait(SimCommandKind.SkipToMorning);
                    skips[shot - 1] = skip.Elapsed.TotalSeconds;
                }
                SendSim(SimCommandKind.Pause);
                // The worker stages every changed tile while paused; give the screen a moment to take them all.
                for (int f = 0; f < 30 || (_resort.States.TilesWaiting > 0 && f < 600); f++) yield return null;
                var now = _simClock.Now;
                if (Lighting != null) Lighting.SetTime(new ViewTime(now.Year, now.DayOfYear, 12 * 3600 + 1800));
                for (int f = 0; f < 10; f++) yield return null;
                Debug.Log($"[SnowLapse] still {shot:D3}: {now} ({clock.Elapsed.TotalSeconds:F0} s) {SnowSummary()}");
                ScreenCapture.CaptureScreenshot(Path.Combine(folder, $"snow-{shot:D3}.png"));
                yield return null;
                yield return null;
            }
            Debug.Log($"[SnowLapse] {days} stills saved to {folder} in {clock.Elapsed.TotalSeconds:F0} s");
            if (days > 1)
            {
                var sorted = (double[])skips.Clone();
                Array.Sort(sorted);
                double total = 0;
                foreach (double s in sorted) total += s;
                Debug.Log($"[SnowLapse] the skips to morning (BG-02 v4: ≤ 5 s): {sorted.Length}, median {sorted[sorted.Length / 2]:F2} s, " +
                          $"p90 {sorted[Math.Min(sorted.Length - 1, sorted.Length * 9 / 10)]:F2} s, the most {sorted[sorted.Length - 1]:F2} s, all {total:F0} s; " +
                          $"each: {string.Join(" ", Array.ConvertAll(skips, s => s.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)))}");
            }
            // What a save of this winter would hold (BG-08 v3's review: the sim sidecar, its snow block the most of it).
            var save = System.Diagnostics.Stopwatch.StartNew();
            var state = SimStateForSave();
            Debug.Log($"[SnowLapse] a save's simulation state: {(state == null ? "none" : $"{state.Bytes.Length / 1e6:F1} MB")} in {save.ElapsedMilliseconds} ms: "
                      + $"waiting for the worker's copy {LastSaveWaitMs:F0} ms (a tick boundary, then the copy), the encoding on the save's task {LastSaveEncodeMs:F0} ms");
            var st = _resort?.States;
            if (st != null)
                Debug.Log($"[SnowLapse] the screen's tile uploads: the first frame {st.FirstUploadMs:F2} ms; then {st.UploadFrames} frames, mean "
                          + $"{st.UploadTotalMs / Math.Max(1, st.UploadFrames):F3} ms, {st.UploadFramesOverBudget} over 0.2 ms, {st.UploadFramesOverMs} over 1 ms, "
                          + $"the most {st.UploadMostLaterMs:F2} ms");
            Application.Quit();
        }

        /// <summary>The ground's grade at cell i, j of the snow field (per cent, from the heights 8 m either side).</summary>
        float GradeAt(Domain.Snow.SnowDepthField field, Rect ring, int i, int j)
        {
            float x = ring.xMin + (i + 0.5f) * field.CellMetres, z = ring.yMin + (j + 0.5f) * field.CellMetres, s = field.CellMetres;
            float gx = (_resort.Surface.HeightAt(x + s, z) - _resort.Surface.HeightAt(x - s, z)) / (2 * s), gz = (_resort.Surface.HeightAt(x, z + s) - _resort.Surface.HeightAt(x, z - s)) / (2 * s);
            return 100f * Mathf.Sqrt(gx * gx + gz * gz);
        }

        /// <summary>The screen's snow over the core: mean and deepest, the share covered (≥ SurfaceStates.FullCoverMetres), and the worker's last day.</summary>
        string SnowSummary()
        {
            var field = _resort?.States?.Snow;
            if (field == null) return "no snow field";
            var ring = _resort.Ring;
            var core = _resort.Core;
            double sum = 0, max = 0;
            int n = 0, covered = 0, maxI = 0, maxJ = 0;
            for (int j = 0; j < field.Height; j++)
            {
                float z = ring.yMin + (j + 0.5f) * field.CellMetres;
                if (z < core.yMin || z > core.yMax) continue;
                for (int i = 0; i < field.Width; i++)
                {
                    float x = ring.xMin + (i + 0.5f) * field.CellMetres;
                    if (x < core.xMin || x > core.xMax) continue;
                    float d = field[i, j];
                    sum += d; n++;
                    if (d > max) { max = d; maxI = i; maxJ = j; }
                    if (d >= Presentation.SurfaceStates.FullCoverMetres) covered++;
                }
            }
            // SN-06 v3's scale: the mean of the 16 × 16 cells (128 m) round the deepest.
            double local = 0; int ln = 0;
            for (int j = Math.Max(0, maxJ - 8); j < Math.Min(field.Height, maxJ + 8); j++)
                for (int i = Math.Max(0, maxI - 8); i < Math.Min(field.Width, maxI + 8); i++) { local += field[i, j]; ln++; }
            var l = _simNow.SnowLastDay;
            double cells = 1000.0 * field.Width * field.Height;   // the ledger sums µm over every cell: mm of water a cell
            return $"core: mean {sum / Math.Max(1, n) / 0.0254:F1} in, deepest {max / 0.0254:F0} in (cell {maxI}, {maxJ}; {ring.xMin + (maxI + 0.5f) * field.CellMetres:F0} m E, {ring.yMin + (maxJ + 0.5f) * field.CellMetres:F0} m N; grade {GradeAt(field, ring, maxI, maxJ):F0}%; the 128 m round it {local / Math.Max(1, ln) / 0.0254:F0} in), {100.0 * covered / Math.Max(1, n):F0}% covered; " +
                   $"last day {l.Day} (mm of water a cell over the field): fell {l.Fell / cells:F1}, melt {l.Melt / cells:F1}, runoff {l.Runoff / cells:F1}, " +
                   $"sublimated {l.Sublimated / cells:F1}, on the ground {l.End / cells:F1}; balance {l.Balance} µm";
        }
    }
}
