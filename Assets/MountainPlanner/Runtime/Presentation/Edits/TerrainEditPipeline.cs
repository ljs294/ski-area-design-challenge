using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Measure;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Persistence;
using MountainPlanner.World;
using UnityEngine;

namespace MountainPlanner.Presentation.Edits
{
    /// <summary>
    /// The terrain edit pipeline (task P3-05; GR-09, CL-05, CL-07, SH-02, CF-09): lays a design's committed
    /// <see cref="TerrainEdits"/> over the open mountain, rebuilding only what changed.
    /// - <b>Heights:</b> the package's float32 heights plus the changes, quantised as the cache quantises them, put into
    ///   the changed rectangles of the touched tiles. Contours, slope and exposure follow, since the terrain shader reads
    ///   the heightmap.
    /// - <b>Cover and splat:</b> recomputed over each changed rectangle (plus the slope stencil and the stand-shade
    ///   margin) from the edited heights and the clearing, and painted by rectangle. Cleared cells lose their forest floor,
    ///   so the snow fills them.
    /// - <b>Trees:</b> the package's forest, filtered by the clearing (never regrown) and stood on the edited ground; the
    ///   crown map is redrawn so the forest floor and the snow between trees follow.
    /// - <b>Snow and contour labels:</b> rebuilt off the main thread after each change; the snow uploads by rectangle.
    /// The cache on disk stays as the package built it, so every save of an area shares it (no cache version change).
    /// Moving between any two edit sets (an edit, undo, redo, a load) gives the same result as opening with the later one,
    /// so undo restores byte-identical heights and the same trees. Changes apply one at a time, in the order asked.
    /// </summary>
    public sealed class TerrainEditPipeline : IDisposable
    {
        /// <summary>Timings of one change, for the edit benchmark (CF-09).</summary>
        public struct Report
        {
            public double PlanMs, HeightsMs, SplatMs, TreesMs, TotalMs, SnowMs;
            /// <summary>Task 08's breakdown of the plan: worker time on posts, on cover and on trees (summed over threads).</summary>
            public double PostsWorkMs, CoverWorkMs, TreesPlanMs;
            public int CacheTiles, Patches, Posts, Texels, Trees, Cleared;
        }

        readonly string _folder;
        readonly CacheManifest _cache;
        readonly TileGrid _grid;
        readonly LocalFrame _frame;
        readonly Dictionary<(int, int), Terrain> _tiles;
        readonly Dictionary<(int, int), CacheTile> _cacheTiles = new Dictionary<(int, int), CacheTile>();
        readonly PackageHeights _source;
        readonly CoverField _cover;
        readonly EditLattice _lattice;
        Task _queue = Task.CompletedTask;

        public TerrainEdits Current { get; private set; } = TerrainEdits.Empty;
        /// <summary>The heights every rule and readout reads (SH-02): the package's plus <see cref="Current"/>.</summary>
        public EditedHeights Heights { get; private set; }
        public Report Last { get; private set; }
        /// <summary>Raised on the main thread when the contour labels were rebuilt for the edited ground.</summary>
        public event Action<ContourLabel[][]> LabelsChanged;

        // The forest, once it's planted.
        ForestRenderer _forest;
        ForestInstance[] _trees;
        Dictionary<(int, int), (int Start, int Count)> _ranges;
        // The snowpack's inputs (8 m cells over the ring), once it's computed.
        SurfaceStates _states;
        float[] _elevation, _canopy, _depth;
        double _convergence;
        Rect _ring;

        TerrainEditPipeline(string folder, PackageManifest package, CacheManifest cache, LocalFrame frame, Dictionary<(int, int), Terrain> tiles)
        {
            _folder = folder;
            _cache = cache;
            _frame = frame;
            _tiles = tiles;
            _grid = TileGrid.For(SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0));
            foreach (var t in cache.Tiles) _cacheTiles[(t.Column, t.Row)] = t;
            _lattice = new EditLattice(_grid.West, _grid.North);
            _source = new PackageHeights(folder, package);
            _cover = new CoverField(package, folder, _source);
            Heights = new EditedHeights(_source, _lattice, Current);
        }

