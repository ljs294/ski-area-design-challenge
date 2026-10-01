using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Flora;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Forest placement (0.3 §4.5): trees stand only on canopy, keep their spacing across cells and tiles,
    /// species follow BIGMAP, the same package always grows the same forest, and the species map matches
    /// the tree library.
    /// </summary>
    public sealed class ForestTests
    {
        [Test]
        public void TheSpeciesMapMatchesTheTreeLibrary()
        {
            string json = null;
            foreach (string start in new[] { TestContext.CurrentContext.TestDirectory, Environment.CurrentDirectory })
                for (var dir = new DirectoryInfo(start); dir != null && json == null; dir = dir.Parent)
                {
                    string candidate = Path.Combine(dir.FullName, "tools", "assets", "trees", "species.json");
                    if (File.Exists(candidate)) json = File.ReadAllText(candidate);
                }
            if (json == null) Assert.Ignore("tools/assets/trees/species.json is not available here.");
            var ids = Regex.Matches(json, "\"id\"\\s*:\\s*\"([a-z_]+)\"").Cast<Match>().Select(m => m.Groups[1].Value).ToArray();
            Assert.That(SpeciesMap.Models, Is.EqualTo(ids), "model order is the prototype order in the game");
        }

        [Test]
        public void EverySpeciesCodeFindsAModel()
        {
            for (int spcd = 1; spcd < 1000; spcd++)
                Assert.That(SpeciesMap.ModelFor(spcd), Is.InRange(0, SpeciesMap.Models.Length - 1), $"FIA {spcd}");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(101)], Is.EqualTo("lodgepole_pine"), "whitebark pine looks like a pine");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(96)], Is.EqualTo("engelmann_spruce"), "blue spruce looks like a spruce");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(749)], Is.EqualTo("quaking_aspen"), "cottonwood looks like an aspen");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(17)], Is.EqualTo("pacific_silver_fir"), "grand fir looks like silver fir");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(21)], Is.EqualTo("noble_fir"), "Shasta red fir looks like noble fir");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(242)], Is.EqualTo("western_hemlock"), "western redcedar looks like western hemlock");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(261)], Is.EqualTo("western_hemlock"), "eastern hemlock looks like western hemlock");
            Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(12)], Is.EqualTo("subalpine_fir"), "balsam fir looks like subalpine fir");
            // Every model but a growth form (krummholz) stands for at least one real species.
            var modelled = SpeciesMap.ModelledCodes.Select(c => SpeciesMap.Models[SpeciesMap.ModelFor(c)]).ToHashSet();
            Assert.That(SpeciesMap.Models.Where(m => m != "krummholz"), Is.SubsetOf(modelled));
        }

        [Test]
        public void DensityFollowsCoverAndCrownSize()
        {
            var cal = ForestCalibration.Default;
            Assert.That(ForestPlacement.TreesPerCell(0, 20, cal), Is.EqualTo(0));
            Assert.That(ForestPlacement.TreesPerCell(0.5, 20, cal), Is.GreaterThan(ForestPlacement.TreesPerCell(0.25, 20, cal)));
            Assert.That(ForestPlacement.TreesPerCell(0.5, 10, cal), Is.GreaterThan(ForestPlacement.TreesPerCell(0.5, 30, cal)), "small trees pack closer");
            Assert.That(ForestPlacement.TreesPerCell(1, 3, cal), Is.LessThanOrEqualTo(ForestPlacement.MaxTreesPerCell));
            Assert.That(ForestPlacement.RingDensity(0), Is.EqualTo(1));
            Assert.That(ForestPlacement.RingDensity(5000), Is.EqualTo(ForestPlacement.RingMinDensity).Within(1e-9));
        }

        /// <summary>A cone 4 km across rising from 2,000 to 3,600 m, forested below <paramref name="forestTop"/>.</summary>
        static (float[] Elevation, bool[] Forest) Cone(double forestTop, int n = 400)
        {
            var elevation = new float[n * n];
            var forest = new bool[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    double r = Math.Sqrt((x - n / 2.0) * (x - n / 2.0) + (y - n / 2.0) * (y - n / 2.0)) * 10;
                    double e = 3600 - 1600 * Math.Min(1, r / 2000);
                    elevation[y * n + x] = (float)e;
                    forest[y * n + x] = e < forestTop;
                }
            return (elevation, forest);
        }

        [Test]
        public void KrummholzGrowsJustBelowTheTreeline()
        {
            var (elevation, forest) = Cone(3000);
            byte[] chance = Treeline.Krummholz(elevation, forest, 400, 400, 10, out double line);
            TestContext.Progress.WriteLine($"Treeline {line:F0} m");
            Assert.That(line, Is.InRange(2960, 3000), "the forest's upper edge");
            for (int i = 0; i < elevation.Length; i++)
            {
                if (!forest[i]) Assert.That(chance[i], Is.EqualTo(0), "only trees turn into krummholz");
                else if (elevation[i] < 3000 - Treeline.BandMetres - 50) Assert.That(chance[i], Is.EqualTo(0), "none far below the treeline");
                else if (elevation[i] > 3000 - 45) Assert.That(chance[i], Is.EqualTo(255), "all of them in the top 50 m");
            }
            // A mountain forested to its summit has no treeline, so no krummholz.
            var (e2, f2) = Cone(9999);
            Assert.That(Treeline.Krummholz(e2, f2, 400, 400, 10, out double none).All(c => c == 0), Is.True);
            Assert.That(double.IsNaN(none), Is.True);
        }

        [Test]
        public void CliffShellsJutOutOnCliffsAndTuckUnderElsewhere()
        {
            Assert.That(CliffShape.Weight(30), Is.EqualTo(0));
            Assert.That(CliffShape.Weight(70), Is.EqualTo(1));
            Assert.That(CliffShape.Displacement(1, 0, 0, 2500, 0), Is.EqualTo(-CliffShape.TuckMetres), "fades under the terrain");
            double min = double.MaxValue, max = double.MinValue;
            for (int k = 0; k < 20000; k++)
            {
                double d = CliffShape.Displacement(7, k * 0.37, k * 0.11, 2400 + k * 0.05, 1);
                min = Math.Min(min, d);
                max = Math.Max(max, d);
            }
            TestContext.Progress.WriteLine($"Cliff displacement {min:F2} to {max:F2} m");
            Assert.That(min, Is.GreaterThanOrEqualTo(CliffShape.LiftMetres), "a full-strength shell always stands off the lidar surface (no flicker)");
            Assert.That(max, Is.GreaterThan(3), "ledges and buttresses jut out metres from the face");
        }

        [Test]
        public void JacksonHoleCliffShellsAreSeamlessAndDeterministic()
        {
            var (manifest, dir) = JacksonHole2Km();
            float[] core = ResortPackage.ReadLayer(dir, manifest, "heights-core", out var coreHeader);
            float[] ring = ResortPackage.ReadLayer(dir, manifest, "heights-ring", out var ringHeader);
            var heights = new TerrainCache.HeightField(core, coreHeader, ring, ringHeader);
            var tiles = Grid(manifest);
            var field = new CliffField(manifest, heights);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var meshes = tiles.All().ToDictionary(k => (k.Column, k.Row), k => field.BuildTile(tiles.Bounds(k)));
            long triangles = meshes.Values.Sum(m => m.Indices.Length / 3L);
            TestContext.Progress.WriteLine($"Cliff shells: {triangles:N0} triangles over {tiles.Count} tiles in {watch.Elapsed.TotalSeconds:F1} s");
            Assert.That(triangles, Is.GreaterThan(10000), "Jackson Hole has real cliffs");

            // Vertices on a shared tile edge are computed at the same world points by both tiles, so they
            // must be bit-identical (no cracks in the shell): look for B's vertices, shifted into A's frame, in A.
            int matched = 0, pairs = 0;
            foreach (var ((c, r), a) in meshes)
            {
                if (!meshes.TryGetValue((c + 1, r), out var b) || a.VertexCount == 0 || b.VertexCount == 0) continue;
                var inA = new HashSet<(float, float, float)>();
                for (int v = 0; v < a.VertexCount; v++) inA.Add((a.Positions[v * 3], a.Positions[v * 3 + 1], a.Positions[v * 3 + 2]));
                int edge = 0, found = 0;
                for (int v = 0; v < b.VertexCount; v++)
                {
                    if (b.Positions[v * 3] > 6) continue;   // near B's west edge (displacement is at most a few metres)
                    edge++;
                    if (inA.Contains((b.Positions[v * 3] + 1024f, b.Positions[v * 3 + 1], b.Positions[v * 3 + 2]))) found++;
                }
                if (edge > 0) pairs++;
                matched += found;
            }
            TestContext.Progress.WriteLine($"{matched} shell vertices shared exactly across {pairs} tile edges");
            Assert.That(matched, Is.GreaterThan(0), "shells meet across tile edges with identical vertices");

            var withCliffs = tiles.All().First(k => meshes[(k.Column, k.Row)].VertexCount > 0);
            var again = new CliffField(manifest, heights).BuildTile(tiles.Bounds(withCliffs));
            var first = meshes[(withCliffs.Column, withCliffs.Row)];
            Assert.That(again.ToBytes(), Is.EqualTo(first.ToBytes()), "the same package builds the same cliffs");
        }

        /// <summary>
        /// Tree hashes of three terrain tiles (a core tile at the summit, a core edge tile and a ring tile) and
        /// the site's tree count: they change only with an approved behaviour change (AGENTS.md determinism rules).
        /// </summary>
        static readonly Dictionary<string, string> GoldenTrees = new Dictionary<string, string>
        {
            ["t4_3"] = "2fdd017661a5a8e9a4ca72e34fff31a529c759df86ffcda80e74b8ec590f8f25",
            ["t4_4"] = "ad6e093375d554e7740e6ab2f8a0d3bf432b6641ce6ab6bdeecbc7e66c6b0b5d",
            ["t0_0"] = "c91758af65bf3fb03276dd849da9f51301283f1c63d8cff2d565c77df3162792",
        };
        const int GoldenTreeCount = 256797;

        static (PackageManifest Manifest, string Dir) JacksonHole2Km()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            return (ResortPackage.ReadManifest(dir), dir);
        }

        static TileGrid Grid(PackageManifest manifest) =>
            TileGrid.For(SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0));

        [Test]
        public void JacksonHoleTreesStandOnCanopyAndFollowBigmap()
        {
            var (manifest, dir) = JacksonHole2Km();
            var forest = new ForestField(manifest, dir);
            var tiles = Grid(manifest);
            byte[] canopy = ResortPackage.ReadByteLayer(dir, manifest, "canopy-core", out var ch);

            var built = forest.BuildAll(tiles);
            TestContext.Progress.WriteLine($"Prepared cells in {forest.PrepareSeconds:F2} s, grew trees in {forest.PlantSeconds:F2} s (plain C#)");
            long coreTrees = 0, ringTrees = 0, offCanopy = 0;
            var byModel = new double[SpeciesMap.Models.Length];
            var keys = tiles.All().ToList();
            for (int n = 0; n < keys.Count; n++)
            {
                var b = tiles.Bounds(keys[n]);
                foreach (var t in built[n])
                {
                    Assert.That(t.X, Is.InRange(0, 1024).And.LessThan(1024));
                    Assert.That(t.Z, Is.InRange(0, 1024).And.LessThan(1024));
                    Assert.That(t.Height, Is.InRange(ForestPlacement.MinHeight * 0.6, ForestPlacement.MaxHeight));
                    double x = b.West + t.X, y = b.South + t.Z;
                    int c = (int)Math.Floor(x - ch.West), r = (int)Math.Floor(ch.North - y);
                    bool inCanopy = c >= 0 && r >= 0 && c < ch.Width && r < ch.Height;
                    if (inCanopy && x - ch.West > 10 && ch.West + ch.Width - x > 10 && ch.North - y > 10 && y - (ch.North - ch.Height) > 10)
                    {
                        coreTrees++;
                        if (canopy[(long)r * ch.Width + c] < ForestRule.TreeCode) offCanopy++;
                        byModel[t.Prototype / SpeciesMap.VariantsPerModel]++;
                    }
                    else ringTrees++;
                }
            }
            TestContext.Progress.WriteLine($"Placed {coreTrees:N0} core and {ringTrees:N0} ring trees (ring share {forest.RingTreeShare:F2}); " +
                                           string.Join(", ", byModel.Select((v, k) => (v, k)).Where(p => p.v > 0).OrderByDescending(p => p.v)
                                               .Select(p => $"{SpeciesMap.Models[p.k]} {p.v / coreTrees:P0}")));
            Assert.That(offCanopy, Is.EqualTo(0), "no core tree stands where the canopy map is open (ski runs stay open)");
            double perHectareOfForest = coreTrees / (4.0 * 100 * 0.42);   // 2 km core, about 42% forest (D4 rule)
            Assert.That(perHectareOfForest, Is.InRange(150, 1500), "a plausible subalpine stand density");
            // BIGMAP's top Jackson Hole species all appear.
            foreach (string model in new[] { "douglas_fir", "engelmann_spruce", "subalpine_fir", "lodgepole_pine" })
                Assert.That(byModel[SpeciesMap.IndexOf(model)], Is.GreaterThan(0), model);
        }

        [Test]
        public void KrummholzStaysNearItsOwnSizeAndPointsDownwind()
        {
            var (manifest, dir) = JacksonHole2Km();
            var plan = new ForestField(manifest, dir).Prepare(Grid(manifest));
            // A stand-in model the site doesn't otherwise grow, and every core cell fully in the band.
            plan.KrummholzModel = SpeciesMap.IndexOf("american_beech");
            for (int i = 0; i < plan.Cells.Length; i++)
                if (plan.Cells[i].Kind == ForestCell.Core)
                {
                    plan.Cells[i].Krummholz = 255;
                    plan.Cells[i].Steep = (byte)((i / plan.CellsX) % 2);   // every other row of cells too steep for mats
                }
            new ManagedForestPlanter().Plant(plan);
            int[] codes = Treeline.HeightCodes();
            var perVariant = new int[3];
            for (int t = 0; t < plan.TileCountTotal; t++)
                for (int k = 0; k < plan.TileCount[t]; k++)
                {
                    var p = plan.Points[plan.TileOffset[t] + k];
                    if (p.Prototype / plan.Variants != plan.KrummholzModel) continue;
                    int v = p.Prototype % plan.Variants;
                    perVariant[v]++;
                    int cell = (p.Y - plan.CellOriginY) / PoissonForest.CellFixed * plan.CellsX + (p.X - plan.CellOriginX) / PoissonForest.CellFixed;
                    if (plan.Cells[cell].Steep != 0) Assert.That(v, Is.Not.EqualTo(Treeline.Mat), "no mats on steep ground");
                    Assert.That(p.HeightCode, Is.InRange(codes[2 * v], codes[2 * v + 1]), "within 15% of the variant's own height");
                    Assert.That(p.Rotation, Is.InRange(Treeline.DownwindRotation - 16, Treeline.DownwindRotation + 15), "flag downwind");
                    Assert.That(p.Width32, Is.EqualTo(32));
                }
            TestContext.Progress.WriteLine($"Krummholz: {perVariant[Treeline.Mat]:N0} mats, {perVariant[Treeline.Cushion]:N0} cushions, {perVariant[Treeline.FlagTree]:N0} flag trees");
            Assert.That(perVariant[Treeline.Mat], Is.GreaterThan(perVariant[Treeline.FlagTree]), "the most exposed ground grows mostly mats");
            Assert.That(perVariant.Min(), Is.GreaterThan(0));
        }

        [Test]
        public void TreesKeepTheirSpacingAcrossCellsAndTiles()
        {
            var (manifest, dir) = JacksonHole2Km();
            var plan = new ForestField(manifest, dir).Prepare(Grid(manifest));
            new ManagedForestPlanter().Plant(plan);

            // Every tree against every other within reach, binned on a 4 m grid (integer maths, like the sampler).
            var points = new List<ForestPoint>();
            for (int t = 0; t < plan.TileCountTotal; t++)
                for (int k = 0; k < plan.TileCount[t]; k++) points.Add(plan.Points[plan.TileOffset[t] + k]);
            const int bin = 4 * PoissonForest.Fixed;
            var bins = new Dictionary<(int, int), List<int>>();
            for (int i = 0; i < points.Count; i++)
            {
                var key = (points[i].X / bin, points[i].Y / bin);
                if (!bins.TryGetValue(key, out var list)) bins[key] = list = new List<int>();
                list.Add(i);
            }
            int reach = (plan.MaxSpacing256 + bin - 1) / bin;
            long neighbours = 0, acrossTiles = 0, tooClose = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                for (int by = p.Y / bin - reach; by <= p.Y / bin + reach; by++)
                    for (int bx = p.X / bin - reach; bx <= p.X / bin + reach; bx++)
                    {
                        if (!bins.TryGetValue((bx, by), out var list)) continue;
                        foreach (int j in list)
                        {
                            if (j <= i) continue;
                            var q = points[j];
                            long dx = p.X - q.X, dy = p.Y - q.Y, d = (p.Spacing256 + q.Spacing256) >> 1, d2 = dx * dx + dy * dy;
                            if (d2 < d * d) tooClose++;
                            if (d2 >= 4 * d * d) continue;   // near neighbours: within twice their spacing
                            neighbours++;
                            if (p.X / PoissonForest.TileFixed != q.X / PoissonForest.TileFixed || p.Y / PoissonForest.TileFixed != q.Y / PoissonForest.TileFixed)
                                acrossTiles++;
                        }
                    }
            }
            TestContext.Progress.WriteLine($"{points.Count:N0} trees; {neighbours:N0} near neighbours, {acrossTiles:N0} of them across a 64 m tile edge");
            Assert.That(acrossTiles, Is.GreaterThan(1000), "trees stand near each other across tile edges (no empty seams)");
            Assert.That(tooClose, Is.EqualTo(0), "no two trees closer than their spacing, even across cells and tiles");
        }

        [Test]
        public void JacksonHoleForestIsDeterministicAndMatchesItsGoldenHashes()
        {
            var (manifest, dir) = JacksonHole2Km();
            var tiles = Grid(manifest);
            var keys = tiles.All().ToList();
            var first = new ForestField(manifest, dir).BuildAll(tiles);
            var second = new ForestField(manifest, dir).BuildAll(tiles);
            string Hash(List<PlacedTree> trees) => GridFile.HashValues(ForestField.Encode(trees, TileGrid.TileMetres));
            for (int n = 0; n < keys.Count; n++)
                Assert.That(Hash(second[n]), Is.EqualTo(Hash(first[n])), $"{keys[n]} identical across runs");

            int total = first.Sum(t => t.Count);
            string Golden(string key) => Hash(first[keys.FindIndex(k => k.ToString() == key)]);
            foreach (var kv in GoldenTrees) TestContext.Progress.WriteLine($"[\"{kv.Key}\"] = \"{Golden(kv.Key)}\",");
            TestContext.Progress.WriteLine($"GoldenTreeCount = {total};");
            foreach (var kv in GoldenTrees) Assert.That(Golden(kv.Key), Is.EqualTo(kv.Value), kv.Key + " golden hash");
            Assert.That(total, Is.EqualTo(GoldenTreeCount), "golden tree count");
        }
    }
}
