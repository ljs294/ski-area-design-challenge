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
    // Finding E5 (owner, 2026-10-23: "the ability to select a tower and edit it appears to have disappeared"): pointing at a
    // tower's head or mast picks that tower, on the quad and the D6, even with the rope's span in front of the head, and a
    // terminal's box (along the line, not the world's axes) doesn't cover the station tower beside it.
    public sealed class TowerPickTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;
        const float Slack = 0.004f;   // ~8 px at 1080p and a 60° field of view

        // A diagonal line (the case world-axis terminal boxes got wrong), rising 25%.
        static readonly Vector2 Dir = new Vector2(0.6f, 0.8f);
        const double Length = 800;

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * ((x - (West + 100)) * 0.6 + (y - (North - 900)) * 0.8);
        }

        static Ray From(Vector3 eye, Vector3 at) => new Ray(eye, (at - eye).normalized);

        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.SesselliftD6)]
        public void E5_EveryTowerPicksByItsHeadAndItsMast(string catalogId)
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var type = LiftType.Of(catalogId);
            var natural = new EditedHeights(new Slope(), new EditLattice(West, North), TerrainEdits.Empty);
            double bx = West + 100, by = North - 900, tx = bx + Dir.x * Length, ty = by + Dir.y * Length;
            var b = LiftBuild.Build(type, natural, bx, by, tx, ty, TowerStance.Leaned);
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, type.CatalogId, new PointMm(0, 0),
                                PointMm.FromMetres(Dir.x * Length, Dir.y * Length), TowerMode.Automatic, TowerStance.Leaned,
                                b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(), b.Levels);
            var material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            var view = new LiftView(null, models, new LiftMaterials(material, material, material), material, "Tower pick test lift");
            try
            {
                view.Build(lift, new EditedHeights(natural.Source, natural.Lattice, b.Edits), new LocalFrame(new AlbersPoint(bx, by)), LiftLivery.For(type.CatalogId));
                var rope = view.Rope;
                Assert.That(rope.Towers.Count, Is.GreaterThan(2));
                var side = new Vector3(-Dir.y, 0, Dir.x);
                var along = new Vector3(Dir.x, 0, Dir.y);
                for (int i = 0; i < rope.Towers.Count; i++)
                {
                    var t = rope.Towers[i];
                    float s = (float)t.BaseS;
                    float ground = (float)natural.At(bx + Dir.x * s, by + Dir.y * s);
                    var foot = new Vector3(Dir.x * s, ground, Dir.y * s);
                    var head = new Vector3(Dir.x * (float)t.RopeS, (float)t.RopeY, Dir.y * (float)t.RopeS);
                    var mast = Vector3.Lerp(foot, head, 0.4f);
                    // From the side and above, at the head and at the mast.
                    var eye = head + side * 120 + Vector3.up * 60;
                    Assert.That(view.Pick(From(eye, head), 10_000, Slack, out _, out int part), Is.True, $"tower {i}'s head");
                    Assert.That(part, Is.EqualTo(i), $"tower {i}'s head picks it");
                    Assert.That(view.Pick(From(eye, mast), 10_000, Slack, out _, out part), Is.True, $"tower {i}'s mast");
                    Assert.That(part, Is.EqualTo(i), $"tower {i}'s mast picks it");
                    // Looking up the line at the head from a little below it and downhill: the span in front comes first. (The
                    // first tower's eye would stand behind the bottom terminal, which rightly hides it.)
                    if (t.BaseS < type.Bottom.Ahead + 50) continue;
                    float back = Mathf.Min(40f, 0.7f * (float)(t.RopeS - rope.Towers[i - 1].RopeS));   // in front of the tower before
                    var low = head - along * back + side * 6 - Vector3.up * 4;
                    Assert.That(view.Pick(From(low, head), 10_000, Slack, out _, out part), Is.True, $"tower {i} along the line");
                    Assert.That(part, Is.EqualTo(i), $"tower {i} along the line picks it, not the rope");
                }
                // Pointing at the rope mid-span, away from any tower, still picks the rope (a click there adds a tower).
                var span = view.RopePoint(0.12f);
                Assert.That(view.Pick(From(span + side * 120 + Vector3.up * 60, span), 10_000, Slack, out _, out int ropePart), Is.True);
                Assert.That(ropePart, Is.EqualTo(LiftView.PartRope).Or.GreaterThanOrEqualTo(0), "the rope (or a tower right by it)");
            }
            finally
            {
                view.Dispose();
                Object.DestroyImmediate(material);
            }
        }
    }
}