        /// <summary>Reads what edits need (the cover layers take a second or two on a 5 km site), off the main thread.</summary>
        public static Task<TerrainEditPipeline> PrepareAsync(string folder, PackageManifest package, CacheManifest cache, LocalFrame frame,
                                                             Dictionary<(int, int), Terrain> tiles, CancellationToken ct = default) =>
            Task.Run(() => new TerrainEditPipeline(folder, package, cache, frame, tiles), ct);

        /// <summary>The edit lattice (the tile grid's north-west corner, 1 m).</summary>
        public EditLattice Lattice => _lattice;
        public TileGrid Grid => _grid;

        /// <summary>The forest's instances (one GPU buffer, each tile's trees together) once it's planted.</summary>
        public void AttachForest(ForestRenderer forest, ForestInstance[] trees, Dictionary<(int, int), (int Start, int Count)> ranges)
        {
            _forest = forest;
            _trees = trees;
            _ranges = ranges;
        }

        /// <summary>The snowpack's inputs once it's computed (ResortOpener's 8 m elevation and canopy cells).</summary>
        public void AttachSnow(SurfaceStates states, float[] elevation, float[] canopy, double convergence, Rect ring)
        {
            _states = states;
            _elevation = elevation;
            _canopy = canopy;
            _convergence = convergence;
            _ring = ring;
            _depth = (float[])states.Snow.Cells.Clone();
        }

        /// <summary>The edited height at a local position, or NaN outside the core tiles (the HUD's readout, SH-02).</summary>
        public float HeightAtLocal(float x, float z)
        {
            var p = _frame.ToAlbers(x, z);
            if (!_grid.TryTileAt(p, out var key) || !_grid.IsCore(key)) return float.NaN;
            return (float)Heights.At(p.X, p.Y);
        }

        /// <summary>
        /// Moves the mountain to <paramref name="to"/>: plans on worker threads, then applies on the main thread. Call
        /// from the main thread; changes queue behind any still applying.
        /// </summary>
        public Task ApplyAsync(TerrainEdits to, CancellationToken ct = default)
        {
            if (to == null) throw new ArgumentNullException(nameof(to));
            var next = _queue.ContinueWith(_ => ApplyOneAsync(to, ct), CancellationToken.None, TaskContinuationOptions.None,
                                           TaskScheduler.FromCurrentSynchronizationContext()).Unwrap();
            _queue = next;
            return next;
        }

        async Task ApplyOneAsync(TerrainEdits to, CancellationToken ct)
        {
            var from = Current;
            if (ReferenceEquals(from, to)) return;
            var total = Stopwatch.StartNew();
            var report = new Report();
            var heights = new EditedHeights(_source, _lattice, to);
            var trees = _trees;
            var ranges = _ranges;
            List<Patch> patches;
            (patches, report) = await Task.Run(() =>
            {
                var r = new Report();
                var list = Plan(from, to, heights, trees, ranges, ref r);
                return (list, r);
            }, ct);
            report.PlanMs = total.Elapsed.TotalMilliseconds;

            // Main thread: heights, then splat, then trees, each by rectangle.
            var part = Stopwatch.StartNew();
            var synced = new HashSet<(int, int)>();
            foreach (var p in patches)
                if (p.Heights != null && _tiles.TryGetValue(p.Tile, out var terrain))
                {
                    terrain.terrainData.SetHeightsDelayLOD(p.HeightX, p.HeightY, p.Heights);
                    synced.Add(p.Tile);
                }
            foreach (var key in synced)
            {
                _tiles[key].terrainData.SyncHeightmap();
                // The sync can hand the tile a new GPU heightmap: rebind it, or the slope and exposure layers read a stale one.
                TerrainTiles.BindSplat(_tiles[key]);
            }
            report.HeightsMs = part.Elapsed.TotalMilliseconds;
            part.Restart();
            foreach (var p in patches)
                if (p.Splat != null && _tiles.TryGetValue(p.Tile, out var terrain) && terrain.terrainData.alphamapResolution == p.Splat.Resolution)
                    TerrainTiles.ApplySplatRegion(terrain.terrainData, p.Splat, p.SplatX, p.SplatY);
            report.SplatMs = part.Elapsed.TotalMilliseconds;
            part.Restart();
            bool treesChanged = false;
            if (_forest != null && trees != null)
            {
                foreach (var p in patches)
                {
                    if (p.TreeIndex == null || p.TreeIndex.Count == 0) continue;
                    int lo = int.MaxValue, hi = -1;
                    for (int k = 0; k < p.TreeIndex.Count; k++)
                    {
                        int i = p.TreeIndex[k];
                        trees[i] = p.TreeValue[k];
                        lo = Math.Min(lo, i);
                        hi = Math.Max(hi, i);
                    }
                    _forest.UpdateTrees(trees, lo, hi - lo + 1);
                    treesChanged = true;
                }
                if (treesChanged) _forest.RedrawCrownCore();
            }
            report.TreesMs = part.Elapsed.TotalMilliseconds;
            Current = to;
            Heights = heights;
            report.TotalMs = total.Elapsed.TotalMilliseconds;
            Last = report;
            UnityEngine.Debug.Log($"[TerrainEdits] {report.Patches} patches over {report.CacheTiles} tiles: {report.Posts:N0} posts, {report.Texels:N0} texels, " +
                                  $"{report.Trees:N0} trees ({report.Cleared:N0} cleared); plan {report.PlanMs:F1} ms, heights {report.HeightsMs:F1}, " +
                                  $"splat {report.SplatMs:F1}, trees {report.TreesMs:F1}, total {report.TotalMs:F1} ms (worker: posts {report.PostsWorkMs:F0}, cover {report.CoverWorkMs:F0}, tree plan {report.TreesPlanMs:F0})");
            if (_states != null) RebuildSnow(patches, ct);
        }

