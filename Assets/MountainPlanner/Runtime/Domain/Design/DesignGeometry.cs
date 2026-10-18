using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The design's units: whole millimetres in the package grid (SH-03), so a document has the same bytes on every
    /// machine. Points are measured from the site's centre on the grid's axes (owner ruling TE5): absolute EPSG:6350
    /// millimetres would overflow an int. The rules' imperial steps are exact in millimetres (5 ft = 1,524 mm).
    /// </summary>
    public static class DesignUnits
    {
        public const int FiveFeetMm = 1524;
        public static int FeetToMm(int feet) => checked(feet * FiveFeetMm / 5);

        /// <summary>TR-01: brush widths 25–400 ft in 5 ft steps.</summary>
        public const int BrushMinMm = 25 * FiveFeetMm / 5;
        public const int BrushMaxMm = 400 * FiveFeetMm / 5;

        /// <summary>TW-02: tower heights 25–100 ft in 5 ft steps.</summary>
        public const int TowerMinMm = 25 * FiveFeetMm / 5;
        public const int TowerMaxMm = 100 * FiveFeetMm / 5;
        /// <summary>TW-02 v2 (owner, task 07, SK18): a station tower, in a terminal's load or unload zone, from 15 ft.</summary>
        public const int StationTowerMinMm = 15 * FiveFeetMm / 5;
        /// <summary>TW-02 v2: the station zone reaches 100 ft from either bullwheel.</summary>
        public const int StationZoneMm = 100 * FiveFeetMm / 5;

        /// <summary>TR-05: ends within 30 ft snap; NW-10's Join uses the same reach.</summary>
        public const int SnapMm = 30 * FiveFeetMm / 5;

        /// <summary>Coordinates stay inside ±1,000 km, so dot products of two offsets fit a long.</summary>
        public const int CoordinateLimitMm = 1_000_000_000;

        /// <summary>
        /// A cut's direction vector is at most 1,000 km per component: with coordinates in range, an offset times a
        /// direction stays below 2e18, and their sum below a long's 9.2e18.
        /// </summary>
        public const int DirectionLimitMm = 1_000_000_000;
    }

    /// <summary>A point in the package grid, in whole millimetres.</summary>
    public readonly struct PointMm : IEquatable<PointMm>
    {
        public readonly int X;
        public readonly int Y;
        public PointMm(int x, int y) { X = x; Y = y; }

        public static PointMm FromMetres(double x, double y) => new PointMm(checked((int)Math.Round(x * 1000)), checked((int)Math.Round(y * 1000)));
        public double XMetres => X / 1000.0;
        public double YMetres => Y / 1000.0;

        public long DistanceSquared(PointMm o)
        {
            long dx = (long)o.X - X, dy = (long)o.Y - Y;
            return dx * dx + dy * dy;
        }

        public bool Within(PointMm o, int mm) => DistanceSquared(o) <= (long)mm * mm;
        internal bool InRange => Math.Abs((long)X) <= DesignUnits.CoordinateLimitMm && Math.Abs((long)Y) <= DesignUnits.CoordinateLimitMm;

        public bool Equals(PointMm o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is PointMm o && Equals(o);
        public override int GetHashCode() => (X * 397) ^ Y;
        public static bool operator ==(PointMm a, PointMm b) => a.Equals(b);
        public static bool operator !=(PointMm a, PointMm b) => !a.Equals(b);
        public override string ToString() => $"({X / 1000.0:0.###}, {Y / 1000.0:0.###}) m";
    }

    /// <summary>
    /// One brush stroke (TR-01, TR-02): the discs of one width along a polyline. An erase stroke removes what earlier
    /// strokes of the same part painted. Rasterising is task 08's; the document only keeps what was drawn.
    /// </summary>
    public sealed class Stroke : IEquatable<Stroke>
    {
        public int WidthMm { get; }
        public bool Erase { get; }
        public IReadOnlyList<PointMm> Points => _points;
        readonly PointMm[] _points;

        public Stroke(int widthMm, bool erase, IReadOnlyList<PointMm> points)
        {
            WidthMm = widthMm;
            Erase = erase;
            _points = new PointMm[points?.Count ?? 0];
            for (int i = 0; i < _points.Length; i++) _points[i] = points[i];
        }

        public bool Equals(Stroke o)
        {
            if (o is null || o.WidthMm != WidthMm || o.Erase != Erase || o._points.Length != _points.Length) return false;
            for (int i = 0; i < _points.Length; i++) if (!_points[i].Equals(o._points[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is Stroke o && Equals(o);
        public override int GetHashCode() => WidthMm ^ (_points.Length << 8) ^ (Erase ? 1 : 0);
    }

    /// <summary>
    /// A split line (NW-10, owner ruling P3-03 Q2): the cross-section a trail was split at. A part keeps the side
    /// ahead of the line (towards the tail, dot ≥ 0) or behind it (dot &lt; 0), so every cell goes to exactly one side.
    /// </summary>
    public readonly struct Cut : IEquatable<Cut>
    {
        public readonly PointMm At;
        /// <summary>The direction along the centreline, from head towards tail (any length; mm).</summary>
        public readonly int DirX, DirY;
        public readonly bool KeepAhead;

        public Cut(PointMm at, int dirX, int dirY, bool keepAhead)
        {
            At = at;
            DirX = dirX;
            DirY = dirY;
            KeepAhead = keepAhead;
        }

        /// <summary>The same line, keeping the other side.</summary>
        public Cut Opposite => new Cut(At, DirX, DirY, !KeepAhead);

        /// <summary>The signed side of a point: ≥ 0 ahead of the line, &lt; 0 behind it.</summary>
        public long Side(PointMm p) => ((long)p.X - At.X) * DirX + ((long)p.Y - At.Y) * DirY;

        public bool Keeps(PointMm p) => KeepAhead ? Side(p) >= 0 : Side(p) < 0;

        /// <summary>The same test for a cell centre in metres, for rasterisers.</summary>
        public bool Keeps(double xMetres, double yMetres)
        {
            double side = (xMetres * 1000 - At.X) * DirX + (yMetres * 1000 - At.Y) * DirY;
            return KeepAhead ? side >= 0 : side < 0;
        }

        public bool Equals(Cut o) => At.Equals(o.At) && DirX == o.DirX && DirY == o.DirY && KeepAhead == o.KeepAhead;
        public override bool Equals(object obj) => obj is Cut o && Equals(o);
        public override int GetHashCode() => At.GetHashCode() ^ (DirX * 31) ^ (DirY * 17) ^ (KeepAhead ? 1 : 0);
        internal bool IsComplementOf(Cut o) => At.Equals(o.At) && DirX == o.DirX && DirY == o.DirY && KeepAhead != o.KeepAhead;
    }

    /// <summary>
    /// One part of a trail's footprint: its strokes in order (paint and erase), clipped by its cuts. A trail's footprint
    /// is the union of its parts (TR-02); a part is one unless the trail came from a Join of independently painted
    /// trails.
    /// </summary>
    public sealed class FootprintPart : IEquatable<FootprintPart>
    {
        public IReadOnlyList<Stroke> Strokes => _strokes;
        public IReadOnlyList<Cut> Cuts => _cuts;
        readonly Stroke[] _strokes;
        readonly Cut[] _cuts;

        public FootprintPart(IReadOnlyList<Stroke> strokes, IReadOnlyList<Cut> cuts = null)
        {
            _strokes = new Stroke[strokes?.Count ?? 0];
            for (int i = 0; i < _strokes.Length; i++) _strokes[i] = strokes[i];
            _cuts = new Cut[cuts?.Count ?? 0];
            for (int i = 0; i < _cuts.Length; i++) _cuts[i] = cuts[i];
        }

        public FootprintPart WithCut(Cut cut)
        {
            var cuts = new Cut[_cuts.Length + 1];
            Array.Copy(_cuts, cuts, _cuts.Length);
            cuts[_cuts.Length] = cut;
            return new FootprintPart(_strokes, cuts);
        }

        /// <summary>True when a point is kept by every cut (strokes are rasterised elsewhere).</summary>
        public bool CutsKeep(PointMm p)
        {
            for (int i = 0; i < _cuts.Length; i++) if (!_cuts[i].Keeps(p)) return false;
            return true;
        }

        /// <summary>
        /// If this part and <paramref name="other"/> are the two sides of one split (same strokes, same cuts but one
        /// complementary pair), the part before that split; otherwise null.
        /// </summary>
        internal FootprintPart RejoinWith(FootprintPart other)
        {
            if (other._cuts.Length != _cuts.Length || other._strokes.Length != _strokes.Length) return null;
            for (int i = 0; i < _strokes.Length; i++) if (!_strokes[i].Equals(other._strokes[i])) return null;
            int differ = -1;
            for (int i = 0; i < _cuts.Length; i++)
            {
                if (_cuts[i].Equals(other._cuts[i])) continue;
                if (differ >= 0 || !_cuts[i].IsComplementOf(other._cuts[i])) return null;
                differ = i;
            }
            if (differ < 0) return null;
            var cuts = new Cut[_cuts.Length - 1];
            for (int i = 0, k = 0; i < _cuts.Length; i++) if (i != differ) cuts[k++] = _cuts[i];
            return new FootprintPart(_strokes, cuts);
        }

        public bool Equals(FootprintPart o)
        {
            if (o is null || o._strokes.Length != _strokes.Length || o._cuts.Length != _cuts.Length) return false;
            for (int i = 0; i < _strokes.Length; i++) if (!_strokes[i].Equals(o._strokes[i])) return false;
            for (int i = 0; i < _cuts.Length; i++) if (!_cuts[i].Equals(o._cuts[i])) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is FootprintPart o && Equals(o);
        public override int GetHashCode() => _strokes.Length * 31 + _cuts.Length;
    }
}
