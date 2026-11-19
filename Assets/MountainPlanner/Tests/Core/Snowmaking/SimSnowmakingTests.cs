using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests.Snowmaking
{
    /// <summary>
    /// Task 4.15 on the worker (HY-08, WS-11): the solve's fixed latency, "Check network", a pump house filling a pond with
    /// both ledgers balanced, and a reloaded game moving the same water. The sources are 4.14's test pond (6,500 ft full
    /// pool, here starting empty) and reservoir (6,510 ft).
    /// </summary>
    public sealed class SimSnowmakingTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        const ulong PondId = 0x40, DamId = 0x50;

        static SimWorld World(bool weather = false) =>
            WaterSourceTests.World(WaterSourceTests.Sources(WaterSourceTests.Table(), pondEmpty: true), weather ? SimWeatherTests.Golden() : null);

        /// <summary>A pump house on the reservoir and a line 1,000 ft to an outlet in the pond, on ground at 6,499 ft.</summary>
        static SimSnowNetwork Fill(PumpSpec? pump = null, int diameterIn = 6)
        {
            var n = new SnowNet { GroundFt = (x, y) => 6499 }.Water(DamId, 6510).Water(PondId, 6500);
            var house = n.House(0, 0, DamId, pump ?? PumpSpec.House);
            var outlet = n.Node(SnowNodeKind.Outlet, 1000, 0, PondId);
            n.Pipe(house, outlet, diameterIn);
            return new SimSnowNetwork(n.Compile(MountainPlanner.Domain.Design.SnowRules.BuryMm));
        }

        /// <summary>The same house feeding one hydrant and its gun, no outlet.</summary>
        static SimSnowNetwork OneGun(GunOverride over)
        {
            var n = new SnowNet { GroundFt = (x, y) => 6499 }.Water(DamId, 6510);
            var house = n.House(0, 0, DamId, PumpSpec.House);
            var h = n.Node(SnowNodeKind.Hydrant, 500, 0);
            n.Pipe(house, h, 8);
            n.Gun(h, over);
            return new SimSnowNetwork(n.Compile(MountainPlanner.Domain.Design.SnowRules.BuryMm));
        }

        static void Ticks(SimWorld w, int n)
        {
            long until = w.Tick + n;
            while (w.Tick < until) w.Step(1);
        }

        static void Send(SimWorld w, SimSnowNetwork net) => w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowNetwork, 0, net));

        [Test]
        public void HY08_ASolveLandsItsFixedLatencyAfterTheAsk()
        {
            var w = World();
            Send(w, OneGun(GunOverride.On));
            long asked = w.Tick;
            while (w.Tick < asked + P.SolveLatencyTicks - 1)
            {
                w.Step(1);
                Assert.That(w.SnowSolution, Is.Null, $"tick {w.Tick}: not yet");
            }
            w.Step(1);
            var s = w.SnowSolution;
            Assert.That(s, Is.Not.Null);
            Assert.That(s.Tick, Is.EqualTo(asked + P.SolveLatencyTicks));
            Assert.That(s.Solved, Is.True);
            // No weather yet: too warm to make snow, so the gun set On runs at the forced stage (HY-09 20).
            Assert.That(s.GunStates, Is.EqualTo(new[] { GunState.RunningWarm }));
            Assert.That(s.GunPsi[0], Is.GreaterThan(GunCatalog.MinPsi));
        }

        [Test]
        public void Q4_CheckNetworkSolvesAsIfEveryGunRan()
        {
            var w = World();
            Send(w, OneGun(GunOverride.Auto));
            Ticks(w, P.SolveLatencyTicks);
            Assert.That(w.SnowSolution.GunStates, Is.EqualTo(new[] { GunState.Idle }), "Auto waits for 4.16's schedules");
            Assert.That(w.SnowCheckSolution, Is.Null);
            w.ApplyNow(SimCommand.Of(SimCommandKind.CheckNetwork, 1));
            Ticks(w, P.SolveLatencyTicks);
            Assert.That(w.SnowCheckSolution.WhatIf, Is.True);
            Assert.That(w.SnowCheckSolution.GunStates, Is.EqualTo(new[] { GunState.RunningWarm }));
            Assert.That(w.SnowSolution.GunStates, Is.EqualTo(new[] { GunState.Idle }), "the guns as set are unchanged");
            w.ApplyNow(SimCommand.Of(SimCommandKind.CheckNetwork, 0));
            Assert.That(w.SnowCheckSolution, Is.Null);
        }

        [Test]
        public void WS11_APumpHouseFillsAPondAndBothLedgersBalance()
        {
            var w = World();
            int pond = w.Sources.IndexOf(PondId), dam = w.Sources.IndexOf(DamId);
            Assert.That(w.HeldMilliOf(pond), Is.Zero);
            long damBefore = w.HeldMilliOf(dam);
            Send(w, Fill());
            SeasonRunner.Run(w, null, w.FaceMs + 3 * Hour, null);

            var s = w.SnowSolution;
            Assert.That(s.Solved, Is.True);
            int outlet = Enumerable.Range(0, s.Graph.LinkCount).Single(k => s.Graph.LinkKind[k] == HLinkKind.Outlet);
            Assert.That(s.LinkGpm[outlet], Is.GreaterThan(100), "the house pushes water into the pond");
            var pin = w.SourceLedgerOf(pond);
            var pout = w.SourceLedgerOf(dam);
            Assert.That(pin.PumpedIn, Is.GreaterThan(0));
            Assert.That(pin.PumpedIn, Is.EqualTo(pout.PumpedOut), "what left the reservoir is what reached the pond");
            Assert.That(w.ResortWaterLedger.PumpedIn, Is.EqualTo(w.ResortWaterLedger.PumpedOut));
            Assert.That(w.HeldMilliOf(pond), Is.GreaterThan(0));
            Assert.That(w.HeldMilliOf(dam), Is.LessThan(damBefore + pout.Inflow + pout.Spring));
            for (int i = 0; i < w.SourceCount; i++)
                Assert.That(w.SourceLedgerOf(i).Held, Is.EqualTo(w.HeldMilliOf(i)), $"source {i}'s ledger balances");
        }

        /// <summary>
        /// The hour's transfer is one hour's flow, in whole gallons (the solve's flow × calendar seconds). A 20 hp, 50 psi
        /// house on 4 in moves about 400 gpm, under what the test pond (about 200,000 gal) has room for in an hour.
        /// </summary>
        [Test]
        public void WS11_AnHourMovesAnHoursFlow()
        {
            var w = World();
            int pond = w.Sources.IndexOf(PondId);
            Send(w, Fill(new PumpSpec(20, 850, 50, true), 4));
            SeasonRunner.Run(w, null, (w.FaceMs / Hour + 1) * Hour, null);   // to the next face hour
            long before = w.SourceLedgerOf(pond).PumpedIn;
            var s = w.SnowSolution;
            int outlet = Enumerable.Range(0, s.Graph.LinkCount).Single(k => s.Graph.LinkKind[k] == HLinkKind.Outlet);
            float gpm = s.LinkGpm[outlet];
            SeasonRunner.Run(w, null, w.FaceMs + Hour, null);
            long moved = (w.SourceLedgerOf(pond).PumpedIn - before) / SimWorld.MilliPerGallon;
            long seconds = P.SeasonCalendarDays * 3600L / P.SeasonDays;
            Assert.That(moved, Is.EqualTo(gpm * seconds / 60.0).Within(gpm * seconds / 60.0 * 0.001 + 2));
        }

        [Test]
        public void SI22_AReloadedGameMovesTheSameWater()
        {
            var a = World(weather: true);
            Send(a, Fill());
            SeasonRunner.Run(a, null, 20 * Day + 5 * Hour, null);
            var b = SimSidecar.Read(a.SaveState(), P, a.Design, SimSite.None, out _, SimWeatherTests.Golden());
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, WaterSourceTests.Table()));
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, WaterSourceTests.Sources(WaterSourceTests.Table(), pondEmpty: true)));
            Send(b, Fill());
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the network is an input, not state");
            SeasonRunner.Run(a, null, 25 * Day, null);
            SeasonRunner.Run(b, null, 25 * Day, null);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the same water moved");
            Assert.That(a.ResortWaterLedger.PumpedIn, Is.GreaterThan(0));
        }

        [Test]
        public void ClearingTheNetworkStopsTheSolves()
        {
            var w = World();
            Send(w, OneGun(GunOverride.On));
            Ticks(w, P.SolveLatencyTicks);
            Assert.That(w.SnowSolution, Is.Not.Null);
            Send(w, null);
            Assert.That(w.SnowSolution, Is.Null);
            Assert.That(w.SnowNet, Is.Null);
        }
    }
}
