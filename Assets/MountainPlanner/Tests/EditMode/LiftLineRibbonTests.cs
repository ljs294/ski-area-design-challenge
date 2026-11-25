using System;
using System.Collections.Generic;
using System.IO;
using MountainPlanner.Presentation.Guests;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.26 BB26 (owner, 2026-11-24: "a smoother curve of the line, not a clean break", "one winding ribbon
    /// throughout"): a lift's line is one ribbon from the loader to its back. Wherever it goes, every skier stands about a
    /// row behind the one ahead in their lane (or in the file), the centre never jumps, nobody is passed over, and nobody
    /// stands in the trees with open ground in reach.
    /// </summary>
    public sealed class LiftLineRibbonTests
    {
        static LineLayout.LiftInput Lift(Vector2 bottom, Vector2 top, int seats, float loadSide = 0) => new LineLayout.LiftInput
        {
            Bottom = bottom, Top = top, Seats = seats, Built = true, LoadSide = loadSide,
            Behind = 4, Ahead = 6, HalfWidth = 4, TopBehind = 4, TopAhead = 6, TopHalfWidth = 4, LaneHalfWidth = 5.3f,
        };

        static void GrowTo(LineLayout layout, int lift, int places)
        {
            for (int k = 0; k < 200 && layout.Capacity(lift) < places; k++)
                if (!layout.Grow(lift)) break;
        }

        /// <summary>The line is one ribbon: no breaks in walking order, no jumps, nobody passed over (at most
        /// <paramref name="passedOver"/>).</summary>
        static LineLayout.LineLook AssertRibbon(LineLayout layout, int lift, string name, Func<float, float, bool> trees = null, int passedOver = 0)
        {
            Dump(layout, lift, name);
            var look = layout.Measure(lift);
            Assert.AreEqual(0, look.CentreBreaks, $"{name}: the centre jumped: {look}");
            Assert.AreEqual(0, look.LaneBreaks, $"{name}: a skier stands more than 1.5 rows behind the one ahead: {look}");
            Assert.LessOrEqual(look.PassedOver, passedOver, $"{name}: places passed over: {look}");
            if (trees != null)
            {
                var s = layout.LiftSlots[lift];
                for (int k = 0; k < s.y; k++)
                {
                    var p = layout.Slots[s.x + k];
                    Assert.IsFalse(trees(p.x, p.z), $"{name}: place {k} in the trees at {p}: {look}");
                }
            }
            return look;
        }

        /// <summary>With MP_LINE_DUMP set, writes the line (and the ground round it) for tools/plot (P/W/Q lines, as the probe).</summary>
        static void Dump(LineLayout layout, int lift, string name)
        {
            var dir = Environment.GetEnvironmentVariable("MP_LINE_DUMP");
            if (string.IsNullOrEmpty(dir)) return;
            var sb = new System.Text.StringBuilder();
            int seats = layout.SeatsOf(lift), k = 0;
            sb.AppendLine($"L 0 {seats}");
            float x0 = float.MaxValue, z0 = float.MaxValue, x1 = float.MinValue, z1 = float.MinValue;
            for (int sct = 0; sct < layout.SectionCount(lift); sct++)
            {
                layout.Section(lift, sct, out int rows, out bool file);
                for (int e = 0; e < rows * seats; e++, k++)
                {
                    if (!layout.PlaceOf(lift, k, out var p)) continue;
                    sb.AppendLine($"P 0 {k} {p.x:F2} {p.z:F2} {(file ? "F" : "R")}{sct}");
                    x0 = Mathf.Min(x0, p.x); z0 = Mathf.Min(z0, p.z); x1 = Mathf.Max(x1, p.x); z1 = Mathf.Max(z1, p.z);
                }
            }
            if (x0 <= x1)
            {
                const float cell = LineLayout.CellMetres, margin = 8f;
                x0 = Mathf.Floor((x0 - margin) / cell) * cell; z0 = Mathf.Floor((z0 - margin) / cell) * cell;
                int nx = Math.Min(600, Mathf.CeilToInt((x1 + margin - x0) / cell)), nz = Math.Min(600, Mathf.CeilToInt((z1 + margin - z0) / cell));
                sb.AppendLine($"W 0 {x0:F2} {z0:F2} {nx} {nz}");
                var row = new char[nx];
                for (int j = 0; j < nz; j++)
                {
                    for (int i = 0; i < nx; i++) row[i] = layout.GroundAt(lift, x0 + (i + 0.5f) * cell, z0 + (j + 0.5f) * cell);
                    sb.Append("Q ").Append(row).AppendLine();
                }
            }
            int ci = 0;
            foreach (var (cp, ck, cf) in layout.CentreOf(lift))
                if (ci++ % 5 == 0) sb.AppendLine($"C {cp.x:F2} {cp.y:F2} {ck:F3} {(cf ? 1 : 0)}");
            sb.AppendLine("M " + layout.Measure(lift));
            sb.AppendLine("D " + layout.Describe(lift));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name.Replace(' ', '_') + ".txt"), sb.ToString());
        }

        [TestCase(1)] [TestCase(2)] [TestCase(4)] [TestCase(6)] [TestCase(8)]
        public void OnOpenGroundALineIsOneRibbonFoldedCloseToItsLift(int seats)
        {
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), seats, 2.5f) }, null, (x, z) => 0f, null);
            GrowTo(layout, 0, 1500);
            var look = AssertRibbon(layout, 0, $"open {seats}");
            Assert.GreaterOrEqual(layout.Capacity(0), 1500, $"{layout.Describe(0)}: {look}");
            Assert.AreEqual(0, look.WalkPlaces, $"open ground: never single file: {look}");
            Assert.AreEqual(0, look.SoftPlaces, $"{look}");
            // Folded close: about as many within 30 m of the loader as the old grid maze stood there, or more (2026-11-24,
            // rows 2.13 m, from its front place: 167, 111, 135, 172, 159 for 1, 2, 4, 6, 8 seats; the ribbon at 1.8 m,
            // from the load point: 145, 211, 280+, 300+, 360).
            int near = seats == 1 ? 140 : seats == 2 ? 200 : seats == 8 ? 340 : 280;
            Assert.GreaterOrEqual(look.Within30, near, $"folded close to its lift: {look}");
            Assert.LessOrEqual(look.TightestInner, float.PositiveInfinity);
            Assert.GreaterOrEqual(look.TightestCentre, LineLayout.BendRadius(seats * LineLayout.ColumnMetres) - 1e-3f, $"{look}");
        }

        [Test]
        public void TheFrontRowsStandSquareAndTheLineEasesOutOfTheLoader()
        {
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4, 2.5f) }, null, (x, z) => 0f, null);
            int throat = layout.ThroatRows(0);
            Assert.GreaterOrEqual(throat, 2, "two rows at least, square to the lift");
            var s = layout.LiftSlots[0];
            for (int d = 0; d < throat; d++)
                for (int c = 0; c < 4; c++)
                {
                    var p = layout.Slots[s.x + d * 4 + c];
                    Assert.AreEqual(-LineLayout.RowMetres * (d + 0.5f), p.z, 1e-3f, $"row {d} stands square");
                    Assert.AreEqual(2.5f + 1.5f * LineLayout.ColumnMetres - c * LineLayout.ColumnMetres, p.x, 1e-3f, $"row {d} column {c}");
                }
            var look = layout.Measure(0);
            Assert.LessOrEqual(look.MaxCurvatureRate, 1f / LineLayout.BendRadius(4 * LineLayout.ColumnMetres) + 1e-3f, $"bends ease in: {look}");
        }

        [Test]
        public void ALoneTreeAndACorridorAndAClearingKeepTheRibbonWhole()
        {
            bool Tree(float x, float z) => (x - 2) * (x - 2) + (z + 16) * (z + 16) < 9f;
            using (var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 2) }, null, (x, z) => 0f, Tree))
            {
                GrowTo(layout, 0, 600);
                AssertRibbon(layout, 0, "lone tree", Tree);
            }
            bool Corridor(float x, float z) => x > -4 && x < 4 && z > -40 && z < 2;
            bool Field(float x, float z) => z <= -40 && z > -200 && x > -100 && x < 100;
            bool CorridorTrees(float x, float z) => !(Corridor(x, z) || Field(x, z));
            using (var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, CorridorTrees))
            {
                GrowTo(layout, 0, 800);
                AssertRibbon(layout, 0, "corridor", CorridorTrees);
            }
            bool Clearing(float x, float z) => x > -8 && x < 8 && z > -30 && z < 2;
            bool Gap(float x, float z) => x > -1 && x < 1 && z <= -30 && z > -60;
            bool Beyond(float x, float z) => z <= -60 && z > -200 && x > -100 && x < 100;
            bool ClearingTrees(float x, float z) => !(Clearing(x, z) || Gap(x, z) || Beyond(x, z));
            using (var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, ClearingTrees))
            {
                GrowTo(layout, 0, 800);
                var look = AssertRibbon(layout, 0, "clearing", ClearingTrees);
                Assert.Greater(look.WalkPlaces, 0, "single file through the gap");
            }
        }

        [TestCase(1)] [TestCase(2)] [TestCase(3)] [TestCase(4)] [TestCase(5)] [TestCase(6)]
        public void InPatchyForestTheRibbonStaysWholeAndOutOfTheTrees(int seed)
        {
            bool Patchy(float x, float z)
            {
                if (x > -3 && x < 3 && z > -9 && z < 2) return false;   // the load lane, as far back as the throat
                uint h = (uint)(Mathf.FloorToInt(x / 7f) * 73856093) ^ (uint)(Mathf.FloorToInt(z / 7f) * 19349663) ^ (uint)(seed * 83492791);
                h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
                return h % 100 < 38;
            }
            using var layout = LineLayout.Build(1, new[] { Lift(Vector2.zero, new Vector2(0, 800), 4) }, null, (x, z) => 0f, Patchy);
            GrowTo(layout, 0, 1500);
            Dump(layout, 0, $"patchy {seed}");
            // At most one in a thousand passed over, where single file squeezes past the ribbon's own end in a pocket.
            var look = AssertRibbon(layout, 0, $"patchy {seed}", null, layout.Capacity(0) / 1000);
            Assert.GreaterOrEqual(layout.Capacity(0), 1500, $"{layout.Describe(0)}: {look}");
            // Open ground in reach: hardly ever in the trees (the ribbon: 0-8%; the old grid maze stood 17-33% of these there).
            Assert.LessOrEqual(look.SoftPlaces, layout.Capacity(0) / 10, $"open ground in reach: hardly ever in the trees: {look}");
        }

        [Test]
        public void SkiersStandALittleOffTheirMarksButTheFrontRowsStandSquare()
        {
            var lifts = new[] { Lift(Vector2.zero, new Vector2(0, 800), 4, 2.5f) };
            float3[] marks; float2[] faces;
            LineLayout.Vary = false;
            try
            {
                using var plain = LineLayout.Build(1, lifts, null, (x, z) => 0f, null);
                GrowTo(plain, 0, 600);
                marks = plain.Slots.GetSubArray(plain.LiftSlots[0].x, plain.LiftSlots[0].y).ToArray();
                faces = plain.Facing.GetSubArray(plain.LiftSlots[0].x, plain.LiftSlots[0].y).ToArray();
            }
            finally { LineLayout.Vary = true; }
            using var layout = LineLayout.Build(1, lifts, null, (x, z) => 0f, null);
            GrowTo(layout, 0, 600);
            var s = layout.LiftSlots[0];
            Assert.AreEqual(marks.Length, s.y, "the same places, only moved a little");
            int front = 4 * layout.ThroatRows(0), moved = 0;
            float most = Mathf.Sqrt(LineLayout.VaryAlongMetres * LineLayout.VaryAlongMetres + LineLayout.VaryAcrossMetres * LineLayout.VaryAcrossMetres);
            for (int k = 0; k < s.y; k++)
            {
                var p = layout.Slots[s.x + k];
                float off = math.distance(p.xz, marks[k].xz);
                float turn = Vector2.Angle(new Vector2(faces[k].x, faces[k].y), new Vector2(layout.Facing[s.x + k].x, layout.Facing[s.x + k].y));
                if (k < front)
                {
                    Assert.AreEqual(0f, off, 1e-5f, $"front row place {k} stands on its mark");
                    Assert.AreEqual(0f, turn, 1e-3f, $"front row place {k} faces straight up the lift");
                    continue;
                }
                Assert.LessOrEqual(off, most + 1e-4f, $"place {k}");
                Assert.LessOrEqual(turn, LineLayout.VaryFacingDegrees + 1e-3f, $"place {k}");
                if (off > 0.03f) moved++;
            }
            Assert.Greater(moved, (s.y - front) / 2, "most skiers stand a little off their marks");
            var look = AssertRibbon(layout, 0, "varied");
            Assert.AreEqual(0, look.LaneBreaks, $"{look}");
        }

        [Test]
        public void TwoRibbonsAtOneBaseHold3000AndStayWhole()
        {
            var lifts = new[] { Lift(new Vector2(0, 0), new Vector2(0, 900), 4, 2.5f), Lift(new Vector2(40, 0), new Vector2(60, 900), 6, 2.5f) };
            bool Trees(float x, float z) => x < -60 || (x - 20) * (x - 20) + (z + 50) * (z + 50) < 100;
            using var layout = LineLayout.Build(1, lifts, null, (x, z) => 0f, Trees);
            for (int k = 0; k < 40; k++) { layout.Grow(0); layout.Grow(1); }
            AssertRibbon(layout, 0, "base 0", Trees);
            AssertRibbon(layout, 1, "base 1", Trees);
            Assert.GreaterOrEqual(layout.Capacity(0) + layout.Capacity(1), 3000, layout.Describe(0) + " / " + layout.Describe(1));
        }

        [Test]
        public void AGondolasTopLineIsARibbonToo()
        {
            // The top-line mirror: an input whose bottom is the top station, facing back down the lift.
            var lifts = new[] { Lift(Vector2.zero, new Vector2(0, 800), 8, 2.5f), Lift(new Vector2(0, 800), Vector2.zero, 8, 2.5f) };
            using var layout = LineLayout.Build(1, lifts, null, (x, z) => 0f, null, topSlotsAt: 1);
            GrowTo(layout, 1, 800);
            AssertRibbon(layout, 1, "top line");
        }

        [Test]
        public void TheTightestBendKeepsTheInnerLaneFromFolding()
        {
            foreach (int seats in new[] { 1, 2, 4, 6, 8 })
            {
                float w = seats * LineLayout.ColumnMetres;
                float inner = (w - LineLayout.ColumnMetres) * 0.5f;
                Assert.LessOrEqual(inner / LineLayout.BendRadius(w), LineLayout.MaxInnerBend + 1e-4f, $"{seats} seats");
                Assert.GreaterOrEqual(2 * LineLayout.MinTurnRadius(w) - w, LineLayout.FoldGapMetres - 1e-4f, $"{seats} seats: folds apart");
            }
        }
    }
}
