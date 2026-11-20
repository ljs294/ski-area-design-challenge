using System;
using System.Collections;
using System.Globalization;

using MountainPlanner.Simulation;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.07 PR A3: <c>-simto &lt;MM-DD&gt;</c>, the season without the drawing (owner, 2026-11-17: sim to mid-season, then
    /// look). The resort opens as usual; once the worker's snow is live the view stops drawing (the camera off, 10 frames a
    /// second, a plain line with the progress; End cancels) and the worker skips by Next month and Next third, then Skip to
    /// morning, until the date's morning. Then the drawing comes back, paused, to fly round and switch the Snow layer. It logs
    /// the real time it took and the snow (the base, the summit and the deepest cell). No seed: the snow the weather built
    /// from the bare October ground, unless the run also passes <c>-snowseed</c>.
    /// </summary>
    public sealed partial class MountainViewer
    {
        internal void StartSimTo(string[] args)
        {
            int i = Array.IndexOf(args, "-simto");
            if (i < 0 || i + 1 >= args.Length) return;
            var parts = args[i + 1].Split('-');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int month) || !int.TryParse(parts[1], out int day) || month < 1 || month > 12 || day < 1 || day > 31)
            {
                Debug.LogWarning($"[SimTo] '{args[i + 1]}' isn't a month-day (MM-DD, e.g. 02-01)");
                return;
            }
            _simToRunning = true;
            StartCoroutine(SimTo(month, day));
        }

        /// <summary>A <c>-simto</c> run hasn't reached its date yet (a capture or <c>-shellcheck</c> waits for it).</summary>
        bool _simToRunning;

        IEnumerator SimTo(int month, int day)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _sim == null || _simNow == null) yield return null;
            float give = Time.unscaledTime + 120;
            while ((_simNow.Held || !_simNow.SnowLive) && Time.unscaledTime < give) yield return null;
            var calendar = new LabelCalendar(Pacing.Default);
            int target = -1;
            for (int j = 0; j < calendar.YearFaceDays && target < 0; j++)
                if (calendar.Month(j) == month && calendar.Day(j) >= day) target = j;
            for (int j = 0; j < calendar.YearFaceDays && target < 0; j++)
                if (calendar.Month(j) == month) target = j;   // past the month's last label day: its last
            if (target < 0)
            {
                Debug.LogWarning($"[SimTo] {MonthName(month)} isn't in the label year");
                _simToRunning = false;
                yield break;
            }
            string goal = $"{MonthName(month)} {calendar.Day(target)}";
            int Now() => (int)(_simNow.FaceMs / SimWorld.DayMs % calendar.YearFaceDays);
            int start = Now();
            Debug.Log($"[SimTo] from {Label(calendar, start)} (face day {start}) to {goal} (face day {target}), snow {(_simNow.SnowLive ? "live" : "not live")}");

            var cam = UnityEngine.Camera.main;
            bool camWas = cam != null && cam.enabled;
            int rateWas = Application.targetFrameRate;
            if (cam != null) cam.enabled = false;
            Application.targetFrameRate = 10;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int skips = 0;
            bool cancelled = false;
            SendSim(SimCommandKind.Resume);
            while (!cancelled)
            {
                int now = Now();
                if (now >= target) break;
                var kind = calendar.NextMonthStart(now) <= target ? SimCommandKind.SkipToNextMonth
                         : calendar.NextThirdStart(now) <= target ? SimCommandKind.SkipToNextThird
                         : SimCommandKind.SkipToMorning;
                long before = _simNow.Tick;
                SendSim(kind);
                skips++;
                while (_simNow.Tick == before || _simNow.Skip != SkipKind.None)
                {
                    int done = Math.Max(0, Now() - start), all = Math.Max(1, target - start);
                    _toast = $"Simulating to {goal} · now {Label(calendar, Now())} · {100 * done / all} % · {clock.Elapsed.TotalSeconds:F0} s · End cancels";
                    _toastUntil = Time.unscaledTime + 1;
                    var keys = Keyboard.current;
                    if (keys != null && keys.endKey.wasPressedThisFrame)
                    {
                        SendSim(SimCommandKind.CancelSkip);
                        cancelled = true;
                    }
                    yield return null;
                }
            }
            SendSim(SimCommandKind.Pause);
            if (cam != null) cam.enabled = camWas;
            Application.targetFrameRate = rateWas;
            // The worker stages every changed tile while paused; give the screen a moment to take them all.
            for (int f = 0; f < 30 || (_resort.States.TilesWaiting > 0 && f < 600); f++) yield return null;
            Debug.Log($"[SimTo] {(cancelled ? "cancelled at" : "reached")} {Label(calendar, Now())} ({_simClock.Now}) in {clock.Elapsed.TotalSeconds:F0} s real time, {skips} skips, "
                      + $"{Math.Max(0, Now() - start)} face days; {SnowSummary()}; {BaseAndSummit()}");
            if (_simNow.TrailFineCells > 0)
                Debug.Log($"[TrailSnow] at {Label(calendar, Now())}: {_simNow.TrailBlocks} trail squares, {_simNow.TrailBlocksGroomed} machine groomed and {_simNow.TrailBlocksPowder} powder in the " +
                          $"Snow conditions layer; mean depth {_simNow.TrailDepthMm / 25.4:F0} in, {_simNow.TrailPackedPermille / 10}% packed, " +
                          $"{_simNow.TrailsGroomedToday} trails groomed today; the last night's pass worked {_simNow.TrailBlocksLastGroom} squares on {_simNow.TrailsLastGroom} trails");
            if (_simNow.TrailFineCells > 0) LogTrailBoard(Label(calendar, Now()));
            Toast(cancelled ? $"Stopped at {Label(calendar, Now())}" : $"{goal}: paused");
            _simToRunning = false;
        }

        static string MonthName(int month) => CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month);

        static string Label(LabelCalendar calendar, int faceDay) => $"{MonthName(calendar.Month(faceDay))} {calendar.Day(faceDay)}";

        /// <summary>The snow at the core's lowest and highest ground (the base and the summit), in inches.</summary>
        string BaseAndSummit()
        {
            var field = _resort?.States?.Snow;
            if (field == null) return "";
            var ring = _resort.Ring;
            var core = _resort.Core;
            float low = float.MaxValue, high = float.MinValue, lowDepth = 0, highDepth = 0;
            for (int j = 0; j < field.Height; j++)
            {
                float z = ring.yMin + (j + 0.5f) * field.CellMetres;
                if (z < core.yMin || z > core.yMax) continue;
                for (int i = 0; i < field.Width; i++)
                {
                    float x = ring.xMin + (i + 0.5f) * field.CellMetres;
                    if (x < core.xMin || x > core.xMax) continue;
                    float h = _resort.Surface.HeightAt(x, z);
                    if (h < low) { low = h; lowDepth = field[i, j]; }
                    if (h > high) { high = h; highDepth = field[i, j]; }
                }
            }
            return $"the base ({low / 0.3048f:F0} ft) {lowDepth / 0.0254f:F0} in, the summit ({high / 0.3048f:F0} ft) {highDepth / 0.0254f:F0} in";
        }

        /// <summary>
        /// Task 4.07 B1 part 2b: each trail's board at the end of the run, by name: its SN-20 v3 label, its walk (late season),
        /// its covered share and its snow rating, and whether it's open. Once per run, so the strings don't matter.
        /// </summary>
        static readonly string[] RatingWords = { "Poor", "Fair", "Good", "Excellent" };

        void LogTrailBoard(string when)
        {
            var s = _simNow;
            var doc = Design?.Current.Document;
            var line = new System.Text.StringBuilder();
            line.Append($"[TrailBoard] at {when}: {s.TrailsCovered} covered, {s.TrailsMarginal} marginal, {s.TrailsBare} bare, " +
                        $"{s.TrailsReady} open{(s.LateSeason ? ", late season" : "")}");
            for (int k = 0; k < s.TrailCount; k++)
            {
                string name = "?";
                if (doc != null)
                    foreach (var t in doc.Trails)
                        if (t.Id.Value == s.TrailIds[k]) { name = t.Name; break; }
                var label = s.TrailLabelAt(k);
                line.Append(System.Environment.NewLine).Append("  ").Append(name).Append(": ").Append(label == TrailSnowLabel.None ? "no snow squares" : label.ToString());
                if (s.TrailWalkFtAt(k) > 0) line.Append($" · walk {s.TrailWalkFtAt(k)} ft");
                line.Append($" · {s.TrailCoverPct[k]}% covered · {RatingWords[s.TrailRatingAt(k)]} · {s.TrailStatus[k]}");
            }
            Debug.Log(line.ToString());
        }
    }
}
