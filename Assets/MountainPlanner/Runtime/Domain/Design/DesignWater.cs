using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Water;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 4.14: the design's ponds and reservoirs as water bodies. The area's table (lakes and streams from the map) gains
    /// one body per pond and dam, in ID order (ponds, then dams), from the fold's wet points: an 8 m cell is water when at
    /// least half its 64 lattice points are. Rebuilt whenever the design's water changes; the simulation is sent the new
    /// table (its hash says when).
    /// </summary>
    public static class DesignWater
    {
        /// <summary>The table's cells a wet mask fills: half or more of each cell's points (8 × 8 at 1 m).</summary>
        public static int[] CellsOf(WaterMask wet, EarthworkSite site, WaterBodyTable table)
        {
            if (wet == null || wet.Count == 0) return Array.Empty<int>();
            const int side = WaterBodyTable.CellMetres;
            var counts = new Dictionary<int, int>();
            var lat = site.Lattice;
            foreach (var (c, r) in wet.Points())
            {
                long xMm = (long)Math.Round((lat.X(c) - site.OriginX) * 1000), yMm = (long)Math.Round((lat.Y(r) - site.OriginY) * 1000);
                long i = FloorDiv(xMm - table.OriginXMm, side * 1000L), j = FloorDiv(yMm - table.OriginYMm, side * 1000L);
                if (i < 0 || j < 0 || i >= table.Width || j >= table.Depth) continue;
                int k = (int)(j * table.Width + i);
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }
            var cells = new List<int>();
            foreach (var kv in counts) if (kv.Value * 2 >= side * side) cells.Add(kv.Key);
            cells.Sort();
            return cells.ToArray();
        }

        /// <summary>
        /// The area's lakes table with the design's ponds and reservoirs added. <paramref name="buildOf"/> gives the fold's
        /// build for a pond's or dam's ID (<see cref="DesignEarthwork.DetailOf"/>); one with no build or no whole cell is left out.
        /// </summary>
        public static WaterBodyTable With(WaterBodyTable lakes, DesignDocument doc, Func<ulong, object> buildOf, EarthworkSite site)
        {
            var t = lakes;
            foreach (var p in doc.Ponds) t = Add(t, p.Id.Value, buildOf(p.Id.Value) as WaterBuild, site, WaterBodyKind.Pond);
            foreach (var d in doc.Dams) t = Add(t, d.Id.Value, buildOf(d.Id.Value) as WaterBuild, site, WaterBodyKind.Reservoir);
            return t;
        }

        static WaterBodyTable Add(WaterBodyTable t, ulong id, WaterBuild b, EarthworkSite site, WaterBodyKind kind)
        {
            if (b == null || t.Ponds >= t.MaxPonds || t.IndexOf(id) >= 0) return t;
            var cells = CellsOf(b.Wet, site, t);
            if (cells.Length == 0) return t;
            int depthTenths = (int)Math.Round(b.MeanDepthMetres / 0.3048 * 10);
            int surfaceFeet = (int)Math.Round(b.FullPoolMetres / 0.3048);
            return t.WithPond(id, cells, Math.Max(1, depthTenths), surfaceFeet, kind);
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
