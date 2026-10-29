using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The terrain brush's captures and bench (task 15, demo.bat 73b and 73m), on Jackson Hole 2 km's sidehill:
    /// <c>-brushtour &lt;folder&gt;</c> builds a quad, then uses each brush beside and under it (Raise/Lower at the bottom
    /// terminal and under the line, Flatten for a junction, Smooth with a Revert, Slope for a cat track closed through the
    /// Confirm / Revert ask; BR17, BR18), shooting each mid-stroke, pending and confirmed; with <c>-brushmovie</c> it also saves every frame (15 a second) for
    /// PickerLabSetup.EncodeMovies; <c>-brushat dx,dz</c> moves the whole tour (metres) to check a second spot. <c>-brushbench &lt;out.json&gt;</c> times the live ground while dragging a 60 ft and a 400 ft
    /// brush, each frame in its parts (demo 75), the frames after letting go and the confirm. Captures never open or save the player's saves (AppFlow CaptureRun).
    /// </summary>
    public sealed partial class MountainViewer
    {
        // The sidehill (local metres from the site's centre): the quad's ends and where each brush works.
        static readonly Vector2 BrushLiftBottom = new Vector2(240, -760), BrushLiftTop = new Vector2(205, -340);

        string _brushFrames;
        Vector2 _brushShift;   // -brushat: the tour's offset, applied wherever its points meet the ground
        int _brushFrame;

        void StartBrushRuns(string[] args)
        {
            int tour = Array.IndexOf(args, "-brushtour");
            int shift = Array.IndexOf(args, "-brushat");
            if (shift >= 0 && shift + 1 < args.Length)
            {
                var parts = args[shift + 1].Split(',');
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                if (parts.Length == 2 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float dx)
                    && float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float dz))
                    _brushShift = new Vector2(dx, dz);
            }
            if (tour >= 0 && tour + 1 < args.Length) { StartCoroutine(BrushTour(args[tour + 1], Array.IndexOf(args, "-brushmovie") >= 0)); return; }
            if (Array.IndexOf(args, "-noseed") >= 0) TerrainBrushTool.SeedFolds = false;   // PR C's before: every fold from scratch
            int bench = Array.IndexOf(args, "-brushbench");
            if (bench >= 0 && bench + 1 < args.Length) StartCoroutine(BrushBench(args[bench + 1]));
        }

        // A tick of input for a scripted stroke: a 30th of a second, either button.
        ToolInput BrushInput(Vector2 xz, bool left = false, bool right = false, bool click = false, bool release = false)
        {
            xz += _brushShift;
            float y = _resort.Surface.HeightAt(xz.x, xz.y);
            var g = new Vector3(xz.x, y, xz.y);
            return new ToolInput
            {
                HasGround = !float.IsNaN(y), Ground = g, Point = DesignFrame.ToDesign(g), InCore = _resort.Core.Contains(xz), MetresPerPixel = 0.5f,
                DeltaSeconds = 1f / 30,
                Click = left && click, Drag = left && !release, Release = left && release,
                RightClick = right && click, RightDrag = right && !release, RightRelease = right && release,
            };
        }

        IEnumerator BrushFrame()
        {
            if (_brushFrames == null) { yield return null; yield break; }
            yield return new WaitForEndOfFrame();
            var shot = ScreenCapture.CaptureScreenshotAsTexture();
            File.WriteAllBytes(Path.Combine(_brushFrames, $"f_{_brushFrame * 1000 / 15:D6}.jpg"), shot.EncodeToJPG(85));
            Destroy(shot);
            _brushFrame++;
        }

        // A drag from a to b at `metresPerTick`, holding `hold` ticks at the end (Raise/Lower keeps working while held).
        IEnumerator BrushDrag(Vector2 a, Vector2 b, bool right, float metresPerTick, int hold, string dir = null, string midShot = null)
        {
            _tools.Tick(BrushInput(a, left: !right, right: right, click: true));
            yield return BrushFrame();
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / metresPerTick));
            for (int t = 1; t <= steps + hold; t++)
            {
                _tools.Tick(BrushInput(Vector2.Lerp(a, b, Mathf.Min(1, t / (float)steps)), left: !right, right: right));
                yield return BrushFrame();
                if (midShot != null && t == steps) yield return BrushShot(dir, midShot);
            }
            _tools.Tick(BrushInput(b, left: !right, right: right, release: true));
            for (int i = 0; i < 20; i++) yield return BrushFrame();
        }

        IEnumerator BrushShot(string dir, string name)
        {
            for (int i = 0; i < 20; i++) yield return null;
            string path = Path.Combine(dir, name + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log("[BrushTour] " + path);
        }

        // Confirm (BR17): the session folds and goes in; no review page.
        IEnumerator BrushConfirm(TerrainBrushTool tool)
        {
            _tools.Confirm(WorkStage.Built);
            for (int i = 0; i < 3000 && (tool.Confirming || EditsBusy); i++) { _tools.Tick(default); yield return BrushFrame(); }
        }

        // The quad the brushes work round, built through the history and the earthwork as the lift tool builds one.
        bool BuildBrushLift()
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var heights = Ground.Heights;
            Vector2 bottom = BrushLiftBottom + _brushShift, top = BrushLiftTop + _brushShift;
            var a = _resort.Frame.ToAlbers(bottom.x, bottom.y);
            var b = _resort.Frame.ToAlbers(top.x, top.y);
            var built = LiftBuild.Build(type, heights, a.X, a.Y, b.X, b.Y, TowerStance.Plumb);
            var history = Design;
            var cmd = new AddLift(history.Revision, "Sidehill Quad", type.CatalogId, DesignFrame.ToDesign(bottom), DesignFrame.ToDesign(top),
                                  TowerMode.Automatic, TowerStance.Plumb, built.Towers, WorkStage.Built, built.Levels);
            var after = cmd.Apply(history.Document);
            if (!after.Accepted) { Debug.LogWarning("[BrushTour] the quad: " + after.Reason); return false; }
            var rebuild = Ground.Earthwork.Rebuild(after.Document, history.Terrain);
            if (!history.Submit(cmd, rebuild.Edits).Accepted) return false;
            Ground.Earthwork.Accept(rebuild);
            return true;
        }

        void BrushLook(Vector2 at, float distance)
        {
            if (Camera == null) return;
            at += _brushShift;
            Camera.Frame(new Vector3(at.x, _resort.Surface.HeightAt(at.x, at.y), at.y), distance);
            Camera.SetAngles(250, 38);
        }

        // Where the quad's tower stands (its base, local metres), from the lift's own earthwork.
        Vector2 BrushTowerBase(int index)
        {
            var lift = Design.Document.Lifts.Count > 0 ? Design.Document.Lifts[0] : null;
            var rope = lift != null ? (Ground.Earthwork.DetailOf(lift.Id.Value) as LiftBuild.Works)?.Rope : null;
            if (rope == null || index >= rope.Towers.Count) return Vector2.Lerp(BrushLiftBottom, BrushLiftTop, 0.4f);
            float f = (float)(rope.Towers[index].BaseS / rope.LengthMetres);
            return Vector2.Lerp(BrushLiftBottom, BrushLiftTop, f);
        }

        IEnumerator BrushTour(string dir, bool movie)
        {
            Directory.CreateDirectory(dir);
            if (movie) Directory.CreateDirectory(_brushFrames = Path.Combine(dir, "brushes"));
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (Camera != null) Camera.InputEnabled = false;
            BrushLook(new Vector2(235, -600), 640);
            Ground.Start();
            while (!Ground.Ready) yield return null;
            if (!BuildBrushLift()) { Debug.LogWarning("[BrushTour] no quad at this spot"); Application.Quit(); yield break; }
            for (int i = 0; i < 30; i++) yield return BrushFrame();
            yield return BrushShot(dir, "0-lift");
            var tower = BrushTowerBase(2);
            Hud.SetToolboxTab("terrain");
            Hud.SetToolbox(true);
            _toolScripted = true;

            // Raise/Lower: lower hard beside the bottom terminal (held by its pad, TB-11), then raise under the line (TB-12).
            _tools.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_tools.Active;
            tool.SetBrush(120, 100);
            for (int i = 0; i < 10; i++) { _tools.Tick(default); yield return BrushFrame(); }
            BrushLook(new Vector2(220, -720), 230);
            yield return BrushDrag(new Vector2(205, -735), new Vector2(205, -690), right: true, 0.8f, 40, dir, "1-lower-live");
            BrushLook(tower, 230);
            yield return BrushDrag(tower + new Vector2(-12, 0), tower + new Vector2(12, 0), right: true, 0.4f, 60, dir, "2a-lower-at-tower");
            BrushLook(new Vector2(228, -560), 260);
            yield return BrushDrag(new Vector2(190, -560), new Vector2(265, -560), right: false, 0.8f, 30, dir, "2-raise-live");
            yield return BrushShot(dir, "3-raise-pending");
            yield return BrushConfirm(tool);
            yield return BrushShot(dir, "4-raise-confirmed");
            for (int i = 0; i < 30; i++) yield return BrushFrame();

            // Flatten: a junction's pad at the ground's height there.
            _tools.Choose(TerrainIds.Flatten);
            tool = (TerrainBrushTool)_tools.Active;
            tool.SetBrush(160, 100);
            BrushLook(new Vector2(312, -620), 260);
            var at = new Vector2(310, -620);
            _tools.Tick(BrushInput(at, right: true, click: true));
            _tools.Tick(BrushInput(at, right: true, release: true));
            yield return BrushDrag(new Vector2(290, -640), new Vector2(335, -600), right: false, 0.6f, 20, dir, "5-flatten-live");
            yield return BrushShot(dir, "6-flatten-pending");
            yield return BrushConfirm(tool);
            for (int i = 0; i < 30; i++) yield return BrushFrame();

            // Smooth.
            _tools.Choose(TerrainIds.Smooth);
            tool = (TerrainBrushTool)_tools.Active;
            tool.SetBrush(200, 100);
            BrushLook(new Vector2(168, -480), 260);
            yield return BrushDrag(new Vector2(150, -500), new Vector2(185, -460), right: false, 0.5f, 30, dir, "7-smooth-live");
            // Revert (BR17): the session's ground goes back; then smooth again and confirm.
            _tools.Confirm(WorkStage.Plan);
            for (int i = 0; i < 20; i++) yield return BrushFrame();
            yield return BrushShot(dir, "7b-smooth-reverted");
            yield return BrushDrag(new Vector2(150, -500), new Vector2(185, -460), right: false, 0.5f, 30);
            yield return BrushConfirm(tool);
            for (int i = 0; i < 30; i++) yield return BrushFrame();

            // Slope: a cat track down the sidehill to a target near the bottom; the grade follows from the two points (BR16).
            _tools.Choose(TerrainIds.Slope);
            tool = (TerrainBrushTool)_tools.Active;
            tool.SetBrush(60, 100);
            var target = new Vector2(330, -760);
            BrushLook(new Vector2(312, -580), 520);
            _tools.Tick(BrushInput(target, right: true, click: true));
            _tools.Tick(BrushInput(target, right: true, release: true));
            yield return BrushDrag(new Vector2(300, -420), new Vector2(325, -720), right: false, 1.2f, 0, dir, "8-slope-live");
            // BR18: closing with the slope not confirmed asks Confirm / Revert / Keep painting; Enter confirms, and the tool closes.
            _tools.Escape();
            yield return BrushShot(dir, "9-slope-ask");
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            for (int i = 0; i < 3000 && _tools.Armed; i++) { _tools.Tick(default); yield return BrushFrame(); }
            for (int i = 0; i < 45; i++) yield return BrushFrame();
            _toolScripted = false;
            Hud.SetToolbox(false);
            BrushLook(new Vector2(235, -600), 640);
            yield return BrushShot(dir, "10-done");
            Debug.Log($"[BrushTour] done: {Design.Document.Strokes.Count} strokes in the design");
            _brushFrames = null;
            Application.Quit();
        }

        [Serializable]
        public sealed class BrushBenchReport
        {
            public string schema = "mountain-planner-brush-benchmark/3";
            public string commit, measuredUtc, gpu, cpu, site;
            public BrushPass brush60, brush400;
            /// <summary>From asking to confirm to the ground committed, ms (before BR17: Break ground's review, then Confirm).</summary>
            public double reviewMs, confirmMs, toCommittedMs;
            /// <summary>BR21: the frames from Confirm until it has landed and settled, and from Revert on (30 frames), ms.</summary>
            public Timing confirmFrames, revertFrames;
            /// <summary>The built lifts and trail lines rebuilt after Confirm, ms on the main thread.</summary>
            public double confirmBuiltMs;
            /// <summary>Confirm's parts, ms: the fold's Apply and Rebuild, the click to the fold landing, and the commit apply's wait in the queue and its own time.</summary>
            public double confirmFoldApplyMs, confirmFoldRebuildMs, confirmLandedMs, confirmQueuedMs, confirmApplyMs;
            /// <summary>Confirm clicked the frame after letting go of a 400 ft stroke (its fold has only just started), ms to committed, and the frames until then.</summary>
            public double toCommittedOnLetGoMs;
            public Timing confirmOnLetGoFrames;
            /// <summary>PR C: Confirm the frame after the third press of a session, ms to committed, its fold's parts and the frames.</summary>
            public double toCommittedThreePressesMs, threePressFoldApplyMs, threePressFoldRebuildMs;
            public Timing confirmThreePressesFrames;
            /// <summary>BR22: towers the brush moved live over the whole bench, and towers Confirm then moved (0: the brush had moved them).</summary>
            public int towersMovedLive, towersMovedAtConfirm = -1;
            /// <summary>BR21: values on screen that differ from a fresh apply of the committed ground, after Confirm and after Revert (0 = none).</summary>
            public int shownDiffersAfterConfirm = -1, shownDiffersAfterRevert = -1;
            public string shownNote;
            public bool pass;
        }

        /// <summary>One drag (demo 75): the frame and each part of it, ms, and the frames just after letting go.</summary>
        [Serializable]
        public sealed class BrushPass
        {
            public int feet;
            public Timing frame, gpu, live, dabs, preview, bill, sample, set, sync, release, trees, towers;
            /// <summary>BR22: trees stood again or taken out live over the drag.</summary>
            public long treesMovedLive;
            /// <summary>Drag frames longer than one 60 Hz refresh (16.7 ms): each one is a dropped frame (round 4's target is none).</summary>
            public int framesOverRefresh;
            public long gcBytesPerFrame, gcBytesMaxFrame, gcFramesWithAllocations, heapGrowthBytes;
            public int gcCollections;
            /// <summary>The first drag frames that allocated (Development game): seconds into the drag, and bytes, to find the source.</summary>
            public float[] gcEventSeconds;
            public long[] gcEventBytes;
            /// <summary>Letting go: the stroke's tiles built and handed over on the main thread, ms.</summary>
            public double finishMs, finishPreviewMs;
        }

        [Serializable]
        public struct Timing { public double p50, p95, max; public int n; }

        /// <summary>One refresh of the owner's 60 Hz monitor, ms: a frame longer than this is a dropped frame.</summary>
        public const double RefreshMs = 1000.0 / 60;

        static Timing Summarise(List<double> ms)
        {
            if (ms.Count == 0) return default;
            ms.Sort();
            return new Timing { p50 = ms[ms.Count / 2], p95 = ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(ms.Count * 0.95) - 1)], max = ms[ms.Count - 1], n = ms.Count };
        }

        bool _brushBenching;

        IEnumerator BrushBench(string outPath)
        {
            _brushBenching = true;
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (Camera != null)
            {
                Camera.InputEnabled = false;
                var focus = new Vector2(240, -580) + _brushShift;
                Camera.Frame(new Vector3(focus.x, _resort.Surface.HeightAt(focus.x, focus.y), focus.y), 900);
                Camera.SetAngles(250, 45);
            }
            Ground.Start();
            while (!Ground.Ready) yield return null;
            if (!BuildBrushLift()) yield break;
            LiveTowersMoved = 0;
            Hud.SetToolboxTab("terrain");
            Hud.SetToolbox(true);
            _toolScripted = true;
            _tools.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_tools.Active;
            var pipeline = _edits?.Pipeline;
            Presentation.Edits.TerrainEditPipeline.Verbose = true;   // every apply in the log, to read the frames against
            var report = new BrushBenchReport { site = _brushShift == Vector2.zero ? "jh2km" : $"jh2km {_brushShift.x:F0},{_brushShift.y:F0}" };
            var stats = new FrameStats(4096);
            for (int pass = 0; pass < 2; pass++)
            {
                int feet = pass == 0 ? 60 : 400;
                tool.SetBrush(feet, 60);
                for (int i = 0; i < 30; i++) { _tools.Tick(default); yield return null; }
                var parts = new List<double>[11];
                for (int k = 0; k < parts.Length; k++) parts[k] = new List<double>(256);
                var a = new Vector2(120, -450 - pass * 150);
                var b = new Vector2(360, -450 - pass * 150);
                _tools.Tick(BrushInput(a, left: true, click: true));
                yield return null;
                stats.Reset();
                long treesMoved = 0;
                int steps = 120;   // 4 s at 30 a second, 2 m a tick
                for (int t = 1; t <= steps; t++)
                {
                    _tools.Tick(BrushInput(Vector2.Lerp(a, b, t / (float)steps), left: true));
                    var tick = tool.LastTick;
                    parts[0].Add(pipeline?.LastLiveMs ?? 0);
                    parts[1].Add(tick.Dabs);
                    parts[2].Add(tick.CatchUp);   // the catch-up's main-thread start (its Preview and enqueue), every 0.2 s
                    parts[3].Add(tick.Bill);
                    parts[4].Add(pipeline?.LastLiveSampleMs ?? 0);
                    parts[5].Add(pipeline?.LastLiveSetMs ?? 0);
                    parts[6].Add(pipeline?.LastLiveSyncMs ?? 0);
                    parts[9].Add(pipeline?.LastLiveTreesMs ?? 0);
                    parts[10].Add(LastLiveTowersMs);
                    treesMoved += pipeline?.LastLiveTrees ?? 0;
                    yield return null;
                    float gpu = FrameStats.SampleGpuMs();
                    stats.Record(Time.unscaledDeltaTime, gpu);
                    float frameMs = Time.unscaledDeltaTime * 1000;
                    parts[7].Add(frameMs);
                    if (gpu > 0) parts[8].Add(gpu);
                    if (frameMs > RefreshMs)
                        Debug.Log($"[BrushBench] frame {Time.frameCount - 1}: {frameMs:F1} ms while painting {feet} ft, tick {t} (live {pipeline?.LastLiveMs ?? 0:F1}, dabs {tick.Dabs:F1}, catch-up {tick.CatchUp:F1}, bill {tick.Bill:F1}, GPU {gpu:F1}, unsynced {pipeline?.LiveUnsynced ?? 0})");
                }
                stats.Stop();
                var release = new List<double>(30);
                _tools.Tick(BrushInput(b, left: true, release: true));
                double finishMs = tool.LastFinishMs, finishPreviewMs = tool.LastFinishPreviewMs;
                for (int i = 0; i < 30; i++)
                {
                    int gcBefore = GC.CollectionCount(0);
                    _tools.Tick(default);   // as a frame of the game: the tool keeps ticking after the let-go
                    yield return null;
                    float ms = Time.unscaledDeltaTime * 1000;
                    release.Add(ms);
                    if (ms > 20) Debug.Log($"[BrushBench] frame {Time.frameCount - 1}: {ms:F1} ms after letting go ({GC.CollectionCount(0) - gcBefore} collections, {GC.CollectionCount(0)} so far)");
                }
                var sum = stats.Summarise();
                var bp = new BrushPass
                {
                    feet = feet, live = Summarise(parts[0]), dabs = Summarise(parts[1]), preview = Summarise(parts[2]), bill = Summarise(parts[3]),
                    sample = Summarise(parts[4]), set = Summarise(parts[5]), sync = Summarise(parts[6]), frame = Summarise(parts[7]), gpu = Summarise(parts[8]),
                    release = Summarise(release),
                    gcBytesPerFrame = sum.gcBytesPerFrame, gcBytesMaxFrame = sum.gcBytesMaxFrame, gcFramesWithAllocations = sum.gcFramesWithAllocations,
                    heapGrowthBytes = sum.heapGrowthBytes, gcCollections = sum.gcCollections, finishMs = finishMs, finishPreviewMs = finishPreviewMs,
                    gcEventSeconds = sum.gcEventSeconds, gcEventBytes = sum.gcEventBytes,
                    trees = Summarise(parts[9]), towers = Summarise(parts[10]), treesMovedLive = treesMoved,
                };
                foreach (double ms in parts[7]) if (ms > RefreshMs) bp.framesOverRefresh++;
                if (pass == 0) report.brush60 = bp; else report.brush400 = bp;
            }
            stats.Dispose();
            yield return BenchConfirm(tool, report);
            report.commit = BuildInfo.Current.commit;
            report.measuredUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            report.gpu = SystemInfo.graphicsDeviceName;
            report.cpu = SystemInfo.processorType;
            // Round 4 (owner, 2026-10-29: a brush like Cities Skylines 2's on a 60 Hz monitor): the 400 ft drag at 12 ms p95 with no frame
            // over a refresh, and none after letting go, Confirm or Revert. The garbage is reported beside it (docs/perf/brush-drag.md
            // round 4: about 80 KB a frame and 2 collections in the Development game, neither costing a frame; the catch-ups' tiles and lists).
            report.pass = report.brush400.frame.p95 <= 12 && report.brush400.framesOverRefresh == 0 && report.brush60.live.p95 <= 8 && report.brush400.live.p95 <= 8
                          && report.toCommittedMs <= 150 && report.toCommittedOnLetGoMs <= 250 && report.confirmOnLetGoFrames.max <= RefreshMs
                          && report.toCommittedThreePressesMs <= 250 && report.confirmThreePressesFrames.max <= RefreshMs
                          && report.brush400.release.max <= RefreshMs && report.confirmFrames.max <= RefreshMs && report.revertFrames.max <= RefreshMs
                          && report.shownDiffersAfterConfirm == 0 && report.shownDiffersAfterRevert == 0;
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var p in new[] { report.brush60, report.brush400 })
                Debug.Log(string.Format(inv,
                    "[BrushBench] {0} ft: frame p95 {1:F1} max {18:F1} ms, {19} over a refresh (GPU {2:F1}); live {3:F1} = sample {4:F1} + set {5:F1} + sync {6:F1} + trees {20:F1} ({22:N0} moved); towers p95 {21:F1}; dabs {7:F1}; catch-up max {8:F1}; bill p95 {9:F1} max {10:F1}; release frame p95 {11:F1} max {12:F1} (finish {16:F1}, tiles {17:F1}); GC {13} B/frame in {14} frames, {15} collections",
                    p.feet, p.frame.p95, p.gpu.p95, p.live.p95, p.sample.p95, p.set.p95, p.sync.p95, p.dabs.p95, p.preview.max, p.bill.p95, p.bill.max,
                    p.release.p95, p.release.max, p.gcBytesPerFrame, p.gcFramesWithAllocations, p.gcCollections, p.finishMs, p.finishPreviewMs, p.frame.max, p.framesOverRefresh,
                    p.trees.p95, p.towers.p95, p.treesMovedLive));
            Debug.Log(string.Format(inv, "[BrushBench] confirm frames p95 {0:F1} max {1:F1} (built {2:F1} ms, {8} towers moved; {9} moved live); revert frames p95 {3:F1} max {4:F1}; shown vs fresh: {5} after Confirm, {6} after Revert{7}",
                report.confirmFrames.p95, report.confirmFrames.max, report.confirmBuiltMs, report.revertFrames.p95, report.revertFrames.max,
                report.shownDiffersAfterConfirm, report.shownDiffersAfterRevert, report.shownNote != null ? " (" + report.shownNote + ")" : "",
                report.towersMovedAtConfirm, report.towersMovedLive));
            Debug.Log(string.Format(inv, "[BrushBench] to committed {0:F0} ms (fold landed at {1:F0}: apply {2:F0}, rebuild {3:F0}; commit apply queued {4:F0}, took {5:F0}); on let-go {6:F0} ms (frames max {7:F1}); {8}",
                report.toCommittedMs, report.confirmLandedMs, report.confirmFoldApplyMs, report.confirmFoldRebuildMs, report.confirmQueuedMs,
                report.confirmApplyMs, report.toCommittedOnLetGoMs, report.confirmOnLetGoFrames.max, report.pass ? "PASS" : "FAIL"));
            Debug.Log(string.Format(inv, "[BrushBench] three presses, Confirm on the last let-go: {0:F0} ms to committed (its fold: apply {1:F0}, rebuild {2:F0}); frames max {3:F1}",
                report.toCommittedThreePressesMs, report.threePressFoldApplyMs, report.threePressFoldRebuildMs, report.confirmThreePressesFrames.max));
            _toolScripted = false;
            Application.Quit();
        }

        // From Confirm, a second after letting go, to the ground committed (BR17: no review page, so reviewMs stays 0), with every frame until 30 after it
        // has landed (BR21: no hitch); then a 400 ft stroke and Revert, with the 30 frames after.
        static void SlowAfter(string what)
        {
            float ms = Time.unscaledDeltaTime * 1000;
            if (ms > 16) Debug.Log($"[BrushBench] frame {Time.frameCount - 1}: {ms:F1} ms after {what}");
        }

        IEnumerator BenchConfirm(TerrainBrushTool tool, BrushBenchReport report)
        {
            // A player's pause between letting go and clicking Confirm: a second, the tool ticking as in the game.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.Elapsed.TotalSeconds < 1) { _tools.Tick(default); yield return null; }
            watch.Restart();
            var frames = new List<double>(256);
            _tools.Confirm(WorkStage.Built);
            while (tool.Confirming || EditsBusy) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); SlowAfter("Confirm"); }
            report.confirmMs = report.toCommittedMs = watch.Elapsed.TotalMilliseconds;
            (report.confirmFoldApplyMs, report.confirmFoldRebuildMs) = tool.LastFold;
            report.confirmLandedMs = tool.LastConfirmToLandedMs;
            if (_edits != null) { var last = _edits.Pipeline.Last; report.confirmQueuedMs = last.QueuedMs; report.confirmApplyMs = last.TotalMs; }
            for (int i = 0; i < 30; i++) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); SlowAfter("Confirm"); }
            report.confirmFrames = Summarise(frames);
            report.confirmBuiltMs = LastBuiltMs;
            report.towersMovedAtConfirm = LastBuiltTowersMoved;
            report.towersMovedLive = LiveTowersMoved;
            if (_edits != null) { report.shownDiffersAfterConfirm = _edits.Pipeline.CheckShown(out string note); report.shownNote ??= note; }

            var a = new Vector2(140, -360);
            var b = new Vector2(320, -360);
            _tools.Tick(BrushInput(a, left: true, click: true));
            yield return null;
            for (int t = 1; t <= 90; t++) { _tools.Tick(BrushInput(Vector2.Lerp(a, b, t / 90f), left: true)); yield return null; }
            _tools.Tick(BrushInput(b, left: true, release: true));
            for (int i = 0; i < 60; i++) { _tools.Tick(default); yield return null; }
            frames.Clear();
            Debug.Log($"[BrushBench] frame {Time.frameCount}: Revert");
            _tools.Confirm(WorkStage.Plan);   // Revert
            for (int i = 0; i < 30 || EditsBusy; i++)
            {
                _tools.Tick(default);
                yield return null;
                float ms = Time.unscaledDeltaTime * 1000;
                frames.Add(ms);
                if (ms > 16) Debug.Log($"[BrushBench] frame {Time.frameCount - 1}: {ms:F1} ms after Revert");
                if (i > 600) break;
            }
            report.revertFrames = Summarise(frames);
            while (EditsBusy) yield return null;
            if (_edits != null) { report.shownDiffersAfterRevert = _edits.Pipeline.CheckShown(out string note); report.shownNote ??= note; }

            // Confirm the frame after letting go: the fold started at the let-go has barely begun, so this is the slowest case.
            _tools.Tick(BrushInput(a, left: true, click: true));
            yield return null;
            for (int t = 1; t <= 90; t++) { _tools.Tick(BrushInput(Vector2.Lerp(a, b, t / 90f), left: true)); yield return null; }
            _tools.Tick(BrushInput(b, left: true, release: true));
            yield return null;
            frames.Clear();
            Debug.Log($"[BrushBench] frame {Time.frameCount}: Confirm on let-go");
            watch.Restart();
            _tools.Confirm(WorkStage.Built);
            while (tool.Confirming || EditsBusy) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); if (watch.Elapsed.TotalSeconds > 20) break; }
            report.toCommittedOnLetGoMs = watch.Elapsed.TotalMilliseconds;
            for (int i = 0; i < 30; i++) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); }
            report.confirmOnLetGoFrames = Summarise(frames);
            if (_edits != null && report.shownDiffersAfterConfirm == 0) { report.shownDiffersAfterConfirm = _edits.Pipeline.CheckShown(out string note); report.shownNote ??= note; }

            // PR C: a session of three presses, Confirm the frame after the last: the fold after each press is seeded with the one
            // before, so the last fold grades only the last press (it used to grade all three).
            for (int k = 0; k < 3; k++)
            {
                var from = new Vector2(140, -330 + 20 * k);
                var to = new Vector2(320, -330 + 20 * k);
                _tools.Tick(BrushInput(from, left: true, click: true));
                yield return null;
                for (int t = 1; t <= 90; t++) { _tools.Tick(BrushInput(Vector2.Lerp(from, to, t / 90f), left: true)); yield return null; }
                _tools.Tick(BrushInput(to, left: true, release: true));
                yield return null;
                if (k < 2) for (int i = 0; i < 60; i++) { _tools.Tick(default); yield return null; }   // a breath between presses: each press's fold lands
            }
            frames.Clear();
            Debug.Log($"[BrushBench] frame {Time.frameCount}: Confirm on the third press's let-go");
            watch.Restart();
            _tools.Confirm(WorkStage.Built);
            while (tool.Confirming || EditsBusy) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); if (watch.Elapsed.TotalSeconds > 20) break; }
            report.toCommittedThreePressesMs = watch.Elapsed.TotalMilliseconds;
            (report.threePressFoldApplyMs, report.threePressFoldRebuildMs) = tool.LastFold;
            for (int i = 0; i < 30; i++) { _tools.Tick(default); yield return null; frames.Add(Time.unscaledDeltaTime * 1000); }
            report.confirmThreePressesFrames = Summarise(frames);
            if (_edits != null && report.shownDiffersAfterConfirm == 0) { report.shownDiffersAfterConfirm = _edits.Pipeline.CheckShown(out string note); report.shownNote ??= note; }
        }
    }
}
