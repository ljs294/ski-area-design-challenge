using System;
using System.Collections.Generic;
using MountainPlanner.Presentation.Guests;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.13 §E (the owner's priority): lift lines grow without running into anything, as wide as the chair, the
    /// same every time.
    /// </summary>
    public sealed class LiftLineLayoutTests
    {
        static LineLayout.LiftInput Lift(Vector2 bottom, Vector2 top, int seats) => new LineLayout.LiftInput
        {
            Bottom = bottom, Top = top, Seats = seats, Built = true,
            Behind = 4, Ahead = 6, HalfWidth = 4, TopBehind = 4, TopAhead = 6, TopHalfWidth = 4, LaneHalfWidth = 5.3f,
        };

        static List<Vector3> SlotsOf(LineLayout layout, int lift)
        {
            var list = new List<Vector3>();
            var s = layout.LiftSlots[lift];
            for (int k = 0; k < s.y; k++) { var p = layout.Slots[s.x + k]; list.Add(new Vector3(p.x, p.y, p.z)); }
            return list;
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(6)] [TestCase(8)]
        public void ALineIsAsWideAsTheChair(int seats)
        {
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), seats) }, null, (x, z) => 0f, null);
            var slots = SlotsOf(layout, 0);
            Assert.GreaterOrEqual(slots.Count, seats * 2);
            // The first row is `seats` skiers side by side, one column apart, and the next row is a row behind.
            for (int c = 1; c < seats; c++)
                Assert.AreEqual(LineLayout.ColumnMetres, Vector3.Distance(slots[c], slots[c - 1]), 1e-3f, $"column {c}");
            Assert.AreEqual(LineLayout.RowMetres, Vector3.Distance(slots[seats], slots[0]), 1e-3f, "row pitch");
        }

        [Test]
        public void AQuadHoldsAHolidayLineOnOpenGround()
        {
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, null);
            Assert.GreaterOrEqual(layout.Capacity(0), LineLayout.InitialSlots, "the design build lays the first places");
            GrowTo(layout, 0, 300);
            Assert.GreaterOrEqual(layout.Capacity(0), 300);
        }

        static void GrowTo(LineLayout layout, int lift, int places)
        {
            for (int k = 0; k < 200 && layout.Capacity(lift) < places; k++)
                if (!layout.Grow(lift)) break;
        }

        static void AssertNoSharedGround(LineLayout layout, int lifts)
        {
            var taken = new HashSet<(int, int)>();
            for (int l = 0; l < lifts; l++)
                foreach (var p in SlotsOf(layout, l))
                    Assert.IsTrue(taken.Add((Mathf.FloorToInt(p.x / 0.5f), Mathf.FloorToInt(p.z / 0.5f))), $"two skiers share ground at {p}");
        }

        // ---- Demo findings 1 and 2 (owner, 2026-11-10): "lines should grow as long as they need to" ----

        [Test]
        public void ALoneTreeInTheWayIsFoldedRoundNotTheEndOfTheLine()
        {
            // A tree 3 m across right where the fold runs, open ground everywhere else (Katahdin's Snowfield Double had
            // one, and its line stopped there with 51 in it).
            bool Tree(float x, float z) => (x - 2) * (x - 2) + (z + 16) * (z + 16) < 9f;
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 2) }, null, (x, z) => 0f, Tree);
            GrowTo(layout, 0, 600);
            Assert.GreaterOrEqual(layout.Capacity(0), 600, layout.Describe(0));
            foreach (var p in SlotsOf(layout, 0)) Assert.IsFalse(Tree(p.x, p.z), $"standing in the tree at {p}");
            AssertNoSharedGround(layout, 1);
        }

        [Test]
        public void AClearingInTheForestWalksOutThroughItsGapAndFoldsAgainBeyond()
        {
            // Forest everywhere but a clearing behind the terminal, a 2 m gap south out of it, and a field beyond.
            bool Clearing(float x, float z) => x > -8 && x < 8 && z > -30 && z < 2;
            bool Gap(float x, float z) => x > -1 && x < 1 && z <= -30 && z > -60;
            bool Field(float x, float z) => z <= -60 && z > -200 && x > -100 && x < 100;
            bool Trees(float x, float z) => !(Clearing(x, z) || Gap(x, z) || Field(x, z));
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, Trees);
            GrowTo(layout, 0, 800);
            Assert.GreaterOrEqual(layout.Capacity(0), 800, layout.Describe(0));
            Assert.Greater(layout.WalkwayPlaces(0), 0, "it strings out through the gap");
            int beyond = 0;
            foreach (var p in SlotsOf(layout, 0))
            {
                Assert.IsFalse(Trees(p.x, p.z), $"in the trees at {p} with open ground in reach");
                if (Field(p.x, p.z)) beyond++;
            }
            Assert.Greater(beyond, 300, "it folds again in the field beyond");
            AssertNoSharedGround(layout, 1);
            // Task 4.26 BB4: the stretches cover every place, front first: rows at the terminal, single file through the
            // gap, rows again in the field (a skier keeps its lane only within rows, and follows the one ahead in a file).
            int rows = 0, files = 0, rowStretches = 0;
            for (int k = 0; k < layout.SectionCount(0); k++)
            {
                layout.Section(0, k, out int n, out bool file);
                rows += n;
                if (file) files++; else rowStretches++;
            }
            Assert.That(rows * 4, Is.EqualTo(layout.Capacity(0)), "every place is in one stretch");
            layout.Section(0, 0, out _, out bool firstFile);
            Assert.IsFalse(firstFile, "rows at the terminal");
            Assert.GreaterOrEqual(files, 1, "single file through the gap");
            Assert.GreaterOrEqual(rowStretches, 2, "rows again beyond");
        }

        // Task 4.26 BB4 (owner, 2026-11-23, lift 0 at the drawn resort): behind the terminal the line stood in a gap between
        // trees, too narrow for two passes side by side, and folded there in 3-row zigzags (clumps of rows with gaps). In a
        // corridor it runs straight on, rows across, and folds where the ground opens out.
        [Test]
        public void ACorridorBetweenTreesRunsStraightDownItNotInShortFolds()
        {
            bool Corridor(float x, float z) => x > -4 && x < 4 && z > -40 && z < 2;
            bool Field(float x, float z) => z <= -40 && z > -200 && x > -100 && x < 100;
            bool Trees(float x, float z) => !(Corridor(x, z) || Field(x, z));
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, Trees);
            GrowTo(layout, 0, 400);
            var s = layout.LiftSlots[0];
            int inCorridor = 0, inField = 0;
            for (int k = 0; k < s.y; k++)
            {
                var p = layout.Slots[s.x + k];
                if (Field(p.x, p.z)) inField++;
                if (!Corridor(p.x, p.z) || p.z < -36) continue;
                inCorridor++;
                Assert.That(layout.Facing[s.x + k].y, Is.GreaterThan(0.95f), $"place {k} at {p} faces up the corridor");
            }
            Assert.That(inCorridor, Is.GreaterThanOrEqualTo(4 * 15), "the line runs the corridor's length, four across");
            Assert.That(inField, Is.GreaterThan(200), "and folds in the field beyond");
            AssertNoSharedGround(layout, 1);
        }

        // Task 4.26 BB4 (owner, 2026-11-23, lift 0 at the drawn resort): the line jumped from its throat to a fold 4 m away,
        // an empty gap at its front. Its first heading was one whose first row didn't fit (it laid nothing, scored 0), chosen
        // over headings that lay rows but scored below 0 for their short folds.
        [Test]
        public void AFirstHeadingThatLaysNothingNeverBeatsOneThatLaysRows()
        {
            Assert.IsTrue(LineLayout.Better(true, -3, false, 0), "rows in short folds beat nothing");
            Assert.IsFalse(LineLayout.Better(false, 0, true, -3), "nothing never beats rows");
            Assert.IsTrue(LineLayout.Better(true, 5, true, 3), "more rows win");
            Assert.IsFalse(LineLayout.Better(true, 3, true, 3), "ties keep the earlier heading");
            Assert.IsTrue(LineLayout.Better(false, 0, false, int.MinValue), "with nothing laid anywhere, the first heading stands");
        }

        // Task 4.26 BB4: the drawn resort ran out of memory laying a line whose centre doubled straight back (its corner
        // came out kilometres away). Corridors of many widths, lengths and angles, and patchy forests: every line grows long,
        // its centre never doubles back, and every place stands in reach.
        [Test]
        public void NoLineDoublesStraightBackInCorridorsOrPatchyForest()
        {
            var cases = new List<(string Name, Func<float, float, bool> Trees)>();
            foreach (float width in new[] { 5f, 7f, 8.5f, 10f, 12f })
                foreach (float length in new[] { 15f, 40f, 70f })
                    foreach (float angle in new[] { 0f, 30f, 75f })
                    {
                        float c = Mathf.Cos(angle * Mathf.Deg2Rad), sn = Mathf.Sin(angle * Mathf.Deg2Rad);
                        float w = width, len = length;
                        bool Open(float x, float z)
                        {
                            // The corridor runs back from the lift at the angle, then a field opens out.
                            float along = -(x * sn + z * c), across = x * c - z * sn;
                            if (along > -2 && along < len && Mathf.Abs(across) < w * 0.5f) return true;
                            return along >= len && along < len + 120 && Mathf.Abs(across) < 60;
                        }
                        cases.Add(($"corridor {width} m x {length} m at {angle}°", (x, z) => !Open(x, z)));
                    }
            foreach (int seed in new[] { 1, 2, 3, 4, 5, 6 })
            {
                int sd = seed;
                bool Patchy(float x, float z)
                {
                    if (x > -3 && x < 3 && z > -6 && z < 2) return false;   // the load lane
                    uint h = (uint)(Mathf.FloorToInt(x / 7f) * 73856093) ^ (uint)(Mathf.FloorToInt(z / 7f) * 19349663) ^ (uint)(sd * 83492791);
                    h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                    return h % 100 < 38;
                }
                cases.Add(($"patchy forest {seed}", Patchy));
            }
            foreach (var (name, trees) in cases)
            {
                using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, (x, z) => trees(x, z));
                GrowTo(layout, 0, 1500);
                Assert.AreEqual(0, layout.Reversals, $"{name}: the line's centre doubled straight back: {layout.FirstReversal}");
                foreach (var p in SlotsOf(layout, 0))
                    Assert.IsTrue(float.IsFinite(p.x) && float.IsFinite(p.z) && new Vector2(p.x, p.z).magnitude <= LineLayout.MaxReachMetres + 10,
                                  $"{name}: a place at {p}");
            }
        }

        [Test]
        public void BoxedInByForestTheLineFoldsOverTheTreesButNeverOnARun()
        {
            // A small clearing, forest all round, and a run across the forest 40 m south (past the line's reach either
            // way, as the test below assumes): nowhere open in reach.
            bool Clearing(float x, float z) => x > -6 && x < 6 && z > -20 && z < 2;
            bool Trees(float x, float z) => !Clearing(x, z);
            var runs = new[] { new LineLayout.RunInput { Line = new[] { new Vector2(-600, -40), new Vector2(0, -40), new Vector2(600, -40) }, HalfWidth = 10 } };
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, runs, (x, z) => 0f, Trees);
            GrowTo(layout, 0, 300);
            Assert.GreaterOrEqual(layout.Capacity(0), 300, layout.Describe(0));
            Assert.Greater(layout.SoftPlaces(0), 0, "the line goes on over the trees");
            foreach (var p in SlotsOf(layout, 0))
            {
                Assert.IsFalse(Mathf.Abs(p.z + 40) < 6f, $"standing on the run at {p}");
                // A lift's line ahead of the terminal is hard ground too: nobody goes up the lift line.
                Assert.IsFalse(p.z > 7f && Mathf.Abs(p.x) <= 5.3f, $"under the lift at {p}");
            }
            AssertNoSharedGround(layout, 1);
        }

        [Test]
        public void PlacesNeverMoveWhenALineGrows()
        {
            bool Trees(float x, float z) => (x * x + z * z) % 700 < 40;
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0.03f * x, Trees);
            var before = SlotsOf(layout, 0);
            GrowTo(layout, 0, before.Count + 250);
            var after = SlotsOf(layout, 0);
            Assert.Greater(after.Count, before.Count);
            for (int k = 0; k < before.Count; k++) Assert.AreEqual(before[k], after[k], $"place {k} moved");
        }

        [Test]
        public void ACrowdOf3000FitsTwoLinesAtOneBaseWithoutSharingGround()
        {
            var lifts = new[] { Lift(new Vector2(0, 0), new Vector2(0, 900), 4), Lift(new Vector2(40, 0), new Vector2(60, 900), 6) };
            bool Trees(float x, float z) => x < -60 || (x - 20) * (x - 20) + (z + 50) * (z + 50) < 100;
            using var layout = LineLayout.Build(1, lifts, null, (x, z) => 0f, Trees);
            for (int k = 0; k < 40; k++) { layout.Grow(0); layout.Grow(1); }
            Assert.GreaterOrEqual(layout.Capacity(0) + layout.Capacity(1), 3000, layout.Describe(0) + " / " + layout.Describe(1));
            for (int l = 0; l < 2; l++) foreach (var p in SlotsOf(layout, l)) Assert.IsFalse(Trees(p.x, p.z), $"lift {l} in the trees at {p}");
            AssertNoSharedGround(layout, 2);
        }

        [Test]
        public void LinesStayOffTreesAndOtherLiftsAndNeverShareGround()
        {
            // Two quads side by side at one base, forest to the west, and a run coming down between them.
            var lifts = new[] { Lift(new Vector2(0, 0), new Vector2(0, 900), 4), Lift(new Vector2(40, 0), new Vector2(60, 900), 6) };
            var runs = new[] { new LineLayout.RunInput { Line = new[] { new Vector2(20, 400), new Vector2(20, 100), new Vector2(20, -100) }, HalfWidth = 15 } };
            bool Trees(float x, float z) => x < -25;
            using var layout = LineLayout.Build(1, lifts, runs, (x, z) => 0f, Trees);
            GrowTo(layout, 0, 300);
            GrowTo(layout, 1, 300);
            var taken = new HashSet<(int, int)>();
            for (int l = 0; l < lifts.Length; l++)
            {
                var slots = SlotsOf(layout, l);
                Assert.GreaterOrEqual(slots.Count, 300, $"lift {l} has room");
                foreach (var p in slots)
                {
                    Assert.IsFalse(Trees(p.x, p.z), $"lift {l}'s line stands in the forest at {p}");
                    // Never in another lift's lane (its chairs and towers).
                    for (int o = 0; o < lifts.Length; o++)
                    {
                        var u = (lifts[o].Top - lifts[o].Bottom).normalized;
                        var d = new Vector2(p.x, p.z) - lifts[o].Bottom;
                        float a = Vector2.Dot(d, u), s = Mathf.Abs(Vector2.Dot(d, new Vector2(u.y, -u.x)));
                        if (a > 0.5f) Assert.Greater(s, lifts[o].LaneHalfWidth, $"lift {l}'s line under lift {o} at {p}");
                    }
                    // No two skiers (of any line) on the same 0.5 m of ground.
                    Assert.IsTrue(taken.Add((Mathf.FloorToInt(p.x / 0.5f), Mathf.FloorToInt(p.z / 0.5f))), $"two skiers share ground at {p}");
                }
            }
        }

        [Test]
        public void TreesRightBehindTheTerminalTurnTheLineInsteadOfStoppingIt()
        {
            // Forest from 6 m behind the bottom terminal's back, open ground to the east: the line leaves the terminal
            // through its throat and folds east.
            bool Trees(float x, float z) => z < -11 && x < 6;
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, Trees);
            Assert.GreaterOrEqual(layout.Capacity(0), 100);
            foreach (var p in SlotsOf(layout, 0)) Assert.IsFalse(Trees(p.x, p.z), $"in the trees at {p}");
        }

        [Test]
        public void TheSameDesignLaysTheSameLine()
        {
            var lifts = new[] { Lift(new Vector2(3, -7), new Vector2(120, 700), 4), Lift(new Vector2(50, 10), new Vector2(-200, 600), 2) };
            Func<float, float, float> ground = (x, z) => 0.05f * x + 0.1f * Mathf.Sin(z * 0.05f);
            using var a = LineLayout.Build(1, lifts, null, ground, (x, z) => (x * x + z * z) % 900 < 30);
            using var b = LineLayout.Build(1, lifts, null, ground, (x, z) => (x * x + z * z) % 900 < 30);
            // The same growth, in the same order, lays the same places too.
            for (int k = 0; k < 3; k++) { a.Grow(1); a.Grow(0); b.Grow(1); b.Grow(0); }
            Assert.AreEqual(a.Slots.Length, b.Slots.Length);
            for (int i = 0; i < a.Slots.Length; i++) Assert.AreEqual(a.Slots[i], b.Slots[i], $"slot {i}");
            for (int i = 0; i < a.LiftSlots.Length; i++) Assert.AreEqual(a.LiftSlots[i], b.LiftSlots[i]);
        }

        [Test]
        public void SteepGroundAndAPlannedLiftHoldNoLine()
        {
            var planned = Lift(Vector2.zero, new Vector2(0, 800), 4);
            planned.Built = false;
            using var layout = LineLayout.Build(1, new[] { planned, Lift(new Vector2(300, 0), new Vector2(300, 800), 4) }, null,
                                                (x, z) => x > 300 ? (x - 300) * 0.6f : 0f, null);
            Assert.AreEqual(0, layout.Capacity(0), "a planned lift has no line");
            // The throat runs under the terminal whatever the ground; past it, nobody stands on the 60% slope.
            int throat = 4 * layout.ThroatRows(1);
            var slots = SlotsOf(layout, 1);
            Assert.Greater(slots.Count, throat);
            for (int k = throat; k < slots.Count; k++) Assert.LessOrEqual(slots[k].x, 300.5f, "nobody stands on the 60% slope");
        }
    }
}
