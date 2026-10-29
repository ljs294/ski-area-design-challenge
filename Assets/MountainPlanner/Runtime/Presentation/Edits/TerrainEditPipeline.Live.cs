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
    /// The live ground (task 15, owner BR5: "you should see it raise as you hold the mouse"): while a terrain brush paints,
    /// only the heights under this frame's dabs go to the tiles, every frame; the trees, cover, snow and labels catch up when
    /// the stroke ends (<see cref="ApplyAsync"/> of the pending ground). <see cref="Current"/> and <see cref="Heights"/> stay
    /// the committed ones, so letting go and cancelling both land through the usual apply, and nothing a rule reads moves
    /// mid-stroke.
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
            foreach (var key in _liveUnsynced) return SyncLiveTile(key);
            return false;
        }

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
            LastLiveMs = watch.Elapsed.TotalMilliseconds;
            LastLiveSampleMs = sample;
            LastLiveSetMs = set;
            LastLiveSyncMs = LastLiveMs - sync0;
        }

        static int RoundUp(int n) => (n + LiveRound - 1) / LiveRound * LiveRound;
    }
}
