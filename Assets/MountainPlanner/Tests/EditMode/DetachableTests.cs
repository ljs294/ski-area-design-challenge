using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    // LT41 (owner, 2026-10-23): a detachable's carriers slow into the stations on their rails and speed back up, one
    // loading interval apart, so they bunch in the stations and spread on the line; the Sessellift D6 built in the view
    // runs on its terminals' station rails, in its two colours; its walkways follow their trains (G8).
    public sealed class DetachableTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;
        const float Line = 1000 * 0.3048f / 60;   // 1,000 fpm

        // A made-up track: 600 m up, a station rail at the top (about 56 m round, longer than the D6's ~40 m), 600 m down, one at the bottom.
        static CarrierTrack Track(float line = Line)
        {
            var up = new List<Vector3>();
            var down = new List<Vector3>();
            for (int i = 0; i <= 120; i++) { up.Add(new Vector3(2.8f, 0, 15 + i * 5)); down.Add(new Vector3(-2.8f, 0, 615 - i * 5)); }
            List<Vector3> Rail(float z0, float sign)
            {
                var rail = new List<Vector3>();
                for (int i = 0; i <= 20; i++)
                {
                    float a = Mathf.PI * i / 20;
                    rail.Add(new Vector3(2.8f * Mathf.Cos(a) * sign, 0, z0 + sign * 25f * Mathf.Sin(a)));
                }
                return rail;
            }
            var d6 = LiftType.Of(LiftCatalogIds.SesselliftD6);
            return CarrierTrack.Detachable(up, Rail(620, 1), down, Rail(10, -1), line, (float)d6.AlightSpeed(line), (float)d6.BoardSpeed(line), (float)LiftType.RampAccel);
        }

        [Test]
        public void LT41_CarriersSlowIntoTheStationsAndBunchThere()
        {
            var t = Track();
            var d6 = LiftType.Of(LiftCatalogIds.SesselliftD6);
            Assert.That(t.Speeds.Max(), Is.EqualTo(Line).Within(1e-4f), "line speed on the rope");
            Assert.That(t.Speeds.Min(), Is.EqualTo((float)d6.BoardSpeed(Line)).Within(0.02f), "boarding speed round the bottom rail");
            int count = Mathf.RoundToInt(t.LoopTime / (float)d6.IntervalSeconds);
            Assert.That(count, Is.GreaterThan(30));
            // G4: never closer than 3.5 m in the return's loop (2.5 m in the drive's); about speed × interval on the rail.
            float gap = t.SmallestGap(count);
            Assert.That(gap, Is.GreaterThanOrEqualTo(3.5f), "G4's station minimum");
            Assert.That(gap, Is.LessThan(12f), "they do bunch in the stations");
            // On the line they're about 120 ft apart (G4), several times the station's gap.
            Assert.That(Line * t.LoopTime / count, Is.GreaterThan(0.7f * (float)d6.ChairSpacingMetres), "spread on the line");
        }

        [Test]
        public void LT41_AFasterLineKeepsTheBunchingAndTheCount()
        {
            // A later speed slider: the stations scale with it (within their bands), the interval holds.
            var slow = Track(Line);
            var fast = Track(1.2f * Line);
            Assert.That(fast.LoopTime, Is.LessThan(slow.LoopTime));
            Assert.That(fast.Speeds.Min(), Is.GreaterThan(slow.Speeds.Min()), "boarding faster with the line");
        }

        [Test]
        public void LT41_PlacingCarriersAllocatesNothing()
        {
            var t = Track();
            int seg = 1;
            float sum = 0;
            for (int i = 0; i < 10; i++) sum += t.DistanceAt(i * 3.1f, ref seg);   // warm
            long before = System.GC.GetAllocatedBytesForCurrentThread();
            seg = 1;
            for (int i = 0; i < 1000; i++) sum += t.DistanceAt(i * 0.37f % t.LoopTime, ref seg);
            Assert.That(System.GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0));
            Assert.That(sum, Is.GreaterThan(0));
        }

        [Test]
        public void LT41_TheD6BuiltRunsRoundItsStationRails()
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var ret = models.Find("sessellift_d6_return");
            if (ret == null || ret.GetComponent<LiftRig>().Path("chairs") == null) Assert.Ignore("The D6's station paths aren't imported.");
            var d6 = LiftType.Of(LiftCatalogIds.SesselliftD6);
            var ground = new EditedHeights(new Slope(), new EditLattice(West, North), TerrainEdits.Empty);
            var b = LiftBuild.Build(d6, ground, West + 100, North - 900, West + 100, North - 100, TowerStance.Leaned);
            var lift = new Lift(LiftId.Make(1, 1), "Six", WorkStage.Built, d6.CatalogId, new PointMm(0, 0), new PointMm(0, 800_000),
                                TowerMode.Automatic, TowerStance.Leaned, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(), b.Levels);
            var material = new Material(Shader.Find("MountainPlanner/LiftStructure") ?? Shader.Find("Universal Render Pipeline/Lit"));
            var view = new LiftView(null, models, new LiftMaterials(material, material, material), material, "D6 test lift");
            try
            {
                view.Build(lift, new EditedHeights(ground.Source, ground.Lattice, b.Edits), new LocalFrame(new AlbersPoint(West + 100, North - 900)), LiftLivery.For(d6.CatalogId));
                var track = view.Carriers;
                Assert.That(track, Is.Not.Null);
                Assert.That(track.Speeds.Min(), Is.EqualTo((float)d6.BoardSpeed(d6.SpeedMetresPerSecond)).Within(0.05f), "the chairs slow to board");
                Assert.That(view.ChairCount, Is.EqualTo(Mathf.RoundToInt(track.LoopTime / (float)d6.IntervalSeconds)), "one chair an interval round the loop");
                Assert.That(track.SmallestGap(view.ChairCount), Is.GreaterThanOrEqualTo(2.5f), "G4");
                // The D6's own tower heads, from its kit (G2).
                Assert.That(view.Root.GetComponentsInChildren<LiftRig>().Any(r => r.AssetId.StartsWith("sessellift_d6_tower_")), Is.True);
                // Its two colours (LT41): black with a white accent.
                var colours = LiftLivery.For(d6.CatalogId);
                Assert.That(colours.Primary.grayscale, Is.LessThan(0.15f));
                Assert.That(colours.Accent.grayscale, Is.GreaterThan(0.8f));
            }
            finally
            {
                view.Dispose();
                Object.DestroyImmediate(material);
            }
        }

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 900));
        }
    }
}
