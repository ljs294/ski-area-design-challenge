using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.15's Snowmaking tools without a scene (the solver's hand checks are Core's Snowmaking tests): the tray's five
    /// new tools, a pump house beside a source, a water line from it with hydrants every 100 ft, a booster pushing away from
    /// the house, guns hooked to hydrants and set On, the lasso's Auto/On/Off, and the Check tool's fixes.
    /// </summary>
    public sealed class SnowToolTests
    {
        const string TestPrefix = "MountainPlannerTest.P4-15.";
        const ulong Pond = 0x77;
        static readonly PointMm PondWater = PointMm.FromMetres(-60, 0);

        sealed class Slope : ITerrainSurface
        {
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float HeightAt(float x, float z) => Footprint.Contains(new Vector2(x, z)) ? 2000 + 0.15f * z : float.NaN;
            public float LiftAt(float x, float z) => 0;
        }

        sealed class NullPreview : IToolPreview
        {
            public event Action Cleared;
            public void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false) { }
            public void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink) { }
            public void Dot(int slot, Vector2 at, PreviewInk ink) { }
            public void Hide(int slot) { }
            public void HideFrom(int slot) { }
            public void Clear() => Cleared?.Invoke();
        }

        DesignHistory _design;
        ToolHost _host;
        bool _checkOn;

        [SetUp]
        public void SetUp()
        {
            SettingsStore.Prefix = TestPrefix;
            KeyBindings.Reload();
            KeyBindings.ClearClaims();
            _design = new DesignHistory(DesignDocument.Empty(0x5EED_0415));
            _checkOn = false;
            _host = new ToolHost(_ =>
            {
                var c = new ToolContext(_design, new NullPreview(), new Slope(), new Rect(-1500, -1500, 3000, 3000));
                // One pond whose shore is the line x = −60 m.
                c.SourceNear = (p, reach) =>
                {
                    double d = Math.Max(0, p.X / 1000.0 + 60);
                    if (d * 1000 > reach) return null;
                    return new SourceNear(Pond, "Pond 1", new PointMm(Math.Min(p.X, PondWater.X), p.Y), d, true);
                };
                c.SetSnowCheck = on => _checkOn = on;
                return c;
            }, _ => { });
        }

        [TearDown]
        public void TearDown()
        {
            _host.Close();
            KeyBindings.ClearClaims();
            SettingsStore.Prefix = SettingsStore.DefaultPrefix;
            KeyBindings.Reload();
        }

        static ToolInput At(float x, float z, bool click = false)
        {
            var g = new Vector3(x, 2000 + 0.15f * z, z);
            return new ToolInput { HasGround = true, Ground = g, Point = DesignFrame.ToDesign(g), InCore = true, Click = click, MetresPerPixel = 0.5f };
        }

        void Click(float x, float z) { _host.Tick(At(x, z, click: true)); _host.Tick(default); }

        SnowNetwork Net => _design.Document.Snow;

        /// <summary>Ticks the host (which hands finished analyses over) until the condition holds, or fails after 10 s.</summary>
        void Settle(Func<bool> done)
        {
            var until = DateTime.UtcNow.AddSeconds(10);
            while (!done())
            {
                Assert.That(DateTime.UtcNow, Is.LessThan(until), "the analysis never came back");
                System.Threading.Thread.Sleep(5);
                _host.Tick(default);
            }
        }

        PumpHouse BuildHouse()
        {
            Assert.That(_host.Choose(PumpHouseTool.ToolId), Is.True);
            Click(-40, 0);    // 40 m from the shore: within 300 ft
            Click(-40, 30);   // its long side north
            var tool = (PumpHouseTool)_host.Active;
            Assert.That(tool.Reviewing, Is.True);
            Assert.That(tool.Source, Is.EqualTo(Pond));
            var r = _host.Confirm(WorkStage.Built);
            Assert.That(r != null && r.Accepted, Is.True, r?.Reason);
            return Net.Houses.Single();
        }

        SnowPipe BuildLine()
        {
            Assert.That(_host.Choose(WaterLineTool.ToolId), Is.True);
            Click(-40, 0);     // on the house: snaps to it
            Click(200, 0);
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            var line = (WaterLineTool)_host.Active;
            Assert.That(line.Reviewing, Is.True);
            Settle(() => line.Last != null && !line.Solving);   // its readout comes off the main thread (CF-03)
            Assert.That(line.Last.NoGround, Is.True, "no mountain here: the figures wait, the line can still be laid");
            var r = _host.Confirm(WorkStage.Built);
            Assert.That(r != null && r.Accepted, Is.True, r?.Reason);
            return Net.Pipes.Single();
        }

        [Test]
        public void TheSnowmakingTrayHasItsFiveNetworkTools()
        {
            foreach (var id in new[] { PumpHouseTool.ToolId, WaterLineTool.ToolId, InlinePumpTool.ToolId, SnowGunTool.ToolId, CheckNetworkTool.ToolId })
                Assert.That(ToolCatalog.IsLive(id), Is.True, id);
            // W stays the camera's: the Water line takes the tray's N, and every tray letter is free of the camera's keys.
            Assert.That(KeyBindings.Get(GameAction.SnowPumpHouse, 0), Is.EqualTo(new KeyChord(UnityEngine.InputSystem.Key.P)));
            Assert.That(KeyBindings.Get(GameAction.SnowInlinePump, 0), Is.EqualTo(new KeyChord(UnityEngine.InputSystem.Key.I)));
            Assert.That(KeyBindings.Get(GameAction.SnowCheck, 0), Is.EqualTo(new KeyChord(UnityEngine.InputSystem.Key.K)));
        }

        [Test]
        public void HY01_APumpHouseStandsBesideItsSourceNotFarFromIt()
        {
            Assert.That(_host.Choose(PumpHouseTool.ToolId), Is.True);
            Click(100, 0);   // 160 m from the shore: too far
            Assert.That(((PumpHouseTool)_host.Active).Reviewing, Is.False);
            Assert.That(_host.Active.HasWork, Is.False, "refused: still on its centre");
            _host.Close();
            var h = BuildHouse();
            Assert.That(h.Source, Is.EqualTo(Pond));
            Assert.That(h.CompressorCfm, Is.EqualTo(SnowRules.DefaultCompressorCfm));
            Assert.That(Net.Nodes.Count(n => n.Kind == SnowNodeKind.House), Is.EqualTo(1));
        }

        [Test]
        public void HY03_AWaterLineFromTheHouseHasHydrantsEvery100Feet()
        {
            BuildHouse();
            var pipe = BuildLine();
            Assert.That(pipe.DiameterIn, Is.EqualTo(8), "8 in by default");
            var from = Net.FindNode(pipe.From);
            Assert.That(from.Kind, Is.EqualTo(SnowNodeKind.House), "it starts on the house it was clicked on");
            // About 240 m less the house's half-length: hydrants every 30.48 m, both ends included.
            int hydrants = Net.Nodes.Count(n => n.Kind == SnowNodeKind.Hydrant);
            double lengthFt = SnowGeometry.Length(pipe.Route) / 304.8;
            Assert.That(hydrants, Is.InRange((int)(lengthFt / 100) - 1, (int)(lengthFt / 100) + 2), $"{lengthFt:0} ft of line");
        }

        [Test]
        public void HY05_ABoosterPushesAwayFromThePumpHouse()
        {
            BuildHouse();
            var pipe = BuildLine();
            Assert.That(_host.Choose(InlinePumpTool.ToolId), Is.True);
            Click(85, 0);   // between hydrants (they're 30.5 m apart from the house's wall)
            var pump = Net.Nodes.SingleOrDefault(n => n.Kind == SnowNodeKind.Pump);
            if (pump == null)
            {
                Click(100, 0);   // too near a hydrant: try another spot
                pump = Net.Nodes.SingleOrDefault(n => n.Kind == SnowNodeKind.Pump);
            }
            Assert.That(pump, Is.Not.Null, "a booster on the line");
            // The route runs from the house eastward, so "away from the house" is along the route.
            Assert.That(pump.Forward, Is.True);
            Assert.That(pump.Pump, Is.EqualTo(PumpSpec.Booster));
        }

        [Test]
        public void SG04_GunsHookToTheNearestFreeHydrantAndStartOn()
        {
            BuildHouse();
            BuildLine();
            Assert.That(_host.Choose(SnowGunTool.ToolId), Is.True);
            var hydrant = Net.Nodes.Where(n => n.Kind == SnowNodeKind.Hydrant).OrderBy(n => n.At.X).Skip(2).First();
            Click(hydrant.At.X / 1000f + 1, hydrant.At.Y / 1000f - 4);
            var gun = Net.Guns.Single();
            Assert.That(gun.Hydrant, Is.EqualTo(hydrant.Id));
            Assert.That(gun.Override, Is.EqualTo(GunOverride.On), "new guns are On until 4.16's schedules");
            Assert.That(gun.Model, Is.EqualTo(GunModel.Stick20));
            // Fill hydrants: a gun beside every free hydrant of the line, in one step.
            var tool = (SnowGunTool)_host.Active;
            tool.Choose(GunModel.Stick30, 1);
            Click(50, 0);
            int hydrants = Net.Nodes.Count(n => n.Kind == SnowNodeKind.Hydrant);
            Assert.That(Net.Guns.Count, Is.EqualTo(hydrants));
            Assert.That(Net.Guns.All(g => !g.Hydrant.IsNone), Is.True);
            // Select: set them all Off together, one undo step.
            tool.Choose(GunModel.Stick30, 2);
            tool.Pick(Net.Guns.Select(g => g.Id));
            long rev = _design.Revision;
            var off = new SetGunOverride(_design.Revision, Net.Guns.Select(g => g.Id).ToList(), GunOverride.Off);
            Assert.That(_design.Submit(off, null).Accepted, Is.True);
            Assert.That(Net.Guns.All(g => g.Override == GunOverride.Off), Is.True);
            Assert.That(_design.Revision, Is.EqualTo(rev + 1));
        }

        [Test]
        public void TheLinesFreeEndGetsAHydrantAndTheGunToolAddsOneAnywhere()
        {
            BuildHouse();
            var pipe = BuildLine();
            var end = Net.FindNode(pipe.To);
            Assert.That(end.Kind, Is.EqualTo(SnowNodeKind.Hydrant), "the dead end gets a hydrant (owner 2026-11-19)");
            int before = Net.HydrantsOn(pipe.Id).Count;
            Assert.That(_host.Choose(SnowGunTool.ToolId), Is.True);
            var tool = (SnowGunTool)_host.Active;
            tool.Choose(GunModel.Stick20, 3);
            // Midway between the first two spaced hydrants, on the line.
            var spaced = Net.TapsOn(pipe.Id).OrderBy(n => n.At.X).ToList();
            float x = (spaced[0].At.X + spaced[1].At.X) / 2000f;
            Click(x, 0);
            Assert.That(Net.HydrantsOn(pipe.Id).Count, Is.EqualTo(before + 1), "a hydrant where it was clicked");
            Click(spaced[2].At.X / 1000f, 0);
            Assert.That(Net.HydrantsOn(pipe.Id).Count, Is.EqualTo(before + 1), "too near another hydrant: refused");
        }

        [Test]
        public void HY09_CheckTurnsTheEveryGunSolveOnAndOffAndFixesAPump()
        {
            BuildHouse();
            var pipe = BuildLine();
            // A booster pushing back at its pump house (diagnostic 7): Check's fix flips it.
            var tap = new AddSnowTap(_design.Revision, SnowNodeKind.Pump, pipe.Id, PointMm.FromMetres(85, 0), forward: false, PumpSpec.Booster);
            var r = _design.Submit(tap, null);
            if (!r.Accepted) r = _design.Submit(new AddSnowTap(_design.Revision, SnowNodeKind.Pump, pipe.Id, PointMm.FromMetres(100, 0), false, PumpSpec.Booster), null);
            Assert.That(r.Accepted, Is.True, r.Reason);
            Assert.That(_host.Choose(CheckNetworkTool.ToolId), Is.True);
            Assert.That(_checkOn, Is.False, "as they're set by default");
            ((CheckNetworkTool)_host.Active).Read(true);
            Assert.That(_checkOn, Is.True);
            _host.Close();
            Assert.That(_checkOn, Is.False, "leaving Check stops the every-gun solve");
            var pump = Net.Nodes.Single(n => n.Kind == SnowNodeKind.Pump);
            var flip = new EditSnowPump(_design.Revision, pump.Id, pump.Pump, !pump.Forward);
            Assert.That(_design.Submit(flip, null).Accepted, Is.True);
            Assert.That(Net.FindNode(pump.Id).Forward, Is.True);
        }
    }
}
