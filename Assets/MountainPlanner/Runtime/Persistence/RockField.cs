#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// Places a tile's rocks from the lidar slope and the land cover (<see cref="RockPlacement"/>).
    /// Like the forest, candidates come from keyed hashes of global 10 m cells, so every rock belongs to
    /// one tile and the same package always gets the same rocks. Stored in the tree format
    /// (<see cref="ForestField.Encode"/>): prototypes 0–5 are boulders, 6–9 outcrops.
    /// </summary>
    public sealed class RockField
    {
        readonly TerrainCache.HeightField _heights;
        readonly byte[]? _cover, _canopy, _osm;
        readonly GridHeader _coverHeader, _canopyHeader, _osmHeader;
        readonly AlbersBox _core;
        readonly ulong _seed;

        public RockField(PackageManifest package, string folder, TerrainCache.HeightField heights)
        {
            _heights = heights;
            _seed = CoverNoise.SeedFor(package.Site.CentreX, package.Site.CentreY) ^ 0x50C4UL;
            _core = SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0).Core;
            if (package.Layers.Any(l => l.Id == "cover")) _cover = ResortPackage.ReadByteLayer(folder, package, "cover", out _coverHeader);
            if (package.Layers.Any(l => l.Id == "canopy-core")) _canopy = ResortPackage.ReadByteLayer(folder, package, "canopy-core", out _canopyHeader);
            if (package.Layers.Any(l => l.Id == "osm-ring")) _osm = ResortPackage.ReadByteLayer(folder, package, "osm-ring", out _osmHeader);
        }

        public List<PlacedTree> BuildTile(AlbersBox tile)
        {
            var rocks = new List<PlacedTree>();
            const int m = RockPlacement.CellMetres;
            long c0 = (long)Math.Floor(tile.West / m), c1 = (long)Math.Floor((tile.East - 1e-9) / m);
            long r0 = (long)Math.Floor(tile.South / m), r1 = (long)Math.Floor((tile.North - 1e-9) / m);
            for (long cy = r0; cy <= r1; cy++)
                for (long cx = c0; cx <= c1; cx++)
                    PlaceCell(cx, cy, tile, rocks);
            return rocks;
        }

        void PlaceCell(long cx, long cy, AlbersBox tile, List<PlacedTree> rocks)
        {
            const int m = RockPlacement.CellMetres;
            double x0 = cx * m, y0 = cy * m, xc = x0 + m / 2.0, yc = y0 + m / 2.0;
            int cls = CoverAt(xc, yc);
            if (cls == 80 || cls == 50) return;                                  // water, built-up
            if (_osm != null && OsmAt(xc, yc) > 128) return;                      // mapped water or roads
            double slope = Slope(xc, yc);
            double bare = cls == 60 || cls == 70 ? 1 : 0;
            double trees = TreeShare(xc, yc);

            // Outcrops on the steepest faces, anywhere on the map.
            if (ForestPlacement.Hash01(_seed, cx, cy, 0, 1) < RockPlacement.OutcropChance(slope))
                Add(rocks, tile, cx, cy, 0, x0, y0, RockPlacement.BoulderVariants, RockPlacement.OutcropVariants,
                    RockPlacement.OutcropSize(ForestPlacement.Hash01(_seed, cx, cy, 0, 2)), 1.0 + 0.6 * ForestPlacement.Hash01(_seed, cx, cy, 0, 3));

            // Boulders and talus, near enough to see.
            if (OutsideCore(xc, yc) > RockPlacement.BoulderRingMetres) return;
            double expected = RockPlacement.MaxBouldersPerCell * RockPlacement.Rockiness(slope, bare, trees);
            int count = (int)Math.Floor(expected + ForestPlacement.Hash01(_seed, cx, cy, 0, 4));
            for (int k = 1; k <= count; k++)
                Add(rocks, tile, cx, cy, k, x0, y0, 0, RockPlacement.BoulderVariants,
                    RockPlacement.BoulderSize(ForestPlacement.Hash01(_seed, cx, cy, k, 5)), 0.8 + 0.6 * ForestPlacement.Hash01(_seed, cx, cy, k, 6));
        }

        void Add(List<PlacedTree> rocks, AlbersBox tile, long cx, long cy, int k, double x0, double y0, int firstPrototype, int variants, double size, double width)
        {
            const int m = RockPlacement.CellMetres;
            double x = x0 + m * ForestPlacement.Hash01(_seed, cx, cy, k, 7), y = y0 + m * ForestPlacement.Hash01(_seed, cx, cy, k, 8);
            if (x < tile.West || x >= tile.East || y < tile.South || y >= tile.North) return;
            int variant = Math.Min(variants - 1, (int)(ForestPlacement.Hash01(_seed, cx, cy, k, 9) * variants));
            rocks.Add(new PlacedTree
            {
                X = (float)(x - tile.West), Z = (float)(y - tile.South), Height = (float)size,
                Prototype = (byte)(firstPrototype + variant),
                Rotation = (byte)(ForestPlacement.Hash01(_seed, cx, cy, k, 10) * 256),
                WidthScale = (float)width,
            });
        }

        double Slope(double x, double y)
        {
            const double d = 5;
            double hx = _heights.At(x + d, y) - _heights.At(x - d, y), hy = _heights.At(x, y + d) - _heights.At(x, y - d);
            return Math.Atan(Math.Sqrt(hx * hx + hy * hy) / (2 * d)) * (180 / Math.PI);
        }

        int CoverAt(double x, double y) => _cover == null ? 0 : ByteAt(_cover, _coverHeader, 1, 0, x, y);

        int OsmAt(double x, double y) => Math.Max(ByteAt(_osm!, _osmHeader, 2, 0, x, y), ByteAt(_osm!, _osmHeader, 2, 1, x, y));

        double TreeShare(double x, double y)
        {
            if (_canopy == null) return CoverAt(x, y) == 10 ? 0.5 : 0;
            int trees = 0, cells = 0;
            for (int j = -2; j <= 2; j++)
                for (int i = -2; i <= 2; i++)
                {
                    int v = ByteAt(_canopy, _canopyHeader, 1, 0, x + i * 2, y + j * 2, -1);
                    if (v < 0) continue;
                    cells++;
                    if (v >= ForestRule.TreeCode) trees++;
                }
            return cells == 0 ? (CoverAt(x, y) == 10 ? 0.5 : 0) : (double)trees / cells;
        }

        static int ByteAt(byte[] g, GridHeader h, int bands, int band, double x, double y, int outside = 0)
        {
            int width = h.Width / bands;
            int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
            if (c < 0 || r < 0 || c >= width || r >= h.Height) return outside;
            return g[((long)r * width + c) * bands + band];
        }

        double OutsideCore(double x, double y)
        {
            double dx = Math.Max(0, Math.Max(_core.West - x, x - _core.East));
            double dy = Math.Max(0, Math.Max(_core.South - y, y - _core.North));
            return Math.Sqrt(dx * dx + dy * dy);
        }
    }
}
