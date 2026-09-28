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
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.App
{
    /// <summary>
    /// Review tools for the forest (tree realism review): repeatable evidence rather than impressions.
    ///   -benchmark &lt;out.json&gt;  flies fixed views over the opened mountain, recording frame and GPU times,
    ///                            visible trees per LOD and a screenshot of each view, then quits.
    ///   -lineup &lt;out prefix&gt;    no mountain: every species in a row on flat snow, captured at each forced LOD
    ///                            from the side, from above and against the sun, close-ups, trunks from a few
    ///                            metres, and a mixed stand seen from near to far at natural LOD. Then quits.
    ///   -shadows &lt;metres&gt;      the sun's shadow distance for this run (players only), to measure its cost.
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

        IEnumerator RunBenchmark(string outPath)
        {
            _hud = false;
            while (Forest == null) yield return null;
            while (!_resort.CoverReady.IsCompleted) yield return null;
            string prefix = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".", Path.GetFileNameWithoutExtension(outPath));
            var json = new StringBuilder();
            json.Append("{\n  \"screen\": [").Append(Screen.width).Append(", ").Append(Screen.height).Append("],\n  \"shadowDistance\": ")
                .Append(ShadowDistance.ToString("F0", CultureInfo.InvariantCulture)).Append(",\n  \"trees\": ").Append(Forest.TreeCount)
                .Append(",\n  \"views\": [\n");
            var timings = new FrameTiming[1];
            for (int vi = 0; vi < BenchViews.Length; vi++)
            {
                var v = BenchViews[vi];
                PlaceBenchCamera(v);
                for (int f = 0; f < 90; f++) yield return null;   // settle
                var frame = new List<double>();
                var gpu = new List<double>();
                for (int f = 0; f < 300; f++)
                {
                    yield return null;
                    frame.Add(Time.unscaledDeltaTime * 1000.0);
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0) gpu.Add(timings[0].gpuFrameTime);
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
                    .Append(triangles.ToString("F0", CultureInfo.InvariantCulture)).Append("}").Append(vi + 1 < BenchViews.Length ? ",\n" : "\n");
                Debug.Log($"[Benchmark] {v.Name}: frame {Stats(frame)} gpu {Stats(gpu)} lods [{string.Join(", ", lods)}] tris {triangles / 1e6:F1} M");
            }
            json.Append("  ]\n}\n");
            File.WriteAllText(outPath, json.ToString());
            Debug.Log($"[Benchmark] written to {outPath}");
            Application.Quit();
        }

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
            var forest = new ForestRenderer(Trees, trees.ToArray(), ForestCull, TreeShader, TreeImpostorShader);
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
