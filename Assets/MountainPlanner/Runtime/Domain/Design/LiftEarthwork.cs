using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// Task 09's part of the earthwork fold (TL3, TL24): every built lift with its levels and towers is a piece laid after
    /// every trail, in creation order. Its writes are what <see cref="LiftBuild.Earthwork"/> (search-free, from what the lift
    /// stores) changes on the ground the fold hands it: the terminals' pads, the trench under the line and the clearing. So a
    /// trail graded under a lift is cut again by the lift's pads and trench, and removing or editing a lift rebuilds only
    /// the ground it reaches, the trails' and the other lifts' work kept exactly.
    /// </summary>
    public sealed class LiftEarthwork : IEarthworkContributor
    {
        /// <summary>How far from the line or a bullwheel a lift's earthwork can reach, metres: a terminal's footprint and its
        /// clearing (CL-09: +20 ft round, +50 ft in front), the corridor (CL-03) and the trench's sides, with room to spare.</summary>
        public const double ReachMetres = 72;

        public void Collect(DesignDocument doc, EarthworkSite site, List<IEarthworkItem> into)
        {
            Dictionary<ulong, long> order = null;
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built || l.Levels == null || l.Towers.Count == 0) continue;
                order ??= Ordinals(doc);
                into.Add(new Item(l, order.TryGetValue(l.Id.Value, out long o) ? o : long.MaxValue, site));
            }
        }

        /// <summary>Whether two lifts do the same earthwork (and hang the same rope): type, ends, stance, levels and towers.</summary>
        public static bool SameWork(Lift a, Lift b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            if (a.CatalogId != b.CatalogId || !a.Bottom.Equals(b.Bottom) || !a.Top.Equals(b.Top) || a.Stance != b.Stance
                || a.Stage != b.Stage || !Nullable.Equals(a.Levels, b.Levels) || a.Towers.Count != b.Towers.Count) return false;
            for (int i = 0; i < a.Towers.Count; i++)
                if (a.Towers[i].AlongMm != b.Towers[i].AlongMm || a.Towers[i].HeightMm != b.Towers[i].HeightMm) return false;
            return true;
        }

        // Creation order: IDs are keyed hashes of the ordinal they were minted at (SH-04).
        static Dictionary<ulong, long> Ordinals(DesignDocument doc) =>
            EarthworkGeometry.OrdinalsOf("lift", doc, (seed, o) => LiftId.Make(seed, o).Value);

        sealed class Item : IEarthworkItem, IReachTiles
        {
            readonly Lift _lift;
            readonly EarthworkSite _site;

            public Item(Lift lift, long order, EarthworkSite site)
            {
                _lift = lift;
                _site = site;
                Order = order;
                // Kept with the lift object (task P3-11): worked out once, not on every preview.
                var geometry = EarthworkGeometry.Of(lift, site, () => Geometry(lift, site));
                Reads = geometry.Item1;
                Tiles = geometry.Item2;
                ReachTiles = geometry.Item3;
            }

            static Tuple<PointRect, IReadOnlyCollection<EditTileKey>, IReadOnlyCollection<EditTileKey>> Geometry(Lift lift, EarthworkSite site)
            {
                var lat = site.Lattice;
                site.PointOf(lift.Bottom, out double bx, out double by);
                site.PointOf(lift.Top, out double tx, out double ty);
                var rect = PointRect.Empty;
                var keys = new SortedSet<EditTileKey>();
                double len = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
                int steps = Math.Max(1, (int)Math.Ceiling(len / 16));
                for (int q = 0; q <= steps; q++)
                {
                    double x = bx + (tx - bx) * q / steps, y = by + (ty - by) * q / steps;
                    var box = new PointRect((int)Math.Floor(x - ReachMetres - lat.West), (int)Math.Floor(lat.North - y - ReachMetres),
                                            (int)Math.Ceiling(x + ReachMetres - lat.West), (int)Math.Ceiling(lat.North - y + ReachMetres));
                    rect = rect.Union(box);
                    foreach (var key in box.TileKeys()) keys.Add(key);
                }
                return Tuple.Create(rect, (IReadOnlyCollection<EditTileKey>)keys, ReachOf(lift, site, bx, by, tx, ty, len));
            }

            /// <summary>
            /// The tiles the lift's earthwork reads (task P3-11), from its stored towers and its type's measures, never
            /// hard-coded: a band along the line (the corridor's clearing, the rope's samples, the envelope's band and
            /// faces), a disc round each bullwheel (the pad's faces and CL-09's clearing, whichever reaches further), and
            /// the trench's box (its loop reads the whole box across the line). Anything wider is <see cref="Tiles"/>.
            /// </summary>
            static IReadOnlyCollection<EditTileKey> ReachOf(Lift lift, EarthworkSite site, double bx, double by, double tx, double ty, double len)
            {
                var lat = site.Lattice;
                var keys = new SortedSet<EditTileKey>();
                var type = LiftType.Of(lift.CatalogId);
                if (len <= 0) return keys;
                double ux = (tx - bx) / len, uy = (ty - by) / len;
                void Box(double x0, double y0, double x1, double y1)
                {
                    var box = new PointRect((int)Math.Floor(Math.Min(x0, x1) - lat.West), (int)Math.Floor(lat.North - Math.Max(y0, y1)),
                                            (int)Math.Ceiling(Math.Max(x0, x1) - lat.West), (int)Math.Ceiling(lat.North - Math.Min(y0, y1)));
                    foreach (var key in box.TileKeys()) keys.Add(key);
                }
                // The band: the corridor (CL-03), or the chairs' paths with the envelope's margin, faces and reads, 2 m to spare.
                double band = Math.Max(LiftClearing.CorridorMetres / 2,
                                       type.GaugeMetres / 2 + type.ChairHalfWidthMetres + LiftLineEnvelope.MarginMetres + LiftLineEnvelope.CapMetres + 2) + 2;
                int steps = Math.Max(1, (int)Math.Ceiling(len / 16));
                for (int q = 0; q < steps; q++)
                {
                    double ax = bx + (tx - bx) * q / steps, ay = by + (ty - by) * q / steps;
                    double cx = bx + (tx - bx) * (q + 1) / steps, cy = by + (ty - by) * (q + 1) / steps;
                    Box(Math.Min(ax, cx) - band, Math.Min(ay, cy) - band, Math.Max(ax, cx) + band, Math.Max(ay, cy) + band);
                }
                // A disc round each bullwheel: the pad's faces (TerminalPad) or the terminal's clearing (CL-09), plus 1 m.
                void Disc(double x, double y, TerminalEnd end)
                {
                    double a = end.Ahead + TerminalPad.MarginMetres, b = end.Behind + TerminalPad.MarginMetres, w = end.HalfWidth + TerminalPad.MarginMetres;
                    double pad = Math.Sqrt(Math.Max(a, b) * Math.Max(a, b) + w * w) + TerminalPad.FaceReachMetres + 2;
                    double ca = end.Ahead + LiftClearing.TerminalMarginMetres + LiftClearing.StationZoneMetres, cb = end.Behind + LiftClearing.TerminalMarginMetres,
                           cw = end.HalfWidth + LiftClearing.TerminalMarginMetres;
                    double clearing = Math.Sqrt(Math.Max(ca, cb) * Math.Max(ca, cb) + cw * cw) + 1;
                    double reach = Math.Max(pad, clearing) + 1;
                    Box(x - reach, y - reach, x + reach, y + reach);
                }
                Disc(bx, by, type.Bottom);
                Disc(tx, ty, type.Top);
                // The trench's box: from the bottom pad's edge (or the station tower's pit) to 15 ft short of the first line
                // tower, with the envelope's reach along and across, as its loop reads it (an axis-aligned box over that run).
                LiftRope.Zones(type, len, out double front, out _, out _, out double end);
                double stationS = type.Bottom.OwnStation ? type.Bottom.ExitAlong : lift.Towers.Count > 0 ? lift.Towers[0].AlongMm / 1000.0 : 0;
                foreach (var tower in lift.Towers)
                {
                    double s = tower.AlongMm / 1000.0;
                    if (s > stationS + 1) { end = Math.Min(end, s - LiftLineEnvelope.TowerKeepMetres); break; }
                }
                end += 5;   // a leaned tower's foot can slide a little from where it's stored
                double half = type.GaugeMetres / 2 + type.ChairHalfWidthMetres + LiftLineEnvelope.MarginMetres;
                double reachT = LiftLineEnvelope.CapMetres + 2;
                double s0 = Math.Min(front, stationS - LiftLineEnvelope.PitHalfMetres) - 1.5 - reachT, s1 = end + 1.5 + reachT;
                double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
                foreach (double s in new[] { s0, s1 })
                    foreach (double d in new[] { -half - reachT, half + reachT })
                    {
                        double x = bx + ux * s + uy * d, y = by + uy * s - ux * d;
                        minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    }
                Box(minX - 1, minY - 1, maxX + 1, maxY + 1);
                return keys;
            }

            public ulong Key => _lift.Id.Value;
            public int Phase => 1;   // after every trail (TL3)
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }
            public IReadOnlyCollection<EditTileKey> ReachTiles { get; }

            public bool SameAs(IEarthworkItem other) => other is Item o && SameWork(_lift, o._lift);

            public EarthworkWrites Build(EditedHeights ground, EarthworkContext context, out object detail)
            {
                _site.PointOf(_lift.Bottom, out double bx, out double by);
                _site.PointOf(_lift.Top, out double tx, out double ty);
                var works = LiftBuild.Earthwork(LiftType.Of(_lift.CatalogId), ground, bx, by, tx, ty, _lift.Towers, _lift.Levels.Value, _lift.Stance);
                detail = works;
                var writes = new EarthworkWrites();
                foreach (var key in TerrainEdits.Changed(ground.Edits, works.Edits))
                {
                    var before = ground.Edits.Get(key);
                    var after = works.Edits.Get(key);
                    for (int j = 0; j < EditTileKey.Side; j++)
                        for (int i = 0; i < EditTileKey.Side; i++)
                        {
                            int c = key.FirstColumn + i, r = key.FirstRow + j;
                            int d = after.DeltaMm(i, j);
                            if (d != before.DeltaMm(i, j)) writes.Write(c, r, d);
                            if (after.IsCleared(i, j) && !before.IsCleared(i, j)) writes.Clear(c, r);
                        }
                }
                return writes;
            }
        }
    }
}
