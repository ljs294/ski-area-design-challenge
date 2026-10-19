using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// CL-03: a built lift clears a 55 ft (16.8 m) corridor centred on its line, from bullwheel to bullwheel, with
    /// straight edges: every cell whose centre lies within half the width of the segment.
    /// </summary>
    public static class LiftClearing
    {
        public const double CorridorMetres = 16.764;
        /// <summary>CL-09 (owner, task 07): clear ground round a terminal, 20 ft beyond its footprint (tuned at 07).</summary>
        public const double TerminalMarginMetres = 6.096;
        /// <summary>CL-09: and 50 ft more along the line in front of it, where chairs are loaded or unloaded (tuned at 07).</summary>
        public const double StationZoneMetres = 15.24;

        /// <summary>
        /// CL-09: clears round a terminal: a rectangle on the line through its bullwheel at (<paramref name="x"/>,
        /// <paramref name="y"/>), from <paramref name="behind"/> metres behind the bullwheel (away from the other terminal)
        /// to <paramref name="ahead"/> in front, <paramref name="halfWidth"/> either side, each grown by the margin, and the
        /// station zone added in front. (ux, uy) is the unit direction toward the other terminal.
        /// </summary>
        public static TerrainEdits Terminal(TerrainEdits edits, EditLattice lattice, double x, double y, double ux, double uy,
                                            double behind, double ahead, double halfWidth, out int cleared)
        {
            double b = behind + TerminalMarginMetres, a = ahead + TerminalMarginMetres + StationZoneMetres, w = halfWidth + TerminalMarginMetres;
            double reach = Math.Sqrt(Math.Max(a, b) * Math.Max(a, b) + w * w) + 1;
            int c0 = (int)Math.Floor(x - reach - lattice.West), c1 = (int)Math.Ceiling(x + reach - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - (y + reach)), r1 = (int)Math.Ceiling(lattice.North - (y - reach));
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            cleared = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c) + 0.5 - x, py = lattice.Y(r) - 0.5 - y;
                    double along = px * ux + py * uy, across = -px * uy + py * ux;
                    if (along < -b || along > a || Math.Abs(across) > w) continue;
                    var key = EditTileKey.Of(c, r);
                    if (!builders.TryGetValue(key, out var bd)) builders[key] = bd = edits.Get(key).Edit();
                    bd.SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                    cleared++;
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var bd in builders.Values) tiles.Add(bd.Build());
            return edits.With(tiles);
        }

        public static TerrainEdits Corridor(TerrainEdits edits, EditLattice lattice, double bx, double by, double tx, double ty, out int cleared)
        {
            double half = CorridorMetres / 2;
            double sx = tx - bx, sy = ty - by, len2 = sx * sx + sy * sy;
            if (len2 <= 0) throw new ArgumentException("A lift needs two distinct terminals.");
            int c0 = (int)Math.Floor(Math.Min(bx, tx) - half - 1 - lattice.West), c1 = (int)Math.Ceiling(Math.Max(bx, tx) + half + 1 - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - (Math.Max(by, ty) + half + 1)), r1 = (int)Math.Ceiling(lattice.North - (Math.Min(by, ty) - half - 1));
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            cleared = 0;
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c) + 0.5 - bx, py = lattice.Y(r) - 0.5 - by;
                    double t = (px * sx + py * sy) / len2;
                    if (t < 0 || t > 1) continue;
                    double qx = px - sx * t, qy = py - sy * t;
                    if (qx * qx + qy * qy > half * half) continue;
                    var key = EditTileKey.Of(c, r);
                    if (!builders.TryGetValue(key, out var b)) builders[key] = b = edits.Get(key).Edit();
                    b.SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
                    cleared++;
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var b in builders.Values) tiles.Add(b.Build());
            return edits.With(tiles);
        }
    }
}
