using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MountainPlanner.App.Edits;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Presentation;
using UnityEngine;
using UnityEngine.InputSystem;
using Debug = UnityEngine.Debug;

namespace MountainPlanner.App
{
    /// <summary>
    /// Terrain edits in the viewer (task P3-05), until task 08's trail tool drives them:
    /// - <c>-testedit</c>: dev keys for the test bench (TE4). F6 previews a 300 m × 100 ft bench from the pointer down the
    ///   fall line (yellow contours, TE3); F7 breaks ground; F8 cancels; Ctrl+Z and Ctrl+Y undo and redo.
    /// - <c>-testbench x,z</c>: grades one bench from a local point (metres from the centre) once the mountain is in, for
    ///   captures; <c>-testbench-preview</c> stops at the preview.
    /// - <c>-editbench &lt;out.json&gt;</c>: the edit timing run (CF-09), then quits.
    /// The HUD's elevation and slope read the edited float32 heights once the session is up (SH-02).
    /// The session drives the open design's history (task P3-04's <see cref="OpenDesign.Current"/>), so a save holds the
    /// edits and a loaded save's edits are laid over the mountain; without the game's flow it keeps its own.
    /// </summary>
    public sealed partial class MountainViewer
    {
        TerrainEditSession _edits;
        bool _editKeys, _editsStarting;

        /// <summary>The terrain edit session, once started (null without -testedit, -testbench or -editbench).</summary>
        public TerrainEditSession Edits => _edits;

        void StartTerrainEdits(string[] args)
        {
            _editKeys = Array.IndexOf(args, "-testedit") >= 0;
            int bench = Array.IndexOf(args, "-testbench");
            int timing = Array.IndexOf(args, "-editbench");
            bool styleTile = Array.IndexOf(args, "-styletile") >= 0 || Array.IndexOf(args, "-liftbench") >= 0 || DrawnResortWanted(args);
            // A loaded save with terrain edits needs the session too, to lay them over the mountain.
            bool saved = OpenDesign.Current != null && !OpenDesign.Current.History.Terrain.IsEmpty;
            // UI captures arm the trail tools (task 08), which need the ground's session ready when they do.
            bool capture = Array.IndexOf(args, "-uicapture") >= 0;
            if (!_editKeys && bench < 0 && timing < 0 && !saved && !styleTile && !capture) return;
            _editsStarting = true;
            _drawnResortBuilding = DrawnResortWanted(args);   // the benchmark waits from the first frame (task P3-11)
            StartCoroutine(StartEditsWhenReady(args, bench, timing));
        }

        /// <summary>
        /// Save v8 (sections finding 7): an older save's built work graded once more, as this build grades it (its trails were
        /// graded from strokes, now from their clearings' cells). Off the main thread, from the untouched ground, so the
        /// same save always regrades the same way; in memory only (<see cref="OpenDesign.Regraded"/>), so the file is left
        /// as it was until the player saves. Skipped if the player changes the design first.
        /// </summary>
        /// <summary>The older save's notice has shown (captures).</summary>
        internal bool RegradeNoticeShown;

        IEnumerator RegradeOlderSave(OpenDesign open)
        {
            var doc = open.History.Document;
            var old = open.History.Terrain;
            var site = _edits.Earthwork.Site;
            var source = _edits.Pipeline.Heights.Source;
            var work = System.Threading.Tasks.Task.Run(() => DesignEarthwork.Regrade(source, site, doc));
            while (!work.IsCompleted) yield return null;
            if (work.IsFaulted) { Debug.LogException(work.Exception); yield break; }
            if (OpenDesign.Current != open || !ReferenceEquals(open.History.Document, doc) || !ReferenceEquals(open.History.Terrain, old))
            {
                Debug.Log("[Saves] the older save's regrade was skipped: the design changed first");
                yield break;
            }
            var fresh = work.Result;
            Compare(old, fresh, out int points, out double cubicMetres, out int cells);
            open.Regraded(fresh);
            int trails = doc.Trails.Count(t => t.Stage == WorkStage.Built);
            Debug.Log($"[Saves] regraded an older save (v{open.MigratedFrom}): {trails} built trails, {points} points moved, " +
                      $"{cubicMetres:F1} m³ of cut and fill changed, {cells} cleared cells changed; the file is unchanged until saved");
            while (WorkBusy) yield return null;   // the notice once the area has settled, so it isn't missed behind the loading
            Hud?.Toast(points == 0 && cells == 0
                ? "Opened a save from an earlier version: nothing on the ground changes. Save to keep it in this version's format."
                : $"Opened a save from an earlier version: its trails were regraded ({points:N0} points moved). Save to keep it.");
            RegradeNoticeShown = true;
        }

