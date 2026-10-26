using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// LF-12 v4 (owner, 2026-10-20, LT35; was v3's cut only): the pad is the terminal's footprint plus 5 ft all round,
    /// **flattened** to the terminal's level: ground above is cut and ground below filled, with 1:1 faces out to the ground
    /// (GR-02, the trail doctrine) within GR-11's 50 ft terminal-pad envelope (a face that hasn't met the ground there is
    /// clipped, and the step is reported). So the terminal and its piers stand on the pad where it's placed. Everything it
    /// changes is cleared (CL-09 v2).
    /// LF-12 v5 (owner, LT42, second review E1): a detachable terminal's pad ends at the hood's middle. Its front half (nose,
    /// bullwheel, station rail) stands over natural ground: nothing there is filled and no face reaches into it; only ground
    /// that would rise above the pad's level, into the terminal itself, is cut back to it (with 1:1 faces), as the
    /// terminal must stand clear.
    /// Positions in the edit lattice's frame; (ux, uy) points toward the other terminal.
    /// </summary>
    public static class TerminalPad
    {
        /// <summary>LF-12 v2: 5 ft round the footprint (owner, round 3).</summary>
        public const double MarginMetres = 1.524;
        /// <summary>GR-11 v2: the faces' envelope past the pad, 50 ft.</summary>
        public const double FaceReachMetres = 15.24;
        public const double FaceSlope = 1;

        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            public double CutCubicMetres { get; internal set; }
            public double FillCubicMetres { get; internal set; }
            /// <summary>The largest height difference left where a face reached the envelope without meeting the ground.</summary>
            public double ClippedStepMetres { get; internal set; }
            public int ClearedCells { get; internal set; }
        }

        /// <summary>
        /// Grades a pad around a bullwheel at (<paramref name="x"/>, <paramref name="y"/>): from <paramref name="behind"/>
        /// metres behind it to <paramref name="ahead"/> in front, <paramref name="halfWidth"/> either side (the footprint;
        /// the margin is added here), level at <paramref name="elevation"/>, over the current edited heights.
        /// <paramref name="padAhead"/> (LF-12 v5): where a detachable's pad ends in front of the bullwheel, the hood's
        /// middle; past it the ground is only cut where it rises above the pad. NaN (or <paramref name="ahead"/>): the
        /// whole footprint, as before.
        /// </summary>
        public static Result Grade(EditedHeights current, double x, double y, double ux, double uy,
                                   double behind, double ahead, double halfWidth, double elevation, double padAhead = double.NaN)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double b = behind + MarginMetres, a = ahead + MarginMetres, w = halfWidth + MarginMetres;
            bool front = !double.IsNaN(padAhead) && padAhead < ahead - 1e-9;   // the front half left natural (LF-12 v5)
            double p = front ? padAhead : a;   // the levelled pad's front edge (no margin past the hood's middle)
            double reach = Math.Sqrt(Math.Max(a, b) * Math.Max(a, b) + w * w) + FaceReachMetres + 2;
            var lattice = current.Lattice;
            int c0 = (int)Math.Floor(x - reach - lattice.West), c1 = (int)Math.Ceiling(x + reach - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - (y + reach)), r1 = (int)Math.Ceiling(lattice.North - (y - reach));
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(int c, int r)
            {
                var key = EditTileKey.Of(c, r);
                if (!builders.TryGetValue(key, out var bd)) builders[key] = bd = current.Edits.Get(key).Edit();
                return bd;
            }
            // Distance outside a pad rectangle ending at `to` in front (0 inside), in the line's frame.
            double OutsideTo(double px, double py, double to)
            {
                double rx = px - x, ry = py - y;
                double along = rx * ux + ry * uy, across = -rx * uy + ry * ux;
                double da = along < -b ? -b - along : along > to ? along - to : 0;
                double dc = Math.Abs(across) > w ? Math.Abs(across) - w : 0;
                return Math.Sqrt(da * da + dc * dc);
            }
            double Outside(double px, double py) => OutsideTo(px, py, a);
            bool InFront(double px, double py) => front && (px - x) * ux + (py - y) * uy > p;
            // The design height at a point: cut and fill toward the pad (to its front edge), and in front of a detachable's
            // pad only cut, where the ground rises above the pad toward the terminal's whole footprint.
            double Design(double px, double py, double ground)
            {
                double cutOff = Outside(px, py);
                if (InFront(px, py)) return Math.Min(elevation + cutOff * FaceSlope, ground);
                double fillOff = front ? OutsideTo(px, py, p) : cutOff;
                return Math.Max(elevation - fillOff * FaceSlope, Math.Min(elevation + cutOff * FaceSlope, ground));
            }
            double Reach(double px, double py) => front && !InFront(px, py) ? Math.Min(Outside(px, py), OutsideTo(px, py, p)) : Outside(px, py);
            var result = new Result();
            double cut = 0, fill = 0, clipped = 0;
            int cleared = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c), py = lattice.Y(r);
                    double off = Reach(px, py);
                    if (off > FaceReachMetres) continue;
                    double ground = current.AtPoint(c, r);
                    double design = Design(px, py, ground);   // cut and fill (LF-12 v4); cut only in front of a detachable's pad (v5)
                    if (off > FaceReachMetres - 1 && Math.Abs(design - ground) > 1e-3) clipped = Math.Max(clipped, Math.Abs(design - ground));
                    if (Math.Abs(design - ground) < 1e-4) design = ground;   // untouched past where the face met the ground
                    double source = current.Source.At(px, py);
                    long mm = (long)Math.Round((design - source) * 1000, MidpointRounding.AwayFromZero);
                    mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                    double change = source + mm / 1000.0 - ground;
                    if (Math.Abs(change) < 1e-6 && off > 0) continue;
                    if (change > 0) fill += change; else cut -= change;
                    var key = EditTileKey.Of(c, r);
                    For(c, r).SetDeltaMm(c - key.FirstColumn, r - key.FirstRow, (int)mm);
                }
            // CL-09 v2: clear the pad and every cell its faces changed.
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double cx = lattice.X(c) + 0.5, cy = lattice.Y(r) - 0.5;
                    double off = Reach(cx, cy);
                    if (off > FaceReachMetres) continue;
                    bool onPad = off <= 0 && !InFront(cx, cy);
                    if (!onPad)
                    {
                        double before = current.AtPoint(c, r), design = Design(cx, cy, before);
                        if (Math.Abs(design - before) < 0.05) continue;
                    }
                    var key = EditTileKey.Of(c, r);
                    For(c, r).SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                    cleared++;
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var bd in builders.Values) tiles.Add(bd.Build());
            result.Edits = current.Edits.With(tiles);
            result.CutCubicMetres = cut;
            result.FillCubicMetres = fill;
            result.ClippedStepMetres = clipped;
            result.ClearedCells = cleared;
            return result;
        }
    }
}
