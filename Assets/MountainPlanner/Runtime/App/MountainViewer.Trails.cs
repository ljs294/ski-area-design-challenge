using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
            _trailLabels?.Update(cam, _trailLayer.Ground, on && _hudShown && !_photo && (Hud == null || Hud.LabelsOn));
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

        // ---------- -opensave <folder> [-regrade]: the owner's own design in a capture (task 08 review) ----------

        /// <summary>
        /// Opens a save on a package opened from the command line, as the game's flow opens one from the library (captures
        /// of a player's own trails, with -view and -screenshot; -regrade then shows them as this build grades them).
        /// </summary>
        void OpenSaveForCapture(string[] args)
        {
            int i = Array.IndexOf(args, "-opensave");
            if (i < 0 || i + 1 >= args.Length || Persistence.Saves.OpenDesign.Current != null) return;
            var m = _resort.Manifest;
            string heights = m.Layers.FirstOrDefault(l => l.Id == "heights-core")?.Sha256 ?? "";
            Persistence.Saves.OpenDesign.Current = Persistence.Saves.OpenDesign.Load(DataRoot, args[i + 1], m.PackageId, heights);
            Debug.Log($"[Saves] Opened {args[i + 1]} for a capture: {Persistence.Saves.OpenDesign.Current.History.Document.Trails.Count} trails");
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

        // ---------- -paintbench <file>: how the brush feels (owner, 2026-10-20: "laggy, not very elegant") ----------

        /// <summary>
        /// Paints strokes with the New trail tool, one input per frame at a quick drag (5 m a frame, a 600 m stroke in two
        /// seconds), and records each frame's time while dragging and how long after letting go the footprint shows. Only
        /// the tool's own input is used, so the same bench measures any build.
        /// </summary>
        IEnumerator PaintBench(string file)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            _tools.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_tools.Active;
            _toolScripted = true;
            while (!Ground.Ready) yield return null;
            for (int i = 0; i < 30; i++) yield return null;
            var drag = new List<double>();
            var settle = new List<double>();
            var showFrame = new List<double>();
            const int strokes = 12;
            for (int s = -1; s < strokes; s++)   // the first stroke warms up
            {
                float angle = (s + 1) * Mathf.PI * 2 / strokes;
                var head = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 500f;
                var path = FallLine(head, 600);
                var pts = new List<Vector2>();
                foreach (var p in path) pts.Add(DesignFrame.ToLocal(p));
                _tools.Tick(GroundInput(pts[0], click: true, drag: true));
                yield return null;
                // Walk the line 5 m a frame.
                float walked = 0, total = 0;
                for (int k = 1; k < pts.Count; k++) total += Vector2.Distance(pts[k - 1], pts[k]);
                int seg = 1;
                float segStart = 0;
                while (walked < total)
                {
                    walked = Mathf.Min(total, walked + 5f);
                    while (seg < pts.Count - 1 && segStart + Vector2.Distance(pts[seg - 1], pts[seg]) < walked) { segStart += Vector2.Distance(pts[seg - 1], pts[seg]); seg++; }
                    float len = Vector2.Distance(pts[seg - 1], pts[seg]);
                    var at = Vector2.Lerp(pts[seg - 1], pts[seg], len > 0 ? Mathf.Clamp01((walked - segStart) / len) : 1);
                    _tools.Tick(GroundInput(at, drag: true));
                    yield return null;
                    if (s >= 0) drag.Add(Time.unscaledDeltaTime * 1000.0);
                }
                var watch = System.Diagnostics.Stopwatch.StartNew();
                _tools.Tick(GroundInput(pts[pts.Count - 1], release: true));
                int frames = 0;
                double worst = 0;
                while ((trail.Analysing || _tools.Context.Running > 0) && frames < 3000)
                {
                    _tools.Tick(default);
                    yield return null;
                    frames++;
                    worst = Math.Max(worst, Time.unscaledDeltaTime * 1000.0);
                }
                _tools.Tick(default);
                yield return null;
                if (s >= 0) { settle.Add(watch.Elapsed.TotalMilliseconds); showFrame.Add(Math.Max(worst, Time.unscaledDeltaTime * 1000.0)); }
                // Undo the stroke, so every stroke paints a fresh trail.
                _tools.Tick(new ToolInput { Keys = ToolKeys.UndoStroke });
                for (int i = 0; i < 3; i++) { _tools.Tick(default); yield return null; }
                while (trail.Analysing) { _tools.Tick(default); yield return null; }
            }
            drag.Sort(); settle.Sort(); showFrame.Sort();
            double P(List<double> v, double q) => v.Count == 0 ? 0 : v[Math.Min(v.Count - 1, (int)Math.Floor(q * v.Count))];
            string line = $"[PaintBench] dragging: frame p50 {P(drag, 0.5):F1} ms, p95 {P(drag, 0.95):F1} ms, max {P(drag, 1):F1} ms over {drag.Count} frames; " +
                          $"letting go to the footprint shown: p50 {P(settle, 0.5):F0} ms, p95 {P(settle, 0.95):F0} ms; the slowest frame meanwhile p95 {P(showFrame, 0.95):F1} ms";
            Debug.Log(line);
            File.WriteAllText(file, line + "\n");
            UndoCaptureTrails();
            Application.Quit();
        }

        /// <summary>
        /// -paintmovie &lt;folder&gt; (with -view and -trailspot): the brush at work for the owner's review, a frame saved
        /// per step as &lt;folder&gt;/paint/f_&lt;ms&gt;.jpg at 15 fps (Editor/PickerLabSetup.EncodeMovies makes paint.mp4):
        /// the spot's path painted at a steady drag, a wider second pass, an island rubbed out, then a hold. Not a measurement.
        /// </summary>
        IEnumerator PaintMovie(string folder, string spotName)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            if (!TrailSpots.TryGetValue(spotName, out var spot)) yield break;
            if (Camera != null) Camera.InputEnabled = false;
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            _tools.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_tools.Active;
            _toolScripted = true;
            while (!Ground.Ready) yield return null;
            string frames = Path.Combine(folder, "paint");
            Directory.CreateDirectory(frames);
            int frame = 0;
            IEnumerator Shoot()
            {
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(frames, $"f_{frame * 1000 / 15:D6}.jpg"), shot.EncodeToJPG(88));
                Destroy(shot);
                frame++;
            }
            IEnumerator Hover(Vector2 at, int count)
            {
                for (int i = 0; i < count; i++) { _tools.Tick(GroundInput(at)); yield return null; yield return Shoot(); }
            }
            IEnumerator Drag(Vector2 a, Vector2 b, bool erase, float metresPerFrame)
            {
                _tools.Tick(GroundInput(a, click: true, drag: true, erase: erase));
                yield return null;
                yield return Shoot();
                int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / metresPerFrame));
                for (int t = 1; t <= steps; t++)
                {
                    _tools.Tick(GroundInput(Vector2.Lerp(a, b, t / (float)steps), drag: true, erase: erase));
                    yield return null;
                    yield return Shoot();
                }
                _tools.Tick(GroundInput(b, release: true, erase: erase));
                yield return null;
                yield return Shoot();
            }
            int n = spot.path.Length / 2;
            var first = new Vector2(spot.path[0], spot.path[1]);
            for (int i = 0; i < 30; i++) yield return null;
            yield return Hover(first, 10);
            for (int k = 0; k + 1 < n; k++)
            {
                int feet = spot.feet.Length == 1 ? spot.feet[0] : spot.feet[k];
                while (trail.BrushFeet < feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
                while (trail.BrushFeet > feet) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                var a = new Vector2(spot.path[2 * k], spot.path[2 * k + 1]);
                var b = new Vector2(spot.path[2 * k + 2], spot.path[2 * k + 3]);
                yield return Hover(a, 4);
                yield return Drag(a, b, false, 4f);
            }
            foreach (var (x, z, ft) in spot.islands)
            {
                while (trail.BrushFeet > ft) _tools.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
                var c = new Vector2(x, z);
                yield return Hover(c + new Vector2(-12, 0), 6);
                yield return Drag(c + new Vector2(-6, 0), c + new Vector2(6, 0), true, 1.5f);
            }
            yield return Hover(new Vector2(spot.path[2 * n - 2], spot.path[2 * n - 1]), 30);
            Debug.Log($"[PaintMovie] {frame} frames saved to {frames}");
            UndoCaptureTrails();
            Application.Quit();
        }

        /// <summary>
        /// -paintcheck &lt;file&gt;: the paint on the GPU is the paint on the CPU in a real player, with its render thread
        /// (findings 2026-10-20, #1-#2: earlier paint vanished, then paint landed where none was painted). Paints three
        /// areas far apart with the New trail tool, a drag a frame and a let-go each, quick second strokes before the last
        /// footprint is worked out among them, and after every frame's upload reads the paint texture back and compares
        /// it with the CPU copy. Writes one line, "[PaintCheck] ok" or the frames that differ.
        /// </summary>
        IEnumerator PaintCheck(string file)
        {
            while (_resort == null || !_resort.CoverReady.IsCompleted || WorkBusy || _tools == null) yield return null;
            Hud.SetToolboxTab("trails");
            Hud.SetToolbox(true);
            _tools.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_tools.Active;
            _toolScripted = true;
            while (!Ground.Ready) yield return null;
            for (int i = 0; i < 30; i++) yield return null;
            var core = _resort.Core;
            Vector2 At(float u, float v) => core.center + new Vector2(u * core.width, v * core.height);
            var areas = new[] { (At(-0.3f, 0), At(-0.1f, 0)), (At(0.2f, 0.25f), At(0.35f, 0.25f)), (At(-0.1f, -0.2f), At(-0.1f, -0.38f)) };
            int frames = 0, bad = 0;
            var faults = new List<string>();
            IEnumerator Check(string when)
            {
                yield return null;   // the viewer's Update has flushed this frame's paint
                frames++;
                var tex = _trailPaint?.GpuTexture;
                if (tex == null) yield break;
                var cpu = _trailPaint.CpuBytes.ToArray();
                var request = UnityEngine.Rendering.AsyncGPUReadback.Request(tex, 0);
                request.WaitForCompletion();
                if (request.hasError) { bad++; faults.Add(when + ": readback failed"); yield break; }
                var gpu = request.GetData<byte>();
                int wrong = 0;
                for (int i = 0; i < cpu.Length && i < gpu.Length; i++) if (cpu[i] != gpu[i]) wrong++;
                if (wrong > 0 || cpu.Length != gpu.Length) { bad++; if (faults.Count < 12) faults.Add($"{when}: {wrong} bytes differ"); }
            }
            for (int n = 0; n < areas.Length; n++)
            {
                var (a, b) = areas[n];
                _tools.Tick(GroundInput(a, click: true, drag: true));
                yield return Check($"area {n + 1} click");
                int steps = Mathf.CeilToInt(Vector2.Distance(a, b) / 4f);
                for (int t = 1; t <= steps; t++)
                {
                    _tools.Tick(GroundInput(Vector2.Lerp(a, b, t / (float)steps), drag: true));
                    yield return Check($"area {n + 1} drag {t}");
                }
                _tools.Tick(GroundInput(b, release: true));
                yield return Check($"area {n + 1} let go");
                // A quick second stroke along the same area while the footprint is worked out (#3).
                var back = b + (a - b).normalized * 30;
                _tools.Tick(GroundInput(b, click: true, drag: true));
                for (int t = 1; t <= 8; t++)
                {
                    _tools.Tick(GroundInput(Vector2.Lerp(b, back, t / 8f), drag: true));
                    yield return Check($"area {n + 1} second stroke {t}");
                }
                _tools.Tick(GroundInput(back, release: true));
                yield return Check($"area {n + 1} second let go");
                while (trail.Analysing) { _tools.Tick(default); yield return Check($"area {n + 1} worked out"); }
                for (int i = 0; i < 5; i++) { _tools.Tick(default); yield return Check($"area {n + 1} settled"); }
            }
            string line = bad == 0 ? $"[PaintCheck] ok: {frames} frames, the GPU paint matched the CPU paint every frame"
                                   : $"[PaintCheck] FAILED: {bad} of {frames} frames differ; " + string.Join("; ", faults);
            Debug.Log(line);
            File.WriteAllText(file, line + "\n");
            UndoCaptureTrails();
            Application.Quit(bad == 0 ? 0 : 1);
        }

        void StartTrailShots(string[] args)
        {
            int pc = Array.IndexOf(args, "-paintcheck");
            if (pc >= 0 && pc + 1 < args.Length) { StartCoroutine(PaintCheck(args[pc + 1])); return; }
            int pm = Array.IndexOf(args, "-paintmovie");
            if (pm >= 0 && pm + 1 < args.Length)
            {
                int sp = Array.IndexOf(args, "-trailspot");
                StartCoroutine(PaintMovie(args[pm + 1], sp >= 0 && sp + 1 < args.Length ? args[sp + 1] : "sidehill"));
                return;
            }
            int pb = Array.IndexOf(args, "-paintbench");
            if (pb >= 0 && pb + 1 < args.Length) { StartCoroutine(PaintBench(args[pb + 1])); return; }
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
                if (k == 0) yield return Shot(dir, "0-brushing");   // the first stroke mid-drag: the brush's paint as it goes on
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