        // How far two edit sets differ: lattice points whose height differs, the cut and fill between them, cleared cells.
        static void Compare(TerrainEdits a, TerrainEdits b, out int points, out double cubicMetres, out int cells)
        {
            points = 0; cells = 0; cubicMetres = 0;
            var keys = new SortedSet<EditTileKey>();
            foreach (var t in a.Tiles) keys.Add(t.Key);
            foreach (var t in b.Tiles) keys.Add(t.Key);
            foreach (var k in keys)
                for (int j = 0; j < EditTileKey.Side; j++)
                    for (int i = 0; i < EditTileKey.Side; i++)
                    {
                        int c = k.FirstColumn + i, r = k.FirstRow + j;
                        int d = b.DeltaMmAt(c, r) - a.DeltaMmAt(c, r);
                        if (d != 0) { points++; cubicMetres += Math.Abs(d) / 1000.0; }
                        if (b.IsClearedAt(c, r) != a.IsClearedAt(c, r)) cells++;
                    }
        }

        IEnumerator StartEditsWhenReady(string[] args, int bench, int timing)
        {
            // The open area's design (its save's edits come with it), or a fresh one in a run without the game's flow.
            var history = Design ?? new DesignHistory(DesignDocument.Empty(1));   // the tools' design (P3-06), so both edit one history
            var task = TerrainEditSession.StartAsync(_resort, history, destroyCancellationToken);
            while (!task.IsCompleted) yield return null;
            _editsStarting = false;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
            if (task.IsCanceled) yield break;
            _edits = task.Result;
            FollowBuiltWork(_edits);   // built trails and lifts drawn as they are (MountainViewer.Built.cs, task 07)
            if (_preview != null) _preview.Cleared += _edits.ClearPreview;   // a tool's preview clearing takes the yellow contours with it (CF-08)
            Debug.Log("[TerrainEdits] ready" + (_editKeys ? ": F6 preview a bench at the pointer, F7 break ground, F8 cancel, Ctrl+Z / Ctrl+Y" : ""));
            if (Array.IndexOf(args, "-regrade") >= 0) _edits.RegradeForCapture();   // captures: a save graded as this build grades
            else if (OpenDesign.Current is OpenDesign open && open.NeedsRegrade && ReferenceEquals(open.History, _edits.History))
                StartCoroutine(RegradeOlderSave(open));
            if (bench >= 0 && bench + 1 < args.Length)
            {
                var v = args[bench + 1].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                var head = new Vector2(v[0], v[1]);
                var dir = v.Length >= 3 ? new Vector2(Mathf.Sin(v[2] * Mathf.Deg2Rad), Mathf.Cos(v[2] * Mathf.Deg2Rad)) : _edits.Downhill(head.x, head.y);
                double ms = _edits.PreviewBench(head, dir);
                Debug.Log($"[TerrainEdits] test bench at {head} towards {dir}: preview {ms:F1} ms, {_edits.Preview.Segments} contour segments");
                if (Array.IndexOf(args, "-testbench-preview") < 0)
                {
                    _edits.CommitBench();
                    while (!_edits.Applying.IsCompleted) yield return null;
                }
            }
            if (timing >= 0 && timing + 1 < args.Length) StartCoroutine(RunEditBenchmark(args[timing + 1]));
            if (Array.IndexOf(args, "-styletile") >= 0)
            {
                var report = StyleTile.Build(_edits, _resort.Frame, Lifts);
                Debug.Log("[StyleTile] " + report);
                // -extralift x,z[,length]: a second lift up the fall line from there, through the same build path (a check, round 3).
                int extra = Array.IndexOf(args, "-extralift");
                if (extra >= 0 && extra + 1 < args.Length)
                {
                    var v = args[extra + 1].Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                    // -extramonta: a Monta FG4 instead, its drive at the bottom (task 09); -extrad6: the Sessellift D6 (LT41).
                    bool monta = Array.IndexOf(args, "-extramonta") >= 0, d6 = Array.IndexOf(args, "-extrad6") >= 0;
                    Debug.Log("[StyleTile] extra " + StyleTile.BuildUphill(_edits, _resort.Frame, Lifts, v[0], v[1], v.Length > 2 ? v[2] : 650,
                                                                          d6 ? "Check D6" : monta ? "Check Monta" : "Check Quad",
                                                                          d6 ? LiftCatalogIds.SesselliftD6 : monta ? LiftCatalogIds.MontaFg4 : LiftCatalogIds.SesselliftFgq4));
                }
                while (!_edits.Applying.IsCompleted) yield return null;
            }
            int liftBench = Array.IndexOf(args, "-liftbench");
            if (liftBench >= 0 && liftBench + 1 < args.Length && int.TryParse(args[liftBench + 1], out int want)) yield return BuildBenchLifts(want);
            if (DrawnResortWanted(args)) yield return BuildDrawnResort();
        }

