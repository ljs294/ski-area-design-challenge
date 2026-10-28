using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>A grade's detail that holds several pieces' details (NT2's split chains): the one for a key, or null.</summary>
    public interface IGroupedDetail
    {
        object DetailFor(ulong key);
    }

    /// <summary>
    /// NT2 (owner, 2026-10-25): the parts of a split trail are graded as one, so Split changes no ground (NW-10). Two
    /// built trails are one chain's neighbours when the upper's tail is the lower's head, their settings match and their
    /// clearings touch without sharing a cell (Shape 8: a split's two sides). The chain is graded as one
    /// trail, <see cref="Whole"/>: the trail before the splits, so the ground is the same byte for byte. Where parts branch (two below one) or
    /// anything else breaks the chain, each trail grades on its own.
    /// </summary>
    public static class TrailChains
    {
        /// <summary>The chains among <paramref name="built"/> (each top first), ordered by their top's ID; a lone trail is a
        /// chain of one.</summary>
        public static List<List<Trail>> Of(IReadOnlyList<Trail> built)
        {
            var trails = new List<Trail>(built);
            trails.Sort((a, b) => a.Id.CompareTo(b.Id));
            int n = trails.Count;
            var next = new int[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) next[i] = prev[i] = -1;
            var nexts = new int[n];
            var prevs = new int[n];
            for (int i = 0; i < n; i++)
                for (int k = 0; k < n; k++)
                {
                    if (i == k || !Above(trails[i], trails[k])) continue;
                    nexts[i]++;
                    prevs[k]++;
                    next[i] = k;
                    prev[k] = i;
                }
            // A branch breaks the chain there: every link into or out of it is dropped.
            for (int i = 0; i < n; i++)
            {
                if (next[i] >= 0 && (nexts[i] > 1 || prevs[next[i]] > 1)) next[i] = -1;
                if (prev[i] >= 0 && (prevs[i] > 1 || nexts[prev[i]] > 1)) prev[i] = -1;
            }
            var chains = new List<List<Trail>>();
            var placed = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (placed[i] || (prev[i] >= 0 && next[prev[i]] == i)) continue;
                var chain = new List<Trail>();
                for (int k = i; k >= 0 && !placed[k]; k = next[k] >= 0 && prev[next[k]] == k ? next[k] : -1)
                {
                    placed[k] = true;
                    chain.Add(trails[k]);
                }
                chains.Add(chain);
            }
            // A loop (no top) is left as lone trails.
            for (int i = 0; i < n; i++) if (!placed[i]) chains.Add(new List<Trail> { trails[i] });
            chains.Sort((a, b) => a[0].Id.CompareTo(b[0].Id));
            return chains;
        }

        /// <summary>Whether <paramref name="upper"/>'s tail is <paramref name="lower"/>'s head across a split (Shape 8): the
        /// same settings, and clearings that share no cell but touch, as a split's two sides do.</summary>
        static bool Above(Trail upper, Trail lower)
        {
            if (!upper.Tail.HasValue || !lower.Head.HasValue || upper.Tail.Value != lower.Head.Value) return false;
            if (!upper.Settings.Equals(lower.Settings)) return false;
            var a = upper.Shape.Mask;
            var b = lower.Shape.Mask;
            return a.Touches(b) && a.Union(b).Cells == a.Cells + b.Cells;
        }

        /// <summary>
        /// The chain as one trail: the top part's ID, name, settings and head, the bottom part's tail, and every part's
        /// clearing together. A split's sides divide the clearing's cells exactly, so this is the trail as it was before
        /// the splits, however many times and in whatever order it was split.
        /// </summary>
        public static Trail Whole(IReadOnlyList<Trail> chain)
        {
            var shape = chain[0].Shape;
            for (int i = 1; i < chain.Count; i++) shape = shape.Union(chain[i].Shape);
            var top = chain[0];
            return new Trail(top.Id, top.Name, top.Stage, top.Settings, shape, top.Head, chain[chain.Count - 1].Tail);
        }

        /// <summary>The chain's grade, with each part's share of it: its own footprint, and the chain's centreline and
        /// stations between its head and tail. Volumes and cleared cells are shared out by stations.</summary>
        internal static ChainBuild Parts(TrailBuild whole, IReadOnlyList<Trail> members, EarthworkSite site)
        {
            var parts = new TrailBuild[members.Count];
            var line = whole.Centreline != null && whole.Centreline.Found ? whole.Centreline : null;
            double from = 0;
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                double to = line == null ? 0 : line.LengthMetres;
                if (line != null && i + 1 < members.Count)
                {
                    site.PointOf(m.Tail.Value, out double tx, out double ty);
                    to = Math.Max(from, AlongOf(line.Points, tx, ty));
                }
                var b = new TrailBuild
                {
                    Trail = m,
                    Footprint = TrailFootprint.Build(m.Shape, site.Lattice, site.OriginX, site.OriginY),
                    CentrelineIsOwn = false,
                    Writes = whole.Writes,
                };
                if (line != null)
                {
                    b.Centreline = Slice(line, from, to);
                    if (whole.Grade != null) b.Grade = Slice(whole.Grade, from, to);
                }
                parts[i] = b;
                from = to;
            }
            return new ChainBuild(whole, members, parts);
        }

        internal sealed class ChainBuild : IGroupedDetail
        {
            public TrailBuild Whole { get; }
            readonly IReadOnlyList<Trail> _members;
            readonly TrailBuild[] _parts;

            public ChainBuild(TrailBuild whole, IReadOnlyList<Trail> members, TrailBuild[] parts)
            {
                Whole = whole;
                _members = members;
                _parts = parts;
            }

            public object DetailFor(ulong key)
            {
                for (int i = 0; i < _members.Count; i++) if (_members[i].Id.Value == key) return _parts[i];
                return null;
            }
        }

        static double AlongOf(double[] pts, double px, double py)
        {
            double best = double.MaxValue, bestAlong = 0, run = 0;
            for (int i = 0; i + 3 < pts.Length; i += 2)
            {
                double ax = pts[i], ay = pts[i + 1], dx = pts[i + 2] - ax, dy = pts[i + 3] - ay, len2 = dx * dx + dy * dy;
                double f = len2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0;
                f = f < 0 ? 0 : f > 1 ? 1 : f;
                double qx = ax + f * dx, qy = ay + f * dy, d = (qx - px) * (qx - px) + (qy - py) * (qy - py);
                double len = Math.Sqrt(len2);
                if (d < best) { best = d; bestAlong = run + f * len; }
                run += len;
            }
            return bestAlong;
        }

        static TrailCentreline.Result Slice(TrailCentreline.Result line, double from, double to)
        {
            var pts = line.Points;
            var outPts = new List<double>();
            double run = 0;
            void Add(double x, double y)
            {
                int c = outPts.Count;
                if (c >= 2 && outPts[c - 2] == x && outPts[c - 1] == y) return;
                outPts.Add(x);
                outPts.Add(y);
            }
            for (int i = 0; i + 3 < pts.Length; i += 2)
            {
                double ax = pts[i], ay = pts[i + 1], bx = pts[i + 2], by = pts[i + 3];
                double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                double s0 = run, s1 = run + len;
                run = s1;
                if (s1 < from || s0 > to || len <= 0) continue;
                double f0 = Math.Max(0, (from - s0) / len), f1 = Math.Min(1, (to - s0) / len);
                Add(ax + (bx - ax) * f0, ay + (by - ay) * f0);
                Add(ax + (bx - ax) * f1, ay + (by - ay) * f1);
            }
            if (outPts.Count < 4) outPts.Clear();
            return new TrailCentreline.Result { Points = outPts.ToArray(), LengthMetres = Math.Max(0, to - from) };
        }

        static TrailGrader.Graded Slice(TrailGrader.Graded g, double from, double to)
        {
            int first = -1, last = -1;
            for (int k = 0; k < g.Along.Length; k++)
            {
                if (g.Along[k] < from - 1e-6 || g.Along[k] > to + 1e-6) continue;
                if (first < 0) first = k;
                last = k;
            }
            if (first < 0)
            {
                // A part shorter than a station: the nearest station stands for it.
                first = last = NearestStation(g.Along, (from + to) / 2);
            }
            int n = last - first + 1, all = Math.Max(1, g.Along.Length);
            double share = (double)n / all;
            var along = new double[n];
            for (int k = 0; k < n; k++) along[k] = g.Along[first + k] - from;
            int ungraded = 0;
            if (g.IsGraded) for (int k = first; k <= last; k++) if (!g.Benched[k]) ungraded++;
            return new TrailGrader.Graded
            {
                Writes = g.Writes,
                Stations = Range(g.Stations, 2 * first, 2 * n),
                Along = along,
                Ground = Range(g.Ground, first, n),
                Benched = Range(g.Benched, first, n),
                BenchLeft = Range(g.BenchLeft, first, n),
                BenchRight = Range(g.BenchRight, first, n),
                CrossSlope = Range(g.CrossSlope, first, n),
                UngradedStations = g.IsGraded ? ungraded : n,
                UngradedMetres = g.Stations.Length > 0 ? g.UngradedMetres * (g.IsGraded ? (double)ungraded / Math.Max(1, g.UngradedStations) : share) : 0,
                LengthMetres = Math.Max(0, to - from),
                CutCubicMetres = g.CutCubicMetres * share,
                FillCubicMetres = g.FillCubicMetres * share,
                ClearedCells = (int)Math.Round(g.ClearedCells * share),
                IsGraded = g.IsGraded,
            };
        }

        static int NearestStation(double[] along, double at)
        {
            int best = 0;
            for (int k = 1; k < along.Length; k++) if (Math.Abs(along[k] - at) < Math.Abs(along[best] - at)) best = k;
            return best;
        }

        static T[] Range<T>(T[] a, int start, int count)
        {
            if (a == null) return null;
            var r = new T[count];
            Array.Copy(a, start, r, 0, Math.Min(count, Math.Max(0, a.Length - start)));
            return r;
        }
    }
}
