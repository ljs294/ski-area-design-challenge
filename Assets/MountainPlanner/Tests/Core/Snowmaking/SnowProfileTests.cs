using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// Task 4.15's pipe profile (the pressure layer, the pipe card, the water line's readouts): pressure between a pipe's
    /// graph nodes, worked by hand. A 3,000 ft 8 in main climbs 1 ft in 5 from a 1,000 hp, 600 psi house to one 20 ft stick at
    /// its end, at 10 °F wet bulb (stage 4: 48 gpm).
    /// </summary>
    public sealed class SnowProfileTests
    {
        const ulong Pond = 0x50;

        static (SnowNet Net, SnowPipe Main, SnowSolution Sol) Climb()
        {
            var n = new SnowNet { GroundFt = (x, y) => 0.2 * x }.Water(Pond, 0);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var end = n.Node(SnowNodeKind.Hydrant, 3000, 0);
            var main = n.Pipe(house, end, 8);
            n.Gun(end);
            var g = n.Compile();
            var s = new SnowSolve(g);
            Assert.That(s.Run(SnowNet.Asks(g, 100), n.Level, cold: true), Is.True);
            return (n, main, new SnowSolution(s, -1, false));
        }

        [Test]
        public void PressureHalfwayUpIsTheHeadOverTheGroundThere()
        {
            var (n, main, sol) = Climb();
            // Hand: the house holds 600 psi less its 2 % droop at 48 gpm; half the main's friction (its 3D length) is lost by
            // the middle, where the ground stands 300 ft up.
            double qStar = 3960.0 * 1000 * 0.85 / (600 * 2.31);
            double h0 = 600 * 2.31 * (1 - 0.02 * 48 / qStar);
            double length = Math.Sqrt(3000.0 * 3000 + 600.0 * 600);
            double friction = 10.44 * length * Math.Pow(48, 1.852) / (Math.Pow(120, 1.852) * Math.Pow(8, 4.87));
            double handMid = (h0 - friction / 2 - 300) / 2.31;
            double handEnd = (h0 - friction - 600) / 2.31;

            double total = SnowGeometry.Length(main.Route);
            var at = new[] { 0, total / 2, total };
            var psi = new float[3];
            SnowProfile.PressureAlong(sol, main, new SnowNet.Ground(n.GroundFt), at, psi);
            TestContext.Progress.WriteLine($"profile: {psi[0]:F2} / {psi[1]:F2} / {psi[2]:F2} psi; hand {h0 / 2.31:F2} / {handMid:F2} / {handEnd:F2}");
            Assert.That(psi[0], Is.EqualTo(h0 / 2.31).Within(0.01));
            Assert.That(psi[1], Is.EqualTo(handMid).Within(0.05), "a climb shows halfway, where no hydrant stands");
            Assert.That(psi[2], Is.EqualTo(handEnd).Within(0.01));
        }

        [Test]
        public void TheStretchCarriesTheGunsFlowUpTheRoute()
        {
            var (_, main, sol) = Climb();
            var st = SnowProfile.Stretches(sol, main);
            Assert.That(st.Count, Is.EqualTo(1));
            Assert.That(st[0].Gpm, Is.EqualTo(48).Within(0.01), "positive: along the route, house to gun");
            Assert.That(st[0].VelocityFtS, Is.EqualTo(48 / (2.448 * 64)).Within(1e-6));
            Assert.That(st[0].FromPsi - st[0].ToPsi, Is.EqualTo(st[0].FrictionPsi + 600 / 2.31).Within(0.01), "friction plus the 600 ft climb");
        }

        [Test]
        public void NoSolveNoPressure()
        {
            var (n, main, _) = Climb();
            var psi = new float[1];
            SnowProfile.PressureAlong(null, main, new SnowNet.Ground(n.GroundFt), new double[] { 0 }, psi);
            Assert.That(float.IsNaN(psi[0]), Is.True);
            Assert.That(SnowProfile.Stretches(null, main), Is.Empty);
        }
    }
}
