using System;
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
    // Task 15 round 4 (B0): a tower re-heights by moving its pieces and showing or hiding mast sections, as often as the ground
    // under it moves (every frame of a brush near it). The second re-height used to find no pieces (picking's part map was
    // cleared by the first), leave the old tower standing and build a second one.
    public sealed class TowerReheightTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;
        const float Slack = 0.004f;

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 900));
        }

        // The ground with a hole dug round one point (the brush lowering the ground under a tower).
        sealed class Dug : IHeightSource
        {
            readonly IHeightSource _under;
            readonly double _x, _y, _depth;
            public Dug(IHeightSource under, double x, double y, double depth) { _under = under; _x = x; _y = y; _depth = depth; }
            public double At(double x, double y)
            {
                double d = Math.Sqrt((x - _x) * (x - _x) + (y - _y) * (y - _y));
                return _under.At(x, y) - (d < 15 ? _depth : 0);
            }
        }

        static int Active(GameObject root)
        {
            int n = 0;
            for (int i = 0; i < root.transform.childCount; i++) if (root.transform.GetChild(i).gameObject.activeSelf) n++;
            return n;
        }

        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.SesselliftD6)]
        public void B0_ATowerReheightsAgainAndAgainWithOneSetOfPieces(string catalogId)
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var type = LiftType.Of(catalogId);
            var natural = new EditedHeights(new Slope(), new EditLattice(West, North), TerrainEdits.Empty);
            double bx = West + 100, by = North - 900, tx = bx, ty = by + 800;
            var b = LiftBuild.Build(type, natural, bx, by, tx, ty, TowerStance.Plumb);
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, type.CatalogId, new PointMm(0, 0), PointMm.FromMetres(0, 800),
                                TowerMode.Automatic, TowerStance.Plumb, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(), b.Levels);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var view = new LiftView(null, models, new LiftMaterials(material, material, material), material, "Reheight test lift");
            var frame = new LocalFrame(new AlbersPoint(bx, by));
            try
            {
                var built = new EditedHeights(natural.Source, natural.Lattice, b.Edits);
                view.Build(lift, built, frame, LiftLivery.For(type.CatalogId));
                var rope = view.Rope;
                Assert.That(rope.Towers.Count, Is.GreaterThan(2));
                int i = rope.Towers.Count / 2;
                var t = rope.Towers[i];
                float before = view.AsBuiltHeights[i];
                int shown = Active(view.Root);
                var head = new Vector3(0, (float)t.RopeY, (float)t.RopeS);

                foreach (double depth in new[] { 2.0, 4.0, 6.0, 3.0 })
                {
                    Assert.That(view.Reheight(lift, new Dug(built, bx, by + t.BaseS, depth), frame), Is.True, $"dug {depth} m");
                    Assert.That(view.AsBuiltHeights[i], Is.GreaterThan(before), $"dug {depth} m: tower {i} grows");
                    var eye = head + new Vector3(120, 60, 0);
                    Assert.That(view.Pick(new Ray(eye, (head - eye).normalized), 10_000, Slack, out _, out int part), Is.True, $"dug {depth} m: the head");
                    Assert.That(part, Is.EqualTo(i), $"dug {depth} m: tower {i}'s head still picks it");
                }
                Assert.That(view.Reheight(lift, built, frame), Is.True);
                Assert.That(view.AsBuiltHeights[i], Is.EqualTo(before), "back on its ground, its steel as built");
                Assert.That(Active(view.Root), Is.EqualTo(shown), "one set of pieces: nothing left over, nothing doubled");
            }
            finally
            {
                view.Dispose();
                Object.DestroyImmediate(material);
            }
        }
    }
}
