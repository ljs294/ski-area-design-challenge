using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// LO-02 on screen (task 4.13, the owner's priority): where each skier in a lift's line stands. A line is as wide as
    /// the carrier's seats (a double two, a quad four, a carpet one), 3 ft a column and 7 ft a row (a skier on skis is
    /// about 1.7 m long: LO-02's 2.5 ft would stack skis through each other), and folds back and forth in passes of at most
    /// <see cref="PassMetres"/> with a 1 m aisle between them, starting at the load point and running away from the lift.
    ///
    /// It never runs into anything. A free-space grid of <see cref="CellMetres"/> cells round each bottom station blocks
    /// every lift terminal (plus a margin), every lift's line (its chairs and towers), uncleared forest, ground steeper than
    /// <see cref="MaxGrade"/>, water, the runs themselves (except their last 30 m into the base) and the cells another lift's
    /// line has taken. Lifts at one base take turns: each first takes the <see cref="FirstSlots"/> places nearest its own
    /// load point, then the rest go in blocks of the same size in turn, so no lift takes the whole base.
    ///
    /// A line grows as long as it needs to (owner, 2026-11-10, demo finding 2: "lines should grow as long as they need to,
    /// to hold every guest in them"). Where a fold meets a tree, a steep patch or another line it turns there and steers
    /// round it; where it is boxed in it strings out single file along the shortest free way to open ground and folds
    /// again there; with no open ground in reach its walkway goes on through trees or steep ground (never a terminal, a
    /// lift's line or a run). The design build lays the first <see cref="InitialSlots"/> places of each line;
    /// <see cref="Grow"/> adds <see cref="GrowSlots"/> more when a line nears its end, and places only ever append, so a
    /// skier's place never moves and the tail visibly extends.
    ///
    /// Deterministic: the same design and the same growth requests give the same places, in the same order. Plain C#, a
    /// few milliseconds for a resort and about a millisecond a growth, never per frame.
    /// </summary>
    public sealed class LineLayout : IDisposable
    {
        public const float ColumnMetres = 0.9144f;   // 3 ft
        public const float RowMetres = 2.1336f;      // 7 ft
        public const float AisleMetres = 1f;
        public const float PassMetres = 12f;
        public const float CellMetres = 0.5f;
        /// <summary>Steeper than this nobody stands in a line (35%: the drawn resort's mid-mountain lifts had no room at 25%).</summary>
        public const float MaxGrade = 0.35f;
        public const float TerminalMarginMetres = 1f;
        public const float RunoutMetres = 30f;
        /// <summary>A line's places stop here: more than the whole crowd (the skier cap is 3,000), so in practice never.</summary>
        public const int MaxSlots = 4000;
        public const int FirstSlots = 25;
        /// <summary>The places each line gets when the design is built; <see cref="Grow"/> adds the rest as lines get long.</summary>
        public const int InitialSlots = 100;
        public const int GrowSlots = 100;
        /// <summary>How far from its load point a line may reach, metres (a safety bound, not a look).</summary>
        public const float MaxReachMetres = 400f;
        /// <summary>How far a walkway looks for open ground through a gap, metres of path.</summary>
        public const float MaxWalkMetres = 150f;
        const int WalkSearchCells = 12_000, SoftWalkSteps = 800;

        /// <summary>A lift as the layout sees it: its bottom (where the line is), its top, its seats and its terminals' size.</summary>
        public struct LiftInput
        {
            public Vector2 Bottom, Top;
            public int Seats;
            /// <summary>The bottom terminal's extent along the lift (behind the bullwheel and ahead of it) and half its width.</summary>
            public float Behind, Ahead, HalfWidth;
            /// <summary>The top terminal's, the same way (along the lift from the top, so "behind" is further uphill).</summary>
            public float TopBehind, TopAhead, TopHalfWidth;
            /// <summary>Half the width of the lift's line that must stay clear (its chairs, rope and towers).</summary>
            public float LaneHalfWidth;
            /// <summary>Whether the lift is built and has a line at all (a planned lift blocks nothing yet and queues nobody).</summary>
            public bool Built;
        }

        /// <summary>A run's centreline, for keeping lines off the runs (local x, z per point) and its half width.</summary>
        public struct RunInput
        {
            public Vector2[] Line;
            public float HalfWidth;
        }

        /// <summary>Every lift's places, front first: index <c>LiftSlots[i].x + rank</c>, <c>LiftSlots[i].y</c> of them.</summary>
        public NativeArray<float3> Slots;
        public NativeArray<int2> LiftSlots;
        public long Revision { get; private set; } = -1;

        /// <summary>How many skiers lift <paramref name="lift"/>'s line has places for now.</summary>
        /// <summary>Place <paramref name="k"/> of lift <paramref name="lift"/>'s line, if it has one (logs, captures).</summary>
        public bool PlaceOf(int lift, int k, out UnityEngine.Vector3 at)
        {
            at = default;
            if (k < 0 || k >= Capacity(lift)) return false;
            var p = Slots[LiftSlots[lift].x + k];
            at = new UnityEngine.Vector3(p.x, p.y, p.z);
            return true;
        }

        public int Capacity(int lift) => LiftSlots.IsCreated && lift >= 0 && lift < LiftSlots.Length ? LiftSlots[lift].y : 0;

        /// <summary>Whether lift <paramref name="lift"/>'s line can still grow (it has a line, and room was found last time).</summary>
        public bool CanGrow(int lift) => _snakes != null && lift >= 0 && lift < _snakes.Length && _snakes[lift] != null
                                         && !_snakes[lift].Done && _snakes[lift].Placed.Count < MaxSlots;

        // ================= the ground =================

        // A cell's state: not looked at yet, open, soft (trees or steep: a walkway may cross it when nothing else is
        // left), taken by a line, or hard (a terminal, a lift's line, a run: never).
        const byte Unknown = 0, Open = 1, Soft = 2, Claimed = 3, Hard = 4;

        sealed class Grid
        {
            readonly Dictionary<long, byte> _cells = new Dictionary<long, byte>();
            readonly Func<float, float, int> _reason;
            public Grid(Func<float, float, int> reason) { _reason = reason; }

            static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;
            public static int Cell(float v) => Mathf.FloorToInt(v / CellMetres);
            public static Vector2 Centre(int i, int j) => new Vector2((i + 0.5f) * CellMetres, (j + 0.5f) * CellMetres);

            public byte State(int i, int j)
            {
                long k = Key(i, j);
                if (!_cells.TryGetValue(k, out byte v))
                {
                    int r = _reason((i + 0.5f) * CellMetres, (j + 0.5f) * CellMetres);
                    v = r == 0 ? Open : r == 4 || r == 5 ? Soft : Hard;
                    _cells[k] = v;
                }
                return v;
            }

            public bool Free(int i, int j) => State(i, j) == Open;
            public bool Passable(int i, int j) { byte s = State(i, j); return s == Open || s == Soft; }
            public void Claim(int i, int j) => _cells[Key(i, j)] = Claimed;
        }

        /// <summary>One lift's line as it is laid: a serpentine in a frame of its own, one row at a time.</summary>
        sealed class Snake
        {
            public int Lift;
            public Vector2 Load;                    // the load point (the bound's centre)
            public Vector2 Origin, Along, Across;   // Along: the way the passes run; Across: the side the passes step to
            public int Seats;
            public float Width;
            public int Pass;          // which pass the next row is on
            public float U;           // the next row's distance along, metres
            public int Direction = 1; // +1 along, −1 back
            public int RowsOnPass;
            public bool Done;
            public Vector2 Tail, TailDir;   // the last place laid, and the way the line was going there
            public int Turns, WalkPlaces, SoftPlaces;
            /// <summary>The last search for a gap found none: don't search again until the line is on open ground.</summary>
            public bool WalkFailed;
            /// <summary>
            /// No open ground in reach: its folds stand on trees or steep ground too (never a hard block), so the line stays
            /// folded at its lift rather than strung out single file through the woods.
            /// </summary>
            public bool SoftGround;
            public bool Fits(Grid grid, int i, int j) => SoftGround ? grid.Passable(i, j) : grid.Free(i, j);
            public readonly List<Vector3> Placed = new List<Vector3>();

            public float V => Pass * (Width + AisleMetres) + Width * 0.5f;
            public Vector2 Point(float u, float v) => Origin + Along * u + Across * v;
        }

        IReadOnlyList<LiftInput> _lifts;
        Grid[] _grids;
        Snake[] _snakes;
        Func<float, float, float> _ground;

        // ================= building =================

        public static LineLayout Build(long revision, IReadOnlyList<LiftInput> lifts, IReadOnlyList<RunInput> runs,
                                       Func<float, float, float> ground, Func<float, float, bool> coverOrWater)
        {
            var layout = new LineLayout { Revision = revision, _lifts = lifts, _ground = ground };
            var runIndex = new RunIndex(runs);
            // Why a spot is no good for standing in a line (0: it's fine); the reasons feed the build's diagnosis.
            // 1 bottom terminal, 2 top terminal, 3 a lift's line, 4 forest or water, 5 steep, 6 a run.
            int Reason(float x, float z, int exempt)
            {
                var p = new Vector2(x, z);
                for (int i = 0; i < lifts.Count; i++)
                {
                    var l = lifts[i];
                    if (!l.Built) continue;
                    var up = (l.Top - l.Bottom);
                    float len = up.magnitude;
                    if (len < 1e-3f) continue;
                    var u = up / len;
                    var r = new Vector2(u.y, -u.x);
                    var d = p - l.Bottom;
                    float a = Vector2.Dot(d, u), s = Vector2.Dot(d, r);
                    // The bottom terminal, except the load lane of the lift whose line this is.
                    bool inBottom = a >= -l.Behind - TerminalMarginMetres && a <= l.Ahead + TerminalMarginMetres
                                    && Mathf.Abs(s) <= l.HalfWidth + TerminalMarginMetres;
                    if (inBottom && !(i == exempt && a <= 0.5f && Mathf.Abs(s) <= l.Seats * ColumnMetres * 0.5f + 0.25f)) return 1;
                    // The top terminal (its "behind" is further uphill).
                    var dt = p - l.Top;
                    float at = Vector2.Dot(dt, -u), st = Vector2.Dot(dt, r);
                    if (at >= -l.TopBehind - TerminalMarginMetres && at <= l.TopAhead + TerminalMarginMetres
                        && Mathf.Abs(st) <= l.TopHalfWidth + TerminalMarginMetres) return 2;
                    // The line itself: chairs, rope and towers.
                    if (a >= 0 && a <= len && Mathf.Abs(s) <= l.LaneHalfWidth) return 3;
                }
                var load = exempt >= 0 ? lifts[exempt].Bottom : p;
                if (runIndex.OnARun(p, load)) return 6;
                if (coverOrWater != null && coverOrWater(x, z)) return 4;
                if (ground != null)
                {
                    float e = ground(x + 1, z), w = ground(x - 1, z), n = ground(x, z + 1), so = ground(x, z - 1);
                    if (!float.IsNaN(e) && !float.IsNaN(w) && !float.IsNaN(n) && !float.IsNaN(so))
                    {
                        float gx = (e - w) * 0.5f, gz = (n - so) * 0.5f;
                        if (gx * gx + gz * gz > MaxGrade * MaxGrade) return 5;
                    }
                }
                return 0;
            }

            // One grid per lift (its exemption differs), sharing the claims through Claim on every grid.
            var grids = layout._grids = new Grid[lifts.Count];
            var snakes = layout._snakes = new Snake[lifts.Count];
            for (int i = 0; i < lifts.Count; i++)
            {
                int me = i;
                grids[i] = new Grid((x, z) => Reason(x, z, me));
            }
            // The throat first, for every lift: the line's front rows run straight back from the load point under the
            // terminal (its load lane is always the way in), so the fold starts clear of the terminal and can turn any
            // way from there. Without it a line could only leave the terminal straight back, and trees right behind it
            // left a lift no line at all with open ground beside it (drawn resort, first capture).
            var throats = new List<Vector3>[lifts.Count];
            var starts = new Vector2[lifts.Count];
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                if (!l.Built || l.Seats < 1) continue;
                var up = l.Top - l.Bottom;
                var u = up.sqrMagnitude > 1e-6f ? up.normalized : Vector2.up;
                var back = -u;
                var side = new Vector2(back.y, -back.x);
                float width = l.Seats * ColumnMetres;
                // Past the terminal's back by half the line's width, so a fold turned sideways clears its corner.
                int rows = Mathf.Max(1, Mathf.CeilToInt((Mathf.Max(0, l.Behind) + TerminalMarginMetres + width * 0.5f) / RowMetres));
                throats[i] = new List<Vector3>(rows * l.Seats);
                for (int k = 0; k < rows; k++)
                {
                    var centre = l.Bottom + back * (RowMetres * (k + 0.5f));
                    for (float du = -RowMetres * 0.5f + CellMetres * 0.5f; du < RowMetres * 0.5f; du += CellMetres)
                        for (float dv = -width * 0.5f + CellMetres * 0.5f; dv < width * 0.5f; dv += CellMetres)
                        {
                            var q = centre + back * du + side * dv;
                            foreach (var g in grids) g.Claim(Grid.Cell(q.x), Grid.Cell(q.y));
                        }
                    for (int c = 0; c < l.Seats; c++)
                    {
                        var q = centre + side * (-width * 0.5f + ColumnMetres * (c + 0.5f));
                        throats[i].Add(new Vector3(q.x, layout.Height(q), q.y));
                    }
                }
                starts[i] = l.Bottom + back * (RowMetres * rows);
            }
            for (int i = 0; i < lifts.Count; i++)
            {
                if (throats[i] == null) continue;
                var l = lifts[i];
                var up = l.Top - l.Bottom;
                var back = -(up.sqrMagnitude > 1e-6f ? up.normalized : Vector2.up);
                snakes[i] = layout.Choose(l, i, grids[i], starts[i]);
                snakes[i].Placed.AddRange(throats[i]);
                snakes[i].Tail = starts[i] - back * (RowMetres * 0.5f);
                snakes[i].TailDir = back;
            }

            // Turns: everyone's first places, then blocks of the same size, round and round, up to the first places.
            bool any = true;
            while (any)
            {
                any = false;
                for (int i = 0; i < snakes.Length; i++)
                {
                    var s = snakes[i];
                    if (s == null || s.Done) continue;
                    int target = Math.Min(InitialSlots, s.Placed.Count + FirstSlots);
                    while (!s.Done && s.Placed.Count < target)
                        if (!layout.NextRow(s, grids[i], claim: true)) s.Done = true;
                    if (!s.Done && s.Placed.Count < InitialSlots) any = true;
                }
            }
            layout.Rebuild();

            // A line with little room says why: what covers the ground in the 40 m behind its load point.
            var why = new System.Text.StringBuilder();
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                if (!l.Built || layout.LiftSlots[i].y >= FirstSlots) continue;
                var up = l.Top - l.Bottom;
                var u = up.sqrMagnitude > 1e-6f ? up.normalized : Vector2.up;
                var r = new Vector2(u.y, -u.x);
                var count = new int[7];
                for (float a = 0.5f; a < 40; a += 1f)
                    for (float sd = -20f; sd <= 20f; sd += 1f)
                    {
                        var q = l.Bottom - u * a + r * sd;
                        count[Reason(q.x, q.y, i)]++;
                    }
                why.Append($"lift {i} room {layout.LiftSlots[i].y}: free {count[0]}, bottom terminal {count[1]}, top terminal {count[2]}, " +
                           $"lift lane {count[3]}, forest {count[4]}, steep {count[5]}, run {count[6]} (of 1640 m² behind it); ");
            }
            layout.Diagnosis = why.ToString();
            return layout;
        }

        /// <summary>For each lift whose line has little room, what covers the ground behind its load point (logs, captures).</summary>
        public string Diagnosis { get; private set; } = "";

        /// <summary>How lift <paramref name="lift"/>'s line was laid (logs, captures): its places and how it found them.</summary>
        public string Describe(int lift)
        {
            var s = _snakes != null && lift >= 0 && lift < _snakes.Length ? _snakes[lift] : null;
            if (s == null) return "no line";
            return $"{s.Placed.Count} places ({s.Turns} turns round something, {s.WalkPlaces} on walkways, " +
                   $"{s.SoftPlaces} through trees or steep ground){(s.Done ? ", no more ground in reach" : "")}";
        }

        /// <summary>Lift <paramref name="lift"/>'s places on walkways through gaps, and through trees or steep ground (tests, logs).</summary>
        public int WalkwayPlaces(int lift) => _snakes != null && lift >= 0 && lift < _snakes.Length ? _snakes[lift]?.WalkPlaces ?? 0 : 0;
        public int SoftPlaces(int lift) => _snakes != null && lift >= 0 && lift < _snakes.Length ? _snakes[lift]?.SoftPlaces ?? 0 : 0;

        /// <summary>
        /// Lays up to <see cref="GrowSlots"/> more places on lift <paramref name="lift"/>'s line (the renderer calls it when
        /// the line nears its end, at most one lift a frame). Places only append; returns whether any were added.
        /// </summary>
        public bool Grow(int lift)
        {
            if (!CanGrow(lift)) return false;
            var s = _snakes[lift];
            int before = s.Placed.Count, target = Math.Min(MaxSlots, before + GrowSlots);
            while (!s.Done && s.Placed.Count < target)
                if (!NextRow(s, _grids[lift], claim: true)) s.Done = true;
            if (s.Placed.Count == before) return false;
            Rebuild();
            return true;
        }

        void Rebuild()
        {
            int total = 0;
            foreach (var s in _snakes) if (s != null) total += s.Placed.Count;
            if (Slots.IsCreated) Slots.Dispose();
            if (LiftSlots.IsCreated) LiftSlots.Dispose();
            Slots = new NativeArray<float3>(Math.Max(1, total), Allocator.Persistent);
            LiftSlots = new NativeArray<int2>(_snakes.Length, Allocator.Persistent);
            int at = 0;
            for (int i = 0; i < _snakes.Length; i++)
            {
                var s = _snakes[i];
                int n = s?.Placed.Count ?? 0;
                LiftSlots[i] = new int2(at, n);
                for (int k = 0; k < n; k++) Slots[at + k] = s.Placed[k];
                at += n;
            }
        }

        float Height(Vector2 p)
        {
            float y = _ground != null ? _ground(p.x, p.y) : 0;
            return float.IsNaN(y) ? 0 : y;
        }

        /// <summary>
        /// Picks the line's first heading: straight back from the lift first, then turned 45° and 90° either way, each with
        /// its passes stepping to either side. The one that lays the most rows (tried without claiming) wins; ties keep the
        /// earlier, so the straight-back line is preferred when it fits.
        /// </summary>
        Snake Choose(LiftInput l, int lift, Grid grid, Vector2 start)
        {
            var up = l.Top - l.Bottom;
            var u = up.sqrMagnitude > 1e-6f ? up.normalized : Vector2.up;
            var back = -u;
            Snake best = null;
            int bestRows = -1;
            foreach (float turn in new[] { 0f, 45f, -45f, 90f, -90f })
            {
                var along = Rotate(back, turn);
                foreach (int sideSign in new[] { 1, -1 })
                {
                    var across = new Vector2(along.y, -along.x) * sideSign;
                    var trial = NewSnake(l, lift, along, across, start, turn);
                    int rows = 0;
                    while (rows < 60 && NextRow(trial, grid, claim: false)) rows++;
                    if (rows > bestRows) { bestRows = rows; best = NewSnake(l, lift, along, across, start, turn); }
                }
            }
            return best;
        }

        static Snake NewSnake(LiftInput l, int lift, Vector2 along, Vector2 across, Vector2 start, float turn)
        {
            int seats = Math.Max(1, l.Seats);
            float width = seats * ColumnMetres;
            // The line's first row stands just behind the load point, centred on it: the origin is the corner of the
            // first pass, half a line's width to the near side.
            return new Snake
            {
                Lift = lift, Seats = seats, Width = width, Along = along, Across = across, Load = l.Bottom,
                // A fold turned off the throat starts beside it, not on its last row (which is already taken).
                Origin = start - across * (width * 0.5f) + along * (RowMetres * 0.5f + width * 0.5f * Mathf.Abs(Mathf.Sin(turn * Mathf.Deg2Rad))),
            };
        }

        /// <summary>A fresh fold whose first row is centred on <paramref name="centre"/>, its passes running along <paramref name="along"/>.</summary>
        static Snake FrameAt(Snake s, Vector2 centre, Vector2 along, Vector2 across) => new Snake
        {
            Lift = s.Lift, Seats = s.Seats, Width = s.Width, Load = s.Load, SoftGround = s.SoftGround,
            Along = along, Across = across, Origin = centre - across * (s.Width * 0.5f),
        };

        /// <summary>
        /// Lays the next row (or, boxed in, a stretch of walkway). On this pass if it fits and the pass isn't full; else
        /// across the aisle onto the next pass going the other way (the turn's elbow and the next pass's first row must be
        /// free). A trial (<paramref name="claim"/> false) stops there. Laying for real it then steers round whatever is in
        /// the way, then walks through a gap to open ground, then, with nothing open in reach, through trees or steep
        /// ground. Returns false only when the line can't go on at all.
        /// </summary>
        bool NextRow(Snake s, Grid grid, bool claim)
        {
            bool passFull = s.Direction > 0 ? s.U > PassMetres - RowMetres * 0.5f : s.U < RowMetres * 0.5f;
            if (!passFull && RowFree(s, s.U, s.V, grid))
            {
                PlaceRow(s, s.U, s.V, claim);
                s.U += s.Direction * RowMetres;
                s.RowsOnPass++;
                return true;
            }
            // Turn: the aisle between this pass and the next, at the pass's end, must be free, and the next pass's first row.
            if (s.RowsOnPass > 0 && s.Pass < 64)
            {
                float endU = s.U - s.Direction * RowMetres;
                float nextV = s.V + s.Width + AisleMetres;
                if (StripFree(s, endU, s.V + s.Width * 0.5f, nextV - s.Width * 0.5f, grid) && RowFree(s, endU, nextV, grid))
                {
                    s.Pass++;
                    s.Direction = -s.Direction;
                    s.U = endU;
                    PlaceRow(s, s.U, s.V, claim);
                    s.U += s.Direction * RowMetres;
                    s.RowsOnPass = 1;
                    return true;
                }
            }
            if (!claim) return false;
            // Each way on must lay someone, or the line has ended (a true here always means a new place).
            int before = s.Placed.Count;
            if (Steer(s, grid, s.Tail, s.TailDir, commit: true, allowSoft: false) && s.Placed.Count > before) return true;
            if (!s.WalkFailed)
            {
                if (Walk(s, grid) && s.Placed.Count > before) return true;
                s.WalkFailed = true;   // the same search from the same pocket would find nothing again (and costs ~100 ms)
            }
            // Nothing open in reach: fold on over trees or steep ground, right here (finding 2 follow-up, 2026-11-10: a
            // single-file walkway 180 m through the woods didn't read as the lift's line).
            if (!s.SoftGround)
            {
                s.SoftGround = true;
                if (Steer(s, grid, s.Tail, s.TailDir, commit: true, allowSoft: true) && s.Placed.Count > before) return true;
            }
            // Hemmed in by hard blocks (terminals, lift lines, runs) even so: a walkway out through whatever soft ground there is.
            SoftWalk(s, grid);
            return s.Placed.Count > before;
        }

        static readonly float[] SteerTurns = { 0f, 45f, -45f, 90f, -90f, 135f, -135f };

        /// <summary>
        /// Steers round what is in the way: from <paramref name="from"/>, a new fold headed straight on, then 45°, 90° and
        /// 135° either way, its passes stepping to either side, its first row a row or so off. The one that lays the most
        /// rows (tried, up to 12) wins; ties keep the earlier. With <paramref name="commit"/> it becomes the line's frame and
        /// its first row is laid.
        /// </summary>
        bool Steer(Snake s, Grid grid, Vector2 from, Vector2 dir, bool commit, bool allowSoft)
        {
            if (dir.sqrMagnitude < 1e-6f) dir = (from - s.Load).sqrMagnitude > 1e-6f ? (from - s.Load).normalized : Vector2.down;
            dir = dir.normalized;
            Snake best = null;
            int bestRows = 0;
            float reach = MaxReachMetres * MaxReachMetres;
            foreach (float turn in SteerTurns)
            {
                var h = Rotate(dir, turn).normalized;
                foreach (int side in new[] { 1, -1 })
                {
                    var across = new Vector2(h.y, -h.x) * side;
                    foreach (float d in new[] { RowMetres, RowMetres * 1.5f, RowMetres + s.Width * 0.5f, RowMetres * 2.5f })
                    {
                        var centre = from + h * d;
                        if ((centre - s.Load).sqrMagnitude > reach) continue;
                        var trial = FrameAt(s, centre, h, across);
                        if (!RowFree(trial, 0, trial.V, grid) || !GapClear(grid, from, centre, allowSoft)) continue;
                        int rows = 0;
                        while (rows < 12 && NextRow(trial, grid, claim: false)) rows++;
                        if (rows > bestRows) { bestRows = rows; best = FrameAt(s, centre, h, across); }
                        break;   // the nearest start that fits, for this heading and side
                    }
                }
            }
            if (best == null) return false;
            if (!commit) return true;
            s.Origin = best.Origin; s.Along = best.Along; s.Across = best.Across;
            s.Pass = 0; s.U = 0; s.Direction = 1; s.RowsOnPass = 0;
            s.Turns++;
            s.WalkFailed = false;   // open ground again: a gap may help next time
            // Its first row, laid here (never through NextRow, which would steer again if it somehow didn't fit).
            if (!RowFree(s, 0, s.V, grid)) return false;
            PlaceRow(s, 0, s.V, claim: true);
            s.U = RowMetres;
            s.RowsOnPass = 1;
            return true;
        }

        /// <summary>
        /// Boxed in: the shortest way over open cells (8-connected, at most <see cref="MaxWalkMetres"/> of path) to ground
        /// where a fold fits, laid single file, a skier every row's length, then the fold there.
        /// </summary>
        bool Walk(Snake s, Grid grid)
        {
            float reach = MaxReachMetres * MaxReachMetres;
            float disc = Mathf.Max(s.Width, RowMetres) * 0.5f + 0.5f;
            var parent = new Dictionary<long, long>();
            var depth = new Dictionary<long, int>();
            var queue = new Queue<(int, int)>();
            float r0 = Mathf.Max(s.Width, RowMetres) * 0.5f + 1f;
            int rc = Mathf.CeilToInt(r0 / CellMetres);
            int ti = Grid.Cell(s.Tail.x), tj = Grid.Cell(s.Tail.y);
            for (int dj = -rc; dj <= rc; dj++)
                for (int di = -rc; di <= rc; di++)
                {
                    int i = ti + di, j = tj + dj;
                    if ((Grid.Centre(i, j) - s.Tail).magnitude > r0 || !grid.Free(i, j)) continue;
                    long k = Key(i, j);
                    if (parent.ContainsKey(k)) continue;
                    parent[k] = long.MinValue; depth[k] = 0;
                    queue.Enqueue((i, j));
                }
            int maxDepth = Mathf.CeilToInt(MaxWalkMetres / CellMetres), visited = 0;
            while (queue.Count > 0 && visited < WalkSearchCells)
            {
                var (i, j) = queue.Dequeue();
                visited++;
                long key = Key(i, j);
                int dep = depth[key];
                var c = Grid.Centre(i, j);
                if (dep >= 4 && (dep & 1) == 0 && OpenDisc(grid, c, disc))
                {
                    var dir = (c - s.Tail).normalized;
                    if (Steer(s, grid, c, dir, commit: false, allowSoft: false))
                    {
                        // The path back to the tail, laid front (the tail's end) first.
                        var path = new List<Vector2>();
                        for (long at = key; at != long.MinValue; at = parent[at]) path.Add(Grid.Centre((int)(at >> 32), (int)(uint)at));
                        path.Reverse();
                        float since = 0;
                        int laid = 0;
                        var prev = s.Tail;
                        foreach (var p in path)
                        {
                            since += (p - prev).magnitude;
                            ClaimAround(p);
                            if (since >= RowMetres) { s.Placed.Add(new Vector3(p.x, Height(p), p.y)); s.WalkPlaces++; laid++; since = 0; }
                            prev = p;
                        }
                        var end = path[path.Count - 1];
                        if (laid == 0) { s.Placed.Add(new Vector3(end.x, Height(end), end.y)); s.WalkPlaces++; }
                        s.TailDir = path.Count > 1 ? (end - path[path.Count - 2]).normalized : dir;
                        s.Tail = end;
                        Steer(s, grid, end, s.TailDir, commit: true, allowSoft: false);
                        return true;
                    }
                }
                if (dep >= maxDepth) continue;
                for (int n = 0; n < 8; n++)
                {
                    int ni = i + Ni[n], nj = j + Nj[n];
                    long nk = Key(ni, nj);
                    if (parent.ContainsKey(nk) || !grid.Free(ni, nj)) continue;
                    if ((Grid.Centre(ni, nj) - s.Load).sqrMagnitude > reach) continue;
                    parent[nk] = key; depth[nk] = dep + 1;
                    queue.Enqueue((ni, nj));
                }
            }
            return false;
        }

        static readonly int[] Ni = { 1, 0, -1, 0, 1, -1, -1, 1 }, Nj = { 0, 1, 0, -1, 1, 1, -1, -1 };
        static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

        /// <summary>
        /// No open ground in reach: the walkway goes on through trees or steep ground (never a terminal, a lift's line, a
        /// run or another line), away from the lift, until a fold fits again. Returns whether it laid anyone.
        /// </summary>
        bool SoftWalk(Snake s, Grid grid)
        {
            float reach = MaxReachMetres * MaxReachMetres;
            float disc = Mathf.Max(s.Width, RowMetres) * 0.5f + 0.5f;
            var away = (s.Tail - s.Load).sqrMagnitude > 1f ? (s.Tail - s.Load).normalized : s.TailDir.normalized;
            int h = Octant(away);
            // Out of the tail's own row first: the nearest way off it (away from the lift first) onto ground a walkway may cross.
            float r0 = Mathf.Max(s.Width, RowMetres) * 0.5f + 0.5f;
            int ci = 0, cj = 0;
            bool start = false;
            foreach (int turn in new[] { 0, 1, -1, 2, -2, 3, -3, 4 })
            {
                int d = ((h + turn) % 8 + 8) % 8;
                var q = s.Tail + new Vector2(Oi[d], Oj[d]).normalized * r0;
                ci = Grid.Cell(q.x); cj = Grid.Cell(q.y);
                if ((q - s.Load).sqrMagnitude <= reach && grid.Passable(ci, cj) && GapClear(grid, s.Tail, q, allowSoft: true)) { h = d; start = true; break; }
            }
            if (!start) return false;
            float since = RowMetres;   // the first step off the row is someone's place
            int placed = 0;
            for (int step = 0; step < SoftWalkSteps; step++)
            {
                // One cell on: straight, then 45° and 90° either way (cells, so the walkway never steps on itself).
                int next = -1;
                foreach (int turn in new[] { 0, 1, -1, 2, -2 })
                {
                    int d = ((h + turn) % 8 + 8) % 8;
                    int ni = ci + Oi[d], nj = cj + Oj[d];
                    if ((Grid.Centre(ni, nj) - s.Load).sqrMagnitude > reach || !grid.Passable(ni, nj)) continue;
                    next = d;
                    break;
                }
                if (next < 0) break;
                // Take this cell and the two beside it (across the way on), then step.
                foreach (var g in _grids)
                {
                    g.Claim(ci, cj);
                    g.Claim(ci + Oi[(next + 2) % 8], cj + Oj[(next + 2) % 8]);
                    g.Claim(ci + Oi[(next + 6) % 8], cj + Oj[(next + 6) % 8]);
                }
                ci += Oi[next]; cj += Oj[next]; h = next;
                since += (next & 1) == 1 ? CellMetres * 1.4142f : CellMetres;
                var pos = Grid.Centre(ci, cj);
                var heading = new Vector2(Oi[h], Oj[h]).normalized;
                s.Tail = pos; s.TailDir = heading;
                if (since >= RowMetres)
                {
                    s.Placed.Add(new Vector3(pos.x, Height(pos), pos.y));
                    s.SoftPlaces++; placed++; since = 0;
                }
                if (placed > 0 && OpenDisc(grid, pos + heading * (disc + 0.5f), disc)
                    && Steer(s, grid, pos, heading, commit: true, allowSoft: true)) return true;
            }
            return placed > 0;
        }

        // The eight ways round a cell, counter-clockwise from east.
        static readonly int[] Oi = { 1, 1, 0, -1, -1, -1, 0, 1 }, Oj = { 0, 1, 1, 1, 0, -1, -1, -1 };
        static int Octant(Vector2 v) => ((int)Mathf.Round(Mathf.Atan2(v.y, v.x) / (Mathf.PI / 4f)) % 8 + 8) % 8;

        /// <summary>Takes a walkway's cell and its neighbours, on every lift's grid, so no line stands in it.</summary>
        void ClaimAround(Vector2 p)
        {
            int ci = Grid.Cell(p.x), cj = Grid.Cell(p.y);
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                    foreach (var g in _grids) g.Claim(ci + di, cj + dj);
        }

        static bool OpenDisc(Grid grid, Vector2 c, float r)
        {
            // The centre and eight points round the rim first: most spots fail there, cheaply.
            if (!grid.Free(Grid.Cell(c.x), Grid.Cell(c.y))) return false;
            for (int d = 0; d < 8; d++)
            {
                var q = c + new Vector2(Oi[d], Oj[d]).normalized * (r - CellMetres * 0.5f);
                if (!grid.Free(Grid.Cell(q.x), Grid.Cell(q.y))) return false;
            }
            int rc = Mathf.CeilToInt(r / CellMetres), ci = Grid.Cell(c.x), cj = Grid.Cell(c.y);
            for (int dj = -rc; dj <= rc; dj++)
                for (int di = -rc; di <= rc; di++)
                {
                    if ((Grid.Centre(ci + di, cj + dj) - c).sqrMagnitude > r * r) continue;
                    if (!grid.Free(ci + di, cj + dj)) return false;
                }
            return true;
        }

        /// <summary>Whether the way from the tail to a new fold's first row crosses nothing (another line is fine to pass).</summary>
        static bool GapClear(Grid grid, Vector2 a, Vector2 b, bool allowSoft)
        {
            float len = (b - a).magnitude;
            for (float t = CellMetres; t < len; t += CellMetres)
            {
                var p = a + (b - a) * (t / len);
                byte st = grid.State(Grid.Cell(p.x), Grid.Cell(p.y));
                if (st == Hard || (st == Soft && !allowSoft)) return false;
            }
            return true;
        }

        static bool RowFree(Snake s, float u, float v, Grid grid)
        {
            float half = s.Width * 0.5f;
            float reach = MaxReachMetres * MaxReachMetres;
            for (float du = -RowMetres * 0.5f + CellMetres * 0.5f; du < RowMetres * 0.5f; du += CellMetres)
                for (float dv = -half + CellMetres * 0.5f; dv < half; dv += CellMetres)
                {
                    var p = s.Point(u + du, v + dv);
                    if ((p - s.Load).sqrMagnitude > reach) return false;
                    if (!s.Fits(grid, Grid.Cell(p.x), Grid.Cell(p.y))) return false;
                }
            return true;
        }

        static bool StripFree(Snake s, float u, float v0, float v1, Grid grid)
        {
            for (float du = -RowMetres * 0.5f + CellMetres * 0.5f; du < RowMetres * 0.5f; du += CellMetres)
                for (float v = v0 + CellMetres * 0.5f; v < v1; v += CellMetres)
                {
                    var p = s.Point(u + du, v);
                    if (!s.Fits(grid, Grid.Cell(p.x), Grid.Cell(p.y))) return false;
                }
            return true;
        }

        void PlaceRow(Snake s, float u, float v, bool claim)
        {
            if (!claim) return;
            float half = s.Width * 0.5f;
            if (s.SoftGround)
            {
                var c0 = s.Point(u, v);
                if (_grids[s.Lift].State(Grid.Cell(c0.x), Grid.Cell(c0.y)) == Soft) s.SoftPlaces += s.Seats;
            }
            // The cells it covers are taken, on every lift's grid, so no other line ever stands on them.
            for (float du = -RowMetres * 0.5f + CellMetres * 0.5f; du < RowMetres * 0.5f; du += CellMetres)
                for (float dv = -half + CellMetres * 0.5f; dv < half; dv += CellMetres)
                {
                    var p = s.Point(u + du, v + dv);
                    int ci = Grid.Cell(p.x), cj = Grid.Cell(p.y);
                    foreach (var g in _grids) g.Claim(ci, cj);
                }
            // Front of the line first: column 0 is on the side the line came from on this pass, so the snake reads
            // as one queue folding back on itself.
            for (int c = 0; c < s.Seats; c++)
            {
                float dv = -half + ColumnMetres * (c + 0.5f);
                var p = s.Point(u, v + dv);
                s.Placed.Add(new Vector3(p.x, Height(p), p.y));
            }
            s.Tail = s.Point(u, v);
            s.TailDir = s.Along * s.Direction;
        }

        static Vector2 Rotate(Vector2 v, float degrees)
        {
            float r = degrees * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
            return new Vector2(v.x * c - v.y * s, v.x * s + v.y * c);
        }

        /// <summary>The runs' centrelines in 4 m buckets, so "is this on a run" costs a few lookups.</summary>
        sealed class RunIndex
        {
            const float Bucket = 4f;
            readonly Dictionary<long, List<(Vector2 P, float Half, float FromEnd)>> _b = new Dictionary<long, List<(Vector2, float, float)>>();

            public RunIndex(IReadOnlyList<RunInput> runs)
            {
                if (runs == null) return;
                foreach (var r in runs)
                {
                    if (r.Line == null || r.Line.Length < 2) continue;
                    float total = 0;
                    for (int k = 1; k < r.Line.Length; k++) total += (r.Line[k] - r.Line[k - 1]).magnitude;
                    float half = Mathf.Max(2f, r.HalfWidth * 0.6f);
                    float walked = 0;
                    Add(r.Line[0], half, total);
                    for (int k = 1; k < r.Line.Length; k++)
                    {
                        // Every 2 m along the piece, not just its vertices, so a sparse centreline blocks its whole length.
                        var a = r.Line[k - 1];
                        float seg = (r.Line[k] - a).magnitude;
                        int steps = Mathf.Max(1, Mathf.CeilToInt(seg / 2f));
                        for (int t = 1; t <= steps; t++) Add(Vector2.Lerp(a, r.Line[k], t / (float)steps), half, total - walked - seg * t / steps);
                        walked += seg;
                    }
                }
            }

            void Add(Vector2 p, float half, float fromEnd)
            {
                long key = Key(Mathf.FloorToInt(p.x / Bucket), Mathf.FloorToInt(p.y / Bucket));
                if (!_b.TryGetValue(key, out var list)) _b[key] = list = new List<(Vector2, float, float)>();
                list.Add((p, half, fromEnd));
            }

            static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

            /// <summary>Whether <paramref name="p"/> is on a run, except a run's last 30 m and anything near the load point.</summary>
            public bool OnARun(Vector2 p, Vector2 load)
            {
                int bi = Mathf.FloorToInt(p.x / Bucket), bj = Mathf.FloorToInt(p.y / Bucket);
                for (int i = bi - 3; i <= bi + 3; i++)
                    for (int j = bj - 3; j <= bj + 3; j++)
                    {
                        if (!_b.TryGetValue(Key(i, j), out var list)) continue;
                        foreach (var (q, half, fromEnd) in list)
                        {
                            if (fromEnd <= RunoutMetres) continue;                 // a run's runout into the base
                            if ((q - load).sqrMagnitude < RunoutMetres * RunoutMetres) continue;
                            if ((q - p).sqrMagnitude <= half * half) return true;
                        }
                    }
                return false;
            }
        }

        public void Dispose()
        {
            if (Slots.IsCreated) Slots.Dispose();
            if (LiftSlots.IsCreated) LiftSlots.Dispose();
        }
    }
}
