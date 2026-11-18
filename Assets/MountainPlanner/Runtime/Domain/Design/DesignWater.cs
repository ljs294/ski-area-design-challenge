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

        /// <summary>
        /// WS-01 (task 4.14 PR B): the sources the simulation keeps water for, from the same table: every pond and dam with a
        /// body in it, then the lakes the player switched on, at most <paramref name="maxSources"/> (ponds and dams first,
        /// lakes after in ID order). <paramref name="streamWidthFeet"/> gives a dam's stream width (WS-03: the player's,
        /// OSM's or the class default; way 0 is the streams body); <paramref name="catchments"/> (null: none) says where each
        /// cell's runoff goes, counted for a natural pond or a lake (a dam's stream already carries its valley's water).
        /// </summary>
        public static WaterSources Sources(WaterBodyTable table, DesignDocument doc, Func<ulong, object> buildOf, Func<ulong, double> streamWidthFeet,
                                           Catchments catchments, int maxSources)
        {
            var list = new List<WaterSource>();
            foreach (var p in doc.Ponds)
            {
                int body = table.IndexOf(p.Id.Value);
                if (body < 0 || !(buildOf(p.Id.Value) is WaterBuild b) || b.Curve == null || list.Count >= maxSources) continue;
                list.Add(new WaterSource(p.Id.Value, WaterBodyKind.Pond, body, p.FullPoolMm, b.Curve, 0, p.SpringGpm, p.Fill == PondFill.Natural, p.StartsEmpty));
            }
            foreach (var d in doc.Dams)
            {
                int body = table.IndexOf(d.Id.Value);
                if (body < 0 || !(buildOf(d.Id.Value) is WaterBuild b) || b.Curve == null || list.Count >= maxSources) continue;
                double width = streamWidthFeet != null ? streamWidthFeet(d.StreamWay) : StreamFlow.DefaultWidthMetres("stream") / 0.3048;
                int milliCfs = (int)Math.Round(StreamFlow.BaseCfs(width) * 1000);
                list.Add(new WaterSource(d.Id.Value, WaterBodyKind.Reservoir, body, d.FullPoolMm, b.Curve, milliCfs, 0, true, d.StartsEmpty));
            }
            var lakes = new List<LakeRecord>(doc.Lakes);
            lakes.Sort((x, y) => x.Lake.CompareTo(y.Lake));
            foreach (var rec in lakes)
            {
                if (!rec.Source || list.Count >= maxSources) continue;
                int body = table.IndexOf(rec.Lake);
                if (body < 0 || table[body].Kind != WaterBodyKind.Lake) continue;
                var w = table[body];
                int tenths = rec.DepthTenthsFt > 0 ? rec.DepthTenthsFt : w.DepthTenthsFt;
                var curve = StageCurve.Prism(w.AreaSquareMetres, (long)tenths * StageCurve.StepMicrons);
                list.Add(new WaterSource(rec.Lake, WaterBodyKind.Lake, body, (int)Math.Round(w.SurfaceFeet * 304.8), curve, 0, 0, true, false));
            }
            list.Sort((x, y) => x.Id.CompareTo(y.Id));
            byte[] drains = null;
            if (catchments != null && catchments.Width == table.Width && catchments.Depth == table.Depth && list.Count > 0)
            {
                var ofBody = new byte[table.Count];
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Natural && list[i].Kind != WaterBodyKind.Reservoir) ofBody[list[i].Body] = (byte)(i + 1);
                drains = catchments.DrainsTo(table.Mask, ofBody);
            }
            return new WaterSources(list.ToArray(), table.Width, table.Depth, drains);
        }

        static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);
    }
}
