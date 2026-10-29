using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.World;

namespace MountainPlanner.Presentation.Edits
{
    /// <summary>
    /// The live ground (task 15, owner BR5: "you should see it raise as you hold the mouse"; round 4, BR22: like Cities
    /// Skylines 2's brushes): while a terrain brush paints, the heights under this frame's dabs go to the tiles every frame,
    /// and the trees on them stand on the new ground and come out where the stroke clears; the cover, snow and labels catch
    /// up a few times a second (<see cref="ShowAsync"/>). <see cref="Current"/> and <see cref="Heights"/> stay the committed
    /// ones, so nothing a rule reads moves mid-stroke.
    /// </summary>
    public sealed partial class TerrainEditPipeline
    {
        // Reused height blocks, by size rounded up to 16 posts, so painting allocates nothing per frame once warm.
        readonly Dictionary<(int, int), float[,]> _liveBlocks = new Dictionary<(int, int), float[,]>();
        readonly HashSet<(int, int)> _liveTouched = new HashSet<(int, int)>();
        // Tiles the live upload wrote since their last SyncHeightmap (round 4): synced one a frame by the next apply.
        readonly HashSet<(int, int)> _liveUnsynced = new HashSet<(int, int)>();
        const int LiveRound = 16;

        /// <summary>Tiles the live upload wrote whose patch LOD and bounds Unity hasn't rebuilt yet (the bench).</summary>
        public int LiveUnsynced => _liveUnsynced.Count;

        /// <summary>
        /// Syncs one tile the live upload wrote (<see cref="ApplyLive"/>), if any: Unity rebuilds its patch LOD and bounds
        /// over the whole tile (3–7 ms), which is why the tool asks for one a frame, between presses and once per catch-up
        /// while painting, and never inside an apply (an apply waiting on it held up the next one, and Confirm with it).
        /// True if a tile was synced. Main thread.
        /// </summary>
        public bool SyncLiveTileNow()
        {
            // The crown map (the forest floor's shade between the trees) follows the trees the strokes took out, once a press is
            // let go: a whole-map pass, so not while painting (BR22).
            if (_crownDue && !Painting && _forest != null)
            {
                _crownDue = false;
                _forest.RedrawCrownCore();
                return true;
            }
            foreach (var key in _liveUnsynced) return SyncLiveTile(key);
            return false;
        }

        bool _crownDue;

        bool SyncLiveTile((int, int) key)
        {
            if (!_liveUnsynced.Remove(key)) return false;
            if (!_tiles.TryGetValue(key, out var terrain)) return false;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            terrain.terrainData.SyncHeightmap();
            TerrainTiles.BindSplat(terrain);
            if (Verbose) UnityEngine.Debug.Log($"[TerrainEdits] frame {UnityEngine.Time.frameCount}: live tile {key} synced in {watch.Elapsed.TotalMilliseconds:F1} ms ({_liveUnsynced.Count} to go)");
            return true;
        }

        /// <summary>Time the last live upload took, ms (the brush bench).</summary>
        public double LastLiveMs { get; private set; }

        /// <summary>The last live upload's parts, ms (the brush bench, demo 75): reading heights, SetHeights, then sync and rebind.</summary>
        public double LastLiveSampleMs { get; private set; }
        public double LastLiveSetMs { get; private set; }
        public double LastLiveSyncMs { get; private set; }
        /// <summary>BR22: the trees under the last live upload stood again, ms, and how many moved or came out.</summary>
        public double LastLiveTreesMs { get; private set; }
        public int LastLiveTrees { get; private set; }

