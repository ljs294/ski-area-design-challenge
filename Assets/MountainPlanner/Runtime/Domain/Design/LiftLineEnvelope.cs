using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// GR-11 v2's lift-line envelope (owner, task 07 round 3, finding #3, SK24): the trench under the chairs leaving the
    /// bottom terminal, like the dug-in loading area in front of a real lift. From the terminal's front, wherever the chairs
    /// fall short of TW-01 v2 (the low zone's rising line, 15 ft past it), the ground under both ropes' chair paths (and
    /// 5 ft either side) comes
    /// down, level across, just enough for them; under a station tower whose 15 ft of steel would stand too high, a pit
    /// (TW-02 v3). Cut only, 1:1 sides, at most 10 ft deep; never within 15 ft of a line tower's base (TF-11). Everything it
    /// changes is cleared (CL-09 v3). Positions in the edit lattice's frame.
    /// </summary>
    public static class LiftLineEnvelope
    {
        /// <summary>At most 10 ft deep (GR-11 v2); deeper needs are met by raising the terminal instead.</summary>
        public const double CapMetres = 3.048;
        /// <summary>Past both ropes' chair paths, 5 ft either side (as LF-12's pads): the ground under a chair's edge is read
        /// from lattice posts up to 1.4 m away, which must be cut too.</summary>
        public const double MarginMetres = 1.524;
        /// <summary>TF-11: never within 15 ft of a line tower's base.</summary>
        public const double TowerKeepMetres = 4.572;
        /// <summary>A pit's level bench under a station tower, 5 ft either side of its base along the line.</summary>
        public const double PitHalfMetres = 1.524;
        const double FaceSlope = 1;
        /// <summary>A little deeper than the chairs need, so the ground read between lattice posts is below it too.</summary>
        public const double OverCutMetres = 0.05;
        /// <summary>Samples this close along the line are one stretch of floor, sloping straight between them.</summary>
        const double JoinMetres = 1.01;

        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            public double CutCubicMetres { get; internal set; }
            /// <summary>The deepest cut the chairs needed (before the cap), metres; 0 when the ground let them climb.</summary>
            public double NeededMetres { get; internal set; }
            /// <summary>The deepest cut made (within the cap), and the trench's full width with its faces, metres.</summary>
            public double DeepestMetres { get; internal set; }
            public double WidthMetres { get; internal set; }
            /// <summary>Where it runs along the line, metres from the bottom bullwheel (0, 0 when nothing is cut).</summary>
            public double FromS { get; internal set; }
            public double ToS { get; internal set; }
            public int ClearedCells { get; internal set; }

            /// <summary>This cut followed by a deeper one over it.</summary>
            internal Result Then(Result next) => new Result
            {
                Edits = next.Edits,
                CutCubicMetres = CutCubicMetres + next.CutCubicMetres,
                NeededMetres = Math.Max(NeededMetres, DeepestMetres + next.NeededMetres),   // a deeper re-check counts for the raise
                DeepestMetres = DeepestMetres + next.DeepestMetres,
                WidthMetres = Math.Max(WidthMetres, next.WidthMetres),
                FromS = CutCubicMetres > 0 ? Math.Min(FromS, next.FromS) : next.FromS,
                ToS = Math.Max(ToS, next.ToS),
                ClearedCells = ClearedCells + next.ClearedCells,
            };
        }

        /// <summary>
        /// Designs and grades the envelope for <paramref name="rope"/> (solved over <paramref name="line"/>, the ground in
        /// <paramref name="current"/>) on the line from the bottom bullwheel (<paramref name="bx"/>, <paramref name="by"/>)
        /// toward the top one. <paramref name="stationS"/> is the bottom station tower's base and <paramref name="pit"/> how
        /// far the ground under it must come down. <paramref name="overCut"/> is how much deeper than measured to cut: real
        /// ground has bumps between the edit lattice's posts that only a slightly deeper cut at the posts takes away.
        /// </summary>
        public static Result Grade(EditedHeights current, LiftRope rope, LineGround line, LiftType type, double bx, double by,
                                   double tx, double ty, double stationS, double pit, double overCut = OverCutMetres)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = (tx - bx) / length, uy = (ty - by) / length;
            // From the terminal's front to 15 ft short of the first line tower past the station tower.
            LiftRope.Zones(type, length, out double front, out _, out _, out double end);
            foreach (var tower in rope.Towers)
                if (tower.BaseS > stationS + 1) { end = Math.Min(end, tower.BaseS - TowerKeepMetres); break; }
            var samples = new List<(double s, double floor)>();
            double needed = 0;
            for (double s = Math.Ceiling(front); s <= end; s += 1)
            {
                if (!rope.ClearanceAt(s, out double clearance, out double need) || need - clearance <= LiftRope.ClearanceTolerance) continue;
                double depth = need - clearance + overCut;
                needed = Math.Max(needed, depth);
                samples.Add((s, line.UnderChairs(s) - Math.Min(depth, CapMetres)));
            }
            if (pit > 0)
            {
                needed = Math.Max(needed, pit);
                double g = current.At(bx + ux * stationS, by + uy * stationS);
                for (double ds = -PitHalfMetres; ds <= PitHalfMetres + 1e-9; ds += 0.5)
                    samples.Add((stationS + ds, g - Math.Min(pit + overCut, CapMetres)));
            }
            var result = new Result { Edits = current.Edits, NeededMetres = needed };
            if (samples.Count == 0) return result;

            samples.Sort((p, q) => p.s.CompareTo(q.s));
            // Each stretch runs 1.5 m past its ends at the same floor, so the ground read between lattice posts there is cut too.
            var ends = new List<(double s, double floor)>();
            for (int k = 0; k < samples.Count; k++)
            {
                for (double e = 0.5; e <= 1.5 + 1e-9; e += 0.5)
                {
                    if (k == 0 || samples[k].s - samples[k - 1].s > JoinMetres) ends.Add((samples[k].s - e, samples[k].floor));
                    if ((k == samples.Count - 1 || samples[k + 1].s - samples[k].s > JoinMetres) && samples[k].s + e <= end + 1e-9)
                        ends.Add((samples[k].s + e, samples[k].floor));   // never into TF-11's 15 ft round a line tower
                }
            }
            samples.AddRange(ends);
            samples.Sort((p, q) => p.s.CompareTo(q.s));
            // The floor along the line: straight between neighbouring samples, rising 1:1 past a stretch's ends.
            double FloorAt(double s)
            {
                double best = double.MaxValue;
                for (int k = 0; k < samples.Count; k++)
                {
                    var (sk, fk) = samples[k];
                    best = Math.Min(best, fk + Math.Abs(s - sk) * FaceSlope);
                    if (k + 1 < samples.Count && samples[k + 1].s - sk <= JoinMetres && s >= sk && s <= samples[k + 1].s)
                    {
                        double t = (s - sk) / (samples[k + 1].s - sk);
                        best = Math.Min(best, fk + (samples[k + 1].floor - fk) * t);
                    }
                }
                return best;
            }
            double half = type.GaugeMetres / 2 + type.ChairHalfWidthMetres + MarginMetres;
            double from = double.MaxValue, to = double.MinValue;
            foreach (var (s, _) in samples) { from = Math.Min(from, s); to = Math.Max(to, s); }
            double reach = CapMetres / FaceSlope + 2;
            var lattice = current.Lattice;
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(int c, int r)
            {
                var key = EditTileKey.Of(c, r);
                if (!builders.TryGetValue(key, out var bd)) builders[key] = bd = current.Edits.Get(key).Edit();
                return bd;
            }
            // The cells round the trench, in the line's frame.
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (double s in new[] { from - reach, to + reach })
                foreach (double d in new[] { -half - reach, half + reach })
                {
                    double x = bx + ux * s + uy * d, y = by + uy * s - ux * d;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
            int c0 = (int)Math.Floor(minX - lattice.West), c1 = (int)Math.Ceiling(maxX - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - maxY), r1 = (int)Math.Ceiling(lattice.North - minY);
            double cut = 0, deepest = 0, widest = 0;
            int cleared = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c) - bx, py = lattice.Y(r) - by;
                    double s = px * ux + py * uy, d = px * uy - py * ux;
                    if (s < from - reach || s > to + reach) continue;
                    double design = FloorAt(s) + Math.Max(0, Math.Abs(d) - half) * FaceSlope;
                    double ground = current.AtPoint(c, r);
                    if (design >= ground - 1e-4) continue;
                    double source = current.Source.At(lattice.X(c), lattice.Y(r));
                    long mm = (long)Math.Round((design - source) * 1000, MidpointRounding.AwayFromZero);
                    mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                    double change = ground - (source + mm / 1000.0);
                    if (change <= 1e-6) continue;
                    var key = EditTileKey.Of(c, r);
                    var bd = For(c, r);
                    bd.SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, (int)mm);
                    cut += change;
                    deepest = Math.Max(deepest, change);
                    widest = Math.Max(widest, Math.Abs(d));
                    if (change > 0.05)
                    {
                        bd.SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                        cleared++;
                    }
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var bd in builders.Values) tiles.Add(bd.Build());
            result.Edits = current.Edits.With(tiles);
            result.CutCubicMetres = cut;
            result.DeepestMetres = deepest;
            result.WidthMetres = 2 * widest;
            result.FromS = from;
            result.ToS = to;
            result.ClearedCells = cleared;
            return result;
        }
    }
}
