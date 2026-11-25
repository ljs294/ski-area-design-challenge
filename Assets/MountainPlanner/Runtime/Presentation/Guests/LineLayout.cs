using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// LO-02 on screen (task 4.13, the owner's priority): where each skier in a lift's line stands. A line is as wide as
    /// the carrier's seats (a double two, a quad four, a carpet one), 3 ft a column and <see cref="RowMetres"/> a row (a
    /// skier on skis is about 1.7 m long: LO-02's 2.5 ft would stack skis through each other), and winds away from the
    /// load point as one ribbon (task 4.26 BB26, <c>LineLayout.Ribbon.cs</c>), folding back on itself in round hairpins.
    ///
    /// It never runs into anything. A free-space grid of <see cref="CellMetres"/> cells round each bottom station blocks
    /// every lift terminal (plus a margin), every lift's line (its chairs and towers), uncleared forest, ground steeper than
    /// <see cref="MaxGrade"/>, water, the runs themselves (except their last 30 m into the base) and the cells another lift's
    /// line has taken. Lifts at one base take turns: each first takes the <see cref="FirstSlots"/> places nearest its own
    /// load point, then the rest go in blocks of the same size in turn, so no lift takes the whole base.
    ///
    /// A line grows as long as it needs to (owner, 2026-11-10, demo finding 2: "lines should grow as long as they need to,
    /// to hold every guest in them"). Where a fold meets a tree, a steep patch or another line it bends round it; where it
    /// is boxed in it narrows to single file along the shortest free way to open ground and widens again there; with no
    /// open ground in reach it goes on through trees or steep ground (never a terminal, a
    /// lift's line or a run). The design build lays the first <see cref="InitialSlots"/> places of each line;
    /// <see cref="Grow"/> adds <see cref="GrowSlots"/> more when a line nears its end, and places only ever append, so a
    /// skier's place never moves and the tail visibly extends.
    ///
    /// Deterministic: the same design and the same growth requests give the same places, in the same order. Plain C#, a
    /// few milliseconds for a resort and about a millisecond a growth, never per frame.
    /// </summary>
    public sealed partial class LineLayout : IDisposable
    {
        public const float ColumnMetres = 0.9144f;   // 3 ft
        /// <summary>Task 4.26 BB26 (owner, 2026-11-24: "the skiers can stand closer together"): 1.8 m a row (was 7 ft).</summary>
        public const float RowMetres = 1.8f;
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
        /// <summary>Task 4.26 BB4: how near a planted tree's trunk a line stands at most, metres (clear of its lowest branches).</summary>
        public const float TreeClearanceMetres = 1.25f;
        /// <summary>How far a walkway looks for open ground through a gap, metres of path.</summary>
        public const float MaxWalkMetres = 150f;
        const int WalkSearchCells = 12_000;

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
            /// <summary>Task 4.22: how far right of the centreline (looking uphill) the carriers load, metres: half the rope
            /// gauge, so the line's front rows stand under the loaded rope's chairs (0 for a carpet).</summary>
            public float LoadSide;
            /// <summary>Task 4.26 BB26: the line's own key for its keyed choices (0: from its index).</summary>
            public uint Key;
        }

        /// <summary>A run's centreline, for keeping lines off the runs (local x, z per point) and its half width.</summary>
        public struct RunInput
        {
            public Vector2[] Line;
            public float HalfWidth;
        }

        /// <summary>Every lift's places, front first: index <c>LiftSlots[i].x + rank</c>, <c>LiftSlots[i].y</c> of them.</summary>
        public NativeArray<float3> Slots;
        /// <summary>Task 4.22: per place, the way its skier faces (flat, unit): toward the front of the line, along the pass
        /// it is on, so a row stands side by side facing the lift's way rather than at its neighbour.</summary>
        public NativeArray<float2> Facing;
        public NativeArray<int2> LiftSlots;
        public long Revision { get; private set; } = -1;
        /// <summary>Task 4.24 PR B (RD-2): where the top lines start among the inputs (lift i's is input TopSlotsAt + i, a
        /// mirror of it laid at its top station's down side), or -1 when there are none.</summary>
        public int TopSlotsAt { get; private set; } = -1;

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

        /// <summary>Task 4.26 BB4: how many columns line <paramref name="line"/> stands in (place d·seats + c is row d, column c).</summary>
        /// <summary>Task 4.26 BB4: how many stretches line <paramref name="line"/> has (rows across, then single file, ...).</summary>
        public int SectionCount(int line) => RibbonOf(line)?.SectionRows.Count ?? 0;

        /// <summary>Task 4.26 BB4: stretch <paramref name="k"/> of line <paramref name="line"/>: its rows, and whether it is single file.</summary>
        public void Section(int line, int k, out int rows, out bool file)
        {
            var r = _ribbons[line];
            rows = r.SectionRows[k]; file = r.SectionFile[k];
        }

        public int SeatsOf(int line) => RibbonOf(line)?.Seats ?? 1;

        /// <summary>Line <paramref name="line"/>'s ribbon, or null when it has none.</summary>
        Ribbon RibbonOf(int line) => _ribbons != null && line >= 0 && line < _ribbons.Length ? _ribbons[line] : null;

        public int Capacity(int lift) => LiftSlots.IsCreated && lift >= 0 && lift < LiftSlots.Length ? LiftSlots[lift].y : 0;

        /// <summary>Whether lift <paramref name="lift"/>'s line can still grow (it has a line, and room was found last time).</summary>
        public bool CanGrow(int lift) => RibbonOf(lift) is Ribbon r && !r.Done && r.Out.Count < MaxSlots;

        // ================= the ground =================

        // A cell's state: not looked at yet, open, soft (trees or steep: a walkway may cross it when nothing else is
        // left), or hard (a terminal, a lift's line, a run: never). A line's own ground is in the ribbons' claims.
        const byte Unknown = 0, Open = 1, Soft = 2, Hard = 4;

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

        }

        IReadOnlyList<LiftInput> _lifts;
        Grid[] _grids;
        Func<float, float, float> _ground;

        // ================= building =================

        public static LineLayout Build(long revision, IReadOnlyList<LiftInput> lifts, IReadOnlyList<RunInput> runs,
                                       Func<float, float, float> ground, Func<float, float, bool> coverOrWater, int topSlotsAt = -1)
        {
            var layout = new LineLayout { Revision = revision, _lifts = lifts, _ground = ground, TopSlotsAt = topSlotsAt };
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
            for (int i = 0; i < lifts.Count; i++)
            {
                int me = i;
                grids[i] = new Grid((x, z) => Reason(x, z, me));
            }
            layout.BuildRibbons(lifts);
            layout.Diagnose(lifts, Reason);
            return layout;
        }

        /// <summary>A line with little room says why: what covers the ground in the 40 m behind its load point.</summary>
        void Diagnose(IReadOnlyList<LiftInput> lifts, Func<float, float, int, int> Reason)
        {
            var layout = this;
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
        }

        /// <summary>For each lift whose line has little room, what covers the ground behind its load point (logs, captures).</summary>
        public string Diagnosis { get; private set; } = "";

        /// <summary>How lift <paramref name="lift"/>'s line was laid (logs, captures): its places and how it found them.</summary>
        public string Describe(int lift)
        {
            var r = RibbonOf(lift);
            if (r == null) return "no line";
            return $"{r.Out.Count} places ({r.Reframes} turns round something, {r.WalkPlaces} on walkways in {r.Walks}, " +
                   $"{r.SoftPlaces} through trees or steep ground, {r.Length:F0} m of line){(r.Done ? ", no more ground in reach" : "")}";
        }

        /// <summary>Lift <paramref name="lift"/>'s places on walkways through gaps, and through trees or steep ground (tests, logs).</summary>
        public int WalkwayPlaces(int lift) => RibbonOf(lift)?.WalkPlaces ?? 0;
        /// <summary>Task 4.26 BB4: how often a line's centre doubled straight back on itself (it never should), and the
        /// first time, described.</summary>
        public int Reversals
        {
            get
            {
                if (_ribbons == null) return 0;
                int n = 0;
                for (int i = 0; i < _ribbons.Length; i++)
                    if (_ribbons[i] != null)
                    {
                        var look = Measure(i);
                        n += look.CentreBreaks;
                        if (look.CentreBreaks > 0) FirstReversal ??= $"lift {i}: {look.FirstBreak}";
                    }
                return n;
            }
        }
        public string FirstReversal { get; private set; }

        /// <summary>Task 4.26 BB4 probe: the ground lift <paramref name="lift"/>'s line sees at (x, z): '.' open, 't' trees or
        /// steep (a walkway may cross it), '#' never (a terminal, a lift's line, a run), 'o' a line's.</summary>
        public char GroundAt(int lift, float x, float z)
        {
            if (_grids == null || lift < 0 || lift >= _grids.Length || _grids[lift] == null) return ' ';
            if (_ribbons != null && _claims.ContainsKey(Key(Grid.Cell(x), Grid.Cell(z)))) return 'o';
            switch (_grids[lift].State(Grid.Cell(x), Grid.Cell(z)))
            {
                case Open: return '.';
                case Soft: return 't';
                default: return '#';
            }
        }

        public int SoftPlaces(int lift) => RibbonOf(lift)?.SoftPlaces ?? 0;

        /// <summary>
        /// Lays up to <see cref="GrowSlots"/> more places on lift <paramref name="lift"/>'s line (the renderer calls it when
        /// the line nears its end, at most one lift a frame). Places only append; returns whether any were added.
        /// </summary>
        public bool Grow(int lift)
        {
            if (!CanGrow(lift)) return false;
            var r = _ribbons[lift];
            int had = r.Out.Count;
            Extend(r, Math.Min(MaxSlots, had + GrowSlots));
            if (r.Out.Count == had) return false;
            Rebuild();
            return true;
        }

        void Rebuild()
        {
            int count = _ribbons.Length;
            List<Vector3> OutOf(int i) => _ribbons[i]?.Out;
            List<Vector2> FacesOf(int i) => _ribbons[i]?.OutFaces;
            int total = 0;
            for (int i = 0; i < count; i++) total += OutOf(i)?.Count ?? 0;
            if (Slots.IsCreated) Slots.Dispose();
            if (Facing.IsCreated) Facing.Dispose();
            if (LiftSlots.IsCreated) LiftSlots.Dispose();
            Slots = new NativeArray<float3>(Math.Max(1, total), Allocator.Persistent);
            Facing = new NativeArray<float2>(Math.Max(1, total), Allocator.Persistent);
            LiftSlots = new NativeArray<int2>(count, Allocator.Persistent);
            int at = 0;
            for (int i = 0; i < count; i++)
            {
                var o = OutOf(i); var f = FacesOf(i);
                int n = o?.Count ?? 0;
                LiftSlots[i] = new int2(at, n);
                for (int k = 0; k < n; k++) { Slots[at + k] = o[k]; Facing[at + k] = f[k]; }
                at += n;
            }
        }

        // ================= places (task 4.26 BB4) =================

        /// <summary>The line's centre is sampled this often, metres.</summary>
        const float SampleMetres = 0.1f;
        /// <summary>The nearest a lane's next place stands to its last in a straight line, metres (round a tight loop).</summary>
        const float MinLaneGapMetres = 0.8f;

        /// <summary>The half-metre cells every line's places stand on (no two places share one).</summary>
        readonly Dictionary<long, Vector2> _taken = new Dictionary<long, Vector2>();

        /// <summary>The nearest two places stand, metres (more than a cell's diagonal, so a cell holds at most one).</summary>
        const float MinPlaceMetres = 0.75f;

        /// <summary>No place of any line stands nearer than <see cref="MinPlaceMetres"/> to <paramref name="p"/>.</summary>
        bool Free(Vector2 p)
        {
            int ci = Mathf.FloorToInt(p.x / CellMetres), cj = Mathf.FloorToInt(p.y / CellMetres);
            for (int di = -2; di <= 2; di++)
                for (int dj = -2; dj <= 2; dj++)
                    if (_taken.TryGetValue(((long)(ci + di) << 32) ^ (uint)(cj + dj), out var q) && (q - p).sqrMagnitude < MinPlaceMetres * MinPlaceMetres)
                        return false;
            return true;
        }
        static long CellKey(Vector2 p) => ((long)Mathf.FloorToInt(p.x / CellMetres) << 32) ^ (uint)Mathf.FloorToInt(p.y / CellMetres);

        float Height(Vector2 p)
        {
            float y = _ground != null ? _ground(p.x, p.y) : 0;
            return float.IsNaN(y) ? 0 : y;
        }

        static readonly int[] Ni = { 1, 0, -1, 0, 1, -1, -1, 1 }, Nj = { 0, 1, 0, -1, 1, 1, -1, -1 };

        static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

        static Vector2 Toward(Vector2 v, Vector2 otherwise) => v.sqrMagnitude > 1e-6f ? v.normalized : otherwise;

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
            if (Facing.IsCreated) Facing.Dispose();
            if (LiftSlots.IsCreated) LiftSlots.Dispose();
        }
    }
}