        /// <summary>
        /// Puts <paramref name="ground"/>'s heights into the tiles over <paramref name="dirty"/> (lattice points) and nowhere
        /// else; heights only, each the source plus the point's change, as <see cref="Normalised"/> makes them. Reading the
        /// stroke in hand point by point (<see cref="BrushRun.Live"/>) builds no edit tiles. Main thread.
        /// </summary>
        public void ApplyLive(IPointDeltas ground, PointRect dirty)
        {
            if (ground == null || dirty.IsEmpty) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            double sample = 0, set = 0;
            _liveTouched.Clear();
            int tc0 = FloorDiv(dirty.MinColumn - 1, 1024), tc1 = FloorDiv(dirty.MaxColumn + 1, 1024);
            int tr0 = FloorDiv(dirty.MinRow - 1, 1024), tr1 = FloorDiv(dirty.MaxRow + 1, 1024);
            for (int tr = tr0; tr <= tr1; tr++)
                for (int tc = tc0; tc <= tc1; tc++)
                {
                    if (!_cacheTiles.TryGetValue((tc, tr), out var tile) || !tile.Core || tile.Resolution != TerrainTiles.Resolution) continue;
                    if (!_tiles.TryGetValue((tc, tr), out var terrain)) continue;
                    int gc = tc * 1024, gr = tr * 1024;
                    int lo0 = RingEdge(tile, -1, 0) ? 1 : 0, hiI = RingEdge(tile, 1, 0) ? 1023 : 1024;
                    int lo1 = RingEdge(tile, 0, -1) ? 1 : 0, hiJ = RingEdge(tile, 0, 1) ? 1023 : 1024;
                    int i0 = Math.Max(dirty.MinColumn - gc - 1, lo0), i1 = Math.Min(dirty.MaxColumn - gc + 1, hiI);
                    int j0 = Math.Max(dirty.MinRow - gr - 1, lo1), j1 = Math.Min(dirty.MaxRow - gr + 1, hiJ);
                    if (i1 < i0 || j1 < j0) continue;
                    // Round the block up to a pooled size, inside the tile: the extra posts get their own (unchanged) heights.
                    int w = Math.Min(RoundUp(i1 - i0 + 1), hiI - lo0 + 1), h = Math.Min(RoundUp(j1 - j0 + 1), hiJ - lo1 + 1);
                    if (i0 + w - 1 > hiI) i0 = hiI - w + 1;
                    if (j0 + h - 1 > hiJ) j0 = hiJ - h + 1;
                    i1 = i0 + w - 1;
                    j1 = j0 + h - 1;
                    if (!_liveBlocks.TryGetValue((w, h), out var hm)) _liveBlocks[(w, h)] = hm = new float[h, w];
                    double t0 = watch.Elapsed.TotalMilliseconds;
                    // By 64-post block of memoised source heights (round 4): a row's posts in one block read it once.
                    for (int j = j0; j <= j1; j++)
                    {
                        int bj = j >> 6, jr = (j & 63) * SourceBlock, row = gr + j;
                        for (int bi = i0 >> 6; bi <= i1 >> 6; bi++)
                        {
                            var source = SourceRow(SourceBlockAt(tc, tr, bi, bj), tc, tr, bi, j);
                            int ia = Math.Max(i0, bi << 6), ib = Math.Min(i1, (bi << 6) + SourceBlock - 1);
                            for (int i = ia; i <= ib; i++)
                                hm[j1 - j, i - i0] = Normalised(source[jr + (i & 63)], ground, gc + i, row);
                        }
                    }
                    double t1 = watch.Elapsed.TotalMilliseconds;
                    terrain.terrainData.SetHeightsDelayLOD(i0, 1024 - j1, hm);
                    sample += t1 - t0;
                    set += watch.Elapsed.TotalMilliseconds - t1;
                    _liveTouched.Add((tc, tr));
                }
            double sync0 = watch.Elapsed.TotalMilliseconds;
            foreach (var key in _liveTouched)
            {
                // Round 4: SetHeightsDelayLOD alone puts the heights on the GPU heightmap the shader draws from (the PlayMode
                // probe, LiveHeightmapProbeTests), so the whole-tile SyncHeightmap (7 ms at 400 ft) waits for the next
                // catch-up or the let-go (SyncLiveTile), which brings Unity's patch LOD and bounds up to date. Every set
                // hands the tile a new heightmap texture, so the info layers' _Heightmap is rebound here, every frame.
                TerrainTiles.BindSplat(_tiles[key]);
                _liveUnsynced.Add(key);
            }
            double trees0 = watch.Elapsed.TotalMilliseconds;
            LastLiveSyncMs = trees0 - sync0;
            LastLiveTrees = LiveTrees(ground, dirty);
            LastLiveMs = watch.Elapsed.TotalMilliseconds;
            LastLiveTreesMs = LastLiveMs - trees0;
            LastLiveSampleMs = sample;
            LastLiveSetMs = set;
        }

