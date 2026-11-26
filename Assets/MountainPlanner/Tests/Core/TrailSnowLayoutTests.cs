using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snow;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Task 4.07 PR B1: SN-03 v3's fine field layout over the trails. Engine-free.
    public sealed class TrailSnowLayoutTests
    {
        // A 1,024 m square grid centred on the site.
        const int Grid = 128;
        const long Origin = -512_000;

        static TrailSnowInput Straight(int x0, int y0, int x1, int y1, int widthMm, int[] starts = null, int[] edges = null)
        {
            var stroke = new Stroke(widthMm, false, new[] { new PointMm(x0, y0), new PointMm(x1, y1) });
            int row0 = int.MaxValue, rowEnd = int.MinValue;
            TrailMask.Builder.RowsOf(stroke, ref row0, ref rowEnd);
            var b = new TrailMask.Builder(row0, rowEnd);
            b.Stamp(stroke);
            return new TrailSnowInput
            {
                Mask = b.ToMask(), LineXMm = new[] { x0, x1 }, LineYMm = new[] { y0, y1 }, WidthMm = widthMm,
                SegmentStartMm = starts ?? new[] { 0 }, SegmentEdge = edges ?? new[] { 0 },
            };
        }

        static bool InMask(TrailMask m, long x, long y)
            => m.Contains((int)Math.Floor((x + 125) / 250.0), (int)Math.Floor((y + 125) / 250.0));

        [Test]
        public void EveryTrailCellIsCoveredOnce()
        {
            var t = Straight(0, -200_000, 0, 200_000, 30_000);
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { t }, 2_000_000);
            Assert.That(L.FineMetres, Is.EqualTo(1));
            for (int b = 1; b < L.Blocks; b++) Assert.That(L.BlockCell[b], Is.GreaterThan(L.BlockCell[b - 1]), "blocks ascend, once each");
            int owned = 0;
            for (int f = 0; f < L.FineCells; f++)
            {
                L.CentreMm(f, out long x, out long y);
                bool inside = InMask(t.Mask, x, y);
                Assert.That(L.Owner[f], Is.EqualTo(inside ? 0 : -1), $"fine cell at ({x}, {y})");
                if (inside) owned++;
            }
            Assert.That(L.TrailCells[0], Is.EqualTo(owned));
            // About 30 m × 400 m of 1 m cells (the capsule's round ends add a little).
            Assert.That(owned, Is.InRange(30 * 400, 30 * 400 + 800));
            // Every mask cell's 8 m cell is a block.
            for (int j = t.Mask.Row0; j < t.Mask.RowEnd; j += 7)
            {
                var r = t.Mask.Row(j);
                for (int k = 0; k < r.Count; k += 2)
                {
                    long x = r[k] * 250L, y = j * 250L;
                    int cell = (int)(Math.Floor((y - Origin) / 8000.0) * Grid + Math.Floor((x - Origin) / 8000.0));
                    Assert.That(L.BlockOf(cell), Is.GreaterThanOrEqualTo(0));
                }
            }
        }

        [Test]
        public void TheSameTrailsGiveTheSameBytes()
        {
            var a = new[] { Straight(0, -200_000, 0, 200_000, 30_000), Straight(-100_000, 0, 150_000, 40_000, 20_000) };
            var b = new[] { Straight(0, -200_000, 0, 200_000, 30_000), Straight(-100_000, 0, 150_000, 40_000, 20_000) };
            var l1 = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, a, 2_000_000, 7);
            var l2 = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, b, 2_000_000, 7);
            Assert.That(l2.Hash(), Is.EqualTo(l1.Hash()));
            var l3 = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, b, 2_000_000, 8);
            Assert.That(l3.Hash(), Is.Not.EqualTo(l1.Hash()), "the network's hash is part of it");
        }

        [Test]
        public void AnOverlapGoesToTheNearestCentreline()
        {
            // Two parallel north-running trails 20 m apart, each 40 m wide: they overlap for 20 m.
            var w = Straight(-10_000, -100_000, -10_000, 100_000, 40_000);
            var e = Straight(10_000, -100_000, 10_000, 100_000, 40_000);
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { w, e }, 2_000_000);
            int both = 0;
            for (int f = 0; f < L.FineCells; f++)
            {
                L.CentreMm(f, out long x, out long y);
                if (!(InMask(w.Mask, x, y) && InMask(e.Mask, x, y))) continue;
                both++;
                Assert.That(L.Owner[f], Is.EqualTo(x < 0 ? 0 : 1), $"at x = {x} mm");
            }
            Assert.That(both, Is.GreaterThan(19 * 200));
        }

        [Test]
        public void AlongRunsFromTheHeadAndLanesCrossTheWidth()
        {
            // A trail heading north: left is west, so lane 0 is the west edge.
            var t = Straight(0, -200_000, 0, 200_000, 32_000);
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { t }, 2_000_000);
            var seen = new HashSet<int>();
            for (int f = 0; f < L.FineCells; f++)
            {
                if (L.Owner[f] != 0) continue;
                L.CentreMm(f, out long x, out long y);
                if (y > -200_000 && y < 200_000) Assert.That(L.AlongMm[f], Is.EqualTo(y + 200_000).Within(1), "along");
                if (Math.Abs(x) < 16_000) Assert.That(L.Lane[f], Is.EqualTo((int)((16_000 + x) / 4_000)), $"lane at x = {x}");
                seen.Add(L.Lane[f]);
            }
            Assert.That(seen.Count, Is.EqualTo(TrailSnowLayout.Lanes), "all eight lanes");
            Assert.That(TrailSnowLayout.LaneOf(16_000, 32_000), Is.EqualTo(0));
            Assert.That(TrailSnowLayout.LaneOf(-16_000, 32_000), Is.EqualTo(7));
            Assert.That(TrailSnowLayout.LaneOf(1, 32_000), Is.EqualTo(3));
            Assert.That(TrailSnowLayout.LaneOf(-1, 32_000), Is.EqualTo(4));
        }

        [Test]
        public void SegmentsMapOneToOneToTheNetworksEdges()
        {
            // Three segments: 0–150 m is edge 5, 150–300 m edge 9, beyond edge 2.
            var t = Straight(0, -200_000, 0, 200_000, 30_000, new[] { 0, 150_000, 300_000 }, new[] { 5, 9, 2 });
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { t }, 2_000_000);
            var count = new Dictionary<int, int>();
            for (int f = 0; f < L.FineCells; f++)
            {
                if (L.Owner[f] < 0) { Assert.That(L.Edge[f], Is.EqualTo(-1)); continue; }
                int a = L.AlongMm[f];
                int want = a < 150_000 ? 5 : a < 300_000 ? 9 : 2;
                Assert.That(L.Edge[f], Is.EqualTo(want), $"along {a}");
                count[want] = count.TryGetValue(want, out int c) ? c + 1 : 1;
            }
            Assert.That(count.Keys, Is.EquivalentTo(new[] { 5, 9, 2 }), "every segment has cells");
        }

        [Test]
        public void TheScaleStepsPastTheCap()
        {
            var t = new[] { Straight(0, -200_000, 0, 200_000, 30_000) };
            var fine = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, t, 2_000_000);
            int blocks = fine.Blocks;
            Assert.That(fine.FineCells, Is.EqualTo(blocks * 64));
            var two = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, t, blocks * 64 - 1);
            Assert.That((two.FineMetres, two.FineCells, two.OverCap), Is.EqualTo((2, blocks * 16, false)));
            var four = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, t, blocks * 16 - 1);
            Assert.That((four.FineMetres, four.FineCells, four.OverCap), Is.EqualTo((4, blocks * 4, false)));
            var over = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, t, blocks * 4 - 1);
            Assert.That((over.FineMetres, over.OverCap), Is.EqualTo((4, true)));
            // At 2 m a 16 m trail still has all eight lanes.
            var narrow = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { Straight(0, -100_000, 0, 100_000, 16_000) }, 1);
            var lanes = new HashSet<int>();
            var twoM = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { Straight(0, -100_000, 0, 100_000, 16_000) },
                narrow.Blocks * 16);
            Assert.That(twoM.FineMetres, Is.EqualTo(2));
            for (int f = 0; f < twoM.FineCells; f++) if (twoM.Owner[f] == 0) lanes.Add(twoM.Lane[f]);
            Assert.That(lanes.Count, Is.EqualTo(8));
        }

        [Test]
        public void TrailsOffTheGridAreSkipped()
        {
            var t = Straight(0, 600_000, 0, 700_000, 30_000);   // north of the 1,024 m grid
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { t }, 2_000_000);
            Assert.That((L.Blocks, L.FineCells, L.TrailCells[0]), Is.EqualTo((0, 0, 0)));
        }

        // Task 4.29 PR B: each 8 m cell draws its grass as the trail that owns most of its fine cells.
        [Test]
        public void GrassOwnersAreEachCellsMainTrail()
        {
            var ns = Straight(0, -200_000, 0, 200_000, 30_000);
            var ew = Straight(-200_000, 50_000, 200_000, 50_000, 30_000);
            var L = TrailSnowLayout.Build(Grid, Grid, Origin, Origin, new[] { ns, ew }, 2_000_000);
            var map = TrailCellOwners.Build(L);
            Assert.That(map.Length, Is.EqualTo(Grid * Grid));
            int CellAt(long x, long y) => (int)(Math.Floor((y - Origin) / 8000.0) * Grid + Math.Floor((x - Origin) / 8000.0));
            Assert.That(map[CellAt(4_000, -100_000)], Is.EqualTo(1), "on the north-south trail alone: trail 0, + 1");
            Assert.That(map[CellAt(-100_000, 52_000)], Is.EqualTo(2), "on the east-west trail alone");
            Assert.That(map[CellAt(300_000, 300_000)], Is.EqualTo(0), "off every trail");
            int[] seen = new int[3];
            for (int b = 0; b < L.Blocks; b++)
            {
                int cell = L.BlockCell[b], o = map[cell];
                seen[o]++;
                // The owner holds at least as many of the block's fine cells as any other trail.
                int[] counts = new int[2];
                for (int k = b * L.BlockCells; k < (b + 1) * L.BlockCells; k++) if (L.Owner[k] >= 0) counts[L.Owner[k]]++;
                if (counts[0] + counts[1] == 0) { Assert.That(o, Is.Zero); continue; }
                Assert.That(o, Is.InRange(1, 2));
                Assert.That(counts[o - 1], Is.GreaterThanOrEqualTo(counts[2 - o]), $"block {b}");
            }
            for (int c = 0; c < map.Length; c++) if (L.BlockOf(c) < 0) Assert.That(map[c], Is.Zero, "only blocks have trails");
            Assert.That(seen[1] > 0 && seen[2] > 0, Is.True);
            Assert.That(TrailCellOwners.Build(L), Is.EqualTo(map), "the same layout, the same map");
        }
    }
}
