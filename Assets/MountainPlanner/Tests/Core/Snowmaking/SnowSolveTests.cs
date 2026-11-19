using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// Task 4.15: the job around the solve (HY-07, HY-08, SG-06, SG-07) and HY-09's diagnostics, one test each (1–20). Wet
    /// bulbs are tenths of a degree F: 100 is 10 °F (stage 4, 48 gpm, 16 cfm), 200 is 20 °F (stage 2, 28 gpm, 56 cfm).
    /// </summary>
    public sealed class SnowSolveTests
    {
        const ulong Pond = 0x40, Lake = 0x20;
        const int Cold = 100, Mild = 200, Warm = 320;

        /// <summary>A pump house on a pond at 0 ft feeding a straight 8 in line with <paramref name="hydrants"/> hydrants 100 ft apart.</summary>
        static (SnowNet n, SnowNode house, SnowPipe line, List<SnowNode> hydrants) Line(int hydrants, int diameter = 8, PumpSpec? pump = null, int cfm = SnowRules.DefaultCompressorCfm)
        {
            var n = new SnowNet().Water(Pond, 0);
            var house = n.House(0, 0, Pond, pump ?? PumpSpec.House, cfm);
            var end = n.Node(SnowNodeKind.Junction, 100 * (hydrants + 1), 0);
            var line = n.Pipe(house, end, diameter);
            var hs = new List<SnowNode>();
            for (int i = 1; i <= hydrants; i++) hs.Add(n.Tap(SnowNodeKind.Hydrant, line, 100 * i, 0));
            foreach (var h in hs) n.Gun(h);
            return (n, house, line, hs);
        }

        static SnowSolve Run(SnowNet n, int wetBulb, out bool ok)
        {
            var g = n.Compile();
            var s = new SnowSolve(g);
            ok = s.Run(SnowNet.Asks(g, wetBulb), n.Level);
            return s;
        }

        static bool Has(SnowSolve s, int code) => s.Diagnostics.Any(d => d.Code == code);

        [Test]
        public void TwentyGunsRunOnOneHouse()
        {
            var (n, _, _, _) = Line(20);
            var s = Run(n, Cold, out bool ok);
            Assert.That(ok, Is.True);
            Assert.That(s.GunStates, Is.All.EqualTo(GunState.Running));
            Assert.That(s.DemandGpm, Is.EqualTo(20 * 48));
            Assert.That(s.GunPsi.Min(), Is.GreaterThan(GunCatalog.MinPsi));
            Assert.That(s.AirDrawn[0], Is.EqualTo(20 * 16));
        }

        /// <summary>
        /// SG-07 / HY-08: a 4 in line 2,100 ft long can't hold 200 psi for twenty stage-5 guns. Guns shed, the furthest (lowest
        /// pressure) first, in at most ten re-solves, until every gun still running has 200 psi.
        /// </summary>
        [Test]
        public void ShortGunsShedFurthestFirst()
        {
            var (n, _, _, hs) = Line(20, diameter: 4, pump: new PumpSpec(1000, 850, 300, true));
            var s = Run(n, 50, out bool ok);
            Assert.That(ok, Is.True);
            int running = s.GunStates.Count(x => x == GunState.Running), shed = s.GunStates.Count(x => x == GunState.ShortOfPressure);
            Assert.That(running, Is.GreaterThan(0));
            Assert.That(shed, Is.GreaterThan(0));
            Assert.That(s.Resolves, Is.LessThanOrEqualTo(SnowSolve.DefaultMaxResolves));
            for (int i = 0; i < s.GunStates.Length; i++)
                if (s.GunStates[i] == GunState.Running) Assert.That(s.GunPsi[i], Is.GreaterThanOrEqualTo(GunCatalog.MinPsi - 1e-6));
            // The guns shed are the far ones: every running gun is nearer the house than every shed one.
            var g = s.Graph;
            double nearestShed = Enumerable.Range(0, g.Guns.Length).Where(i => s.GunStates[i] == GunState.ShortOfPressure).Min(i => g.FromWaterFt[g.GunNode[i]]);
            double furthestRun = Enumerable.Range(0, g.Guns.Length).Where(i => s.GunStates[i] == GunState.Running).Max(i => g.FromWaterFt[g.GunNode[i]]);
            Assert.That(furthestRun, Is.LessThan(nearestShed));
            Assert.That(Has(s, 12), Is.True);
        }

        /// <summary>SG-06 v2: a 500 cfm compressor runs eight guns at 56 cfm (stage 2); the rest drop for air (HY-09 18).</summary>
        [Test]
        public void AirLimitsTheGuns()
        {
            var (n, _, _, _) = Line(20, cfm: 500);
            var s = Run(n, Mild, out bool ok);
            Assert.That(ok, Is.True);
            Assert.That(s.GunStates.Count(x => x == GunState.Running), Is.EqualTo(8));
            Assert.That(s.GunStates.Count(x => x == GunState.ShortOfAir), Is.EqualTo(12));
            Assert.That(s.AirDrawn[0], Is.EqualTo(8 * 56));
            Assert.That(Has(s, 18), Is.True);
        }

        /// <summary>HY-07: a pump that would push the line over its 1,000 psi class throttles to it.</summary>
        [Test]
        public void PumpThrottlesAtTheClass()
        {
            var (n, _, _, _) = Line(2, pump: new PumpSpec(2000, 850, 1400, true));
            var s = Run(n, Cold, out bool ok);
            Assert.That(ok, Is.True);
            var g = s.Graph;
            for (int i = 0; i < g.NodeCount; i++)
                if (g.Unknown[i] >= 0) Assert.That(s.Solver.Psi(i), Is.LessThanOrEqualTo(SnowSolve.DefaultPipeClassPsi + 1));
            Assert.That(s.PumpStates.Contains(PumpState.Throttled), Is.True);
            Assert.That(Has(s, 13), Is.True);
        }

        // ---------------- HY-09, one test per diagnostic ----------------

        [Test]
        public void D01_NoSource()
        {
            var n = new SnowNet();
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, 0x99);   // a pond the design no longer has
            var h = n.Node(SnowNodeKind.Hydrant, 100, 0);
            n.Pipe(intake, h, 8);
            n.Gun(h);
            Assert.That(n.Compile().Diagnostics.Any(d => d.Code == 1), Is.True);
        }

        [Test]
        public void D02_GunNotOnAHydrant()
        {
            var (n, _, _, _) = Line(1);
            n.Guns.Add(new SnowGun(n.Id(), GunModel.Ground, SnowNet.Ft(500, 500), SnowId.None, 0, GunOverride.On));
            var s = Run(n, Cold, out _);
            Assert.That(Has(s, 2), Is.True);
            Assert.That(s.GunStates.Last(), Is.EqualTo(GunState.NoWater));
        }

        [Test]
        public void D03_HydrantOnNoPipe()
        {
            var (n, _, _, _) = Line(1);
            var lone = n.Node(SnowNodeKind.Hydrant, 900, 900);
            n.Gun(lone);
            Assert.That(n.Compile().Diagnostics.Any(d => d.Code == 3), Is.True);
        }

        [Test]
        public void D04_NoRoute()
        {
            var (n, _, _, _) = Line(1);
            var a = n.Node(SnowNodeKind.Junction, 0, 900);
            var b = n.Node(SnowNodeKind.Hydrant, 300, 900);
            n.Pipe(a, b, 8);
            n.Gun(b);
            var s = Run(n, Cold, out _);
            Assert.That(Has(s, 4), Is.True);
            Assert.That(s.GunStates.Last(), Is.EqualTo(GunState.NoWater));
        }

        /// <summary>HY-09 5 and 6 (a pump end with no direction; a pump without an inlet and an outlet) are made impossible: a pump is
        /// a tap on one pipe, so it always has two sides and a direction, and nothing else may end on it.</summary>
        [Test]
        public void D05_D06_PumpsAlwaysHaveTwoSidesAndADirection()
        {
            var doc = DesignDocument.Empty(1);
            var line = new AddSnowPipe(0, "", 8, new[] { SnowNet.Ft(0, 0), SnowNet.Ft(1000, 0) }, SnowEnd.Free(SnowNet.Ft(0, 0)), SnowEnd.Free(SnowNet.Ft(1000, 0)));
            doc = line.Apply(doc).Document;
            var pipe = doc.Snow.Pipes[0];
            doc = new AddSnowTap(0, SnowNodeKind.Pump, pipe.Id, SnowNet.Ft(500, 0)).Apply(doc).Document;
            var pump = doc.Snow.Nodes.Single(x => x.Kind == SnowNodeKind.Pump);
            var onPump = new AddSnowPipe(0, "", 8, new[] { pump.At, SnowNet.Ft(500, 500) }, SnowEnd.OnNode(pump), SnowEnd.Free(SnowNet.Ft(500, 500))).Apply(doc);
            Assert.That(onPump.Accepted, Is.False);
            Assert.That(onPump.Reason, Does.Contain("not to a pump"));
        }

        [Test]
        public void D07_PumpFacesAway()
        {
            var n = new SnowNet().Water(Pond, 0);
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, Pond);
            var h = n.Node(SnowNodeKind.Hydrant, 1000, 0);
            var line = n.Pipe(intake, h, 8);
            n.Tap(SnowNodeKind.Pump, line, 500, 0, forward: false, pump: PumpSpec.Booster);
            n.Gun(h);
            var g = n.Compile();
            Assert.That(g.Diagnostics.Any(d => d.Code == 7), Is.True);
        }

        [Test]
        public void D08_UnresolvedElevation()
        {
            var (n, _, _, _) = Line(1);
            var g = HydraulicGraph.Compile(n.Network(), new NoGround(), 0, 25_000, s => 0);
            Assert.That(g.Diagnostics.Any(d => d.Code == 8), Is.True);
        }

        [Test]
        public void D09_BadPumpRefused()
        {
            Assert.That(SnowRules.PumpProblem(new PumpSpec(0, 850, 300, true)), Is.Not.Null);
            Assert.That(SnowRules.PumpProblem(new PumpSpec(100, 0, 300, true)), Is.Not.Null);
            Assert.That(SnowRules.PumpProblem(new PumpSpec(100, 1200, 300, true)), Is.Not.Null);
            Assert.That(SnowRules.PumpProblem(PumpSpec.House), Is.Null);
        }

        /// <summary>HY-09 10: a network no solution satisfies (a gun's demand behind a closed valve, held at full) fails and says so,
        /// without throwing. Here forced by a demand far beyond any pipe: the line search stalls.</summary>
        [Test]
        public void D10_NotConverged()
        {
            var n = new SnowNet().Water(Pond, 0);
            var intake = n.Node(SnowNodeKind.Intake, 0, 0, Pond);
            var h = n.Node(SnowNodeKind.Hydrant, 1000, 0);
            n.Pipe(intake, h, 4);
            var g = n.Compile();
            var s = n.Solver(g);
            s.AddDemand(g.NodeOf(h.Id), 1e12);
            Assert.That(s.Solve(false), Is.False);
        }

        [Test]
        public void D11_PumpNoForwardFlow()
        {
            // A booster pushing toward a pond that stands far above what it can add: its check valve shuts.
            const ulong High = 0x41;
            var n = new SnowNet().Water(Pond, 0).Water(High, 2000);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var h = n.Node(SnowNodeKind.Hydrant, 500, 0);
            var outlet = n.Node(SnowNodeKind.Outlet, 1500, 0, High);
            n.Pipe(house, h, 8);
            var up = n.Pipe(h, outlet, 8);
            n.Tap(SnowNodeKind.Pump, up, 1000, 0, forward: true, pump: new PumpSpec(50, 850, 50, true));
            n.Gun(h);
            var s = Run(n, Cold, out bool ok);
            Assert.That(ok, Is.True);
            Assert.That(Has(s, 11), Is.True);
        }

        [Test]
        public void D12_ShortOfPressure() => Assert.That(Has(RunShort(), 12), Is.True);

        static SnowSolve RunShort()
        {
            var (n, _, _, _) = Line(20, diameter: 4, pump: new PumpSpec(1000, 850, 300, true));
            return Run(n, 50, out _);
        }

        [Test]
        public void D13_OverClass()
        {
            var (n, _, _, _) = Line(2, pump: new PumpSpec(2000, 850, 1400, true));
            Assert.That(Has(Run(n, Cold, out _), 13), Is.True);
        }

        [Test]
        public void D14_VelocityHigh()
        {
            // Twenty stage-5 guns (1,160 gpm) down a 4 in line run at about 30 ft/s near the house.
            var (n, _, _, _) = Line(20, diameter: 4);
            var s = Run(n, 50, out _);
            Assert.That(Has(s, 14), Is.True);
        }

        [Test]
        public void D15_AirLock()
        {
            // A 300 psi house (693 ft of head) pushing over a 900 ft ridge 1,000 ft out to a hydrant back at 0 ft beyond it: the
            // ridge's high point stands above the hydraulic grade, so the pipe there would be below atmospheric pressure.
            var n = new SnowNet().Water(Pond, 0);
            var house = n.House(0, 0, Pond, new PumpSpec(1000, 850, 300, true));
            var h = n.Node(SnowNodeKind.Hydrant, 2000, 0);
            n.Pipe(house, h, 8);
            n.Gun(h);
            n.GroundFt = (x, y) => Math.Max(0, 900 - Math.Abs(x - 1000) * 1.5);
            var s = Run(n, Cold, out _);
            Assert.That(Has(s, 15), Is.True);
        }

        [Test]
        public void D16_SourceAtMinimumPool()
        {
            var (n, _, _, _) = Line(2);
            n.Floors[Pond] = true;
            var s = Run(n, Cold, out _);
            Assert.That(Has(s, 16), Is.True);
            Assert.That(s.GunStates, Is.All.EqualTo(GunState.NoWater));
        }

        [Test]
        public void D17_LakeAtDrawdownLimit()
        {
            var n = new SnowNet().Water(Lake, 0);
            var house = n.House(0, 0, Lake, PumpSpec.House);
            var h = n.Node(SnowNodeKind.Hydrant, 200, 0);
            n.Pipe(house, h, 8);
            n.Gun(h);
            var g = n.Compile();
            var s = new SnowSolve(g);
            s.Run(SnowNet.Asks(g, Cold), src => new SourceLevel(0, false, false, true));
            Assert.That(s.Diagnostics.Any(d => d.Code == 17), Is.True);
        }

        [Test]
        public void D18_ShortOfAir() => Assert.That(Has(Run(Line(20, cfm: 500).n, Mild, out _), 18), Is.True);

        [Test]
        public void D19_ValveIsolates()
        {
            var (n, _, line, _) = Line(2);
            int i = n.Pipes.IndexOf(line);
            n.Pipes[i] = line.WithValves(true, false);
            var s = Run(n, Cold, out _);
            Assert.That(Has(s, 19), Is.True);
            Assert.That(s.GunStates, Is.All.EqualTo(GunState.NoWater));
        }

        [Test]
        public void D20_ForcedOnWhileTooWarm()
        {
            var (n, _, _, _) = Line(2);
            var s = Run(n, Warm, out _);
            Assert.That(Has(s, 20), Is.True);
            Assert.That(s.GunStates, Is.All.EqualTo(GunState.RunningWarm));
            Assert.That(s.DemandGpm, Is.EqualTo(2 * 18));
        }

        /// <summary>SG-01: Auto guns above 28 °F wet bulb take nothing (only forced ones run warm).</summary>
        [Test]
        public void AutoGunsWaitWhenTooWarm()
        {
            var (n, _, _, _) = Line(2);
            for (int i = 0; i < n.Guns.Count; i++) n.Guns[i] = n.Guns[i].WithOverride(GunOverride.Auto);
            var g = n.Compile();
            var asks = SnowNet.Asks(g, Warm);
            for (int i = 0; i < asks.Length; i++) asks[i].Wanted = true;
            var s = new SnowSolve(g);
            s.Run(asks, n.Level);
            Assert.That(s.GunStates, Is.All.EqualTo(GunState.TooWarm));
            Assert.That(s.DemandGpm, Is.Zero);
        }

        sealed class NoGround : IGroundHeights
        {
            public bool TryHeightMm(PointMm p, out int heightMm) { heightMm = 0; return false; }
        }
    }
}
