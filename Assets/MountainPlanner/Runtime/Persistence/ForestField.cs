#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Flora;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Persistence
{
    /// <summary>
    /// Places a tile's trees from the package's canopy, land cover and species layers (0.3 §4.5, T7).
    /// Deterministic: every candidate comes from a keyed hash of its global 10 m cell, so the same
    /// package always grows the same forest, and a tree belongs to exactly one tile.
    /// </summary>
    public sealed class ForestField
    {
        /// <summary>Bytes per stored tree: x and z (16-bit fractions of the tile), height, prototype, rotation, width.</summary>
        public const int BytesPerTree = 8;
        public const double HeightStep = 0.25, WidthStep = 1.0 / 32;

        readonly byte[]? _canopy;
        readonly GridHeader _canopyHeader;
        readonly byte[]? _cover;
        readonly GridHeader _coverHeader;
        readonly byte[]? _speciesIds, _speciesWeights;
        readonly GridHeader _speciesHeader;
        readonly int[] _modelOfIndex;          // species table index (1-based) → model
        readonly double[] _siteShares;         // model → share of the site's biomass
        readonly AlbersBox _core;
        /// <summary>Tree share given to a ring WorldCover forest cell, measured in the core (see constructor).</summary>
        public readonly double RingTreeShare;
        readonly ulong _seed;
        readonly ForestCalibration _calibration = ForestCalibration.Default;

        public ForestField(PackageManifest package, string folder)
        {
            _seed = CoverNoise.SeedFor(package.Site.CentreX, package.Site.CentreY) ^ 0x7EE5UL;
            var site = SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0);
            _core = site.Core;
            if (package.Layers.Any(l => l.Id == "canopy-core")) _canopy = ResortPackage.ReadByteLayer(folder, package, "canopy-core", out _canopyHeader);
            if (package.Layers.Any(l => l.Id == "cover")) _cover = ResortPackage.ReadByteLayer(folder, package, "cover", out _coverHeader);
            if (package.Layers.Any(l => l.Id == "species-ids") && package.Layers.Any(l => l.Id == "species-weights"))
            {
                _speciesIds = ResortPackage.ReadByteLayer(folder, package, "species-ids", out _speciesHeader);
                _speciesWeights = ResortPackage.ReadByteLayer(folder, package, "species-weights", out _);
            }
            _modelOfIndex = new int[256];
            _siteShares = new double[SpeciesMap.Models.Length];
            foreach (var s in package.Species)
            {
                if (s.Index <= 0 || s.Index > 255) continue;
                int model = SpeciesMap.ModelFor(s.Spcd);
                _modelOfIndex[s.Index] = model;
                _siteShares[model] += s.ShareOfBiomass;
            }
            if (_siteShares.Sum() <= 0) _siteShares[SpeciesMap.IndexOf("douglas_fir")] = 1;
            RingTreeShare = MeasureRingShare();
        }

        /// <summary>
        /// Calibrates the ring against the core: the canopy's total tree share over the core divided by
        /// the number of core cells WorldCover calls forest. Ring forest cells then carry, on average, as
        /// many trees as the core really has per WorldCover forest cell, so density doesn't jump at the seam.
        /// </summary>
        double MeasureRingShare()
        {
            if (_canopy == null || _cover == null) return ForestPlacement.WorldCoverTreeShare;
            var h = _canopyHeader;
            const int m = ForestPlacement.CellMetres;
            double shares = 0;
            long forestCells = 0;
            for (int br = 0; br + m <= h.Height; br += m)
                for (int bc = 0; bc + m <= h.Width; bc += m)
                {
                    int trees = 0;
                    for (int j = 0; j < m; j++)
                    {
                        long row = (long)(br + j) * h.Width + bc;
                        for (int i = 0; i < m; i++) if (_canopy[row + i] >= ForestRule.TreeCode) trees++;
                    }
                    shares += trees / 100.0;
                    if (CoverAt(h.West + bc + m / 2.0, h.North - br - m / 2.0) == 10) forestCells++;
                }
            if (forestCells == 0) return ForestPlacement.WorldCoverTreeShare;
            return Clamp(shares / forestCells, 0.05, 0.8);
        }

        /// <summary>The trees whose positions fall in [West, East) × [South, North) of a tile.</summary>
        public List<PlacedTree> BuildTile(AlbersBox tile)
        {
            var trees = new List<PlacedTree>();
            long c0 = (long)Math.Floor(tile.West / ForestPlacement.CellMetres), c1 = (long)Math.Floor((tile.East - 1e-9) / ForestPlacement.CellMetres);
            long r0 = (long)Math.Floor(tile.South / ForestPlacement.CellMetres), r1 = (long)Math.Floor((tile.North - 1e-9) / ForestPlacement.CellMetres);
            var accepted = new List<(double X, double Y)>(ForestPlacement.MaxTreesPerCell);
            for (long cy = r0; cy <= r1; cy++)
                for (long cx = c0; cx <= c1; cx++)
                    PlaceCell(cx, cy, tile, trees, accepted);
            return trees;
        }

        void PlaceCell(long cx, long cy, AlbersBox tile, List<PlacedTree> trees, List<(double X, double Y)> accepted)
        {
            const int m = ForestPlacement.CellMetres;
            double x0 = cx * m, y0 = cy * m;
            bool core = _canopy != null && Inside(_canopyHeader, x0 + m / 2.0, y0 + m / 2.0);
            double share, dominant, expected, width = 1;
            if (core)
            {
                int trees10 = 0, tallest = 0;
                for (int j = 0; j < m; j++)
                    for (int i = 0; i < m; i++)
                    {
                        int v = CanopyAt(x0 + i + 0.5, y0 + j + 0.5);
                        if (v >= ForestRule.TreeCode) trees10++;
                        if (v > tallest) tallest = v;
                    }
                if (trees10 == 0) return;
                share = trees10 / 100.0;
                dominant = Clamp(_calibration.DominantHeight((byte)tallest), ForestPlacement.MinHeight, ForestPlacement.MaxHeight);
                expected = ForestPlacement.TreesPerCell(share, dominant, _calibration);
            }
            else
            {
                if (_cover == null || CoverAt(x0 + m / 2.0, y0 + m / 2.0) != 10) return;
                share = RingTreeShare;
                dominant = ForestPlacement.RingDominantHeight * (0.85 + 0.3 * ForestPlacement.Hash01(_seed, cx, cy, 0, 7));
                double density = ForestPlacement.RingDensity(OutsideCore(x0 + m / 2.0, y0 + m / 2.0));
                expected = ForestPlacement.TreesPerCell(share, dominant, _calibration) * density;
                width = Math.Min(3, 1 / Math.Sqrt(density));   // fewer, wider crowns in the distance
            }

            int count = (int)Math.Floor(expected + ForestPlacement.Hash01(_seed, cx, cy, 0, 1));
            if (count <= 0) return;
            double spacing = 0.9 * ForestPlacement.CrownRadius(dominant) * width;
            accepted.Clear();
            for (int k = 0; k < count * 6 && accepted.Count < count; k++)
            {
                double x = x0 + m * ForestPlacement.Hash01(_seed, cx, cy, k, 2);
                double y = y0 + m * ForestPlacement.Hash01(_seed, cx, cy, k, 3);
                if (core && CanopyAt(x, y) < ForestRule.TreeCode) continue;    // only where the canopy map has trees
                bool clear = true;
                foreach (var a in accepted)
                    if ((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y) < spacing * spacing) { clear = false; break; }
                if (!clear) continue;
                accepted.Add((x, y));
                if (x < tile.West || x >= tile.East || y < tile.South || y >= tile.North) continue;

                double u = ForestPlacement.Hash01(_seed, cx, cy, k, 4);
                int model = PickModel(x, y, u);
                int variant = Math.Min(SpeciesMap.VariantsPerModel - 1, (int)(ForestPlacement.Hash01(_seed, cx, cy, k, 5) * SpeciesMap.VariantsPerModel));
                double h = dominant * (0.65 + 0.35 * ForestPlacement.Hash01(_seed, cx, cy, k, 6));
                trees.Add(new PlacedTree
                {
                    X = (float)(x - tile.West), Z = (float)(y - tile.South), Height = (float)h,
                    Prototype = (byte)(model * SpeciesMap.VariantsPerModel + variant),
                    Rotation = (byte)(ForestPlacement.Hash01(_seed, cx, cy, k, 8) * 256),
                    WidthScale = (float)(width * (0.85 + 0.3 * ForestPlacement.Hash01(_seed, cx, cy, k, 9))),
                });
            }
        }

        /// <summary>A model drawn from the 30 m cell's BIGMAP species weights, else from the site's mix.</summary>
        int PickModel(double x, double y, double u)
        {
            if (_speciesIds != null && _speciesWeights != null)
            {
                var h = _speciesHeader;
                int width = h.Width / 4;
                int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
                if (c >= 0 && r >= 0 && c < width && r < h.Height)
                {
                    long o = ((long)r * width + c) * 4;
                    int total = 0;
                    for (int k = 0; k < 4; k++) if (_speciesIds[o + k] != 0) total += _speciesWeights[o + k];
                    if (total > 0)
                    {
                        double pick = u * total;
                        for (int k = 0; k < 4; k++)
                        {
                            if (_speciesIds[o + k] == 0) continue;
                            pick -= _speciesWeights[o + k];
                            if (pick < 0) return _modelOfIndex[_speciesIds[o + k]];
                        }
                    }
                }
            }
            double sum = _siteShares.Sum(), p = u * sum;
            for (int k = 0; k < _siteShares.Length; k++)
            {
                p -= _siteShares[k];
                if (p < 0 && _siteShares[k] > 0) return k;
            }
            return Array.FindLastIndex(_siteShares, s => s > 0);
        }

        int CanopyAt(double x, double y)
        {
            var h = _canopyHeader;
            int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
            if (c < 0 || r < 0 || c >= h.Width || r >= h.Height) return 0;
            return _canopy![(long)r * h.Width + c];
        }

        int CoverAt(double x, double y)
        {
            var h = _coverHeader;
            int c = (int)Math.Floor((x - h.West) / h.CellSize), r = (int)Math.Floor((h.North - y) / h.CellSize);
            if (c < 0 || r < 0 || c >= h.Width || r >= h.Height) return 0;
            return _cover![(long)r * h.Width + c];
        }

        static bool Inside(GridHeader h, double x, double y) =>
            x >= h.West && x < h.West + h.Width * h.CellSize && y <= h.North && y > h.North - h.Height * h.CellSize;

        double OutsideCore(double x, double y)
        {
            double dx = Math.Max(0, Math.Max(_core.West - x, x - _core.East));
            double dy = Math.Max(0, Math.Max(_core.South - y, y - _core.North));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Packs trees for the cache: 8 bytes each; positions are 16-bit fractions of the tile (1.6 cm steps).</summary>
        public static byte[] Encode(List<PlacedTree> trees, double tileMetres)
        {
            var bytes = new byte[trees.Count * BytesPerTree];
            for (int i = 0; i < trees.Count; i++)
            {
                var t = trees[i];
                int o = i * BytesPerTree;
                ushort x = (ushort)Math.Min(65535, Math.Round(t.X / tileMetres * 65535)), z = (ushort)Math.Min(65535, Math.Round(t.Z / tileMetres * 65535));
                bytes[o] = (byte)x;
                bytes[o + 1] = (byte)(x >> 8);
                bytes[o + 2] = (byte)z;
                bytes[o + 3] = (byte)(z >> 8);
                bytes[o + 4] = (byte)Math.Min(255, Math.Round(t.Height / HeightStep));
                bytes[o + 5] = t.Prototype;
                bytes[o + 6] = t.Rotation;
                bytes[o + 7] = (byte)Math.Min(255, Math.Round(t.WidthScale / WidthStep));
            }
            return bytes;
        }

        /// <summary>Unpacks one tree: position as a 0–1 fraction of the tile (x east, z north).</summary>
        public static void Decode(byte[] bytes, int i, out float fx, out float fz, out float height, out int prototype, out float rotationRadians, out float width)
        {
            int o = i * BytesPerTree;
            fx = (bytes[o] | bytes[o + 1] << 8) / 65535f;
            fz = (bytes[o + 2] | bytes[o + 3] << 8) / 65535f;
            height = bytes[o + 4] * (float)HeightStep;
            prototype = bytes[o + 5];
            rotationRadians = bytes[o + 6] / 256f * 2 * (float)Math.PI;
            width = bytes[o + 7] * (float)WidthStep;
        }
    }
}
