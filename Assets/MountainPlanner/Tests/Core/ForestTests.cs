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
    /// Forest placement (0.3 §4.5): trees stand only on canopy, species follow BIGMAP, the same package
    /// always grows the same forest, and the species map matches the tree library.
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
            Assert.That(TreeLibrary.ModelledSpecies.Count, Is.EqualTo(SpeciesMap.Models.Length));
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

        [Test]
        public void RocksFollowSlopeAndStayOffWater()
        {
            Assert.That(RockPlacement.Rockiness(10, 0, 0), Is.EqualTo(0), "gentle meadows have no rocks");
            Assert.That(RockPlacement.Rockiness(50, 0, 0), Is.EqualTo(1), "steep faces are rocky");
            Assert.That(RockPlacement.Rockiness(50, 0, 1), Is.LessThan(0.5), "fewer under forest");
            Assert.That(RockPlacement.Rockiness(5, 1, 0), Is.GreaterThan(0.3), "bare alpine ground is rocky");
            Assert.That(RockPlacement.Rockiness(30, 0, 0), Is.EqualTo(0), "in winter, rocks on skiable slopes are buried");
            Assert.That(RockPlacement.OutcropChance(30), Is.EqualTo(0));
            Assert.That(RockPlacement.OutcropChance(70), Is.EqualTo(0.3));
            Assert.That(RockPlacement.BoulderSize(0), Is.EqualTo(0.5));
            Assert.That(RockPlacement.BoulderSize(0.5), Is.LessThan(1.0), "most boulders are small");
        }

        [Test]
        public void JacksonHoleRocksAreDeterministicAndOnSteepGround()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            var manifest = ResortPackage.ReadManifest(dir);
            float[] core = ResortPackage.ReadLayer(dir, manifest, "heights-core", out var coreHeader);
            float[] ring = ResortPackage.ReadLayer(dir, manifest, "heights-ring", out var ringHeader);
            var heights = new TerrainCache.HeightField(core, coreHeader, ring, ringHeader);
            var site = SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0);
            var tiles = TileGrid.For(site);
            long boulders = 0, outcrops = 0;
            string hash = null;
            foreach (var key in tiles.All())
            {
                var rocks = new RockField(manifest, dir, heights).BuildTile(tiles.Bounds(key));
                foreach (var r in rocks)
                {
                    if (r.Prototype < RockPlacement.BoulderVariants) boulders++; else outcrops++;
                    Assert.That(r.Prototype, Is.LessThan(RockPlacement.Prototypes));
                }
                if (hash == null && rocks.Count > 0)
                {
                    hash = GridFile.HashValues(ForestField.Encode(rocks, 1024));
                    var again = new RockField(manifest, dir, heights).BuildTile(tiles.Bounds(key));
                    Assert.That(GridFile.HashValues(ForestField.Encode(again, 1024)), Is.EqualTo(hash), "the same package gets the same rocks");
                }
            }
            TestContext.Progress.WriteLine($"Rocks: {boulders:N0} boulders, {outcrops:N0} outcrops");
            Assert.That(boulders, Is.GreaterThan(500), "Jackson Hole is a rocky mountain");
            Assert.That(outcrops, Is.GreaterThan(50));
        }

        [Test]
        public void JacksonHoleTreesStandOnCanopyAndFollowBigmap()
        {
            string dir = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(dir, "*.grid")) TestData.Bytes(dir, Path.GetFileName(f));
            var manifest = ResortPackage.ReadManifest(dir);
            var forest = new ForestField(manifest, dir);
            var site = SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0);
            var tiles = TileGrid.For(site);
            byte[] canopy = ResortPackage.ReadByteLayer(dir, manifest, "canopy-core", out var ch);

            var watch = System.Diagnostics.Stopwatch.StartNew();
            long coreTrees = 0, ringTrees = 0, offCanopy = 0;
            var byModel = new double[SpeciesMap.Models.Length];
            string firstHash = null;
            foreach (var key in tiles.All())
            {
                var b = tiles.Bounds(key);
                var trees = forest.BuildTile(b);
                foreach (var t in trees)
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
                if (firstHash == null && trees.Count > 0) firstHash = GridFile.HashValues(ForestField.Encode(trees, 1024));
            }
            TestContext.Progress.WriteLine($"Placed {coreTrees:N0} core and {ringTrees:N0} ring trees in {watch.Elapsed.TotalSeconds:F1} s (ring share {forest.RingTreeShare:F2}); " +
                                           string.Join(", ", byModel.Select((n, k) => (n, k)).Where(p => p.n > 0).OrderByDescending(p => p.n)
                                               .Select(p => $"{SpeciesMap.Models[p.k]} {p.n / coreTrees:P0}")));
            Assert.That(offCanopy, Is.EqualTo(0), "no core tree stands where the canopy map is open (ski runs stay open)");
            double perHectareOfForest = coreTrees / (4.0 * 100 * 0.42);   // 2 km core, about 42% forest (D4 rule)
            Assert.That(perHectareOfForest, Is.InRange(150, 1500), "a plausible subalpine stand density");
            // BIGMAP's top Jackson Hole species all appear.
            foreach (string model in new[] { "douglas_fir", "engelmann_spruce", "subalpine_fir", "lodgepole_pine" })
                Assert.That(byModel[SpeciesMap.IndexOf(model)], Is.GreaterThan(0), model);

            var again = new ForestField(manifest, dir).BuildTile(tiles.Bounds(tiles.All().First(k => forest.BuildTile(tiles.Bounds(k)).Count > 0)));
            Assert.That(GridFile.HashValues(ForestField.Encode(again, 1024)), Is.EqualTo(firstHash), "the same package grows the same forest");
        }
    }
}
