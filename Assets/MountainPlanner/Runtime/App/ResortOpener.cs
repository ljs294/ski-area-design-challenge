using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.Presentation;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>What the opener is doing, for the loading status line (U6).</summary>
    public readonly struct OpenProgress
    {
        public readonly string Detail;
        public readonly float Fraction;

        public OpenProgress(string detail, float fraction)
        {
            Detail = detail;
            Fraction = fraction;
        }
    }

    /// <summary>What the forest needs: the tree library, the culling compute shader and the tree shader.</summary>
    public sealed class ForestAssets
    {
        public TreePrototypeSet Trees;
        /// <summary>Cliff shells (optional): the Cliff.shader material.</summary>
        public Material Cliff;
        /// <summary>The diorama base at the edge of the data (optional): the DioramaWall.shader material.</summary>
        public Material Edge;
        public ComputeShader Cull;
        public Shader Shader;
        /// <summary>Far trees (TreeImpostor.shader).</summary>
        public Shader ImpostorShader;
        public bool IsComplete => Trees != null && Cull != null && Shader != null && ImpostorShader != null && SystemInfo.supportsComputeShaders;
    }

    /// <summary>A resort on screen.</summary>
    public sealed class OpenedResort
    {
        public PackageManifest Manifest;
        public CacheManifest Cache;
        public GameObject Root;
        public Dictionary<(int, int), Terrain> Tiles;
        public ITerrainSurface Surface;
        public LocalFrame Frame;
        public GroundLayers Ground;
        public string PackageFolder;
        public bool SnowOn = true;
        /// <summary>Completes when every tile's ground cover is painted (it streams in after the terrain is playable).</summary>
        public Task CoverReady = Task.CompletedTask;
        public double CoverSeconds;
        public long TreesPlanted;
        public long CliffTriangles;
        public Material CliffMaterial;
        /// <summary>The diorama walls (their snow cap follows the snow).</summary>
        public Material EdgeMaterial;
        public double ForestSeconds;
        public double Seconds;
    }

    /// <summary>
    /// Opens a downloaded resort from disk (0.3 §4.3 step 4; task 06). No network: everything comes from
    /// the package. Heights decode on worker threads while the main thread creates a few Terrain tiles
    /// per frame, so the window stays responsive and the status line keeps moving.
    /// </summary>
    public static class ResortOpener
    {
        public const int TilesPerFrame = 4;

        public static async Task<OpenedResort> OpenAsync(string packageFolder, Transform parent, TerrainDetail detail,
                                                         IProgress<OpenProgress> progress, CancellationToken ct = default, Material material = null,
                                                         ForestAssets forest = null)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new OpenProgress("Opening: reading the package", 0));
            var manifest = await Task.Run(() => ResortPackage.ReadManifest(packageFolder), ct);

            if (!TerrainCache.IsCurrent(packageFolder, manifest))
            {
                var tileProgress = new Progress<CacheProgress>(p =>
                    progress?.Report(new OpenProgress($"Preparing terrain: tile {p.Tile} of {p.Tiles}", 0.5f * p.Tile / p.Tiles)));
                await Task.Run(() => TerrainCache.Build(packageFolder, manifest, tileProgress, ct, new BurstForestPlanter()), ct);   // the forest grows with Burst
            }
            var cache = await Task.Run(() => TerrainCache.ReadManifest(packageFolder), ct);
            var frame = new LocalFrame(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY));

            var root = new GameObject($"Resort: {manifest.Site.Name}");
            if (parent != null) root.transform.SetParent(parent, false);
            var ground = new GroundLayers();
            var ringBox = SiteSquare.Create(frame.Origin, manifest.Site.SizeMetres / 1000.0).Ring;
            var (rw, rs) = frame.ToLocal(new AlbersPoint(ringBox.West, ringBox.South));
            var ringRect = new Rect((float)rw, (float)rs, (float)ringBox.Width, (float)ringBox.Height);
            if (TerrainTiles.UsesMountainShader(material))
            {
                material = new Material(material) { name = material.name + " (" + manifest.Site.Name + ")" };
                ground.Configure(material, ringRect);
            }
            // Tiles start with just the snow layer (all a fresh tile shows); the ground layers join when its
            // cover is painted. Six layers per tile up front added about half a second to opening.
            var layers = new[] { ground.Layers[0] };
            var tiles = new Dictionary<(int, int), Terrain>();

            // Decode every tile on worker threads (in order of distance from the centre, so the view fills in
            // from the middle); create Terrains on the main thread as they become ready.
            var order = cache.Tiles.OrderBy(t => Distance(t, cache, frame)).ToList();
            var decoded = order.Select(t => Task.Run(() => TerrainTiles.LoadHeights(packageFolder, t), ct)).ToList();
            // Cover decodes alongside; it's painted after the terrain is up (snow is layer 0, so tiles already read as snow).
            var covers = order.Select(t => Task.Run(() => SplatTexels.Load(packageFolder, t, snow: true), ct)).ToList();
            List<Task<ForestInstance[]>> forests = null;
            if (forest != null && forest.IsComplete)
            {
                float[] nativeHeights = forest.Trees.NativeHeights;
                float tileMetres = (float)cache.TileMetres, heightMin = (float)cache.HeightMin, heightRange = (float)cache.HeightRange;
                forests = order.Select((t, i) =>
                {
                    var (ox, oz) = frame.ToLocal(new AlbersPoint(t.West, t.North - cache.TileMetres));
                    var origin = new Vector3((float)ox, 0, (float)oz);
                    var heightsTask = decoded[i];
                    return Task.Run(async () => ForestInstance.Decode(TerrainCache.ReadTrees(packageFolder, t), nativeHeights,
                                                                      await heightsTask.ConfigureAwait(false), origin, tileMetres, heightMin, heightRange), ct);
                }).ToList();
            }
            List<Task<CliffShells.Prepared>> cliffTasks = null;
            if (forest != null && forest.Cliff != null)
                cliffTasks = order.Select(t => Task.Run(() => CliffShells.Prepare(TerrainCache.ReadCliffs(packageFolder, t)), ct)).ToList();
            float start = progress == null ? 0 : 0.5f;
            double waited = 0, created = 0;
            var part = Stopwatch.StartNew();
            for (int n = 0; n < order.Count; n++)
            {
                part.Restart();
                float[,] heights = await decoded[n];
                waited += part.Elapsed.TotalSeconds;
                part.Restart();
                var tile = order[n];
                tiles[(tile.Column, tile.Row)] = TerrainTiles.Create(root.transform, cache, tile, heights, frame, material, layers, detail, TerrainTiles.UnpaintedSplatResolution);
                created += part.Elapsed.TotalSeconds;
                progress?.Report(new OpenProgress($"Opening terrain: tile {n + 1} of {order.Count}", start + (1 - start) * (n + 1) / order.Count));
                if ((n + 1) % TilesPerFrame == 0) await Task.Yield();
            }
            TerrainTiles.ConnectNeighbours(tiles);
            UnityEngine.Debug.Log($"[ResortOpener] {order.Count} tiles: waiting for decode {waited:F2} s, creating terrains {created:F2} s " +
                                  $"(setup {TerrainTiles.ProfileSetup:F2}, heights {TerrainTiles.ProfileSetHeights:F2}, sync {TerrainTiles.ProfileSync:F2}), total {clock.Elapsed.TotalSeconds:F2} s");

            var first = cache.Tiles.OrderBy(t => t.Row).ThenBy(t => t.Column).First();
            var (tw, tn) = frame.ToLocal(new AlbersPoint(first.West, first.North));
            var surface = new UnityTerrainSurface(tiles, (float)tw, (float)tn, (float)cache.TileMetres, ringRect);
            progress?.Report(new OpenProgress("Ready", 1));
            var resort = new OpenedResort
            {
                Manifest = manifest, Cache = cache, Root = root, Tiles = tiles, Surface = surface, Frame = frame, Seconds = clock.Elapsed.TotalSeconds,
                Ground = ground, PackageFolder = packageFolder,
            };
            if (forest?.Edge != null)
            {
                // Sampled a few centimetres inside the cut, where the terrain has data on every side.
                float Edge(float x, float z) => surface.HeightAt(Mathf.Clamp(x, ringRect.xMin + 0.05f, ringRect.xMax - 0.05f),
                                                                 Mathf.Clamp(z, ringRect.yMin + 0.05f, ringRect.yMax - 0.05f));
                resort.EdgeMaterial = DioramaBase.Create(root.transform, ringRect, Edge, (float)cache.HeightMin - DioramaBase.BaseDepth, forest.Edge).Walls;
            }
            resort.CoverReady = PaintCoverAsync(resort, order, covers, cliffTasks, forests, forest, clock, ct);
            return resort;
        }

        /// <summary>Paints each tile's ground cover, nearest first, a few tiles per frame.</summary>
        static async Task PaintCoverAsync(OpenedResort resort, List<CacheTile> order, List<Task<SplatTexels>> covers, List<Task<CliffShells.Prepared>> cliffTasks,
                                          List<Task<ForestInstance[]>> forests, ForestAssets forest,
                                          Stopwatch clock, CancellationToken ct)
        {
            for (int n = 0; n < order.Count; n++)
            {
                var splat = await covers[n];
                ct.ThrowIfCancellationRequested();
                if (resort.Root == null) return; // closed meanwhile
                var terrain = resort.Tiles[(order[n].Column, order[n].Row)];
                terrain.terrainData.terrainLayers = resort.Ground.Layers;
                TerrainTiles.ApplySplat(terrain.terrainData, splat);
                TerrainTiles.BindSplat(terrain);
                if ((n + 1) % TilesPerFrame == 0) await Task.Yield();
            }
            resort.CoverSeconds = clock.Elapsed.TotalSeconds;
            UnityEngine.Debug.Log($"[ResortOpener] ground cover painted at {resort.CoverSeconds:F2} s");

            // Cliff shells: the rock volume the heightmap can't hold.
            if (cliffTasks != null)
            {
                resort.CliffMaterial = forest.Cliff;
                long triangles = 0;
                for (int n = 0; n < order.Count; n++)
                {
                    var shell = await cliffTasks[n];
                    if (resort.Root == null) return;
                    if (shell == null) continue;
                    var tile = order[n];
                    var (ox, oz) = resort.Frame.ToLocal(new AlbersPoint(tile.West, tile.North - resort.Cache.TileMetres));
                    CliffShells.Create(resort.Root.transform, $"Cliffs t{tile.Column}_{tile.Row}", new Vector3((float)ox, 0, (float)oz), shell, forest.Cliff);
                    triangles += shell.Indices.Length / 3;
                    if (n % TilesPerFrame == 0) await Task.Yield();
                }
                resort.CliffTriangles = triangles;
                UnityEngine.Debug.Log($"[ResortOpener] cliff shells ({triangles:N0} triangles) at {clock.Elapsed.TotalSeconds:F2} s");
            }
            if (forests == null) return;

            // Then the forest: every tile's trees go into one GPU buffer (ForestRenderer).
            var all = await Task.WhenAll(forests);
            ct.ThrowIfCancellationRequested();
            if (resort.Root == null) return;
            var instances = await Task.Run(() => all.SelectMany(a => a).ToArray(), ct);
            var renderer = new ForestRenderer(forest.Trees, instances, forest.Cull, forest.Shader, forest.ImpostorShader);
            var view = resort.Root.AddComponent<ForestView>();
            view.Renderer = renderer;
            resort.TreesPlanted = instances.Length;
            resort.ForestSeconds = clock.Elapsed.TotalSeconds;
            UnityEngine.Debug.Log($"[ResortOpener] {instances.Length:N0} trees planted at {resort.ForestSeconds:F2} s ({renderer.DrawCount} indirect draws)");
        }

        /// <summary>
        /// Shows or hides the snow (the "under the snow" view, T17): recomposes every tile's splat from the
        /// cache on worker threads, then uploads a few tiles per frame.
        /// </summary>
        public static async Task SetSnowAsync(OpenedResort resort, bool snow, CancellationToken ct = default)
        {
            await resort.CoverReady;
            resort.SnowOn = snow;
            if (resort.CliffMaterial != null) resort.CliffMaterial.SetFloat("_SnowLoad", snow ? 1 : 0);
            // Lakes keep their water weight either way; the shader draws snow on ice or bare ice.
            if (resort.Ground?.Material != null) resort.Ground.Material.SetFloat("_SnowOn", snow ? 1 : 0);
            if (resort.EdgeMaterial != null) resort.EdgeMaterial.SetFloat("_SnowOn", snow ? 1 : 0);
            var jobs = resort.Cache.Tiles.Select(t => (Tile: t, Splat: Task.Run(() => SplatTexels.Load(resort.PackageFolder, t, snow), ct))).ToList();
            int n = 0;
            foreach (var (tile, splat) in jobs)
            {
                var texels = await splat;
                if (resort.Tiles.TryGetValue((tile.Column, tile.Row), out var terrain))
                {
                    TerrainTiles.ApplySplat(terrain.terrainData, texels);
                    TerrainTiles.BindSplat(terrain);
                }
                if (++n % 8 == 0) await Task.Yield();
            }
        }

        static double Distance(CacheTile t, CacheManifest cache, LocalFrame frame)
        {
            var (x, z) = frame.ToLocal(new AlbersPoint(t.West + cache.TileMetres / 2, t.North - cache.TileMetres / 2));
            return x * x + z * z;
        }
    }
}
