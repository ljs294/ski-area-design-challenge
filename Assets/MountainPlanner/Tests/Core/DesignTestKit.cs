using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Shared helpers for the design document tests (task P3-03).</summary>
    static class DesignTestKit
    {
        public static int Ft(int feet) => DesignUnits.FeetToMm(feet);

        /// <summary>
        /// LT34: a lift built as a player builds it. The terminals stand where they're placed; where the review suggests
        /// raising (or lowering) one, the player does with Page Up / Page Down and builds again. <paramref name="build"/>
        /// takes the levels set by hand (bottom, top), metres.
        /// </summary>
        public static LiftBuild.Result AsPlayer(Func<double, double, LiftBuild.Result> build)
        {
            double bottom = 0, top = 0;
            var b = build(0, 0);
            for (int round = 0; round < 8 && (b.SuggestedBottomMetres != 0 || b.SuggestedTopMetres != 0); round++)
            {
                bottom += b.SuggestedBottomMetres;
                top += b.SuggestedTopMetres;
                b = build(bottom, top);
            }
            return b;
        }

        public static PointMm P(int xFt, int yFt) => new PointMm(Ft(xFt), Ft(yFt));

        /// <summary>One painted part: a straight stroke between two points, 100 ft wide by default.</summary>
        public static FootprintPart[] Straight(PointMm a, PointMm b, int widthFt = 100) =>
            new[] { new FootprintPart(new[] { new Stroke(Ft(widthFt), false, new[] { a, b }) }) };

        public static TrailId AddTrail(DesignHistory h, string name, PointMm head, PointMm tail, WorkStage stage = WorkStage.Built)
        {
            var r = h.Submit(new AddTrail(h.Revision, name, TrailSettings.GradedRun, Straight(head, tail), head, tail, stage));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return new TrailId(r.Created[0]);
        }

        public static LiftId AddLift(DesignHistory h, string name, PointMm bottom, PointMm top, WorkStage stage = WorkStage.Built, params TowerSpec[] towers)
        {
            var r = h.Submit(new AddLift(h.Revision, name, LiftCatalogIds.SesselliftFgq4, bottom, top, TowerMode.Automatic, TowerStance.Plumb, towers, stage));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return new LiftId(r.Created[0]);
        }

        public static TowerSpec Tower(int alongFt, int heightFt) => new TowerSpec(Ft(alongFt), Ft(heightFt));

        public static byte[] Bytes(DesignHistory h) => DesignBytes.Encode(h.Document);

        public static string Sha(byte[] bytes)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var sb = new System.Text.StringBuilder();
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// The scripted design behind the golden tests: two lifts, three trails, a split and a join, an undo and a
        /// redo, a plan and Break ground. Same script, same bytes (SH-04).
        /// </summary>
        public static DesignHistory Scripted()
        {
            var h = new DesignHistory(DesignDocument.Empty(0x5EED_2026));
            var chair1 = AddLift(h, "Chair 1", P(0, 0), P(2000, 3000), WorkStage.Built, Tower(400, 35), Tower(900, 40), Tower(1500, 30));
            var chair2 = AddLift(h, "Chair 2", P(2400, -200), P(4000, 2600), WorkStage.Plan);
            var run1 = AddTrail(h, "Run 1", P(2030, 3000), P(20, 10));
            var run2 = AddTrail(h, "Run 2", P(1990, 2980), P(2400, -180));
            var split = h.Submit(new SplitTrail(h.Revision, run1, P(1000, 1500), Ft(-2010), Ft(-2990)));
            Assert.That(split.Accepted, Is.True, split.ToString());
            var lower = new TrailId(split.Created[0]);
            h.Submit(new RenameTrail(h.Revision, lower, "Lower Run 1"));
            h.Submit(new JoinTrails(h.Revision, run1, lower));
            h.Undo();
            h.Redo();
            h.Undo(); // leave it split
            var tower = h.Document.FindLift(chair1).Towers[1].Id;
            h.Submit(new EditTower(h.Revision, tower, Ft(950), Ft(45)));
            h.Submit(new SetTowerStance(h.Revision, chair1, TowerStance.Leaned));
            h.Submit(new SetTrailSettings(h.Revision, run2, TrailSettings.NaturalRun));
            h.Submit(new BreakGround(h.Revision, null, new[] { chair2 }));
            return h;
        }
    }

    /// <summary>Keyed randomness for the property tests (SH-04): SplitMix64, never System.Random.</summary>
    struct TestRng
    {
        ulong _s;
        public TestRng(ulong seed) { _s = seed * 0x9E3779B97F4A7C15UL + 0x632BE59BD9B4E019UL; }

        public ulong Next()
        {
            ulong z = _s += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public int Int(int below) => (int)(Next() % (ulong)below);
        public int Range(int min, int max) => min + Int(max - min + 1);
        public bool Chance(int percent) => Int(100) < percent;
        public T Pick<T>(IReadOnlyList<T> list) => list[Int(list.Count)];
    }
}
