using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// Task 4.15's exit test: the solver matches hand-checked networks (HY-04…06). Every expected number below was worked
    /// independently of the solver: Hazen–Williams by hand, the parallel split and the two sources by bisection on equal
    /// heads, the loop by Hardy Cross, each to three decimals (plan v2's adversarial review, M1–M9).
    /// </summary>
    public sealed class HydraulicSolverTests
    {
        const ulong Pond = 0x40, Lake = 0x20;

        /// <summary>HY-04's worked example: 1,000 gpm through 1,000 ft of 8 in loses 21.2 ft at 10.44 (the SI 10.67 gave 21.7).</summary>
        [Test]
        public void HazenWilliamsWorkedExample()
        {
            Assert.That(HydraulicGraph.FrictionFt(1000, 1000, 8), Is.EqualTo(21.18).Within(0.01));
            // Cross-checked in SI units: 10.67 · L(m) · Q(m³/s)^1.852 / (C^1.852 · d(m)^4.87), back in feet.
            double q = 1000 * 3.785411784e-3 / 60, l = 304.8, d = 8 * 0.0254;
            double si = 10.67 * l * Math.Pow(q, 1.852) / (Math.Pow(120, 1.852) * Math.Pow(d, 4.87)) / 0.3048;
            Assert.That(HydraulicGraph.FrictionFt(1000, 1000, 8), Is.EqualTo(si).Within(0.05));
            Assert.That(HydraulicGraph.VelocityFtS(1000, 8), Is.EqualTo(6.38).Within(0.01));
        }

        /// <summary>Gravity through one pipe: the head at the hydrant is the pond's level less the pipe's friction.</summary>
        [Test]
        public void GravityOnePipe()
        {
            var n = new SnowNet().Water(Pond, 1000);
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, Pond);
            var h = n.Node(SnowNodeKind.Hydrant, 1000, 0);
            n.Pipe(intake, h, 8);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 1000);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(s.H[g.NodeOf(h.Id)], Is.EqualTo(1000 - 21.18).Within(0.02));
            Assert.That(s.Psi(g.NodeOf(h.Id)), Is.EqualTo((1000 - 21.18) / 2.31).Within(0.01));
        }

        /// <summary>Series: 1,000 ft of 8 in then 500 ft of 6 in at 500 gpm lose 17.778 ft together.</summary>
        [Test]
        public void Series()
        {
            var n = new SnowNet().Water(Pond, 1000);
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, Pond);
            var j = n.Node(SnowNodeKind.Junction, 1000, 0);
            var h = n.Node(SnowNodeKind.Hydrant, 1500, 0);
            n.Pipe(intake, j, 8);
            n.Pipe(j, h, 6);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 500);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(1000 - s.H[g.NodeOf(h.Id)], Is.EqualTo(17.778).Within(0.02));
        }

        /// <summary>Parallel: 1,000 gpm split between 1,000 ft of 8 in and of 6 in so both lose the same head: 680.590 and 319.410.</summary>
        [Test]
        public void Parallel()
        {
            var n = new SnowNet().Water(Pond, 1000);
            var intake = n.Node(SnowNodeKind.Intake, -10, 0, Pond);
            var j = n.Node(SnowNodeKind.Junction, 0, 0);
            var h = n.Node(SnowNodeKind.Hydrant, 1000, 0);
            n.Pipe(intake, j, 24);
            var p8 = n.Pipe(j, h, 8);
            var p6 = n.Pipe(j, h, 6);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 1000);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(Flow(g, s, p8.Id), Is.EqualTo(680.590).Within(0.1));
            Assert.That(Flow(g, s, p6.Id), Is.EqualTo(319.410).Within(0.1));
        }

        /// <summary>
        /// A loop (HY-02, SR8: MapLibre drew loops but solved trees). J1–J2 1,000 ft of 8 in, J2–J3 800 ft of 6 in, J1–J3 1,800 ft
        /// of 8 in, 400 gpm out at J2 and 600 at J3. Hardy Cross by hand: 545.730 J1→J2, 145.730 J2→J3, 454.270 J1→J3; heads
        /// 999.706, 992.805 and 990.862 ft behind 100 ft of 12 in.
        /// </summary>
        [Test]
        public void LoopMatchesHardyCross()
        {
            var n = new SnowNet().Water(Pond, 1000);
            var intake = n.Node(SnowNodeKind.Intake, -100, 0, Pond);
            var j1 = n.Node(SnowNodeKind.Junction, 0, 0);
            var j2 = n.Node(SnowNodeKind.Junction, 1000, 0);
            var j3 = n.Node(SnowNodeKind.Junction, 1000, 800);
            n.Pipe(intake, j1, 12);
            var p12 = n.Pipe(j1, j2, 8);
            var p23 = n.Pipe(j2, j3, 6);
            var p13 = n.Pipe(j1, j3, 8, (0, 800));
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(j2.Id), 400);
            s.AddDemand(g.NodeOf(j3.Id), 600);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(Flow(g, s, p12.Id), Is.EqualTo(545.730).Within(0.1));
            Assert.That(Flow(g, s, p23.Id), Is.EqualTo(145.730).Within(0.1));
            Assert.That(Flow(g, s, p13.Id), Is.EqualTo(454.270).Within(0.1));
            Assert.That(s.H[g.NodeOf(j1.Id)], Is.EqualTo(999.706).Within(0.02));
            Assert.That(s.H[g.NodeOf(j2.Id)], Is.EqualTo(992.805).Within(0.02));
            Assert.That(s.H[g.NodeOf(j3.Id)], Is.EqualTo(990.862).Within(0.02));
        }

        /// <summary>Two sources at 1,000 and 990 ft feed one hydrant through 1,000 ft of 8 in each: 681.344 and 118.656 gpm.</summary>
        [Test]
        public void TwoSources()
        {
            var n = new SnowNet().Water(Pond, 1000).Water(Lake, 990);
            var a = n.Node(SnowNodeKind.Intake, -1000, 0, Pond);
            var b = n.Node(SnowNodeKind.Intake, 1000, 0, Lake);
            var h = n.Node(SnowNodeKind.Hydrant, 0, 0);
            var pa = n.Pipe(a, h, 8);
            var pb = n.Pipe(b, h, 8);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 800);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(Flow(g, s, pa.Id), Is.EqualTo(681.344).Within(0.1));
            Assert.That(Flow(g, s, pb.Id), Is.EqualTo(118.656).Within(0.1));
            Assert.That(s.H[g.NodeOf(h.Id)], Is.EqualTo(989.591).Within(0.02));
        }

        /// <summary>
        /// HY-05 v2: a 1,000 hp, 85 % house set to add 600 psi (1,386 ft) holds it, with a 2 % droop, up to its power limit of
        /// 3,960·850/1,386 = 2,428.6 gpm: at 1,000 gpm it adds 1,374.586 ft; past the limit it runs on power: at 3,000 gpm
        /// 0.98 · 3,366,000 / 3,000 = 1,099.560 ft.
        /// </summary>
        [TestCase(1000, 1374.586)]
        [TestCase(3000, 1099.560)]
        public void PumpHoldsItsBoostThenItsPower(double gpm, double gainFt)
        {
            var n = new SnowNet().Water(Pond, 0);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var h = n.Node(SnowNodeKind.Hydrant, 100, 0);
            n.Pipe(house, h, 24);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), gpm);
            Assert.That(s.Solve(false), Is.True);
            double friction = HydraulicGraph.FrictionFt(100, gpm, 24);
            Assert.That(s.H[g.NodeOf(h.Id)], Is.EqualTo(gainFt - friction).Within(0.05));
        }

        /// <summary>
        /// Plan v2 Q2: pumps have check valves. A pond at 1,100 ft feeds a hydrant; an off pump between the hydrant and an outlet
        /// into a pond at 1,000 ft faces the hydrant, so water would run back through it: its valve holds, nothing flows.
        /// </summary>
        [Test]
        public void OffPumpCheckValveHolds()
        {
            const ulong High = 0x41, Low = 0x42;
            var n = new SnowNet().Water(High, 1100).Water(Low, 1000);
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, High);
            var h = n.Node(SnowNodeKind.Hydrant, 1000, 0);
            var outlet = n.Node(SnowNodeKind.Outlet, 2000, 0, Low);
            n.Pipe(intake, h, 8);
            var line = n.Pipe(outlet, h, 8);
            n.Tap(SnowNodeKind.Pump, line, 2500 - 1000, 0, forward: true, pump: PumpSpec.Booster.WithOn(false));
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 300);
            Assert.That(s.Solve(false), Is.True);
            Assert.That(Flow(g, s, line.Id), Is.EqualTo(0).Within(0.05), "no water runs back through the off pump");
            Assert.That(s.H[g.NodeOf(h.Id)], Is.EqualTo(1100 - HydraulicGraph.FrictionFt(1000, 300, 8)).Within(0.02));
        }

        /// <summary>A warm solve from the last answer lands on the same answer as a cold one, in fewer iterations.</summary>
        [Test]
        public void WarmStartMatchesCold()
        {
            var n = new SnowNet().Water(Pond, 1000);
            var intake = n.Node(SnowNodeKind.Intake, -100, 0, Pond);
            var j1 = n.Node(SnowNodeKind.Junction, 0, 0);
            var j2 = n.Node(SnowNodeKind.Junction, 1000, 0);
            n.Pipe(intake, j1, 12);
            n.Pipe(j1, j2, 8);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(j2.Id), 400);
            Assert.That(s.Solve(false), Is.True);
            int cold = s.Iterations;
            s.ClearDemands();
            s.AddDemand(g.NodeOf(j2.Id), 420);
            Assert.That(s.Solve(true), Is.True);
            double warm = s.H[g.NodeOf(j2.Id)];
            Assert.That(s.Iterations, Is.LessThanOrEqualTo(Math.Max(cold, 2)));
            var c = n.Solver(g);
            c.AddDemand(g.NodeOf(j2.Id), 420);
            c.Solve(false);
            Assert.That(warm, Is.EqualTo(c.H[g.NodeOf(j2.Id)]).Within(0.01));
        }

        /// <summary>SH-04 / SI-08: the same network given in any order solves to the same bytes.</summary>
        [Test]
        public void OrderDoesNotMatter()
        {
            double Solve(bool reverse)
            {
                var n = new SnowNet().Water(Pond, 1000);
                var intake = n.Node(SnowNodeKind.Intake, -100, 0, Pond);
                var j1 = n.Node(SnowNodeKind.Junction, 0, 0);
                var j2 = n.Node(SnowNodeKind.Junction, 1000, 0);
                var j3 = n.Node(SnowNodeKind.Junction, 1000, 800);
                n.Pipe(intake, j1, 12);
                n.Pipe(j1, j2, 8);
                n.Pipe(j2, j3, 6);
                n.Pipe(j1, j3, 8, (0, 800));
                if (reverse) { n.Nodes.Reverse(); n.Pipes.Reverse(); }
                var g = n.Compile();
                var s = n.Solver(g);
                s.AddDemand(g.NodeOf(j2.Id), 400);
                s.AddDemand(g.NodeOf(j3.Id), 600);
                s.Solve(false);
                return s.H[g.NodeOf(j3.Id)];
            }
            Assert.That(BitConverter.DoubleToInt64Bits(Solve(true)), Is.EqualTo(BitConverter.DoubleToInt64Bits(Solve(false))));
        }

        /// <summary>
        /// The review chart's line, worked by hand: a pump house on a pond at 0 ft holds 600 psi (1,386 ft) into an 8 in main
        /// climbing 600 ft over 3,000 ft (a 20 % grade), with a stage-4 gun (48 gpm) on a hydrant every 300 ft: ten guns,
        /// 480 gpm. By hand: the pump adds 1,386 × (1 − 0.02 × 480 ÷ 2,428.6) = 1,380.52 ft (it is under its power limit,
        /// 3,960 × 1,000 × 0.85 ÷ 1,386 = 2,428.6 gpm); each 300 ft reach carries the guns past it, 48 × (11 − i) gpm, and
        /// loses 10.44 × L × Q^1.852 ÷ (120^1.852 × 8^4.87) over the pipe's length on the slope, L = √(300² + 60²) = 305.9 ft
        /// (the pipe follows the ground); psi = (head − 0.2 x) ÷ 2.31 at grade.
        /// </summary>
        [Test]
        public void PressureAlongAClimbingMainMatchesTheHandCalculation()
        {
            var n = new SnowNet { GroundFt = (x, y) => 0.2 * x }.Water(Pond, 0);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var end = n.Node(SnowNodeKind.Hydrant, 3000, 0);
            var main = n.Pipe(house, end, 8);
            var hydrants = new System.Collections.Generic.List<SnowNode>();
            for (int i = 1; i < 10; i++) hydrants.Add(n.Tap(SnowNodeKind.Hydrant, main, 300 * i, 0));
            hydrants.Add(end);
            foreach (var h in hydrants) n.Gun(h);
            var g = n.Compile();
            var s = n.Solver(g);
            foreach (var h in hydrants) s.AddDemand(g.NodeOf(h.Id), 48);
            Assert.That(s.Solve(false), Is.True);

            double qStar = 3960.0 * 1000 * 0.85 / (600 * 2.31);
            double head = 600 * 2.31 * (1 - 0.02 * 480 / qStar);
            Assert.That(head, Is.EqualTo(1380.52).Within(0.01));
            TestContext.Progress.WriteLine("CHART x_ft,ground_ft,solver_psi,hand_psi");
            TestContext.Progress.WriteLine($"CHART 0,0,{s.GradePsi(g.NodeOf(house.Id)):F3},{head / 2.31:F3}");
            for (int i = 1; i <= 10; i++)
            {
                double q = 48 * (11 - i);
                head -= 10.44 * Math.Sqrt(300 * 300 + 60 * 60) * Math.Pow(q, 1.852) / (Math.Pow(120, 1.852) * Math.Pow(8, 4.87));
                double hand = (head - 0.2 * 300 * i) / 2.31;
                double solved = s.GradePsi(g.NodeOf(hydrants[i - 1].Id));
                TestContext.Progress.WriteLine($"CHART {300 * i},{0.2 * 300 * i:F0},{solved:F3},{hand:F3}");
                Assert.That(solved, Is.EqualTo(hand).Within(0.01), $"hydrant {i} at {300 * i} ft");
            }
        }

        /// <summary>The flow along a design pipe (from its first point to its last), gpm, read on its first link.</summary>
        internal static double Flow(HydraulicGraph g, HydraulicSolver s, SnowId pipe)
        {
            for (int k = 0; k < g.LinkCount; k++)
            {
                if (g.LinkKind[k] != HLinkKind.Pipe || g.LinkDesign[k] != pipe) continue;
                return s.Q[k];
            }
            throw new ArgumentException("no such pipe");
        }
    }
}
