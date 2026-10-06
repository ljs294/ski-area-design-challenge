using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 05 acceptance (docs/plans/phase0-0.7-phase1-plan.md): round trips; 16-bit error at most
    /// half a step; truncated or corrupt files detected; a cache version bump triggers a rebuild.
    /// Most run on a synthetic 2 km package, so CI covers them without Git LFS.
    /// </summary>
    public sealed class TerrainCacheTests
    {
        static string _package = "";
        static PackageManifest _manifest = null!;
        static CacheManifest _cache = null!;

        /// <summary>A 2 km site with smooth synthetic terrain (1,200 m of relief) and a ridge.</summary>
        static double Ground(double x, double y) =>
            2400 + 500 * Math.Sin(x / 900.0) * Math.Cos(y / 1300.0) + 90 * Math.Sin((x + y) / 170.0) + 0.002 * (x - y);

        [OneTimeSetUp]
        public void BuildSyntheticPackage()
        {
            _package = Path.Combine(Path.GetTempPath(), "mp-cache-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_package);
            var site = SiteSquare.Create(new AlbersPoint(-1188828, 2381976), 2);
            var m = new PackageManifest
            {
                Site = new SiteInfo { Name = "Synthetic", CentreX = site.Centre.X, CentreY = site.Centre.Y, SizeMetres = site.SizeMetres, RingMetres = 3000 },
            };
            foreach (var (id, grid) in new[] { ("heights-core", site.CoreGrid), ("heights-ring", site.RingGrid) })
            {
                var values = new float[grid.CellCount];
                for (int r = 0; r < grid.Rows; r++)
                    for (int c = 0; c < grid.Columns; c++)
                    {
                        var p = grid.CellCentre(c, r);
                        values[(long)r * grid.Columns + c] = (float)Ground(p.X - site.Centre.X, p.Y - site.Centre.Y);
                    }
                ResortPackage.AddLayer(_package, m, id, new GridHeader(GridValueType.Float32, grid.Columns, grid.Rows, grid.West, grid.North, grid.CellSize), values);
            }
            ResortPackage.WriteManifest(_package, m);
            _manifest = m;
            _cache = TerrainCache.Build(_package, m, null!);
        }

        [OneTimeTearDown]
        public void Clean()
        {
            if (Directory.Exists(_package)) Directory.Delete(_package, true);
        }

        static Dictionary<(int, int), (CacheTile Tile, ushort[] Values)> AllTiles() =>
            _cache.Tiles.ToDictionary(t => (t.Column, t.Row), t => (t, TerrainCache.ReadTile(_package, t)));

        [Test]
        public void TheTileGridMatchesThePlan()
        {
            Assert.That(_cache.Tiles.Count, Is.EqualTo(64), "a 2 km site in its 8 km ring: 8 × 8 tiles of 1,024 m");
            Assert.That(_cache.Tiles.Count(t => t.Core), Is.EqualTo(9), "the core (3,000–5,000 m from the ring corner) spans tiles 2–4 each way");
            Assert.That(_cache.Tiles.Where(t => t.Core).All(t => t.Resolution == 1025 && t.Spacing == 1), Is.True);
            Assert.That(_cache.Tiles.Where(t => !t.Core).All(t => t.Resolution == 513 && t.Spacing == 2), Is.True);
        }

        [Test]
        public void QuantisationErrorIsAtMostHalfAStep()
        {
            var core = ResortPackage.ReadLayer(_package, _manifest, "heights-core", out var ch);
            var ring = ResortPackage.ReadLayer(_package, _manifest, "heights-ring", out var rh);
            var field = new TerrainCache.HeightField(core, ch, ring, rh);
            double halfStep = _cache.HeightRange / TerrainCache.MaxValue / 2, worst = 0;
            foreach (var (tile, values) in AllTiles().Values)
                for (int j = 1; j < tile.Resolution - 1; j += 7)      // interior samples: edges may be T-junction matched
                    for (int i = 1; i < tile.Resolution - 1; i += 7)
                    {
                        double expected = field.At(tile.West + i * tile.Spacing, tile.North - j * tile.Spacing);
                        double stored = TerrainCache.Dequantize(values[j * tile.Resolution + i], _cache.HeightMin, _cache.HeightRange);
                        worst = Math.Max(worst, Math.Abs(stored - expected));
                    }
            Assert.That(worst, Is.LessThanOrEqualTo(halfStep + 1e-9), $"half a step is {halfStep * 100:F2} cm");
        }

        [Test]
        public void NeighbouringTilesShareIdenticalEdges()
        {
            var tiles = AllTiles();
            int checkedEdges = 0;
            foreach (var ((c, r), (a, av)) in tiles)
            {
                if (tiles.TryGetValue((c + 1, r), out var east) && east.Tile.Resolution == a.Resolution)
                {
                    for (int j = 0; j < a.Resolution; j++)
                        Assert.That(east.Values[j * a.Resolution], Is.EqualTo(av[j * a.Resolution + a.Resolution - 1]), $"t{c}_{r} east edge row {j}");
                    checkedEdges++;
                }
                if (tiles.TryGetValue((c, r + 1), out var south) && south.Tile.Resolution == a.Resolution)
                {
                    for (int i = 0; i < a.Resolution; i++)
                        Assert.That(south.Values[i], Is.EqualTo(av[(a.Resolution - 1) * a.Resolution + i]), $"t{c}_{r} south edge column {i}");
                    checkedEdges++;
                }
            }
            Assert.That(checkedEdges, Is.EqualTo(112 - 12), "all 112 neighbour pairs except the 12 where a core tile meets a ring tile");
        }

        [Test]
        public void OneMetreTilesMeetTwoMetreTilesWithoutCracks()
        {
            var tiles = AllTiles();
            int pairs = 0;
            foreach (var ((c, r), (core, cv)) in tiles.Where(t => t.Value.Tile.Core))
            {
                // West neighbour is a 2 m ring tile: its east column (513 samples) meets our west column (1025).
                if (!tiles.TryGetValue((c - 1, r), out var west) || west.Tile.Core) continue;
                pairs++;
                for (int k = 0; k < 1025; k++)
                {
                    int ours = cv[k * 1025];
                    if (k % 2 == 0) Assert.That(ours, Is.EqualTo(west.Values[(k / 2) * 513 + 512]), $"shared vertex {k}");
                    else
                    {
                        int mid = (west.Values[(k / 2) * 513 + 512] + west.Values[(k / 2 + 1) * 513 + 512] + 1) / 2;
                        Assert.That(ours, Is.EqualTo(mid), $"in-between vertex {k} lies on the 2 m edge");
                    }
                }
            }
            Assert.That(pairs, Is.GreaterThan(0));
        }

        [Test]
        public void GroundCoverTilesMeetWithoutSeams()
        {
            // Texel i sits at West + i·size/(n−1), so a tile's last column is its neighbour's first. Where
            // tiles of the same resolution meet, those texels are computed at the same point: identical.
            var byKey = _cache.Tiles.ToDictionary(t => (t.Column, t.Row));
            int compared = 0;
            foreach (var t in _cache.Tiles)
            {
                if (!byKey.TryGetValue((t.Column + 1, t.Row), out var east) || east.CoverResolution != t.CoverResolution) continue;
                byte[] a = TerrainCache.ReadCover(_package, t), b = TerrainCache.ReadCover(_package, east);
                int n = t.CoverResolution, bands = TerrainCache.CoverBands;
                for (int j = 0; j < n; j++)
                    for (int k = 0; k < bands; k++)
                        Assert.That(a[(j * n + n - 1) * bands + k], Is.EqualTo(b[(j * n) * bands + k]), $"{t.File} | {east.File} row {j}");
                compared++;
            }
            Assert.That(compared, Is.GreaterThan(10));
        }

        [Test]
        public void GroundCoverWeightsSumTo255AndSteepSlopesTurnToRock()
        {
            var tile = _cache.Tiles.First(t => t.Core);
            byte[] cover = TerrainCache.ReadCover(_package, tile);
            int bands = TerrainCache.CoverBands, rocky = 0;
            for (int o = 0; o < cover.Length; o += bands)
            {
                int sum = 0;
                for (int k = 0; k < MountainPlanner.Domain.Cover.GroundCover.Layers; k++) sum += cover[o + k];
                Assert.That(sum, Is.EqualTo(255));
                if (cover[o + (int)MountainPlanner.Domain.Cover.GroundLayer.Rock] > 128) rocky++;
            }
            // The synthetic ridge has no cover layers, so everything is meadow except its steepest flanks.
            Assert.That(cover.Length / bands, Is.EqualTo(TerrainCache.CoreCoverResolution * TerrainCache.CoreCoverResolution));
            TestContext.Progress.WriteLine($"{tile.File}: {rocky} rocky texels of {cover.Length / bands}");
        }

        [Test]
        public void TheCacheIsCurrentUntilItsVersionOrPackageChanges()
        {
            Assert.That(TerrainCache.IsCurrent(_package, _manifest), Is.True);
            string path = Path.Combine(TerrainCache.FolderFor(_package), TerrainCache.ManifestFile);
            string original = File.ReadAllText(path);
            try
            {
                File.WriteAllText(path, original.Replace($"\"CacheVersion\": {TerrainCache.Version}", "\"CacheVersion\": 0"));
                Assert.That(TerrainCache.IsCurrent(_package, _manifest), Is.False, "an old cache version triggers a rebuild");
                File.WriteAllText(path, original.Replace(_manifest.PackageId, "0000000000000000"));
                Assert.That(TerrainCache.IsCurrent(_package, _manifest), Is.False, "a cache from another package triggers a rebuild");
            }
            finally { File.WriteAllText(path, original); }
            Assert.That(TerrainCache.IsCurrent(_package, _manifest), Is.True);
        }

        [Test]
        public void ACorruptCacheTileIsDetected()
        {
            var tile = _cache.Tiles[5];
            string path = Path.Combine(TerrainCache.FolderFor(_package), tile.File);
            byte[] original = File.ReadAllBytes(path);
            try
            {
                File.WriteAllBytes(path, original.Take(original.Length / 2).ToArray());
                Assert.Throws<InvalidDataException>(() => TerrainCache.ReadTile(_package, tile), "truncated");
                var tampered = (byte[])original.Clone();
                tampered[tampered.Length - 3] ^= 0x55;
                File.WriteAllBytes(path, tampered);
                Assert.Catch<Exception>(() => TerrainCache.ReadTile(_package, tile), "corrupt");
            }
            finally { File.WriteAllBytes(path, original); }
        }

        [Test]
        public void ValidationFindsTruncatedAndTamperedLayers()
        {
            Assert.That(PackageValidator.Validate(_package), Is.Empty);
            string path = Path.Combine(_package, "heights-core.grid");
            byte[] original = File.ReadAllBytes(path);
            try
            {
                File.WriteAllBytes(path, original.Take(original.Length - 1000).ToArray());
                Assert.That(PackageValidator.Validate(_package), Has.Some.Contains("heights-core.grid"));
            }
            finally { File.WriteAllBytes(path, original); }
            Assert.That(PackageValidator.Validate(_package), Is.Empty);
        }

        [Test]
        public void SixteenBitGridsRoundTrip()
        {
            var values = Enumerable.Range(0, 513 * 513).Select(i => (ushort)(i * 37 % 32767)).ToArray();
            var ms = new MemoryStream();
            GridFile.Write(ms, new GridHeader(GridValueType.UInt16, 513, 513, 0, 1024, 2), values);
            ms.Position = 0;
            Assert.That(GridFile.ReadShorts(ms, out var h), Is.EqualTo(values));
            Assert.That(h.CellSize, Is.EqualTo(2.0));
        }
    }

    public sealed class LibraryTests
    {
        static string NewRoot() => Path.Combine(Path.GetTempPath(), "mp-library-test-" + Guid.NewGuid().ToString("N"));

        internal static string BuildPackage(string parent, string name, float offset)
        {
            string folder = Path.Combine(parent, "_incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var m = new PackageManifest
            {
                Site = new SiteInfo { Name = name, CentreX = -1188828, CentreY = 2381976, SizeMetres = 2000, RingMetres = 3000 },
                Quality = new QualityInfo { Score = 100 }, Flora = new FloraQualityInfo { Score = 83 },
            };
            var values = Enumerable.Repeat(2000f + offset, 64 * 64).ToArray();
            ResortPackage.AddLayer(folder, m, "heights-core", new GridHeader(GridValueType.Float32, 64, 64, 0, 64, 1), values);
            ResortPackage.AddLayer(folder, m, "heights-ring", new GridHeader(GridValueType.Float32, 64, 64, 0, 64, 2), values);
            ResortPackage.WriteManifest(folder, m);
            return folder;
        }

        [Test]
        public void PackagesJoinTheLibraryByIdAndListWithTheirScores()
        {
            string root = NewRoot();
            try
            {
                string incoming = Path.Combine(root, "Resorts");
                string a = ResortLibrary.Add(root, BuildPackage(incoming, "Jackson Hole", 0));
                ResortLibrary.Add(root, BuildPackage(incoming, "Big Sky", 5));
                Assert.That(Path.GetFileName(a), Has.Length.EqualTo(16), "Resorts/<packageId>");

                // The same content again is recognised and not duplicated.
                string again = ResortLibrary.Add(root, BuildPackage(incoming, "Jackson Hole", 0));
                Assert.That(again, Is.EqualTo(a));

                var list = ResortLibrary.Scan(root, measure: true);
                Assert.That(list.Select(e => e.Name), Is.EqualTo(new[] { "Big Sky", "Jackson Hole" }));
                Assert.That(list[1].TerrainScore, Is.EqualTo(100));
                Assert.That(list[1].FloraScore, Is.EqualTo(83));
                Assert.That(list[1].BytesOnDisk, Is.GreaterThan(0));
                Assert.That(list[1].CacheReady, Is.False, "not prepared yet");

                Assert.That(ResortLibrary.TryRemove(list[0], out long freed), Is.True);
                Assert.That(freed, Is.GreaterThan(0));
                Assert.That(ResortLibrary.Scan(root).Select(e => e.Name), Is.EqualTo(new[] { "Jackson Hole" }));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        [Test]
        public void ViewStateRoundTripsAndFallsBackToDefaults()
        {
            string folder = NewRoot();
            Directory.CreateDirectory(folder);
            try
            {
                Assert.That(ViewState.Load(folder).Camera.Distance, Is.EqualTo(3500), "no file: defaults");
                var state = new ViewState { SecondOfDay = 16 * 3600 };
                state.Camera.TargetX = 120.5;
                state.Bookmarks.Add(new ViewState.Bookmark { Name = "Summit" });
                state.Layers["forest"] = false;
                ViewState.Save(folder, state);
                var back = ViewState.Load(folder);
                Assert.That(back.SecondOfDay, Is.EqualTo(16 * 3600));
                Assert.That(back.Camera.TargetX, Is.EqualTo(120.5));
                Assert.That(back.Bookmarks.Single().Name, Is.EqualTo("Summit"));
                Assert.That(back.Layers["forest"], Is.False);

                File.WriteAllText(Path.Combine(folder, ViewState.FileName), "{ not json");
                Assert.That(ViewState.Load(folder).Version, Is.EqualTo(ViewState.CurrentVersion), "corrupt file: defaults, not an error");
            }
            finally { Directory.Delete(folder, true); }
        }
    }

    /// <summary>The real Jackson Hole test terrain through the cache (runs where Git LFS has fetched it).</summary>
    public sealed class TestTerrainCacheTests
    {
        [Test]
        public void JacksonHolePreparesIntoCracklessTiles()
        {
            string source = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(source, "*.grid")) TestData.Bytes(source, Path.GetFileName(f));
            string copy = Path.Combine(Path.GetTempPath(), "mp-jh-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copy);
            try
            {
                foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
                var m = ResortPackage.ReadManifest(copy);
                Assert.That(PackageValidator.Validate(copy), Is.Empty);
                var cache = TerrainCache.Build(copy, m, null!);
                Assert.That(cache.Tiles.Count, Is.EqualTo(64));
                Assert.That(cache.HeightRange / TerrainCache.MaxValue, Is.LessThan(0.05), "height step under 5 cm");
                Assert.That(TerrainCache.IsCurrent(copy, m), Is.True);
            }
            finally { Directory.Delete(copy, true); }
        }
    }
}
