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
        const int LiveRound = 16;

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
                    var b = _grid.Bounds(new TileKey(tc, tr));
                    double t0 = watch.Elapsed.TotalMilliseconds;
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                            hm[j1 - j, i - i0] = Normalised(b, ground, gc, gr, i, j);
                    double t1 = watch.Elapsed.TotalMilliseconds;
                    terrain.terrainData.SetHeightsDelayLOD(i0, 1024 - j1, hm);
                    sample += t1 - t0;
                    set += watch.Elapsed.TotalMilliseconds - t1;
                    _liveTouched.Add((tc, tr));
                }
            double sync0 = watch.Elapsed.TotalMilliseconds;
            foreach (var key in _liveTouched)
            {
                _tiles[key].terrainData.SyncHeightmap();
                TerrainTiles.BindSplat(_tiles[key]);   // the sync can hand the tile a new GPU heightmap (P3-05)
            }
            LastLiveMs = watch.Elapsed.TotalMilliseconds;
            LastLiveSampleMs = sample;
            LastLiveSetMs = set;
            LastLiveSyncMs = LastLiveMs - sync0;
        }

        // Normalised's sum with the change read from any ground: the same doubles, so the same quantised height.
        float Normalised(AlbersBox b, IPointDeltas ground, int gc, int gr, int i, int j)
        {
            double h = _source.At(b.West + i, b.North - j) + ground.DeltaMmAt(gc + i, gr + j) / 1000.0;
            return TerrainCache.Quantize(h, _cache.HeightMin, _cache.HeightRange) * (1f / TerrainCache.MaxValue);
        }

        static int RoundUp(int n) => (n + LiveRound - 1) / LiveRound * LiveRound;
    }
}