        /// <summary>True while the -liftbench lifts are being built: the benchmark and captures wait for them.</summary>
        bool _benchLiftsBuilding;

        /// <summary>
        /// -liftbench N (task 09): N fixed-grip quads, each 600 m up the fall line from a grid across the core box, built by the
        /// game's own build path (towers, pads, trench, clearing), for the benchmark's chairs: in turn a Sessellift quad, a
        /// Sessellift D6 detachable (LT41) and a Monta FG4. Deterministic: the same grid in the same order every run.
        /// </summary>
        IEnumerator BuildBenchLifts(int want)
        {
            _benchLiftsBuilding = true;
            var core = _resort.Core;
            int side = Mathf.CeilToInt(Mathf.Sqrt(want * 2f));
            float inset = 300;
            int built = 0, tried = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < side * side && built < want; i++)
            {
                int row = i / side, col = i % side;
                float x = Mathf.Lerp(core.xMin + inset, core.xMax - inset, (col + 0.5f) / side);
                float z = Mathf.Lerp(core.yMin + inset, core.yMax - inset, (row + 0.5f) / side);
                int before = _edits.History.Document.Lifts.Count;
                tried++;
                // A third Sessellift quads, a third Montas, a third D6 detachables (LT41: their chairs run round the stations).
                string report = StyleTile.BuildUphill(_edits, _resort.Frame, Lifts, x, z, 600, "Bench " + (built + 1),
                                                      built % 3 == 2 ? LiftCatalogIds.MontaFg4 : built % 3 == 1 ? LiftCatalogIds.SesselliftD6 : LiftCatalogIds.SesselliftFgq4);
                if (_edits.History.Document.Lifts.Count > before) built++;
                else Debug.Log($"[LiftBench] ({x:F0}, {z:F0}): {report}");
                yield return null;
            }
            while (!_edits.Applying.IsCompleted) yield return null;
            Debug.Log($"[LiftBench] {built} lifts built of {want} asked ({tried} places tried) in {clock.Elapsed.TotalSeconds:F1} s");
            _benchLiftsBuilding = false;
        }

        /// <summary>True while edits are starting or applying: captures wait for them.</summary>
        bool EditsBusy => _editsStarting || _benchLiftsBuilding || _drawnResortBuilding || (_edits != null && !_edits.Applying.IsCompleted);

        /// <summary>Edits applying or built work waiting to be redrawn: captures and the benchmark wait for both.</summary>
        bool WorkBusy => EditsBusy || BuiltBusy;

        void UpdateTerrainEdits(Keyboard keyboard)
        {
            if (!_editKeys || _edits == null || keyboard == null || Camera == null) return;
            bool ctrl = keyboard.ctrlKey.isPressed;
            if (keyboard.f6Key.wasPressedThisFrame)
            {
                var mouse = Mouse.current;
                if (mouse == null) return;
                var ray = Camera.GetComponent<UnityEngine.Camera>().ScreenPointToRay(mouse.position.ReadValue());
                float hit = Camera.GroundAlong(ray, 30000);
                if (float.IsNaN(hit)) return;
                var g = ray.GetPoint(hit);
                double ms = _edits.PreviewBench(new Vector2(g.x, g.z), _edits.Downhill(g.x, g.z));
                Debug.Log($"[TerrainEdits] preview {ms:F1} ms, {_edits.Preview.Segments} contour segments");
            }
            else if (keyboard.f7Key.wasPressedThisFrame) _edits.CommitBench();
            else if (keyboard.f8Key.wasPressedThisFrame) _edits.ClearPreview();
            // Without the Toolbox (a run with no HUD), Ctrl+Z / Ctrl+Y here; with it, the Toolbox's undo already moves this history.
            else if (_tools == null && ctrl && keyboard.zKey.wasPressedThisFrame) _edits.History.Undo();
            else if (_tools == null && ctrl && keyboard.yKey.wasPressedThisFrame) _edits.History.Redo();
        }

