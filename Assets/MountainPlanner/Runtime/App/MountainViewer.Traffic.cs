using System;
using System.Collections.Generic;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// SN-31, the Traffic info layer (task 4.13 PR C): the worker's counters drawn over the dark relief, once a second.
    /// <list type="bullet">
    /// <item>Runs are coloured by skiers an hour per 100 ft of width, over the last hour or today's average hour.</item>
    /// <item>Lifts are coloured by how full their chairs ran, with a circle at the bottom that grows with the line.</item>
    /// <item>A label "42 · 1:45" gives the line and the wait.</item>
    /// <item>The pointer reads the run or lift under it.</item>
    /// <item>The dots step back, coloured as the legend says.</item>
    /// </list>
    /// Display only: nothing here touches the simulation.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>How far the dots fade back while Traffic is on (the selected and hovered one stay whole).</summary>
        const float TrafficDotDim = 0.55f;

        TrafficView _traffic;
        SkierRenderBuffers.Frame _skierFrame;
        float _trafficNext;
        bool _trafficWasOn;
        MountainPlanner.UI.LandmarkLabelOverlay _trafficLabels;
        readonly List<(string Name, Vector3 At)> _trafficLabelSpots = new List<(string, Vector3)>();
        string[] _trafficLabelText = Array.Empty<string>();
        int[] _designToSnapshot = Array.Empty<int>();

        /// <summary>The Traffic layer is what the ground shows (the Network view, when open, wins).</summary>
        bool TrafficOn => _layers != null && _layers.Info == MapLayers.InfoView.Traffic && !_networkOn;

        /// <summary>Once a frame, after the dots: the layer's lines follow the camera; its colours and labels once a second.</summary>
        void TickTraffic(Camera cam)
        {
            bool on = TrafficOn && _resort != null && !PhotoOpen && !_riding;
            if (on != _trafficWasOn)
            {
                _trafficWasOn = on;
                _mapLines?.SetVisible(_liftsShown && !_networkOn && !on);   // the layer's lift lines take their place
                if (!on) { _trafficKey = 0; Hud?.ClearTrafficReadout(); _trafficLabels?.SetLandmarks(Array.Empty<(string, Vector3)>()); }
                _trafficNext = 0;
            }
            if (_skiers != null)
            {
                _skiers.Dim = on ? TrafficDotDim : 0;
                _skiers.Mode = Hud != null ? (SkierLayer.ColourMode)Mathf.Clamp(Hud.DotColourMode, 0, 2) : SkierLayer.ColourMode.Ability;
            }
            if (!on)
            {
                if (_traffic != null) _traffic.Visible = false;
                return;
            }
            var paths = _skiers?.Paths;
            if (paths == null || _resort == null) return;
            if (_traffic != null && !_traffic.Alive) _traffic = null;   // a mountain closed: its root took the layer with it
            _traffic ??= new TrafficView(_resort.Root.transform);
            if (_traffic.Revision != paths.Revision) { _traffic.SetPaths(paths, PathEdges()); _trafficNext = 0; }
            _traffic.Visible = true;
            if (Time.unscaledTime >= _trafficNext)
            {
                _trafficNext = Time.unscaledTime + 1f;
                RecolourTraffic(paths);
            }
            _traffic.Follow(cam);
            _trafficLabels?.Update(cam, _resort.Surface, _hudShown);
        }

        /// <summary>Which of the design's edges are walking paths (no trail): drawn thin, never coloured.</summary>
        bool[] PathEdges()
        {
            var net = _skierDesign?.Network;
            if (net == null) return null;
            var paths = new bool[net.EdgeTrail.Length];
            for (int e = 0; e < paths.Length; e++) paths[e] = net.EdgeTrail[e] < 0;
            return paths;
        }

        /// <summary>The legend's Today, or past the stop (the last hour empties as the hill clears, so the day is read back).</summary>
        bool TrafficReadsToday(SimSnapshot s) => (Hud != null && Hud.TrafficToday) || s.SecondOfDay >= s.StopSecond;

        /// <summary>The hours the resort has been open today, for today's average hour (at least ten minutes, at most the day).</summary>
        static float HoursOpen(SimSnapshot s)
        {
            int day = Math.Max(600, s.StopSecond - s.OpenSecond);
            return Mathf.Clamp(s.SecondOfDay - s.OpenSecond, 600, day) / 3600f;
        }

        void RecolourTraffic(SkierPaths paths)
        {
            var f = _skierFrame;
            var s = _simNow;
            var design = _skierDesign;
            if (f == null || s == null || design == null || f.Revision != paths.Revision) return;
            bool today = TrafficReadsToday(s);
            Hud?.SetTrafficDayOver(s.SecondOfDay >= s.StopSecond);
            float hours = HoursOpen(s);
            int edges = Math.Min(f.EdgeCount, _traffic.RunCount);
            for (int e = 0; e < edges; e++)
            {
                float perHour = today ? f.EdgeToday[e] / hours : f.EdgeLastHour[e];
                _traffic.SetRunStep(e, TrafficView.RunStep(perHour, _traffic.WidthOf(e)));
            }
            // The snapshot's lifts are the design's, by id (both sorted), as the dots match them.
            if (_designToSnapshot.Length != design.Lifts.Length) _designToSnapshot = new int[design.Lifts.Length];
            if (_trafficLabelText.Length != design.Lifts.Length) _trafficLabelText = new string[design.Lifts.Length];
            var speeds = Pacing.Default.Speeds;
            int factor = s.Paused || s.Speed < 1 || s.Speed > speeds.Length ? 1 : speeds[s.Speed - 1];
            bool relabel = _trafficLabels == null || _trafficLabels.Count != design.Lifts.Length || _trafficLabelsRevision != paths.Revision;
            int k = 0;
            for (int i = 0; i < design.Lifts.Length; i++)
            {
                ulong id = design.Lifts[i].Id;
                while (k < s.LiftCount && s.LiftIds[k] < id) k++;
                int at = k < s.LiftCount && s.LiftIds[k] == id ? k : -1;
                _designToSnapshot[i] = at;
                int line = at >= 0 ? s.LineCount[at] : 0;
                float boarded = at < 0 || at >= f.LiftCount ? 0 : today ? s.RidesToday[at] / hours : f.LiftLastHour[at];
                float capacity = at >= 0 && at < f.LiftCount ? f.LiftHourCapacity[at] : 0;
                _traffic.SetLift(i, TrafficView.LoadStep(boarded, capacity), line);
                _trafficLabelText[i] = line > 0 ? LineText(line, (int)(s.WaitMs[at] / 1000 / factor)) : "";
                if (!relabel) _trafficLabels.SetText(i, _trafficLabelText[i]);
            }
            if (relabel) { LabelTrafficLines(); _trafficLabelsRevision = paths.Revision; }
        }

        long _trafficLabelsRevision = -1;
        readonly Dictionary<long, string> _lineTexts = new Dictionary<long, string>();

        /// <summary>"42 · 1:45", made once for each line and wait it shows (a long day sees a few hundred), so a label
        /// changing once a second allocates nothing once they've been seen.</summary>
        string LineText(int line, int waitSeconds)
        {
            long key = (long)Math.Min(line, 1 << 20) << 32 | (uint)Math.Max(0, waitSeconds);
            if (_lineTexts.TryGetValue(key, out var text)) return text;
            if (_lineTexts.Count >= 8192) _lineTexts.Clear();
            text = line.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture) + " · " + WaitClock(waitSeconds);
            _lineTexts[key] = text;
            return text;
        }

        /// <summary>One label per lift under its circle (empty with no line), made once per design; the text changes in place.</summary>
        void LabelTrafficLines()
        {
            if (Hud == null) return;
            _trafficLabelSpots.Clear();
            for (int i = 0; i < _trafficLabelText.Length; i++)
                _trafficLabelSpots.Add((_trafficLabelText[i], _traffic.LiftBottom(i, out var at) ? at : new Vector3(0, -100_000, 0)));
            // Under the circle (screen y grows down), and always shown, as the lift names are.
            _trafficLabels ??= new MountainPlanner.UI.LandmarkLabelOverlay(Hud.ContourLabelLayer, _labelPlacement, MountainPlanner.UI.LabelPlacement.Lifts,
                                                                           yields: false, nudges: new[] { new Vector2(0, 30) });
            _trafficLabels.SetLandmarks(_trafficLabelSpots);
        }

        // ---------- the readout under the pointer ----------

        /// <summary>From the HUD's 10 Hz update: the run or lift under the pointer, with its numbers, or nothing.</summary>
        void TrafficReadout(Camera cam, Vector2? pointer)
        {
            if (Hud == null) return;
            if (!TrafficOn || _traffic == null || !_traffic.Visible || cam == null || !pointer.HasValue || Hud.IsPointerOverPanel(pointer.Value))
            {
                NoTrafficReadout();
                return;
            }
            const float radius = 10f;
            int lift = _traffic.LiftAt(cam, pointer.Value, radius);
            int run = lift < 0 ? _traffic.RunAt(cam, pointer.Value, radius) : -1;
            var f = _skierFrame;
            var s = _simNow;
            var design = _skierDesign;
            if ((lift < 0 && run < 0) || f == null || s == null || design == null)
            {
                NoTrafficReadout();
                return;
            }
            bool today = TrafficReadsToday(s);
            if (lift >= 0)
            {
                int at = lift < _designToSnapshot.Length ? _designToSnapshot[lift] : -1;
                if (at < 0 || at >= f.LiftCount) { NoTrafficReadout(); return; }
                int line = s.LineCount[at];
                var speeds = Pacing.Default.Speeds;
                int factor = s.Paused || s.Speed < 1 || s.Speed > speeds.Length ? 1 : speeds[s.Speed - 1];
                int wait = (int)(s.WaitMs[at] / 1000 / factor);
                float boarded = today ? s.RidesToday[at] / HoursOpen(s) : f.LiftLastHour[at];
                int full = f.LiftHourCapacity[at] <= 0 ? 0 : Mathf.RoundToInt(boarded * 100f / f.LiftHourCapacity[at]);
                long key = 1L << 62 | (long)lift << 44 | (long)Math.Min(line, 65535) << 28 | (long)Math.Min(wait, 65535) << 12 | (long)Math.Min(full, 999) << 2 | (today ? 1L : 0);
                if (key != _trafficKey)
                {
                    _trafficKey = key;
                    string name = LiftName(lift, design, _skierDoc) ?? "Lift";
                    string figures = (line == 0 ? "no line" : line.ToString("#,0") + " in line · " + WaitClock(wait)) + " · " + full + "% full" + (today ? " today" : "");
                    Hud.SetTrafficReadout(key, InfoLegend.TrafficColours[InfoLegend.Step(full, InfoLegend.LoadBreaksPercent)], name, figures);
                }
                return;
            }
            if (run >= f.EdgeCount) { NoTrafficReadout(); return; }
            int on = f.EdgeOn[run], hour = f.EdgeLastHour[run], day = f.EdgeToday[run];
            long runKey = 2L << 62 | (long)run << 40 | (long)Math.Min(on, 4095) << 28 | (long)Math.Min(hour, 16383) << 14 | Math.Min(day, 16383);
            if (runKey != _trafficKey)
            {
                _trafficKey = runKey;
                string name = RunName(run, design, _skierDoc) ?? "Trail";
                bool path = design.Network != null && run < design.Network.EdgeTrail.Length && design.Network.EdgeTrail[run] < 0;
                string figures = on + " on it · " + hour.ToString("#,0") + " in the last hour · " + day.ToString("#,0") + " today";
                Hud.SetTrafficReadout(runKey, path ? new Color(0.62f, 0.66f, 0.72f) : InfoLegend.TrafficColours[_traffic.RunStepOf(run)], name, figures);
            }
        }

        long _trafficKey;

        // ---------- -guestshot: the layer for a review, with the numbers that set its steps ----------

        System.Collections.IEnumerator TrafficShots(string dir, string name)
        {
            if (_layers == null) yield break;
            _help = false;
            SetLayerForCapture(MapLayers.Traffic, true);
            HomeView();
            for (int k = 0; k < 90; k++) yield return null;
            _trafficNext = 0;
            for (int k = 0; k < 10; k++) yield return null;
            LogTrafficNumbers(name);
            // The pointer on the busiest run, so the shot shows the readout too.
            var cam = Camera != null ? Camera.GetComponent<Camera>() : null;
            var f = _skierFrame;
            if (cam != null && f != null && _traffic != null)
            {
                int best = -1, most = -1;
                for (int e = 0; e < Math.Min(f.EdgeCount, _traffic.RunCount); e++)
                    if (f.EdgeLastHour[e] > most && _traffic.WidthOf(e) > 0 && !(_skierDesign?.Network?.EdgeTrail[e] < 0)) { most = f.EdgeLastHour[e]; best = e; }
                if (best >= 0 && _traffic.RunMiddle(best, out var mid))
                {
                    var p = cam.WorldToScreenPoint(mid);
                    if (p.z > 0) SetPointerForCapture(new Vector2(p.x / Screen.width, p.y / Screen.height));
                }
            }
            for (int k = 0; k < 20; k++) yield return null;
            yield return GuestShot(dir, name);
            SetPointerForCapture(null);
            SetLayerForCapture(MapLayers.Traffic, false);
            _help = true;
        }

        void LogTrafficNumbers(string name)
        {
            var f = _skierFrame;
            var s = _simNow;
            if (f == null || s == null || _traffic == null) { Debug.Log("[Traffic] no frame yet"); return; }
            bool today = TrafficReadsToday(s);
            float hours = HoursOpen(s);
            var values = new List<float>();
            var steps = new int[InfoLegend.TrafficColours.Length];
            for (int e = 0; e < Math.Min(f.EdgeCount, _traffic.RunCount); e++)
            {
                if (_skierDesign?.Network != null && _skierDesign.Network.EdgeTrail[e] < 0) continue;
                float perHour = today ? f.EdgeToday[e] / hours : f.EdgeLastHour[e];
                values.Add(perHour * 30.48f / Mathf.Max(3f, _traffic.WidthOf(e)));
                steps[_traffic.RunStepOf(e)]++;
            }
            values.Sort();
            string Q(float q) => values.Count == 0 ? "-" : values[Mathf.Min(values.Count - 1, (int)(q * values.Count))].ToString("F0");
            var loads = new List<string>();
            for (int i = 0; i < f.LiftCount; i++)
                loads.Add(f.LiftHourCapacity[i] <= 0 ? "-" : Mathf.RoundToInt((today ? s.RidesToday[i] / hours : f.LiftLastHour[i]) * 100f / f.LiftHourCapacity[i]) + "%" + "(" + s.LineCount[i] + ")");
            Debug.Log($"[Traffic] {name}: {(today ? "today" : "last hour")}, {s.Guests?.SkierCount ?? 0} on the hill; runs per hour per 100 ft p10 {Q(0.1f)} p50 {Q(0.5f)} p90 {Q(0.9f)} max {Q(0.999f)} over {values.Count}; " +
                      $"steps {string.Join("/", steps)}; lifts full (line) {string.Join(" ", loads)}");
        }

        void NoTrafficReadout()
        {
            _trafficKey = 0;
            Hud?.SetTrafficReadout(0, default, null, null);
        }
    }
}