        /// <summary>One rectangle of one cache tile to rebuild.</summary>
        sealed class Patch
        {
            public (int, int) Tile;
            public float[,] Heights;
            public int HeightX, HeightY;
            public SplatTexels Splat;
            public int SplatX, SplatY;
            /// <summary>The changed area in package metres (west, south, east, north).</summary>
            public double West, South, East, North;
            public List<int> TreeIndex;
            public List<ForestInstance> TreeValue;
        }

        List<Patch> Plan(TerrainEdits from, TerrainEdits to, EditedHeights heights, ForestInstance[] trees,
                         Dictionary<(int, int), (int Start, int Count)> ranges, ref Report report)
        {
            var changed = TerrainEdits.Changed(from, to);
            // Each changed edit tile's changed points and cells, as a box of lattice points.
            var areas = new List<(int C0, int R0, int C1, int R1)>();
            foreach (var key in changed)
            {
                var a = from.Get(key);
                var b = to.Get(key);
                int c0 = int.MaxValue, r0 = int.MaxValue, c1 = int.MinValue, r1 = int.MinValue;
                for (int j = 0; j < EditTileKey.Side; j++)
                    for (int i = 0; i < EditTileKey.Side; i++)
                        if (a.DeltaMm(i, j) != b.DeltaMm(i, j) || a.IsCleared(i, j) != b.IsCleared(i, j))
                        {
                            c0 = Math.Min(c0, i); c1 = Math.Max(c1, i);
                            r0 = Math.Min(r0, j); r1 = Math.Max(r1, j);
                        }
                if (c1 < 0) continue;
                // A cell reaches one point further east and south than the point that names it.
                areas.Add((key.FirstColumn + c0, key.FirstRow + r0, key.FirstColumn + c1 + 1, key.FirstRow + r1 + 1));
            }

            // Every core cache tile each area's posts fall in (an edge post belongs to two, a corner to four).
            var jobs = new List<(CacheTile Tile, (int C0, int R0, int C1, int R1) Area)>();
            foreach (var area in areas)
            {
                int tc0 = FloorDiv(area.C0 - 1, 1024), tc1 = FloorDiv(area.C1 + 1, 1024), tr0 = FloorDiv(area.R0 - 1, 1024), tr1 = FloorDiv(area.R1 + 1, 1024);
                for (int tr = tr0; tr <= tr1; tr++)
                    for (int tc = tc0; tc <= tc1; tc++)
                        if (_cacheTiles.TryGetValue((tc, tr), out var tile) && tile.Core && tile.Resolution == TerrainTiles.Resolution) jobs.Add((tile, area));
            }
            // The patches are independent, so they plan in parallel; each lands in its own slot, so the order is fixed.
            var planned = new Patch[jobs.Count];
            var counts = new Report[jobs.Count];
            Parallel.For(0, jobs.Count, WorkerLimits.Options(CancellationToken.None), k =>
            {
                var r = new Report();
                planned[k] = PlanPatch(jobs[k].Tile, jobs[k].Area, heights, ref r);
                counts[k] = r;
            });
            var patches = new List<Patch>();
            var tilesTouched = new HashSet<(int, int)>();
            for (int k = 0; k < planned.Length; k++)
            {
                if (planned[k] == null) continue;
                patches.Add(planned[k]);
                tilesTouched.Add(planned[k].Tile);
                report.Posts += counts[k].Posts;
                report.Texels += counts[k].Texels;
                report.PostsWorkMs += counts[k].PostsWorkMs;
                report.CoverWorkMs += counts[k].CoverWorkMs;
            }
            report.Patches = patches.Count;
            report.CacheTiles = tilesTouched.Count;

            // Trees: each touched tile's packed trees, read once; the ones in a patch stand on the new ground, cleared or not.
            var treeWatch = Stopwatch.StartNew();
            if (trees != null && ranges != null)
                foreach (var key in tilesTouched)
                {
                    if (!ranges.TryGetValue(key, out var range) || range.Count == 0) continue;
                    var tile = _cacheTiles[key];
                    byte[] packed = TerrainCache.ReadTrees(_folder, tile);
                    int count = packed.Length / ForestField.BytesPerTree;
                    if (count != range.Count) throw new InvalidOperationException($"Tile {key} has {count} trees in the cache and {range.Count} on screen.");
                    var b = _grid.Bounds(new TileKey(key.Item1, key.Item2));
                    double tileMetres = _cache.TileMetres;
                    foreach (var p in patches)
                    {
                        if (p.Tile != key) continue;
                        p.TreeIndex = new List<int>();
                        p.TreeValue = new List<ForestInstance>();
                        for (int i = 0; i < count; i++)
                        {
                            ForestField.Decode(packed, i, out float fx, out float fz, out _, out _, out _, out _);
                            double x = b.West + fx * tileMetres, y = b.North - tileMetres + fz * tileMetres;
                            if (x < p.West - 1 || x > p.East + 1 || y < p.South - 1 || y > p.North + 1) continue;
                            var t = trees[range.Start + i];
                            t.Flags = ForestField.IsCleared(packed, i, b.West, b.North, tileMetres, heights.Edits, _lattice) ? ForestInstance.Cleared : 0;
                            t.Position.y = GroundY(tile, b, heights, fx, fz);
                            p.TreeIndex.Add(range.Start + i);
                            p.TreeValue.Add(t);
                            report.Trees++;
                            if (t.Flags != 0) report.Cleared++;
                        }
                    }
                }
            report.TreesPlanMs = treeWatch.Elapsed.TotalMilliseconds;
            return patches;
        }

