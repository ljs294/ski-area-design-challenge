using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.26 BB4 probe: <c>-linecheck &lt;out.txt&gt;</c> with <c>-drawnresort -guests N</c> puts the crowd on the hill
    /// and, twice a second for four minutes at 8×, counts each lift line's queued guests standing within 0.4 m of another
    /// (two in one place), with the line's length, its places and whether it can still grow. Then it quits.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>The -linelayout probe's lines (built with no dots, under -nographics).</summary>
        Presentation.Guests.LineLayout _probeLines;

        /// <summary>
        /// Task 4.26 BB4: every bottom line's places (P line k x z section-kind), its queued skiers (G line place rank shown
        /// x z) and the ground round its first places (W line x0 z0 columns rows, then a Q row of cells per 0.5 m, south
        /// first), for a plot.
        /// </summary>
        void DumpLines(string path, Presentation.Guests.LineLayout lines, bool guests, bool ground)
        {
            if (lines == null) return;
            var sb = new System.Text.StringBuilder();
            for (int l = 0; l < 12 && lines.Capacity(l) > 0; l++)
            {
                int seats = Math.Max(1, lines.SeatsOf(l)), k = 0;
                sb.AppendLine($"L {l} {seats}");
                float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
                for (int sct = 0; sct < lines.SectionCount(l); sct++)
                {
                    lines.Section(l, sct, out int rows, out bool file);
                    for (int e = 0; e < rows * seats; e++, k++)
                    {
                        if (!lines.PlaceOf(l, k, out var p)) continue;
                        sb.AppendLine($"P {l} {k} {p.x:F2} {p.z:F2} {(file ? "F" : "R")}{sct}");
                        if (k >= 800) continue;
                        x0 = Mathf.Min(x0, p.x); z0 = Mathf.Min(z0, p.z); x1 = Mathf.Max(x1, p.x); z1 = Mathf.Max(z1, p.z);
                    }
                }
                if (!ground || x0 > x1) continue;
                const float cell = Presentation.Guests.LineLayout.CellMetres, margin = 12f;
                x0 = Mathf.Floor((x0 - margin) / cell) * cell; z0 = Mathf.Floor((z0 - margin) / cell) * cell;
                int nx = Math.Min(400, Mathf.CeilToInt((x1 + margin - x0) / cell)), nz = Math.Min(400, Mathf.CeilToInt((z1 + margin - z0) / cell));
                sb.AppendLine($"W {l} {x0:F2} {z0:F2} {nx} {nz}");
                var row = new char[nx];
                for (int j = 0; j < nz; j++)
                {
                    for (int i = 0; i < nx; i++) row[i] = lines.GroundAt(l, x0 + (i + 0.5f) * cell, z0 + (j + 0.5f) * cell);
                    sb.Append("Q ").Append(row).AppendLine();
                }
            }
            for (int i = 0; guests && _skiers != null && i < _skiers.Count; i++)
            {
                if (!_skiers.RecordAt(i, out var rec) || rec.State != (byte)GuestState.Queued) continue;
                _skiers.Describe(i, out var dot, out _);
                sb.AppendLine($"G {rec.Index} {_skiers.LinePlaceAt(i)} {rec.Rank} {_skiers.ShownAt(i)} {dot.x:F2} {dot.z:F2}");
            }
            File.WriteAllText(path, sb.ToString());
            Debug.Log("[LineDump] " + path);
        }

        void StartLineCheck(string[] args)
        {
            int i = Array.IndexOf(args, "-linecheck");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(LineCheck(Path.GetFullPath(args[i + 1]), GuestsArgument(args, 3000)));
            i = Array.IndexOf(args, "-linelayout");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(LineLayoutProbe(Path.GetFullPath(args[i + 1])));
        }

        /// <summary>
        /// Task 4.26 BB4 probe: <c>-linelayout &lt;out.txt&gt;</c> (with <c>-nographics</c>: no GPU) lays the open design's
        /// lift lines as the game does, grows each to 1,500 places, writes them with the ground round them
        /// (<see cref="DumpLines"/>) and quits.
        /// </summary>
        IEnumerator LineLayoutProbe(string outPath)
        {
            StartCoroutine(QuitAfter(900));
            bool ready = false;
            for (float until = Time.realtimeSinceStartup + 600; !ready && Time.realtimeSinceStartup < until;)   // seconds: frames fly with no graphics
            {
                if (_resort != null && _resort.CoverReady.IsCompleted) EnsureTerrainEdits();
                ready = _resort != null && _resort.CoverReady.IsCompleted && _edits != null && !WorkBusy && _sections != null
                        && !NetworkBusy && _skierDesign?.Network != null && _skierGraph != null && _netGeometry != null && _edits?.Earthwork?.Site != null;
                if (!ready) yield return null;
            }
            for (int k = 0; k < 30; k++) yield return null;
            BuildSkierPaths(null);
            var lines = _probeLines;
            if (lines == null)
            {
                Debug.LogError($"[LineLayout] no layout (ready {ready}, design {_skierDesign != null}, graph {_skierGraph != null}, geometry {_netGeometry != null}, site {_edits?.Earthwork?.Site != null})");
                Application.Quit(1);
                yield break;
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // In turns, a growth each, as a busy morning grows them (every line claims its ground on every lift's grid).
            for (int g = 0; g < 40; g++)
                for (int l = 0; l < 12 && lines.Capacity(l) > 0; l++)
                {
                    if (lines.Capacity(l) >= 1500) continue;
                    long t0 = watch.ElapsedMilliseconds;
                    int was = lines.Capacity(l);
                    try { lines.Grow(l); }
                    catch (Exception e) { Debug.LogError($"[LineLayout] grow {g} lift {l} failed: {e.GetType().Name} {e.Message}"); }
                    long ms = watch.ElapsedMilliseconds - t0;
                    if (ms > 50) Debug.Log($"[LineLayout] grow {g} lift {l}: {was} to {lines.Capacity(l)} places in {ms} ms");
                }
            DumpLines(outPath, lines, guests: false, ground: true);
            Debug.Log($"[LineLayout] grown in {watch.ElapsedMilliseconds} ms; reversals {lines.Reversals}" + (lines.FirstReversal != null ? ": " + lines.FirstReversal : ""));
            for (int l = 0; l < 12 && lines.Capacity(l) > 0; l++)
            {
                Debug.Log("[LineLayout] " + lines.Describe(l));
                Debug.Log($"[LineLook] lift {l}: {lines.Measure(l)}");   // task 4.26 BB26: breaks, bends, density
            }
            lines.Dispose();
            _probeLines = null;
            Application.Quit(0);
        }

        IEnumerator LineCheck(string outPath, int crowd)
        {
            StartCoroutine(QuitAfter(1500));
            yield return BenchCrowd(crowd);
            _scriptedRun = true;
            SendSim(SimCommandKind.SetSpeed, 4);
            var lines = new List<string>();
            var at = new Dictionary<long, (int Rank, int Lift, int Record)>();
            int settled = 0, moving = 0, sameLane = 0, otherLane = 0, pastEnd = 0;
            var near = new List<string>();
            float until = Time.unscaledTime + 240;
            int samples = 0, samplesWithPairs = 0;
            var worst = new Dictionary<int, string>();
            var worstPairs = new Dictionary<int, int>();
            while (Time.unscaledTime < until)
            {
                for (int k = 0; k < 30; k++) yield return null;
                if (_skiers == null || _skiers.Lines == null) continue;
                samples++;
                at.Clear();
                var pairs = new Dictionary<int, int>();
                var queued = new Dictionary<int, int>();
                var pairRanks = new Dictionary<int, string>();
                for (int i = 0; i < _skiers.Count; i++)
                {
                    if (!_skiers.RecordAt(i, out var rec) || rec.State != (byte)GuestState.Queued) continue;
                    _skiers.Describe(i, out var dot, out _);
                    queued[rec.Index] = (queued.TryGetValue(rec.Index, out int q) ? q : 0) + 1;
                    long key = ((long)Mathf.RoundToInt(dot.x / 0.4f) << 32) ^ (uint)Mathf.RoundToInt(dot.z / 0.4f);
                    if (at.TryGetValue(key, out var other) && other.Lift == rec.Index)
                    {
                        pairs[rec.Index] = (pairs.TryGetValue(rec.Index, out int p) ? p : 0) + 1;
                        if (!pairRanks.ContainsKey(rec.Index)) pairRanks[rec.Index] = $"places {other.Rank} and {_skiers.LinePlaceAt(i)}";
                        bool onA = _skiers.Lines.PlaceOf(rec.Index, _skiers.LinePlaceAt(i), out var pa) && Vector3.Distance(new Vector3(dot.x, dot.y, dot.z), pa) < 0.15f;
                        bool onB = _skiers.Lines.PlaceOf(rec.Index, other.Rank, out var pb);
                        if (onA) settled++; else moving++;
                        int seats = Math.Max(1, _skiers.Lines.SeatsOf(rec.Index));
                        if (other.Rank % seats == _skiers.LinePlaceAt(i) % seats) sameLane++; else otherLane++;
                        int cap = _skiers.Lines.Capacity(rec.Index);
                        if (_skiers.LinePlaceAt(i) >= cap || other.Rank >= cap) pastEnd++;
                        if (near.Count < 40 && other.Rank % seats != _skiers.LinePlaceAt(i) % seats)
                        {
                            string Who(int j)
                            {
                                _skiers.RecordAt(j, out var rj); _skiers.Describe(j, out var dj, out _);
                                int pl = _skiers.LinePlaceAt(j), sh = _skiers.ShownAt(j);
                                _skiers.Lines.PlaceOf(rj.Index, pl, out var pp);
                                _skiers.Lines.PlaceOf(rj.Index, Math.Max(0, sh), out var ps);
                                _skiers.Lines.PlaceOf(rj.Index, pl + seats, out var pq);
                                return $"place {pl} lane {pl % seats} walking to {sh}: dot ({dj.x:F2},{dj.z:F2}) place ({pp.x:F2},{pp.z:F2}) one back ({pq.x:F2},{pq.z:F2})";
                            }
                            near.Add($"lift {rec.Index} two lanes: [{Who(other.Record)}] and [{Who(i)}]");
                        }
                        if (near.Count < 12) near.Add($"lift {rec.Index} places {other.Rank}/{_skiers.LinePlaceAt(i)}: dot {(onA ? "on its place" : "off its place by " + Vector3.Distance(new Vector3(dot.x, dot.y, dot.z), pa).ToString("F1") + " m")}, places {Vector3.Distance(pa, pb):F2} m apart");
                    }
                    else at[key] = (_skiers.LinePlaceAt(i), rec.Index, i);   // its place (4.26 BB4: row and column), not its rank
                }
                if (pairs.Count > 0) samplesWithPairs++;
                foreach (var kv in pairs)
                {
                    int lift = kv.Key;
                    if (worstPairs.TryGetValue(lift, out int w) && w >= kv.Value) continue;
                    worstPairs[lift] = kv.Value;
                    worst[lift] = $"lift {lift}: {kv.Value} pairs ({pairRanks[lift]}), {queued[lift]} in line, {_skiers.Lines.Capacity(lift)} places, " +
                                  $"can grow {_skiers.Lines.CanGrow(lift)}, grown {_skiers.LinesGrown} times, day {_simNow.FaceMs / SimWorld.DayMs} {_simNow.FaceMs % SimWorld.DayMs / 3_600_000:D2}h";
                }
            }
            lines.Add($"{samples} samples, {samplesWithPairs} with two guests in one place; of the pairs, {settled} on their own place, {moving} off it (walking up)");
            lines.Add($"pairs in one lane {sameLane}, in two lanes {otherLane}, a place past the line's end {pastEnd}; lanes given anew to skiers already in line {_skiers.ColumnResets}");
            // the layout itself: any two places of one line closer than 0.5 m
            for (int lift = 0; _skiers.Lines != null && lift < 12; lift++)   // the first dozen lines (Capacity is 0 past the last)
            {
                int n = _skiers.Lines.Capacity(lift), close = 0; string first = null;
                var cells = new Dictionary<long, int>();
                for (int k = 0; k < n; k++)
                {
                    _skiers.Lines.PlaceOf(lift, k, out var pk);
                    long key = ((long)Mathf.RoundToInt(pk.x / 0.5f) << 32) ^ (uint)Mathf.RoundToInt(pk.z / 0.5f);
                    if (cells.TryGetValue(key, out int j)) { close++; first ??= $"places {j} and {k}"; } else cells[key] = k;
                }
                string front = n > 0 && _skiers.Lines.PlaceOf(lift, 0, out var p0) ? $", front at ({p0.x:F0}, {p0.z:F0})" : "";
                lines.Add($"layout lift {lift}: {n} places, {close} within 0.5 m of another{(first != null ? " (first: " + first + ")" : "")}{front}");
            }
            lines.AddRange(near);
            foreach (var kv in worst) lines.Add(kv.Value);
            File.WriteAllLines(outPath, lines);
            foreach (var l in lines) Debug.Log("[LineCheck] " + l);
            Application.Quit(0);
        }
    }
}