        /// <summary>The ground height the HUD reads: the edited float32 heights in the core (SH-02), else the terrain's.</summary>
        float ReadoutHeight(float x, float z)
        {
            if (_edits != null)
            {
                float h = _edits.Pipeline.HeightAtLocal(x, z);
                if (!float.IsNaN(h)) return h;
            }
            return _resort.Surface.HeightAt(x, z);
        }

        void ReleaseTerrainEdits()
        {
            ReleaseBuiltWork();
            if (_preview != null && _edits != null) _preview.Cleared -= _edits.ClearPreview;
            _edits?.Dispose();
            _edits = null;
        }

        /// <summary>
        /// -editbench &lt;out.json&gt; (CF-09, exit criterion 2): the test bench at 10 fixed spots 500 m round the centre,
        /// 5 rounds each. Preview is the bench's grading plus its yellow contours; commit is Break ground until the
        /// mountain shows it (heights, splat and trees applied, then a frame); undo likewise. It also checks that undo
        /// leaves byte-identical heights in Unity's tiles, and the garbage of idle frames afterwards. Writes
        /// an <see cref="EditBenchmarkReport"/>, then quits.
        /// </summary>
        IEnumerator RunEditBenchmark(string outPath)
        {
            while (Forest == null || !_resort.CoverReady.IsCompleted) yield return null;
            if (Camera != null) Camera.InputEnabled = false;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < 60; i++) yield return null;
            const int spots = 10, rounds = 5;
            var preview = new List<double>();
            var commit = new List<double>();
            var undo = new List<double>();
            var plan = new List<double>();
            var heights = new List<double>();
            var splat = new List<double>();
            var trees = new List<double>();
            bool identical = true;
            int posts = 0, texels = 0, cleared = 0, tiles = 0;
            var before = SnapshotCoreHeights();
            for (int round = 0; round < rounds; round++)
                for (int s = 0; s < spots; s++)
                {
                    float angle = s * Mathf.PI * 2 / spots;
                    var head = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 500f;
                    preview.Add(_edits.PreviewBench(head, _edits.Downhill(head.x, head.y)));
                    yield return null;
                    var watch = Stopwatch.StartNew();
                    _edits.CommitBench();
                    while (!_edits.Applying.IsCompleted) yield return null;
                    yield return null;   // the frame that shows it
                    commit.Add(watch.Elapsed.TotalMilliseconds);
                    var r = _edits.Pipeline.Last;
                    plan.Add(r.PlanMs);
                    heights.Add(r.HeightsMs);
                    splat.Add(r.SplatMs);
                    trees.Add(r.TreesMs);
                    posts = Math.Max(posts, r.Posts);
                    texels = Math.Max(texels, r.Texels);
                    cleared = Math.Max(cleared, r.Cleared);
                    tiles = Math.Max(tiles, r.CacheTiles);
                    for (int i = 0; i < 10; i++) yield return null;
                    watch.Restart();
                    _edits.History.Undo();
                    while (!_edits.Applying.IsCompleted) yield return null;
                    yield return null;
                    undo.Add(watch.Elapsed.TotalMilliseconds);
                    if (round == 0 && !SameHeights(before, SnapshotCoreHeights()))
                    {
                        identical = false;
                        Debug.LogError($"[EditBenchmark] undo at spot {s} left different heights");
                    }
                    for (int i = 0; i < 10; i++) yield return null;
                }

            // Idle frames after editing: nothing may allocate (CF-09).
            for (int i = 0; i < 120; i++) yield return null;
            var idle = new FrameStats(1 << 12);
            idle.Reset();
            for (int i = 0; i < 600; i++)
            {
                yield return null;
                idle.Record(Time.unscaledDeltaTime);
            }
            idle.Stop();
            var idleSummary = idle.Summarise();
            idle.Dispose();

