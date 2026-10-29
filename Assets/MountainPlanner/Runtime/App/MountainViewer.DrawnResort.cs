using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Phase 3 task 11, the benchmark with a drawn resort (exit criteria 2 and 3):
    /// - <c>-drawnresort</c> builds <see cref="DrawnResort"/> (six lifts, twenty trails) into the open design by command
    ///   before anything is measured or captured, and the benchmark path flies six more legs over it (DB5);
    /// - <c>-drawnpathonly</c> flies the same legs over the bare mountain, for the design's cost on one build (A/B);
    /// - <c>-designbench &lt;out.json&gt;</c> times the edits a player makes with the whole design loaded (preview, commit,
    ///   undo), against CF-09's budgets (TL18), and lifts' builds reported beside them (DB6).
    /// Captures and benchmarks never open or save a player's saves (AppFlow's CaptureSwitches).
    /// </summary>
    public sealed partial class MountainViewer
    {
        bool _drawnResortBuilding;

        static bool DrawnResortWanted(string[] args) => Array.IndexOf(args, "-drawnresort") >= 0 || Array.IndexOf(args, "-designbench") >= 0;

        static bool DrawnLegsWanted(string[] args) => Array.IndexOf(args, "-drawnresort") >= 0 || Array.IndexOf(args, "-drawnpathonly") >= 0;

        /// <summary>Builds the drawn resort into the session's design once its earthwork is primed; the benchmark and captures wait for it.</summary>
        IEnumerator BuildDrawnResort()
        {
            _drawnResortBuilding = true;
            while (!_edits.EarthworkReady) yield return null;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var heights = _edits.Pipeline.Heights;
            var report = DrawnResort.Build(_edits.History, _edits.Earthwork, heights.Source, heights.Lattice, _resort.Frame,
                                           id => LiftTypes.Measured(LiftType.Of(id), Lifts));
            double buildMs = clock.Elapsed.TotalMilliseconds;
            while (!_edits.Applying.IsCompleted) yield return null;
            // The built models rebuild only once edits aren't busy (UpdateBuiltWork), so stop holding EditsBusy here; the
            // commits marked them dirty, so WorkBusy keeps everything waiting until they're in.
            _drawnResortBuilding = false;
            while (BuiltBusy) yield return null;
            Debug.Log($"[DrawnResort] {report.Accepted} built, {report.Rejected} rejected; design {buildMs / 1000:F1} s, ground and models {(clock.Elapsed.TotalMilliseconds - buildMs) / 1000:F1} s\n{report.Text}");
        }

        /// <summary>
        /// The legs over the drawn resort, worked out from its lines (nothing placed by hand): the whole resort from below,
        /// the first lift's bottom station, along the first graded run, beside the second lift's line, the first cat track's
        /// head, and the third lift's top. Targets on the ground, metres from the site's centre.
        /// </summary>
        List<BenchmarkPath.Leg> DrawnLegs()
        {
            Vector3 At(float x, float z) => new Vector3(x, _resort.Surface.HeightAt(x, z), z);
            float Yaw(float dx, float dz) => Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            var lifts = DrawnResort.Lifts;
            float ux = 0, uz = 0, cx = 0, cz = 0;
            foreach (var l in lifts)
            {
                ux += l.TopX - l.BottomX; uz += l.TopZ - l.BottomZ;
                cx += (l.TopX + l.BottomX) / 2; cz += (l.TopZ + l.BottomZ) / 2;
            }
            cx /= lifts.Length; cz /= lifts.Length;
            float uphill = Yaw(ux, uz);
            var first = lifts[0];
            var second = lifts[1];
            var third = lifts[2];
            var run = DrawnResort.Trails.First(r => r.Graded && !r.CatTrack);
            int k = run.Centreline.Length / 2 * 2 / 5 * 2;   // 40% of the way down
            float rdx = run.Centreline[k + 2] - run.Centreline[k], rdz = run.Centreline[k + 3] - run.Centreline[k + 1];
            var cat = DrawnResort.Trails.First(r => r.CatTrack);
            float cdx = cat.Centreline[6] - cat.Centreline[0], cdz = cat.Centreline[7] - cat.Centreline[1];
            float lineYaw = Yaw(second.TopX - second.BottomX, second.TopZ - second.BottomZ);
            // The close legs look uphill at their target, so the camera stands over lower ground, and each is raised
            // until its eye clears the ground and the trees (Clear).
            return new List<BenchmarkPath.Leg>
            {
                Clear("drawn-resort", At(cx, cz), 3200, uphill, 28),
                Clear("drawn-liftbase", At(first.BottomX, first.BottomZ), 260, Yaw(first.TopX - first.BottomX, first.TopZ - first.BottomZ) + 25, 20),
                Clear("drawn-graded", At(run.Centreline[k], run.Centreline[k + 1]), 300, Yaw(-rdx, -rdz), 22),
                Clear("drawn-liftline", At((second.BottomX + second.TopX) / 2, (second.BottomZ + second.TopZ) / 2), 160, lineYaw + 75, 14),
                Clear("drawn-cattrack", At(cat.Centreline[0], cat.Centreline[1]), 220, Yaw(cdx, cdz), 24),
                Clear("drawn-top", At(third.TopX, third.TopZ), 320, Yaw(third.TopX - third.BottomX, third.TopZ - third.BottomZ) - 20, 22),
            };
        }

        /// <summary>
        /// A leg whose orbit eye (the target, <paramref name="distance"/> back along the look) stands at least
        /// <see cref="EyeClearance"/> over the ground under it and under the sightline's near half, steepened 2° at a time.
        /// </summary>
        BenchmarkPath.Leg Clear(string name, Vector3 target, float distance, float yaw, float pitch)
        {
            for (; pitch < 80; pitch += 2)
            {
                var back = Quaternion.Euler(pitch, yaw, 0) * Vector3.back;
                bool clear = true;
                for (int i = 2; i <= 4 && clear; i++)
                {
                    var p = target + back * (distance * i / 4);
                    clear = p.y - _resort.Surface.HeightAt(p.x, p.z) >= EyeClearance * i / 4;
                }
                if (clear) break;
            }
            return new BenchmarkPath.Leg(name, target, distance, yaw, pitch);
        }

        const float EyeClearance = 70;   // m: over the tallest trees (about 45 m) with room for the hold's drift

        // ---------- -designbench <out.json>: edit timing with the whole resort loaded (task 11, CF-09, TL18) ----------

        void StartDesignBench(string[] args)
        {
            int i = Array.IndexOf(args, "-designbench");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(RunDesignBench(args[i + 1]));
        }

        /// <summary>
        /// With the drawn resort built (26 items, their ground folded, TL3), the edits a player makes, as the tools make
        /// them: the preview is the command applied and the design's earthwork rebuilt, with the contours and rating
        /// (<see cref="AnalyseForBench"/>); the commit is the command in and its ground on screen; undo takes it back.
        /// Gated (CF-09, TL18): new 1,000 ft and 1 km graded trails among the resort's, grading a built natural run.
        /// Reported (DB6): removing a run, and a new lift's build and commit. The same ten spots as -trailbench (600 m
        /// round the site's centre), so the numbers compare with task 08's on a bare mountain.
        /// </summary>
        IEnumerator RunDesignBench(string outPath)
        {
            while (_resort == null || Forest == null || !_resort.CoverReady.IsCompleted) yield return null;
            while (_edits == null || WorkBusy) yield return null;
            if (Camera != null) Camera.InputEnabled = false;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 60; i++) yield return null;
            var history = _edits.History;
            var report = new DesignBenchReport { trails = history.Document.Trails.Count, lifts = history.Document.Lifts.Count };
            var shortPreview = new List<double>();
            var shortCommit = new List<double>();
            var longPreview = new List<double>();
            var longCommit = new List<double>();
            var gradePreview = new List<double>();
            var gradeCommit = new List<double>();
            var removePreview = new List<double>();
            var removeCommit = new List<double>();
            var liftPreview = new List<double>();
            var liftCommit = new List<double>();
            var undo = new List<double>();
            int rejected = 0;

            // Commits a previewed command as Break ground does, timed to the frame that shows it; undo is timed the same way.
            IEnumerator Commit(DesignCommand cmd, DesignEarthwork.Result rebuilt, List<double> into, bool counted, Action<CommandResult> done)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var r = history.Submit(cmd, rebuilt.Edits);
                if (r.Accepted) _edits.Earthwork.Accept(rebuilt);
                while (!_edits.Applying.IsCompleted) yield return null;
                yield return null;
                if (counted) into.Add(watch.Elapsed.TotalMilliseconds);
                if (!r.Accepted) { rejected++; Debug.LogWarning("[DesignBench] rejected: " + r); }
                done?.Invoke(r);
                for (int i = 0; i < 60; i++) yield return null;   // the snow rebuild after an edit settles, as it would while a player works
            }
            IEnumerator Undo(bool counted)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                history.Undo();
                while (!_edits.Applying.IsCompleted) yield return null;
                yield return null;
                if (counted) undo.Add(watch.Elapsed.TotalMilliseconds);
                for (int i = 0; i < 60; i++) yield return null;
            }

            const int spots = 10, rounds = 2;
            var natural = history.Document.Trails.Where(t => t.Settings.Grading == TrailGrading.Natural).Select(t => t.Id).ToList();
            var builtTrails = history.Document.Trails.Select(t => t.Id).ToList();
            // Pass -1 is a warm-up (the code's first run, the ground's first reads), as -trailbench's.
            for (int pass = -1; pass < spots * rounds; pass++)
            {
                int s = pass < 0 ? 0 : pass % spots;
                bool counted = pass >= 0;
                float angle = s * Mathf.PI * 2 / spots;
                var head = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 600f;

                // New graded trails, 1,000 ft and 1 km at 100 ft, painted among the resort's.
                foreach (var (length, previewList, commitList) in new[] { (300f, shortPreview, shortCommit), (1000f, longPreview, longCommit) })
                {
                    var path = FallLine(head, length);
                    var shape = TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(100), false, path.ToArray()) });
                    var add = new AddTrail(history.Revision, "Bench " + s, TrailSettings.GradedRun, shape, path[0], path[path.Count - 1], WorkStage.Built);
                    var rebuilt = AnalyseForBench(add, out double ms);
                    if (counted) previewList.Add(ms);
                    yield return null;
                    CommandResult added = null;
                    yield return Commit(add, rebuilt, commitList, counted, r => added = r);
                    if (added != null && added.Accepted) yield return Undo(counted);
                }

                // Grading a built natural run of the resort (Graded ticked, NT47), then undo.
                {
                    var t = history.Document.FindTrail(natural[s % natural.Count]);
                    var grade = new EditTrail(history.Revision, t.Id, t.Name, t.Shape, t.Head.Value, t.Tail.Value, TrailSettings.GradedRun, WorkStage.Built);
                    var rebuilt = AnalyseForBench(grade, out double ms);
                    if (counted) gradePreview.Add(ms);
                    yield return null;
                    CommandResult graded = null;
                    yield return Commit(grade, rebuilt, gradeCommit, counted, r => graded = r);
                    if (graded != null && graded.Accepted) yield return Undo(counted);
                }

                // Removing a run (reported): the earthwork without it, then undo.
                {
                    var remove = new DeleteTrail(history.Revision, builtTrails[(s * 2 + pass / spots) % builtTrails.Count]);
                    var rebuilt = AnalyseForBench(remove, out double ms);
                    if (counted) removePreview.Add(ms);
                    yield return null;
                    CommandResult removed = null;
                    yield return Commit(remove, rebuilt, removeCommit, counted, r => removed = r);
                    if (removed != null && removed.Accepted) yield return Undo(counted);
                }

                // A new fixed-grip quad 600 m up the fall line from the spot (reported, DB6): the tool's build and the fold, then commit and undo.
                {
                    var type = LiftTypes.Measured(LiftType.Of(LiftCatalogIds.SesselliftFgq4), Lifts);
                    var down = _edits.Downhill(head.x, head.y);
                    var top = head - down * 600f;
                    var b = _resort.Frame.ToAlbers(head.x, head.y);
                    var tp = _resort.Frame.ToAlbers(top.x, top.y);
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var ground = new EditedHeights(_edits.Pipeline.Heights.Source, _edits.Pipeline.Heights.Lattice, history.Terrain);
                    var built = LiftBuild.Build(type, ground, b.X, b.Y, tp.X, tp.Y, TowerStance.Leaned, null, 0, 0, suggest: false);
                    var add = new AddLift(history.Revision, "Bench lift " + s, LiftCatalogIds.SesselliftFgq4, PointMm.FromMetres(head.x, head.y),
                                          PointMm.FromMetres(top.x, top.y), TowerMode.Automatic, TowerStance.Leaned, built.Towers, WorkStage.Built, built.Levels);
                    var after = add.Apply(history.Document);
                    var rebuilt = after.Accepted ? _edits.Earthwork.Rebuild(after.Document, history.Terrain) : null;
                    if (counted) liftPreview.Add(watch.Elapsed.TotalMilliseconds);
                    yield return null;
                    if (rebuilt != null)
                    {
                        CommandResult lifted = null;
                        yield return Commit(add, rebuilt, liftCommit, counted, r => lifted = r);
                        if (lifted != null && lifted.Accepted) yield return Undo(counted);
                    }
                }
            }

            var build = BuildInfo.Current;
            report.commit = build.commit;
            report.commitDirty = build.dirty;
            report.measuredUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            report.gpu = SystemInfo.graphicsDeviceName;
            report.cpu = SystemInfo.processorType;
            report.development = Debug.isDebugBuild;
            report.site = _resort.Manifest.Site.Name;
            report.screen = $"{Screen.width}x{Screen.height}";
            report.spots = spots;
            report.rounds = rounds;
            report.rejected = rejected;
            report.shortPreview = Stat.Of(shortPreview);
            report.shortCommit = Stat.Of(shortCommit);
            report.longPreview = Stat.Of(longPreview);
            report.longCommit = Stat.Of(longCommit);
            report.gradePreview = Stat.Of(gradePreview);
            report.gradeCommit = Stat.Of(gradeCommit);
            report.removePreview = Stat.Of(removePreview);
            report.removeCommit = Stat.Of(removeCommit);
            report.liftPreview = Stat.Of(liftPreview);
            report.liftCommit = Stat.Of(liftCommit);
            report.undo = Stat.Of(undo);
            bool Within(Stat preview, Stat commit) => preview.p95 <= report.previewBudgetMs && commit.p95 <= report.commitBudgetMs;
            report.shortPass = Within(report.shortPreview, report.shortCommit);
            report.longPass = Within(report.longPreview, report.longCommit);
            report.gradePass = Within(report.gradePreview, report.gradeCommit);
            report.pass = report.shortPass && report.longPass && report.gradePass && rejected == 0;
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[DesignBench] with {0} trails and {1} lifts: 1,000 ft preview p95 {2:F1} / commit {3:F1} ms; 1 km {4:F1} / {5:F1} ms; " +
                "grade a run {6:F1} / {7:F1} ms; remove a run {8:F1} / {9:F1} ms; new lift {10:F1} / {11:F1} ms; undo p95 {12:F1} ms; {13} rejected; {14}",
                report.trails, report.lifts, report.shortPreview.p95, report.shortCommit.p95, report.longPreview.p95, report.longCommit.p95,
                report.gradePreview.p95, report.gradeCommit.p95, report.removePreview.p95, report.removeCommit.p95, report.liftPreview.p95,
                report.liftCommit.p95, report.undo.p95, rejected, report.pass ? "PASS" : "FAIL"));
            Application.Quit();
        }
    }

    /// <summary>-designbench's report (task 11): edit timings with the drawn resort loaded, against CF-09's budgets (TL18).</summary>
    [Serializable]
    public sealed class DesignBenchReport
    {
        public string schema = "mountain-planner-design-edit-benchmark/1";
        public string commit, measuredUtc, gpu, cpu, site, screen;
        public bool commitDirty, development, pass, shortPass, longPass, gradePass;
        public int trails, lifts, spots, rounds, rejected;
        public Stat shortPreview, shortCommit, longPreview, longCommit, gradePreview, gradeCommit;
        public Stat removePreview, removeCommit, liftPreview, liftCommit, undo;
        public float previewBudgetMs = 100, commitBudgetMs = 250;
    }
}
