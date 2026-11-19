using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>SG-03 v1: the gun's footprint stencil, on 1 m cells. A 30 ft stick throws 125 ft (38.1 m).</summary>
    public sealed class GunStencilTests
    {
        static readonly GunSpec Stick30 = GunCatalog.Spec(GunModel.Stick30);
        const double FtPerCell = 1000 / 304.8;

        static (double x, double y) Centroid(GunStencil s)
        {
            double x = 0, y = 0;
            for (int k = 0; k < s.Count; k++) { x += s.Dx[k] * (double)s.Weight[k]; y += s.Dy[k] * (double)s.Weight[k]; }
            return (x / GunStencil.Total * FtPerCell, y / GunStencil.Total * FtPerCell);
        }

        static double Spread(GunStencil s, Func<int, double> along)
        {
            var (cx, cy) = Centroid(s);
            double m = 0;
            for (int k = 0; k < s.Count; k++) m += along(k) * along(k) * s.Weight[k];
            return Math.Sqrt(m / GunStencil.Total);
        }

        [Test]
        public void WeightsSumToExactlyOne()
        {
            foreach (var spec in GunCatalog.Models)
                foreach (int aim in new[] { 0, 4_500, 13_300, 27_000 })
                    foreach (int wind in new[] { 0, 7, 24 })
                    {
                        var s = GunStencil.Build(spec, aim, wind, 9_000);
                        Assert.That(s.Stopped, Is.False);
                        Assert.That(s.Weight.Sum(), Is.EqualTo(GunStencil.Total), $"{spec.Name} aim {aim} wind {wind}");
                        Assert.That(s.Weight.All(w => w >= 0), Is.True);
                    }
        }

        [Test]
        public void TheSnowLandsHalfTheThrowDownTheAim()
        {
            var north = GunStencil.Build(Stick30, 0, 0, 0);
            var (x, y) = Centroid(north);
            Assert.That(y, Is.EqualTo(Stick30.ThrowFt / 2.0).Within(2.0), "half the 125 ft throw north");
            Assert.That(x, Is.EqualTo(0).Within(1.0));
            var east = Centroid(GunStencil.Build(Stick30, 9_000, 0, 0));
            Assert.That(east.x, Is.EqualTo(Stick30.ThrowFt / 2.0).Within(2.0));
            Assert.That(east.y, Is.EqualTo(0).Within(1.0));
        }

        [Test]
        public void LongerDownTheAimThanAcross()
        {
            var s = GunStencil.Build(Stick30, 0, 0, 0);
            var (cx, cy) = Centroid(s);
            double along = Spread(s, k => s.Dy[k] * FtPerCell - cy), across = Spread(s, k => s.Dx[k] * FtPerCell - cx);
            Assert.That(along / across, Is.EqualTo(3).Within(0.4), "length = throw, width = throw ÷ 3");
        }

        [Test]
        public void TheWindBendsItDownwindAndWidensIt()
        {
            var calm = GunStencil.Build(Stick30, 0, 0, 0);
            var windy = GunStencil.Build(Stick30, 0, 20, 9_000);   // 20 mph blowing east
            var (x, y) = Centroid(windy);
            Assert.That(x, Is.EqualTo(20 * 2).Within(2.0), "the centre moves wind × 2 ft downwind");
            Assert.That(y, Is.EqualTo(Stick30.ThrowFt / 2.0).Within(2.0));
            var (cx0, _) = Centroid(calm);
            double w0 = Spread(calm, k => calm.Dx[k] * FtPerCell - cx0), w1 = Spread(windy, k => windy.Dx[k] * FtPerCell - x);
            Assert.That(w1 / w0, Is.EqualTo(1.4).Within(0.08), "20 % wider per 10 mph");
        }

        [Test]
        public void AboveTheWindLimitTheGunStops()
        {
            Assert.That(GunStencil.Build(Stick30, 0, 25, 0).Stopped, Is.False);
            var s = GunStencil.Build(Stick30, 0, 26, 0);
            Assert.That(s.Stopped, Is.True);
            Assert.That(s.Count, Is.Zero);
        }

        [Test]
        public void TheKeyBucketsTheWind()
        {
            Assert.That(GunStencil.Key(Stick30, 0, 11, 9_000), Is.EqualTo(GunStencil.Key(Stick30, 0, 14, 9_400)), "same 10–14 mph bucket and 90° ± 5°");
            Assert.That(GunStencil.Key(Stick30, 0, 11, 9_000), Is.Not.EqualTo(GunStencil.Key(Stick30, 0, 15, 9_000)));
            Assert.That(GunStencil.Key(Stick30, 0, 3, 0), Is.EqualTo(GunStencil.Key(Stick30, 0, 0, 27_000)), "calm: direction doesn't matter");
            Assert.That(GunStencil.Key(Stick30, 100, 0, 0), Is.Not.EqualTo(GunStencil.Key(Stick30, 0, 0, 0)));
            Assert.That(GunStencil.Key(GunCatalog.Spec(GunModel.Ground), 0, 0, 0), Is.Not.EqualTo(GunStencil.Key(Stick30, 0, 0, 0)));
            // Equal keys build equal stencils.
            var a = GunStencil.Build(Stick30, 0, 11, 9_000);
            var b = GunStencil.Build(Stick30, 0, 14, 9_400);
            Assert.That(a.Weight, Is.EqualTo(b.Weight));
            Assert.That(a.Dx, Is.EqualTo(b.Dx));
        }
    }
}
