using System;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 13: the picker's hand-off to the download flow (task 14). Engine-free.
    public sealed class PickedSiteTests
    {
        static readonly SiteEstimate Estimate = new SiteEstimate(100, new double[] { 1, 0, 0, 0 }, 85_000_000, 40, false);

        [TestCase(2.0)]
        [TestCase(4.3)]
        [TestCase(5.0)]
        public void ItsCentreRebuildsTheIdenticalSquare(double km)
        {
            var clicked = Albers6350.Forward(new GeoPoint(43.593, -110.848)) + (0.7, -1.3);
            var site = PickedSite.Create("  Jackson Hole  ", clicked, km, false, Estimate);
            Assert.That(site.Name, Is.EqualTo("Jackson Hole"));
            var again = SiteSquare.Create(site.Centre, site.SizeKm);
            Assert.That(again.Core, Is.EqualTo(site.Square.Core), "what the picker shows is what downloads");
            Assert.That(again.Centre, Is.EqualTo(site.Square.Centre));
            Assert.That(site.SizeKm, Is.EqualTo(site.Square.SizeKm));
            Assert.That(site.Centre, Is.EqualTo(Albers6350.Inverse(site.Square.Centre)), "the snapped centre, not the raw click");
        }

        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void ASiteNeedsAName(string name)
        {
            Assert.Throws<ArgumentException>(() => PickedSite.Create(name, new AlbersPoint(0, 2000000), 2, false, Estimate));
        }

        [Test]
        public void TheEstimateKeepsItsOwnCopyOfTheShares()
        {
            var shares = new double[] { 0.25, 0.25, 0.25, 0.25 };
            var e = new SiteEstimate(71, shares, 1, 1, true);
            shares[0] = 1;
            Assert.That(e.Share(TerrainSource.S1m), Is.EqualTo(0.25));
        }
    }
}