        /// <summary>
        /// BR22 (round 4): the trees whose ground or clearing <paramref name="dirty"/> touched, stood on <paramref name="ground"/>
        /// and taken out where it clears (when it's an <see cref="ICellClearing"/>: a stroke in hand, or a set of edits),
        /// exactly as an apply stands them (<see cref="GroundY"/>, ForestField.IsCleared by the trunk's cell): only the 64 m
        /// squares the change reaches are looked at, each uploaded as its own run. Returns how many changed.
        /// </summary>
        int LiveTrees(IPointDeltas ground, PointRect dirty)
        {
            if (_forest == null || _trees == null || _ranges == null) return 0;
            var clearing = ground as ICellClearing;
            // A tree stands on the four posts round its trunk's cell, and the cell clears when a corner moves: the cells from
            // one west and one north of the changed points.
            int c0 = dirty.MinColumn - 1, c1 = dirty.MaxColumn, r0 = dirty.MinRow - 1, r1 = dirty.MaxRow;
            double tileMetres = _cache.TileMetres;
            int changed = 0;
            for (int tr = FloorDiv(r0, 1024); tr <= FloorDiv(r1, 1024); tr++)
                for (int tc = FloorDiv(c0, 1024); tc <= FloorDiv(c1, 1024); tc++)
                {
                    if (!_cacheTiles.TryGetValue((tc, tr), out var tile) || !tile.Core || tile.Resolution != TerrainTiles.Resolution) continue;
                    if (!_ranges.TryGetValue((tc, tr), out var range) || range.Count == 0) continue;
                    var squares = TreeSquares((tc, tr), tile);
                    var packed = squares.Packed;
                    var b = _grid.Bounds(new TileKey(tc, tr));
                    int gc = tc * 1024, gr = tr * 1024;
                    int lc0 = Math.Max(0, c0 - gc), lc1 = Math.Min(1023, c1 - gc), lr0 = Math.Max(0, r0 - gr), lr1 = Math.Min(1023, r1 - gr);
                    for (int sr = lr0 >> 6; sr <= lr1 >> 6; sr++)
                        for (int sc = lc0 >> 6; sc <= lc1 >> 6; sc++)
                        {
                            int sq = sr * 16 + sc, lo = int.MaxValue, hi = -1;
                            for (int k = squares.Start[sq]; k < squares.Start[sq + 1]; k++)
                            {
                                int i = squares.Order[k], at = range.Start + i;
                                ForestField.Decode(packed, i, out float fx, out float fz, out _, out _, out _, out _);
                                var t = _trees[at];
                                float y = GroundY(tile, b, ground, fx, fz);
                                uint flags = clearing == null ? t.Flags
                                    : ForestField.IsCleared(packed, i, b.West, b.North, tileMetres, clearing, _lattice) ? ForestInstance.Cleared : 0;
                                if (y == t.Position.y && flags == t.Flags) continue;
                                if (flags != t.Flags) _crownDue = true;
                                t.Position.y = y;
                                t.Flags = flags;
                                _trees[at] = t;
                                lo = Math.Min(lo, at);
                                hi = Math.Max(hi, at);
                                changed++;
                            }
                            if (hi >= 0) _forest.UpdateTrees(_trees, lo, hi - lo + 1);
                        }
                }
            return changed;
        }

        /// <summary>A core tile's trees by 64 m square (the square of the trunk's cell, as ForestField.IsCleared finds it): each square's tree indices, in order.</summary>
        sealed class Squares
        {
            public byte[] Packed;
            public int[] Order, Start;   // Start: 257 entries; square s's trees are Order[Start[s]..Start[s + 1])
        }

        readonly Dictionary<(int, int), Squares> _squares = new Dictionary<(int, int), Squares>();

        /// <summary>
        /// Every core tile's trees by square, built on a worker once the forest is planted, so the first brush over a tile
        /// doesn't build its index in a frame (round 4: 5 ms the first time).
        /// </summary>
        void PrepareTreeSquares()
        {
            var keys = new List<(int, int)>();
            foreach (var kv in _cacheTiles)
                if (kv.Value.Core && kv.Value.Resolution == TerrainTiles.Resolution && _ranges != null && _ranges.ContainsKey(kv.Key)) keys.Add(kv.Key);
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var key in keys) TreeSquares(key, _cacheTiles[key]);
            });
        }

        Squares TreeSquares((int, int) key, CacheTile tile)
        {
            lock (_squares)
                if (_squares.TryGetValue(key, out var built)) return built;
            Squares squares;
            var packed = PackedTrees(key, tile);
            int count = packed.Length / ForestField.BytesPerTree;
            var b = _grid.Bounds(new TileKey(key.Item1, key.Item2));
            double tileMetres = _cache.TileMetres;
            int gc = key.Item1 * 1024, gr = key.Item2 * 1024;
            var of = new int[count];
            var start = new int[257];
            for (int i = 0; i < count; i++)
            {
                ForestField.Decode(packed, i, out float fx, out float fz, out _, out _, out _, out _);
                _lattice.CellAt(b.West + fx * tileMetres, b.North - tileMetres + fz * tileMetres, out int c, out int r);
                int lc = Math.Min(1023, Math.Max(0, c - gc)), lr = Math.Min(1023, Math.Max(0, r - gr));
                of[i] = (lr >> 6) * 16 + (lc >> 6);
                start[of[i] + 1]++;
            }
            for (int q = 0; q < 256; q++) start[q + 1] += start[q];
            var order = new int[count];
            var fill = (int[])start.Clone();
            for (int i = 0; i < count; i++) order[fill[of[i]]++] = i;   // in index order within a square
            squares = new Squares { Packed = packed, Order = order, Start = start };
            lock (_squares)
            {
                if (_squares.TryGetValue(key, out var first)) return first;   // the worker and a brush both built it
                _squares[key] = squares;
            }
            return squares;
        }

        static int RoundUp(int n) => (n + LiveRound - 1) / LiveRound * LiveRound;
    }
}
