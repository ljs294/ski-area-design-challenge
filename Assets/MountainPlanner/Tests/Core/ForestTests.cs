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
        public void NewEnglandSpeciesAreDrawnAsThemselves()
        {
            // Task 09 phase 2 (NE5): three new models, and six species that share a model because they look
            // largely alike. Both count as drawn as themselves for species fidelity (F1); a look-alike doesn't.
            var expected = new Dictionary<int, string>
            {
                [129] = "eastern_white_pine", [833] = "northern_red_oak", [762] = "black_cherry",
                [12] = "subalpine_fir", [241] = "subalpine_fir", [97] = "engelmann_spruce",
                [261] = "western_hemlock", [541] = "sugar_maple", [372] = "black_cherry",
            };
            foreach (var kv in expected)
            {
                Assert.That(SpeciesMap.Models[SpeciesMap.ModelFor(kv.Key)], Is.EqualTo(kv.Value), $"FIA {kv.Key}");
                Assert.That(SpeciesMap.IsModelled(kv.Key), Is.True, $"FIA {kv.Key} counts as modelled");
                Assert.That(SpeciesMap.ModelledCodes, Does.Contain(kv.Key));
            }
            Assert.That(SpeciesMap.IsModelled(802), Is.False, "white oak is only a look-alike");
            Assert.That(SpeciesMap.IsModelled(543), Is.False, "black ash is only a look-alike");
            Assert.That(SpeciesMap.ModelledCodes, Is.Unique);
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
            ["t4_3"] = "ad8b35958d807f8a8b18d416018e22743872dcc89dac599f94d00a4df78a4206",
            ["t4_4"] = "303b751035854b868c4c1d40ad0cd50348ecdb9d6bf6245093ac77b267be467f",
            ["t0_0"] = "26a334e0f87529f9e193b1accb4379e90b976791a9185f365ad8bc803df74d6f",
        };
        const int GoldenTreeCount = 182149;   // task 4.27 (cache v13): forest-grown aspen crowns spaced by their own size; was 195,568 (cache v12)
        /// <summary>The goldens before task 4.27 (owner-approved 2026-10-05, cache v12): every tree spaced by the conifer crown.</summary>
        static readonly Dictionary<string, string> ConiferCrownGoldenTrees = new Dictionary<string, string>
        {
            ["t4_3"] = "e5037b18efdc790dedd987ff7ecc5b12ff1a479391a4181e622f1aafdd8a7f7b",
            ["t4_4"] = "17e7c892a83a6199b0192dd636eeb4103c1073ef335114238db4807f68b0ea80",
            ["t0_0"] = "1596d39616262363aa08959648fbeee8e6b134f6f4fdf571997ca037472fa71a",
        };
        const int ConiferCrownGoldenTreeCount = 195568;

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
        public void StandHeightsFollowTheirFormula()
        {
            int[] table = ForestPlacement.StandHeightTable();
            Assert.That(table.Length, Is.EqualTo(PoissonForest.StandQuantiles + 1));
            for (int i = 0; i < table.Length; i++)
            {
                double u = (double)i / PoissonForest.StandQuantiles;
                double share = 1 - (1 - ForestPlacement.StandShortest) * Math.Pow(1 - u, ForestPlacement.StandSkew);
                Assert.That(table[i], Is.EqualTo(256 * share).Within(0.5), $"quantile {i}");
                if (i > 0) Assert.That(table[i], Is.GreaterThanOrEqualTo(table[i - 1]), "shortest first");
            }
            Assert.That(table[table.Length - 1], Is.EqualTo(256), "the tallest reach the dominant height");
        }

        [Test]
        public void ShadeTolerantConifersAreTheFirsSprucesHemlocksAndCedars()
        {
            foreach (int spcd in new[] { 12, 19, 94, 95, 97, 241, 261, 264 })
                Assert.That(ForestField.IsTolerantConifer(spcd), Is.True, $"FIA {spcd}");
            foreach (int spcd in new[] { 108, 122, 129, 202, 73, 316, 375, 746 })
                Assert.That(ForestField.IsTolerantConifer(spcd), Is.False, $"FIA {spcd}");
            Assert.That(ForestPlacement.StandWeight(1, 1), Is.EqualTo(1));
            Assert.That(ForestPlacement.StandWeight(0.1, 1), Is.EqualTo(0), "pine and Douglas-fir stands stay even");
            Assert.That(ForestPlacement.StandWeight(1, 0.2), Is.EqualTo(0), "open stands stay as they were");
        }

        [Test]
        public void TheClumpFieldIsSeamlessAndKeepsItsRange()
        {
            var f = new ForestInputs
            {
                Seed = 0x5EEDUL, ClumpLattice = ForestPlacement.ClumpLattice256, ClumpRadius = ForestPlacement.ClumpRadius256,
                ClumpFloor = ForestPlacement.ClumpFloor,
            };
            int full = 0, floor = 0, n = 0;
            long sum = 0;
            for (int y = 0; y < 200 * PoissonForest.Fixed; y += 97)
                for (int x = 0; x < 200 * PoissonForest.Fixed; x += 389)
                {
                    int keep = PoissonForest.ClumpKeep(ref f, x, y);
                    Assert.That(keep, Is.InRange(ForestPlacement.ClumpFloor, 256));
                    if (keep >= 240) full++;
                    if (keep == ForestPlacement.ClumpFloor) floor++;
                    sum += keep;
                    n++;
                }
            TestContext.Progress.WriteLine($"Clump field: {100.0 * full / n:F0}% near a clump centre, {100.0 * floor / n:F0}% in the gaps, mean keep {sum / (double)n / 256:P0}");
            Assert.That(full, Is.GreaterThan(0), "clumps");
            Assert.That(floor, Is.GreaterThan(n / 20), "gaps between them");
            // A function of frame position alone: the same on both sides of a tile or cell edge, so no seams.
            for (int d = -3; d <= 3; d++)
            {
                int x = 5 * PoissonForest.TileFixed + d, y = 3 * PoissonForest.TileFixed + 7 * d;
                Assert.That(Math.Abs(PoissonForest.ClumpKeep(ref f, x, y) - PoissonForest.ClumpKeep(ref f, x + 1, y + 1)), Is.LessThanOrEqualTo(2));
            }
        }

        /// <summary>Every tree of a grown plan.</summary>
        static List<ForestPoint> Trees(ForestPlan plan)
        {
            var points = new List<ForestPoint>();
            for (int t = 0; t < plan.TileCountTotal; t++)
                for (int k = 0; k < plan.TileCount[t]; k++) points.Add(plan.Points[plan.TileOffset[t] + k]);
            return points;
        }

        /// <summary>Jackson Hole's plan with every forest core cell a full dense conifer stand, or none.</summary>
        static ForestPlan JacksonHolePlan(byte stand)
        {
            var (manifest, dir) = JacksonHole2Km();
            var plan = new ForestField(manifest, dir).Prepare(Grid(manifest));
            for (int i = 0; i < plan.Cells.Length; i++)
            {
                if (plan.Cells[i].Kind == ForestCell.Core) plan.Cells[i].Stand = stand;
                plan.Cells[i].Krummholz = 0;
            }
            new ManagedForestPlanter().Plant(plan);
            return plan;
        }

        [Test]
        public void DenseConiferStandsGrowAnUnderstoryInClumpsWithTheSameTrees()
        {
            ForestPlan even = JacksonHolePlan(0), stand = JacksonHolePlan(255);
            (double Cv, double Under, double Clumping, long Trees) Measure(ForestPlan plan)
            {
                var trees = Trees(plan);
                double sum = 0, sum2 = 0;
                long under = 0, core = 0;
                var quadrats = new Dictionary<(int, int), int>();
                foreach (var p in trees)
                {
                    int cell = (p.Y - plan.CellOriginY) / PoissonForest.CellFixed * plan.CellsX + (p.X - plan.CellOriginX) / PoissonForest.CellFixed;
                    if (plan.Cells[cell].Kind != ForestCell.Core) continue;   // the ring never grows stands
                    core++;
                    double share = p.HeightCode / (double)plan.Cells[cell].HeightCode;
                    sum += share;
                    sum2 += share * share;
                    if (share < 0.6) under++;
                    var q = (p.X / (5 * PoissonForest.Fixed), p.Y / (5 * PoissonForest.Fixed));
                    quadrats[q] = quadrats.TryGetValue(q, out int c) ? c + 1 : 1;
                }
                double mean = sum / core, cv = Math.Sqrt(sum2 / core - mean * mean) / mean;
                double qm = quadrats.Values.Average(), qv = quadrats.Values.Average(v => (v - qm) * (v - qm));
                return (cv, under / (double)core, qv / qm, core);
            }
            var a = Measure(even);
            var b = Measure(stand);
            TestContext.Progress.WriteLine($"Even: {a.Trees:N0} core trees, height CV {a.Cv:F3}, {a.Under:P1} under 60%, quadrat variance/mean {a.Clumping:F2}");
            TestContext.Progress.WriteLine($"Stand: {b.Trees:N0} core trees, height CV {b.Cv:F3}, {b.Under:P1} under 60%, quadrat variance/mean {b.Clumping:F2}");
            Assert.That(a.Under, Is.EqualTo(0), "even stands keep 65–100% of the dominant height");
            Assert.That(b.Under, Is.GreaterThan(0.1), "a visible understory of suppressed trees");
            Assert.That(b.Cv, Is.GreaterThan(a.Cv * 1.5), "a wider height spread");
            Assert.That(b.Clumping, Is.GreaterThan(a.Clumping), "clumps and gaps");
            // The density calibration holds: the cells' quotas are unchanged. Smaller understory crowns only let crowded
            // cells come closer to their quota (the even forest falls a few percent short where crowns jam).
            Assert.That(b.Trees, Is.InRange(a.Trees, a.Trees * 1.05), "no fewer trees, and none beyond the quotas' slack");
            Assert.That(Trees(stand).Count, Is.LessThanOrEqualTo(stand.Points.Length), "never more than the cells' quotas");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TreesKeepTheirSpacingAcrossCellsAndTiles(bool denseConiferStands)
        {
            ForestPlan plan;
            if (denseConiferStands) plan = JacksonHolePlan(255);
            else
            {
                var (manifest, dir) = JacksonHole2Km();
                plan = new ForestField(manifest, dir).Prepare(Grid(manifest));
                new ManagedForestPlanter().Plant(plan);
            }

            // Every tree against every other within reach, binned on a 4 m grid (integer maths, like the sampler).
            var points = Trees(plan);
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

        // Task 4.27: forest-grown broadleaf crowns, spaced by their own size.

        [Test]
        public void AnEmptyCrownTableGrowsTheForestAsBefore()
        {
            // Every tree spaced and counted by the conifer crown, as before task 4.27: byte-identical to the goldens before it (cache v12).
            var (manifest, dir) = JacksonHole2Km();
            var tiles = Grid(manifest);
            var keys = tiles.All().ToList();
            var trees = new ForestField(manifest, dir, null, new int[SpeciesMap.Models.Length]).BuildAll(tiles);
            string Hash(List<PlacedTree> t) => GridFile.HashValues(ForestField.Encode(t, TileGrid.TileMetres));
            foreach (var kv in ConiferCrownGoldenTrees)
                Assert.That(Hash(trees[keys.FindIndex(k => k.ToString() == kv.Key)]), Is.EqualTo(kv.Value), kv.Key + " golden hash");
            Assert.That(trees.Sum(t => t.Count), Is.EqualTo(ConiferCrownGoldenTreeCount), "golden tree count");
        }

        [Test]
        public void TheCrownTableMatchesTheTreeLibrary()
        {
            string json = null;
            foreach (string start in new[] { TestContext.CurrentContext.TestDirectory, Environment.CurrentDirectory })
                for (var d = new DirectoryInfo(start); d != null && json == null; d = d.Parent)
                {
                    string candidate = Path.Combine(d.FullName, "tools", "assets", "trees", "species.json");
                    if (File.Exists(candidate)) json = File.ReadAllText(candidate);
                }
            if (json == null) Assert.Ignore("tools/assets/trees/species.json is not available here.");
            var table = ForestPlacement.BroadleafCrowns.ToDictionary(c => c.Model, c => c.Share256);
            var deciduous = new List<string>();
            foreach (string block in Regex.Split(json, "\\{\\s*\"id\"").Skip(1))
            {
                string id = Regex.Match(block, "^\\s*:\\s*\"([a-z_]+)\"").Groups[1].Value;
                if (!Regex.IsMatch(block, "\"form\"\\s*:\\s*\"deciduous\"")) continue;
                deciduous.Add(id);
                double crown = double.Parse(Regex.Match(block, "\"crownRadius\"\\s*:\\s*([0-9.]+)").Groups[1].Value,
                                            System.Globalization.CultureInfo.InvariantCulture);
                Assert.That(table.ContainsKey(id), id + " has a crown in the table");
                Assert.That(table[id], Is.EqualTo((int)Math.Round(crown * 256)), id + ": the table's crown is species.json's crownRadius");
            }
            Assert.That(table.Keys, Is.EquivalentTo(deciduous), "every broadleaf model, and only those");
        }

        [TestCase(20.0)]
        [TestCase(35.0)]
        public void BroadleafStandsCloseWithoutStackingCrowns(double height)
        {
            // Crowns over each point of a full, pure stand: the D4 density, about 1.5 (task 4.27). By the conifer crown a
            // broadleaf stand stacked far deeper (BB21: about 9 at 18 m with the open-field oak).
            var calibration = ForestCalibration.Default;
            const double cell = ForestPlacement.CellMetres * ForestPlacement.CellMetres;
            foreach (var (model, share) in ForestPlacement.BroadleafCrowns)
            {
                double r = ForestPlacement.CrownRadius(height, share), area = Math.PI * r * r;
                double layers = ForestPlacement.TreesPerCell(1, calibration, area) * area / cell;
                double before = ForestPlacement.TreesPerCell(1, height, calibration) * area / cell;
                Assert.That(layers, Is.LessThanOrEqualTo(calibration.DensityFactor + 1e-9), $"{model} at {height} m: {layers:F2} crowns deep");
                Assert.That(before, Is.GreaterThan(layers), $"{model}: the conifer rule stacked its crowns ({before:F2})");
            }
        }

        [Test]
        public void BroadleafTreesStandApartByTheirOwnCrownsAndWidenAtOpenings()
        {
            var (manifest, dir) = JacksonHole2Km();
            var plan = new ForestField(manifest, dir).Prepare(Grid(manifest));
            new ManagedForestPlanter().Plant(plan);
            var trees = Trees(plan);
            int aspen = SpeciesMap.IndexOf("quaking_aspen"), variants = SpeciesMap.VariantsPerModel;
            bool Broad(ForestPoint p) => plan.CrownShare256[p.Prototype / variants] != 0 && p.Prototype / variants != plan.KrummholzModel;
            ForestCell CellOf(ForestPoint p)
            {
                int i = (p.X - plan.CellOriginX) / PoissonForest.CellFixed, j = (p.Y - plan.CellOriginY) / PoissonForest.CellFixed;
                return plan.Cells[j * plan.CellsX + i];
            }
            var broad = trees.Where(Broad).ToList();
            var conifers = trees.Where(p => !Broad(p)).ToList();
            Assert.That(broad.Count(p => p.Prototype / variants == aspen), Is.GreaterThan(1000), "Jackson Hole grows aspen");
            double Ratio(List<ForestPoint> list) => list.Average(p => p.Spacing256 / (double)CellOf(p).Spacing256);
            Assert.That(Ratio(broad), Is.GreaterThan(1.05), "an aspen stands further apart than its cell's conifer spacing");
            Assert.That(Ratio(conifers), Is.LessThanOrEqualTo(1.0), "a conifer never further");

            // Edge trees: some broadleaf trees widen beyond the 85-115% jitter, no conifer does, and not every broadleaf tree.
            int jitterMax = 32 * (218 + 77) / 256;
            var core = broad.Where(p => CellOf(p).Kind == ForestCell.Core).ToList();
            var widened = core.Where(p => p.Width32 > jitterMax).ToList();
            TestContext.Progress.WriteLine($"{core.Count:N0} core broadleaf trees, {widened.Count:N0} widened at openings");
            Assert.That(widened.Count, Is.GreaterThan(core.Count / 50), "trees beside trails and glades widen");
            Assert.That(widened.Count, Is.LessThan(core.Count * 6 / 10), "interior trees don't");
            Assert.That(conifers.Where(p => CellOf(p).Kind == ForestCell.Core).All(p => p.Width32 <= jitterMax), "conifers keep their width");
            Assert.That(widened.All(p => p.Width32 <= jitterMax * (256 + ForestPlacement.EdgeWiden256) / 256), "at most 1.25 x");

            // Each widened tree has open ground on the canopy map within two crown radii (never only the core's straight edge).
            int Bit(int px, int py)
            {
                int mx = (px >> 8) - plan.MaskWest, my = (int)Math.Floor(((plan.MaskNorth << 8) - py) / 256.0);
                if (mx < 0 || my < 0 || mx >= plan.MaskWidth || my >= plan.MaskHeight) return -1;
                long bit = (long)my * plan.MaskWidth + mx;
                return (int)((plan.CanopyMask[bit >> 6] >> (int)(bit & 63)) & 1UL);
            }
            foreach (var p in widened)
            {
                int r = PoissonForest.BroadleafCrown256(plan.CrownShare256[p.Prototype / variants], p.HeightCode);
                bool open = false;
                for (int d = 0; d < 8 && !open; d++)
                {
                    double a = d * Math.PI / 4;
                    open = Bit(p.X + (int)(Math.Cos(a) * 2 * r), p.Y + (int)(Math.Sin(a) * 2 * r)) == 0;
                }
                Assert.That(open, $"a widened tree at ({p.X / 256}, {p.Y / 256}) has an opening on the map beside it");
            }
        }
    }
}
