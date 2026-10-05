using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json;

namespace MountainPlanner.Persistence
{
    /// <summary>Progress while preparing a mountain's terrain tiles (U6).</summary>
    public readonly struct CacheProgress
    {
        public readonly int Tile;
        public readonly int Tiles;

        public CacheProgress(int tile, int tiles)
        {
            Tile = tile;
            Tiles = tiles;
        }
    }

    /// <summary>cache.json: how the tiles map back to metres.</summary>
    public sealed class CacheManifest
    {
        public int CacheVersion { get; set; }
        public string PackageId { get; set; } = "";
        /// <summary>World height of stored value 0, in metres (NAVD88).</summary>
        public double HeightMin { get; set; }
        /// <summary>Metres between stored values 0 and <see cref="TerrainCache.MaxValue"/>.</summary>
        public double HeightRange { get; set; }
        public double TileMetres { get; set; }
        public List<CacheTile> Tiles { get; set; } = new List<CacheTile>();
    }

    public sealed class CacheTile
    {
        public int Column { get; set; }
        public int Row { get; set; }
        public int Resolution { get; set; }
        public double Spacing { get; set; }
        public double West { get; set; }
        public double North { get; set; }
        public bool Core { get; set; }
        public string File { get; set; } = "";
        public string Sha256 { get; set; } = "";
        /// <summary>Ground cover (task 07): <see cref="CoverResolution"/>² texels × <see cref="TerrainCache.CoverBands"/> bytes.</summary>
        public string CoverFile { get; set; } = "";
        public int CoverResolution { get; set; }
        public string CoverSha256 { get; set; } = "";
        /// <summary>The tile's trees (task 08): <see cref="ForestField.BytesPerTree"/> bytes each.</summary>
        public string TreesFile { get; set; } = "";
        public int TreeCount { get; set; }
        public string TreesSha256 { get; set; } = "";
        /// <summary>The tile's cliff shell (<see cref="CliffMeshData"/>); empty where there are no cliffs.</summary>
        public string CliffFile { get; set; } = "";
        public int CliffTriangles { get; set; }
        public string CliffSha256 { get; set; } = "";
    }

    /// <summary>
    /// The terrain cache (0.3 §4.3 step 3, T5): 1,024 m tiles of Unity-ready heights, built once per
    /// package and rebuilt when <see cref="Version"/> changes. Regenerable; never the only copy.
    ///
    /// Heights are one global function of position: the 1 m core inside the core, eased into the 2 m
    /// ring over 16 m at the core's edge, bilinear between cell centres. Every tile samples that same
    /// function, so neighbours share identical edge values. Where a 1 m tile borders a 2 m tile, the
    /// 1 m tile's in-between edge samples are set to the midpoint of the 2 m tile's, so there are no
    /// cracks (T-junctions).
    /// </summary>
    public static class TerrainCache
    {
        /// <summary>Bump when the tile format or sampling changes: existing caches are then rebuilt.</summary>
        public const int Version = 12;   // 12: ring forest at the core's height, in patches (polish); 11: roads (task 12d), eight cover bands

        /// <summary>
        /// Splat texels per tile edge: 1 m in core tiles, 4 m in the ring (Unity needs powers of two).
        /// </summary>
        public const int CoreCoverResolution = 1024, RingCoverResolution = 256;
        /// <summary>Bytes per cover texel: five ground-layer weights (sum 255), then snow cover.</summary>
        public const int CoverBands = 8;

        /// <summary>
        /// Unity terrain heightmaps hold 0–32,766 (15 effective bits; normalised 1.0 = 32,766), so the
        /// cache stores exactly what Unity will hold. Checked against real TerrainData in task 06.
        /// </summary>
        public const int MaxValue = 32766;

        public const double CoreBlendMetres = 16;
        public const string ManifestFile = "cache.json";

        static readonly JsonSerializerSettings Json = new JsonSerializerSettings { Formatting = Formatting.Indented };

