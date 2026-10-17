using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// A painted footprint on a grid (P3-06's stand-in for task 08's footprint geometry): the strokes stamped in order,
    /// paint setting cells and erase clearing them (TR-02), then the area and how many separate patches it makes
    /// (TF-02: a second patch blocks Next). Built off the main thread; immutable once built.
    /// </summary>
    public sealed class FootprintRaster
    {
        public readonly double OriginX, OriginY, Cell;
        public readonly int Width, Height;
        readonly bool[] _on;
        public readonly int Cells, Patches;
        public double AreaSquareMetres => Cells * Cell * Cell;

        FootprintRaster(double ox, double oy, double cell, int w, int h, bool[] on, int cells, int patches)
        {
            OriginX = ox; OriginY = oy; Cell = cell; Width = w; Height = h; _on = on; Cells = cells; Patches = patches;
        }

        /// <summary>Whether a point (design mm) lies on the painted area.</summary>
        public bool Contains(PointMm p)
        {
            int i = (int)Math.Floor((p.XMetres - OriginX) / Cell), j = (int)Math.Floor((p.YMetres - OriginY) / Cell);
            return i >= 0 && j >= 0 && i < Width && j < Height && _on[j * Width + i];
        }

        /// <summary>Stamps <paramref name="strokes"/> in order on a grid of at most about 2 million cells, 5 ft or finer.</summary>
        public static FootprintRaster Build(IReadOnlyList<Stroke> strokes)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var s in strokes)
            {
                double r = s.WidthMm / 2000.0;
                foreach (var p in s.Points)
                {
                    minX = Math.Min(minX, p.XMetres - r); maxX = Math.Max(maxX, p.XMetres + r);
                    minY = Math.Min(minY, p.YMetres - r); maxY = Math.Max(maxY, p.YMetres + r);
                }
            }
            if (strokes.Count == 0 || minX > maxX) return new FootprintRaster(0, 0, 1, 0, 0, Array.Empty<bool>(), 0, 0);
            double cell = Math.Max(DesignUnits.FiveFeetMm / 1000.0, Math.Sqrt((maxX - minX) * (maxY - minY) / 2_000_000.0));
            int w = (int)Math.Ceiling((maxX - minX) / cell) + 1, h = (int)Math.Ceiling((maxY - minY) / cell) + 1;
            var on = new bool[w * h];
            foreach (var s in strokes)
            {
                double r = s.WidthMm / 2000.0;
                var pts = s.Points;
                // A one-point stroke is a dab; otherwise each segment is a capsule of the brush's width.
                for (int k = 0; k < Math.Max(1, pts.Count - 1); k++)
                {
                    var a = pts[k];
                    var b = pts[Math.Min(k + 1, pts.Count - 1)];
                    Capsule(on, w, h, minX, minY, cell, a.XMetres, a.YMetres, b.XMetres, b.YMetres, r, !s.Erase);
                }
            }
            int cells = 0;
            foreach (bool b in on) if (b) cells++;
            return new FootprintRaster(minX, minY, cell, w, h, on, cells, CountPatches(on, w, h));
        }

        static void Capsule(bool[] on, int w, int h, double ox, double oy, double cell, double ax, double ay, double bx, double by, double r, bool paint)
        {
            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(ax, bx) - r - ox) / cell)), i1 = Math.Min(w - 1, (int)Math.Ceiling((Math.Max(ax, bx) + r - ox) / cell));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(ay, by) - r - oy) / cell)), j1 = Math.Min(h - 1, (int)Math.Ceiling((Math.Max(ay, by) + r - oy) / cell));
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy, r2 = r * r;
            for (int j = j0; j <= j1; j++)
            {
                double y = oy + (j + 0.5) * cell;
                for (int i = i0; i <= i1; i++)
                {
                    double x = ox + (i + 0.5) * cell;
                    double t = len2 == 0 ? 0 : Math.Max(0, Math.Min(1, ((x - ax) * dx + (y - ay) * dy) / len2));
                    double ex = ax + t * dx - x, ey = ay + t * dy - y;
                    if (ex * ex + ey * ey <= r2) on[j * w + i] = paint;
                }
            }
        }

        static int CountPatches(bool[] on, int w, int h)
        {
            var seen = new bool[on.Length];
            var queue = new int[on.Length];
            int patches = 0;
            for (int start = 0; start < on.Length; start++)
            {
                if (!on[start] || seen[start]) continue;
                patches++;
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int c = queue[head++], x = c % w, y = c / w;
                    if (x > 0) Visit(c - 1);
                    if (x < w - 1) Visit(c + 1);
                    if (y > 0) Visit(c - w);
                    if (y < h - 1) Visit(c + w);
                }
                void Visit(int n)
                {
                    if (!on[n] || seen[n]) return;
                    seen[n] = true;
                    queue[tail++] = n;
                }
            }
            return patches;
        }
    }
}
