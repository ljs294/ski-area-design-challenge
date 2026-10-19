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

        /// <summary>
        /// A place grading keeps off (TF-11), in the lattice's frame: a tower base's 15 ft round, or a terminal's measured
        /// footprint (from its bullwheel, Behind away from the line to Ahead along it, and HalfWidth either side) grown by
        /// 15 ft.
        /// </summary>
        public readonly struct Keep : IEquatable<Keep>
        {
            public readonly double X, Y, Ux, Uy, Behind, Ahead, HalfWidth;
            public Keep(double x, double y, double ux, double uy, double behind, double ahead, double halfWidth)
            {
                X = x; Y = y; Ux = ux; Uy = uy; Behind = behind; Ahead = ahead; HalfWidth = halfWidth;
            }

            public static Keep Round(double x, double y) => new Keep(x, y, 1, 0, 0, 0, 0);

            public bool Covers(double x, double y)
            {
                double dx = x - X, dy = y - Y;
                double s = dx * Ux + dy * Uy, t = Math.Abs(-dx * Uy + dy * Ux);
                double ds = s < -Behind ? -Behind - s : s > Ahead ? s - Ahead : 0, dt = Math.Max(0, t - HalfWidth);
                return ds * ds + dt * dt <= LiftRoundMetres * LiftRoundMetres;
            }

            public double Reach => Math.Max(Math.Max(Behind, Ahead), HalfWidth) + LiftRoundMetres;

            public bool Equals(Keep o) => X == o.X && Y == o.Y && Ux == o.Ux && Uy == o.Uy && Behind == o.Behind && Ahead == o.Ahead && HalfWidth == o.HalfWidth;
            public override bool Equals(object obj) => obj is Keep o && Equals(o);
            public override int GetHashCode() => X.GetHashCode() ^ Y.GetHashCode();
        }

        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            Dictionary<ulong, long> order = null;
            List<Keep> rounds = null;
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

        /// <summary>TF-11's places: each built lift's terminals (their measured footprints) and tower bases.</summary>
        static List<Keep> Rounds(DesignDocument doc, EarthworkSite site)
        {
            var keeps = new List<Keep>();
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built) continue;
                site.PointOf(l.Bottom, out double bx, out double by);
                site.PointOf(l.Top, out double tx, out double ty);
                double len = l.LengthMetres;
                if (len <= 0) { keeps.Add(Keep.Round(bx, by)); continue; }
                double ux = (tx - bx) / len, uy = (ty - by) / len;
                var type = LiftType.Of(l.CatalogId);
                keeps.Add(type?.Bottom != null ? new Keep(bx, by, ux, uy, type.Bottom.Behind, type.Bottom.Ahead, type.Bottom.HalfWidth) : Keep.Round(bx, by));
                keeps.Add(type?.Top != null ? new Keep(tx, ty, -ux, -uy, type.Top.Behind, type.Top.Ahead, type.Top.HalfWidth) : Keep.Round(tx, ty));
                foreach (var tw in l.Towers)
                {
                    double f = tw.AlongMm / 1000.0 / len;
                    keeps.Add(Keep.Round(bx + (tx - bx) * f, by + (ty - by) * f));
                }
            }
            return keeps;
        }

        sealed class Item : IEarthworkItem
        {
            readonly Trail _trail;
            readonly EarthworkSite _site;
            readonly Keep[] _rounds;
            readonly ulong _seed;

            public Item(Trail trail, ulong seed, long order, EarthworkSite site, List<Keep> rounds)
            {
                _trail = trail;
                _seed = seed;
                _site = site;
                Order = order;
                Reads = ReadsOf(trail, site);
                Tiles = TilesOf(trail, site);
                var mine = new List<Keep>();
                var lat = site.Lattice;
                foreach (var k in rounds)
                {
                    double reach = k.Reach;
                    var box = new PointRect((int)Math.Floor(k.X - reach - lat.West), (int)Math.Floor(lat.North - k.Y - reach),
                                            (int)Math.Ceiling(k.X + reach - lat.West), (int)Math.Ceiling(lat.North - k.Y + reach));
                    if (box.Intersects(Reads)) mine.Add(k);
                }
                _rounds = mine.ToArray();
            }

            public ulong Key => _trail.Id.Value;
            public int Phase => 0;
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }

            /// <summary>The tiles each stroke's capsule reaches, 3 m wider than the brush (inside <see cref="Reads"/>).</summary>
            static IReadOnlyCollection<EditTileKey> TilesOf(Trail t, EarthworkSite site)
            {
                var keys = new SortedSet<EditTileKey>();
                var lat = site.Lattice;
                foreach (var part in t.Parts)
                    foreach (var s in part.Strokes)
                    {
                        if (s.Erase) continue;
                        double r = s.WidthMm / 2000.0 + 3;
                        var pts = s.Points;
                        for (int k = 0; k < Math.Max(1, pts.Count - 1); k++)
                        {
                            site.PointOf(pts[k], out double ax, out double ay);
                            site.PointOf(pts[Math.Min(k + 1, pts.Count - 1)], out double bx, out double by);
                            double len = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
                            int steps = Math.Max(1, (int)Math.Ceiling(len / 16));
                            for (int q = 0; q <= steps; q++)
                            {
                                double x = ax + (bx - ax) * q / steps, y = ay + (by - ay) * q / steps, reach = r + 16;
                                var box = new PointRect((int)Math.Floor(x - reach - lat.West), (int)Math.Floor(lat.North - y - reach),
                                                        (int)Math.Ceiling(x + reach - lat.West), (int)Math.Ceiling(lat.North - y + reach));
                                foreach (var key in box.TileKeys()) keys.Add(key);
                            }
                        }
                    }
                return keys;
            }

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
                for (int i = 0; i < _rounds.Length; i++) if (!o._rounds[i].Equals(_rounds[i])) return false;
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
                                       IReadOnlyList<Keep> rounds)
        {
            var fp = TrailFootprint.Build(trail.Parts, site.Lattice, site.OriginX, site.OriginY);
            var build = new TrailBuild { Footprint = fp };
            var lat = site.Lattice;
            Func<int, int, bool> isFixed = (c, r) =>
            {
                if (claimed != null && claimed(c, r)) return true;
                if (rounds == null) return false;
                double x = lat.X(c), y = lat.Y(r);
                for (int i = 0; i < rounds.Count; i++) if (rounds[i].Covers(x, y)) return true;
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
