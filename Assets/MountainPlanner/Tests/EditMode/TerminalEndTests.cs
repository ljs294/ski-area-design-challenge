using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // TW-16 v3 (task 07, round 3): each end of a lift reads its row from the terminal model standing there, and the
    // posing reads every train the models carry, the Monta drive's two-equaliser rows included.
    public sealed class TerminalEndTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";

        static LiftModelSet Models()
        {
            var set = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (set == null) Assert.Ignore("The lift library isn't imported.");
            return set;
        }

        [Test]
        public void EachEndsRowMatchesItsTerminalModel()
        {
            // A hold-down row has the rope_*_hold sockets the rope runs under; a support row has none. (The Monta's own ends
            // come with task 09: its drive stands at the bottom.)
            var models = Models();
            var type = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            foreach (var end in new[] { type.Bottom, type.Top })
            {
                var prefab = models.Find(end.Model);
                Assert.That(prefab, Is.Not.Null, end.Model);
                var rig = prefab.GetComponent<LiftRig>();
                bool hold = rig.Socket("rope_right_hold") != null && rig.Socket("rope_left_hold") != null;
                Assert.That(hold, Is.EqualTo(end.Row == TerminalRow.HoldDown), $"{end.Model}: {end.Row}");
            }
        }

        [Test]
        public void MeasuredEndsComeFromTheModelsSockets()
        {
            var models = Models();
            var type = LiftTypes.Measured(LiftType.Of(LiftCatalogIds.SesselliftFgq4), models);
            Assert.That(LiftView.RopeExit(models, type.Bottom.Model, out float along, out float rise), Is.True);
            Assert.That(type.Bottom.ExitAlong, Is.EqualTo(along).Within(1e-6));
            Assert.That(type.Bottom.ExitRise, Is.EqualTo(rise).Within(1e-6));
            Assert.That(type.Top.OriginAlong, Is.EqualTo(LiftView.OriginAlong(models, type.Top.Model)).Within(1e-6));
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftFgq4).Bottom.ExitAlong, Is.EqualTo(5.889).Within(1e-9), "the catalog entry is untouched");
        }

        [Test]
        public void TheMontaDrivesCarrierAndLinkBarPoseAsOneTrain()
        {
            // The Monta drive's exit rows (bb42477): per rope, a carrier (eq_<s>) carrying a link bar (eq_<s>2), three yokes, six rockers,
            // twelve sheaves, resting for a rope in level and out at 10° under them (a hold-down row: the Monta's drive is its
            // bottom station). The carrier is the train's one root with the link bar under it, and the rope poses under it within the stops
            // from 6° to 14° (the model thread's sweep: 5–16° left, 6–16° right).
            var prefab = Models().Find("monta_fg4_drive");
            if (prefab == null) Assert.Ignore("No Monta drive.");
            var go = Object.Instantiate(prefab);
            try
            {
                var rig = go.GetComponent<LiftRig>();
                foreach (string side in new[] { "l", "r" })
                {
                    Assert.That(TrainPosing.Posable(rig, side), Is.True, side);
                    var dir = go.transform.forward;
                    var train = TrainPosing.Read(rig, side, go.transform.position, dir, Vector3.Cross(Vector3.up, dir));
                    Assert.That(train, Is.Not.Null, side);
                    Assert.That(train.Tree.Nodes.Count(n => n.Parent < 0), Is.EqualTo(1), $"{side}: one carrier at the top");
                    Assert.That(train.Nodes.Count(t => t.name.Contains("_pivot_eq_")), Is.EqualTo(2), $"{side}: the carrier and its link bar");
                    Assert.That(train.Tree.Sheaves.Count, Is.EqualTo(12), side);
                    double rho = LiftType.SheaveRadiusMetres + 0.04445 / 2;
                    foreach (double degrees in new[] { 6.0, 10.0, 14.0 })
                    {
                        var pose = RopeOverSheaves.Solve(train.Tree, 0, degrees * Mathf.Deg2Rad, false, rho);
                        Assert.That(pose.Clamped, Is.False, $"{side} at {degrees}°: within the stops");
                    }
                }
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
