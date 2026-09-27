using System;
using System.Linq;
using MountainPlanner.Domain.Geo;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 03 acceptance (docs/plans/phase0-0.7-phase1-plan.md): round trip < 1 mm; reference points
    // match PROJ within 1 cm; scale factor within ±1% at ski areas. Engine-free.
    public sealed class Albers6350Tests
    {
        // Precomputed with PROJ 9 (pyproj 3.8): EPSG:6318 → EPSG:6350, and get_factors on EPSG:6350.
        public static readonly object[] ProjReference =
        {
            new object[] { "Jackson Hole", 43.593, -110.848, -1188827.5175, 2381975.6670, 0.995551258, 1.004468622 },
            new object[] { "Crystal Mountain", 46.93, -121.49, -1926410.3061, 2919856.6649, 1.004382021, 0.995637097 },
            new object[] { "Big Sky", 45.284, -111.401, -1202324.9075, 2574950.6163, 0.999418709, 1.000581629 },
            new object[] { "Killington", 43.6045, -72.8201, 1844773.1514, 2516303.2204, 0.995573615, 1.004446065 },
            new object[] { "Taos", 36.5961, -105.4545, -836634.3564, 1547155.1907, 0.990573064, 1.009516649 },
            new object[] { "Key West", 24.5551, -81.78, 1454884.7614, 278520.2352, 1.013668847, 0.986515471 },
            new object[] { "Northwest Angle", 49.38, -95.15, 62600.0212, 2930205.5886, 1.014247254, 0.985952879 },
            new object[] { "Cape Flattery", 48.38, -124.72, -2115828.8188, 3142204.5521, 1.009837434, 0.990258398 },
            new object[] { "Projection origin", 23.0, -96.0, 0.0, 0.0, 1.019079491, 0.981277720 },
        };

        [TestCaseSource(nameof(ProjReference))]
        public void ForwardMatchesProjWithinOneCentimetre(string name, double lat, double lon, double x, double y, double k, double h)
        {
            var p = Albers6350.Forward(new GeoPoint(lat, lon));
            Assert.That(p.X, Is.EqualTo(x).Within(0.01), name);
            Assert.That(p.Y, Is.EqualTo(y).Within(0.01), name);
        }

        [TestCaseSource(nameof(ProjReference))]
        public void InverseMatchesProjWithinOneCentimetre(string name, double lat, double lon, double x, double y, double k, double h)
        {
            var g = Albers6350.Inverse(new AlbersPoint(x, y));
            // 1 cm is about 9e-8 degrees of latitude.
            Assert.That(g.Latitude, Is.EqualTo(lat).Within(9e-8), name);
            Assert.That(g.Longitude, Is.EqualTo(lon).Within(1.3e-7), name);
        }

        [TestCaseSource(nameof(ProjReference))]
        public void ScaleFactorsMatchProj(string name, double lat, double lon, double x, double y, double k, double h)
        {
            var s = Albers6350.ScaleAt(lat);
            Assert.That(s.Parallel, Is.EqualTo(k).Within(1e-8), name);
            Assert.That(s.Meridian, Is.EqualTo(h).Within(1e-8), name);
            Assert.That(s.Parallel * s.Meridian, Is.EqualTo(1).Within(1e-12), "equal-area: k·h = 1");
        }

        [Test]
        public void RoundTripIsBelowOneMillimetreAcrossTheContiguousUs()
        {
            double worst = 0;
            for (double lat = 24.5; lat <= 49.5; lat += 0.5)
                for (double lon = -125; lon <= -66.5; lon += 0.5)
                {
                    var p = Albers6350.Forward(new GeoPoint(lat, lon));
                    var back = Albers6350.Forward(Albers6350.Inverse(p));
                    worst = Math.Max(worst, p.DistanceTo(back));
                }
            Assert.That(worst, Is.LessThan(0.001));
        }

        [TestCase("Jackson Hole", 43.593)]
        [TestCase("Crystal Mountain", 46.93)]
        [TestCase("Big Sky", 45.284)]
        [TestCase("Killington", 43.6045)]
        [TestCase("Taos", 36.5961)]
        [TestCase("Snowbird", 40.58)]
        [TestCase("Stowe", 44.53)]
        [TestCase("Mammoth", 37.63)]
        public void ScaleIsWithinOnePercentAtSkiAreas(string name, double lat)
        {
            Assert.That(Albers6350.ScaleAt(lat).MaxDistortion, Is.LessThan(0.01), name);
        }

        // True north's bearing on the grid, measured with PROJ by stepping 0.001° north.
        [TestCase("Jackson Hole", 43.593, -110.848, 8.9519)]
        [TestCase("Crystal Mountain", 46.93, -121.49, 15.3680)]
        [TestCase("Killington", 43.6045, -72.8201, -13.9752)]
        public void GridConvergenceMatchesProj(string name, double lat, double lon, double expected)
        {
            Assert.That(Albers6350.GridConvergence(lon), Is.EqualTo(expected).Within(0.001), name);
            // And it agrees with the projection itself: a step due north moves along that grid bearing.
            var a = Albers6350.Forward(new GeoPoint(lat, lon));
            var b = Albers6350.Forward(new GeoPoint(lat + 0.001, lon));
            double bearing = Math.Atan2(b.X - a.X, b.Y - a.Y) * 180 / Math.PI;
            Assert.That(bearing, Is.EqualTo(Albers6350.GridConvergence(lon)).Within(0.001), name);
        }

        [Test]
        public void ScaleIsExactlyOneOnTheStandardParallels()
        {
            Assert.That(Albers6350.ScaleAt(29.5).Parallel, Is.EqualTo(1).Within(1e-12));
            Assert.That(Albers6350.ScaleAt(45.5).Parallel, Is.EqualTo(1).Within(1e-12));
        }

        [Test]
        public void InvalidCoordinatesAreRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(91, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(0, -181));
            Assert.Throws<ArgumentOutOfRangeException>(() => new GeoPoint(double.NaN, 0));
        }
    }

    public sealed class GridTests
    {
        static readonly GridSpec Grid = new GridSpec(-1000, 2000, 2, 50, 40);

        [Test]
        public void CellCentresAndLookupAgree()
        {
            for (int r = 0; r < Grid.Rows; r += 7)
                for (int c = 0; c < Grid.Columns; c += 7)
                {
                    Assert.That(Grid.TryCellAt(Grid.CellCentre(c, r), out int c2, out int r2), Is.True);
                    Assert.That((c2, r2), Is.EqualTo((c, r)));
                }
        }

        [Test]
        public void EdgesBelongToTheCellEastAndSouthOfThem()
        {
            Assert.That(Grid.TryCellAt(new AlbersPoint(-1000, 2000), out int c, out int r), Is.True);
            Assert.That((c, r), Is.EqualTo((0, 0)), "the north-west corner is cell (0, 0)");
            Assert.That(Grid.TryCellAt(new AlbersPoint(Grid.East, 1990), out _, out _), Is.False, "the east edge is outside");
            Assert.That(Grid.TryCellAt(new AlbersPoint(-990, Grid.South), out _, out _), Is.False, "the south edge is outside");
        }

        [Test]
        public void BoundsAndIndexAreConsistent()
        {
            Assert.That(Grid.Bounds, Is.EqualTo(new AlbersBox(-1000, 1920, -900, 2000)));
            Assert.That(Grid.Index(3, 2), Is.EqualTo(2 * 50 + 3));
            Assert.That(Grid.CellCount, Is.EqualTo(2000));
        }

        [Test]
        public void AlignmentMeansWholeCellOffsets()
        {
            Assert.That(Grid.IsAlignedWith(new GridSpec(-990, 1980, 2, 5, 5)), Is.True);
            Assert.That(Grid.IsAlignedWith(new GridSpec(-989, 1980, 2, 5, 5)), Is.False);
            Assert.That(Grid.IsAlignedWith(new GridSpec(-1000, 2000, 1, 5, 5)), Is.False);
        }

        [Test]
        public void CoveringRejectsPartialCells()
        {
            Assert.That(GridSpec.Covering(new AlbersBox(0, 0, 10, 6), 2).Columns, Is.EqualTo(5));
            Assert.Throws<ArgumentException>(() => GridSpec.Covering(new AlbersBox(0, 0, 11, 6), 2));
        }

        [Test]
        public void LocalFrameIsCentredOnTheSite()
        {
            var frame = new LocalFrame(new AlbersPoint(-1188828, 2381976));
            Assert.That(frame.ToLocal(new AlbersPoint(-1188828, 2381976)), Is.EqualTo((0.0, 0.0)));
            Assert.That(frame.ToLocal(new AlbersPoint(-1188728, 2381876)), Is.EqualTo((100.0, -100.0)));
            Assert.That(frame.ToAlbers(100, -100), Is.EqualTo(new AlbersPoint(-1188728, 2381876)));
        }
    }

    public sealed class SiteSquareTests
    {
        static readonly GeoPoint JacksonHole = new GeoPoint(43.593, -110.848);

        [TestCase(2.0)]
        [TestCase(3.7)]
        [TestCase(5.0)]
        public void SitesAreExactSquaresOnS1mPixelEdges(double km)
        {
            var site = SiteSquare.Create(JacksonHole, km);
            var core = site.Core;
            Assert.That(core.Width, Is.EqualTo(km * 1000).Within(1e-9));
            Assert.That(core.Height, Is.EqualTo(km * 1000).Within(1e-9));
            // S1M pixels have whole-metre edges; its 2 m overview has even-metre edges.
            foreach (double edge in new[] { core.West, core.South, core.East, core.North })
                Assert.That(edge % 1, Is.EqualTo(0), "core edges are whole metres");
            var ring = site.Ring;
            foreach (double edge in new[] { ring.West, ring.South, ring.East, ring.North })
                Assert.That(edge % 2, Is.EqualTo(0), "ring edges are even metres");
        }

        [Test]
        public void TheCentreSnapsToTheNearestTwoMetres()
        {
            var clicked = Albers6350.Forward(JacksonHole);
            var site = SiteSquare.Create(clicked, 2);
            Assert.That(site.Centre.DistanceTo(clicked), Is.LessThanOrEqualTo(Math.Sqrt(2) + 1e-9));
            Assert.That(site.Centre, Is.EqualTo(new AlbersPoint(-1188828, 2381976)));
        }

        [TestCase(1.9)]
        [TestCase(5.1)]
        [TestCase(2.05)]
        [TestCase(double.NaN)]
        public void SizesOutsideTwoToFiveKmInTenthsAreRejected(double km)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SiteSquare.Create(JacksonHole, km));
        }

        [Test]
        public void GridsCoverTheCoreAtOneMetreAndTheRingAtTwo()
        {
            var site = SiteSquare.Create(JacksonHole, 5);
            Assert.That(site.CoreGrid.Columns, Is.EqualTo(5000));
            Assert.That(site.CoreGrid.CellCount, Is.EqualTo(25_000_000), "25 million heights (0.3 §4.2)");
            Assert.That(site.RingGrid.Columns, Is.EqualTo(5500), "11 km at 2 m");
            Assert.That(site.RingGrid.Bounds, Is.EqualTo(site.Core.Expand(3000)));
            Assert.That(site.CoreGrid.IsAlignedWith(new GridSpec(-1190000, 2390000, 1, 10000, 10000)), Is.True, "aligned with S1M tile n2390w1190");
        }

        [Test]
        public void TheSiteRecordsItsScaleFactors()
        {
            var s = SiteSquare.Create(JacksonHole, 2).Scale;
            Assert.That(s.Parallel, Is.EqualTo(0.995551).Within(1e-5));
        }
    }

    public sealed class TileGridTests
    {
        [Test]
        public void AFiveKmSiteMakes121Tiles()
        {
            var tiles = TileGrid.For(SiteSquare.Create(new GeoPoint(43.593, -110.848), 5));
            Assert.That((tiles.Columns, tiles.Rows), Is.EqualTo((11, 11)));
            Assert.That(tiles.Count, Is.EqualTo(121), "0.3 §4.3");
        }

        [Test]
        public void CoreTilesAreOneMetreAndTheRestTwo()
        {
            var site = SiteSquare.Create(new GeoPoint(43.593, -110.848), 5);
            var tiles = TileGrid.For(site);
            var core = tiles.All().Where(tiles.IsCore).ToArray();
            Assert.That(core.Length, Is.EqualTo(36), "the 5 km core straddles 6 × 6 tiles of 1,024 m");
            Assert.That(core.All(k => tiles.Resolution(k) == 1025), Is.True);
            Assert.That(tiles.All().Where(k => !tiles.IsCore(k)).All(k => tiles.Resolution(k) == 513), Is.True);
        }

        [Test]
        public void TilesShareEdgesAndStartAtTheRingCorner()
        {
            var site = SiteSquare.Create(new GeoPoint(43.593, -110.848), 2);
            var tiles = TileGrid.For(site);
            var a = tiles.Bounds(new TileKey(0, 0));
            var b = tiles.Bounds(new TileKey(1, 0));
            var below = tiles.Bounds(new TileKey(0, 1));
            Assert.That(a.West, Is.EqualTo(site.Ring.West));
            Assert.That(a.North, Is.EqualTo(site.Ring.North));
            Assert.That(b.West, Is.EqualTo(a.East));
            Assert.That(below.North, Is.EqualTo(a.South));
            Assert.That(tiles.TryTileAt(site.Centre, out var key), Is.True);
            Assert.That(tiles.IsCore(key), Is.True);
        }

        [Test]
        public void TileOrderIsStable()
        {
            var tiles = TileGrid.For(SiteSquare.Create(new GeoPoint(43.593, -110.848), 3));
            var keys = tiles.All().ToArray();
            Assert.That(keys, Is.Ordered);
            Assert.That(keys.Length, Is.EqualTo(tiles.Count));
        }
    }
}
