using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Water
{
    /// <summary>
    /// The outline of a set of grid cells (task 4.14, LK-10, F3): closed loops along the cells' edges, outer shores and
    /// islands alike, for a lake's selection outline or a reservoir's dashed shore. Corners are in cell units (corner (i, j)
    /// is cell (i, j)'s low corner); straight runs collapse to their ends. Deterministic: cells and edges in index order.
    /// </summary>
    public static class WaterOutline
    {
        /// <summary>
        /// The loops around the cells in [<paramref name="minI"/>, <paramref name="maxI"/>] × [<paramref name="minJ"/>,
        /// <paramref name="maxJ"/>] where <paramref name="inside"/> holds; each loop is corner x, y pairs, not repeating its first.
        /// </summary>
        public static List<int[]> Trace(int minI, int minJ, int maxI, int maxJ, Func<int, int, bool> inside)
        {
            int w = maxI - minI + 2;   // corners per row
            long Key(int x, int y) => (long)(y - minJ) * w + (x - minI);
            var next = new Dictionary<long, List<long>>();
            var order = new List<(long From, long To)>();
            void Edge(int ax, int ay, int bx, int by)
            {
                long a = Key(ax, ay), b = Key(bx, by);
                if (!next.TryGetValue(a, out var l)) next[a] = l = new List<long>(1);
                l.Add(b);
                order.Add((a, b));
            }
            bool In(int i, int j) => i >= minI && i <= maxI && j >= minJ && j <= maxJ && inside(i, j);
            for (int j = minJ; j <= maxJ; j++)
            for (int i = minI; i <= maxI; i++)
            {
                if (!inside(i, j)) continue;
                if (!In(i, j - 1)) Edge(i, j, i + 1, j);
                if (!In(i + 1, j)) Edge(i + 1, j, i + 1, j + 1);
                if (!In(i, j + 1)) Edge(i + 1, j + 1, i, j + 1);
                if (!In(i - 1, j)) Edge(i, j + 1, i, j);
            }

            var used = new HashSet<(long, long)>();
            var loops = new List<int[]>();
            foreach (var (from, to) in order)
            {
                if (used.Contains((from, to))) continue;
                var pts = new List<long> { from };
                long at = from, go = to;
                while (true)
                {
                    used.Add((at, go));
                    if (go == from) break;
                    pts.Add(go);
                    at = go;
                    go = -1;
                    foreach (long n in next[at]) if (!used.Contains((at, n))) { go = n; break; }
                    if (go < 0) break;   // never: every corner has as many edges out as in
                }
                loops.Add(Straighten(pts, w, minI, minJ));
            }
            return loops;
        }

        static int[] Straighten(List<long> pts, int w, int minI, int minJ)
        {
            int n = pts.Count;
            var xy = new List<int>(n * 2);
            for (int k = 0; k < n; k++)
            {
                long p = pts[(k + n - 1) % n], c = pts[k], q = pts[(k + 1) % n];
                int px = (int)(p % w), py = (int)(p / w), cx = (int)(c % w), cy = (int)(c / w), qx = (int)(q % w), qy = (int)(q / w);
                if ((cx - px) * (qy - cy) == (cy - py) * (qx - cx)) continue;   // a straight run: no corner here
                xy.Add(cx + minI);
                xy.Add(cy + minJ);
            }
            return xy.ToArray();
        }
    }
}