            var build = BuildInfo.Current;
            var report = new EditBenchmarkReport
            {
                commit = build.commit, commitDirty = build.dirty, measuredUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                gpu = SystemInfo.graphicsDeviceName, cpu = SystemInfo.processorType, development = Debug.isDebugBuild,
                site = _resort.Manifest.Site.Name, quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                width = Screen.width, height = Screen.height, spots = spots, rounds = rounds,
                benchLengthM = (float)TerrainEditSession.BenchLengthMetres, benchWidthM = (float)TerrainEditSession.BenchWidthMetres,
                preview = Stat.Of(preview), commitApplied = Stat.Of(commit), undo = Stat.Of(undo),
                planStage = Stat.Of(plan), heightsStage = Stat.Of(heights), splatStage = Stat.Of(splat), treesStage = Stat.Of(trees),
                maxPosts = posts, maxTexels = texels, maxClearedTrees = cleared, maxCacheTiles = tiles,
                undoByteIdentical = identical, idleFrames = idleSummary.frames, idleGarbageBytes = idleSummary.gcBytesTotal,
                previewBudgetMs = 100, commitBudgetMs = 250,
            };
            report.pass = report.preview.p95 <= report.previewBudgetMs && report.commitApplied.p95 <= report.commitBudgetMs && identical
                          && (!Debug.isDebugBuild || report.idleGarbageBytes == 0);
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            Debug.Log(string.Format(CultureInfo.InvariantCulture,
                "[EditBenchmark] preview p95 {0:F1} ms (max {1:F1}), commit p95 {2:F1} ms (max {3:F1}), undo p95 {4:F1} ms; undo identical {5}; idle garbage {6} B; {7}",
                report.preview.p95, report.preview.max, report.commitApplied.p95, report.commitApplied.max, report.undo.p95, identical,
                report.idleGarbageBytes, report.pass ? "PASS" : "FAIL"));
            Debug.Log($"[EditBenchmark] written to {outPath}");
            Application.Quit();
        }

        Dictionary<(int, int), float[,]> SnapshotCoreHeights()
        {
            var shots = new Dictionary<(int, int), float[,]>();
            foreach (var kv in _resort.Tiles)
            {
                var data = kv.Value.terrainData;
                if (data.heightmapResolution != World.TerrainTiles.Resolution) continue;
                shots[kv.Key] = data.GetHeights(0, 0, data.heightmapResolution, data.heightmapResolution);
            }
            return shots;
        }

        static bool SameHeights(Dictionary<(int, int), float[,]> a, Dictionary<(int, int), float[,]> b)
        {
            foreach (var kv in a)
            {
                if (!b.TryGetValue(kv.Key, out var other)) return false;
                var x = kv.Value;
                for (int j = 0; j < x.GetLength(0); j++)
                    for (int i = 0; i < x.GetLength(1); i++)
                        if (BitConverter.SingleToInt32Bits(x[j, i]) != BitConverter.SingleToInt32Bits(other[j, i])) return false;
            }
            return true;
        }
    }

    /// <summary>-editbench's JSON (task P3-05): the edit paths' timings against CF-09's budgets.</summary>
    [Serializable]
    public sealed class EditBenchmarkReport
    {
        public string schema = "mountain-planner-edit-benchmark/1";
        public string commit, measuredUtc, gpu, cpu, site, quality;
        public bool commitDirty, development;
        public int width, height, spots, rounds;
        public float benchLengthM, benchWidthM;
        public Stat preview, commitApplied, undo, planStage, heightsStage, splatStage, treesStage;
        public int maxPosts, maxTexels, maxClearedTrees, maxCacheTiles;
        public bool undoByteIdentical;
        public int idleFrames;
        public long idleGarbageBytes;
        public float previewBudgetMs, commitBudgetMs;
        public bool pass;
    }

    [Serializable]
    public struct Stat
    {
        public int count;
        public float mean, p50, p95, max;

        public static Stat Of(List<double> values)
        {
            var sorted = values.OrderBy(v => v).ToArray();
            if (sorted.Length == 0) return default;
            float At(double q) => (float)sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
            return new Stat { count = sorted.Length, mean = (float)sorted.Average(), p50 = At(0.5), p95 = At(0.95), max = (float)sorted[sorted.Length - 1] };
        }
    }
}
