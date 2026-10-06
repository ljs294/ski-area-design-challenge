using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Domain.Flora;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.App
{
    /// <summary>
    /// Review tools for the forest (tree realism review): repeatable evidence rather than impressions.
    ///   -benchmark &lt;out.json&gt;  flies the fixed camera path over the opened mountain (task 15), recording frame and
    ///                            GPU times, garbage and memory per leg and in total, checks them against the
    ///                            quality preset's budget (-quality), saves a screenshot of each leg, then quits.
    ///   -benchmark-views &lt;out.json&gt;  the earlier benchmark: 300 frames at each fixed view, with visible trees per LOD.
    ///   -lineup &lt;out prefix&gt;    no mountain: every species in a row on flat snow, captured at each forced LOD
    ///                            from the side, from above and against the sun, close-ups, trunks from a few
    ///                            metres, and a mixed stand seen from near to far at natural LOD. Then quits.
    ///   -shadows &lt;metres&gt;      the sun's shadow distance for this run (players only), to measure its cost.
    ///   -clip &lt;out prefix&gt;      3 s of frames at a fixed 24 fps from the opened mountain (wind review), then quits.
    /// </summary>
    public sealed partial class MountainViewer
    {
        struct BenchView
        {
            public string Name;
            public float X, Z, Distance, Yaw, Pitch;
            public BenchView(string name, float x, float z, float distance, float yaw, float pitch)
            {
                Name = name; X = x; Z = z; Distance = distance; Yaw = yaw; Pitch = pitch;
            }
        }

        /// <summary>Fixed views over Jackson Hole (metres from the site centre; degrees). Keep them stable: before/after depends on it.</summary>
        static readonly BenchView[] BenchViews =
        {
            new BenchView("overview", float.NaN, 0, 0, 200, 24),
            new BenchView("corbet", float.NaN, 1, 600, 200, 24),
            new BenchView("valley", 600, -900, 1500, 160, 22),
            new BenchView("slope", -300, 700, 1200, 200, 18),
            new BenchView("forest", 350, -150, 250, 200, 30),
            new BenchView("inforest", 350, -150, 70, 200, 6),
            new BenchView("cliffs", -1500, 560, 300, 250, 14),
            new BenchView("ringforest", 2560, 0, 900, 90, 12),
        };

        /// <summary>
        /// Views for a site other than Jackson Hole, chosen from its own forest: the overview, the core tile with
        /// the most trees from 250 m and from inside, the core's highest point, and the ring tile with the most
        /// trees from 900 m.
        /// </summary>
        BenchView[] SiteViews()
        {
            if (_resort.Manifest.Site.Name == "Jackson Hole") return BenchViews;
            var cache = _resort.Cache;
            Vector2 Centre(MountainPlanner.Persistence.CacheTile t)
            {
                var (x, z) = _resort.Frame.ToLocal(new MountainPlanner.Domain.Geo.AlbersPoint(t.West + cache.TileMetres / 2, t.North - cache.TileMetres / 2));
                return new Vector2((float)x, (float)z);
            }
            var core = Centre(cache.Tiles.Where(t => t.Core).OrderByDescending(t => t.TreeCount).First());
            var ring = Centre(cache.Tiles.Where(t => !t.Core).OrderByDescending(t => t.TreeCount).First());
            float half = _resort.Manifest.Site.SizeMetres / 2f;
            Vector2 summit = Vector2.zero;
            float top = float.MinValue;
            for (float x = -half; x <= half; x += 50)
                for (float z = -half; z <= half; z += 50)
                {
                    float h = _resort.Surface.HeightAt(x, z);
                    if (h > top) { top = h; summit = new Vector2(x, z); }
                }
            return new[]
            {
                new BenchView("overview", float.NaN, 0, 0, 200, 24),
                new BenchView("forest", core.x, core.y, 250, 200, 30),
                new BenchView("inforest", core.x, core.y, 70, 200, 6),
                new BenchView("summit", summit.x, summit.y, 1200, 200, 20),
                new BenchView("ringforest", ring.x, ring.y, 900, 90, 12),
            };
        }

        static float ShadowDistance => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp ? urp.shadowDistance : QualitySettings.shadowDistance;

        bool StartReviewTools()
        {
            string[] args = Environment.GetCommandLineArgs();
            int s = Array.IndexOf(args, "-shadows");
            if (s >= 0 && s + 1 < args.Length && !Application.isEditor && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset asset)
                asset.shadowDistance = float.Parse(args[s + 1], CultureInfo.InvariantCulture);   // the player's copy; the project asset is untouched
            int i = Array.IndexOf(args, "-lineup");
            if (i < 0 || i + 1 >= args.Length) return false;
            StartCoroutine(RunLineup(args[i + 1]));
            return true;
        }

        void PlaceBenchCamera(BenchView v)
        {
            if (float.IsNaN(v.X) && v.Z == 0)
            {
                float centre = _resort.Surface.HeightAt(0, 0);
                Camera.Frame(new Vector3(0, centre, 0), _resort.Manifest.Site.SizeMetres * 1.1f);
            }
            else if (float.IsNaN(v.X)) Camera.Frame(_landmarks.Count > 0 ? _landmarks[0].Centre : Vector3.zero, v.Distance);
            else Camera.Frame(new Vector3(v.X, _resort.Surface.HeightAt(v.X, v.Z), v.Z), v.Distance);
            Camera.SetAngles(v.Yaw, v.Pitch);
        }

        /// <summary>The benchmark path's legs: the fixed views, in order, as poses (targets on the ground, metres from the site centre).</summary>
        BenchmarkPath.Leg[] BenchLegs()
        {
            var views = SiteViews();
            var legs = new BenchmarkPath.Leg[views.Length];
            for (int i = 0; i < views.Length; i++)
            {
                var v = views[i];
                Vector3 target;
                float distance = v.Distance;
                if (float.IsNaN(v.X) && v.Z == 0)
                {
                    target = new Vector3(0, _resort.Surface.HeightAt(0, 0), 0);
                    distance = _resort.Manifest.Site.SizeMetres * 1.1f;
                }
                else if (float.IsNaN(v.X)) target = _landmarks.Count > 0 ? _landmarks[0].Centre : Vector3.zero;
                else target = new Vector3(v.X, _resort.Surface.HeightAt(v.X, v.Z), v.Z);
                legs[i] = new BenchmarkPath.Leg(v.Name, target, distance, v.Yaw, v.Pitch);
            }
            return legs;
        }

        /// <summary>The mountain, its forest and its cover are in: the benchmark (and its PlayMode test) can start.</summary>
        internal bool BenchmarkReady => _resort != null && Forest != null && _resort.CoverReady.IsCompleted;

        /// <summary>The fixed camera path for the open mountain (the PlayMode performance test flies it too).</summary>
        internal BenchmarkPath CreateBenchmarkPath() => new BenchmarkPath(BenchLegs());

        internal int PlaceOnPath(BenchmarkPath path, float seconds)
        {
            int leg = path.Evaluate(seconds, out var pose);
            if (Camera != null)
            {
                Camera.Frame(pose.Target, pose.Distance);
                Camera.SetAngles(pose.Yaw, pose.Pitch);
            }
            return leg;
        }

        /// <summary>
        /// -benchmark &lt;out.json&gt; (task 15; 0.3 §8): flies the fixed camera path (<see cref="BenchmarkPath"/>) once to
        /// warm up, taking a screenshot of each leg, then -laps times (2 by default) recording every frame, per leg and
        /// in total, without allocating. Writes a <see cref="BenchmarkReport"/> checked against the preset's
        /// <see cref="PerformanceBudget"/>, then quits. -withhud measures with the HUD on.
        /// </summary>
        IEnumerator RunBenchmark(string outPath)
        {
            string[] args = Environment.GetCommandLineArgs();
            _hud = Array.IndexOf(args, "-withhud") >= 0;
            int laps = 2;
            int lapsArg = Array.IndexOf(args, "-laps");
            if (lapsArg >= 0 && lapsArg + 1 < args.Length && int.TryParse(args[lapsArg + 1], out int l)) laps = Mathf.Max(1, l);
            while (Forest == null) yield return null;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            if (Camera != null) Camera.InputEnabled = false;
            var path = new BenchmarkPath(BenchLegs());
            string prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", Path.GetFileNameWithoutExtension(outPath));

            // Warm-up lap: shaders, LODs, shadows and streaming settle; each leg's screenshot is taken here, not while measuring.
            int shot = -1;
            float start = Time.unscaledTime;
            for (float t = 0; t < path.LapSeconds; t = Time.unscaledTime - start)
            {
                int leg = PlaceOnPath(path, t);
                if (leg != shot && BenchmarkPath.NearHoldEnd(t))
                {
                    shot = leg;
                    ScreenCapture.CaptureScreenshot($"{prefix}_{path.LegName(leg)}.png");
                }
                yield return null;
            }

            // Measured laps: nothing below allocates per frame (FrameStats records into preallocated buffers).
            var total = new FrameStats(1 << 20);
            var legs = new FrameStats[path.LegCount];
            for (int i = 0; i < legs.Length; i++) legs[i] = new FrameStats(1 << 17);
            // One second at the path's start before measuring: the last warm-up screenshot finishes saving here, not in a measured frame.
            for (start = Time.unscaledTime; Time.unscaledTime - start < 1f;)
            {
                PlaceOnPath(path, 0);
                yield return null;
            }
            total.Reset();   // marks the managed heap for the garbage check
            foreach (var s in legs) s.Reset();
            start = Time.unscaledTime;
            float duration = laps * path.LapSeconds;
            for (float t = 0; t < duration; t = Time.unscaledTime - start)
            {
                int leg = PlaceOnPath(path, t);
                yield return null;
                float gpu = FrameStats.SampleGpuMs();
                float dt = Time.unscaledDeltaTime;
                total.Record(dt, gpu);
                legs[leg].Record(dt, gpu);
            }
            total.Stop();

            var report = BuildReport(path, laps, total, legs);
            total.Dispose();
            foreach (var s in legs) s.Dispose();
            File.WriteAllText(outPath, JsonUtility.ToJson(report, true));
            foreach (var leg in report.legs)
                Debug.Log(string.Format(CultureInfo.InvariantCulture, "[Benchmark] {0}: frame p95 {1:F2} ms, GPU p95 {2:F2} ms, garbage {3} B, draw calls {4}",
                                        leg.name, leg.stats.p95Ms, leg.stats.gpuP95Ms, leg.stats.gcBytesTotal, leg.stats.drawCalls));
            Debug.Log("[Benchmark] " + report.result.summary);
            Debug.Log($"[Benchmark] written to {outPath}");
            Application.Quit();
        }

        BenchmarkReport BuildReport(BenchmarkPath path, int laps, FrameStats total, FrameStats[] legs)
        {
            var urp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            var build = BuildInfo.Current;
            var preset = QualityPresets.Current;
            var budget = PerformanceBudget.For(preset);
            var t = total.Summarise();
            var report = new BenchmarkReport
            {
                commit = build.commit,
                commitDirty = build.dirty,
                builtUtc = build.builtUtc,
                measuredUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                gpu = SystemInfo.graphicsDeviceName,
                gpuMemoryMB = SystemInfo.graphicsMemorySize,
                cpu = SystemInfo.processorType,
                systemMemoryMB = SystemInfo.systemMemorySize,
                unity = Application.unityVersion,
                development = Debug.isDebugBuild,
                quality = preset.ToString(),
                width = Screen.width,
                height = Screen.height,
                renderScale = urp != null ? urp.renderScale : 1f,
                msaa = urp != null ? urp.msaaSampleCount : QualitySettings.antiAliasing,
                shadowDistance = ShadowDistance,
                lodBias = QualitySettings.lodBias,
                terrainDetail = Detail.ToString(),
                instancedTerrain = TerrainTiles.DrawInstanced,
                terrainShading = _farShadows != null && _farShadows.Enabled,
                hud = _hud,
                site = _resort.Manifest.Site.Name,
                siteSizeKm = (float)(_resort.Manifest.Site.SizeMetres / 1000.0),
                trees = Forest.TreeCount,
                laps = laps,
                lapSeconds = path.LapSeconds,
                total = t,
                legs = new BenchmarkReport.Leg[legs.Length],
                budget = new BenchmarkReport.Budget
                {
                    isChecked = budget.Checked, p95Ms = budget.P95Ms, p99Ms = budget.P99Ms,
                    percentOver50Ms = budget.PercentOver50Ms, gfxMemoryMB = budget.GfxMemoryMB, gcBytes = 0,
                },
            };
            for (int i = 0; i < legs.Length; i++) report.legs[i] = new BenchmarkReport.Leg { name = path.LegName(i), stats = legs[i].Summarise() };

            var r = new BenchmarkReport.Result
            {
                frameTime = t.p95Ms <= budget.P95Ms && t.p99Ms <= budget.P99Ms && t.percentOver50Ms <= budget.PercentOver50Ms,
                // Garbage: the per-frame counter where the player records it (Development builds), else the heap check.
                garbageMeasured = t.gcBytesTotal >= 0 || t.heapGrowthBytes >= 0,
                memoryMeasured = t.gfxMemoryMBMax >= 0,
            };
            // "0 bytes per frame in steady state" (0.3 §8): under a byte per frame on average, no collection, and no
            // recurring allocation (at most one frame in 10,000 allocates: a one-off such as a new text's first draw).
            r.garbage = t.gcBytesTotal >= 0
                ? t.gcBytesTotal < t.frames && t.gcFramesWithAllocations * 10000L <= Math.Max(10000, t.frames)
                : t.heapGrowthBytes < t.frames && t.gcCollections == 0;
            r.memory = r.memoryMeasured && t.gfxMemoryMBMax <= budget.GfxMemoryMB;
            // Release players don't record graphics memory; the Development run checks it (demo.bat runs both).
            r.all = budget.Checked && r.frameTime && r.garbage && (r.memory || !r.memoryMeasured);
            r.summary = string.Format(CultureInfo.InvariantCulture,
                "{0} at {1}x{2}{3}: frame p95 {4:F2} ms (≤{5}), p99 {6:F2} ms (≤{7}), over 50 ms {8:F2}% (≤{9}), GPU p95 {10:F2} ms, " +
                "garbage {11} ({12}), graphics memory {13} → {14}",
                preset, Screen.width, Screen.height, _hud ? " with the HUD" : "",
                t.p95Ms, Limit(budget.Checked, budget.P95Ms), t.p99Ms, Limit(budget.Checked, budget.P99Ms),
                t.percentOver50Ms, Limit(budget.Checked, budget.PercentOver50Ms), t.gpuP95Ms,
                t.gcBytesTotal >= 0 ? $"{t.gcBytesTotal} B in {t.gcFramesWithAllocations} of {t.frames} frames"
                    : t.heapGrowthBytes >= 0 ? $"heap +{t.heapGrowthBytes} B, {t.gcCollections} collections over {t.frames} frames" : "not measured",
                r.garbageMeasured ? (r.garbage ? "pass" : "FAIL") : "-",
                r.memoryMeasured ? $"{t.gfxMemoryMBMax:F0} MB (≤{budget.GfxMemoryMB:F0})" : "not measured",
                !budget.Checked ? "not checked" : r.all ? "PASS" : "FAIL");
            report.result = r;
            return report;
        }

        static string Limit(bool isChecked, float value) => isChecked ? value.ToString("0.#", CultureInfo.InvariantCulture) : "–";

        /// <summary>
        /// -pathmovie &lt;folder&gt;: one lap of the benchmark path at a fixed 15 frames a second (game time steps exactly
        /// 1/15 s), each frame saved as &lt;folder&gt;/path/f_&lt;ms&gt;.jpg for the owner's review movie
        /// (Editor/PickerLabSetup.EncodeMovies turns the folder into path.mp4). Then quits. Not a measurement.
        /// </summary>
        IEnumerator RecordPathMovie(string folder)
        {
            _hud = Array.IndexOf(Environment.GetCommandLineArgs(), "-withhud") >= 0;
            while (Forest == null) yield return null;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            if (Camera != null) Camera.InputEnabled = false;
            var path = new BenchmarkPath(BenchLegs());
            string frames = Path.Combine(folder, "path");
            Directory.CreateDirectory(frames);
            for (int i = 0; i < 90; i++) { PlaceOnPath(path, 0); yield return null; }   // LODs and shadows settle
            const int fps = 15;
            int count = Mathf.CeilToInt(path.LapSeconds * fps);
            for (int k = 0; k < count; k++)
            {
                PlaceOnPath(path, k / (float)fps);
                yield return new WaitForEndOfFrame();
                var shot = ScreenCapture.CaptureScreenshotAsTexture();
                File.WriteAllBytes(Path.Combine(frames, $"f_{k * 1000 / fps:D6}.jpg"), shot.EncodeToJPG(85));
                Destroy(shot);
                yield return null;
            }
            Debug.Log($"[MountainViewer] {count} path frames saved to {frames}");
            Application.Quit();
        }

        /// <summary>-benchmark-views &lt;out.json&gt;: the earlier benchmark, 300 frames at each of the fixed views (kept for comparisons with older reports).</summary>
        IEnumerator RunViewBenchmark(string outPath)
        {
            _hud = Array.IndexOf(Environment.GetCommandLineArgs(), "-withhud") >= 0;   // -withhud: measure with the HUD on
            while (Forest == null) yield return null;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            string prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", Path.GetFileNameWithoutExtension(outPath));
            var json = new StringBuilder();
            json.Append("{\n  \"screen\": [").Append(Screen.width).Append(", ").Append(Screen.height).Append("],\n  \"shadowDistance\": ")
                .Append(ShadowDistance.ToString("F0", CultureInfo.InvariantCulture)).Append(",\n  \"trees\": ").Append(Forest.TreeCount)
                .Append(",\n  \"forestDraws\": ").Append(Forest.DrawCount)
                .Append(",\n  \"forestGpuMB\": ").Append((Forest.GpuBytes / 1e6).ToString("F1", CultureInfo.InvariantCulture))
                .Append(",\n  \"site\": \"").Append(_resort.Manifest.Site.Name)
                .Append("\",\n  \"wind\": \"").Append(Forest.Wind.Target.ToString().ToLowerInvariant())
                .Append("\",\n  \"views\": [\n");
            // Unity's own counters: draw calls, batches and set-pass calls per frame, and memory.
            using var drawCalls = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Draw Calls Count");
            using var batches = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setPass = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            using var gfxMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Gfx Used Memory");
            using var totalMemory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            var views = SiteViews();
            var timings = new FrameTiming[1];
            for (int vi = 0; vi < views.Length; vi++)
            {
                var v = views[vi];
                PlaceBenchCamera(v);
                for (int f = 0; f < 90; f++) yield return null;   // settle
                var frame = new List<double>();
                var gpu = new List<double>();
                var draws = new List<double>();
                for (int f = 0; f < 300; f++)
                {
                    yield return null;
                    frame.Add(Time.unscaledDeltaTime * 1000.0);
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) gpu.Add(timings[0].gpuFrameTime);
                    if (drawCalls.Valid && drawCalls.LastValue > 0) draws.Add(drawCalls.LastValue);
                }
                int[] lods = null;
                double triangles = 0;
                Forest.RequestLodCounts((c, t) => { lods = c; triangles = t; });
                while (lods == null) yield return null;
                string shot = $"{prefix}_{v.Name}.png";
                ScreenCapture.CaptureScreenshot(shot);
                yield return null;
                yield return null;
                json.Append("    {\"view\": \"").Append(v.Name).Append("\", \"frameMs\": ").Append(Stats(frame)).Append(", \"gpuMs\": ").Append(Stats(gpu))
                    .Append(", \"visibleTreesPerLod\": [").Append(string.Join(", ", lods)).Append("], \"treeTriangles\": ")
                    .Append(triangles.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(", \"drawCalls\": ").Append(draws.Count > 0 ? draws.Average().ToString("F0", CultureInfo.InvariantCulture) : "null")
                    .Append(", \"batches\": ").Append(Counter(batches, 1))
                    .Append(", \"setPassCalls\": ").Append(Counter(setPass, 1))
                    .Append(", \"gfxMemoryMB\": ").Append(Counter(gfxMemory, 1e6))
                    .Append(", \"totalMemoryMB\": ").Append(Counter(totalMemory, 1e6))
                    .Append("}").Append(vi + 1 < views.Length ? ",\n" : "\n");
                Debug.Log($"[Benchmark] {v.Name}: frame {Stats(frame)} gpu {Stats(gpu)} lods [{string.Join(", ", lods)}] tris {triangles / 1e6:F1} M, " +
                          $"draw calls {(draws.Count > 0 ? draws.Average() : 0):F0}, batches {Counter(batches, 1)}, set-pass {Counter(setPass, 1)}, " +
                          $"gfx memory {Counter(gfxMemory, 1e6)} MB, total memory {Counter(totalMemory, 1e6)} MB");
            }
            json.Append("  ]\n}\n");
            File.WriteAllText(outPath, json.ToString());
            Debug.Log($"[Benchmark] written to {outPath}");
            Application.Quit();
        }

        /// <summary>
        /// -clip &lt;prefix&gt;: once the view has settled, captures 3 s of frames at a fixed 24 fps (game time
        /// steps exactly 1/24 s a frame, so the wind moves the same way every run), then quits.
        /// </summary>
        IEnumerator CaptureClipAndQuit(string prefix)
        {
            _hud = false;
            for (int i = 0; i < 90; i++) yield return null;   // let LOD and shadows settle
            Time.captureFramerate = 24;
            for (int frame = 0; frame < 72; frame++)
            {
                ScreenCapture.CaptureScreenshot($"{prefix}_{frame:000}.png");
                yield return null;
            }
            Time.captureFramerate = 0;
            yield return null;
            yield return null;
            Debug.Log($"[MountainViewer] Clip saved to {prefix}_000..071.png");
            Application.Quit();
        }

        /// <summary>A counter's last value in the given unit, or null where the player doesn't record it.</summary>
        static string Counter(ProfilerRecorder r, double unit) =>
            r.Valid && r.LastValue > 0 ? (r.LastValue / unit).ToString("F0", CultureInfo.InvariantCulture) : "null";

        static string Stats(List<double> ms)
        {
            if (ms.Count == 0) return "null";
            var s = ms.OrderBy(x => x).ToArray();
            double P(double q) => s[Math.Min(s.Length - 1, (int)Math.Floor(q * (s.Length - 1)))];
            return "{" + string.Format(CultureInfo.InvariantCulture, "\"mean\": {0:F2}, \"p50\": {1:F2}, \"p95\": {2:F2}, \"p99\": {3:F2}", s.Average(), P(0.5), P(0.95), P(0.99)) + "}";
        }

        // ---- lineup ------------------------------------------------------------------------------------

        IEnumerator RunLineup(string prefix)
        {
            _hud = false;
            yield return null;
            int models = Trees.Prefabs.Length / 3;
            const float spacing = 22f;

            // Flat snow: the cliff material with zero shell weight is pure snow.
            var ground = new GameObject("Lineup ground");
            var mesh = new Mesh { name = "LineupGround" };
            const float g = 6000;
            mesh.vertices = new[] { new Vector3(-g, 0, -g), new Vector3(-g, 0, g), new Vector3(g, 0, g), new Vector3(g, 0, -g) };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new Vector2[4];
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2 * g, 1, 2 * g));
            ground.AddComponent<MeshFilter>().sharedMesh = mesh;
            ground.AddComponent<MeshRenderer>().sharedMaterial = new Material(CliffMaterial);

            // A row of every species (variant 0), then a mixed stand 900 m north for far views.
            var trees = new List<ForestInstance>();
            for (int m = 0; m < models; m++)
                trees.Add(new ForestInstance { Position = new Vector3((m - (models - 1) / 2f) * spacing, 0, 0), HeightScale = 1, WidthScale = 1, Prototype = (uint)(m * 3) });
            var mix = new[] { 2, 2, 1, 1, 0, 0, 3, 3, 5, 5, 4 };   // roughly Jackson Hole: Douglas-fir, spruce, fir, lodgepole, aspen, hemlock
            for (int gz = 0; gz < 60; gz++)
                for (int gx = 0; gx < 60; gx++)
                {
                    uint h = (uint)(gx * 73856093 ^ gz * 19349663);
                    h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                    float jx = (h & 0xFF) / 255f - 0.5f, jz = ((h >> 8) & 0xFF) / 255f - 0.5f;
                    int model = mix[(h >> 16) % (uint)mix.Length];
                    int variant = (int)((h >> 24) % 3);
                    float scale = 0.7f + 0.35f * (((h >> 4) & 0xFF) / 255f);
                    trees.Add(new ForestInstance
                    {
                        Position = new Vector3((gx - 30 + jx) * 7f, 0, 900 + (gz - 30 + jz) * 7f),
                        Rotation = (h & 0xFFFF) / 65535f * 6.2831853f, HeightScale = scale, WidthScale = scale, Prototype = (uint)(model * 3 + variant),
                    });
                }
            ForestRenderer.BackTintOn = Array.IndexOf(Environment.GetCommandLineArgs(), "-nobacktint") < 0;
            var forest = new ForestRenderer(Trees, trees.ToArray(), ForestCull, TreeShader, TreeImpostorShader);
            forest.Wind.Set(ForestWind.Level.Calm);   // still trees, so every capture is repeatable
            var forestGo = new GameObject("Lineup forest");
            forestGo.AddComponent<ForestView>().Renderer = forest;

            Camera.Surface = null;
            var camera = Camera.GetComponent<UnityEngine.Camera>();
            camera.farClipPlane = 20000;

            IEnumerator Shot(string name, Vector3 target, float distance, float yaw, float pitch, int lod)
            {
                forest.ForcedLod = lod;
                Camera.Frame(target, distance);
                Camera.SetAngles(yaw, pitch);
                for (int f = 0; f < 40; f++) yield return null;
                ScreenCapture.CaptureScreenshot($"{prefix}_{name}.png");
                yield return null;
                yield return null;
            }

            // From straight overhead (task P2-09): the mixed stand at a forced LOD2 and as impostors, so the far
            // impostor can be held against the mesh it replaces; -lineupset top takes only these.
            var top = new Vector3(0, 8, 900);
            foreach (float distance in new[] { 200f, 600f })
                foreach (int lod in new[] { 0, 2, 3 })
                    yield return Shot($"top{distance:0}_lod{lod}", top, distance, 200, 89, lod);
            yield return Shot("top600_auto", top, 600, 200, 89, -1);
            yield return Shot("top1500_auto", top, 1500, 200, 89, -1);
            // Silver and noble fir from under their lower branches (the underside tint).
            foreach (string species in new[] { "pacific_silver_fir", "noble_fir", "subalpine_fir" })
            {
                int m = SpeciesMap.IndexOf(species);
                if (m >= 0 && m < models) yield return Shot($"under_{species}", new Vector3((m - (models - 1) / 2f) * spacing, Trees.NativeHeights[m * 3] * 0.3f, 0), 9, 200, 2, 0);
            }
            // Beech and red oak keep brown leaves through the winter: LOD0 against LOD2 (the backlog's "strips").
            foreach (string species in new[] { "american_beech", "northern_red_oak" })
            {
                int m = SpeciesMap.IndexOf(species);
                if (m < 0 || m >= models) continue;
                foreach (int lod in new[] { 0, 2 })
                    yield return Shot($"leaves_{species}_lod{lod}", new Vector3((m - (models - 1) / 2f) * spacing, 9f, 0), 45, 180, 8, lod);
            }
            int set = Array.IndexOf(Environment.GetCommandLineArgs(), "-lineupset");
            if (set >= 0 && set + 1 < Environment.GetCommandLineArgs().Length && Environment.GetCommandLineArgs()[set + 1] == "top")
            {
                Debug.Log($"[Lineup] captured to {prefix}_top*.png");
                Application.Quit();
                yield break;
            }

            var row = new Vector3(0, 12, 0);
            for (int lod = 0; lod < 4; lod++)
            {
                yield return Shot($"lod{lod}_side", row, 140, 180, 4, lod);     // front-lit (the sun is behind the camera)
                yield return Shot($"lod{lod}_high", row, 140, 180, 35, lod);    // the usual elevated game camera
                yield return Shot($"lod{lod}_back", row, 140, 0, 4, lod);       // against the sun
            }
            for (int c = 0; c < 4; c++)
            {
                float x = (c * 3 + 1 - (models - 1) / 2f) * spacing;
                yield return Shot($"close{c}", new Vector3(x, 10, 0), 48, 180, 8, 0);
            }
            // Trunks up close (as close as the camera goes), the sun raking across them: bark and root flare.
            foreach (string species in new[] { "douglas_fir", "engelmann_spruce", "subalpine_fir", "lodgepole_pine", "mountain_hemlock", "quaking_aspen", "paper_birch" })
            {
                int m = SpeciesMap.IndexOf(species);
                if (m >= 0 && m < models) yield return Shot($"trunk_{species}", new Vector3((m - (models - 1) / 2f) * spacing, 1.8f, 0), 7, 240, 6, 0);
            }
            var stand = new Vector3(0, 8, 900);
            yield return Shot("stand_300", stand, 300, 180, 25, -1);
            yield return Shot("stand_800", stand, 800, 180, 25, -1);
            yield return Shot("stand_1600", stand, 1600, 180, 25, -1);
            yield return Shot("stand_3200", stand, 3200, 180, 25, -1);
            Debug.Log($"[Lineup] captured to {prefix}_*.png");
            Application.Quit();
        }
    }
}
