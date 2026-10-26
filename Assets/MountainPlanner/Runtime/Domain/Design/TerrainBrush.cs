using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The terrain brush's numbers (task 15, TB-02 to TB-10). Strokes store what they were painted with, so changing a
    /// default here never moves saved ground (TB-14); only <see cref="BrushRun"/>'s maths is replayed.
    /// </summary>
    public static class TerrainBrush
    {
        /// <summary>TB-02: diameters 10–400 ft, so radii 5–200 ft, in 2½ ft steps (5 ft of diameter).</summary>
        public const int RadiusMinMm = 5 * DesignUnits.FiveFeetMm / 5;
        public const int RadiusMaxMm = 200 * DesignUnits.FiveFeetMm / 5;
        public const int RadiusStepMm = DesignUnits.FiveFeetMm / 2;
        /// <summary>TB-02: the default 60 ft across.</summary>
        public const int RadiusDefaultMm = 30 * DesignUnits.FiveFeetMm / 5;
        /// <summary>TB-02: the one soft edge, the outer half of the radius.</summary>
        public const int FalloffPermille = 500;
        /// <summary>TB-03: dab ticks a second.</summary>
        public const int TicksPerSecond = 30;
        /// <summary>TB-04: at 100% the centre moves about 8 ft a second, 81 mm a tick.</summary>
        public const int StepAtFullMm = 81;
        /// <summary>The largest step a stroke may store (5 ft a tick).</summary>
        public const int StepMaxMm = DesignUnits.FiveFeetMm;
        /// <summary>TB-05 to TB-07: at 100% a full-weight tick moves the ground 35% of the way.</summary>
        public const int StrengthAtFullPermille = 350;
        /// <summary>TB-10: 40 ft from natural ground.</summary>
        public const int LimitMm = 40 * DesignUnits.FiveFeetMm / 5;
        /// <summary>TB-07: grades up to 200%.</summary>
        public const int GradeMaxPermille = 2000;
        /// <summary>TB-10: a cell a stroke moves 1 ft or more is cleared of trees.</summary>
        public const int ClearMm = 305;

        /// <summary>TB-04: a Raise/Lower stroke's step from the pane's strength (1–100%).</summary>
        public static int StepFor(int strengthPercent) => Math.Max(1, (Clamp(strengthPercent) * StepAtFullMm + 50) / 100);

        /// <summary>TB-05 to TB-07: a stroke's share per tick from the pane's strength (1–100%).</summary>
        public static int StrengthFor(int strengthPercent) => Math.Max(1, (Clamp(strengthPercent) * StrengthAtFullPermille + 50) / 100);

        static int Clamp(int percent) => Math.Max(1, Math.Min(100, percent));

        /// <summary>TB-06: the smoothing box's half-width, a sixth of the radius, 1–8 m.</summary>
        public static int SmoothHalfWidth(int radiusMm) => Math.Max(1, Math.Min(8, (radiusMm + 3000) / 6000));
    }

    /// <summary>
    /// One stroke at work (task 15, TB-03 to TB-13): its dabs laid in order over the ground the earthwork fold hands it,
    /// each clamped by <see cref="FixedGround"/> and TB-10, never making a point worse than it stood before the stroke.
    /// The same settings and dabs over the same ground give the same bytes: heights are whole mm, each dab's weights are
    /// quantised to 1/4,096 at once and everything after is integer, with doubles only for +, −, ×, ÷ and √ (no Pow, Exp
    /// or trig). The live preview and the replay run this same code, so what the player sees is what's confirmed.
    /// Not thread-safe; one run per thread.
    /// </summary>
    public sealed class BrushRun
    {
        const int Side = EditTileKey.Side, Count = EditTileKey.Count;
        const long WeightOne = 4096;

        sealed class Patch
        {
            public readonly EditTileKey Key;
            public readonly int[] S = new int[Count];      // the source, whole mm
            public readonly int[] D = new int[Count];      // the change now (absolute over the source)
            public readonly int[] D0 = new int[Count];     // the change before the stroke
            public readonly int[] Low = new int[Count], High = new int[Count];
            public readonly bool[] Locked = new bool[Count];
            public readonly ushort[] LowHold = new ushort[Count], HighHold = new ushort[Count];
            public bool Dirty;
            public Patch(EditTileKey key) { Key = key; }
        }

        readonly BrushSettings _s;
        readonly EditedHeights _ground;
        readonly EarthworkSite _site;
        readonly FixedGround _fixed;
        readonly SortedDictionary<EditTileKey, Patch> _patches = new SortedDictionary<EditTileKey, Patch>();
        readonly HashSet<ushort> _held = new HashSet<ushort>();
        readonly double[] _row = new double[Side];
        Patch _last;
        PointRect _dirty = PointRect.Empty;

        // The slope's path (TB-07): dab centres (lattice metres) and the distance along to each, whole mm.
        readonly List<double> _px = new List<double>(), _py = new List<double>();
        readonly List<long> _along = new List<long>();
        // Segments of non-zero length: their end dab's index.
        readonly List<int> _segments = new List<int>();
        int _firstSegment;

        // Smooth's scratch (TB-06), grown as needed.
        long[] _box = Array.Empty<long>(), _sum = Array.Empty<long>();

        public BrushRun(BrushSettings settings, EditedHeights ground, EarthworkSite site, FixedGround fixedGround)
        {
            _s = settings;
            _ground = ground ?? throw new ArgumentNullException(nameof(ground));
            _site = site;
            _fixed = fixedGround ?? FixedGround.None(site);
        }

        public BrushSettings Settings => _s;
        public int DabCount => _along.Count;

        /// <summary>What held the brush so far (TB-09), as <see cref="FixedGround.Holds"/> indices.</summary>
        public IReadOnlyCollection<ushort> HeldIndices => _held;

        /// <summary>What held the brush so far.</summary>
        public List<Hold> Held()
        {
            var list = new List<Hold>();
            var idx = new List<ushort>(_held);
            idx.Sort();
            foreach (var i in idx) list.Add(_fixed.Holds[i]);
            return list;
        }

        /// <summary>Lays one dab.</summary>
        public void Dab(BrushDab dab)
        {
            _site.PointOf(dab.At, out double cx, out double cy);
            Track(cx, cy);
            double radius = _s.RadiusMm / 1000.0;
            var lat = _site.Lattice;
            int c0 = (int)Math.Ceiling(cx - radius - lat.West), c1 = (int)Math.Floor(cx + radius - lat.West);
            int r0 = (int)Math.Ceiling(lat.North - cy - radius), r1 = (int)Math.Floor(lat.North - cy + radius);
            long weight = dab.WeightPermille;
            if (_s.Kind == BrushKind.Smooth) { SmoothDab(cx, cy, radius, c0, c1, r0, r1, weight); return; }
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    long w = Weight(lat.X(c) - cx, lat.Y(r) - cy, radius);
                    if (w == 0) continue;
                    var p = PatchAt(c, r, out int at);
                    if (p.Locked[at]) { _held.Add(p.LowHold[at]); continue; }
                    long h = (long)p.S[at] + p.D[at];
                    long change;
                    switch (_s.Kind)
                    {
                        case BrushKind.Raise: change = RoundDiv(_s.StepMm * w * weight, WeightOne * 1000); break;
                        case BrushKind.Lower: change = -RoundDiv(_s.StepMm * w * weight, WeightOne * 1000); break;
                        case BrushKind.Flatten: change = Toward(_s.TargetMm - h, w, weight); break;
                        default: change = Toward(SlopeTarget(lat.X(c), lat.Y(r)) - h, w, weight); break;
                    }
                    Set(p, at, c, r, h + change);
                }
        }

        /// <summary>Lays every dab of a stroke (the replay).</summary>
        public void Dabs(IReadOnlyList<BrushDab> dabs)
        {
            foreach (var d in dabs) Dab(d);
        }

        long Toward(long gap, long w, long weight) => RoundDiv(gap * w * _s.StrengthPermille * weight, WeightOne * 1000 * 1000);

        // TB-02's falloff: full inside (1 − f) of the radius, a smoothstep to 0 at the edge; quantised to 1/4,096.
        long Weight(double dx, double dy, double radius)
        {
            double d2 = dx * dx + dy * dy;
            if (d2 >= radius * radius) return 0;
            double q = Math.Sqrt(d2) / radius, f = _s.FalloffPermille / 1000.0, inner = 1 - f;
            double w = 1;
            if (q > inner && f > 0)
            {
                double u = (q - inner) / f;
                w = 1 - u * u * (3 - 2 * u);
            }
            return (long)Math.Floor(w * WeightOne + 0.5);
        }

        void SmoothDab(double cx, double cy, double radius, int c0, int c1, int r0, int r1, long weight)
        {
            if (c1 < c0 || r1 < r0) return;
            var lat = _site.Lattice;
            int k = TerrainBrush.SmoothHalfWidth(_s.RadiusMm);
            int w = c1 - c0 + 1 + 2 * k, h = r1 - r0 + 1 + 2 * k;
            if (_box.Length < w * h) { _box = new long[w * h]; _sum = new long[w * h]; }
            // Pass 1, read only: heights over the dab grown by the box, then the box sums (rows, then columns).
            for (int j = 0; j < h; j++)
                for (int i = 0; i < w; i++)
                {
                    var p = PatchAt(c0 - k + i, r0 - k + j, out int at);
                    _box[j * w + i] = (long)p.S[at] + p.D[at];
                }
            for (int j = 0; j < h; j++)
            {
                long run = 0;
                for (int i = 0; i < w; i++)
                {
                    run += _box[j * w + i];
                    if (i >= 2 * k + 1) run -= _box[j * w + i - 2 * k - 1];
                    _sum[j * w + i] = run;   // the row sum ending at i
                }
            }
            long n = (2L * k + 1) * (2L * k + 1);
            // Pass 2: each point's mean over the (2k + 1)² box, then the blend.
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    long wt = Weight(lat.X(c) - cx, lat.Y(r) - cy, radius);
                    if (wt == 0) continue;
                    int i = c - c0 + 2 * k, j0 = r - r0;
                    long total = 0;
                    for (int j = j0; j <= j0 + 2 * k; j++) total += _sum[j * w + i];
                    long mean = RoundDiv(total, n);
                    long here = _box[(j0 + k) * w + i - k];
                    var p = PatchAt(c, r, out int at);
                    if (p.Locked[at]) { _held.Add(p.LowHold[at]); continue; }
                    Set(p, at, c, r, here + Toward(mean - here, wt, weight));
                }
        }

        // Clamps a new height (TB-10 to TB-13) and stores it; never worse than before the stroke.
        void Set(Patch p, int at, int c, int r, long h)
        {
            long s = p.S[at];
            long before = s + p.D0[at];
            long d = h - s;
            long lim = _s.LimitMm;
            long dLow = Math.Min(p.D0[at], -lim), dHigh = Math.Max(p.D0[at], lim);
            if (d > dHigh) { d = dHigh; _held.Add(_fixed.DepthHold); }
            else if (d < dLow) { d = dLow; _held.Add(_fixed.DepthHold); }
            h = s + d;
            long high = p.High[at] == FixedGround.OpenTop ? long.MaxValue : Math.Max(p.High[at], before);
            long low = p.Low[at] == FixedGround.Open ? long.MinValue : Math.Min(p.Low[at], before);
            if (h > high) { h = high; _held.Add(p.HighHold[at]); }
            else if (h < low) { h = low; _held.Add(p.LowHold[at]); }
            int nd = checked((int)(h - s));
            if (nd == p.D[at]) return;
            p.D[at] = nd;
            p.Dirty = true;
            _dirty = _dirty.Include(c, r);
        }

        // TB-07: the ramp's height at a point, from its distance along the path (its projection onto the nearest segment
        // within 2 radii back), so the ramp is level across.
        long SlopeTarget(double x, double y)
        {
            long start = _s.StartMm, target = _s.TargetMm;
            long s = _along[_along.Count - 1];
            double best = double.MaxValue;
            for (int g = _firstSegment; g < _segments.Count; g++)
            {
                int j = _segments[g];
                double ax = _px[j - 1], ay = _py[j - 1], bx = _px[j], by = _py[j];
                double vx = bx - ax, vy = by - ay, len2 = vx * vx + vy * vy;
                double t = ((x - ax) * vx + (y - ay) * vy) / len2;
                t = t < 0 ? 0 : t > 1 ? 1 : t;
                double qx = ax + vx * t - x, qy = ay + vy * t - y, d2 = qx * qx + qy * qy;
                if (d2 <= best)
                {
                    best = d2;
                    s = _along[j - 1] + (long)Math.Floor(t * (_along[j] - _along[j - 1]) + 0.5);
                }
            }
            long gap = target - start, run = Math.Abs(gap);
            long rise = Math.Min(RoundDiv(s * _s.GradePermille, 1000), run);
            return start + (gap >= 0 ? rise : -rise);
        }

        void Track(double x, double y)
        {
            if (_s.Kind != BrushKind.Slope) { _along.Add(0); return; }
            int n = _along.Count;
            long along = 0;
            if (n > 0)
            {
                double dx = x - _px[n - 1], dy = y - _py[n - 1];
                long step = (long)Math.Floor(Math.Sqrt(dx * dx + dy * dy) * 1000 + 0.5);
                along = _along[n - 1] + step;
                if (step > 0) _segments.Add(n);
            }
            _px.Add(x);
            _py.Add(y);
            _along.Add(along);
            long window = 2L * _s.RadiusMm;
            while (_firstSegment < _segments.Count && along - _along[_segments[_firstSegment]] > window) _firstSegment++;
        }

        Patch PatchAt(int c, int r, out int at)
        {
            var key = EditTileKey.Of(c, r);
            at = (r - key.FirstRow) * Side + (c - key.FirstColumn);
            if (_last != null && _last.Key == key) return _last;
            if (!_patches.TryGetValue(key, out var p)) _patches[key] = p = Load(key);
            _last = p;
            return p;
        }

        Patch Load(EditTileKey key)
        {
            var p = new Patch(key);
            var lat = _site.Lattice;
            var edits = _ground.Edits.Get(key);
            for (int j = 0; j < Side; j++)
            {
                int row = key.FirstRow + j;
                double y = lat.Y(row);
                if (_ground.Source is IHeightRows rows) rows.AtRow(y, lat.West, key.FirstColumn, Side, _row, 0);
                else for (int i = 0; i < Side; i++) _row[i] = _ground.Source.At(lat.X(key.FirstColumn + i), y);
                for (int i = 0; i < Side; i++)
                {
                    int at = j * Side + i;
                    p.S[at] = (int)Math.Floor(_row[i] * 1000 + 0.5);
                    p.D[at] = p.D0[at] = edits.DeltaMm(i, j);
                }
            }
            _fixed.Fill(key, p.Low, p.High, p.Locked, p.LowHold, p.HighHold);
            return p;
        }

        /// <summary>The points changed since the last call (the live upload's rectangle, TB-08), then forgets them.</summary>
        public PointRect TakeDirty()
        {
            var d = _dirty;
            _dirty = PointRect.Empty;
            return d;
        }

        /// <summary>The stroke's writes over the ground it was given: changed heights, and cells cleared (TB-10).</summary>
        public EarthworkWrites Writes()
        {
            var writes = new EarthworkWrites();
            foreach (var p in _patches.Values)
                for (int at = 0; at < Count; at++)
                    if (p.D[at] != p.D0[at]) writes.Write(p.Key.FirstColumn + at % Side, p.Key.FirstRow + at / Side, p.D[at]);
            foreach (var (c, r) in ClearedCells()) writes.Clear(c, r);
            return writes;
        }

        // TB-10: cells with a corner moved 1 ft or more, not cleared already, in key order.
        List<(int c, int r)> ClearedCells()
        {
            var cells = new SortedSet<long>();
            foreach (var p in _patches.Values)
                for (int at = 0; at < Count; at++)
                {
                    if (Math.Abs((long)p.D[at] - p.D0[at]) < TerrainBrush.ClearMm) continue;
                    int c = p.Key.FirstColumn + at % Side, r = p.Key.FirstRow + at / Side;
                    for (int dr = -1; dr <= 0; dr++)
                        for (int dc = -1; dc <= 0; dc++)
                            if (!_ground.Edits.IsClearedAt(c + dc, r + dr)) cells.Add(((long)(r + dr) << 32) | (uint)(c + dc));
                }
            var list = new List<(int, int)>(cells.Count);
            foreach (long v in cells) list.Add(((int)(uint)v, (int)(v >> 32)));
            return list;
        }

        /// <summary>
        /// <paramref name="under"/> with this stroke laid on it (the live preview, TB-08): the changed tiles rebuilt from
        /// <paramref name="under"/>'s, which must be the ground this run was given.
        /// </summary>
        public TerrainEdits Preview(TerrainEdits under)
        {
            var cleared = ClearedCells();
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(EditTileKey key)
            {
                if (!builders.TryGetValue(key, out var b)) builders[key] = b = under.Get(key).Edit();
                return b;
            }
            foreach (var p in _patches.Values)
            {
                TerrainEditTile.Builder b = null;
                for (int at = 0; at < Count; at++)
                    if (p.D[at] != p.D0[at]) (b ??= For(p.Key)).SetDeltaMm(at % Side, at / Side, p.D[at]);
            }
            foreach (var (c, r) in cleared)
            {
                var key = EditTileKey.Of(c, r);
                For(key).SetCleared(c - key.FirstColumn, r - key.FirstRow, true);
            }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var b in builders.Values) tiles.Add(b.Build());
            return tiles.Count == 0 ? under : under.With(tiles);
        }

        /// <summary>TB-09: the stroke's cut and fill against the ground before it, m³ (1 m² a lattice point).</summary>
        public void Volumes(out double cutCubicMetres, out double fillCubicMetres)
        {
            long cut = 0, fill = 0;
            foreach (var p in _patches.Values)
                for (int at = 0; at < Count; at++)
                {
                    long d = (long)p.D[at] - p.D0[at];
                    if (d < 0) cut -= d; else fill += d;
                }
            cutCubicMetres = cut / 1000.0;
            fillCubicMetres = fill / 1000.0;
        }

        /// <summary>TB-09: how many cells the stroke clears.</summary>
        public int ClearedCount => ClearedCells().Count;

        /// <summary>Rounds n / d half away from zero (d &gt; 0).</summary>
        internal static long RoundDiv(long n, long d) => n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d);
    }
}
