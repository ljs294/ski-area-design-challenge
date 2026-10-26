using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One lift terminal in a station: the lift, which end, and its bullwheel point.</summary>
    public readonly struct StationMember
    {
        public readonly LiftId Lift;
        public readonly bool Bottom;
        public readonly PointMm At;
        public StationMember(LiftId lift, bool bottom, PointMm at) { Lift = lift; Bottom = bottom; At = at; }
    }

    /// <summary>
    /// A station (NW-14, owner NT13; v2 NT17/NT18): the area round one or more lift terminals that the network treats as
    /// one node, the rounded hull of its terminals (their convex hull grown by <see cref="DesignStations.ReachMm"/>). A
    /// lone terminal is a station of its own; stations whose areas touch share one, so a hub grows as more joins it.
    /// </summary>
    public sealed class Station
    {
        /// <summary>JN-04: a keyed hash of its members' end keys, in order; a reload, or an unrelated lift, keeps it.</summary>
        public ulong Id { get; }
        /// <summary>The mean of its hull's corners, in whole mm.</summary>
        public PointMm Centre { get; }
        /// <summary>By lift ID, the bottom before the top.</summary>
        public IReadOnlyList<StationMember> Members { get; }
        /// <summary>The terminals' convex hull, counter-clockwise; one or two points when that's all there is.</summary>
        public IReadOnlyList<PointMm> Hull { get; }
        /// <summary>The lifts with a terminal here, in ID order.</summary>
        public IReadOnlyList<LiftId> Lifts { get; }

        internal Station(ulong id, PointMm centre, StationMember[] members, PointMm[] hull, LiftId[] lifts)
        {
            Id = id;
            Centre = centre;
            Members = members;
            Hull = hull;
            Lifts = lifts;
        }

        public bool HasBottom
        {
            get
            {
                foreach (var m in Members) if (m.Bottom) return true;
                return false;
            }
        }

        /// <summary>NW-15 (owner NT19): a station holding terminals of two or more lifts is an Area.</summary>
        public bool IsArea => Lifts.Count >= 2;

        /// <summary>The distance squared (mm²) from <paramref name="p"/> to the hull: 0 inside it.</summary>
        public double DistanceSquared(PointMm p) => DesignStations.HullDistanceSquared(Hull, p);
    }

    /// <summary>
    /// NW-14's stations (owner NT13, 2026-10-26; v2 NT17/NT18): every built lift's terminals, each station reaching
    /// <see cref="ReachMm"/> (100 ft) beyond the hull of its terminals. Stations whose areas touch (hulls within twice the
    /// reach) merge, again until none touch; a merge that would hold both ends of one lift is skipped. Deterministic:
    /// lifts in ID order, the bottom before the top, merges scanned in that order; stations in ID order.
    /// </summary>
    public static class DesignStations
    {
        /// <summary>NW-14: a station reaches 100 ft (30.48 m) from its terminals' hull.</summary>
        public static readonly int ReachMm = DesignUnits.FeetToMm(100);

        /// <param name="leaveOut">A lift to leave out (Edit lift's own, while its catchment previews, NT16).</param>
        /// <param name="extra">Terminals being placed (NT20: the hub drawn as it would be), each its own end to start.</param>
        public static List<Station> Of(DesignDocument doc, LiftId? leaveOut = null, IReadOnlyList<StationMember> extra = null)
        {
            var lifts = new List<Lift>();
            foreach (var l in doc.Lifts) if (l.Stage == WorkStage.Built && l.Id != leaveOut) lifts.Add(l);
            lifts.Sort((a, b) => a.Id.CompareTo(b.Id));
            var groups = new List<List<StationMember>>(lifts.Count * 2 + (extra?.Count ?? 0));
            foreach (var l in lifts)
            {
                groups.Add(new List<StationMember> { new StationMember(l.Id, true, l.Bottom) });
                groups.Add(new List<StationMember> { new StationMember(l.Id, false, l.Top) });
            }
            if (extra != null) foreach (var e in extra) groups.Add(new List<StationMember> { e });

            // Merge stations whose areas touch until none do, scanning in order; the hull grows with each merge.
            var hulls = new List<PointMm[]>(groups.Count);
            foreach (var g in groups) hulls.Add(HullOf(g));
            double join = 2.0 * ReachMm, join2 = join * join;
            for (bool merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < groups.Count && !merged; i++)
                    for (int k = i + 1; k < groups.Count && !merged; k++)
                    {
                        if (SharesLift(groups[i], groups[k]) || !BoxesNear(hulls[i], hulls[k], (long)join)) continue;
                        if (HullDistanceSquared(hulls[i], hulls[k]) > join2) continue;
                        groups[i].AddRange(groups[k]);
                        groups.RemoveAt(k);
                        hulls.RemoveAt(k);
                        hulls[i] = HullOf(groups[i]);
                        merged = true;
                    }
            }

            var stations = new List<Station>(groups.Count);
            for (int g = 0; g < groups.Count; g++)
            {
                var members = groups[g];
                members.Sort((a, b) => a.Lift != b.Lift ? a.Lift.CompareTo(b.Lift) : a.Bottom == b.Bottom ? 0 : a.Bottom ? -1 : 1);
                ulong id = 0;
                var liftIds = new List<LiftId>();
                foreach (var m in members)
                {
                    ulong key = m.Bottom ? DesignNetwork.LiftBottomNode(doc.Seed, m.Lift) : DesignNetwork.LiftTopNode(doc.Seed, m.Lift);
                    id = id == 0 ? DesignKey.Hash(doc.Seed, DesignKey.StationTag, key) : DesignKey.Hash(doc.Seed, DesignKey.StationTag, id, key);
                    if (liftIds.Count == 0 || liftIds[liftIds.Count - 1] != m.Lift) liftIds.Add(m.Lift);
                }
                var hull = hulls[g];
                long sx = 0, sy = 0;
                foreach (var p in hull) { sx += p.X; sy += p.Y; }
                var centre = new PointMm((int)Math.Round((double)sx / hull.Length), (int)Math.Round((double)sy / hull.Length));
                stations.Add(new Station(id, centre, members.ToArray(), hull, liftIds.ToArray()));
            }
            stations.Sort((a, b) => a.Id.CompareTo(b.Id));
            return stations;
        }

        static bool SharesLift(List<StationMember> a, List<StationMember> b)
        {
            foreach (var m in a)
                foreach (var o in b)
                    if (m.Lift == o.Lift) return true;   // both ends of one lift never share a station
            return false;
        }

        static bool BoxesNear(PointMm[] a, PointMm[] b, long gap)
        {
            Box(a, out long ax0, out long ay0, out long ax1, out long ay1);
            Box(b, out long bx0, out long by0, out long bx1, out long by1);
            return ax0 - gap <= bx1 && bx0 - gap <= ax1 && ay0 - gap <= by1 && by0 - gap <= ay1;
        }

        static void Box(PointMm[] h, out long x0, out long y0, out long x1, out long y1)
        {
            x0 = y0 = long.MaxValue;
            x1 = y1 = long.MinValue;
            foreach (var p in h)
            {
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
        }

        /// <summary>The station whose area holds <paramref name="p"/> (the nearest hull), ties to the lower ID; null if none.</summary>
        public static Station At(IReadOnlyList<Station> stations, PointMm p)
        {
            Station best = null;
            double bestD = (double)ReachMm * ReachMm;
            foreach (var s in stations)   // in ID order, so a tie keeps the lower ID
            {
                double d = s.DistanceSquared(p);
                if (d > bestD || (d == bestD && best != null)) continue;
                best = s;
                bestD = d;
            }
            return best;
        }

        /// <summary>The station holding a lift's end; null when the lift isn't built.</summary>
        public static Station Of(IReadOnlyList<Station> stations, LiftId lift, bool bottom)
        {
            foreach (var s in stations)
                foreach (var m in s.Members)
                    if (m.Lift == lift && m.Bottom == bottom) return s;
            return null;
        }

        // ---- the hull (NT17) ----

        static PointMm[] HullOf(List<StationMember> members)
        {
            var pts = new List<PointMm>(members.Count);
            foreach (var m in members) pts.Add(m.At);
            return HullOf(pts);
        }

        /// <summary>The convex hull (monotone chain), counter-clockwise; repeated points once; one or two points as they are.</summary>
        public static PointMm[] HullOf(IReadOnlyList<PointMm> points)
        {
            var p = new List<PointMm>(points);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            for (int i = p.Count - 1; i > 0; i--) if (p[i] == p[i - 1]) p.RemoveAt(i);
            if (p.Count < 3) return p.ToArray();
            var lower = new List<PointMm>();
            foreach (var q in p)
            {
                while (lower.Count > 1 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], q) <= 0) lower.RemoveAt(lower.Count - 1);
                lower.Add(q);
            }
            var upper = new List<PointMm>();
            for (int i = p.Count - 1; i >= 0; i--)
            {
                var q = p[i];
                while (upper.Count > 1 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], q) <= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(q);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower.ToArray();
        }

        static long Cross(PointMm o, PointMm a, PointMm b) => (long)(a.X - o.X) * (b.Y - o.Y) - (long)(a.Y - o.Y) * (b.X - o.X);

        /// <summary>Whether <paramref name="p"/> lies inside (or on) a hull of three or more corners.</summary>
        public static bool InsideHull(IReadOnlyList<PointMm> hull, PointMm p)
        {
            if (hull.Count < 3) return false;
            for (int i = 0; i < hull.Count; i++)
                if (Cross(hull[i], hull[(i + 1) % hull.Count], p) < 0) return false;
            return true;
        }

        /// <summary>The distance squared (mm²) from <paramref name="p"/> to a hull: 0 inside it.</summary>
        public static double HullDistanceSquared(IReadOnlyList<PointMm> hull, PointMm p)
        {
            if (InsideHull(hull, p)) return 0;
            double best = double.MaxValue;
            int n = hull.Count;
            for (int i = 0; i < n; i++) best = Math.Min(best, PointSegmentSquared(p, hull[i], hull[(i + 1) % n]));
            return best;
        }

        /// <summary>The distance squared (mm²) between two hulls: 0 when they touch or overlap.</summary>
        public static double HullDistanceSquared(IReadOnlyList<PointMm> a, IReadOnlyList<PointMm> b)
        {
            if (InsideHull(a, b[0]) || InsideHull(b, a[0])) return 0;
            double best = double.MaxValue;
            int na = a.Count, nb = b.Count;
            for (int i = 0; i < na; i++)
                for (int k = 0; k < nb; k++)
                    best = Math.Min(best, SegmentSegmentSquared(a[i], a[(i + 1) % na], b[k], b[(k + 1) % nb]));
            return best;
        }

        static double PointSegmentSquared(PointMm p, PointMm a, PointMm b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2)) : 0;
            double ex = a.X + t * dx - p.X, ey = a.Y + t * dy - p.Y;
            return ex * ex + ey * ey;
        }

        static double SegmentSegmentSquared(PointMm a0, PointMm a1, PointMm b0, PointMm b1)
        {
            long d1 = Cross(b0, b1, a0), d2 = Cross(b0, b1, a1), d3 = Cross(a0, a1, b0), d4 = Cross(a0, a1, b1);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return 0;   // they cross
            return Math.Min(Math.Min(PointSegmentSquared(a0, b0, b1), PointSegmentSquared(a1, b0, b1)),
                            Math.Min(PointSegmentSquared(b0, a0, a1), PointSegmentSquared(b1, a0, a1)));
        }

        /// <summary>
        /// The station's area outline for drawing (NT17): each hull edge pushed out by the reach, an arc round each corner
        /// (<paramref name="stepsPerQuarter"/> steps a quarter turn), counter-clockwise; x, y pairs in metres, closed (the
        /// last point repeats the first).
        /// </summary>
        public static List<double> Outline(Station station, int stepsPerQuarter = 8)
        {
            var hull = station.Hull;
            double r = ReachMm / 1000.0, step = Math.PI / 2 / Math.Max(1, stepsPerQuarter);
            var outline = new List<double>();
            int n = hull.Count;
            if (n == 1)
            {
                int steps = 4 * Math.Max(1, stepsPerQuarter);
                for (int k = 0; k <= steps; k++)
                {
                    double a = k * Math.PI * 2 / steps;
                    outline.Add(hull[0].XMetres + r * Math.Cos(a));
                    outline.Add(hull[0].YMetres + r * Math.Sin(a));
                }
                return outline;
            }
            // Each edge's outward normal (to the right of a counter-clockwise edge), as an angle; two points make two edges.
            var normal = new double[n];
            for (int i = 0; i < n; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % n];
                normal[i] = Math.Atan2(-(double)(b.X - a.X), b.Y - a.Y);
            }
            for (int i = 0; i < n; i++)
            {
                // The corner at hull[i + 1]: from edge i's normal round to edge i + 1's.
                var c = hull[(i + 1) % n];
                double from = normal[i], to = normal[(i + 1) % n];
                while (to < from) to += Math.PI * 2;
                int steps = Math.Max(1, (int)Math.Ceiling((to - from) / step));
                for (int k = 0; k <= steps; k++)
                {
                    double a = from + (to - from) * k / steps;
                    outline.Add(c.XMetres + r * Math.Cos(a));
                    outline.Add(c.YMetres + r * Math.Sin(a));
                }
            }
            outline.Add(outline[0]);
            outline.Add(outline[1]);
            return outline;
        }

        /// <summary>Points along a hull's edges no more than <paramref name="stepMm"/> apart, its corners included.</summary>
        public static List<PointMm> Samples(IReadOnlyList<PointMm> hull, int stepMm)
        {
            var samples = new List<PointMm>();
            int n = hull.Count;
            if (n == 1) { samples.Add(hull[0]); return samples; }
            for (int i = 0; i < n; i++)
            {
                var a = hull[i];
                var b = hull[(i + 1) % n];
                double len = Math.Sqrt(a.DistanceSquared(b));
                int steps = Math.Max(1, (int)Math.Ceiling(len / Math.Max(1, stepMm)));
                for (int k = 0; k < steps; k++)
                    samples.Add(new PointMm((int)Math.Round(a.X + (double)(b.X - a.X) * k / steps), (int)Math.Round(a.Y + (double)(b.Y - a.Y) * k / steps)));
                if (n == 2) { samples.Add(b); break; }   // a two-point hull is one segment
            }
            return samples;
        }
    }
}
