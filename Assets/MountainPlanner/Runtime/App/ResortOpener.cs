using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Domain.Snow;
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
        /// <summary>Holds the cache open while the area is on screen (task P2-04): no game deletes it meanwhile. Dispose when closing.</summary>
        public IDisposable CacheLease;
        /// <summary>The ring (local x/z): where the downloaded data ends and the diorama walls stand.</summary>
        public Rect Ring;
        /// <summary>The top of the diorama's plinth (local y): the lowest the camera may go off the terrain.</summary>
        public float PlinthTop;
        /// <summary>Task 10's seams: the snow-depth field and the water's surface state, and their upload to the GPU.</summary>
        public SurfaceStates States;
        /// <summary>Completes when every tile's ground cover is painted (it streams in after the terrain is playable).</summary>
        public Task CoverReady = Task.CompletedTask;
        public double CoverSeconds;
        /// <summary>When the natural snowpack (task 12b) was in the snow-depth field, seconds after opening began.</summary>
        public double SnowpackSeconds;
        /// <summary>Contour labels (task 12b.2), by <see cref="UnitSystem"/>: every 200 ft, or every 50 m. Empty until the snowpack pass ends.</summary>
        public ContourLabel[][] ContourLabels = { Array.Empty<ContourLabel>(), Array.Empty<ContourLabel>() };
        public long TreesPlanted;
        public long CliffTriangles;
        public Material CliffMaterial;
        /// <summary>The diorama walls (their snow cap follows the Snow layer).</summary>
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

        /// <param name="readOnly">
        /// A package built into the game (task P2-03): opened without writing a byte. It takes no cache lease (nothing
        /// can delete it) and its cache must already be current, since it can't be built in place.
        /// </param>
        public static async Task<OpenedResort> OpenAsync(string packageFolder, Transform parent, TerrainDetail detail,
                                                         IProgress<OpenProgress> progress, CancellationToken ct = default, Material material = null,
                                                         ForestAssets forest = null, bool readOnly = false)
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new OpenProgress("Opening: reading the package", 0));
            // Held from the start, so no game (another branch's included) deletes this cache while it opens or shows.
            var lease = readOnly ? null : await Task.Run(() => TerrainCache.Hold(packageFolder), ct);
            try { return await OpenHeldAsync(packageFolder, parent, detail, progress, ct, material, forest, clock, lease); }
            catch
            {
                lease?.Dispose();
                throw;
            }
        }

        static async Task<OpenedResort> OpenHeldAsync(string packageFolder, Transform parent, TerrainDetail detail, IProgress<OpenProgress> progress,
                                                      CancellationToken ct, Material material, ForestAssets forest, Stopwatch clock, IDisposable lease)
        {
            var manifest = await Task.Run(() => ResortPackage.ReadManifest(packageFolder), ct);

            bool current = await Task.Run(() => TerrainCache.IsCurrent(packageFolder, manifest), ct);
            if (!current && lease == null) throw new System.IO.InvalidDataException(BundledAreas.StaleCache);
            if (!current)
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
                ground.SetElevationRange((float)cache.HeightMin, (float)(cache.HeightMin + cache.HeightRange));
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
            var covers = order.Select(t => Task.Run(() => SplatTexels.Load(packageFolder, t), ct)).ToList();
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
                Ground = ground, PackageFolder = packageFolder, CacheLease = lease,
                Ring = ringRect, PlinthTop = (float)cache.HeightMin - DioramaBase.BaseDepth, States = new SurfaceStates(ringRect),
            };
            if (forest?.Edge != null)
            {
                // Sampled a few centimetres inside the cut, where the terrain has data on every side.
                float Edge(float x, float z) => surface.HeightAt(Mathf.Clamp(x, ringRect.xMin + 0.05f, ringRect.xMax - 0.05f),
                                                                 Mathf.Clamp(z, ringRect.yMin + 0.05f, ringRect.yMax - 0.05f));
                resort.EdgeMaterial = DioramaBase.Create(root.transform, ringRect, Edge, (float)cache.HeightMin - DioramaBase.BaseDepth, forest.Edge).Walls;
            }
            // The natural snowpack (task 12b) from the heights and cover already decoded, on a worker thread.
            var states = resort.States;
            double convergence = manifest.Crs.GridConvergenceDegrees;
            var snowpack = Task.Run(async () => BuildSnowpack(order, await Task.WhenAll(decoded).ConfigureAwait(false),
                                                                await Task.WhenAll(covers).ConfigureAwait(false), cache, frame, ringRect,
                                                                states.Snow.Width, states.Snow.Height, convergence), ct);
            resort.CoverReady = PaintCoverAsync(resort, order, covers, cliffTasks, forests, forest, clock, ct, snowpack);
            return resort;
        }

        /// <summary>Paints each tile's ground cover, nearest first, a few tiles per frame.</summary>
        static async Task PaintCoverAsync(OpenedResort resort, List<CacheTile> order, List<Task<SplatTexels>> covers, List<Task<CliffShells.Prepared>> cliffTasks,
                                          List<Task<ForestInstance[]>> forests, ForestAssets forest,
                                          Stopwatch clock, CancellationToken ct, Task<(float[] Depths, ContourLabel[][] Labels)> snowpack)
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

            // The snowpack replaces the opening 12 in; SurfaceStates.Sync uploads it once.
            var (depths, labels) = await snowpack;
            if (resort.Root == null) return;
            resort.States.Snow.CopyFrom(depths);
            resort.ContourLabels = labels;
            resort.SnowpackSeconds = clock.Elapsed.TotalSeconds;
            UnityEngine.Debug.Log($"[ResortOpener] snowpack in the snow-depth field at {resort.SnowpackSeconds:F2} s");

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
        /// The natural snowpack over the snow-depth field's cells (8 m over the ring): each cell's elevation and canopy
        /// share (the forest-floor weight of the bare ground cover) from the tile under its centre, then
        /// <see cref="Snowpack.Compute"/>. Cells no tile covers have no data and get the valley depth. The same
        /// elevations place the contour labels for both unit systems (task 12b.2).
        /// </summary>
        static (float[] Depths, ContourLabel[][] Labels) BuildSnowpack(List<CacheTile> order, float[][,] heights, SplatTexels[] covers, CacheManifest cache, LocalFrame frame,
                                     Rect ring, int width, int depth, double convergence)
        {
            const float cell = SurfaceStates.CellMetres;
            var elevation = new float[width * depth];
            var canopy = new float[width * depth];
            for (int k = 0; k < elevation.Length; k++) elevation[k] = float.NaN;
            float tileMetres = (float)cache.TileMetres;
            for (int n = 0; n < order.Count; n++)
            {
                var tile = order[n];
                var (ox, oz) = frame.ToLocal(new AlbersPoint(tile.West, tile.North - cache.TileMetres));
                float[,] h = heights[n];
                int res = h.GetLength(0);
                var cover = covers[n];
                int i0 = Mathf.Max(0, Mathf.CeilToInt(((float)ox - ring.xMin) / cell - 0.5f)), i1 = Mathf.Min(width, Mathf.CeilToInt(((float)ox + tileMetres - ring.xMin) / cell - 0.5f));
                int j0 = Mathf.Max(0, Mathf.CeilToInt(((float)oz - ring.yMin) / cell - 0.5f)), j1 = Mathf.Min(depth, Mathf.CeilToInt(((float)oz + tileMetres - ring.yMin) / cell - 0.5f));
                for (int j = j0; j < j1; j++)
                    for (int i = i0; i < i1; i++)
                    {
                        // Fractions across the tile: east, and north (Unity's rows and the splat's run south to north).
                        float u = (ring.xMin + (i + 0.5f) * cell - (float)ox) / tileMetres, v = (ring.yMin + (j + 0.5f) * cell - (float)oz) / tileMetres;
                        int col = Mathf.Clamp(Mathf.RoundToInt(u * (res - 1)), 0, res - 1), row = Mathf.Clamp(Mathf.RoundToInt(v * (res - 1)), 0, res - 1);
                        elevation[j * width + i] = (float)(cache.HeightMin + h[row, col] * cache.HeightRange);
                        int c = cover.Resolution;
                        int ci = Mathf.Clamp(Mathf.RoundToInt(u * (c - 1)), 0, c - 1), cj = Mathf.Clamp(Mathf.RoundToInt(v * (c - 1)), 0, c - 1);
                        canopy[j * width + i] = cover.Textures[0][(cj * c + ci) * 4 + 1] / 255f;
                    }
            }
            var result = new float[width * depth];
            Snowpack.Compute(width, depth, cell, elevation, canopy, convergence, result);
            // A one-line summary for the log: the range, the mean and the share too thin to cover the ground.
            double sum = 0, max = 0;
            int thin = 0;
            foreach (float d in result)
            {
                sum += d;
                if (d > max) max = d;
                if (d < SurfaceStates.FullCoverMetres) thin++;
            }
            UnityEngine.Debug.Log($"[ResortOpener] snowpack: mean {sum / result.Length:F2} m, deepest {max:F2} m, {100.0 * thin / result.Length:F1}% thinner than {SurfaceStates.FullCoverMetres * 100:F0} cm");
            var labels = new ContourLabel[2][];
            foreach (var units in new[] { UnitSystem.Imperial, UnitSystem.Metric })
                labels[(int)units] = ContourLabels.Place(width, depth, cell, ring.xMin, ring.yMin, elevation, units);
            UnityEngine.Debug.Log($"[ResortOpener] contour labels: {labels[0].Length:N0} (200 ft), {labels[1].Length:N0} (50 m)");
            return (result, labels);
        }

        static double Distance(CacheTile t, CacheManifest cache, LocalFrame frame)
        {
            var (x, z) = frame.ToLocal(new AlbersPoint(t.West + cache.TileMetres / 2, t.North - cache.TileMetres / 2));
            return x * x + z * z;
        }
    }
}
