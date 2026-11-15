using System;
using System.Globalization;
using MountainPlanner.Domain.Measure;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// Task 4.09 PR B: the screen's side of the lakes, engine-free and over plain numbers (Domain never sees the simulation's
    /// snapshot). The simulation's units become a <see cref="WaterSurface"/>; the body table's mask becomes the map the
    /// renderer and the readout share (dilated one cell, so the splat's soft shore never falls outside a body); and a body
    /// becomes its readout line.
    /// </summary>
    public static class LakeSurfaces
    {
        const double MetresPerInch = 0.0254;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        /// <summary>A body's surface from the simulation's units: ice in 0.01 in, the snow on it in 0.1 in.</summary>
        public static WaterSurface Surface(WaterSurfaceState state, int iceHundredthsIn, int snowTenthsIn) =>
            new WaterSurface(state, (float)(Math.Max(0, iceHundredthsIn) * MetresPerInch / 100), (float)(Math.Max(0, snowTenthsIn) * MetresPerInch / 10));

        /// <summary>
        /// The mask (body index + 1 per cell, 0 none) grown by one cell: a cell with no body next to one (8 neighbours) takes
        /// a neighbour's body. A lake or pond beats the streams (body 0); between lakes the larger wins, then the lower index.
        /// Body cells keep their own body. A new array; the table is never touched.
        /// </summary>
        public static byte[] Dilated(WaterBodyTable t)
        {
            if (t == null) throw new ArgumentNullException(nameof(t));
            int w = t.Width, d = t.Depth;
            var mask = t.Mask;
            var o = (byte[])mask.Clone();
            for (int y = 0; y < d; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (mask[i] != 0) continue;
                    int best = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int yy = y + dy;
                        if (yy < 0 || yy >= d) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if ((dx == 0 && dy == 0) || xx < 0 || xx >= w) continue;
                            int m = mask[yy * w + xx];
                            if (m != 0 && (best == 0 || Better(t, m - 1, best - 1))) best = m;
                        }
                    }
                    o[i] = (byte)best;
                }
            return o;
        }

        static bool Better(WaterBodyTable t, int a, int b)
        {
            if (a == b) return false;
            if ((a == WaterBodyTable.Streams) != (b == WaterBodyTable.Streams)) return b == WaterBodyTable.Streams;
            int ca = t[a].Cells, cb = t[b].Cells;
            return ca != cb ? ca > cb : a < b;
        }

        /// <summary>
        /// The readout over a body: "Lake · 14 in ice · 6 in snow · froze Dec 3", "Lake · open water · opened Apr 12",
        /// "Pond · open water · not frozen yet". Dates are the label year's (month 1–12 and day; month 0 = not yet).
        /// </summary>
        public static string Readout(WaterBodyKind kind, WaterSurfaceState state, int iceHundredthsIn, int snowTenthsIn,
                                     int onMonth, int onDay, int outMonth, int outDay, UnitSystem units)
        {
            string name = kind == WaterBodyKind.Streams ? "Stream" : kind == WaterBodyKind.Pond ? "Pond" : "Lake";
            if (state == WaterSurfaceState.OpenWater)
                return name + " · open water · " + (outMonth > 0 ? "opened " + Date(outMonth, outDay) : onMonth > 0 ? "thawing" : "not frozen yet");
            string text = name + " · " + Ice(iceHundredthsIn, units) + " ice";
            if (state == WaterSurfaceState.SnowCoveredIce && snowTenthsIn > 0)
                text += " · " + UnitFormat.SnowDepth(snowTenthsIn * MetresPerInch / 10, units) + " snow";
            if (onMonth > 0) text += " · froze " + Date(onMonth, onDay);
            return text;
        }

        /// <summary>Ice thickness: whole inches, or whole centimetres.</summary>
        public static string Ice(int iceHundredthsIn, UnitSystem units) => units == UnitSystem.Imperial
            ? Math.Max(1, (int)Math.Round(iceHundredthsIn / 100.0)).ToString(Invariant) + " in"
            : Math.Max(1, (int)Math.Round(iceHundredthsIn * 2.54 / 100)).ToString(Invariant) + " cm";

        static string Date(int month, int day) => Months[(month - 1 + 12) % 12] + " " + day.ToString(Invariant);
    }
}