        /// <summary>The heights and splat of one area within one tile, or null if it misses the tile.</summary>
        Patch PlanPatch(CacheTile tile, (int C0, int R0, int C1, int R1) area, EditedHeights heights, ref Report report)
        {
            var b = _grid.Bounds(new TileKey(tile.Column, tile.Row));
            int gc = tile.Column * 1024, gr = tile.Row * 1024;
            // Posts: the area's, plus one round (the bilinear between posts), within this tile and off any edge it shares
            // with a 2 m ring tile (TerrainCache.MatchRingEdges keeps those; edits stay inside the core, SH-06).
            int i0 = Math.Max(area.C0 - gc - 1, RingEdge(tile, -1, 0) ? 1 : 0), i1 = Math.Min(area.C1 - gc + 1, RingEdge(tile, 1, 0) ? 1023 : 1024);
            int j0 = Math.Max(area.R0 - gr - 1, RingEdge(tile, 0, -1) ? 1 : 0), j1 = Math.Min(area.R1 - gr + 1, RingEdge(tile, 0, 1) ? 1023 : 1024);
            if (i1 < i0 || j1 < j0) return null;
            var patch = new Patch
            {
                Tile = (tile.Column, tile.Row),
                West = b.West + (area.C0 - gc), East = b.West + (area.C1 - gc), North = b.North - (area.R0 - gr), South = b.North - (area.R1 - gr),
            };
            int w = i1 - i0 + 1, h = j1 - j0 + 1;
            var watch = Stopwatch.StartNew();
            var hm = new float[h, w];   // [z, x], south row first
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    hm[j1 - j, i - i0] = Normalised(b, heights, gc, gr, i, j);
            patch.Heights = hm;
            patch.HeightX = i0;
            patch.HeightY = 1024 - j1;
            report.Posts += w * h;
            report.PostsWorkMs += watch.Elapsed.TotalMilliseconds;
            watch.Restart();

            // Cover texels within 2 m of the area (the slope stencil reads 1 m either side of a bilinear sample).
            int n = tile.CoverResolution;
            double step = (b.East - b.West) / (n - 1);
            int ti0 = Math.Max(0, (int)Math.Floor((patch.West - 2 - b.West) / step)), ti1 = Math.Min(n, (int)Math.Ceiling((patch.East + 2 - b.West) / step) + 1);
            int tj0 = Math.Max(0, (int)Math.Floor((b.North - patch.North - 2) / step)), tj1 = Math.Min(n, (int)Math.Ceiling((b.North - patch.South + 2) / step) + 1);
            if (ti1 > ti0 && tj1 > tj0)
            {
                int reach = SplatTexels.ShadeReach(n);
                int wi0 = Math.Max(0, ti0 - reach), wi1 = Math.Min(n, ti1 + reach), wj0 = Math.Max(0, tj0 - reach), wj1 = Math.Min(n, tj1 + reach);
                int ww = wi1 - wi0, wh = wj1 - wj0;
                var cover = new byte[ww * wh * TerrainCache.CoverBands];
                _cover.BuildRegion(b, n, wi0, wj0, wi1, wj1, heights, heights.IsCleared, cover);
                var inner = new RectInt(ti0, tj0, ti1 - ti0, tj1 - tj0);
                patch.Splat = SplatTexels.ComposeWindow(cover, n, wi0, wj0, ww, wh, ww, inner);
                patch.SplatX = ti0;
                patch.SplatY = n - tj1;
                report.Texels += inner.width * inner.height;
            }
            report.CoverWorkMs += watch.Elapsed.TotalMilliseconds;
            return patch;
        }

