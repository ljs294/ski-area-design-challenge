using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Tests.Snowmaking;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.15's cards (<see cref="SnowCards"/>) from a solved network, no scene: a pump house at the bottom of a 50%
    /// slope feeding an 8 in line 3,000 ft up it, a hydrant every 500 ft with a gun On. The house holds 600 psi
    /// (1,386 ft of head), so the top hydrant at 1,450 ft stands too high and its gun is short of pressure.
    /// </summary>
    public sealed class SnowCardTests
    {
        const ulong Pond = 0x77;
        SnowNet _n;
        SnowNode _house, _top;
        SnowPipe _line;
        SnowSolution _sol;

        [SetUp]
        public void SetUp()
        {
            _n = new SnowNet { GroundFt = (x, y) => 0.5 * x };
            _n.Water(Pond, 0);
            _house = _n.House(0, 0, Pond, PumpSpec.House);
            var end = _n.Node(SnowNodeKind.Junction, 3000, 0);
            _line = _n.Pipe(_house, end, 8);
            for (int ft = 500; ft <= 2500; ft += 500) _n.Gun(_n.Tap(SnowNodeKind.Hydrant, _line, ft, 0));
            _top = _n.Tap(SnowNodeKind.Hydrant, _line, 2900, 0);
            _n.Gun(_top);
            var g = _n.Compile();
            var s = new SnowSolve(g);
            s.Run(SnowNet.Asks(g, 200), _n.Level, cold: true);
            _sol = new SnowSolution(s, -1, false);
            Assert.That(_sol.Solved, Is.True);
        }

        static string Row(WaterCardModel m, string k) => m.Rows.FirstOrDefault(r => r.K == k).V;
        static bool Has(WaterCardModel m, string id) => m.Actions.Any(a => a.Id == id);

        [Test]
        public void TheKeyRoundTrips()
        {
            Assert.That(SnowCards.TryParse(SnowCards.Key(_line.Id), out var id), Is.True);
            Assert.That(id, Is.EqualTo(_line.Id));
            Assert.That(SnowCards.TryParse("pond:0000000000000077", out _), Is.False);
        }

        [Test]
        public void APipeCardReadsTheStretchUnderTheClickAndTheClassMargin()
        {
            var m = SnowCards.For(_n.Network(), _sol, _line.Id, 750 * 304.8, null, false).Value;
            Assert.That(m.Kind, Is.EqualTo("Water line"));
            Assert.That(Row(m, "Diameter"), Is.EqualTo("8 in"));
            StringAssert.Contains("climbs", Row(m, "Length"));
            Assert.That(Row(m, "Hydrants"), Is.EqualTo("6 · 6 guns"));
            var hydrants = _n.Nodes.Where(x => x.Kind == SnowNodeKind.Hydrant).OrderBy(x => x.At.X).ToList();
            Assert.That(Row(m, "Here"), Is.EqualTo($"H{hydrants[0].Label}–H{hydrants[1].Label}"), "750 ft up: between the first two hydrants");
            StringAssert.Contains("uphill", Row(m, "Flow"));
            StringAssert.Contains("→", Row(m, "Pressure"));
            StringAssert.Contains("of 1,000 psi", Row(m, "Class"));
            Assert.That(Row(m, "Air"), Is.EqualTo("laid alongside · from House"), "one pipe: the air runs beside the water");
            Assert.That(Has(m, "upsize") && Has(m, "downsize"), Is.True);
            var cmd = SnowCards.Action(_n.Network(), 0, _line.Id, "upsize", out string done) as EditSnowPipe;
            Assert.That(cmd, Is.Not.Null);
            Assert.That(cmd.DiameterIn, Is.EqualTo(10));
            StringAssert.Contains("10 in", done);
        }

        [Test]
        public void AShortGunSaysWhatItLacksAndOffersTheUpsize()
        {
            var gun = _n.Guns.Single(g => g.Hydrant == _top.Id);
            int gi = _sol.GunIndex(gun.Id);
            Assert.That(_sol.GunStates[gi], Is.EqualTo(GunState.ShortOfPressure), "1,450 ft up on a 600 psi house");
            var m = SnowCards.For(_n.Network(), _sol, gun.Id, 0, null, false).Value;
            Assert.That(Row(m, "Now"), Is.EqualTo("short of pressure"));
            StringAssert.StartsWith("Needs 200 psi, has ", m.Hint);
            Assert.That(Has(m, "gunupsize"), Is.True);
            Assert.That(m.Actions.Single(a => a.Id == "on").On, Is.True, "the mode buttons show the gun's override");
            var running = _n.Guns.First();
            var ok = SnowCards.For(_n.Network(), _sol, running.Id, 0, null, false).Value;
            Assert.That(Row(ok, "Now"), Is.EqualTo("making snow"));
            Assert.That(Has(ok, "gunupsize"), Is.False);
        }

        [Test]
        public void APumpHouseCardShowsItsPumpAirAndSourceAndRenames()
        {
            var house = _n.Houses.Single();
            var m = SnowCards.For(_n.Network(), _sol, house.Id, 0, s => s == Pond ? "Pond 1" : null, false).Value;
            Assert.That(m.Kind, Is.EqualTo("Pump house"));
            Assert.That(Row(m, "Source"), Is.EqualTo("Pond 1"));
            StringAssert.Contains("cfm", Row(m, "Air"));
            StringAssert.Contains("gpm", Row(m, "Pumped out"));
            Assert.That(Row(m, "Pumped in"), Is.EqualTo("none"));
            // Its discharge node opens the same card.
            Assert.That(SnowCards.For(_n.Network(), _sol, _house.Id, 0, null, false).Value.Key, Is.EqualTo(SnowCards.Key(house.Id)));

            var doc = DesignDocument.Empty(1).WithSnow(_n.Network());
            var history = new DesignHistory(doc);
            Assert.That(history.Submit(SnowCards.Rename(history.Document.Snow, history.Revision, house.Id, "Base Pump House"), null).Accepted, Is.True);
            Assert.That(history.Document.Snow.FindHouse(house.Id).Name, Is.EqualTo("Base Pump House"));
            var up = SnowCards.Action(history.Document.Snow, history.Revision, house.Id, "setup", out _) as EditSnowPump;
            Assert.That(up.Pump.BoostPsi, Is.EqualTo(625));
        }

        [Test]
        public void AHydrantCardReadsItsPressureAndGun()
        {
            var m = SnowCards.For(_n.Network(), _sol, _top.Id, 0, null, false).Value;
            Assert.That(m.Kind, Is.EqualTo("Hydrant"));
            Assert.That(m.Name, Is.EqualTo("H" + _top.Label));
            StringAssert.Contains("psi", Row(m, "Pressure"));
            StringAssert.StartsWith("On · SLE stick 20 ft", Row(m, "Gun"));
        }

        [Test]
        public void AboveAWrongWayPumpNothingReadsAPressure()
        {
            // A booster at 1,250 ft facing the house: its check valve cuts off everything above it (HY-09 4 and 7).
            var n = new SnowNet { GroundFt = (x, y) => 0.1 * x };
            n.Water(Pond, 0);
            var house = n.House(0, 0, Pond, PumpSpec.House);
            var line = n.Pipe(house, n.Node(SnowNodeKind.Junction, 3000, 0), 8);
            var below = n.Tap(SnowNodeKind.Hydrant, line, 500, 0);
            var pump = n.Tap(SnowNodeKind.Pump, line, 1250, 0, forward: false, PumpSpec.Booster);
            var above = n.Tap(SnowNodeKind.Hydrant, line, 2000, 0);
            n.Gun(below);
            var cut = n.Gun(above);
            var g = n.Compile();
            var s = new SnowSolve(g);
            s.Run(SnowNet.Asks(g, 200), n.Level, cold: true);
            var sol = new SnowSolution(s, -1, false);
            Assert.That(sol.Known[g.NodeOf(below.Id)], Is.True);
            Assert.That(sol.Known[g.NodeOf(above.Id)], Is.False, "behind the check valve: no water, so no pressure");
            Assert.That(float.IsNaN(sol.GunPsi[sol.GunIndex(cut.Id)]), Is.True);
            var gun = SnowCards.For(n.Network(), sol, cut.Id, 0, null, false).Value;
            Assert.That(Row(gun, "Now"), Is.EqualTo("no water reaches its hydrant"));
            Assert.That(Row(gun, "Pressure"), Is.Null, "a cut-off gun shows no pressure");
            Assert.That(Row(gun, "Stage"), Is.Null);
            StringAssert.StartsWith("stage ", Row(gun, "Would run at"));
            var card = SnowCards.For(n.Network(), sol, pump.Id, 0, null, false).Value;
            Assert.That(Row(card, "Status"), Is.EqualTo("faces away from the water: flip it"));
            Assert.That(Row(SnowCards.For(n.Network(), sol, above.Id, 0, null, false).Value, "Pressure"), Is.EqualTo("no water reaches it"));
        }

        [Test]
        public void WithNoSolveTheCardSaysSolving()
        {
            var m = SnowCards.For(_n.Network(), null, _line.Id, 0, null, true).Value;
            Assert.That(Row(m, "Water"), Is.EqualTo("solving…"));
            StringAssert.StartsWith("Updating", m.Hint);
        }
    }
}
