using System;
using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// How a trail made of sections is drawn on the map (task P3-10, sections findings 5 and 6): its corners rounded where it
    /// turns from one run onto another, so two trails through one junction read as two curves rather than a cross, and its
    /// line stopped short of its top and bottom. Drawing only: the trail, its ends and the ground stay as built.
    /// </summary>
    public static class TrailLineShape
    {
        /// <summary>Finding 6: a trail's line stops 50 ft short of its top and of its bottom.</summary>
        public const float EndGapMetres = 15.24f;

        /// <summary>Finding 5: how far either side of a turn onto another run the line is rounded.</summary>
        public const float JoinReachMetres = 30f;

        /// <summary>
        /// The line rounded round each join (a point index where the trail changes run): the stretch within
        /// <paramref name="reach"/> metres either side is cut by Chaikin's corner cutting, its own ends kept.
        /// </summary>
        public static Vector2[] RoundJoins(Vector2[] line, IReadOnlyList<int> joins, float reach = JoinReachMetres, int passes = 3)
        {
            if (line == null || line.Length < 3 || joins == null || joins.Count == 0) return line;
            var points = new List<Vector2>(line);
            // Last first, so the earlier joins' indices still hold.
            var order = new List<int>(joins);
            order.Sort();
            for (int j = order.Count - 1; j >= 0; j--)
            {
                int at = order[j];
                if (at <= 0 || at >= points.Count - 1) continue;
                int a = at, b = at;
                float back = 0, ahead = 0;
                while (a > 0 && back < reach) { back += Vector2.Distance(points[a - 1], points[a]); a--; }
                while (b < points.Count - 1 && ahead < reach) { ahead += Vector2.Distance(points[b], points[b + 1]); b++; }
                var stretch = points.GetRange(a, b - a + 1);
                // Short sides get points of their own, so the cut stays within reach of the corner.
                stretch = Densify(stretch, reach / 4);
                for (int p = 0; p < passes; p++) stretch = Chaikin(stretch);
                points.RemoveRange(a, b - a + 1);
                points.InsertRange(a, stretch);
            }
            return points.ToArray();
        }

        /// <summary>
        /// The line with <paramref name="gap"/> metres taken off each end; a line shorter than three gaps keeps its middle
        /// third. Null for a line of under two points.
        /// </summary>
        public static Vector2[] Trim(Vector2[] line, float gap = EndGapMetres)
        {
            if (line == null || line.Length < 2) return line;
            float total = 0;
            for (int k = 1; k < line.Length; k++) total += Vector2.Distance(line[k - 1], line[k]);
            if (total <= 0) return line;
            gap = Math.Min(gap, total / 3);
            float from = gap, to = total - gap, run = 0;
            var into = new List<Vector2>(line.Length);
            for (int k = 1; k < line.Length; k++)
            {
                float len = Vector2.Distance(line[k - 1], line[k]);
                float s0 = run, s1 = run + len;
                run = s1;
                if (len <= 0 || s1 < from || s0 > to) continue;
                if (into.Count == 0) into.Add(Vector2.Lerp(line[k - 1], line[k], Math.Max(0, (from - s0) / len)));
                into.Add(Vector2.Lerp(line[k - 1], line[k], Math.Min(1, (to - s0) / len)));
            }
            return into.Count >= 2 ? into.ToArray() : line;
        }

        // One pass of Chaikin's corner cutting: each segment's quarter and three-quarter points, the ends kept.
        static List<Vector2> Chaikin(List<Vector2> p)
        {
            if (p.Count < 3) return p;
            var q = new List<Vector2>(p.Count * 2) { p[0] };
            for (int k = 0; k < p.Count - 1; k++)
            {
                q.Add(Vector2.Lerp(p[k], p[k + 1], 0.25f));
                q.Add(Vector2.Lerp(p[k], p[k + 1], 0.75f));
            }
            q.Add(p[p.Count - 1]);
            return q;
        }

        static List<Vector2> Densify(List<Vector2> p, float step)
        {
            var q = new List<Vector2>(p.Count * 2) { p[0] };
            for (int k = 1; k < p.Count; k++)
            {
                float len = Vector2.Distance(p[k - 1], p[k]);
                int n = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int i = 1; i <= n; i++) q.Add(Vector2.Lerp(p[k - 1], p[k], (float)i / n));
            }
            return q;
        }
    }
}
