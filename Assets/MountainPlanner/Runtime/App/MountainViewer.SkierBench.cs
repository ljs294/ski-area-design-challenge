using System;
using System.Collections;
using System.IO;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.13's measurements (exit: 3,000 dots within the frame budget; pause and selection p95 ≤ 100 ms).
    /// - <c>-guests N</c> with <c>-benchmark</c>: the mountain opens, the day starts and N skiers are on the hill before the
    ///   camera path's warm-up lap, so the laps measure the frame with the crowd drawn.
    /// - <c>-pickbench &lt;out.json&gt;</c>: with a crowd of 3,000 (or <c>-guests N</c>), 200 picks of dots on screen (the
    ///   pick itself, then until the card has the worker's answer) and 50 pauses (until every dot has stopped), at 1×.
    /// </summary>
    public sealed partial class MountainViewer
    {
        // -skiertiming: each frame's skier work (TickSkiers: lifts, place job, draw, card), logged at the end. Preallocated.
        // -noskiers: none of it (the crowd still runs in the worker), to tell the screen's garbage from the worker's.
        float[] _skierMs;
        int _skierFrames;
        readonly System.Diagnostics.Stopwatch _skierWatch = new System.Diagnostics.Stopwatch();

        void TimedTickSkiers()
        {
            if (_skierMs == null)
            {
                if (_skierFrames == -2) return;   // -noskiers: the guests run, nothing of theirs is drawn (finding garbage)
                if (_skierFrames < 0) { TickSkiers(); return; }
                var args = Environment.GetCommandLineArgs();
                if (Array.IndexOf(args, "-noskiers") >= 0) { _skierFrames = -2; return; }
                if (Array.IndexOf(args, "-skiertiming") < 0) { _skierFrames = -1; TickSkiers(); return; }
                _skierMs = new float[1 << 18];
            }
            _skierWatch.Restart();
            TickSkiers();
            if (_skierFrames < _skierMs.Length) _skierMs[_skierFrames++] = (float)_skierWatch.Elapsed.TotalMilliseconds;
        }

        void LogSkierTiming()
        {
            if (_skierMs == null || _skierFrames <= 0) return;
            Percentiles(_skierMs, _skierFrames, out float p50, out float p95, out float max);
            Debug.Log($"[SkierTiming] {_skierFrames} frames: skier work p50 {p50:F3} ms, p95 {p95:F3} ms, max {max:F2} ms");
        }

        bool _benchLogged;

        /// <summary>The crowd's range and the proof's figures, once (at the benchmark's end, or on quitting).</summary>
        void LogSkierProof()
        {
            if (_benchLogged) return;
            _benchLogged = true;
            LogCrowdHeld();
            if (_skiers?.Proof != null) Debug.Log("[SkierProof] " + _skiers.Proof.Summary());
        }

        /// <summary><c>-dotstress N</c> (task 4.13 PR C2): the dots padded to N with copies, a render-only measurement; 0 off.</summary>
        static int DotStressArgument(string[] args)
        {
            int i = Array.IndexOf(args, "-dotstress");
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? Math.Clamp(n, 0, 1 << 16) : 0;
        }

        /// <summary><c>-skierproof [lod|lod0]</c> (task 4.13 PR C2): the stand-in figures near the camera; lod0 is the worst case.</summary>
        static bool SkierProofArgument(string[] args, out bool lod0)
        {
            int i = Array.IndexOf(args, "-skierproof");
            lod0 = i >= 0 && i + 1 < args.Length && string.Equals(args[i + 1], "lod0", StringComparison.OrdinalIgnoreCase);
            return i >= 0;
        }

        /// <summary>
        /// After a benchmark with a crowd: Traffic on for a moment to log the runs' crowding and the lifts' load at this
        /// hour, so the layer's colour breaks can be checked against a full hill (record only), then off. After the laps,
        /// so it never touches a measured frame.
        /// </summary>
        IEnumerator BenchTrafficCheck()
        {
            if ((_simNow?.Guests?.SkierCount ?? 0) == 0) yield break;
            SetLayerForCapture(MapLayers.Traffic, true);
            for (int k = 0; k < 90; k++) yield return null;
            LogTrafficNumbers("bench end");
            SetLayerForCapture(MapLayers.Traffic, false);
        }

        static int GuestsArgument(string[] args, int fallback)
        {
            int i = Array.IndexOf(args, "-guests");
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int n) ? Math.Max(0, n) : fallback;
        }

        /// <summary>Opens the mountain, lands on the morning, and puts <paramref name="count"/> skiers on the hill at 1×.</summary>
        IEnumerator BenchCrowd(int count)
        {
            if (count <= 0) yield break;
            // The same wait as the review capture's: the area fully open (cover, edits, network), not just the worker.
            bool ready = false;
            for (int k = 0; k < 120_000 && !ready; k++)
            {
                if (_resort != null && _resort.CoverReady.IsCompleted) EnsureTerrainEdits();
                ready = _resort != null && _resort.CoverReady.IsCompleted && _edits != null && !WorkBusy && _sections != null
                        && !NetworkBusy && _sim != null && _simNow != null && _skierDesign?.Network != null;
                if (!ready) yield return null;
            }
            if (_sim == null) { Debug.LogWarning("[SkierBench] no simulation: no crowd"); yield break; }
            if (!ready) Debug.LogWarning("[SkierBench] the area never finished opening; spawning the crowd anyway");
            for (int k = 0; k < 60; k++) yield return null;
            SendSim(SimCommandKind.SetMountainOpen, 1);
            SendSim(SimCommandKind.SkipToMorning);
            for (int k = 0; k < 600 && (_simNow == null || _simNow.Skip == SkipKind.None); k++) yield return null;
            for (int k = 0; k < 6000 && (_simNow == null || _simNow.Skip != SkipKind.None); k++) yield return null;
            SendSim(SimCommandKind.SetSpeed, 4);
            yield return WaitUntilFaceSecondOfDay(10 * 3600);
            // The day's own crowd is in by now; top it up to the count, spread over the bands the mountain offers something
            // to (an archetype is a band). GU-05 turns a band with nothing to ski away at the gate, so spawning it is
            // lost (the drawn resort has no green or blue a skier can reach: half the crowd went home, 1,525 of 3,000).
            // A few rounds, because parties and the free slots round the count.
            for (int round = 0; round < 4; round++)
            {
                if (!TopUpCrowd(count)) break;
                for (int k = 0; k < 300; k++) yield return null;
            }
            for (int k = 0; k < 300; k++) yield return null;
            SendSim(SimCommandKind.SetSpeed, 1);
            for (int k = 0; k < 120; k++) yield return null;
            Debug.Log($"[SkierBench] crowd: {_simNow?.Guests?.SkierCount ?? 0} on the hill, {_skiers?.Drawn ?? 0} dots drawn " +
                      $"(paths {_skierPathsMs:F1} ms, lines {_skierLinesMs:F1} ms)");
            StartCoroutine(HoldCrowd(count));
        }

        /// <summary>
        /// Sends spawns for the skiers short of <paramref name="count"/>, spread over the bands the mountain offers
        /// something to (an archetype is a band; GU-05 turns a band with nothing to ski away at the gate, so spawning it
        /// is lost: the drawn resort has no green or blue a skier can reach). False when nothing was sent.
        /// </summary>
        bool TopUpCrowd(int count)
        {
            var g = _simNow?.Guests;
            int want = Math.Max(0, count - (g?.SkierCount ?? 0));
            if (want == 0 || g == null || _sim == null) return false;
            int open = 0;
            for (int b = 0; b < Pacing.Bands; b++) if (g.UsableBases[b] > 0) open++;
            if (open == 0) { Debug.LogWarning("[SkierBench] no band has anything to ski: no crowd"); return false; }
            for (int b = 0; b < Pacing.Archetypes && want > 0; b++)
            {
                if (g.UsableBases[b] == 0) continue;
                int n = want / open--;
                if (open == 0) n = want;
                if (n > 0) _sim.Send(SimCommand.Of(SimCommandKind.SpawnSkiers, 0, new SkierSpawn(b, n)));
                want -= n;
            }
            return true;
        }

        int _crowdLow = int.MaxValue, _crowdHigh;

        /// <summary>
        /// Task 4.13 PR C2: through the measured laps the crowd stays at <paramref name="count"/>: skiers whose stay ends
        /// go home, so every few seconds the shortfall is spawned again (at 1× a lap is about two face hours, and the
        /// first measurement's crowd fell from 3,000 to 885 by its end). The low and high it saw go in the log.
        /// </summary>
        IEnumerator HoldCrowd(int count)
        {
            var wait = new WaitForSecondsRealtime(4f);
            while (this != null && _sim != null)
            {
                yield return wait;
                int now = _simNow?.Guests?.SkierCount ?? 0;
                if (now < _crowdLow) _crowdLow = now;
                if (now > _crowdHigh) _crowdHigh = now;
                if (now < count - 25) TopUpCrowd(count);
            }
        }

        void LogCrowdHeld()
        {
            if (_crowdHigh > 0) Debug.Log($"[SkierBench] the crowd held between {_crowdLow} and {_crowdHigh} on the hill (sampled every 4 s, topped up below the count)");
        }

        void StartPickBench(string[] args)
        {
            int i = Array.IndexOf(args, "-pickbench");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(PickBench(args[i + 1], GuestsArgument(args, 3000)));
        }

        [Serializable]
        sealed class PickReport
        {
            public int skiers, dots, picks, picksMatched, pauses;
            public float pickCpuP50Us, pickCpuP95Us, pickCpuMaxUs;
            public float selectP50Ms, selectP95Ms, selectMaxMs;
            public float pauseP50Ms, pauseP95Ms, pauseMaxMs;
            public bool pass;
        }

        IEnumerator PickBench(string outPath, int crowd)
        {
            BeginScriptedRun();
            yield return BenchCrowd(crowd);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            var report = new PickReport { skiers = _simNow?.Guests?.SkierCount ?? 0, dots = _skiers?.Drawn ?? 0 };
            var cpu = new float[200];
            var select = new float[200];
            int picks = 0, matched = 0;
            var watch = new System.Diagnostics.Stopwatch();
            for (int n = 0; n < 200 && _skiers != null; n++)
            {
                // A dot on screen, a different one each time (a keyed walk over the column).
                if (!_skiers.ScreenPointOf((n * 7919) % Math.Max(1, _skiers.Count), out var at, out uint expected)) { yield return null; continue; }
                watch.Restart();
                bool hit = _skiers.Pick(at, out uint serial, out _);
                cpu[picks] = (float)(watch.Elapsed.TotalMilliseconds * 1000.0);
                if (!hit) { yield return null; continue; }
                if (serial == expected) matched++;
                float t0 = Time.realtimeSinceStartup;
                SelectSkier(serial);
                for (int k = 0; k < 600 && (_simNow?.Guests == null || _simNow.Guests.SelectedSerial != (int)serial); k++) yield return null;
                FillSkierCard();   // what the next card tick does with the answer
                select[picks] = (Time.realtimeSinceStartup - t0) * 1000f;
                picks++;
                for (int k = 0; k < 3; k++) yield return null;
            }
            report.picks = picks;
            report.picksMatched = matched;
            Percentiles(cpu, picks, out report.pickCpuP50Us, out report.pickCpuP95Us, out report.pickCpuMaxUs);
            Percentiles(select, picks, out report.selectP50Ms, out report.selectP95Ms, out report.selectMaxMs);
            SelectSkier(0);

            var pause = new float[50];
            int pauses = 0;
            for (int n = 0; n < 50; n++)
            {
                SendSim(SimCommandKind.Resume);
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;) yield return null;
                float t0 = Time.realtimeSinceStartup;
                SendSim(SimCommandKind.Pause);
                // Stopped means the newest snapshot says paused, so the rope clock (and every dot on it) has stopped.
                for (int k = 0; k < 600 && (_simNow == null || !_simNow.Paused); k++) yield return null;
                pause[pauses++] = (Time.realtimeSinceStartup - t0) * 1000f;
            }
            report.pauses = pauses;
            Percentiles(pause, pauses, out report.pauseP50Ms, out report.pauseP95Ms, out report.pauseMaxMs);
            report.pass = report.selectP95Ms <= 100 && report.pauseP95Ms <= 100;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            Debug.Log($"[PickBench] {picks} picks ({matched} the dot aimed at), pick {report.pickCpuP95Us:F0} µs p95, select {report.selectP95Ms:F1} ms p95; " +
                      $"{pauses} pauses {report.pauseP95Ms:F1} ms p95 → {(report.pass ? "PASS" : "FAIL")} → {outPath}");
            Application.Quit();
        }

        // ---------- -proofhold: the crowd close up (task 4.13 PR C2) ----------

        void StartProofHold(string[] args)
        {
            int i = Array.IndexOf(args, "-proofhold");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(ProofHold(args[i + 1], GuestsArgument(args, 3000)));
        }

        [Serializable]
        sealed class ProofHoldReport
        {
            public int skiers;
            public string proof;
            public string lift;
            public int line;
            public ProofHoldView[] views;
        }

        [Serializable]
        sealed class ProofHoldView
        {
            public float distance;
            public int frames, figuresMean, figuresMost, nearMean, dotsDrawn;
            public float p50Ms, p95Ms, gpuP95Ms;
        }

        /// <summary>
        /// The benchmark path never comes close to the crowd (18 figures a frame on average with -skierproof), so the proof
        /// also parks the camera over the busiest line, its middle, at 25, 60 and 150 m, and times 15 s at each: the
        /// view a player has of a busy base, where real skier models would cost the most. Run with and without
        /// <c>-skierproof</c> to compare. Writes <paramref name="outPath"/> and quits.
        /// </summary>
        IEnumerator ProofHold(string outPath, int crowd)
        {
            BeginScriptedRun();
            yield return BenchCrowd(crowd);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            var s = _simNow;
            int best = -1, most = -1;
            for (int i = 0; s != null && i < s.LiftCount; i++) if (s.LineCount[i] > most) { most = s.LineCount[i]; best = i; }
            int lift = best >= 0 ? DesignLiftIndex(s.LiftIds[best]) : -1;
            Vector3 front = default, back = default;
            bool found = lift >= 0 && _skiers != null
                         && _skiers.TryFind(_skiers.FrontOfLine(lift), out front, out _) && _skiers.TryFind(_skiers.LastInLine(lift), out back, out _);
            if (!found) { Debug.LogWarning("[ProofHold] no line to look at"); Application.Quit(); yield break; }
            var middle = (front + back) * 0.5f;
            var along = back - front;
            float yaw = Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg + 70f;   // across the line, a little along it
            var report = new ProofHoldReport
            {
                skiers = s.Guests?.SkierCount ?? 0,
                proof = _skiers.Proof == null ? "dots" : _skiers.Proof.AllLod0 ? "lod0" : "lod",
                lift = _skierDoc?.FindLift(new MountainPlanner.Domain.Design.LiftId(s.LiftIds[best]))?.Name ?? "lift " + lift, line = most,
            };
            float[] distances = { 25f, 60f, 150f };
            report.views = new ProofHoldView[distances.Length];
            for (int v = 0; v < distances.Length; v++)
            {
                Camera.Frame(middle, distances[v]);
                Camera.SetAngles(yaw, distances[v] < 50 ? 18f : 26f);
                for (float until = Time.realtimeSinceStartup + 2f; Time.realtimeSinceStartup < until;) yield return null;
                var stats = new FrameStats(1 << 16);
                stats.Reset();
                long figures = 0, near = 0, dots = 0;
                int frames = 0, top = 0;
                for (float until = Time.realtimeSinceStartup + 15f; Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                    stats.Record(Time.unscaledDeltaTime, FrameStats.SampleGpuMs());
                    int f = _skiers.Proof?.Figures ?? 0;
                    figures += f; if (f > top) top = f;
                    near += _skiers.Proof?.CountIn(0) ?? 0;
                    dots += _skiers.Drawn;
                    frames++;
                }
                stats.Stop();
                var sum = stats.Summarise();
                stats.Dispose();
                report.views[v] = new ProofHoldView
                {
                    distance = distances[v], frames = sum.frames, p50Ms = sum.p50Ms, p95Ms = sum.p95Ms, gpuP95Ms = sum.gpuP95Ms,
                    figuresMean = (int)(figures / Math.Max(1, frames)), figuresMost = top,
                    nearMean = (int)(near / Math.Max(1, frames)), dotsDrawn = (int)(dots / Math.Max(1, frames)),
                };
                ScreenCapture.CaptureScreenshot(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".",
                                                             Path.GetFileNameWithoutExtension(outPath) + $"_{distances[v]:F0}m.png"));
                Debug.Log($"[ProofHold] {report.proof} at {distances[v]:F0} m over {report.lift}'s line ({most}): frame p95 {sum.p95Ms:F2} ms, " +
                          $"GPU p95 {sum.gpuP95Ms:F2} ms, {report.views[v].figuresMean} figures (most {top}, {report.views[v].nearMean} under 30 m), {report.views[v].dotsDrawn} dots");
            }
            for (int k = 0; k < 10; k++) yield return null;   // the last screenshot saves
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".");
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            LogSkierProof();
            Application.Quit();
        }

        static void Percentiles(float[] v, int n, out float p50, out float p95, out float max)
        {
            p50 = p95 = max = 0;
            if (n <= 0) return;
            var s = new float[n];
            Array.Copy(v, s, n);
            Array.Sort(s);
            p50 = s[(n - 1) / 2];
            p95 = s[Math.Min(n - 1, (int)Math.Ceiling(n * 0.95) - 1)];
            max = s[n - 1];
        }
    }
}
