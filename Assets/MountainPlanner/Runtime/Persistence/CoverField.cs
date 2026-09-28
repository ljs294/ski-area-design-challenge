#nullable enable
using System;
using System.Linq;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// The package's cover layers as smooth functions of position, fed to <see cref="GroundCover"/>
    /// (0.3 §4.4, task 07). Like the heights, cover is one global function, so neighbouring tiles
    /// share identical edges.
    /// - Canopy: share of 1 m cells with trees within 2 m (a 5 × 5 box), core only.
    /// - WorldCover: per-group weights, bilinear between 10 m cell centres, sampled through a small
    ///   noise warp so the 10 m grid never shows as straight edges.
    /// - OpenStreetMap: bilinear on the 1 m (core) or 2 m (ring) coverage rasters.
    /// </summary>
    public sealed class CoverField
    {
        public const int CanopyRadius = 2;
        /// <summary>Canopy is trusted fully this far inside the core, easing to WorldCover at its edge.</summary>
        public const double CanopyBlendMetres = 30;
        public const double WarpMetres = 7, WarpWavelength = 45, JitterWavelength = 25;

        readonly byte[]? _treeShare;          // 0–255 per 1 m core cell
        readonly GridHeader _canopy;
        readonly byte[]? _cover;
        readonly GridHeader _coverHeader;
        readonly byte[]? _osmCore, _osmRing;
        readonly GridHeader _osmCoreHeader, _osmRingHeader;
        readonly TerrainCache.HeightField _heights;
        readonly ulong _seed;

        public CoverField(PackageManifest package, string folder, TerrainCache.HeightField heights)
        {
            _heights = heights;
            _seed = CoverNoise.SeedFor(package.Site.CentreX, package.Site.CentreY);
            if (package.Layers.Any(l => l.Id == "cover")) _cover = ResortPackage.ReadByteLayer(folder, package, "cover", out _coverHeader);
            if (package.Layers.Any(l => l.Id == "canopy-core"))
            {
                byte[] canopy = ResortPackage.ReadByteLayer(folder, package, "canopy-core", out _canopy);
                _treeShare = TreeShare(canopy, _canopy.Width, _canopy.Height, CanopyRadius);
            }
            if (package.Layers.Any(l => l.Id == "osm-core")) _osmCore = ResortPackage.ReadByteLayer(folder, package, "osm-core", out _osmCoreHeader);
            if (package.Layers.Any(l => l.Id == "osm-ring")) _osmRing = ResortPackage.ReadByteLayer(folder, package, "osm-ring", out _osmRingHeader);
        }

        public bool HasOsm => _osmCore != null;

        /// <summary>Everything the cover rules need at (x, y).</summary>
        public CoverSample Sample(double x, double y, double slopeStep)
        {
            var s = new CoverSample();
            if (_treeShare != null)
            {
                double inside = InsideDistance(_canopy, x, y);
                if (inside > 0)
                {
                    double t = Math.Min(1, inside / CanopyBlendMetres);
                    s.CanopyWeight = t * t * (3 - 2 * t);
                    s.Canopy = Bilinear(_treeShare, _canopy, 1, 0, x, y) / 255.0;
                }
            }

            double wx = x + CoverNoise.At(_seed + 1, x, y, WarpWavelength) * WarpMetres;
            double wy = y + CoverNoise.At(_seed + 2, x, y, WarpWavelength) * WarpMetres;
            WorldCoverWeights(wx, wy, ref s);

            byte[]? osm = null;
            GridHeader oh = default;
            if (_osmCore != null && InsideDistance(_osmCoreHeader, x, y, 2) > 0.5) { osm = _osmCore; oh = _osmCoreHeader; }
            else if (_osmRing != null) { osm = _osmRing; oh = _osmRingHeader; }
            if (osm != null)
            {
                s.OsmWater = Bilinear(osm, oh, 2, 0, x, y) / 255.0;
                s.OsmDeveloped = Bilinear(osm, oh, 2, 1, x, y) / 255.0;
            }

            double hx = _heights.At(x + slopeStep, y) - _heights.At(x - slopeStep, y);
            double hy = _heights.At(x, y + slopeStep) - _heights.At(x, y - slopeStep);
            s.SlopeDegrees = Math.Atan(Math.Sqrt(hx * hx + hy * hy) / (2 * slopeStep)) * (180 / Math.PI);
            s.Noise = CoverNoise.At(_seed + 3, x, y, JitterWavelength);
            return s;
        }

        void WorldCoverWeights(double x, double y, ref CoverSample s)
        {
            if (_cover == null)
            {
                s.WcGrass = 1;
                return;
            }
            var h = _coverHeader;
            double fx = (x - h.West) / h.CellSize - 0.5, fy = (h.North - y) / h.CellSize - 0.5;
            fx = Math.Max(0, Math.Min(h.Width - 1, fx));
            fy = Math.Max(0, Math.Min(h.Height - 1, fy));
            int x0 = Math.Min((int)fx, h.Width - 2), y0 = Math.Min((int)fy, h.Height - 2);
            double tx = fx - x0, ty = fy - y0;
            Span<double> g = stackalloc double[5];
            Add(g, _cover[y0 * h.Width + x0], (1 - tx) * (1 - ty));
            Add(g, _cover[y0 * h.Width + x0 + 1], tx * (1 - ty));
            Add(g, _cover[(y0 + 1) * h.Width + x0], (1 - tx) * ty);
            Add(g, _cover[(y0 + 1) * h.Width + x0 + 1], tx * ty);
            double total = g[0] + g[1] + g[2] + g[3] + g[4];
            if (total <= 1e-9)
            {
                s.WcGrass = 1;
                return;
            }
            s.WcTrees = g[0] / total;
            s.WcGrass = g[1] / total;
            s.WcRock = g[2] / total;
            s.WcDeveloped = g[3] / total;
            s.WcWater = g[4] / total;
        }

        static void Add(Span<double> groups, byte cls, double weight)
        {
            int g = GroundCover.Group(cls);
            if (g >= 0) groups[g] += weight;
        }

        /// <summary>Distance from (x, y) to the nearest edge of a grid, positive inside.</summary>
        static double InsideDistance(GridHeader h, double x, double y, int bands = 1)
        {
            double east = h.West + h.Width / bands * h.CellSize, south = h.North - h.Height * h.CellSize;
            return Math.Min(Math.Min(x - h.West, east - x), Math.Min(h.North - y, y - south));
        }

        /// <summary>Bilinear between cell centres on one band of a band-interleaved byte grid, clamped at the edge.</summary>
        static double Bilinear(byte[] g, GridHeader h, int bands, int band, double x, double y)
        {
            int width = h.Width / bands;
            double fx = (x - h.West) / h.CellSize - 0.5, fy = (h.North - y) / h.CellSize - 0.5;
            fx = Math.Max(0, Math.Min(width - 1, fx));
            fy = Math.Max(0, Math.Min(h.Height - 1, fy));
            int x0 = Math.Min((int)fx, width - 2), y0 = Math.Min((int)fy, h.Height - 2);
            double tx = fx - x0, ty = fy - y0;
            long i = ((long)y0 * width + x0) * bands + band, below = (long)width * bands;
            double top = g[i] + (g[i + bands] - g[i]) * tx;
            double bottom = g[i + below] + (g[i + below + bands] - g[i + below]) * tx;
            return top + (bottom - top) * ty;
        }

        /// <summary>Share of trees (canopy ≥ 3 m) in a (2r+1)² box around each cell, as 0–255 (separable box sums).</summary>
        public static byte[] TreeShare(byte[] canopy, int width, int height, int radius)
        {
            var rows = new ushort[canopy.Length];
            for (int r = 0; r < height; r++)
            {
                long row = (long)r * width;
                int sum = 0;
                for (int c = -radius; c <= radius; c++) if (c >= 0 && c < width && ForestRule.IsTree(canopy[row + c])) sum++;
                for (int c = 0; c < width; c++)
                {
                    rows[row + c] = (ushort)sum;
                    int add = c + radius + 1, drop = c - radius;
                    if (add < width && ForestRule.IsTree(canopy[row + add])) sum++;
                    if (drop >= 0 && ForestRule.IsTree(canopy[row + drop])) sum--;
                }
            }
            var result = new byte[canopy.Length];
            int n = (2 * radius + 1) * (2 * radius + 1);
            for (int c = 0; c < width; c++)
            {
                int sum = 0;
                for (int r = -radius; r <= radius; r++) if (r >= 0 && r < height) sum += rows[(long)r * width + c];
                for (int r = 0; r < height; r++)
                {
                    result[(long)r * width + c] = (byte)((sum * 255 + n / 2) / n);
                    int add = r + radius + 1, drop = r - radius;
                    if (add < height) sum += rows[(long)add * width + c];
                    if (drop >= 0) sum -= rows[(long)drop * width + c];
                }
            }
            return result;
        }

        /// <summary>
        /// A tile's cover texels: <paramref name="resolution"/>² texels spanning the tile edge to edge
        /// (texel i at West + i·size/(resolution−1), as Unity samples splat maps), six bytes each: the
        /// five ground-layer weights (summing to 255) then snow cover.
        /// </summary>
        public byte[] BuildTile(AlbersBox bounds, int resolution)
        {
            var result = new byte[resolution * resolution * TerrainCache.CoverBands];
            double size = bounds.East - bounds.West, step = size / (resolution - 1);
            double slopeStep = Math.Max(1, step);
            Span<double> weights = stackalloc double[GroundCover.Layers];
            Span<byte> quantized = stackalloc byte[GroundCover.Layers];
            for (int j = 0; j < resolution; j++)
            {
                double y = bounds.North - j * step;
                for (int i = 0; i < resolution; i++)
                {
                    double x = bounds.West + i * step;
                    var sample = Sample(x, y, slopeStep);
                    GroundCover.Classify(sample, weights, out double snow);
                    GroundCover.Quantize(weights, quantized);
                    int o = (j * resolution + i) * TerrainCache.CoverBands;
                    for (int k = 0; k < GroundCover.Layers; k++) result[o + k] = quantized[k];
                    result[o + GroundCover.Layers] = GroundCover.ToByte(snow);
                }
            }
            return result;
        }
    }
}
