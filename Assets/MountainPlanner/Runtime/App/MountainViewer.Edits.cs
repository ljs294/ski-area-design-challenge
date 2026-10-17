using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using MountainPlanner.App.Edits;
using MountainPlanner.Domain.Design;
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
            if (!_editKeys && bench < 0 && timing < 0) return;
            _editsStarting = true;
            StartCoroutine(StartEditsWhenReady(args, bench, timing));
        }

        IEnumerator StartEditsWhenReady(string[] args, int bench, int timing)
        {
            // A fresh design on this area; task 04's open design replaces it when saves load.
            var task = TerrainEditSession.StartAsync(_resort, new DesignHistory(DesignDocument.Empty(1)), destroyCancellationToken);
            while (!task.IsCompleted) yield return null;
            _editsStarting = false;
            if (task.IsFaulted)
            {
                Debug.LogException(task.Exception);
                yield break;
            }
            if (task.IsCanceled) yield break;
            _edits = task.Result;
            Debug.Log("[TerrainEdits] ready" + (_editKeys ? ": F6 preview a bench at the pointer, F7 break ground, F8 cancel, Ctrl+Z / Ctrl+Y" : ""));
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
        }

        /// <summary>True while edits are starting or applying: captures wait for them.</summary>
        bool EditsBusy => _editsStarting || (_edits != null && !_edits.Applying.IsCompleted);

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
            else if (ctrl && keyboard.zKey.wasPressedThisFrame) _edits.History.Undo();
            else if (ctrl && keyboard.yKey.wasPressedThisFrame) _edits.History.Redo();
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
