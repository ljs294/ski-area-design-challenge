using System;
using System.Collections.Generic;
using System.Globalization;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// A magic carpet's bed (surface lifts PR B, owner C12/C13 and 2026-11-06): what a belt lift has in place of a rope over
    /// towers. The carpet rides over the ground; the ground is never cut.
    /// <list type="bullet">
    /// <item>The line from the bottom (tension) station's origin up to the top (drive) station's: each station a rigid piece,
    /// level or pitched up to 8 % (owner 2026-11-06: the ends needn't be level, only not steep), the belt between in 2 m
    /// modules (and one 1 m filler where a metre is left), the top station on the last joint, up to a metre short of where
    /// it was placed.</item>
    /// <item>The profile follows the ground: the lowest bed that keeps <see cref="SnowRoomMetres"/> between the module's foot
    /// and the highest ground under it everywhere (room for the snow Phase 4 puts on the ground, so it isn't buried), each
    /// joint turning at most 1° (C12), including where the modules meet the stations.</item>
    /// <item>Under the carpet the ground is filled up to that room below it where it's lower (never cut), with 1:1 fill faces
    /// out to the ground; the module's skirt runs down to it. The approaches (C13) are filled to the snow room below the
    /// plates' tops for 16 ft (the top's after a 2 m run-off falling 5 %), then 1:10 down to the ground, 1 m wider than the
    /// deck each side.</item>
    /// <item>Limits: 30 ft to 1,000 ft long, 115 ft of rise and 25 % at most (red); over 18 % on average or 23 % anywhere is
    /// steep, and more than 3 ft of fill under it deep (amber).</item>
    /// </list>
    /// Stored levels (save v2's) are minimums for the stations' origins, so the same line over the same ground lays the same
    /// bed. Everything it changes, and the deck's corridor, is cleared. Positions in the edit lattice's frame; s is metres
    /// along the line from the bottom station's origin, y the elevation of the modules' (and stations') foot.
    /// </summary>
    public static class CarpetBed
    {
        public const double ModuleMetres = 2.0, FillerMetres = 1.0;
        /// <summary>C12: at most 1° a joint (the hardware takes 2°).</summary>
        public const double BendPerJoint = Math.PI / 180;
        /// <summary>Owner 2026-11-06: the belt's surface 18 in over the ground at least (glidepath_carpet.json deckTop: the belt
        /// 352 mm over the modules' foot).</summary>
        public const double BeltHeightMetres = 18 * 0.0254, DeckTopMetres = 0.352;
        /// <summary>The room that leaves between the carpet's foot and the ground under it (about 4 in), for the snow.</summary>
        public const double SnowRoomMetres = BeltHeightMetres - DeckTopMetres;
        /// <summary>Owner 2026-11-06: each station may sit pitched up or down the line by as much as this.</summary>
        public const double EndGradeLimit = 0.08;
        /// <summary>C13's margin past the deck, each side.</summary>
        public const double MarginMetres = 1.0;
        public const double FaceSlope = 1, FaceReachMetres = 15.24;
        /// <summary>C13: the approaches' level run, the top's run-off before it and its fall, and the ease to the ground.</summary>
        public const double ApproachFlat = 4.9, RunOff = 2.0, RunOffFall = 0.05, Ease = 0.10, EaseReach = 25;
        public const double MinLengthMetres = 30 * 0.3048, MaxLengthMetres = 1000 * 0.3048, RiseLimitMetres = 115 * 0.3048;
        public const double SteepAverage = 0.18, SteepLocal = 0.23, GradeLimit = 0.25;
        /// <summary>More fill than this under the carpet is amber (3 ft).</summary>
        public const double DeepFillMetres = 3 * 0.3048;
        /// <summary>The ground's sampling along the line, metres.</summary>
        const double Step = 0.25;
        /// <summary>A module may be as steep as the ground within this many metres of it, and this much more.</summary>
        const double SlopeWindow = 6, SlopeAllowance = 0.01;

        public sealed class Result
        {
            public TerrainEdits Edits { get; internal set; }
            /// <summary>Each joint along the belt, from the bottom station's exit to the top station's entry: (s, y).</summary>
            public IReadOnlyList<(double s, double y)> Joints { get; internal set; }
            /// <summary>Each module's length in order (2 m, and one 1 m filler at the top end).</summary>
            public IReadOnlyList<double> Modules { get; internal set; }
            /// <summary>Each station's level at its origin, metres.</summary>
            public double BottomLevel { get; internal set; }
            public double TopLevel { get; internal set; }
            /// <summary>Each station's pitch up the line (a fraction, within ±<see cref="EndGradeLimit"/>).</summary>
            public double BottomPitch { get; internal set; }
            public double TopPitch { get; internal set; }
            /// <summary>The steeper station's pitch, as a fraction (0 when both are level).</summary>
            public double EndGrade => Math.Max(Math.Abs(BottomPitch), Math.Abs(TopPitch));
            /// <summary>Where the top station stands, metres along the line from the bottom origin (≤ the placed length).</summary>
            public double TopOriginS { get; internal set; }
            public double LengthMetres { get; internal set; }
            public double RiseMetres => TopLevel - BottomLevel;
            /// <summary>The steepest module's grade and the whole carpet's, as fractions.</summary>
            public double Grade { get; internal set; }
            public double AverageGrade { get; internal set; }
            /// <summary>The most fill under the carpet's centre line, metres (what the snow room leaves over the ground).</summary>
            public double MostFillMetres { get; internal set; }
            /// <summary>Always 0: the carpet never cuts the ground.</summary>
            public double CutCubicMetres { get; internal set; }
            public double FillCubicMetres { get; internal set; }
            public int ClearedCells { get; internal set; }
            public List<LiftProblem> Problems { get; internal set; } = new List<LiftProblem>();
            public bool Ok => !LiftProblems.AnyBlocks(Problems);
            /// <summary>The carpet's foot at <paramref name="s"/>: along each station's pitch from its origin, along the joints
            /// between (straight within a module).</summary>
            public double BaseAt(double s)
            {
                var j = Joints;
                if (j.Count == 0 || s <= j[0].s) return BottomLevel + s * BottomPitch;
                if (s >= j[j.Count - 1].s) return TopLevel + (s - TopOriginS) * TopPitch;
                int lo = 0, hi = j.Count - 1;
                while (hi - lo > 1) { int m = (lo + hi) / 2; if (j[m].s <= s) lo = m; else hi = m; }
                double t = (s - j[lo].s) / (j[hi].s - j[lo].s);
                return j[lo].y + (j[hi].y - j[lo].y) * t;
            }
        }

        /// <summary>The modules a belt of <paramref name="belt"/> metres takes: 2 m ones, and a 1 m filler where a metre is left.</summary>
        public static List<double> ModulesFor(double belt)
        {
            var list = new List<double>();
            int twos = (int)Math.Floor(belt / ModuleMetres + 1e-9);
            for (int i = 0; i < twos; i++) list.Add(ModuleMetres);
            if (belt - twos * ModuleMetres >= FillerMetres - 1e-9) list.Add(FillerMetres);
            return list;
        }

        /// <summary>
        /// The bed for <paramref name="type"/> (a belt) from the bottom origin (<paramref name="bx"/>, <paramref name="by"/>)
        /// to the top (<paramref name="tx"/>, <paramref name="ty"/>), over <paramref name="current"/>: its profile, problems
        /// and earthwork (fill only).
        /// </summary>
        /// <param name="bottomLevel">The stored levels (save v2's, or set by hand): the least each station's origin stands at.
        /// Null: as low as the ground allows.</param>
        public static Result Build(LiftType type, EditedHeights current, double bx, double by, double tx, double ty,
                                   double? bottomLevel = null, double? topLevel = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            var r = Lay(type, current, bx, by, tx, ty, bottomLevel, topLevel);
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = length > 0 ? (tx - bx) / length : 0, uy = length > 0 ? (ty - by) / length : 1;
            Grade(type, current, bx, by, ux, uy, r);
            return r;
        }

        /// <summary>The bed's profile and problems alone, no earthwork (<see cref="Result.Edits"/> null): what the view stands
        /// the modules on, over whatever ground it's given (on the bed's own fill, the same bed).</summary>
        public static Result Lay(LiftType type, IHeightSource current, double bx, double by, double tx, double ty,
                                 double? bottomLevel = null, double? topLevel = null)
        {
            if (current == null) throw new ArgumentNullException(nameof(current));
            if (type == null || !type.Belt) throw new ArgumentException("Not a belt lift.", nameof(type));
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            double ux = length > 0 ? (tx - bx) / length : 0, uy = length > 0 ? (ty - by) / length : 1;
            var r = new Result { LengthMetres = length };
            double bB = type.Bottom.Behind, bA = type.Bottom.Ahead, tB = type.Top.Behind, tA = type.Top.Ahead;
            double belt = Math.Max(0, length - bA - tA);

            // The ground along the line, every 25 cm from the bottom plate's edge past the top's: its highest across the deck
            // and across a station (the room's floor) and at the centre line (for the fill report).
            double from = -bB, to = length + tB;
            int count = (int)Math.Ceiling((to - from) / Step) + 1;
            var deckHigh = new double[count];
            var stationHigh = new double[count];
            var centre = new double[count];
            double deck = type.DeckHalfWidthMetres, station = Math.Max(type.Bottom.HalfWidth, type.Top.HalfWidth);
            for (int i = 0; i < count; i++)
            {
                double s = from + i * Step;
                double At(double d) => current.At(bx + ux * s + uy * d, by + uy * s - ux * d);
                double c = At(0);
                centre[i] = c;
                deckHigh[i] = Math.Max(c, Math.Max(Math.Max(At(-deck), At(deck)), Math.Max(At(-deck / 2), At(deck / 2))));
                stationHigh[i] = Math.Max(deckHigh[i], Math.Max(At(-station), At(station)));
            }
            double Floor(double[] grid, double s)
            {
                double f = (s - from) / Step;
                int i = (int)Math.Floor(f);
                if (i < 0) return grid[0];
                if (i >= count - 1) return grid[count - 1];
                return grid[i] + (grid[i + 1] - grid[i]) * (f - i);
            }

            // Rounds: the modules that fit the run at the last round's angles, the profile over them, until they settle.
            var modules = ModulesFor(belt);
            double guess = Math.Atan2(Floor(centre, length) - Floor(centre, 0), Math.Max(1e-9, length));
            var angles = new double[modules.Count];
            for (int k = 0; k < angles.Length; k++) angles[k] = guess;
            double[] xs = null, ys = null;
            for (int round = 0; round < 8; round++)
            {
                Fit(modules, ref angles, belt);
                Solve(modules, angles, bB, bA, tA, tB, s => Floor(stationHigh, s), s => Floor(deckHigh, s), bottomLevel, topLevel,
                      out xs, out ys);
                double moved = 0;
                for (int k = 0; k < modules.Count; k++)
                {
                    double a = Math.Atan2(ys[k + 2] - ys[k + 1], xs[k + 2] - xs[k + 1]);
                    moved = Math.Max(moved, Math.Abs(a - angles[k]));
                    angles[k] = a;
                }
                if (moved < 1e-5 && Horizontal(modules, angles) <= belt + 1e-9) break;
            }
            if (Horizontal(modules, angles) > belt + 1e-9)
            {
                // Still a little long at the final angles: the last metre off, the profile again over what's left.
                Fit(modules, ref angles, belt);
                Solve(modules, angles, bB, bA, tA, tB, s => Floor(stationHigh, s), s => Floor(deckHigh, s), bottomLevel, topLevel,
                      out xs, out ys);
                for (int k = 0; k < modules.Count; k++) angles[k] = Math.Atan2(ys[k + 2] - ys[k + 1], xs[k + 2] - xs[k + 1]);
            }

            // The rigid pieces from the solved profile: the bottom station's pitch, the modules at their angles from its exit,
            // the top station on the last joint at its pitch.
            int n = modules.Count;
            double bottomPitch = Clamp((ys[1] - ys[0]) / (xs[1] - xs[0]), -EndGradeLimit, EndGradeLimit);
            double topPitch = Clamp((ys[n + 2] - ys[n + 1]) / (xs[n + 2] - xs[n + 1]), -EndGradeLimit, EndGradeLimit);
            r.BottomPitch = bottomPitch;
            r.TopPitch = topPitch;
            r.BottomLevel = ys[1] - bA * bottomPitch;
            r.Modules = modules;
            var joints = new List<(double s, double y)>(n + 1);
            double sj = bA, yj = ys[1];
            joints.Add((sj, yj));
            double steepest = 0;
            for (int k = 0; k < n; k++)
            {
                sj += modules[k] * Math.Cos(angles[k]);
                yj += modules[k] * Math.Sin(angles[k]);
                joints.Add((sj, yj));
                steepest = Math.Max(steepest, Math.Abs(Math.Tan(angles[k])));
            }
            r.Joints = joints;
            r.TopOriginS = sj + tA;
            r.TopLevel = yj + tA * topPitch;
            r.Grade = steepest;
            r.AverageGrade = Math.Abs(r.RiseMetres) / Math.Max(1e-9, r.TopOriginS);
            double most = 0;
            for (double s = -bB; s <= r.TopOriginS + tB + 1e-9; s += Step)
                most = Math.Max(most, r.BaseAt(s) - SnowRoomMetres - Floor(centre, s));
            r.MostFillMetres = most;

            // Limits (red) and steepness (amber).
            string Ft(double m) => (m / 0.3048).ToString("N0", CultureInfo.InvariantCulture) + " ft";
            string Pct(double g) => (g * 100).ToString("N0", CultureInfo.InvariantCulture) + " %";
            if (length < MinLengthMetres)
                r.Problems.Add(new LiftProblem("CB-01", $"Too short for a carpet: {Ft(length)}, {Ft(MinLengthMetres)} at least", -1));
            if (length > MaxLengthMetres)
                r.Problems.Add(new LiftProblem("CB-01", $"Too long for a carpet: {Ft(length)}, {Ft(MaxLengthMetres)} at most", -1));
            if (Math.Abs(r.RiseMetres) > RiseLimitMetres)
                r.Problems.Add(new LiftProblem("CB-02", $"Too much rise for a carpet: {Ft(Math.Abs(r.RiseMetres))}, {Ft(RiseLimitMetres)} at most", -1));
            if (r.Grade > GradeLimit + 1e-9 || r.AverageGrade > GradeLimit + 1e-9)
                r.Problems.Add(new LiftProblem("CB-04", $"Too steep for a carpet: {Pct(Math.Max(r.Grade, r.AverageGrade))}, {Pct(GradeLimit)} at most", -1));
            else if (r.Grade > SteepLocal + 1e-9 || r.AverageGrade > SteepAverage + 1e-9)
                r.Problems.Add(new LiftProblem("CB-04", $"Steep for a carpet: {Pct(r.Grade)} ({Pct(r.AverageGrade)} on average); beginners do best under 18 %", -1, blocks: false));
            if (most > DeepFillMetres + 1e-9)
                r.Problems.Add(new LiftProblem("CB-05", $"Rides high: up to {Ft(most)} of fill under it, where it can't bend down into a dip", -1, blocks: false));
            return r;
        }

        /// <summary>As many modules as fit <paramref name="belt"/> metres of run at <paramref name="angles"/>, no more than a
        /// metre short: a metre on or off at a time (the last 2 m module becomes the filler, or the filler goes).</summary>
        static void Fit(List<double> modules, ref double[] angles, double belt)
        {
            void Less()
            {
                int last = modules.Count - 1;
                if (modules[last] == FillerMetres) modules.RemoveAt(last);
                else { modules.RemoveAt(last); modules.Add(FillerMetres); modules.Sort((p, q) => q.CompareTo(p)); }
            }
            void More()
            {
                int last = modules.Count - 1;
                if (last >= 0 && modules[last] == FillerMetres) modules[last] = ModuleMetres;
                else modules.Add(FillerMetres);
                modules.Sort((p, q) => q.CompareTo(p));
            }
            for (int guard = 0; guard < 2000 && modules.Count > 0 && Horizontal(modules, angles) > belt + 1e-9; guard++)
            {
                Less();
                angles = Resize(angles, modules.Count);
            }
            for (int guard = 0; guard < 2000; guard++)
            {
                More();
                angles = Resize(angles, modules.Count);
                if (Horizontal(modules, angles) <= belt + 1e-9) continue;
                Less();
                angles = Resize(angles, modules.Count);
                break;
            }
        }

        /// <summary>
        /// The lowest profile over the pieces (the bottom station from its outer edge to its exit, the modules, the top station
        /// from its entry to its outer edge) that keeps the snow room over the ground under each, turns at most 1° a joint and
        /// pitches the stations within the end limit, the stations' origins no lower than the stored levels: the least area
        /// under it that meets them all (<see cref="Lowest"/>), so the same ground lays the same bed.
        /// </summary>
        static void Solve(List<double> modules, double[] angles, double bB, double bA, double tA, double tB,
                          Func<double, double> stationFloor, Func<double, double> deckFloor, double? bottomLevel, double? topLevel,
                          out double[] xs, out double[] ys)
        {
            int n = modules.Count, points = n + 3;
            xs = new double[points];
            ys = new double[points];
            xs[0] = -bB;
            xs[1] = bA;
            for (int k = 0; k < n; k++) xs[k + 2] = xs[k + 1] + modules[k] * Math.Cos(angles[k]);
            xs[n + 2] = xs[n + 1] + tA + tB;
            // Each piece's floor, sampled: (piece, t along it, the least height there).
            var samples = new List<(int seg, double t, double need)>();
            for (int seg = 0; seg < points - 1; seg++)
            {
                double x0 = xs[seg], x1 = xs[seg + 1], span = x1 - x0;
                bool isStation = seg == 0 || seg == points - 2;
                int steps = Math.Max(1, (int)Math.Ceiling(span / Step));
                for (int i = 0; i <= steps; i++)
                {
                    double t = (double)i / steps, s = x0 + span * t;
                    samples.Add((seg, t, (isStation ? stationFloor(s) : deckFloor(s)) + SnowRoomMetres));
                }
            }
            if (bottomLevel.HasValue) samples.Add((0, bB / (bB + bA), bottomLevel.Value));
            if (topLevel.HasValue) samples.Add((points - 2, tA / (tA + tB), topLevel.Value));
            // The constraints, each a row sum(c * y) >= rhs over at most three neighbouring points: the floors, each joint's
            // turn within 1° (as a change of slope, at the last round's angles), each station's pitch within the end limit.
            var dx = new double[points - 1];
            for (int seg = 0; seg < points - 1; seg++) dx[seg] = Math.Max(1e-6, xs[seg + 1] - xs[seg]);
            var rows = new List<Row>(samples.Count + 2 * points + 4);
            foreach (var (seg, t, need) in samples) rows.Add(new Row(seg, 1 - t, t, 0, need));
            for (int p = 1; p < points - 1; p++)
            {
                // The slope's change allowed at this joint: 1° at the pieces' present angles, a little inside it.
                double a0 = p - 1 == 0 ? 0 : (p - 2 < angles.Length ? angles[p - 2] : 0);
                double a1 = p - 1 < angles.Length ? angles[p - 1] : 0;
                double g = Math.Tan((a0 + a1) / 2);
                double k = BendPerJoint * (1 + g * g) * 0.995;
                double i0 = 1 / dx[p - 1], i1 = 1 / dx[p];
                // (y[p+1] - y[p]) / dx[p] - (y[p] - y[p-1]) / dx[p-1] within ±k.
                rows.Add(new Row(p - 1, -i0, i0 + i1, -i1, -k));
                rows.Add(new Row(p - 1, i0, -(i0 + i1), i1, -k));
            }
            foreach (int seg in new[] { 0, points - 2 })
            {
                double i = 1 / dx[seg];
                rows.Add(new Row(seg, i, -i, 0, -EndGradeLimit));   // (y1 - y0) / dx <= limit
                rows.Add(new Row(seg, -i, i, 0, -EndGradeLimit));   // and >= -limit
            }
            // No module steeper than the ground near it (its steepest within 6 m, a point over): the least area alone would
            // steepen the middle to save fill at the ends.
            for (int seg = 1; seg < points - 2; seg++)
            {
                double cap = 0;
                for (double at = xs[seg] - SlopeWindow; at <= xs[seg + 1] + SlopeWindow + 1e-9; at += Step)
                    cap = Math.Max(cap, Math.Abs(deckFloor(at + 0.5) - deckFloor(at - 0.5)));
                cap += SlopeAllowance;
                double i = 1 / dx[seg];
                rows.Add(new Row(seg, i, -i, 0, -cap));
                rows.Add(new Row(seg, -i, i, 0, -cap));
            }
            var weight = new double[points];
            for (int seg = 0; seg < points - 1; seg++) { weight[seg] += dx[seg] / 2; weight[seg + 1] += dx[seg] / 2; }
            // Start level, a metre over the highest floor: inside every row.
            double top = double.MinValue;
            foreach (var (_, _, need) in samples) top = Math.Max(top, need);
            for (int p = 0; p < points; p++) ys[p] = top + 1;
            Lowest(ys, weight, rows);
        }

        /// <summary>A constraint row: c0 * y[at] + c1 * y[at + 1] + c2 * y[at + 2] >= rhs.</summary>
        readonly struct Row
        {
            public readonly int At;
            public readonly double C0, C1, C2, Rhs;
            public Row(int at, double c0, double c1, double c2, double rhs) { At = at; C0 = c0; C1 = c1; C2 = c2; Rhs = rhs; }
            public double Slack(double[] y) => C0 * y[At] + C1 * y[At + 1] + (C2 != 0 ? C2 * y[At + 2] : 0) - Rhs;
            public double Along(double[] d) => C0 * d[At] + C1 * d[At + 1] + (C2 != 0 ? C2 * d[At + 2] : 0);
        }

        /// <summary>
        /// The lowest <paramref name="y"/> (least weighted sum) inside every row, from a start inside them all: a log-barrier
        /// interior point, its Newton steps solved banded (each row spans three neighbouring points, so the Hessian is
        /// pentadiagonal). A fixed schedule, so the same rows always give the same heights.
        /// </summary>
        static void Lowest(double[] y, double[] weight, List<Row> rows)
        {
            int n = y.Length;
            var grad = new double[n];
            var band = new double[n * 3];   // the Hessian's diagonal and its first two sub-diagonals, row by row
            var step = new double[n];
            var trial = new double[n];
            double Value(double[] at, double tau)
            {
                double v = 0;
                for (int i = 0; i < n; i++) v += tau * weight[i] * at[i];
                foreach (var r in rows)
                {
                    double sl = r.Slack(at);
                    if (sl <= 0) return double.PositiveInfinity;
                    v -= Math.Log(sl);
                }
                return v;
            }
            for (double tau = 1; tau < 1e9; tau *= 8)
            {
                for (int newton = 0; newton < 60; newton++)
                {
                    for (int i = 0; i < n; i++) grad[i] = tau * weight[i];
                    Array.Clear(band, 0, band.Length);
                    foreach (var r in rows)
                    {
                        double inv = 1 / r.Slack(y), inv2 = inv * inv;
                        int a = r.At;
                        grad[a] -= r.C0 * inv;
                        grad[a + 1] -= r.C1 * inv;
                        band[a * 3] += r.C0 * r.C0 * inv2;
                        band[(a + 1) * 3] += r.C1 * r.C1 * inv2;
                        band[(a + 1) * 3 + 1] += r.C1 * r.C0 * inv2;
                        if (r.C2 != 0)
                        {
                            grad[a + 2] -= r.C2 * inv;
                            band[(a + 2) * 3] += r.C2 * r.C2 * inv2;
                            band[(a + 2) * 3 + 1] += r.C2 * r.C1 * inv2;
                            band[(a + 2) * 3 + 2] += r.C2 * r.C0 * inv2;
                        }
                    }
                    for (int i = 0; i < n; i++) step[i] = -grad[i];
                    if (!SolveBanded(band, step, n)) break;
                    double decrement = 0;
                    for (int i = 0; i < n; i++) decrement -= grad[i] * step[i];
                    if (decrement / 2 < 1e-10) break;
                    // Backtracking: stay inside every row and go down.
                    double now = Value(y, tau), alpha = 1;
                    for (int ls = 0; ls < 60; ls++, alpha *= 0.5)
                    {
                        for (int i = 0; i < n; i++) trial[i] = y[i] + alpha * step[i];
                        if (Value(trial, tau) <= now - 0.25 * alpha * decrement) break;
                    }
                    if (Value(trial, tau) >= now) break;
                    Array.Copy(trial, y, n);
                }
                if (rows.Count / tau < 1e-7) break;
            }
        }

        /// <summary>Solves band * x = rhs in place (rhs becomes x) by banded Cholesky; false when it isn't positive.</summary>
        static bool SolveBanded(double[] band, double[] rhs, int n)
        {
            // band[i*3 + k] = H(i, i - k), k = 0, 1, 2; factor H = L L^T in place, L(i, i - k) at band[i*3 + k].
            for (int i = 0; i < n; i++)
            {
                for (int k = 2; k >= 0; k--)
                {
                    int j = i - k;
                    if (j < 0) continue;
                    double sum = band[i * 3 + k];
                    for (int m = Math.Max(0, i - 2); m < j; m++)
                        sum -= band[i * 3 + (i - m)] * band[j * 3 + (j - m)];
                    if (k == 0)
                    {
                        if (!(sum > 0)) return false;
                        band[i * 3] = Math.Sqrt(sum);
                    }
                    else band[i * 3 + k] = sum / band[j * 3];
                }
            }
            for (int i = 0; i < n; i++)
            {
                double sum = rhs[i];
                for (int k = 1; k <= 2 && i - k >= 0; k++) sum -= band[i * 3 + k] * rhs[i - k];
                rhs[i] = sum / band[i * 3];
            }
            for (int i = n - 1; i >= 0; i--)
            {
                double sum = rhs[i];
                for (int k = 1; k <= 2 && i + k < n; k++) sum -= band[(i + k) * 3 + k] * rhs[i + k];
                rhs[i] = sum / band[i * 3];
            }
            return true;
        }

        /// <summary>
        /// The earthwork, fill only: every lattice post near the carpet raised to the snow room under it where it's lower
        /// (the stations, the deck and its margin), with 1:1 faces, and the approaches to the room under the plates' tops
        /// with 1:10 out to the ground; ground already higher stays.
        /// </summary>
        static void Grade(LiftType type, EditedHeights current, double bx, double by, double ux, double uy, Result r)
        {
            double deck = type.DeckHalfWidthMetres, half = deck + MarginMetres, station = type.Bottom.HalfWidth;
            double topS = r.TopOriginS;
            double plateTop = type.PlateTopMetres;
            double bottomEdge = -type.Bottom.Behind, topEdge = topS + type.Top.Behind;   // each plate's outer edge
            var lattice = current.Lattice;
            Reach(type, bx, by, ux, uy, topS, out double minX, out double minY, out double maxX, out double maxY);
            int c0 = (int)Math.Floor(minX - lattice.West), c1 = (int)Math.Ceiling(maxX - lattice.West);
            int r0 = (int)Math.Floor(lattice.North - maxY), r1 = (int)Math.Ceiling(lattice.North - minY);
            var builders = new SortedDictionary<EditTileKey, TerrainEditTile.Builder>();
            TerrainEditTile.Builder For(int c, int rr)
            {
                var key = EditTileKey.Of(c, rr);
                if (!builders.TryGetValue(key, out var bd)) builders[key] = bd = current.Edits.Get(key).Edit();
                return bd;
            }
            double fill = 0;
            int cleared = 0;
            for (int rr = r0; rr <= r1; rr++)
                for (int c = c0; c <= c1; c++)
                {
                    double px = lattice.X(c) - bx, py = lattice.Y(rr) - by;
                    double s = px * ux + py * uy, d = Math.Abs(px * uy - py * ux);
                    double ground = current.AtPoint(c, rr);
                    double design = Design(s, d, ground, out bool corridor);
                    if (double.IsNaN(design)) continue;
                    if (design - ground < 1e-4) design = ground;
                    double source = current.Source.At(lattice.X(c), lattice.Y(rr));
                    // Fill only, never past the room: down to the whole millimetre, which the ground (the source plus whole
                    // millimetres) never sits above.
                    long mm = (long)Math.Floor((design - source) * 1000 + 1e-6);
                    mm = Math.Max(-TerrainEditTile.DeltaLimitMm, Math.Min(TerrainEditTile.DeltaLimitMm, mm));
                    double change = source + mm / 1000.0 - ground;
                    bool changed = change >= 1e-6;
                    if (!changed && !corridor) continue;
                    var key = EditTileKey.Of(c, rr);
                    var bd = For(c, rr);
                    if (changed)
                    {
                        bd.SetDeltaMm(c - key.FirstColumn, rr - key.FirstRow, (int)mm);
                        fill += change;
                    }
                    if (corridor || change > 0.05)
                    {
                        bd.SetCleared(c - key.FirstColumn, rr - key.FirstRow, true);
                        cleared++;
                    }
                }
            var tiles = new List<TerrainEditTile>(builders.Count);
            foreach (var bd in builders.Values) tiles.Add(bd.Build());
            r.Edits = current.Edits.With(tiles);
            r.CutCubicMetres = 0;
            r.FillCubicMetres = fill;
            r.ClearedCells = cleared;

            // The design height at (s along, d across): the room under the carpet (its stations, deck and margin), the room
            // under the plates' tops on the approaches; past them held within 1:1 (1:10 past an approach) of it. Never under
            // the ground. NaN where nothing reaches.
            double Design(double s, double d, double ground, out bool corridor)
            {
                corridor = false;
                double sideA = Math.Max(0, d - half);
                if (s <= bottomEdge)
                {
                    double past = bottomEdge - s;
                    double level = r.BaseAt(bottomEdge) + plateTop - SnowRoomMetres;
                    double off = Math.Sqrt(Math.Pow(Math.Max(0, past - ApproachFlat), 2) + sideA * sideA);
                    if (off > EaseReach) return double.NaN;
                    corridor = off <= 0;
                    return Math.Max(ground, level - off * Ease);
                }
                if (s >= topEdge)
                {
                    double past = s - topEdge;
                    double level = r.BaseAt(topEdge) + plateTop - SnowRoomMetres - Math.Min(past, RunOff) * RunOffFall;
                    double off = Math.Sqrt(Math.Pow(Math.Max(0, past - RunOff - ApproachFlat), 2) + sideA * sideA);
                    if (off > EaseReach) return double.NaN;
                    corridor = off <= 0;
                    return Math.Max(ground, level - off * Ease);
                }
                bool onStation = s < type.Bottom.Ahead || s > topS - type.Top.Ahead;
                double w = (onStation ? station : deck) + MarginMetres;
                double offSide = Math.Max(0, d - w);
                if (offSide > FaceReachMetres) return double.NaN;
                corridor = offSide <= 0;
                return Math.Max(ground, r.BaseAt(s) - SnowRoomMetres - offSide * FaceSlope);
            }
        }

        static double Horizontal(IReadOnlyList<double> modules, double[] angles)
        {
            double sum = 0;
            for (int k = 0; k < modules.Count; k++) sum += modules[k] * Math.Cos(k < angles.Length ? angles[k] : 0);
            return sum;
        }

        static double[] Resize(double[] angles, int count)
        {
            var next = new double[count];
            for (int k = 0; k < count; k++) next[k] = angles.Length == 0 ? 0 : angles[Math.Min(k, angles.Length - 1)];
            return next;
        }

        /// <summary>
        /// The box a carpet's earthwork reads and writes, in the lattice's frame (its approaches' ease, its faces), for a
        /// line <paramref name="length"/> long: what the earthwork fold reserves for it.
        /// </summary>
        public static void Reach(LiftType type, double bx, double by, double ux, double uy, double length,
                                 out double minX, out double minY, out double maxX, out double maxY)
        {
            double from = -type.Bottom.Behind - ApproachFlat - EaseReach, to = length + type.Top.Behind + RunOff + ApproachFlat + EaseReach;
            double wide = Math.Max(type.DeckHalfWidthMetres, type.Bottom.HalfWidth) + MarginMetres + Math.Max(EaseReach, FaceReachMetres);
            minX = minY = double.MaxValue;
            maxX = maxY = double.MinValue;
            foreach (double s in new[] { from, to })
                foreach (double d in new[] { -wide, wide })
                {
                    double x = bx + ux * s + uy * d, y = by + uy * s - ux * d;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                }
        }

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
