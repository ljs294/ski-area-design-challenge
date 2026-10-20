using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The ground the trail tools shape (task 08): the terrain edit session started when a tool that needs it arms
    /// (P3-05 started it only for test flags and saves with edits), seen by the tools through <see cref="IEarthworkHost"/>.
    /// </summary>
    public sealed partial class MountainViewer
    {
        EarthworkHost _ground;
        TrailMapView _trailLayer;
        MountainPlanner.UI.LandmarkLabelOverlay _trailLabels;
        readonly List<(string Name, Vector3 At)> _trailLabelSpots = new List<(string, Vector3)>();

        /// <summary>The Trails map layer (task 08, TL6), once the ground's session is up.</summary>
        internal TrailMapView TrailLayer => _trailLayer;

        /// <summary>Redraws the Trails layer with the built work (from UpdateBuiltWork, once the edits are laid).</summary>
        void RebuildTrailLayer(DesignDocument doc, EditedHeights heights)
        {
            if (_resort == null) return;
            _trailLayer ??= new TrailMapView(_resort.Root.transform, _resort.Surface, HighlightMaterial);
            _trailLayer.Rebuild(doc, heights, _resort.Frame, _edits?.Earthwork, _trailLabelSpots, _resort.Surface);
            if (_trailLabels == null && Hud != null) _trailLabels = new MountainPlanner.UI.LandmarkLabelOverlay(Hud.ContourLabelLayer);
            _trailLabels?.SetLandmarks(_trailLabelSpots);
            FillTrailList(doc);
        }

        readonly List<MountainPlanner.UI.TrailRow> _trailRows = new List<MountainPlanner.UI.TrailRow>();
        bool _trailListWired;

        /// <summary>Analysis › Trails (task 08): one row per trail, from the Trails layer's figures.</summary>
        void FillTrailList(DesignDocument doc)
        {
            if (Hud == null || _trailLayer == null) return;
            if (!_trailListWired)
            {
                _trailListWired = true;
                Hud.TrailFocused += id => _trailLayer?.Focus(new TrailId(id));
                Hud.TrailEditChosen += id => ChooseTrailTool(EditTrailTool.ToolId, id);
                Hud.TrailRemoveChosen += id => ChooseTrailTool(RemoveTrailTool.ToolId, id);
            }
            _trailRows.Clear();
            foreach (var t in doc.Trails)
            {
                _trailLayer.TryGetFacts(t.Id, out var f);
                _trailRows.Add(new MountainPlanner.UI.TrailRow
                {
                    Id = t.Id.Value, Name = t.Name, RatingPercent = f.Rating, MeanPercent = f.Mean, SteepestPercent = f.Steepest,
                    LengthMetres = f.LengthMetres, VerticalMetres = Math.Abs(f.DropMetres),
                    Graded = t.Settings.Grading == TrailGrading.Graded, Built = t.Stage == WorkStage.Built,
                });
            }
            Hud.SetTrails(_trailRows);
        }

        /// <summary>For captures: a built trail on the map (New trail by script, then Break ground) if there's none yet.</summary>
        internal bool TrailBuiltForCapture()
        {
            if (Design == null || _tools == null) return false;
            if (Design.Document.Trails.Count > 0) return true;
            _captureTrails++;
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            bool built = TrailReviewForCapture() && _tools.Confirm(WorkStage.Built)?.Accepted == true;
            _tools.Close();
            Hud.SetToolbox(false);
            return built;
        }

        int _captureTrails;

        /// <summary>For captures: takes back the trails the captures built, so the design is as it was (and quitting asks nothing).</summary>
        internal void UndoCaptureTrails()
        {
            _tools?.Close();
            for (; _captureTrails > 0; _captureTrails--) Design?.Undo();
        }

        /// <summary>For captures: Edit or Remove trail armed on the last trail (built first if needed).</summary>
        internal bool TrailPickForCapture(string toolId)
        {
            if (!TrailBuiltForCapture()) return false;
            var trails = Design.Document.Trails;
            ChooseTrailTool(toolId, trails[trails.Count - 1].Id.Value);
            return _tools.Active is TrailTool tool && tool.Target != null;
        }

        /// <summary>The card's Edit or Remove (the mockup's fedit, fremove): the Trails tab, that tool, the trail chosen.</summary>
        void ChooseTrailTool(string toolId, ulong trail)
        {
            if (_tools == null || Hud == null) return;
            Hud.SetAnalysis(false);
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            if (_tools.ActiveId != toolId) _tools.Choose(toolId);
            var t = Design.Document.FindTrail(new TrailId(trail));
            if (_tools.Active is TrailTool tool && t != null) tool.ChooseTrail(t);
        }

        /// <summary>Once a frame, after the camera has moved: the layer follows the camera and the names their lines.</summary>
        void TickTrailLayer(UnityEngine.Camera cam)
        {
            if (_trailLayer == null) return;
            bool on = _layers == null || _layers.TrailsOn;
            if (_trailLayer.Visible != on) _trailLayer.Visible = on;
            if (cam != null) _trailLayer.Follow(cam);
            _trailLabels?.Update(cam, on && _hudShown && !_photo && (Hud == null || Hud.LabelsOn));
        }

        void DisposeTrailLayer()
        {
            _trailLayer?.Dispose();
            _trailLayer = null;
        }

        /// <summary>The tools' view of the terrain edit session.</summary>
        internal IEarthworkHost Ground => _ground ?? (_ground = new EarthworkHost(this));

        /// <summary>Starts the terrain edit session for the open design if it isn't running.</summary>
        void EnsureTerrainEdits()
        {
            if (_edits != null || _editsStarting || _resort == null) return;
            _editsStarting = true;
            StartCoroutine(StartEditsWhenReady(Array.Empty<string>(), -1, -1));
        }

        // ---------- -trailshots <folder>: the New trail tool driven by script, a picture per step (task 08 review) ----------

        /// <summary>While a scripted capture drives the armed tool, the pointer's own ticks wait.</summary>
        bool _toolScripted;

        /// <summary>
        /// Test trails in local metres (from the site's centre): the stroke path, a width per segment and erased islands;
        /// the same spots as the Domain review pictures (TrailPictures) on Jackson Hole 2 km.
        /// </summary>
        static readonly Dictionary<string, (float[] path, int[] feet, (float x, float z, int ft)[] islands)> TrailSpots =
            new Dictionary<string, (float[], int[], (float, float, int)[])>
            {
                ["sidehill"] = (new float[] { 200, -330, 225, -480, 235, -600, 250, -760 }, new[] { 100, 220, 100 }, new[] { (245f, -540f, 45) }),
                ["ridge"] = (new float[] { 183, 790, 280, 720, 377, 650 }, new[] { 120 }, new (float, float, int)[0]),
                ["gully"] = (new float[] { 271, -78, 360, -160, 449, -242 }, new[] { 120 }, new (float, float, int)[0]),
                ["steep"] = (new float[] { -650, 380, -660, 500, -650, 640 }, new[] { 150 }, new (float, float, int)[0]),
            };

        void StartTrailShots(string[] args)
        {
            int i = Array.IndexOf(args, "-trailshots");
            if (i < 0 || i + 1 >= args.Length) return;
            int s = Array.IndexOf(args, "-trailspot");
            StartCoroutine(TrailShots(args[i + 1], s >= 0 && s + 1 < args.Length ? args[s + 1] : "sidehill"));
        }

        ToolInput GroundInput(Vector2 xz, bool click = false, bool drag = false, bool release = false, bool erase = false)
        {
            float y = _resort.Surface.HeightAt(xz.x, xz.y);
            var g = new Vector3(xz.x, y, xz.y);
            return new ToolInput
            {
                HasGround = !float.IsNaN(y), Ground = g, Point = DesignFrame.ToDesign(g), InCore = _resort.Core.Contains(xz),
                MetresPerPixel = 0.5f, Click = click, Drag = drag, Release = release, Erase = erase,
            };
        }

        IEnumerator WaitTool(NewTrailTool trail)
        {
            for (int i = 0; i < 3000 && (trail.Analysing || _tools.Context.Running > 0); i++)
            {
                _tools.Tick(default);
                yield return null;
            }
            for (int i = 0; i < 3; i++) { _tools.Tick(default); yield return null; }
        }

        IEnumerator Shot(string dir, string name)
        {
            while (WorkBusy) yield return null;
            for (int i = 0; i < 45; i++) yield return null;
            string path = Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log("[TrailShots] " + path);
        }

        IEnumerator TrailShots(string dir, string spotName)
        {
            Directory.CreateDirectory(dir);
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (!TrailSpots.TryGetValue(spotName, out var spot)) { Debug.LogWarning("[TrailShots] unknown spot " + spotName); yield break; }
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            _tools.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_tools.Active;
            _toolScripted = true;
            while (!Ground.Ready) yield return null;

            // Paint: one stroke per segment at its width, then the islands erased.
            int n = spot.path.Length / 2;
            for (int k = 0; k + 1 < n; k++)
            {
                int feet = spot.feet.Length == 1 ? spot.feet[0] : spot.feet[k];
                while (trail.BrushFeet < feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
                while (trail.BrushFeet > feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                var a = new Vector2(spot.path[2 * k], spot.path[2 * k + 1]);
                var b = new Vector2(spot.path[2 * k + 2], spot.path[2 * k + 3]);
                _tools.Tick(GroundInput(a, click: true, drag: true));
                int steps = Mathf.CeilToInt(Vector2.Distance(a, b) / 5);
                for (int t = 1; t <= steps; t++) _tools.Tick(GroundInput(Vector2.Lerp(a, b, (float)t / steps), drag: true));
                _tools.Tick(GroundInput(b, release: true));
                yield return WaitTool(trail);
            }
            int lastFeet = trail.BrushFeet;
            foreach (var (x, z, ft) in spot.islands)
            {
                while (trail.BrushFeet > ft) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                _tools.Tick(GroundInput(new Vector2(x, z), click: true, erase: true));
                _tools.Tick(GroundInput(new Vector2(x, z), release: true, erase: true));
                yield return WaitTool(trail);
            }
            while (trail.BrushFeet < lastFeet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
            yield return Shot(dir, "1-paint");

            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            _tools.Tick(GroundInput(new Vector2(spot.path[0], spot.path[1]), click: true));
            _tools.Tick(GroundInput(new Vector2(spot.path[2 * n - 2], spot.path[2 * n - 1]), click: true));
            yield return WaitTool(trail);
            yield return Shot(dir, "2-natural");
            trail.ChooseGraded(true);
            yield return WaitTool(trail);
            yield return Shot(dir, "3-graded");
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return WaitTool(trail);
            yield return Shot(dir, "4-review");
            var r = _tools.Confirm(MountainPlanner.Domain.Design.WorkStage.Built);
            Debug.Log("[TrailShots] Break ground: " + r);
            yield return WaitTool(trail);
            _toolScripted = false;
            _tools.Close();
            Hud.SetToolbox(false);
            yield return Shot(dir, "5-built");
            // Analysis › Trails and the trail's card (TL8, TL16), then Edit trail on it (TF-07).
            var built = Design.Document.Trails.Count > 0 ? Design.Document.Trails[Design.Document.Trails.Count - 1] : null;
            Hud.SetAnalysis(true);
            Hud.SetAnalysisTab("trails");
            yield return Shot(dir, "6-analysis");
            if (built != null)
            {
                Hud.OpenTrailCardFor(built.Id.Value);
                yield return Shot(dir, "7-card");
                Hud.CloseTrailCardIfOpen();
                Hud.SetAnalysis(false);
                ChooseTrailTool(EditTrailTool.ToolId, built.Id.Value);
                yield return Shot(dir, "8-edit");
                _tools.Close();
                Hud.SetToolbox(false);
            }
            Design.Undo();
            yield return Shot(dir, "9-undone");
            Application.Quit();
        }

        // ---------- -trailbench <out.json>: the trail edit timing (task 08 acceptance, CF-09) ----------

        void StartTrailBench(string[] args)
        {
            int i = Array.IndexOf(args, "-trailbench");
            if (i >= 0 && i + 1 < args.Length) StartCoroutine(RunTrailBench(args[i + 1]));
        }

        /// <summary>A path down the fall line from a local point, every 25 m, about <paramref name="length"/> long.</summary>
        List<PointMm> FallLine(Vector2 from, float length)
        {
            var pts = new List<PointMm>();
            var at = from;
            for (float walked = 0; walked <= length; walked += 25)
            {
                pts.Add(PointMm.FromMetres(at.x, at.y));
                at += _edits.Downhill(at.x, at.y) * 25;
            }
            return pts;
        }

        /// <summary>
        /// The tool's whole analysis for a command, as New, Edit and Remove trail run it (the preview, CF-09): apply it to
        /// the design, fold the earthwork (TL3), the pending contours and the rating.
        /// </summary>
        DesignEarthwork.Result AnalyseForBench(DesignCommand cmd, out double ms)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var history = _edits.History;
            var after = cmd.Apply(history.Document);
            var rebuilt = _edits.Earthwork.Rebuild(after.Document, history.Terrain);
            var heights = _edits.Pipeline.Heights;
            var pending = new EditedHeights(heights.Source, heights.Lattice, rebuilt.Edits);
            // As the tool runs it: the contours across threads while the rating runs here.
            var contours = System.Threading.Tasks.Task.Run(() => TrailTool.Contours(heights, pending, rebuilt.Changed, UnitFormat.ContourIntervalMetres(DisplayUnits.Current)));
            if (after.Created.Count > 0 && rebuilt.DetailOf(after.Created[0]) is TrailBuild b && b.Grade != null)
                TrailRating.Rate(pending, b.Footprint, b.Grade);
            contours.Wait();
            ms = watch.Elapsed.TotalMilliseconds;
            return rebuilt;
        }

        IEnumerator RunTrailBench(string outPath)
        {
            while (_resort == null || Forest == null || !_resort.CoverReady.IsCompleted) yield return null;
            EnsureTerrainEdits();
            while (_edits == null || !_edits.EarthworkReady) yield return null;
            if (Camera != null) Camera.InputEnabled = false;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 60; i++) yield return null;
            const int spots = 10;
            var report = new TrailBenchReport();
            var shortPreview = new List<double>();
            var shortCommit = new List<double>();
            var straightPreview = new List<double>();
            var straightCommit = new List<double>();
            var mixedPreview = new List<double>();
            var mixedCommit = new List<double>();
            var edit = new List<double>();
            var undo = new List<double>();
            var history = _edits.History;
            // Spot -1 is a warm-up, not counted (the code's first run and the ground's first reads from disk, as the frame
            // benchmark's warm-up lap); then two rounds over the ten spots, so p95 isn't simply the slowest run.
            const int rounds = 2;
            for (int pass = -1; pass < spots * rounds; pass++)
            {
                int s = pass < 0 ? 0 : pass % spots;
                bool counted = pass >= 0;
                float angle = s * Mathf.PI * 2 / spots;
                var head = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 600f;
                var longPath = FallLine(head, 1000);
                var shortPath = FallLine(head, 300);
                foreach (int variant in new[] { 0, 1, 2 })
                {
                    // 0: 1,000 ft (300 m) at 100 ft, CF-09's own edit; 1: 1 km at 100 ft; 2: 1 km of mixed widths with an island.
                    bool mixed = variant == 2;
                    var path = variant == 0 ? shortPath : longPath;
                    var previewList = variant == 0 ? shortPreview : mixed ? mixedPreview : straightPreview;
                    var commitList = variant == 0 ? shortCommit : mixed ? mixedCommit : straightCommit;
                    var strokes = new List<Stroke>();
                    if (!mixed) strokes.Add(new Stroke(DesignUnits.FeetToMm(100), false, path.ToArray()));
                    else
                    {
                        // 100 and 200 ft stretches with an island of trees in the wide one.
                        for (int k = 0; k + 1 < path.Count; k += 8)
                        {
                            int end = Math.Min(path.Count - 1, k + 8);
                            strokes.Add(new Stroke(DesignUnits.FeetToMm((k / 8) % 2 == 0 ? 100 : 200), false, path.GetRange(k, end - k + 1).ToArray()));
                        }
                        strokes.Add(new Stroke(DesignUnits.FeetToMm(40), true, new[] { path[12] }));
                    }
                    var add = new AddTrail(history.Revision, "Bench " + s, TrailSettings.GradedRun, new[] { new FootprintPart(strokes.ToArray()) },
                                           path[0], path[path.Count - 1], WorkStage.Built);
                    var rebuilt = AnalyseForBench(add, out double ms);
                    if (counted) previewList.Add(ms);
                    yield return null;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    var r = history.Submit(add, rebuilt.Edits);
                    _edits.Earthwork.Accept(rebuilt);
                    while (!_edits.Applying.IsCompleted) yield return null;
                    yield return null;   // the frame that shows it
                    if (counted) commitList.Add(watch.Elapsed.TotalMilliseconds);
                    if (!r.Accepted) Debug.LogWarning("[TrailBench] rejected: " + r);
                    for (int i = 0; i < 60; i++) yield return null;   // the snow rebuild after an edit settles, as it would while a player paints
                    if (mixed && r.Accepted)
                    {
                        // An edit (Natural, then back): the earthwork rebuilds only what the trail touches.
                        var id = new TrailId(r.Created[0]);
                        var t = history.Document.FindTrail(id);
                        var change = new EditTrail(history.Revision, id, t.Name, t.Parts, t.Head.Value, t.Tail.Value, TrailSettings.NaturalRun, WorkStage.Built);
                        var re = AnalyseForBench(change, out double ems);
                        if (counted) edit.Add(ems);
                        history.Submit(change, re.Edits);
                        _edits.Earthwork.Accept(re);
                        while (!_edits.Applying.IsCompleted) yield return null;
                        history.Undo();
                        while (!_edits.Applying.IsCompleted) yield return null;
                    }
                    watch.Restart();
                    history.Undo();
                    while (!_edits.Applying.IsCompleted) yield return null;
                    yield return null;
                    if (counted) undo.Add(watch.Elapsed.TotalMilliseconds);
                    for (int i = 0; i < 60; i++) yield return null;   // the snow rebuild after an edit settles, as it would while a player paints
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
            report.spots = spots;
            report.lengthM = 1000;
            report.shortPreview = Stat.Of(shortPreview);
            report.shortCommit = Stat.Of(shortCommit);
            report.straightPreview = Stat.Of(straightPreview);
            report.straightCommit = Stat.Of(straightCommit);
            report.mixedPreview = Stat.Of(mixedPreview);
            report.mixedCommit = Stat.Of(mixedCommit);
            report.editPreview = Stat.Of(edit);
            report.undo = Stat.Of(undo);
            report.pass = report.straightPreview.p95 <= 100 && report.straightCommit.p95 <= 250;
            report.shortPass = report.shortPreview.p95 <= 100 && report.shortCommit.p95 <= 250;
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[TrailBench] 1,000 ft: preview p95 {7:F1} ms, commit p95 {8:F1} ms ({9}); 1 km 100 ft: preview p95 {0:F1} ms, commit p95 {1:F1} ms; mixed with an island: preview p95 {2:F1} ms, commit p95 {3:F1} ms; " +
                "edit preview p95 {4:F1} ms; undo p95 {5:F1} ms; {6}",
                report.straightPreview.p95, report.straightCommit.p95, report.mixedPreview.p95, report.mixedCommit.p95, report.editPreview.p95,
                report.undo.p95, report.pass ? "PASS" : "FAIL", report.shortPreview.p95, report.shortCommit.p95, report.shortPass ? "PASS" : "FAIL"));
            Application.Quit();
        }

        sealed class EarthworkHost : IEarthworkHost
        {
            readonly MountainViewer _viewer;
            public EarthworkHost(MountainViewer viewer) { _viewer = viewer; }

            public void Start() => _viewer.EnsureTerrainEdits();
            public bool Ready => _viewer._edits != null && _viewer._edits.EarthworkReady && _viewer._edits.History == _viewer.Design;
            public DesignEarthwork Earthwork => _viewer._edits?.Earthwork;
            public EditedHeights Heights => _viewer._edits?.Pipeline.Heights;
            public void ShowPending(List<double> segments) => _viewer._edits?.ShowPending(segments);
            public void ClearPending() => _viewer._edits?.ClearPreview();
            public double ContourInterval => UnitFormat.ContourIntervalMetres(DisplayUnits.Current);
        }
    }

    /// <summary>-trailbench's report (task 08): the trail edit timings against CF-09's budgets on a 1 km run.</summary>
    [Serializable]
    public sealed class TrailBenchReport
    {
        public string schema = "mountain-planner-trail-benchmark/1";
        public string commit, measuredUtc, gpu, cpu, site;
        public bool commitDirty, development, pass, shortPass;
        public int spots;
        public float lengthM;
        public Stat shortPreview, shortCommit, straightPreview, straightCommit, mixedPreview, mixedCommit, editPreview, undo;
        public float previewBudgetMs = 100, commitBudgetMs = 250;
    }
}
