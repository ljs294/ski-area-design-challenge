using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task P3-10, sections finding 7 (save v8): a trail keeps its clearing as cells written down as rings, not strokes.
    public sealed class TrailShapeTests
    {
        // Keyed noise for the tests' masks (SH-04: no unkeyed randomness).
        sealed class Keyed
        {
            ulong _s;
            public Keyed(ulong seed) { _s = seed * 0x9E3779B97F4A7C15UL + 1; }
            public int Next(int below)
            {
                _s ^= _s << 13; _s ^= _s >> 7; _s ^= _s << 17;
                return (int)(_s % (ulong)below);
            }
        }

        static TrailMask Noise(ulong seed, int size, int density)
        {
            var k = new Keyed(seed);
            var b = new TrailMask.Builder(-size, size);
            for (int j = -size; j < size; j++)
                for (int i = -size; i < size; i++)
                    if (k.Next(100) < density) b.Set(j, i, i + 1, true);
            return b.ToMask();
        }

        static Stroke Line(int width, bool erase, params (int x, int y)[] pts)
        {
            var p = new PointMm[pts.Length];
            for (int i = 0; i < pts.Length; i++) p[i] = new PointMm(pts[i].x, pts[i].y);
            return new Stroke(width, erase, p);
        }

        [Test]
        public void TS01_RingsGiveBackTheirCellsExactly([Values(1UL, 2UL, 3UL, 4UL, 5UL, 6UL)] ulong seed, [Values(15, 45, 70, 92)] int density)
        {
            var mask = Noise(seed, 24, density);   // single cells, pinches, holes, holes in holes
            var shape = TrailShape.Of(mask);
            Assert.That(TrailShape.Fill(shape.Rings), Is.EqualTo(mask), "the even-odd cells of the rings are the mask");
            var again = TrailShape.FromRings(shape.Rings);
            Assert.That(again.Rings.Count, Is.EqualTo(shape.Rings.Count));
            for (int r = 0; r < shape.Rings.Count; r++) Assert.That(again.Rings[r], Is.EqualTo(shape.Rings[r]), "canonical: the same rings again");
        }

        [Test]
        public void TS02_StrokesStampAsTheFootprintRuleSays()
        {
            var strokes = new[]
            {
                Line(30_480, false, (0, 0), (40_000, 120_000), (90_000, 160_000)),
                Line(12_192, true, (30_000, 90_000), (60_000, 110_000)),
                Line(7_620, false, (45_000, 100_000)),
                Line(60_960, false, (-20_000, -5_000), (-20_000, 30_000)),
            };
            var shape = TrailShape.FromStrokes(null, strokes);
            int checkedCells = 0;
            for (int j = -120; j < 760; j += 3)
                for (int i = -260; i < 480; i += 3)
                {
                    bool want = false;
                    foreach (var s in strokes)
                        for (int k = 0; k < Math.Max(1, s.Points.Count - 1); k++)
                            if (TrailMask.Builder.InCapsule(TrailMask.CentreMm(i), TrailMask.CentreMm(j), s.Points[k], s.Points[Math.Min(k + 1, s.Points.Count - 1)], s.WidthMm))
                                want = !s.Erase;
                    Assert.That(shape.Mask.Contains(i, j), Is.EqualTo(want), $"cell {i}, {j}");
                    checkedCells++;
                }
            Assert.That(checkedCells, Is.GreaterThan(50_000));
            Assert.That(TrailShape.Fill(shape.Rings), Is.EqualTo(shape.Mask));
        }

        [Test]
        public void TS03_BothSidesOfACutMakeTheWhole()
        {
            var shape = TrailShape.FromStrokes(null, new[] { Line(45_720, false, (0, 0), (30_000, 200_000), (-10_000, 380_000)) });
            var cut = new Cut(new PointMm(18_123, 150_777), 3_001, 19_999, true);
            var ahead = shape.Clip(cut);
            var behind = shape.Clip(cut.Opposite);
            Assert.That(ahead.IsEmpty || behind.IsEmpty, Is.False);
            Assert.That(ahead.Union(behind), Is.EqualTo(shape));
            Assert.That(ahead.Mask.Cells + behind.Mask.Cells, Is.EqualTo(shape.Mask.Cells), "no cell on both sides");
            Assert.That(ahead.Mask.Touches(behind.Mask), Is.True);
        }

        [Test]
        public void TS04_PaintingOntoAShapeLeavesTheRestAlone()
        {
            var first = TrailShape.FromStrokes(null, new[] { Line(30_480, false, (0, 0), (0, 300_000)) });
            var more = TrailShape.FromStrokes(first, new[] { Line(15_240, true, (0, 280_000), (0, 320_000)), Line(30_480, false, (0, 0), (60_000, -40_000)) });
            for (int j = first.Mask.Row0; j < first.Mask.RowEnd; j++)
                for (int i = first.Mask.Column0; i < first.Mask.ColumnEnd; i++)
                {
                    long y = TrailMask.CentreMm(j);
                    if (y > 250_000) continue;   // away from the erase
                    if (first.Mask.Contains(i, j)) Assert.That(more.Mask.Contains(i, j), Is.True, $"cell {i}, {j} kept");
                }
            Assert.That(more.Mask.Contains(0, 1_160), Is.False, "the erase took its end");
            // Many repaints never wear the clearing down: the cells are the authority, not a redrawn outline.
            var shape = first;
            for (int k = 0; k < 20; k++) shape = TrailShape.FromStrokes(TrailShape.FromRings(shape.Rings), new[] { Line(7_620, false, (500_000, 0)) });
            Assert.That(shape.Mask.Cells - first.Mask.Cells, Is.EqualTo(TrailShape.FromStrokes(null, new[] { Line(7_620, false, (500_000, 0)) }).Mask.Cells));
        }

        [Test]
        public void TS05_LegacyPartsConvertAsTheyWerePainted()
        {
            var stroke = Line(30_480, false, (0, 0), (20_000, 200_000));
            var cut = new Cut(new PointMm(10_000, 100_000), 2_000, 20_000, false);
            var parts = new[] { new FootprintPart(new[] { stroke }, new[] { cut }), new FootprintPart(new[] { Line(15_240, false, (80_000, 0), (80_000, 50_000)) }) };
            var shape = TrailShape.FromParts(parts);
            var whole = TrailShape.FromStrokes(null, new[] { stroke });
            Assert.That(shape.Mask.Cells, Is.EqualTo(whole.Clip(cut).Mask.Cells + TrailShape.FromStrokes(null, new[] { parts[1].Strokes[0] }).Mask.Cells));
        }

        [Test]
        public void TS06_ARunsOutlineStaysSmall([Values(0, 5, 10, 15, 20, 25, 30, 35, 40, 45)] int degrees)
        {
            double a = degrees * Math.PI / 180;
            var end = (x: (int)Math.Round(600_000 * Math.Sin(a)), y: (int)Math.Round(600_000 * Math.Cos(a)));
            var shape = TrailShape.FromStrokes(null, new[] { Line(30_480, false, (0, 0), end) });
            Assert.That(shape.Rings.Count, Is.EqualTo(1));
            TestContext.WriteLine($"{degrees}°: {shape.Points} points, {shape.Mask.Cells} cells");
            // Today's saves hold about 600 stroke points a trail; the outline stays in that range (its staircase is ~5,000).
            Assert.That(shape.Points, Is.LessThan(1_500), "a 600 m run's outline, not its staircase");
        }
    }
}