        /// <summary>A post's height as the tile holds it: the cache's quantising of the edited height (LoadHeights' scale).</summary>
        float Normalised(AlbersBox b, EditedHeights heights, int gc, int gr, int i, int j)
        {
            double h = heights.AtPost(b.West + i, b.North - j, gc + i, gr + j);
            return TerrainCache.Quantize(h, _cache.HeightMin, _cache.HeightRange) * (1f / TerrainCache.MaxValue);
        }

        /// <summary>Where a tree stands on the edited ground: <see cref="ForestInstance.Decode"/>'s sampling of the tile's posts.</summary>
        float GroundY(CacheTile tile, AlbersBox b, EditedHeights heights, float fx, float fz)
        {
            int res = TerrainTiles.Resolution, gc = tile.Column * 1024, gr = tile.Row * 1024;
            float gx = fx * (res - 1), gz = fz * (res - 1);
            int x0 = Mathf.Min((int)gx, res - 2), z0 = Mathf.Min((int)gz, res - 2);
            float tx = gx - x0, tz = gz - z0;
            // Unity row z is cache row 1024 − z.
            float h00 = Normalised(b, heights, gc, gr, x0, 1024 - z0), h10 = Normalised(b, heights, gc, gr, x0 + 1, 1024 - z0);
            float h01 = Normalised(b, heights, gc, gr, x0, 1024 - z0 - 1), h11 = Normalised(b, heights, gc, gr, x0 + 1, 1024 - z0 - 1);
            float h = ForestInstance.Ground(h00, h10, h01, h11, tx, tz);
            return (float)_cache.HeightMin + h * (float)_cache.HeightRange;
        }

        bool RingEdge(CacheTile tile, int dc, int dr) =>
            _cacheTiles.TryGetValue((tile.Column + dc, tile.Row + dr), out var other) && !other.Core;

        int _snowGeneration;