        public static string FolderFor(string packageFolder) => Path.Combine(packageFolder, "cache-v" + Version);

        /// <summary>True when the cache exists, matches the package and has the current version.</summary>
        public static bool IsCurrent(string packageFolder, PackageManifest package)
        {
            string path = Path.Combine(FolderFor(packageFolder), ManifestFile);
            if (!File.Exists(path)) return false;
            try
            {
                var m = JsonConvert.DeserializeObject<CacheManifest>(File.ReadAllText(path));
                return m != null && m.CacheVersion == Version && m.PackageId == package.PackageId
                       && m.Tiles.All(t => File.Exists(Path.Combine(FolderFor(packageFolder), t.File))
                                           && File.Exists(Path.Combine(FolderFor(packageFolder), t.CoverFile))
                                           && File.Exists(Path.Combine(FolderFor(packageFolder), t.TreesFile))
                                           && File.Exists(Path.Combine(FolderFor(packageFolder), t.CliffFile)));
            }
            catch (JsonException) { return false; }
        }

        public static CacheManifest ReadManifest(string packageFolder) =>
            JsonConvert.DeserializeObject<CacheManifest>(File.ReadAllText(Path.Combine(FolderFor(packageFolder), ManifestFile)))
            ?? throw new InvalidDataException("Empty cache manifest.");

        /// <summary>A tile's ground cover texels (see <see cref="CoverField.BuildTile"/>), verified against their hash.</summary>
        public static byte[] ReadCover(string packageFolder, CacheTile tile)
        {
            byte[] values;
            using (var fs = File.OpenRead(Path.Combine(FolderFor(packageFolder), tile.CoverFile))) values = GridFile.ReadBytes(fs, out _);
            if (GridFile.HashValues(values) != tile.CoverSha256) throw new InvalidDataException($"Cache cover {tile.CoverFile} doesn't match its hash.");
            return values;
        }

        /// <summary>A tile's packed trees (see <see cref="ForestField.Decode"/>), verified against their hash.</summary>
        public static byte[] ReadTrees(string packageFolder, CacheTile tile)
        {
            byte[] values = File.ReadAllBytes(Path.Combine(FolderFor(packageFolder), tile.TreesFile));
            if (GridFile.HashValues(values) != tile.TreesSha256) throw new InvalidDataException($"Cache trees {tile.TreesFile} don't match their hash.");
            return values;
        }

        /// <summary>A tile's cliff shell, verified against its hash.</summary>
        public static CliffMeshData ReadCliffs(string packageFolder, CacheTile tile)
        {
            byte[] values = File.ReadAllBytes(Path.Combine(FolderFor(packageFolder), tile.CliffFile));
            if (GridFile.HashValues(values) != tile.CliffSha256) throw new InvalidDataException($"Cache cliffs {tile.CliffFile} don't match their hash.");
            return CliffMeshData.FromBytes(values);
        }

        public static ushort[] ReadTile(string packageFolder, CacheTile tile)
        {
            ushort[] values;
            using (var fs = File.OpenRead(Path.Combine(FolderFor(packageFolder), tile.File))) values = GridFile.ReadShorts(fs, out _);
            if (GridFile.HashValues(values) != tile.Sha256) throw new InvalidDataException($"Cache tile {tile.File} doesn't match its hash.");
            return values;
        }

