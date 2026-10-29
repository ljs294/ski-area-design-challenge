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
            public EditTileKey Key;
            public readonly int[] S = new int[Count];      // the source, whole mm
            public readonly int[] D = new int[Count];      // the change now (absolute over the source)
            public readonly int[] D0 = new int[Count];     // the change before the stroke
            public readonly int[] Low = new int[Count], High = new int[Count];
            public readonly bool[] Locked = new bool[Count];
            public readonly ushort[] LowHold = new ushort[Count], HighHold = new ushort[Count];
            public bool Dirty;   // changed since the last Preview
            public Patch(EditTileKey key) { Key = key; }
        }

        // Patches pooled across runs (round 4): a stroke's patches, 100 KB a tile, go back when the tool drops the run
        // (Release), and the next stroke's Load writes every entry, so nothing is cleared and nothing is left over.
        static readonly Stack<Patch> PatchPool = new Stack<Patch>();
        const int MaxPooledPatches = 64;   // 6.4 MB

        static Patch NewPatch(EditTileKey key)
        {
            lock (PatchPool)
                if (PatchPool.Count > 0)
                {
                    var p = PatchPool.Pop();
                    p.Key = key;
                    p.Dirty = false;
                    return p;
                }
            return new Patch(key);
        }

        /// <summary>
        /// Fills the pool ahead of the first stroke (the tool arming), so even a 400 ft stroke's first drag over fresh
        /// tiles allocates nothing a frame: <paramref name="count"/> patches at most, 100 KB each.
        /// </summary>
        public static void Warm(int count)
        {
            count = Math.Min(count, MaxPooledPatches);
            lock (PatchPool)
                while (PatchPool.Count < count) PatchPool.Push(new Patch(default));
        }

        /// <summary>Hands the run's patches back for the next run; the run is unusable after (its live view and previews included).</summary>
        public void Release()
        {
            lock (PatchPool)
                foreach (var p in _patches.Values)
                    if (PatchPool.Count < MaxPooledPatches) PatchPool.Push(p);
            _patches.Clear();
            _last = null;
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

        int _dabCount;
        // The running bill (TB-09, BR14), kept as each point moves so the pane never rescans the stroke: cut and fill
        // against the ground before the stroke, whole mm over 1 m² points, and the cells it clears (TB-10), each cell
        // holding how many of its corners moved a foot or more.
        long _cutMm, _fillMm;
        int _clearedCells;
        readonly Dictionary<EditTileKey, byte[]> _cellCorners = new Dictionary<EditTileKey, byte[]>();
        // The last Preview's tiles over the ground it was given, kept so the next one rebuilds only the tiles changed since
        // (a brush's catch-ups ask a few times a second); and the tiles whose cleared cells changed since.
        TerrainEdits _previewUnder;
        readonly SortedDictionary<EditTileKey, TerrainEditTile> _previewTiles = new SortedDictionary<EditTileKey, TerrainEditTile>();
        readonly HashSet<EditTileKey> _cornersDirty = new HashSet<EditTileKey>();
        LiveView _live;
        // The slope's line (TB-07, BR16): its start in lattice metres and the run to the target.
        readonly double _ax, _ay, _vx, _vy, _len2;

        // Smooth's scratch (TB-06), grown as needed.
        long[] _box = Array.Empty<long>(), _sum = Array.Empty<long>();

        public BrushRun(BrushSettings settings, EditedHeights ground, EarthworkSite site, FixedGround fixedGround)
        {
            _s = settings;
            _ground = ground ?? throw new ArgumentNullException(nameof(ground));
            _site = site;
            _fixed = fixedGround ?? FixedGround.None(site);
            if (_s.Kind == BrushKind.Slope)
            {
                site.PointOf(_s.StartAt, out _ax, out _ay);
                site.PointOf(_s.TargetAt, out double bx, out double by);
                _vx = bx - _ax;
                _vy = by - _ay;
                _len2 = _vx * _vx + _vy * _vy;
            }
        }

        public BrushSettings Settings => _s;
        public int DabCount => _dabCount;

        /// <summary>What held the brush so far (TB-09), as <see cref="FixedGround.Holds"/> indices.</summary>
        public IReadOnlyCollection<ushort> HeldIndices => _held;

        /// <summary>The holds <see cref="HeldIndices"/> index: the fixed ground's.</summary>
        public IReadOnlyList<Hold> Holds => _fixed.Holds;

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
            _dabCount++;
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
            int od = p.D[at];
            if (nd == od) return;
            long was = (long)od - p.D0[at], now = (long)nd - p.D0[at];
            _cutMm += Math.Max(0, -now) - Math.Max(0, -was);
            _fillMm += Math.Max(0, now) - Math.Max(0, was);
            bool clearedWas = _s.ClearsTrees && Math.Abs(was) >= _s.ClearMm, clearedNow = _s.ClearsTrees && Math.Abs(now) >= _s.ClearMm;   // TB-10 v2
            if (clearedWas != clearedNow) Corners(c, r, clearedNow ? 1 : -1);
            p.D[at] = nd;
            p.Dirty = true;
            _dirty = _dirty.Include(c, r);
        }

        // TB-07 (BR16): the ramp's height at a point, from where it falls along the line from the start to the target, so
        // the ramp is even between the two points and level across; it holds at the start before it and the target past it.
        long SlopeTarget(double x, double y)
        {
            long start = _s.StartMm, gap = (long)_s.TargetMm - start;
            if (_len2 <= 0) return _s.TargetMm;
            double t = ((x - _ax) * _vx + (y - _ay) * _vy) / _len2;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            return start + (long)Math.Floor(t * gap + 0.5);
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
            var p = NewPatch(key);
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
                    if (!_s.ClearsTrees || Math.Abs((long)p.D[at] - p.D0[at]) < _s.ClearMm) continue;   // TB-10 v2: Clear trees off clears none
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
            // Over another ground than last time, every tile is built again; over the same, only the tiles changed since.
            bool all = !ReferenceEquals(under, _previewUnder);
            if (all) { _previewTiles.Clear(); _previewUnder = under; }
            foreach (var p in _patches.Values)
                if (all || p.Dirty) { Rebuild(p.Key, under); p.Dirty = false; }
            foreach (var key in _cellCorners.Keys)
                if (all || _cornersDirty.Contains(key)) Rebuild(key, under);
            _cornersDirty.Clear();
            return _previewTiles.Count == 0 ? under : under.With(_previewTiles.Values);
        }

        // One tile of the preview: under's tile with the stroke's changes (TB-08) and the cells it clears (TB-10: read from the
        // running corner counts, a cell counting while any corner has moved a foot, never one cleared already), or none.
        void Rebuild(EditTileKey key, TerrainEdits under)
        {
            TerrainEditTile.Builder b = null;
            if (_patches.TryGetValue(key, out var p))
                for (int at = 0; at < Count; at++)
                    if (p.D[at] != p.D0[at]) (b ??= Builder(key, under)).SetDeltaMm(at % Side, at / Side, p.D[at]);
            if (_cellCorners.TryGetValue(key, out var counts))
                for (int at = 0; at < Count; at++)
                    if (counts[at] != 0) (b ??= Builder(key, under)).SetCleared(at % Side, at / Side, true);
            if (b == null) _previewTiles.Remove(key);
            else _previewTiles[key] = b.BuildTaking();
        }

        // One builder a tile, started again from under's tile each time (round 4: a tile is rebuilt a few times a second
        // while painting, so the builder's arrays are reused and the built tile takes them rather than copies).
        readonly Dictionary<EditTileKey, TerrainEditTile.Builder> _builders = new Dictionary<EditTileKey, TerrainEditTile.Builder>();

        TerrainEditTile.Builder Builder(EditTileKey key, TerrainEdits under)
        {
            if (!_builders.TryGetValue(key, out var b)) _builders[key] = b = new TerrainEditTile.Builder(key);
            under.TryGet(key, out var from);
            return b.Reset(key, from);
        }

        /// <summary>TB-09: the stroke's cut and fill against the ground before it, m³ (1 m² a lattice point).</summary>
        public void Volumes(out double cutCubicMetres, out double fillCubicMetres)
        {
            cutCubicMetres = _cutMm / 1000.0;
            fillCubicMetres = _fillMm / 1000.0;
        }

        /// <summary>TB-09: how many cells the stroke clears (the cells of <see cref="Writes"/>' clearing, counted as it goes).</summary>
        public int ClearedCount => _clearedCells;

        // A point's move crossed TB-10's foot: the four cells it's a corner of gain or lose a corner, and a cell not cleared
        // already counts while any of its corners has.
        void Corners(int c, int r, int step)
        {
            for (int dr = -1; dr <= 0; dr++)
                for (int dc = -1; dc <= 0; dc++)
                {
                    int cc = c + dc, cr = r + dr;
                    if (_ground.Edits.IsClearedAt(cc, cr)) continue;
                    var key = EditTileKey.Of(cc, cr);
                    if (!_cellCorners.TryGetValue(key, out var counts)) _cellCorners[key] = counts = new byte[Count];
                    int at = (cr - key.FirstRow) * Side + (cc - key.FirstColumn);
                    _cornersDirty.Add(key);
                    int was = counts[at];
                    counts[at] = (byte)(was + step);
                    if (was == 0) _clearedCells++;
                    else if (was + step == 0) _clearedCells--;
                }
        }

        /// <summary>
        /// The ground with this stroke on it so far, point by point (the live upload, TB-08): at every lattice point exactly
        /// <c>Preview(under)</c>'s change, where <c>under</c> is the ground this run was given, with no tiles built.
        /// Reads the run as it stands; valid until the next dab. Same thread as the dabs.
        /// </summary>
        /// <remarks>It's an <see cref="ICellClearing"/> too (BR22): the cells cleared with the stroke as it stands.</remarks>
        public IPointDeltas Live => _live ??= new LiveView(this);

        sealed class LiveView : IPointDeltas, ICellClearing
        {
            readonly BrushRun _run;
            Patch _last;
            EditTileKey _countsKey = new EditTileKey(int.MinValue, int.MinValue);
            byte[] _counts;

            public LiveView(BrushRun run) { _run = run; }

            /// <summary>
            /// BR22: whether a cell is cleared with the stroke as it stands: <c>Preview</c>'s rule, cleared under it already or
            /// a corner moved the stroke's clearing depth (TB-10 v2), with no tiles built.
            /// </summary>
            public bool IsClearedAt(int cellColumn, int cellRow)
            {
                if (_run._ground.Edits.IsClearedAt(cellColumn, cellRow)) return true;
                var key = EditTileKey.Of(cellColumn, cellRow);
                if (key != _countsKey || _counts == null) { _countsKey = key; _run._cellCorners.TryGetValue(key, out _counts); }   // a tile's counts appear as the stroke reaches it
                return _counts != null && _counts[(cellRow - key.FirstRow) * Side + (cellColumn - key.FirstColumn)] != 0;
            }

            public int DeltaMmAt(int pointColumn, int pointRow)
            {
                var key = EditTileKey.Of(pointColumn, pointRow);
                if (_last == null || _last.Key != key) _run._patches.TryGetValue(key, out _last);
                if (_last == null || _last.Key != key) return _run._ground.Edits.DeltaMmAt(pointColumn, pointRow);
                return _last.D[(pointRow - key.FirstRow) * Side + (pointColumn - key.FirstColumn)];
            }
        }

        /// <summary>Rounds n / d half away from zero (d &gt; 0).</summary>
        internal static long RoundDiv(long n, long d) => n >= 0 ? (n + d / 2) / d : -((-n + d / 2) / d);
    }
}
