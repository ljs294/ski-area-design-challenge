using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>Why the brush stopped at a point (TB-09's "held" rows).</summary>
    public enum HoldKind : byte
    {
        /// <summary>TB-10: outside the area's core.</summary>
        Area = 1,
        /// <summary>TB-10: 40 ft from natural ground.</summary>
        Depth = 2,
        /// <summary>TB-11: a terminal's pad, or the 1:1 slope into it.</summary>
        Terminal = 3,
        /// <summary>TB-12: the lift's clearance floor under the chairs.</summary>
        LiftLine = 4,
        /// <summary>TB-13: a tower's height limits under its fixed head.</summary>
        Tower = 5,
    }

    /// <summary>One thing that held the brush: its kind, the lift (for terminals, lines and towers) and the tower's number.</summary>
    public readonly struct Hold : IEquatable<Hold>
    {
        public readonly HoldKind Kind;
        public readonly LiftId Lift;
        /// <summary>The tower's number counted from the bottom, 1 up (TB-13); 0 for the rest.</summary>
        public readonly int Tower;
        public Hold(HoldKind kind, LiftId lift, int tower) { Kind = kind; Lift = lift; Tower = tower; }
        public bool Equals(Hold o) => Kind == o.Kind && Lift == o.Lift && Tower == o.Tower;
        public override bool Equals(object obj) => obj is Hold o && Equals(o);
        public override int GetHashCode() => ((int)Kind * 31 + Tower) ^ Lift.GetHashCode();
    }

    /// <summary>
    /// The ground the brush may not move, or not past a height (task 15, TB-11 to TB-13; owner BR9–BR11), as a band of
    /// absolute heights per lattice point:
    /// - a terminal's pad (its footprint plus LF-12's 5 ft) is fixed, and round it the ground slopes at most 1:1 toward
    ///   the pad's level (out to <see cref="TerminalReachMetres"/>), as the pad's own faces do;
    /// - under a built lift's chairs, nothing rises above the clearance floor: the rope (fixed with its heads, TW-02 v4) less
    ///   the chair drop less TW-01's clearance there, low zones included;
    /// - round each tower's base, the ground stays where the tower can still reach its fixed head: TW-02's steel range
    ///   (a station tower's from 15 ft) plus a foundation up to just under 5 ft.
    /// Ropes come from the lifts' own earthwork (<see cref="LiftBuild.Works"/>, the fold's detail), so they never move
    /// with the strokes. Every rule needs only stored data and that detail: the same design gives the same bands.
    /// </summary>
    public sealed class FixedGround
    {
        /// <summary>TB-11: how far round a pad its 1:1 slope is kept, metres; past it the band is wider than TB-10's limit.</summary>
        public const double TerminalReachMetres = 50;
        /// <summary>TB-13: the tower's footing, a 10 ft round of its base.</summary>
        public const double TowerFootMetres = 3.048;
        /// <summary>TB-12: the chair band's sides are widened by a metre, so the lattice posts round every LineGround sample
        /// are held; the floor is the lowest within this of the point along the line.</summary>
        const double BandMargin = 1.0, AlongMargin = 1.5;

        /// <summary>No band: anything goes.</summary>
        public const int Open = int.MinValue, OpenTop = int.MaxValue;

        sealed class Pad
        {
            public double X, Y, Ux, Uy, Behind, Ahead, HalfWidth, Level;
            public ushort Hold;
        }

        sealed class TowerFoot
        {
            public double X, Y, Low, High;   // the allowed ground, metres
            public ushort Hold;
        }

        sealed class LiftLimits
        {
            public PointRect Box;
            public readonly List<Pad> Pads = new List<Pad>(2);
            public readonly List<TowerFoot> Towers = new List<TowerFoot>();
            // The envelope (null when the lift's rope isn't known).
            public LiftRope Rope;
            public double Bx, By, Ux, Uy, Near, Far, Drop;
            public ushort LineHold;
        }

        readonly List<LiftLimits> _lifts = new List<LiftLimits>();
        readonly HashSet<ulong> _covered = new HashSet<ulong>();
        readonly List<Hold> _holds = new List<Hold> { default };   // 0 is "nothing"
        readonly EarthworkSite _site;

        public EarthworkSite Site => _site;

        /// <summary>What each hold index stands for (0 is none).</summary>
        public IReadOnlyList<Hold> Holds => _holds;

        /// <summary>The index of <see cref="HoldKind.Area"/> and <see cref="HoldKind.Depth"/>, always present.</summary>
        public readonly ushort AreaHold, DepthHold;

        FixedGround(EarthworkSite site)
        {
            _site = site;
            AreaHold = Add(new Hold(HoldKind.Area, default, 0));
            DepthHold = Add(new Hold(HoldKind.Depth, default, 0));
        }

        ushort Add(Hold h)
        {
            _holds.Add(h);
            return checked((ushort)(_holds.Count - 1));
        }

        /// <summary>No structures: only the area and depth limits (tests, a design without lifts).</summary>
        public static FixedGround None(EarthworkSite site) => new FixedGround(site);

        /// <summary>
        /// The bands of every built lift in <paramref name="doc"/> whose reach meets <paramref name="area"/> (null: all).
        /// <paramref name="ropeOf"/> gives a lift's earthwork detail (its rope); null or a missing rope keeps only its pads.
        /// </summary>
        public static FixedGround For(DesignDocument doc, EarthworkSite site, Func<LiftId, LiftBuild.Works> ropeOf, PointRect? area = null)
        {
            var fg = new FixedGround(site);
            if (doc == null) return fg;
            var lat = site.Lattice;
            foreach (var l in doc.Lifts)
            {
                if (l.Stage != WorkStage.Built || l.Levels == null) continue;
                var type = LiftType.Of(l.CatalogId);
                if (type?.Bottom == null || type.Top == null) continue;
                site.PointOf(l.Bottom, out double bx, out double by);
                site.PointOf(l.Top, out double tx, out double ty);
                double len = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
                if (len <= 0) continue;
                double ux = (tx - bx) / len, uy = (ty - by) / len;
                double padReach = Math.Max(Math.Max(type.Bottom.Behind, type.Bottom.Ahead), Math.Max(type.Top.Behind, type.Top.Ahead))
                                  + Math.Max(type.Bottom.HalfWidth, type.Top.HalfWidth) + TerminalPad.MarginMetres + TerminalReachMetres + 2;
                double reach = Math.Max(padReach, type.GaugeMetres / 2 + type.ChairHalfWidthMetres + BandMargin + 2);
                var box = new PointRect((int)Math.Floor(Math.Min(bx, tx) - reach - lat.West), (int)Math.Floor(lat.North - Math.Max(by, ty) - reach),
                                        (int)Math.Ceiling(Math.Max(bx, tx) + reach - lat.West), (int)Math.Ceiling(lat.North - Math.Min(by, ty) + reach));
                if (area.HasValue && !box.Intersects(area.Value)) continue;
                fg._covered.Add(l.Id.Value);
                var lim = new LiftLimits { Box = box };
                ushort terminal = fg.Add(new Hold(HoldKind.Terminal, l.Id, 0));
                var levels = l.Levels.Value;
                lim.Pads.Add(new Pad { X = bx, Y = by, Ux = ux, Uy = uy, Behind = type.Bottom.Behind, Ahead = type.Bottom.Ahead, HalfWidth = type.Bottom.HalfWidth, Level = levels.BottomMetres, Hold = terminal });
                lim.Pads.Add(new Pad { X = tx, Y = ty, Ux = -ux, Uy = -uy, Behind = type.Top.Behind, Ahead = type.Top.Ahead, HalfWidth = type.Top.HalfWidth, Level = levels.TopMetres, Hold = terminal });
                var works = ropeOf?.Invoke(l.Id);
                var rope = works?.Rope;
                if (rope != null && rope.Towers.Count == l.Towers.Count)
                {
                    lim.Rope = rope;
                    lim.Bx = bx; lim.By = by; lim.Ux = ux; lim.Uy = uy;
                    double half = type.GaugeMetres / 2, chair = type.ChairHalfWidthMetres;
                    lim.Near = half - chair - BandMargin;
                    lim.Far = half + chair + BandMargin;
                    lim.Drop = type.ChairDropMetres;
                    lim.LineHold = fg.Add(new Hold(HoldKind.LiftLine, l.Id, 0));
                    double zone = DesignUnits.StationZoneMm / 1000.0;
                    for (int i = 0; i < rope.Towers.Count; i++)
                    {
                        var t = rope.Towers[i];
                        bool station = (t.BaseS <= zone + 1e-9 && !type.Bottom.OwnStation) || (rope.LengthMetres - t.BaseS <= zone + 1e-9 && !type.Top.OwnStation);
                        double min = (station ? DesignUnits.StationTowerMinMm : DesignUnits.TowerMinMm) / 1000.0;
                        double cos = Math.Cos(t.Lean);
                        lim.Towers.Add(new TowerFoot
                        {
                            X = bx + ux * t.BaseS, Y = by + uy * t.BaseS,
                            Low = t.RopeY - (type.TowerMaxMetres * cos + LiftRope.MaxReveal),
                            High = t.RopeY - min * cos,
                            Hold = fg.Add(new Hold(HoldKind.Tower, l.Id, i + 1)),
                        });
                    }
                }
                fg._lifts.Add(lim);
            }
            return fg;
        }

        /// <summary>Whether a lift's bands are held here (its reach meets the area asked for).</summary>
        public bool Covers(LiftId id) => _covered.Contains(id.Value);

        /// <summary>
        /// Fills one edit tile's bands: <paramref name="low"/> and <paramref name="high"/> are absolute whole mm (or
        /// <see cref="Open"/> / <see cref="OpenTop"/>), <paramref name="locked"/> points never move, and the hold arrays say
        /// what set each side. Points outside the site's core are locked (TB-10).
        /// </summary>
        public void Fill(EditTileKey key, int[] low, int[] high, bool[] locked, ushort[] lowHold, ushort[] highHold)
        {
            var lat = _site.Lattice;
            var core = _site.Core;
            var rect = new PointRect(key.FirstColumn, key.FirstRow, key.FirstColumn + EditTileKey.Side - 1, key.FirstRow + EditTileKey.Side - 1);
            for (int at = 0; at < EditTileKey.Count; at++)
            {
                low[at] = Open;
                high[at] = OpenTop;
                lowHold[at] = highHold[at] = 0;
                int c = key.FirstColumn + at % EditTileKey.Side, r = key.FirstRow + at / EditTileKey.Side;
                locked[at] = !core.IsEmpty && !core.Contains(c, r);
                if (locked[at]) lowHold[at] = highHold[at] = AreaHold;
            }
            foreach (var lim in _lifts)
            {
                if (!lim.Box.Intersects(rect)) continue;
                for (int at = 0; at < EditTileKey.Count; at++)
                {
                    if (locked[at]) continue;
                    int c = key.FirstColumn + at % EditTileKey.Side, r = key.FirstRow + at / EditTileKey.Side;
                    double x = lat.X(c), y = lat.Y(r);
                    foreach (var pad in lim.Pads)
                    {
                        double d = PadDistance(pad, x, y);
                        if (d <= 0)
                        {
                            locked[at] = true;
                            lowHold[at] = highHold[at] = pad.Hold;
                            break;
                        }
                        if (d > TerminalReachMetres) continue;
                        Lower(at, pad.Level + d, pad.Hold, high, highHold);
                        Raise(at, pad.Level - d, pad.Hold, low, lowHold);
                    }
                    if (locked[at]) continue;
                    foreach (var t in lim.Towers)
                    {
                        double dx = x - t.X, dy = y - t.Y;
                        if (dx * dx + dy * dy > TowerFootMetres * TowerFootMetres) continue;
                        Lower(at, t.High, t.Hold, high, highHold);
                        Raise(at, t.Low, t.Hold, low, lowHold);
                    }
                    if (lim.Rope != null)
                    {
                        double ex = x - lim.Bx, ey = y - lim.By;
                        double s = ex * lim.Ux + ey * lim.Uy, side = ex * lim.Uy - ey * lim.Ux;   // right of uphill is positive
                        double off = Math.Abs(side);
                        if (off >= lim.Near && off <= lim.Far && s >= 0 && s <= lim.Rope.LengthMetres)
                        {
                            double floor = double.MaxValue;
                            for (int k = -1; k <= 1; k++)
                            {
                                double sk = s + k * AlongMargin;
                                if (!lim.Rope.ClearanceAt(sk, out _, out double need)) continue;
                                double rope = side > 0 ? Math.Min(LiftRope.RopeAt(lim.Rope.Loaded, sk), LiftRope.RopeAt(lim.Rope.Empty, sk))
                                                       : LiftRope.RopeAt(lim.Rope.Empty, sk);
                                floor = Math.Min(floor, rope - lim.Drop - need);
                            }
                            if (floor != double.MaxValue) Lower(at, floor, lim.LineHold, high, highHold);
                        }
                    }
                }
            }
        }

        static void Lower(int at, double metres, ushort hold, int[] high, ushort[] why)
        {
            int mm = ToMm(metres);
            if (mm < high[at]) { high[at] = mm; why[at] = hold; }
        }

        static void Raise(int at, double metres, ushort hold, int[] low, ushort[] why)
        {
            int mm = ToMm(metres);
            if (mm > low[at]) { low[at] = mm; why[at] = hold; }
        }

        static int ToMm(double metres)
        {
            double mm = Math.Floor(metres * 1000 + 0.5);
            return mm >= int.MaxValue - 1 ? int.MaxValue - 1 : mm <= int.MinValue + 1 ? int.MinValue + 1 : (int)mm;
        }

        // Distance outside a terminal's pad (footprint plus LF-12's 5 ft) in the line's frame; 0 inside.
        static double PadDistance(Pad p, double x, double y)
        {
            double rx = x - p.X, ry = y - p.Y;
            double along = rx * p.Ux + ry * p.Uy, across = Math.Abs(-rx * p.Uy + ry * p.Ux);
            double b = p.Behind + TerminalPad.MarginMetres, a = p.Ahead + TerminalPad.MarginMetres, w = p.HalfWidth + TerminalPad.MarginMetres;
            double da = along < -b ? -b - along : along > a ? along - a : 0;
            double dc = across > w ? across - w : 0;
            return Math.Sqrt(da * da + dc * dc);
        }
    }
}
