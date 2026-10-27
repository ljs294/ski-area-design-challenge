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
        static Dictionary<ulong, long> Ordinals(DesignDocument doc)
        {
            var map = new Dictionary<ulong, long>();
            for (long o = 0; o < doc.NextOrdinal; o++) map[LiftId.Make(doc.Seed, o).Value] = o;
            return map;
        }

        sealed class Item : IEarthworkItem
        {
            readonly Lift _lift;
            readonly EarthworkSite _site;

            public Item(Lift lift, long order, EarthworkSite site)
            {
                _lift = lift;
                _site = site;
                Order = order;
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
                Reads = rect;
                Tiles = keys;
            }

            public ulong Key => _lift.Id.Value;
            public int Phase => 1;   // after every trail (TL3)
            public long Order { get; }
            public PointRect Reads { get; }
            public IReadOnlyCollection<EditTileKey> Tiles { get; }

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
