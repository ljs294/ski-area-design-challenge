using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 07 acceptance (docs/plans/phase0-0.7-phase1-plan.md): the forest rule scores at least 80%
    /// against the Jackson Hole lidar truth set (D4); the forest-fraction check works; cover is
    /// deterministic, soft and seam-free; OpenStreetMap shapes rasterize with anti-aliasing.
    /// </summary>
    public sealed class ForestTruthTests
    {
        /// <summary>The lidar truth fixture: 200 × 200 cells of 10 m, band 0 = % trees, band 1 = tallest tree (0.25 m).</summary>
        static (byte[] Share, byte[] Tallest) Truth(string dir)
        {
            byte[] bytes = TestData.Bytes(dir, "forest-truth.bin");
            int n = bytes.Length / 2;
            return (bytes.Take(n).ToArray(), bytes.Skip(n).ToArray());
        }

        static (byte[] Canopy, GridHeader Header) Canopy(string dir)
        {
            TestData.Bytes(dir, "canopy-core.grid");
            var manifest = ResortPackage.ReadManifest(dir);
            byte[] canopy = ResortPackage.ReadByteLayer(dir, manifest, "canopy-core", out var header);
            return (canopy, header);
        }

        [Test]
        public void TheForestRuleScoresAtLeastEightyPercentAgainstLidar()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            var (share, _) = Truth(dir);
            var (canopy, h) = Canopy(dir);
            bool[] forest = ForestRule.ForestCells(canopy, h.Width, h.Height);
            var score = ForestRule.Score(forest, share);
            TestContext.Progress.WriteLine("Forest rule vs lidar: " + score);
            Assert.That(score.Cells, Is.GreaterThan(35000), "the truth set covers the core");
            Assert.That(score.TruthShare, Is.EqualTo(0.51).Within(0.02), "the fixture matches the data-spike report (51% forest)");
            Assert.That(score.Accuracy, Is.GreaterThanOrEqualTo(0.80), "D4: the canopy rule scores ≥80%");
            Assert.That(score.Precision, Is.GreaterThanOrEqualTo(0.85), "when it says forest, it's almost always right");
        }

        [Test]
        public void WorldCoverAloneScoresLowerThanTheRule()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            var (share, _) = Truth(dir);
            TestData.Bytes(dir, "cover.grid");
            var manifest = ResortPackage.ReadManifest(dir);
            byte[] cover = ResortPackage.ReadByteLayer(dir, manifest, "cover", out var ch);
            var (canopy, h) = Canopy(dir);
            int n = h.Width / 10, offC = (int)Math.Round((h.West - ch.West) / ch.CellSize), offR = (int)Math.Round((ch.North - h.North) / ch.CellSize);
            var wc = new bool[n * n];
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                    wc[r * n + c] = cover[(r + offR) * ch.Width + c + offC] == 10;
            var wcScore = ForestRule.Score(wc, share);
            var ruleScore = ForestRule.Score(ForestRule.ForestCells(canopy, h.Width, h.Height), share);
            TestContext.Progress.WriteLine("WorldCover vs lidar: " + wcScore);
            Assert.That(ruleScore.Accuracy, Is.GreaterThan(wcScore.Accuracy), "D4: the canopy map is the more accurate forest layer");
        }

        [Test]
        public void CalibratedHeightsMatchLidarBetterThanRawHeights()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            var (share, tallest) = Truth(dir);
            var (canopy, h) = Canopy(dir);
            int n = h.Width / 10;
            var raw = new List<double>();
            var truth = new List<double>();
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int i = r * n + c;
                    if (share[i] == ForestRule.NoTruth || tallest[i] < ForestRule.TreeCode) continue;
                    byte max = 0;
                    for (int y = 0; y < 10; y++)
                        for (int x = 0; x < 10; x++)
                            max = Math.Max(max, canopy[(r * 10 + y) * h.Width + c * 10 + x]);
                    if (max < ForestRule.TreeCode) continue;
                    raw.Add(max * ForestRule.CanopyStep);
                    truth.Add(tallest[i] * ForestRule.CanopyStep);
                }
            double Median(List<double> v) => v.OrderBy(x => x).ElementAt(v.Count / 2);
            double rawMedian = Median(raw), truthMedian = Median(truth);
            double calibrated = rawMedian * ForestCalibration.Default.DominantHeightFactor;
            TestContext.Progress.WriteLine($"Tallest tree per forest cell: canopy map {rawMedian:F1} m, calibrated {calibrated:F1} m, lidar {truthMedian:F1} m over {raw.Count} cells");
            Assert.That(Math.Abs(calibrated - truthMedian), Is.LessThan(Math.Abs(rawMedian - truthMedian)), "calibration moves heights toward the lidar");
            Assert.That(calibrated / truthMedian, Is.InRange(0.9, 1.1), "calibrated dominant height within 10% of lidar");
        }

        [Test]
        public void CalibratedDensityMatchesLidar()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            var (share, _) = Truth(dir);
            var (canopy, h) = Canopy(dir);
            int n = h.Width / 10;
            double truthSum = 0, mapSum = 0;
            for (int r = 0; r < n; r++)
                for (int c = 0; c < n; c++)
                {
                    int i = r * n + c;
                    if (share[i] == ForestRule.NoTruth) continue;
                    int trees = 0;
                    for (int y = 0; y < 10; y++)
                        for (int x = 0; x < 10; x++)
                            if (ForestRule.IsTree(canopy[(r * 10 + y) * h.Width + c * 10 + x])) trees++;
                    truthSum += share[i] / 100.0;
                    mapSum += trees / 100.0;
                }
            double ratio = truthSum / mapSum;
            TestContext.Progress.WriteLine($"Tree cover: canopy map {mapSum / (n * n):P1}, lidar {truthSum / (n * n):P1}, ratio {ratio:F2}; factor {ForestCalibration.Default.DensityFactor}");
            Assert.That(ForestCalibration.Default.DensityFactor / ratio, Is.InRange(0.9, 1.1), "the density factor matches the lidar within 10%");
        }

        [Test]
        public void TheForestFractionCheckCatchesACollapsedCanopy()
        {
            Assert.That(ForestRule.CheckFraction(0.40, 0.68), Is.Null, "Jackson Hole's real ratio passes");
            Assert.That(ForestRule.CheckFraction(0.075, 0.65), Is.Not.Null, "the archive's lidar-forest failure (7.5% vs 65%) is caught");
            Assert.That(ForestRule.CheckFraction(0.02, 0.03), Is.Null, "open alpine sites with little forest pass");
        }
    }

    /// <summary>
    /// The real Jackson Hole 2 km package through the whole cover pipeline. Golden hashes pin the
    /// result: they change only with an approved behaviour change (AGENTS.md determinism rules).
    /// </summary>
    public sealed class GroundCoverGoldenTests
    {
        /// <summary>
        /// Cover hashes of three tiles: a core tile at the summit, a core edge tile and a ring tile. Re-pinned for task 12d
        /// (owner approved, 2026-10-02): eight bands with paved and unpaved roads from the package's roads.
        /// </summary>
        static readonly Dictionary<string, string> Golden = new Dictionary<string, string>
        {
            ["t4_3.cover"] = "f0726fa3063d0ab8a4d151cc27837091f2ab70723e45d1c42659cefe398fa26d",
            ["t4_4.cover"] = "239f375d7e5f4cddbd46b1523d09d8fa683502b92e7c12249bad1f5ced498154",
            ["t0_0.cover"] = "db1b47aadf908feed4adc8b6aa7c6b5c2f5f457b72166143d63595eeaf00528e",
        };

        static string BuildCopy(string dir, out CacheManifest cache)
        {
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            string copy = Path.Combine(Path.GetTempPath(), "mp-cover-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(copy);
            foreach (string f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(copy, Path.GetFileName(f)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            cache = TerrainCache.Build(copy, ResortPackage.ReadManifest(copy), null!);
            TestContext.Progress.WriteLine($"Cache with cover built in {watch.Elapsed.TotalSeconds:F1} s");
            return copy;
        }

        [Test]
        public void JacksonHoleCoverIsDeterministicAndMatchesItsGoldenHashes()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            string a = BuildCopy(dir, out var first), b = BuildCopy(dir, out var second);
            try
            {
                Assert.That(second.Tiles.Select(t => t.CoverSha256), Is.EqualTo(first.Tiles.Select(t => t.CoverSha256)), "identical across runs");
                foreach (var t in first.Tiles.Where(t => t.Column == 4 && (t.Row == 4 || t.Row == 3)).Concat(first.Tiles.Where(t => t.Column == 0 && t.Row == 0)))
                    TestContext.Progress.WriteLine($"[\"{t.CoverFile}\"] = \"{t.CoverSha256}\",");
                foreach (var kv in Golden)
                    Assert.That(first.Tiles.Single(t => t.CoverFile == kv.Key).CoverSha256, Is.EqualTo(kv.Value), kv.Key + " golden hash");
                Assert.That(Golden.Count, Is.EqualTo(3), "golden hashes recorded");

                // Shares of each layer over the core tiles, for the PR and as a sanity check.
                var totals = new double[TerrainCache.CoverBands];
                long texels = 0;
                foreach (var t in first.Tiles.Where(t => t.Core))
                {
                    byte[] cover = TerrainCache.ReadCover(a, t);
                    for (int o = 0; o < cover.Length; o += TerrainCache.CoverBands, texels++)
                        for (int k = 0; k < TerrainCache.CoverBands; k++) totals[k] += cover[o + k] / 255.0;
                }
                string[] names = { "forest floor", "grass", "rock", "developed", "water", "paved road", "unpaved road", "snow" };
                TestContext.Progress.WriteLine(string.Join(", ", names.Select((n, k) => $"{n} {totals[k] / texels:P2}")));
                Assert.That(totals[0] / texels, Is.InRange(0.25, 0.6), "forest floor near the canopy's forest share");
                Assert.That(totals[GroundCover.Layers] / texels, Is.GreaterThan(0.9), "mostly snow-covered");
                Assert.That(totals[(int)GroundLayer.PavedRoad] / texels, Is.GreaterThan(0.001), "the package's paved roads are drawn (task 12d)");
                Assert.That(totals[(int)GroundLayer.UnpavedRoad] / texels, Is.GreaterThan(0.0001), "and its tracks");
            }
            finally
            {
                Directory.Delete(a, true);
                Directory.Delete(b, true);
            }
        }
    }

    public sealed class GroundCoverRuleTests
    {
        static (double[] W, double Snow) Classify(CoverSample s)
        {
            var w = new double[GroundCover.Layers];
            GroundCover.Classify(s, w, out double snow);
            return (w, snow);
        }

        [Test]
        public void WeightsAlwaysSumToOne()
        {
            var rng = new List<CoverSample>();
            for (int i = 0; i < 2000; i++)
            {
                double H(int k) => (CoverNoise.Lattice(42, i, k) + 1) / 2;
                rng.Add(new CoverSample
                {
                    Canopy = H(0), CanopyWeight = H(1), WcTrees = H(2) * 0.5, WcGrass = H(3) * 0.3, WcRock = 0.1, WcDeveloped = H(4) * 0.05, WcWater = H(5) * 0.05,
                    OsmWater = H(6) > 0.9 ? H(7) : 0, OsmDeveloped = H(8) > 0.9 ? H(9) : 0, SlopeDegrees = H(10) * 70, Noise = CoverNoise.Lattice(7, i, 0),
                });
            }
            foreach (var s in rng)
            {
                var (w, snow) = Classify(s);
                Assert.That(w.Sum(), Is.EqualTo(1).Within(1e-9));
                Assert.That(w.All(v => v >= 0), Is.True);
                Assert.That(snow, Is.InRange(0, 1));
                var q = new byte[GroundCover.Layers];
                GroundCover.Quantize(w, q);
                Assert.That(q.Sum(b => b), Is.EqualTo(255));
            }
        }

        [Test]
        public void OpenCanopyInTheCoreIsMeadowEvenWhereWorldCoverSaysTrees()
        {
            // A ski run: WorldCover's 10 m cell says trees, the 1 m canopy says open.
            var (w, _) = Classify(new CoverSample { Canopy = 0, CanopyWeight = 1, WcTrees = 1, SlopeDegrees = 20 });
            Assert.That(w[(int)GroundLayer.Grass], Is.GreaterThan(0.99));
            var (ring, _) = Classify(new CoverSample { Canopy = 0, CanopyWeight = 0, WcTrees = 1, SlopeDegrees = 20 });
            Assert.That(ring[(int)GroundLayer.ForestFloor], Is.GreaterThan(0.99), "outside the canopy map, WorldCover decides");
        }

        [Test]
        public void SteepFacesAreRockAndCliffsShedTheirSnow()
        {
            var (gentle, gentleSnow) = Classify(new CoverSample { WcGrass = 1, SlopeDegrees = 25 });
            var (steep, steepSnow) = Classify(new CoverSample { WcGrass = 1, SlopeDegrees = 48 });
            var (cliff, cliffSnow) = Classify(new CoverSample { WcGrass = 1, SlopeDegrees = 65 });
            Assert.That(gentle[(int)GroundLayer.Grass], Is.EqualTo(1).Within(1e-9));
            Assert.That(steep[(int)GroundLayer.Rock], Is.GreaterThan(0.7));
            Assert.That(cliff[(int)GroundLayer.Rock], Is.EqualTo(1).Within(1e-9));
            Assert.That(gentleSnow, Is.EqualTo(1));
            Assert.That(steepSnow, Is.EqualTo(1), "A2: snow holds below about 52°");
            Assert.That(cliffSnow, Is.EqualTo(0), "A2: bare rock on faces over about 55-60°");
        }

        [Test]
        public void MappedWaterAndRoadsWin()
        {
            var (lake, snow) = Classify(new CoverSample { OsmWater = 1, WcTrees = 1, SlopeDegrees = 0 });
            Assert.That(lake[(int)GroundLayer.Water], Is.EqualTo(1).Within(1e-9));
            Assert.That(snow, Is.EqualTo(1), "iteration 1: frozen, snow-covered lakes");
            var (road, _) = Classify(new CoverSample { OsmDeveloped = 0.5, Canopy = 1, CanopyWeight = 1, SlopeDegrees = 5 });
            Assert.That(road[(int)GroundLayer.Developed], Is.EqualTo(0.5).Within(1e-9), "an anti-aliased road edge blends half and half");
        }

        [Test]
        public void NoiseIsKeyedSmoothAndBounded()
        {
            ulong seed = CoverNoise.SeedFor(-1188828, 2381976);
            Assert.That(CoverNoise.SeedFor(-1188828, 2381976), Is.EqualTo(seed));
            Assert.That(CoverNoise.SeedFor(-1188826, 2381976), Is.Not.EqualTo(seed));
            double previous = CoverNoise.At(seed, 0, 0, 40);
            for (int i = 1; i < 4000; i++)
            {
                double v = CoverNoise.At(seed, i * 0.25, 7.3, 40);
                Assert.That(v, Is.InRange(-1, 1));
                Assert.That(Math.Abs(v - previous), Is.LessThan(0.1), "smooth at 0.25 m steps");
                previous = v;
            }
        }
    }

    public sealed class VectorRasterTests
    {
        static readonly GridSpec Grid = new GridSpec(0, 100, 1, 100, 100);

        static List<AlbersPoint> Square(double w, double s, double e, double n) =>
            new List<AlbersPoint> { new AlbersPoint(w, s), new AlbersPoint(e, s), new AlbersPoint(e, n), new AlbersPoint(w, n), new AlbersPoint(w, s) };

        [Test]
        public void PolygonsAreAntiAliasedAndConserveArea()
        {
            var target = new byte[Grid.CellCount];
            VectorRaster.FillPolygon(target, Grid, new[] { Square(10.5, 20.25, 30.5, 40.25) });
            Assert.That(target[Grid.Index(20, 70)], Is.EqualTo(255), "inside");
            Assert.That(target[Grid.Index(10, 70)], Is.EqualTo(128), "half-covered west edge");
            Assert.That(target[Grid.Index(9, 70)], Is.EqualTo(0), "outside");
            Assert.That(target.Sum(b => b / 255.0), Is.EqualTo(20 * 20).Within(1.0), "coverage adds up to the area");
        }

        [Test]
        public void HolesAreCutWithEvenOdd()
        {
            var target = new byte[Grid.CellCount];
            VectorRaster.FillPolygon(target, Grid, new[] { Square(10, 10, 90, 90), Square(40, 40, 60, 60) });
            Assert.That(target[Grid.Index(50, 50)], Is.EqualTo(0));
            Assert.That(target[Grid.Index(20, 50)], Is.EqualTo(255));
            Assert.That(target.Sum(b => b / 255.0), Is.EqualTo(80 * 80 - 20 * 20).Within(1.0));
        }

        [Test]
        public void LinesHaveTheirWidthAndSoftEdges()
        {
            var target = new byte[Grid.CellCount];
            VectorRaster.StrokeLine(target, Grid, new[] { new AlbersPoint(10, 50), new AlbersPoint(90, 50) }, 5);
            // The band spans y 47.5-52.5. Row r covers y (99 - r, 100 - r], centre 99.5 - r.
            Assert.That(target[Grid.Index(50, 49)], Is.EqualTo(255), "centre 50.5: inside");
            Assert.That(target[Grid.Index(50, 47)], Is.EqualTo(128), "centre 52.5: on the edge, half covered");
            Assert.That(target[Grid.Index(50, 46)], Is.EqualTo(0), "centre 53.5: outside");
            double across = Enumerable.Range(0, 100).Sum(r => target[Grid.Index(50, r)] / 255.0);
            Assert.That(across, Is.EqualTo(5).Within(0.05), "the band is 5 m wide");
        }
    }

    public sealed class OsmParseTests
    {
        const string Response = @"{""elements"":[
 {""type"":""way"",""id"":1,""tags"":{""natural"":""water""},""geometry"":[{""lat"":43.60,""lon"":-110.85},{""lat"":43.60,""lon"":-110.84},{""lat"":43.61,""lon"":-110.84},{""lat"":43.60,""lon"":-110.85}]},
 {""type"":""way"",""id"":2,""tags"":{""highway"":""secondary""},""geometry"":[{""lat"":43.58,""lon"":-110.85},{""lat"":43.59,""lon"":-110.84}]},
 {""type"":""way"",""id"":3,""tags"":{""highway"":""footway""},""geometry"":[{""lat"":43.58,""lon"":-110.85},{""lat"":43.59,""lon"":-110.84}]},
 {""type"":""way"",""id"":4,""tags"":{""waterway"":""stream"",""tunnel"":""culvert""},""geometry"":[{""lat"":43.58,""lon"":-110.85},{""lat"":43.59,""lon"":-110.84}]},
 {""type"":""relation"",""id"":5,""tags"":{""type"":""multipolygon"",""natural"":""water""},""members"":[
   {""type"":""way"",""role"":""outer"",""geometry"":[{""lat"":43.50,""lon"":-110.80},{""lat"":43.50,""lon"":-110.79}]},
   {""type"":""way"",""role"":""outer"",""geometry"":[{""lat"":43.50,""lon"":-110.79},{""lat"":43.51,""lon"":-110.79},{""lat"":43.50,""lon"":-110.80}]}]}
]}";

        [Test]
        public void ShapesAreClassifiedAndMultipolygonPiecesJoined()
        {
            var shapes = OsmFeatures.Parse(Encoding.UTF8.GetBytes(Response));
            Assert.That(shapes.Count, Is.EqualTo(3), "lake, road, relation lake; footway and culvert skipped");
            Assert.That(shapes[0].Kind, Is.EqualTo(OsmKind.Water));
            Assert.That(shapes[0].IsArea, Is.True);
            Assert.That(shapes[1].Kind, Is.EqualTo(OsmKind.Developed));
            Assert.That(shapes[1].WidthMetres, Is.EqualTo(9));
            Assert.That(shapes[2].Parts.Count, Is.EqualTo(1), "two open outer pieces joined into one ring");
            var ring = shapes[2].Parts[0];
            Assert.That(ring[0], Is.EqualTo(ring[ring.Count - 1]), "closed");
        }
    }
}