        /// <summary>
        /// Builds (or rebuilds) the cache for a package. Old cache versions are removed. The forest grows with
        /// <paramref name="forestPlanter"/> (the game passes its Burst planter), else as plain C#; both give the same bytes.
        /// </summary>
        public static CacheManifest Build(string packageFolder, PackageManifest package, IProgress<CacheProgress> progress, CancellationToken ct = default,
                                          IForestPlanter forestPlanter = null)
        {
            float[] core = ResortPackage.ReadLayer(packageFolder, package, "heights-core", out var coreHeader);
            float[] ring = ResortPackage.ReadLayer(packageFolder, package, "heights-ring", out var ringHeader);
            var coreLayer = package.Layers.First(l => l.Id == "heights-core");
            var ringLayer = package.Layers.First(l => l.Id == "heights-ring");
            var heights = new HeightField(core, coreHeader, ring, ringHeader);
            var cover = new CoverField(package, packageFolder, heights);
            var forest = new ForestField(package, packageFolder, heights);
            var cliffField = new CliffField(package, heights);

            var site = SiteSquare.Create(new AlbersPoint(package.Site.CentreX, package.Site.CentreY), package.Site.SizeMetres / 1000.0);
            var tiles = TileGrid.For(site);
            double min = Math.Min(coreLayer.Min, ringLayer.Min), max = Math.Max(coreLayer.Max, ringLayer.Max);
            double range = Math.Max(1, max - min);

            foreach (string old in Directory.GetDirectories(packageFolder, "cache-v*"))
                if (!string.Equals(Path.GetFullPath(old).TrimEnd('\\', '/'), Path.GetFullPath(FolderFor(packageFolder)).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(old, true);
            string folder = FolderFor(packageFolder);
            Directory.CreateDirectory(folder);

            var manifest = new CacheManifest
            {
                CacheVersion = Version, PackageId = package.PackageId, HeightMin = min, HeightRange = range, TileMetres = TileGrid.TileMetres,
            };
            var keys = tiles.All().ToList();
            var forestTiles = forest.BuildAll(tiles, forestPlanter);
            ct.ThrowIfCancellationRequested();
            var built = new CacheTile[keys.Count];
            int done = 0;
            // Tiles are independent, so they build in parallel; each file's content doesn't depend on order.
            System.Threading.Tasks.Parallel.For(0, keys.Count, new System.Threading.Tasks.ParallelOptions { CancellationToken = ct }, n =>
            {
                var key = keys[n];
                int res = tiles.Resolution(key);
                double spacing = tiles.SampleSpacing(key);
                var b = tiles.Bounds(key);
                var values = new ushort[res * res];
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                        values[j * res + i] = Quantize(heights.At(b.West + i * spacing, b.North - j * spacing), min, range);
                if (tiles.IsCore(key)) MatchRingEdges(values, res, tiles, key);

                string file = $"t{key.Column}_{key.Row}.h16";
                using (var fs = File.Create(Path.Combine(folder, file)))
                    GridFile.Write(fs, new GridHeader(GridValueType.UInt16, res, res, b.West, b.North, spacing), values);

                int coverRes = tiles.IsCore(key) ? CoreCoverResolution : RingCoverResolution;
                byte[] texels = cover.BuildTile(b, coverRes);
                string coverFile = $"t{key.Column}_{key.Row}.cover";
                using (var fs = File.Create(Path.Combine(folder, coverFile)))
                    GridFile.Write(fs, new GridHeader(GridValueType.UInt8, coverRes * CoverBands, coverRes, b.West, b.North, (b.East - b.West) / (coverRes - 1)), texels);

                byte[] trees = ForestField.Encode(forestTiles[n], TileGrid.TileMetres);
                string treesFile = $"t{key.Column}_{key.Row}.trees";
                File.WriteAllBytes(Path.Combine(folder, treesFile), trees);
                var cliff = cliffField.BuildTile(b);
                byte[] cliffBytes = cliff.ToBytes();
                string cliffFile = $"t{key.Column}_{key.Row}.cliff";
                File.WriteAllBytes(Path.Combine(folder, cliffFile), cliffBytes);

                built[n] = new CacheTile
                {
                    Column = key.Column, Row = key.Row, Resolution = res, Spacing = spacing, West = b.West, North = b.North,
                    Core = tiles.IsCore(key), File = file, Sha256 = GridFile.HashValues(values),
                    CoverFile = coverFile, CoverResolution = coverRes, CoverSha256 = GridFile.HashValues(texels),
                    TreesFile = treesFile, TreeCount = trees.Length / ForestField.BytesPerTree, TreesSha256 = GridFile.HashValues(trees),
                    CliffFile = cliffFile, CliffTriangles = cliff.Indices.Length / 3, CliffSha256 = GridFile.HashValues(cliffBytes),
                };
                progress?.Report(new CacheProgress(Interlocked.Increment(ref done), keys.Count));
            });
            manifest.Tiles.AddRange(built);
            File.WriteAllText(Path.Combine(folder, ManifestFile), JsonConvert.SerializeObject(manifest, Json) + "\n", new UTF8Encoding(false));
            return manifest;
        }

        public static ushort Quantize(double height, double min, double range)
        {
            double q = Math.Round((height - min) / range * MaxValue, MidpointRounding.AwayFromZero);
            return (ushort)(q < 0 ? 0 : q > MaxValue ? MaxValue : q);
        }

        public static double Dequantize(ushort value, double min, double range) => min + value * range / MaxValue;

        /// <summary>
        /// On each edge a 1 m tile shares with a 2 m tile, samples between the 2 m tile's vertices are
        /// set to the midpoint of their neighbours, so the edge is the same straight segments on both
        /// sides and the mesh has no crack.
        /// </summary>
        static void MatchRingEdges(ushort[] v, int res, TileGrid tiles, TileKey key)
        {
            bool Ring(int dc, int dr)
            {
                var k = new TileKey(key.Column + dc, key.Row + dr);
                return k.Column >= 0 && k.Row >= 0 && k.Column < tiles.Columns && k.Row < tiles.Rows && !tiles.IsCore(k);
            }
            void Fix(Func<int, int> index)
            {
                for (int s = 1; s < res - 1; s += 2)
                    v[index(s)] = (ushort)((v[index(s - 1)] + v[index(s + 1)] + 1) / 2);
            }
            if (Ring(0, -1)) Fix(s => s);                         // north edge: row 0
            if (Ring(0, 1)) Fix(s => (res - 1) * res + s);        // south edge: last row
            if (Ring(-1, 0)) Fix(s => s * res);                   // west edge: column 0
            if (Ring(1, 0)) Fix(s => s * res + res - 1);          // east edge: last column
        }

        /// <summary>The package's heights as one continuous function of position.</summary>
        public sealed class HeightField
        {
            readonly float[] _core, _ring;
            readonly GridHeader _c, _r;

            public HeightField(float[] core, GridHeader coreHeader, float[] ring, GridHeader ringHeader)
            {
                _core = core;
                _ring = ring;
                _c = coreHeader;
                _r = ringHeader;
            }

            public double At(double x, double y)
            {
                double ringH = Bilinear(_ring, _r, x, y);
                double cw = _c.West, cn = _c.North, ce = cw + _c.Width * _c.CellSize, cs = cn - _c.Height * _c.CellSize;
                double inside = Math.Min(Math.Min(x - cw, ce - x), Math.Min(cn - y, y - cs));
                if (inside <= 0) return ringH;
                double t = Math.Min(1, inside / CoreBlendMetres);
                t = t * t * (3 - 2 * t);
                return ringH + (Bilinear(_core, _c, x, y) - ringH) * t;
            }

            /// <summary>Bilinear between cell centres, clamped at the grid's edge.</summary>
            static double Bilinear(float[] g, GridHeader h, double x, double y)
            {
                double fx = (x - h.West) / h.CellSize - 0.5, fy = (h.North - y) / h.CellSize - 0.5;
                fx = Math.Max(0, Math.Min(h.Width - 1, fx));
                fy = Math.Max(0, Math.Min(h.Height - 1, fy));
                int x0 = Math.Min((int)fx, h.Width - 2), y0 = Math.Min((int)fy, h.Height - 2);
                double tx = fx - x0, ty = fy - y0;
                int i = y0 * h.Width + x0;
                double top = g[i] + (g[i + 1] - g[i]) * tx, bottom = g[i + h.Width] + (g[i + h.Width + 1] - g[i + h.Width]) * tx;
                return top + (bottom - top) * ty;
            }
        }
    }
}
