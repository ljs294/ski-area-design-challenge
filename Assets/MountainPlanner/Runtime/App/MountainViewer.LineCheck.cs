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
        void StartLineCheck(string[] args)
        {
            int i = Array.IndexOf(args, "-linecheck");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(LineCheck(Path.GetFullPath(args[i + 1]), GuestsArgument(args, 3000)));
        }

        IEnumerator LineCheck(string outPath, int crowd)
        {
            StartCoroutine(QuitAfter(1500));
            yield return BenchCrowd(crowd);
            _scriptedRun = true;
            SendSim(SimCommandKind.SetSpeed, 4);
            var lines = new List<string>();
            var at = new Dictionary<long, (int Rank, int Lift)>();
            int settled = 0, moving = 0;
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
                        if (near.Count < 12) near.Add($"lift {rec.Index} places {other.Rank}/{_skiers.LinePlaceAt(i)}: dot {(onA ? "on its place" : "off its place by " + Vector3.Distance(new Vector3(dot.x, dot.y, dot.z), pa).ToString("F1") + " m")}, places {Vector3.Distance(pa, pb):F2} m apart");
                    }
                    else at[key] = (_skiers.LinePlaceAt(i), rec.Index);   // its place (4.26 BB4: row and column), not its rank
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
