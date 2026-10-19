using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// LF-12 v2 (owner, task 07, SK19; round 3, SK25): every terminal is built on a level pad, so only its plinth comes out
    /// of the ground. The pad hugs the terminal: its footprint plus 5 ft all round, level at the terminal's base elevation,
    /// the same at either end (the unload area beyond comes with the off-ramp models). Around it, 1:1 cut and fill faces
    /// run out to the ground (GR-02's slope) within GR-11's 50 ft terminal-pad envelope (an exception to GR-06): a face that
    /// hasn't met the ground there is clipped, and the step is reported. Everything the pad and its faces touch is cleared
    /// (CL-09 v2). Positions in the edit lattice's frame; (ux, uy) points toward the other terminal.
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
        /// </summary>
        public static Result Grade(EditedHeights current, double x, double y, double ux, double uy,
                                   double behind, double ahead, double halfWidth, double elevation)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            double b = behind + MarginMetres, a = ahead + MarginMetres, w = halfWidth + MarginMetres;
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
            // Distance outside the pad rectangle (0 inside), in the line's frame.
            double Outside(double px, double py)
            {
                double rx = px - x, ry = py - y;
                double along = rx * ux + ry * uy, across = -rx * uy + ry * ux;
                double da = along < -b ? -b - along : along > a ? along - a : 0;
                double dc = Math.Abs(across) > w ? Math.Abs(across) - w : 0;
                return Math.Sqrt(da * da + dc * dc);
            }
            var result = new Result();
            double cut = 0, fill = 0, clipped = 0;
            int cleared = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c), py = lattice.Y(r);
                    double off = Outside(px, py);
                    if (off > FaceReachMetres) continue;
                    double ground = current.AtPoint(c, r);
                    double rise = off * FaceSlope;
                    double design = Math.Max(elevation - rise, Math.Min(elevation + rise, ground));
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
                    double off = Outside(lattice.X(c) + 0.5, lattice.Y(r) - 0.5);
                    if (off > FaceReachMetres) continue;
                    bool onPad = off <= 0;
                    if (!onPad)
                    {
                        double before = current.AtPoint(c, r), design = Math.Max(elevation - off, Math.Min(elevation + off, before));
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
