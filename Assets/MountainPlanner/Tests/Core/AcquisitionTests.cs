using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Tiff;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Finds repo-level test data (TestData/) from Unity's or dotnet's working folder.</summary>
    static class TestData
    {
        public static string Folder(string name)
        {
            foreach (string start in new[] { TestContext.CurrentContext.TestDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "TestData", name);
                    if (Directory.Exists(candidate)) return candidate;
                }
            Assert.Ignore($"TestData/{name} is not available here.");
            return "";
        }

        /// <summary>Skips a test when Git LFS hasn't fetched the file (CI fetches only small fixtures).</summary>
        public static byte[] Bytes(string folder, string file)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(folder, file));
            if (data.Length < 200 && System.Text.Encoding.ASCII.GetString(data).StartsWith("version https://git-lfs", StringComparison.Ordinal))
                Assert.Ignore($"{file} is a Git LFS pointer; run `git lfs pull` to fetch it.");
            return data;
        }
    }

    public sealed class TerrainQualityTests
    {
        // T18: S1M 1 m = 100, 1 m project lidar = 95, ~3 m = 60, ~10 m = 30, area-weighted over the core.
        [Test]
        public void AllS1mScoresOneHundred()
        {
            var cells = new Dictionary<TerrainSource, long> { [TerrainSource.S1m] = 4_000_000 };
            Assert.That(TerrainQuality.Score(cells), Is.EqualTo(100));
            Assert.That(TerrainQuality.OneLiner(cells), Is.EqualTo("Terrain quality 100/100: 100% USGS S1M 1 m lidar"));
        }

        [Test]
        public void MixedSourcesAreAreaWeighted()
        {
            // The Crystal Mountain spike mix: 16% 1 m lidar, 24% 3 m, 60% 10 m -> 0.16*95 + 0.24*60 + 0.6*30 = 47.6.
            var cells = new Dictionary<TerrainSource, long>
            {
                [TerrainSource.Lidar1m] = 16, [TerrainSource.ThreeMetre] = 24, [TerrainSource.TenMetre] = 60,
            };
            Assert.That(TerrainQuality.Score(cells), Is.EqualTo(48));
            Assert.That(TerrainQuality.OneLiner(cells),
                Is.EqualTo("Terrain quality 48/100: 60% 3DEP 10 m, 24% 3DEP 3 m, 16% 3DEP 1 m lidar"));
        }

        [Test]
        public void TinySharesShowAsLessThanOnePercent()
        {
            var cells = new Dictionary<TerrainSource, long> { [TerrainSource.S1m] = 996, [TerrainSource.TenMetre] = 4 };
            Assert.That(TerrainQuality.OneLiner(cells), Is.EqualTo("Terrain quality 100/100: 100% USGS S1M 1 m lidar, <1% 3DEP 10 m"));
        }

        [TestCase(1.0, TerrainSource.Lidar1m)]
        [TestCase(3.0, TerrainSource.ThreeMetre)]
        [TestCase(10.0, TerrainSource.TenMetre)]
        public void FallbackSourcesClassifyByCellSize(double metres, TerrainSource expected)
        {
            Assert.That(TerrainQuality.FromCellSize(metres), Is.EqualTo(expected));
        }
    }

    public sealed class GridFileTests
    {
        static float[] Terrain(int w, int h)
        {
            var v = new float[w * h];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                    v[r * w + c] = 2000 + 40 * (float)Math.Sin(c * 0.013) + 30 * (float)Math.Cos(r * 0.017) + (c * 7 + r * 13) % 5 * 0.013f;
            return v;
        }

        [Test]
        public void FloatGridsRoundTripExactly()
        {
            var header = new GridHeader(GridValueType.Float32, 600, 530, -1189828, 2382976, 1);
            float[] values = Terrain(600, 530);
            values[1234] = float.NaN;
            var ms = new MemoryStream();
            GridFile.Write(ms, header, values);
            ms.Position = 0;
            float[] back = GridFile.ReadFloats(ms, out var h2);
            Assert.That(h2.Width, Is.EqualTo(600));
            Assert.That(h2.North, Is.EqualTo(2382976));
            Assert.That(back.Length, Is.EqualTo(values.Length));
            for (int i = 0; i < values.Length; i++)
                Assert.That(BitConverter.SingleToInt32Bits(back[i]), Is.EqualTo(BitConverter.SingleToInt32Bits(values[i])), $"cell {i}");
        }

        [Test]
        public void TerrainCompressesToWellUnderHalf()
        {
            var header = new GridHeader(GridValueType.Float32, 1000, 1000, 0, 1000, 1);
            var ms = new MemoryStream();
            GridFile.Write(ms, header, Terrain(1000, 1000));
            Assert.That(ms.Length, Is.LessThan(4_000_000 / 2), "lossless, and at least 2:1 on smooth terrain (P5)");
        }

        [Test]
        public void ByteGridsRoundTrip()
        {
            var header = new GridHeader(GridValueType.UInt8, 300, 257, 0, 1000, 10);
            var values = new byte[300 * 257];
            for (int i = 0; i < values.Length; i++) values[i] = (byte)(i * 31 % 251);
            var ms = new MemoryStream();
            GridFile.Write(ms, header, values);
            ms.Position = 0;
            Assert.That(GridFile.ReadBytes(ms, out _), Is.EqualTo(values));
        }

        [Test]
        public void TruncatedAndForeignFilesAreRejected()
        {
            var ms = new MemoryStream();
            GridFile.Write(ms, new GridHeader(GridValueType.Float32, 300, 300, 0, 300, 1), Terrain(300, 300));
            var cut = new MemoryStream(ms.ToArray().Take((int)ms.Length - 100).ToArray());
            Assert.Throws<InvalidDataException>(() => GridFile.ReadFloats(cut, out _));
            Assert.Throws<InvalidDataException>(() => GridFile.ReadFloats(new MemoryStream(new byte[64]), out _));
        }
    }

    public sealed class ResortPackageTests
    {
        static string NewFolder()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mp-package-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        static PackageManifest Build(string folder, float bump, string created)
        {
            var m = new PackageManifest
            {
                Site = new SiteInfo { Name = "Test", CentreX = -1188828, CentreY = 2381976, SizeMetres = 2000, RingMetres = 3000 },
                CreatedUtc = created,
            };
            var values = Enumerable.Range(0, 100 * 100).Select(i => 2000f + i * 0.01f + bump).ToArray();
            ResortPackage.AddLayer(folder, m, "heights-core", new GridHeader(GridValueType.Float32, 100, 100, 0, 100, 1), values);
            ResortPackage.WriteManifest(folder, m);
            return m;
        }

        [Test]
        public void TheSameDataGivesTheSamePackageIdWhateverTheTime()
        {
            string a = NewFolder(), b = NewFolder();
            try
            {
                string idA = Build(a, 0, "2026-09-27T10:00:00Z").PackageId;
                string idB = Build(b, 0, "2031-01-01T00:00:00Z").PackageId;
                Assert.That(idA, Is.EqualTo(idB), "re-runs are idempotent (task 04 acceptance)");
                Assert.That(idA, Has.Length.EqualTo(16));
            }
            finally { Directory.Delete(a, true); Directory.Delete(b, true); }
        }

        [Test]
        public void ChangedHeightsChangeThePackageId()
        {
            string a = NewFolder(), b = NewFolder();
            try { Assert.That(Build(a, 0, "x").PackageId, Is.Not.EqualTo(Build(b, 0.01f, "x").PackageId)); }
            finally { Directory.Delete(a, true); Directory.Delete(b, true); }
        }

        [Test]
        public void ManifestAndLayersReadBackAndVerify()
        {
            string dir = NewFolder();
            try
            {
                var written = Build(dir, 0, "2026-09-27T10:00:00Z");
                var read = ResortPackage.ReadManifest(dir);
                Assert.That(read.PackageId, Is.EqualTo(written.PackageId));
                Assert.That(read.Site.SizeMetres, Is.EqualTo(2000));
                float[] heights = ResortPackage.ReadLayer(dir, read, "heights-core", out var header);
                Assert.That(header.Width, Is.EqualTo(100));
                Assert.That(heights[0], Is.EqualTo(2000f));

                // A tampered layer file is detected.
                read.Layers[0].Sha256 = new string('0', 64);
                Assert.Throws<InvalidDataException>(() => ResortPackage.ReadLayer(dir, read, "heights-core", out _));
            }
            finally { Directory.Delete(dir, true); }
        }
    }

    public sealed class BlendTests
    {
        [Test]
        public void SeamsHaveNoStepAboveTwentyCentimetres()
        {
            // Task 04 acceptance: a 1 m grid whose east third has no S1M; the fallback sits 2 m higher
            // (a typical datum or resolution mismatch). After blending, no neighbouring cells differ by
            // more than the terrain's own slope plus 0.2 m.
            const int w = 400, h = 200;
            var heights = new float[w * h];
            var fallback = new float[w * h];
            var fromS1m = new bool[w * h];
            for (int r = 0; r < h; r++)
                for (int c = 0; c < w; c++)
                {
                    int i = r * w + c;
                    float ground = 2000 + c * 0.1f + r * 0.05f;
                    fallback[i] = ground + 2.0f;
                    fromS1m[i] = c < 270;
                    heights[i] = fromS1m[i] ? ground : float.NaN;
                }
            float[] distance = HeightAssembler.DistanceToGaps(fromS1m, w, h, 1, HeightAssembler.BlendMetres);
            HeightAssembler.Blend(heights, fromS1m, fallback, distance);

            double worst = 0;
            for (int r = 0; r < h; r++)
                for (int c = 1; c < w; c++)
                    worst = Math.Max(worst, Math.Abs(heights[r * w + c] - heights[r * w + c - 1]) - 0.1);
            Assert.That(worst, Is.LessThanOrEqualTo(0.2));
            Assert.That(heights[100 * w + 100], Is.EqualTo(2000 + 100 * 0.1f + 100 * 0.05f).Within(1e-3), "S1M away from the seam is untouched");
            Assert.That(heights[100 * w + 300], Is.EqualTo(fallback[100 * w + 300]), "gaps take the fallback");
        }

        [Test]
        public void GapDistancesAreCloseToEuclidean()
        {
            const int n = 61;
            var filled = Enumerable.Repeat(true, n * n).ToArray();
            filled[30 * n + 30] = false;
            float[] d = HeightAssembler.DistanceToGaps(filled, n, n, 2, 1000);
            for (int r = 0; r < n; r += 5)
                for (int c = 0; c < n; c += 5)
                {
                    double truth = 2 * Math.Sqrt((r - 30) * (r - 30) + (c - 30) * (c - 30));
                    Assert.That(d[r * n + c], Is.EqualTo(truth).Within(truth * 0.09 + 1e-6), $"({c}, {r})");
                }
        }

        [Test]
        public void HolesNoSourceCoversAreFilledFromNeighbours()
        {
            var v = new[] { 1f, 2f, 3f, float.NaN, float.NaN, 6f, 7f, 8f, 9f };
            Assert.That(HeightAssembler.FillRemaining(v, 3, 3), Is.EqualTo(2));
            Assert.That(v.Any(float.IsNaN), Is.False);
        }
    }

    public sealed class ResumeCacheTests
    {
        sealed class FlakySource : IByteSource
        {
            public int Reads;
            public int FailAfter = int.MaxValue;
            public string Name => "https://example.test/tile.tif";

            public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
            {
                if (++Reads > FailAfter) throw new IOException("connection lost");
                return Task.FromResult(Enumerable.Range(0, length).Select(i => (byte)((offset + i) % 251)).ToArray());
            }
        }

        [Test]
        public async Task AnInterruptedDownloadResumesWithoutRefetching()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mp-cache-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                var inner = new FlakySource { FailAfter = 3 };
                var cached = new DiskCache(dir).Wrap(inner, new TransferMeter());
                for (int i = 0; i < 3; i++) await cached.ReadAsync(i * 1000, 1000, CancellationToken.None);
                Assert.ThrowsAsync<IOException>(() => cached.ReadAsync(3000, 1000, CancellationToken.None), "the 'kill'");

                // A new run: the first three ranges come from disk; only the rest are fetched.
                var fresh = new FlakySource();
                var meter = new TransferMeter();
                var resumed = new DiskCache(dir).Wrap(fresh, meter);
                for (int i = 0; i < 5; i++)
                {
                    byte[] data = await resumed.ReadAsync(i * 1000, 1000, CancellationToken.None);
                    Assert.That(data[0], Is.EqualTo((byte)(i * 1000 % 251)));
                }
                Assert.That(fresh.Reads, Is.EqualTo(2));
                Assert.That(meter.CachedBytes, Is.EqualTo(3000));
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }
    }

    public sealed class ProgressTests
    {
        sealed class Sink : IProgress<AcquisitionProgress>
        {
            public readonly List<AcquisitionProgress> Seen = new List<AcquisitionProgress>();
            public void Report(AcquisitionProgress value) { lock (Seen) Seen.Add(value); }
        }

        [Test]
        public void TheDetailLineNamesTheStepAndItsProgress()
        {
            var tracker = new ProgressTracker(null, new TransferMeter());
            tracker.DefineStage("Terrain", 100);
            tracker.DefineStage("Building", 100);
            tracker.BeginStage("Terrain");
            tracker.BeginStep("downloading sector", 5, 19, () => 0.35);
            var p = tracker.Snapshot();
            Assert.That(p.Detail, Is.EqualTo("Terrain: downloading sector 5 of 19 · 35%"), "U6");
            Assert.That(p.StageIndex, Is.EqualTo(1));
            Assert.That(p.StageCount, Is.EqualTo(2));
            // Overall: 4.35 of 19 sectors of the first of two equal stages.
            Assert.That(p.Overall, Is.EqualTo(4.35 / 19 / 2).Within(1e-9));
        }

        [Test]
        public void OverallProgressFollowsStageWeights()
        {
            var tracker = new ProgressTracker(null, new TransferMeter());
            tracker.DefineStage("Terrain", 300);
            tracker.DefineStage("Building", 100);
            tracker.BeginStage("Terrain");
            tracker.BeginStage("Building");
            tracker.BeginStep("compressing", 1, 1, () => 0.5);
            Assert.That(tracker.Snapshot().Overall, Is.EqualTo((300 + 50) / 400.0).Within(1e-9));
            tracker.Finish();
            Assert.That(tracker.Snapshot(finished: true).Overall, Is.EqualTo(1));
        }

        [Test]
        public void ProgressIsPublishedAtLeastFourTimesASecond()
        {
            Assert.That(ProgressTracker.IntervalMilliseconds, Is.LessThanOrEqualTo(250));
            var sink = new Sink();
            using (var tracker = new ProgressTracker(sink, new TransferMeter()))
            {
                tracker.DefineStage("Terrain", 1);
                tracker.BeginStage("Terrain");
                Thread.Sleep(1100);
            }
            lock (sink.Seen) Assert.That(sink.Seen.Count, Is.GreaterThanOrEqualTo(4));
        }
    }

    /// <summary>The Cloud Optimized GeoTIFF reader on recorded bytes of S1M tile n2390w1190 (Jackson Hole).</summary>
    public sealed class S1mReaderTests
    {
        static TiffImage Open(out int col, out int row)
        {
            string dir = TestData.Folder("s1m-fixture");
            string json = File.ReadAllText(Path.Combine(dir, "s1m-fixture.json"));
            long Field(string name) => long.Parse(Regex.Match(json, "\"" + name + "\":\\s*(\\d+)").Groups[1].Value);
            var source = new SparseByteSource("s1m-fixture",
                (0, TestData.Bytes(dir, "s1m-header.bin")),
                (Field("l0Offset"), TestData.Bytes(dir, "s1m-l0-block.bin")),
                (Field("l1Offset"), TestData.Bytes(dir, "s1m-l1-block.bin")));
            col = (int)Field("l0Col");
            row = (int)Field("l0Row");
            return TiffImage.OpenAsync(source).GetAwaiter().GetResult();
        }

        [Test]
        public void TheDirectoryDescribesAnS1mTile()
        {
            var tiff = Open(out _, out _);
            var d = tiff.Directories[0];
            Assert.That((d.Width, d.Compression, d.Predictor, d.Epsg), Is.EqualTo((10000, 5, 3, (int?)6350)));
            Assert.That(d.TiePoint![3], Is.EqualTo(-1190000.0));
            Assert.That(tiff.Directories[1].Width, Is.EqualTo(5000), "2 m overview");
        }

        [Test]
        public async Task OneMetreBlocksAverageToTheTwoMetreOverview()
        {
            var tiff = Open(out int col, out int row);
            float[] full = await tiff.ReadWindowAsync(0, col, row, 512, 512);
            float[] half = await tiff.ReadWindowAsync(1, col / 2, row / 2, 256, 256);
            Assert.That(full.Any(float.IsNaN), Is.False);
            double sum = 0;
            for (int r = 0; r < 256; r++)
                for (int c = 0; c < 256; c++)
                    sum += Math.Abs((full[2 * r * 512 + 2 * c] + full[2 * r * 512 + 2 * c + 1] + full[(2 * r + 1) * 512 + 2 * c] + full[(2 * r + 1) * 512 + 2 * c + 1]) / 4.0 - half[r * 256 + c]);
            Assert.That(sum / (256 * 256), Is.LessThanOrEqualTo(0.05));
            Assert.That(tiff.WindowBytes(0, col, row, 512, 512), Is.EqualTo(549654), "exact bytes for progress (U6)");
        }
    }
}

namespace MountainPlanner.Tests
{
    /// <summary>The committed Jackson Hole 2 km test terrain (D2, P5). Runs where Git LFS has fetched it.</summary>
    public sealed class TestTerrainTests
    {
        [Test]
        public void TheTestTerrainOpensVerifiesAndLooksLikeJacksonHole()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in System.IO.Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, System.IO.Path.GetFileName(f));
            var m = ResortPackage.ReadManifest(dir);
            Assert.That(m.Quality.Score, Is.EqualTo(100));
            float[] core = ResortPackage.ReadLayer(dir, m, "heights-core", out var header);
            Assert.That((header.Width, header.Height, header.CellSize), Is.EqualTo((2000, 2000, 1.0)));
            Assert.That(core.Any(float.IsNaN), Is.False, "no holes");
            Assert.That(core.Min(), Is.GreaterThan(1800f));
            Assert.That(core.Max(), Is.LessThan(3300f));
            float[] ring = ResortPackage.ReadLayer(dir, m, "heights-ring", out var ringHeader);
            Assert.That(ringHeader.CellSize, Is.EqualTo(2.0));
            Assert.That(ring.Any(float.IsNaN), Is.False);
        }
    }
}
