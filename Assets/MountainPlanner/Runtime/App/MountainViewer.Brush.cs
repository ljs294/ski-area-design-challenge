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
    /// terminal and under the line, Flatten for a junction, Smooth, Slope for a cat track), shooting each mid-stroke, at its
    /// review and confirmed; with <c>-brushmovie</c> it also saves every frame (15 a second) for
    /// PickerLabSetup.EncodeMovies. <c>-brushbench &lt;out.json&gt;</c> times the live ground while dragging a 60 ft and a 400 ft
    /// brush, the review and the confirm. Captures never open or save the player's saves (AppFlow CaptureRun).
    /// </summary>
    public sealed partial class MountainViewer
    {
        // The sidehill (local metres from the site's centre): the quad's ends and where each brush works.
        static readonly Vector2 BrushLiftBottom = new Vector2(240, -760), BrushLiftTop = new Vector2(205, -340);

        string _brushFrames;
        int _brushFrame;

        void StartBrushRuns(string[] args)
        {
            int tour = Array.IndexOf(args, "-brushtour");
            if (tour >= 0 && tour + 1 < args.Length) { StartCoroutine(BrushTour(args[tour + 1], Array.IndexOf(args, "-brushmovie") >= 0)); return; }
            int bench = Array.IndexOf(args, "-brushbench");
            if (bench >= 0 && bench + 1 < args.Length) StartCoroutine(BrushBench(args[bench + 1]));
        }

        // A tick of input for a scripted stroke: a 30th of a second, either button.
        ToolInput BrushInput(Vector2 xz, bool left = false, bool right = false, bool click = false, bool release = false)
        {
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

        IEnumerator BrushWaitReview(TerrainBrushTool tool)
        {
            for (int i = 0; i < 3000 && !tool.ReviewReady; i++) { _tools.Tick(default); yield return BrushFrame(); }
        }

        // The quad the brushes work round, built through the history and the earthwork as the lift tool builds one.
        bool BuildBrushLift()
        {
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var heights = Ground.Heights;
            var a = _resort.Frame.ToAlbers(BrushLiftBottom.x, BrushLiftBottom.y);
            var b = _resort.Frame.ToAlbers(BrushLiftTop.x, BrushLiftTop.y);
            var built = LiftBuild.Build(type, heights, a.X, a.Y, b.X, b.Y, TowerStance.Plumb);
            var history = Design;
            var cmd = new AddLift(history.Revision, "Sidehill Quad", type.CatalogId, DesignFrame.ToDesign(BrushLiftBottom), DesignFrame.ToDesign(BrushLiftTop),
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
            if (!BuildBrushLift()) yield break;
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
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return BrushWaitReview(tool);
            yield return BrushShot(dir, "4-raise-review");
            _tools.Confirm(WorkStage.Built);
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
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return BrushWaitReview(tool);
            yield return BrushShot(dir, "6-flatten-review");
            _tools.Confirm(WorkStage.Built);
            for (int i = 0; i < 30; i++) yield return BrushFrame();

            // Smooth.
            _tools.Choose(TerrainIds.Smooth);
            tool = (TerrainBrushTool)_tools.Active;
            tool.SetBrush(200, 100);
            BrushLook(new Vector2(168, -480), 260);
            yield return BrushDrag(new Vector2(150, -500), new Vector2(185, -460), right: false, 0.5f, 30, dir, "7-smooth-live");
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return BrushWaitReview(tool);
            _tools.Confirm(WorkStage.Built);
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
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            yield return BrushWaitReview(tool);
            yield return BrushShot(dir, "9-slope-review");
            _tools.Confirm(WorkStage.Built);
            for (int i = 0; i < 45; i++) yield return BrushFrame();
            _toolScripted = false;
            _tools.Close();
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
            public string schema = "mountain-planner-brush-benchmark/1";
            public string commit, measuredUtc, gpu, cpu;
            public Timing live60, live400, frame60, frame400;
            public double reviewMs, confirmMs;
            public bool pass;
        }

        [Serializable]
        public struct Timing { public double p50, p95, max; public int n; }

        static Timing Summarise(List<double> ms)
        {
            if (ms.Count == 0) return default;
            ms.Sort();
            return new Timing { p50 = ms[ms.Count / 2], p95 = ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(ms.Count * 0.95) - 1)], max = ms[ms.Count - 1], n = ms.Count };
        }

        IEnumerator BrushBench(string outPath)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (Camera != null)
            {
                Camera.InputEnabled = false;
                var focus = new Vector2(240, -580);
                Camera.Frame(new Vector3(focus.x, _resort.Surface.HeightAt(focus.x, focus.y), focus.y), 900);
                Camera.SetAngles(250, 45);
            }
            Ground.Start();
            while (!Ground.Ready) yield return null;
            if (!BuildBrushLift()) yield break;
            Hud.SetToolboxTab("terrain");
            Hud.SetToolbox(true);
            _toolScripted = true;
            _tools.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_tools.Active;
            var live = Ground as ILiveGround;
            var report = new BrushBenchReport();
            for (int pass = 0; pass < 2; pass++)
            {
                int feet = pass == 0 ? 60 : 400;
                tool.SetBrush(feet, 60);
                for (int i = 0; i < 30; i++) { _tools.Tick(default); yield return null; }
                var liveMs = new List<double>();
                var frameMs = new List<double>();
                var a = new Vector2(120, -450 - pass * 150);
                var b = new Vector2(360, -450 - pass * 150);
                _tools.Tick(BrushInput(a, left: true, click: true));
                yield return null;
                int steps = 120;   // 4 s at 30 a second, 2 m a tick
                for (int t = 1; t <= steps; t++)
                {
                    _tools.Tick(BrushInput(Vector2.Lerp(a, b, t / (float)steps), left: true));
                    liveMs.Add(live?.LastLiveMs ?? 0);
                    yield return null;
                    frameMs.Add(Time.unscaledDeltaTime * 1000);
                }
                _tools.Tick(BrushInput(b, left: true, release: true));
                for (int i = 0; i < 30; i++) yield return null;
                if (pass == 0) { report.live60 = Summarise(liveMs); report.frame60 = Summarise(frameMs); }
                else { report.live400 = Summarise(liveMs); report.frame400 = Summarise(frameMs); }
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            while (!tool.ReviewReady) { _tools.Tick(default); yield return null; }
            report.reviewMs = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            _tools.Confirm(WorkStage.Built);
            while (EditsBusy) yield return null;
            report.confirmMs = watch.Elapsed.TotalMilliseconds;
            report.commit = BuildInfo.Current.commit;
            report.measuredUtc = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            report.gpu = SystemInfo.graphicsDeviceName;
            report.cpu = SystemInfo.processorType;
            report.pass = report.live60.p95 <= 8 && report.live400.p95 <= 16 && report.confirmMs <= 250;
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            Debug.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[BrushBench] live 60 ft p95 {0:F1} ms (frame p95 {1:F1}), 400 ft p95 {2:F1} ms (frame p95 {3:F1}); review {4:F0} ms; confirm {5:F0} ms; {6}",
                report.live60.p95, report.frame60.p95, report.live400.p95, report.frame400.p95, report.reviewMs, report.confirmMs, report.pass ? "PASS" : "FAIL"));
            _toolScripted = false;
            Application.Quit();
        }
    }
}