        /// <summary>
        /// The snowpack and contour labels over the edited ground: the changed cells' elevation and canopy (as the opener
        /// samples them), then the whole snowpack again off the main thread (its elevation bands are site-wide); only the
        /// cells whose depth changed upload. A newer change supersedes an older one still computing (CF-03), so the last
        /// change's snow is the one that lands, whatever the timing.
        /// </summary>
        async void RebuildSnow(List<Patch> patches, CancellationToken ct)
        {
            int generation = ++_snowGeneration;
            var watch = Stopwatch.StartNew();
            const float cell = SurfaceStates.CellMetres;
            int width = _states.Snow.Width, depth = _states.Snow.Height;
            foreach (var p in patches)
            {
                var (lw, ls) = _frame.ToLocal(new AlbersPoint(p.West, p.South));
                var (le, ln) = _frame.ToLocal(new AlbersPoint(p.East, p.North));
                int ci0 = Math.Max(0, (int)Math.Floor((lw - 1 - _ring.xMin) / cell)), ci1 = Math.Min(width - 1, (int)Math.Ceiling((le + 1 - _ring.xMin) / cell));
                int cj0 = Math.Max(0, (int)Math.Floor((ls - 1 - _ring.yMin) / cell)), cj1 = Math.Min(depth - 1, (int)Math.Ceiling((ln + 1 - _ring.yMin) / cell));
                var tile = _cacheTiles[p.Tile];
                var (ox, oz) = _frame.ToLocal(new AlbersPoint(tile.West, tile.North - _cache.TileMetres));
                var b = _grid.Bounds(new TileKey(tile.Column, tile.Row));
                int res = TerrainTiles.Resolution, gc = tile.Column * 1024, gr = tile.Row * 1024;
                float tileMetres = (float)_cache.TileMetres;
                for (int j = cj0; j <= cj1; j++)
                    for (int i = ci0; i <= ci1; i++)
                    {
                        // As ResortOpener.BuildSnowpack: the nearest post and texel to the cell's centre, in the tile under it.
                        float u = (_ring.xMin + (i + 0.5f) * cell - (float)ox) / tileMetres, v = (_ring.yMin + (j + 0.5f) * cell - (float)oz) / tileMetres;
                        if (u < 0 || u > 1 || v < 0 || v > 1) continue;
                        int col = Mathf.Clamp(Mathf.RoundToInt(u * (res - 1)), 0, res - 1), row = Mathf.Clamp(Mathf.RoundToInt(v * (res - 1)), 0, res - 1);
                        float hn = Normalised(b, Heights, gc, gr, col, 1024 - row);
                        _elevation[j * width + i] = (float)(_cache.HeightMin + hn * _cache.HeightRange);
                        if (p.Splat != null)
                        {
                            int c = p.Splat.Resolution;
                            int ti = Mathf.Clamp(Mathf.RoundToInt(u * (c - 1)), 0, c - 1), tj = Mathf.Clamp(Mathf.RoundToInt(v * (c - 1)), 0, c - 1);
                            int px = ti - p.SplatX, py = tj - p.SplatY;
                            if (px >= 0 && py >= 0 && px < p.Splat.Width && py < p.Splat.Height)
                                _canopy[j * width + i] = p.Splat.Textures[0][(py * p.Splat.Width + px) * 4 + 1] / 255f;
                        }
                    }
            }
            float[] elevation = (float[])_elevation.Clone(), canopy = (float[])_canopy.Clone();
            double convergence = _convergence;
            var ring = _ring;
            float[] result;
            ContourLabel[][] labels;
            try
            {
                (result, labels) = await Task.Run(() =>
            {
                var r = new float[width * depth];
                Snowpack.Compute(width, depth, cell, elevation, canopy, convergence, r);
                var l = new ContourLabel[2][];
                foreach (var units in new[] { UnitSystem.Imperial, UnitSystem.Metric })
                    l[(int)units] = ContourLabels.Place(width, depth, cell, ring.xMin, ring.yMin, elevation, units);
                return (r, l);
            }, ct);
            }
            catch (OperationCanceledException) { return; }
            if (_states == null || generation != _snowGeneration) return;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                    if (result[j * width + i] != _depth[j * width + i])
                    {
                        x0 = Math.Min(x0, i); x1 = Math.Max(x1, i);
                        y0 = Math.Min(y0, j); y1 = Math.Max(y1, j);
                    }
            if (x1 >= 0) _states.Snow.CopyRect(result, x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            _depth = result;
            LabelsChanged?.Invoke(labels);
            var report = Last;
            report.SnowMs = watch.Elapsed.TotalMilliseconds;
            Last = report;
        }

        static int FloorDiv(int a, int b) => a >= 0 ? a / b : -((-a + b - 1) / b);

        public void Dispose() => _source.Dispose();
    }
}
