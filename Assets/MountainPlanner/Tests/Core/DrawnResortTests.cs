using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The drawn resort (Phase 3 task 11): its frozen lines keep the mix the owner chose (DB3, DB4) and stay paintable,
    /// and, on the Jackson Hole 5 km package (local only, E2: set MP_JH5_PACKAGE to its folder), every lift and trail goes
    /// in through the design's commands and the same mountain builds the same design.
    /// </summary>
    public class DrawnResortTests
    {
        [Test]
        public void DB3_TwoLiftsOfEachBuildableType()
        {
            Assert.That(DrawnResort.Lifts.Length, Is.EqualTo(6));
            foreach (string id in new[] { LiftCatalogIds.SesselliftFgq4, LiftCatalogIds.MontaFg4, LiftCatalogIds.SesselliftD6 })
                Assert.That(DrawnResort.Lifts.Count(l => l.CatalogId == id), Is.EqualTo(2), id);
            Assert.That(DrawnResort.Lifts.All(l => LiftCatalogIds.Buildable(l.CatalogId)), Is.True);
            Assert.That(DrawnResort.Lifts.Select(l => l.Name).Distinct().Count(), Is.EqualTo(6), "names are unique");
            foreach (var l in DrawnResort.Lifts)
            {
                double length = Math.Sqrt(Math.Pow(l.TopX - l.BottomX, 2) + Math.Pow(l.TopZ - l.BottomZ, 2));
                Assert.That(length, Is.InRange(800, 1600), l.Name);
            }
        }

        [Test]
        public void DB4_TenGradedTenNaturalTwoCatTracksTwoIslands()
        {
            var runs = DrawnResort.Trails;
            Assert.That(runs.Length, Is.EqualTo(20));
            Assert.That(runs.Count(r => r.Graded), Is.EqualTo(10));
            Assert.That(runs.Count(r => r.CatTrack), Is.EqualTo(2));
            Assert.That(runs.Where(r => r.CatTrack).All(r => r.Graded && r.WidthFeet == DrawnResort.CatTrackFeet), Is.True);
            Assert.That(runs.Count(r => r.IslandAt >= 0), Is.EqualTo(2));
            Assert.That(runs.Select(r => r.Name).Distinct().Count(), Is.EqualTo(20), "names are unique");
            foreach (var r in runs)
            {
                // TR-01's brush, 25-400 ft in 5 ft steps; DB4's 60-200 ft for runs.
                Assert.That(r.WidthFeet % 5, Is.EqualTo(0), r.Name);
                if (!r.CatTrack) Assert.That(r.WidthFeet, Is.InRange(60, 200), r.Name);
                Assert.That(r.Centreline.Length % 2, Is.EqualTo(0), r.Name);
                Assert.That(r.Centreline.Length / 2, Is.GreaterThanOrEqualTo(10), r.Name);
                for (int i = 2; i < r.Centreline.Length; i += 2)
                {
                    double step = Math.Sqrt(Math.Pow(r.Centreline[i] - r.Centreline[i - 2], 2) + Math.Pow(r.Centreline[i + 1] - r.Centreline[i - 1], 2));
                    Assert.That(step, Is.InRange(1, 35), $"{r.Name}: points about 25 m apart (a little more where two legs join)");
                }
                Assert.That(r.IslandAt, Is.LessThan(r.Centreline.Length / 2), r.Name);
                // Everything inside the 5 km core.
                Assert.That(r.Centreline.All(v => Math.Abs(v) < 2400), Is.True, r.Name);
            }
        }

        [Test]
        public void DB1_TheResortBuildsByCommandTheSameEachTime()
        {
            string dir = Environment.GetEnvironmentVariable("MP_JH5_PACKAGE");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("set MP_JH5_PACKAGE to the Jackson Hole 5 km package folder");
            byte[] first = null;
            for (int pass = 0; pass < 2; pass++)
            {
                var watch = Stopwatch.StartNew();
                var (history, report) = Build(dir);
                TestContext.Progress.WriteLine($"pass {pass}: {watch.Elapsed.TotalSeconds:F1} s\n{report.Text}");
                Assert.That(report.Rejected, Is.EqualTo(0), report.Text);
                Assert.That(report.Accepted, Is.EqualTo(26));
                var doc = history.Document;
                Assert.That(doc.Lifts.Count, Is.EqualTo(6));
                Assert.That(doc.Lifts.All(l => l.Stage == WorkStage.Built && l.Towers.Count > 0 && l.Levels.HasValue), Is.True);
                byte[] bytes = DesignBytes.Encode(doc);
                if (first == null) first = bytes;
                else Assert.That(bytes, Is.EqualTo(first), "the same mountain builds the same design");
            }
        }

        /// <summary>The resort built over the package's natural ground, with the game's fold over the core (TerrainEditSession's).</summary>
        internal static (DesignHistory history, DrawnResort.Report report) Build(string dir)
        {
            var m = ResortPackage.ReadManifest(dir);
            var square = SiteSquare.Create(new AlbersPoint(m.Site.CentreX, m.Site.CentreY), m.Site.SizeMetres / 1000.0);
            var tiles = TileGrid.For(square);
            var lattice = new EditLattice(tiles.West, tiles.North);
            var core = square.Core;
            var site = new EarthworkSite(lattice, m.Site.CentreX, m.Site.CentreY,
                                         new PointRect((int)Math.Ceiling(core.West - lattice.West), (int)Math.Ceiling(lattice.North - core.North),
                                                       (int)Math.Floor(core.East - lattice.West), (int)Math.Floor(lattice.North - core.South)));
            using var heights = new PackageHeights(dir, m);
            var history = new DesignHistory(DesignDocument.Empty(1));
            var fold = new DesignEarthwork(heights, site);
            fold.Prime(history.Document, history.Terrain);
            var report = DrawnResort.Build(history, fold, heights, lattice, new LocalFrame(new AlbersPoint(m.Site.CentreX, m.Site.CentreY)));
            return (history, report);
        }
    }
}
