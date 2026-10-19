using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What a built trail's grade hands back to the tools (task 08): its footprint, centreline and grade.</summary>
    public sealed class TrailBuild
    {
        public TrailFootprint Footprint { get; internal set; }
        public TrailCentreline.Result Centreline { get; internal set; }
        /// <summary>Null when no centreline joins the ends.</summary>
        public TrailGrader.Graded Grade { get; internal set; }
        /// <summary>What it does to the ground.</summary>
        public EarthworkWrites Writes { get; internal set; }
    }

    /// <summary>
    /// The built trails as earthwork (TL3, task 08): each in creation order, graded over the ground the earlier trails
    /// left (TF-10: it keeps off the points they claim), never within 15 ft of a built lift's tower base or terminal
    /// (TF-11).
    /// </summary>
    public sealed class TrailEarthwork : IEarthworkContributor
    {
        /// <summary>TF-11: grading keeps 15 ft (4.6 m) round each tower base and terminal.</summary>
        public const double LiftRoundMetres = 4.572;

        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            Dictionary<ulong, long> order = null;
            List<(double x, double y)> rounds = null;
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || !t.HasEnds) continue;
                order = order ?? Ordinals(doc);
                rounds = rounds ?? Rounds(doc, site);
                into.Add(new Item(t, doc.Seed, order.TryGetValue(t.Id.Value, out long o) ? o : long.MaxValue, site, rounds));
            }
        }

        /// <summary>
        /// Creation order: IDs are keyed hashes of the document ordinal they were minted at (SH-04), so the ordinal is found
        /// by hashing each one.
        /// </summary>
        static Dictionary<ulong, long> Ordinals(DesignDocument doc)
        {
            var map = new Dictionary<ulong, long>();
            for (long o = 0; o < doc.NextOrdinal; o++) map[TrailId.Make(doc.Seed, o).Value] = o;
            return map;
        }

        /// <summary>TF-11's protected rounds: each built lift's terminals and tower bases, in the lattice's frame.</summary>
        static List<(double x, double y)> Rounds(DesignDocument doc, EarthworkSite site)
        {
            var rounds = new List<(double, double)>();
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built) continue;
                site.PointOf(l.Bottom, out double bx, out double by);
                site.PointOf(l.Top, out double tx, out double ty);
                rounds.Add((bx, by));
                rounds.Add((tx, ty));
                double len = l.LengthMetres;
                if (len <= 0) continue;
                foreach (var tw in l.Towers)
                {
                    double f = tw.AlongMm / 1000.0 / len;
                    rounds.Add((bx + (tx - bx) * f, by + (ty - by) * f));
                }
            }
            return rounds;
        }

        sealed class Item : IEarthworkItem
        {
            readonly Trail _trail;
            readonly EarthworkSite _site;
            readonly (double x, double y)[] _rounds;
            readonly ulong _seed;

            public Item(Trail trail, ulong seed, long order, EarthworkSite site, List<(double x, double y)> rounds)
            {
                _trail = trail;
                _seed = seed;
                _site = site;
                Order = order;
                Reads = ReadsOf(trail, site);
                var mine = new List<(double, double)>();
                var lat = site.Lattice;
                foreach (var (x, y) in rounds)
                {
                    var box = new PointRect((int)Math.Floor(x - LiftRoundMetres - lat.West), (int)Math.Floor(lat.North - y - LiftRoundMetres),
                                            (int)Math.Ceiling(x + LiftRoundMetres - lat.West), (int)Math.Ceiling(lat.North - y + LiftRoundMetres));
                    if (box.Intersects(Reads)) mine.Add((x, y));
                }
                _rounds = mine.ToArray();
            }

            public ulong Key => _trail.Id.Value;
            public int Phase => 0;
            public long Order { get; }
            public PointRect Reads { get; }

            static PointRect ReadsOf(Trail t, EarthworkSite site)
            {
                var rect = PointRect.Empty;
                var lat = site.Lattice;
                foreach (var part in t.Parts)
                    foreach (var s in part.Strokes)
                    {
                        if (s.Erase) continue;
                        double r = s.WidthMm / 2000.0 + 3;
                        foreach (var p in s.Points)
                        {
                            site.PointOf(p, out double x, out double y);
                            rect = rect.Include((int)Math.Floor(x - r - lat.West), (int)Math.Floor(lat.North - y - r));
                            rect = rect.Include((int)Math.Ceiling(x + r - lat.West), (int)Math.Ceiling(lat.North - y + r));
                        }
                    }
                return rect;
            }

            public bool SameAs(IEarthworkItem other)
            {
                if (!(other is Item o)) return false;
                if (o._seed != _seed) return false;
                if (ReferenceEquals(o._trail, _trail) && SameRounds(o)) return true;
                var a = _trail;
                var b = o._trail;
                if (!a.Settings.Equals(b.Settings) || a.Head != b.Head || a.Tail != b.Tail || a.Parts.Count != b.Parts.Count) return false;
                for (int i = 0; i < a.Parts.Count; i++) if (!a.Parts[i].Equals(b.Parts[i])) return false;
                return SameRounds(o);
            }

            bool SameRounds(Item o)
            {
                if (o._rounds.Length != _rounds.Length) return false;
                for (int i = 0; i < _rounds.Length; i++) if (o._rounds[i] != _rounds[i]) return false;
                return true;
            }

            public EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail)
            {
                var build = Grade(_trail, ground, _site, _seed, context.ClaimedEarlier, _rounds);
                detail = build;
                return build.Writes;
            }
        }

        /// <summary>
        /// Grades one trail over <paramref name="ground"/> (the ground as it is before it): footprint, centreline, then the
        /// grade (graded or natural, TL13). <paramref name="claimed"/> and <paramref name="rounds"/> are the points it must
        /// leave alone (TF-10, TF-11).
        /// </summary>
        public static TrailBuild Grade(Trail trail, EditedHeights ground, EarthworkSite site, ulong seed, Func<int, int, bool> claimed,
                                       IReadOnlyList<(double x, double y)> rounds)
        {
            var fp = TrailFootprint.Build(trail.Parts, site.Lattice, site.OriginX, site.OriginY);
            var build = new TrailBuild { Footprint = fp };
            var lat = site.Lattice;
            double r2 = LiftRoundMetres * LiftRoundMetres;
            Func<int, int, bool> isFixed = (c, r) =>
            {
                if (claimed != null && claimed(c, r)) return true;
                if (rounds == null) return false;
                double x = lat.X(c), y = lat.Y(r);
                for (int i = 0; i < rounds.Count; i++)
                {
                    double dx = x - rounds[i].x, dy = y - rounds[i].y;
                    if (dx * dx + dy * dy <= r2) return true;
                }
                return false;
            };
            if (trail.HasEnds && !fp.IsEmpty)
            {
                site.PointOf(trail.Head.Value, out double hx, out double hy);
                site.PointOf(trail.Tail.Value, out double tx, out double ty);
                build.Centreline = TrailCentreline.Find(fp, hx, hy, tx, ty);
            }
            bool graded = trail.Settings.Grading == TrailGrading.Graded;
            if (build.Centreline != null && build.Centreline.Found && build.Centreline.LengthMetres >= 1
                && build.Centreline.Points.Length / 2 <= TrailGrader.MaxStations * 8)
            {
                try
                {
                    build.Grade = TrailGrader.GradeFootprint(ground, fp, build.Centreline.Points, graded, seed, trail.Id, isFixed);
                    build.Writes = build.Grade.Writes;
                    return build;
                }
                catch (ArgumentException) { }   // longer than TR-03's 800 stations: cleared, not graded
            }
            build.Writes = TrailGrader.ClearOnly(fp, seed, trail.Id, isFixed);
            return build;
        }
    }
}
