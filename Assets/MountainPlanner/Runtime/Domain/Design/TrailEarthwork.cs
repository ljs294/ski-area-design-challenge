using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>What a built trail's grade hands back to the tools (task 08): its footprint, centreline and grade.</summary>
    public sealed class TrailBuild
    {
        /// <summary>The trail as it was graded: a view can reuse this build while the trail is the same object.</summary>
        public Trail Trail { get; internal set; }
        public TrailFootprint Footprint { get; internal set; }
        public TrailCentreline.Result Centreline { get; internal set; }
        /// <summary>Whether <see cref="Centreline"/> is the trail's own (TR-03 on its own footprint and ends); false for a
        /// part of a split pair graded as one (NT2), whose line is the pair's, cut at its ends.</summary>
        public bool CentrelineIsOwn { get; internal set; } = true;
        /// <summary>Null when no centreline joins the ends.</summary>
        public TrailGrader.Graded Grade { get; internal set; }
        /// <summary>What it does to the ground.</summary>
        public EarthworkWrites Writes { get; internal set; }

        /// <summary>
        /// Whether this build was made for <paramref name="t"/>: the same object, or the same trail by value (task P3-11:
        /// the history applies a command again, so the trail it keeps is never the object the earthwork graded).
        /// </summary>
        public bool IsFor(Trail t) =>
            t != null && Trail != null && (ReferenceEquals(Trail, t) ||
                Trail.Id == t.Id && Trail.Stage == t.Stage && Trail.Head == t.Head && Trail.Tail == t.Tail &&
                Trail.Settings.Equals(t.Settings) && Trail.Shape.Equals(t.Shape));

        /// <summary>The build's centreline when it's <paramref name="t"/>'s own (never a split part's slice of its pair's, NT2), else null.</summary>
        public TrailCentreline.Result OwnLineFor(Trail t) => IsFor(t) && CentrelineIsOwn ? Centreline : null;
    }

    /// <summary>
    /// The built trails as earthwork (TL3, task 08): each in creation order, graded over the ground the earlier trails
    /// left (TF-10: it keeps off the points they claim), never within 15 ft of a built lift's tower base or terminal
    /// (TF-11). The parts of a split trail grade as one (NT2, <see cref="TrailChains"/>), so Split changes no ground.
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
            var built = new List<Trail>();
            foreach (var t in doc.Trails) if (t.Stage == WorkStage.Built && t.HasEnds) built.Add(t);
            if (built.Count == 0) return;
            order = Ordinals(doc);
            rounds = Rounds(doc, site);
            foreach (var chain in TrailChains.Of(built))
            {
                var top = chain[0];
                long o = order.TryGetValue(top.Id.Value, out long v) ? v : long.MaxValue;
                into.Add(chain.Count == 1
                    ? new Item(top, null, doc.Seed, o, site, rounds)
                    : new Item(TrailChains.Whole(chain), chain.ToArray(), doc.Seed, o, site, rounds));
            }
        }

        /// <summary>
        /// Creation order: IDs are keyed hashes of the document ordinal they were minted at (SH-04), so the ordinal is found
        /// by hashing each one.
        /// </summary>
        static Dictionary<ulong, long> Ordinals(DesignDocument doc) =>
            EarthworkGeometry.OrdinalsOf("trail", doc, (seed, o) => TrailId.Make(seed, o).Value);

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
            /// <summary>NT2: the parts of a split chain, top first, which <see cref="_trail"/> (the chain as one) grades for;
            /// null for a trail on its own.</summary>
            readonly Trail[] _members;
            readonly EarthworkSite _site;
            readonly Keep[] _rounds;
            readonly ulong _seed;

            public Item(Trail trail, Trail[] members, ulong seed, long order, EarthworkSite site, List<Keep> rounds)
            {
                _trail = trail;
                _members = members;
                _seed = seed;
                _site = site;
                Order = order;
                // Kept with the trail object (task P3-11): the mask's rows are walked once, not on every preview.
                var geometry = EarthworkGeometry.Of(trail, site, () => Tuple.Create(ReadsOf(trail, site), TilesOf(trail, site)));
                Reads = geometry.Item1;
                Tiles = geometry.Item2;
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

            /// <summary>The tiles the clearing's rows reach, 3 m wider than it and the grader's 16 m reach (inside <see cref="Reads"/>).</summary>
            static IReadOnlyCollection<EditTileKey> TilesOf(Trail t, EarthworkSite site)
            {
                var keys = new SortedSet<EditTileKey>();
                var lat = site.Lattice;
                var mask = t.Shape.Mask;
                const double Cell = TrailMask.CellMm / 1000.0, Reach = 3 + 16;
                // Every run of every row, widened by the reach, as a box: a wide box would swallow the gap between two arms.
                for (int j = mask.Row0; j < mask.RowEnd; j++)
                {
                    {
                        var r = mask.Row(j);
                        for (int k = 0; k < r.Count; k += 2)
                        {
                            site.PointOf(new PointMm((int)TrailMask.EdgeMm(r[k]), (int)TrailMask.EdgeMm(j)), out double ax, out double ay);
                            double bx = ax + (r[k + 1] - r[k]) * Cell, by = ay + Cell;
                            var box = new PointRect((int)Math.Floor(ax - Reach - lat.West), (int)Math.Floor(lat.North - by - Reach),
                                                    (int)Math.Ceiling(bx + Reach - lat.West), (int)Math.Ceiling(lat.North - ay + Reach));
                            foreach (var key in box.TileKeys()) keys.Add(key);
                        }
                    }
                }
                return keys;
            }

            static PointRect ReadsOf(Trail t, EarthworkSite site)
            {
                var lat = site.Lattice;
                if (t.Shape.IsEmpty) return PointRect.Empty;
                t.Shape.Bounds(out int x0, out int y0, out int x1, out int y1);
                site.PointOf(new PointMm(x0, y0), out double ax, out double ay);
                site.PointOf(new PointMm(x1, y1), out double bx, out double by);
                const double R = 3;
                var rect = PointRect.Empty;
                rect = rect.Include((int)Math.Floor(ax - R - lat.West), (int)Math.Floor(lat.North - by - R));
                rect = rect.Include((int)Math.Ceiling(bx + R - lat.West), (int)Math.Ceiling(lat.North - ay + R));
                return rect;
            }

            public bool SameAs(IEarthworkItem other)
            {
                if (!(other is Item o)) return false;
                if (o._seed != _seed || !SameMembers(o)) return false;
                if (ReferenceEquals(o._trail, _trail) && SameRounds(o)) return true;
                var a = _trail;
                var b = o._trail;
                if (!a.Settings.Equals(b.Settings) || a.Head != b.Head || a.Tail != b.Tail || !a.Shape.Equals(b.Shape)) return false;
                return SameRounds(o);
            }

            bool SameMembers(Item o)
            {
                if ((_members == null) != (o._members == null)) return false;
                if (_members == null) return true;
                if (_members.Length != o._members.Length) return false;
                for (int i = 0; i < _members.Length; i++)
                    if (_members[i].Id != o._members[i].Id || _members[i].Head != o._members[i].Head || _members[i].Tail != o._members[i].Tail)
                        return false;
                return true;
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
                detail = _members == null ? (object)build : TrailChains.Parts(build, _members, _site);
                return build.Writes;
            }
        }

        /// <summary>
        /// Grades one trail over <paramref name="ground"/> (the ground as it is before it): footprint, centreline, then the
        /// grade (graded or natural, TL13). <paramref name="claimed"/> and <paramref name="rounds"/> are the points it must
        /// leave alone (TF-10, TF-11).
        /// </summary>
        public static TrailBuild Grade(Trail trail, EditedHeights ground, EarthworkSite site, ulong seed, Func<int, int, bool> claimed,
                                       IReadOnlyList<Keep> rounds, TrailGrader.Options options = null)
        {
            var fp = TrailFootprint.Build(trail.Shape, site.Lattice, site.OriginX, site.OriginY);
            var build = new TrailBuild { Footprint = fp, Trail = trail };
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
                    build.Grade = TrailGrader.GradeFootprint(ground, fp, build.Centreline.Points, graded, seed, trail.Id, isFixed, options: options);
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
