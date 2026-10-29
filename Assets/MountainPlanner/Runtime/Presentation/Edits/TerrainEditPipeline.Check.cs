using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.World;

namespace MountainPlanner.Presentation.Edits
{
    public sealed partial class TerrainEditPipeline
    {
        // Every edit tile any change has reached since the area opened: where the check looks.
        readonly HashSet<EditTileKey> _touched = new HashSet<EditTileKey>();

        /// <summary>
        /// The brush bench's check (owner BR21): once settled, the heights on the tiles and the forest's trees (their footing
        /// and which are out) are exactly what a fresh apply of <see cref="Current"/> would give, over every edit tile a change
        /// has reached, so moving what's shown ahead of what's committed and back never leaves anything behind. Main thread;
        /// slow (reads the tiles and the cache's trees): for benches and tests only. Returns how many values differ.
        /// </summary>
        public int CheckShown(out string first)
        {
            first = null;
            int bad = 0;
            var heights = Heights;
            var touchedTiles = new HashSet<(int, int)>();
            foreach (var key in _touched)
            {
                int c0 = key.FirstColumn, r0 = key.FirstRow, c1 = c0 + EditTileKey.Side, r1 = r0 + EditTileKey.Side;
                for (int tr = FloorDiv(r0, 1024); tr <= FloorDiv(r1, 1024); tr++)
                    for (int tc = FloorDiv(c0, 1024); tc <= FloorDiv(c1, 1024); tc++)
                    {
                        if (!_cacheTiles.TryGetValue((tc, tr), out var tile) || !tile.Core || tile.Resolution != TerrainTiles.Resolution) continue;
                        if (!_tiles.TryGetValue((tc, tr), out var terrain)) continue;
                        touchedTiles.Add((tc, tr));
                        int gc = tc * 1024, gr = tr * 1024;
                        int lo0 = RingEdge(tile, -1, 0) ? 1 : 0, hiI = RingEdge(tile, 1, 0) ? 1023 : 1024;
                        int lo1 = RingEdge(tile, 0, -1) ? 1 : 0, hiJ = RingEdge(tile, 0, 1) ? 1023 : 1024;
                        int i0 = Math.Max(c0 - gc, lo0), i1 = Math.Min(c1 - gc, hiI), j0 = Math.Max(r0 - gr, lo1), j1 = Math.Min(r1 - gr, hiJ);
                        if (i1 < i0 || j1 < j0) continue;
                        var b = _grid.Bounds(new TileKey(tc, tr));
                        var got = terrain.terrainData.GetHeights(i0, 1024 - j1, i1 - i0 + 1, j1 - j0 + 1);   // [z, x], south row first
                        for (int j = j0; j <= j1; j++)
                            for (int i = i0; i <= i1; i++)
                            {
                                float want = Normalised(heights.Edits, gc, gr, i, j), have = got[j1 - j, i - i0];
                                if (want == have) continue;
                                if (bad++ == 0) first = $"height at tile ({tc}, {tr}) post ({i}, {j}): {have} on the tile, {want} fresh";
                            }
                    }
            }
            if (_trees == null || _ranges == null) return bad;
            foreach (var key in touchedTiles)
            {
                if (!_ranges.TryGetValue(key, out var range) || range.Count == 0) continue;
                var tile = _cacheTiles[key];
                byte[] packed = TerrainCache.ReadTrees(_folder, tile);
                var b = _grid.Bounds(new TileKey(key.Item1, key.Item2));
                for (int i = 0; i < range.Count; i++)
                {
                    ForestField.Decode(packed, i, out float fx, out float fz, out _, out _, out _, out _);
                    var t = _trees[range.Start + i];
                    uint flags = ForestField.IsCleared(packed, i, b.West, b.North, _cache.TileMetres, Current, _lattice) ? ForestInstance.Cleared : 0u;
                    float y = GroundY(tile, b, heights.Edits, fx, fz);
                    if (t.Flags == flags && t.Position.y == y) continue;
                    // A tree on its tile's outermost posts that no edit ever moved keeps the footing it was planted with, which
                    // samples the tile's edge its own way (a few cm, from before task 15): not what this checks.
                    int res = TerrainTiles.Resolution;
                    int x0 = Math.Min((int)(fx * (res - 1)), res - 2), z0 = Math.Min((int)(fz * (res - 1)), res - 2);
                    bool edge = x0 == 0 || z0 == 0 || x0 == res - 2 || z0 == res - 2;
                    int pc = key.Item1 * 1024 + x0, pr = key.Item2 * 1024 + (1024 - z0);
                    if (edge && t.Flags == flags && Current.DeltaMmAt(pc, pr) == 0 && Current.DeltaMmAt(pc + 1, pr) == 0
                        && Current.DeltaMmAt(pc, pr - 1) == 0 && Current.DeltaMmAt(pc + 1, pr - 1) == 0) continue;
                    if (bad++ < 4)
                    {
                        double x = b.West + fx * _cache.TileMetres, yN = b.North - _cache.TileMetres + fz * _cache.TileMetres;
                        int c = (int)Math.Floor(x - _lattice.West), r = (int)Math.Floor(_lattice.North - yN);
                        first += $"{(first == null ? "" : "; ")}tree {i} of tile {key} at lattice ({c}, {r}), shown ground {ShownGround.DeltaMmAt(c, r)} mm, committed {Current.DeltaMmAt(c, r)} mm: " +
                                 $"flags {t.Flags} y {t.Position.y} shown, flags {flags} y {y} fresh{(ReferenceEquals(ShownGround, Current) ? "" : " (shown ground isn't the committed one)")}";
                    }
                }
            }
            return bad;
        }
    }
}
