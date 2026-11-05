using System.Collections.Generic;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;

namespace MountainPlanner.Tests
{
    // LD9 (drive at either end): the engine-free table of every fixed-grip family's ends (TerminalHeads) is the models' own,
    // head by head, within 2 cm, so the Domain build stands each end where the view draws it. On a miss the message carries
    // the whole table as the models measure it, ready to copy into TerminalHeads.Measured.
    public sealed class TerminalHeadsTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";

        static readonly string[] Families =
        {
            "sessellift_fgq4_", "sessellift_fgt3_", "sessellift_fgd2_", "sessellift_stern4_", "sessellift_stern3_", "sessellift_stern2_",
            "monta_fg4_", "monta_fg3_", "monta_fg2_",
        };

        [Test]
        public void LD9_EveryHeadsEndIsItsModels()
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var table = new StringBuilder();
            var misses = new List<string>();
            var done = new HashSet<string>();
            foreach (string family in Families)
            {
                var set = TerminalHeads.Of(family);
                var ends = new List<TerminalEnd> { set.ReturnBottom, set.DriveBottom };
                if (set.DriveTop != null) ends.AddRange(set.DriveTop);
                if (set.ReturnTop != null) ends.AddRange(set.ReturnTop);
                foreach (var end in ends)
                {
                    if (end == null) continue;
                    string key = end.Model + "|" + end.Head;
                    if (!done.Add(key)) continue;
                    Assert.That(models.Find(end.Model), Is.Not.Null, end.Model);
                    Assert.That(LiftView.RopeExit(models, end.Model, end.Head, out float along, out float rise), Is.True, key);
                    Assert.That(LiftView.TerminalExtent(models, end.Model, end.Head, out float behind, out float ahead, out float half), Is.True, key);
                    double origin = LiftView.OriginAlong(models, end.Model);
                    table.AppendLine($"            [\"{key}\"] = new Measure({behind:F3}, {ahead:F3}, {half:F3}, {along:F3}, {rise:F3}, {origin:F3}),");
                    bool Near(double a, double b) => System.Math.Abs(a - b) <= 0.02;
                    if (!(Near(end.Behind, behind) && Near(end.Ahead, ahead) && Near(end.HalfWidth, half) && Near(end.ExitAlong, along)
                          && Near(end.ExitRise, rise) && Near(end.OriginAlong, origin)))
                        misses.Add(key);
                }
            }
            TestContext.Out.WriteLine(table.ToString());
            Assert.That(misses, Is.Empty, "TerminalHeads.Measured differs from the models; the models measure:\n" + table);
        }
    }
}
