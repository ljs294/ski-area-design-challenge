using System;
using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// Task 4.26 BB26 (owner, 2026-11-24: "I want this to be a smoother curve of the line, not a clean break", "in reality
    /// this line would snake around and twist naturally", "one winding ribbon throughout"): a lift's line is one ribbon as
    /// wide as the chair, from the loader to its back, that never jumps. Its centre is planned a decision at a time as arcs
    /// whose curvature changes gradually (it eases into every bend over <see cref="EaseMetres"/>), so the line leaves the
    /// loader on a widening curve and folds back on itself in round hairpins, one fold beside the last.
    ///
    /// The ground each stretch stands on (its footprint, the line's width and a margin) must be open, in reach and nobody
    /// else's; the line's own ground counts only within the last few metres behind it, so it never overlaps itself. Every
    /// decision keeps a way on in hand (a hairpin or a few metres straight), so a fold never runs into a dead end. Boxed in,
    /// it narrows to single file along the shortest way to open ground and widens again there; with no open ground in
    /// reach it goes on over trees or steep ground. It never stands on a terminal, a lift's line or a run.
    ///
    /// Deterministic (no randomness but keyed hashes, fixed orders) and append-only: the centre only ever grows, and the
    /// places along it (<see cref="LayLanes"/>) never move.
    /// </summary>
    public sealed partial class LineLayout
    {
        /// <summary>The space between two folds of one line, metres (footprint to footprint, a little less).</summary>
        public const float FoldGapMetres = 1f;
        /// <summary>The most a bend may squeeze a line's inner lane: at most this share of its length (it never folds on itself).</summary>
        public const float MaxInnerBend = 0.7f;
        const float EaseMetres = 1f, DecideMetres = 1f, LookMetres = 3f, BandMetres = 36f;
        const float FileHalfMetres = 0.5f, FootMarginMetres = 0.1f, MinPassMetres = 1f, StrayRuns = 1f;
        const float FirstLookMetres = 60f, UnstickMetres = 30f, UnstickMinMetres = 6f, WalkTrialMetres = 8f;
        /// <summary>The footprint is checked (and taken) every this many samples (half a metre).</summary>
        const int DiscSamples = 5;
        const int WalkTargets = 48;
        /// <summary>A walk tries no spot this near one that failed (a pocket's every spot fails alike).</summary>
        const float WalkTriedMetres = 4f;
        const float SoftRetryMetres = 20f;
        /// <summary>
        /// Backtracking: places stand only this far behind the planned end (<see cref="LagMetres"/>), so a line that boxes
        /// itself in can take back up to that much and go another way; each try takes back <see cref="BackMetres"/> more,
        /// and the ground it gave up past its first <see cref="DeadAfterMetres"/> is a dead end it never enters again.
        /// </summary>
        const float LagMetres = 48f, BackMetres = 16f, DeadAfterMetres = 4f, SnapMetres = 2f;
        const int BacktracksPerGrow = 8, MaxBacktracks = 64;

        /// <summary>The tightest a line of <paramref name="width"/> bends (90° turns): its inner lane keeps 30% of its length.</summary>
        public static float BendRadius(float width) => Mathf.Max((width - ColumnMetres) * 0.5f / MaxInnerBend, 1f);

        /// <summary>A hairpin's radius: the fold after it stands beside the one before, <see cref="FoldGapMetres"/> apart.</summary>
        public static float MinTurnRadius(float width) => Mathf.Max((width + FoldGapMetres) * 0.5f, BendRadius(width));

        /// <summary>Whose a cell is, and at which sample of that line's centre it was taken.</summary>
        struct Claim { public int Lift, N; }

        readonly Dictionary<long, Claim> _claims = new Dictionary<long, Claim>();
        readonly Dictionary<long, Claim> _overlay = new Dictionary<long, Claim>();
        Ribbon[] _ribbons;

        /// <summary>A point of a line's centre, every <see cref="SampleMetres"/>: where, which way on (from the front), how
        /// sharply it bends, and whether the line is single file there.</summary>
        struct Sample { public Vector2 P, T; public float K; public bool File; }

        const byte PassMode = 0, TurnMode = 1;
        const byte SinkCheck = 0, SinkTrial = 1, SinkCommit = 2;

        /// <summary>The planner's state at the line's end (or a trial's).</summary>
        struct Pen
        {
            public Vector2 P;
            public float H, K;   // heading (radians, x toward z) and curvature (1/m, + turns left)
            public int N;        // sample index of P
            public byte Mode;
            public float TurnLeft, TurnK;
            public int TurnSign;
            /// <summary>At the turn's end a new frame starts, its passes stepping toward <see cref="ReframeB"/>.</summary>
            public bool Reframe;
            public Vector2 ReframeB;
            // The frame: passes run along A (PassDir), from a = 0 to L, stepping toward B; the pass's line is b = LineB.
            public Vector2 O, A, B;
            public float L, LineB, BandB, PassFrom, RunOn;
            public int PassDir, Band;
            public Vector2 Dir => new Vector2(Mathf.Cos(H), Mathf.Sin(H));
        }

        sealed class Ribbon
        {
            public int Lift, Seats;
            public float Width, Half, RH, R90, KMax, KRate, Rfp;
            public int Window;
            public uint Key;
            public Vector2 Load;
            public readonly List<Sample> Samples = new List<Sample>();
            public Pen Pen;
            public bool Done, Soft;
            public float SoftFrom;
            public int ThroatEnd, Reframes, Walks;
            // The places, as laid (row d's column c is entry d·Seats + c), and the sample each stands at.
            public readonly List<Vector3> Out = new List<Vector3>();
            public readonly List<Vector2> OutFaces = new List<Vector2>();
            public readonly List<int> OutN = new List<int>();
            public readonly List<bool> OutFile = new List<bool>();
            public readonly List<int> SectionRows = new List<int>();
            public readonly List<bool> SectionFile = new List<bool>();
            public int Depth, ThroatRows, PassedOver, WalkPlaces, SoftPlaces;
            public bool ThroatOpen = true;
            // The lanes, laid incrementally.
            public int LaidTo;
            public List<Vector2>[] Lanes, Faces;
            public List<bool>[] Single;
            public List<int>[] LaneN;
            public float[] Walked;
            public Vector2[] Prev;
            public bool InFile;
            public float FileWalk;
            public float Length => (Samples.Count - 1) * SampleMetres;
            // Backtracking: every cell taken (in order), the planner's state every few metres, the dead ends.
            public readonly List<long> ClaimKeys = new List<long>();
            public readonly List<int> ClaimNs = new List<int>();
            public readonly List<Snap> Snaps = new List<Snap>();
            public readonly HashSet<long> Dead = new HashSet<long>();
            public int LastSnapN = -1, Backtracks, BacktrackBudget;
            public string PassedAt;
        }

        /// <summary>The planner's state at one point of the line, to go back to.</summary>
        struct Snap { public Pen Pen; public bool Soft; public float SoftFrom; public int Walks, Reframes; }

        static Vector2 Left(Vector2 v) => new Vector2(-v.y, v.x);
        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static float Hash01(uint key, int n)
        {
            uint h = key * 0x9E3779B1u ^ (uint)n * 0x85EBCA77u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216f;
        }

        /// <summary>A band's pass length (footprint end to end): its hairpins and 6–12 m straight between, keyed per band.</summary>
        static float PassLength(Ribbon r, int band) => 2f * r.Rfp + 6f + 6f * Hash01(r.Key, band);

        // ================= building =================

        void BuildRibbons(IReadOnlyList<LiftInput> lifts)
        {
            _ribbons = new Ribbon[lifts.Count];
            for (int i = 0; i < lifts.Count; i++)
            {
                var l = lifts[i];
                if (!l.Built || l.Seats < 1) continue;
                _ribbons[i] = Throat(l, i);
            }
            // Each line's first heading, once every throat is in (a line never heads into another's throat).
            for (int i = 0; i < _ribbons.Length; i++) if (_ribbons[i] != null) ChooseFrame(_ribbons[i]);
            // Turns: everyone's first places, then blocks of the same size, round and round, up to the first places.
            bool any = true;
            while (any)
            {
                any = false;
                foreach (var r in _ribbons)
                {
                    if (r == null || r.Done) continue;
                    Extend(r, Math.Min(InitialSlots, r.Out.Count + FirstSlots));
                    if (!r.Done && r.Out.Count < InitialSlots) any = true;
                }
            }
            Rebuild();
        }

        /// <summary>
        /// A line's throat: straight back from the load point (under the loaded rope) until its footprint clears the
        /// terminal's back, whatever the ground (the load lane is always the way in). Its front rows stand square to the lift.
        /// </summary>
        Ribbon Throat(LiftInput l, int lift)
        {
            var up = l.Top - l.Bottom;
            var u = up.sqrMagnitude > 1e-6f ? up.normalized : Vector2.up;
            var back = -u;
            int seats = Math.Max(1, l.Seats);
            float width = seats * ColumnMetres;
            var r = new Ribbon
            {
                Lift = lift, Seats = seats, Width = width, Half = width * 0.5f + FootMarginMetres,
                R90 = BendRadius(width), RH = MinTurnRadius(width), Key = l.Key != 0 ? l.Key : (uint)(lift * 2654435761u + 1),
                Load = l.Bottom + new Vector2(u.y, -u.x) * l.LoadSide,
            };
            r.KMax = 1f / r.R90;
            r.KRate = r.KMax / EaseMetres;
            r.Rfp = r.RH + r.Half + EaseMetres * 0.5f;   // a hairpin's reach ahead: its radius, the footprint, the ease in
            r.Window = Mathf.CeilToInt((Mathf.PI * Mathf.Max(r.R90, r.Half) + 0.5f) / SampleMetres);
            float length = Mathf.Max(0, l.Behind) + TerminalMarginMetres + r.Half + 0.25f;
            int n = Mathf.CeilToInt(length / SampleMetres);
            for (int k = 0; k <= n; k++)
            {
                var p = r.Load + back * (k * SampleMetres);
                r.Samples.Add(new Sample { P = p, T = back });
                if (k % DiscSamples == 0 || k == n) ClaimDisc(r, p, r.Half, k, null);
            }
            r.ThroatEnd = n;
            r.Pen = new Pen { P = r.Samples[n].P, H = Mathf.Atan2(back.y, back.x), N = n, Mode = PassMode };
            MakeFrame(r, ref r.Pen, back, new Vector2(back.y, -back.x));
            return r;
        }

        /// <summary>
        /// The line's first heading: on straight back from the throat with its folds stepping right or left, or turned a
        /// quarter either way with its folds stepping back. The one that lays the most of the next 60 m (tried, laying
        /// nobody) wins; ties keep the earlier.
        /// </summary>
        void ChooseFrame(Ribbon r)
        {
            var p = r.Pen;
            var h = p.Dir;
            var options = new Pen[4];
            options[0] = p; MakeFrame(r, ref options[0], h, -Left(h));
            options[1] = p; MakeFrame(r, ref options[1], h, Left(h));
            options[2] = p; StartQuarter(r, ref options[2], 1, h);
            options[3] = p; StartQuarter(r, ref options[3], -1, h);
            float best = -1;
            foreach (var o in options)
            {
                float m = TrialMetres(r, o, FirstLookMetres);
                if (m > best + 1e-3f) { best = m; r.Pen = o; }
            }
        }

        /// <summary>Lays lift line <paramref name="r"/> on until it has <paramref name="target"/> places, or can't go on.</summary>
        void Extend(Ribbon r, int target)
        {
            target = Math.Min(target, MaxSlots);
            r.BacktrackBudget = BacktracksPerGrow;
            for (int guard = 0; r.Out.Count < target && !r.Done && guard < 20000; guard++)
            {
                Plan(r, 4f);
                LayLanes(r);
            }
            LayLanes(r);
        }

        /// <summary>
        /// Plans <paramref name="metres"/> more of the line's centre. Stuck, it tries another way on (<see cref="Unstick"/>),
        /// then single file to open ground (<see cref="Walk"/>), then the same over trees or steep ground; else it's done.
        /// </summary>
        void Plan(Ribbon r, float metres)
        {
            float goal = r.Length + metres;
            int fails = 0;
            while (!r.Done && r.Length < goal)
            {
                if (r.Pen.N >= r.LastSnapN + Mathf.RoundToInt(SnapMetres / SampleMetres))
                {
                    r.Snaps.Add(new Snap { Pen = r.Pen, Soft = r.Soft, SoftFrom = r.SoftFrom, Walks = r.Walks, Reframes = r.Reframes });
                    r.LastSnapN = r.Pen.N;
                    while (r.Snaps.Count > 2 && r.Snaps[1].Pen.N < r.LaidTo) r.Snaps.RemoveAt(0);
                }
                if (Advance(r, ref r.Pen, SinkCommit, null)) { fails = 0; continue; }
                if (fails++ < 3 && Unstick(r)) continue;
                if (Guide(r, r.Soft) || Walk(r, r.Soft, true) || Walk(r, r.Soft, false)) { fails = 0; continue; }
                // Boxed in (by its own folds, often): back out and go another way, before trees and before giving up.
                if (Backtrack(r)) { fails = 0; continue; }
                // Over trees now: the dead ends found on open ground alone may lead on.
                if (!r.Soft) { r.Soft = true; r.SoftFrom = r.Length; r.Dead.Clear(); fails = 0; continue; }
                r.Done = true;
            }
            // Over trees or steep ground a while: try open ground again (a walk finds it if it's near).
            if (r.Soft && r.Length - r.SoftFrom > SoftRetryMetres) r.Soft = false;
        }

        /// <summary>
        /// Takes back the line's planned end (nobody stands there yet) to its state <see cref="BackMetres"/> or more
        /// back, giving up its ground; the ground past the first <see cref="DeadAfterMetres"/> becomes a dead end for
        /// this line. On open ground only, it takes back no stretch laid over trees. False when there is nothing left to
        /// take back (or this grow's tries are spent).
        /// </summary>
        bool Backtrack(Ribbon r)
        {
            if (r.BacktrackBudget <= 0 || r.Backtracks >= MaxBacktracks) return false;
            int floor = Math.Max(r.LaidTo, r.ThroatEnd);
            int want = r.Pen.N - Mathf.RoundToInt(BackMetres / SampleMetres);
            int pick = -1;
            for (int s = r.Snaps.Count - 1; s >= 0; s--)
            {
                int n = r.Snaps[s].Pen.N;
                if (n < floor) break;
                // On open ground only, never back over a stretch laid over trees: trees take it on from here instead.
                if (!r.Soft && r.Snaps[s].Soft) break;
                if (n <= want) { pick = s; break; }
            }
            if (pick < 0) return false;
            r.BacktrackBudget--;
            r.Backtracks++;
            var snap = r.Snaps[pick];
            int cut = snap.Pen.N, dead = cut + Mathf.RoundToInt(DeadAfterMetres / SampleMetres);
            for (int k = r.ClaimNs.Count - 1; k >= 0 && r.ClaimNs[k] > cut; k--)
            {
                long key = r.ClaimKeys[k];
                _claims.Remove(key);
                if (r.ClaimNs[k] > dead) r.Dead.Add(key);
                r.ClaimKeys.RemoveAt(k);
                r.ClaimNs.RemoveAt(k);
            }
            r.Samples.RemoveRange(cut + 1, r.Samples.Count - cut - 1);
            r.Pen = snap.Pen;
            r.Soft = snap.Soft;
            r.SoftFrom = snap.SoftFrom;
            r.Walks = snap.Walks;
            r.Reframes = snap.Reframes;
            r.Snaps.RemoveRange(pick + 1, r.Snaps.Count - pick - 1);
            r.LastSnapN = cut;
            return true;
        }

        // ================= the planner =================

        void MakeFrame(Ribbon r, ref Pen p, Vector2 a, Vector2 b)
        {
            p.A = a.normalized;
            var bb = b - p.A * Vector2.Dot(b, p.A);
            p.B = bb.sqrMagnitude > 1e-6f ? bb.normalized : Left(p.A);
            p.O = p.P - p.A * r.Rfp;
            p.LineB = 0; p.BandB = 0; p.PassDir = 1; p.RunOn = 0;
            p.L = PassLength(r, p.Band);
            p.PassFrom = p.N * SampleMetres;
            p.Mode = PassMode;
            p.Reframe = false;
        }

        static void StartHairpin(Ribbon r, ref Pen p)
        {
            p.Mode = TurnMode;
            p.TurnLeft = Mathf.PI;
            p.TurnK = 1f / r.RH;
            p.TurnSign = Cross(p.Dir, p.B) >= 0 ? 1 : -1;
            p.Reframe = false;
        }

        /// <summary>A quarter turn (<paramref name="sign"/> +1 left), then a new frame stepping toward <paramref name="b"/>.</summary>
        static void StartQuarter(Ribbon r, ref Pen p, int sign, Vector2 b)
        {
            p.Mode = TurnMode;
            p.TurnLeft = Mathf.PI * 0.5f;
            p.TurnK = r.KMax;
            p.TurnSign = sign;
            p.Reframe = true;
            p.ReframeB = b;
        }

        void EndTurn(Ribbon r, ref Pen p)
        {
            p.Mode = PassMode;
            if (p.Reframe)
            {
                p.Band++;
                MakeFrame(r, ref p, p.Dir, p.ReframeB);
                return;
            }
            p.PassDir = -p.PassDir;
            p.LineB = Vector2.Dot(p.P - p.O, p.B);
            p.PassFrom = p.N * SampleMetres;
            p.RunOn = 0;
        }

        /// <summary>One sample on: the curvature moves toward <paramref name="target"/> (at most the ease rate), then an arc.</summary>
        static void Step(Ribbon r, ref Pen p, float target)
        {
            p.K = Mathf.MoveTowards(p.K, target, r.KRate * SampleMetres);
            float mid = p.H + p.K * SampleMetres * 0.5f;
            p.P += new Vector2(Mathf.Cos(mid), Mathf.Sin(mid)) * SampleMetres;
            p.H += p.K * SampleMetres;
            p.N++;
        }

        /// <summary>In a turn, the curvature wanted now: the turn's, until what is left is what easing out takes.</summary>
        static float TurnTarget(Ribbon r, ref Pen p)
        {
            float k = Mathf.Abs(p.K);
            float easeOut = k * k / (2f * r.KRate);
            return p.TurnLeft <= easeOut + 1e-4f ? 0f : p.TurnSign * p.TurnK;
        }

        /// <summary>
        /// Moves <paramref name="p"/> on <paramref name="metres"/> bending toward curvature <paramref name="k"/> (or as
        /// its turn goes, stopping at the turn's end). check: whether the footprint fits; trial:
        /// also takes it in the trial's overlay (call only after a check); commit: lays it (unchecked).
        /// </summary>
        bool Sweep(Ribbon r, ref Pen p, float k, float metres, byte sink, Dictionary<long, Claim> overlay)
        {
            int steps = Mathf.Max(1, Mathf.RoundToInt(metres / SampleMetres));
            for (int s = 0; s < steps; s++)
            {
                bool turning = p.Mode == TurnMode;
                float target = turning ? TurnTarget(r, ref p) : k;
                Step(r, ref p, target);
                if (turning) p.TurnLeft -= p.K * p.TurnSign * SampleMetres;
                bool disc = p.N % DiscSamples == 0;
                if (sink == SinkCommit)
                {
                    r.Samples.Add(new Sample { P = p.P, T = p.Dir, K = p.K });
                    if (disc) ClaimDisc(r, p.P, r.Half, p.N, null);
                }
                else if (disc)
                {
                    if (sink == SinkCheck && !DiscOk(r, p.P, r.Half, p.N, r.Soft, overlay)) return false;
                    if (sink == SinkTrial) ClaimDisc(r, p.P, r.Half, p.N, overlay);
                }
                if (turning && target == 0 && p.K == 0) { EndTurn(r, ref p); return true; }
            }
            return true;
        }

        /// <summary>Checks, then moves <paramref name="p"/> on (laying or trying it): false, and nothing moved, if it doesn't fit.</summary>
        bool Go(Ribbon r, ref Pen p, float k, float metres, byte sink, Dictionary<long, Claim> overlay)
        {
            var q = p;
            if (!Sweep(r, ref q, k, metres, SinkCheck, overlay)) return false;
            if (sink == SinkCheck) { p = q; return true; }
            Sweep(r, ref p, k, metres, sink, overlay);
            return true;
        }

        /// <summary>Whether a hairpin toward the frame's side fits from <paramref name="p"/>, and 2 m on after it.</summary>
        bool HairpinFits(Ribbon r, Pen p, Dictionary<long, Claim> overlay)
        {
            StartHairpin(r, ref p);
            if (!Sweep(r, ref p, 0, 4f * Mathf.PI * r.RH + 2f, SinkCheck, overlay) || p.Mode == TurnMode) return false;
            return Sweep(r, ref p, 0, 2f, SinkCheck, overlay);
        }

        /// <summary>Whether a quarter turn (<paramref name="sign"/> +1 left) fits from <paramref name="p"/>, and 2 m on after it.</summary>
        bool QuarterFits(Ribbon r, Pen p, int sign, Dictionary<long, Claim> overlay)
        {
            StartQuarter(r, ref p, sign, p.Dir);
            if (!Sweep(r, ref p, 0, 2f * Mathf.PI * r.R90 + 2f, SinkCheck, overlay) || p.Mode == TurnMode) return false;
            return Sweep(r, ref p, 0, 2f, SinkCheck, overlay);
        }

        /// <summary>
        /// A way on in hand from <paramref name="p"/>: a hairpin, a quarter turn either way, or (in a corridor, where
        /// neither fits) 6 m straight on.
        /// </summary>
        bool Escape(Ribbon r, Pen p, Dictionary<long, Claim> overlay, out bool hairpin)
        {
            hairpin = HairpinFits(r, p, overlay);
            if (hairpin || QuarterFits(r, p, 1, overlay) || QuarterFits(r, p, -1, overlay)) return true;
            var q = p;
            return Sweep(r, ref q, 0, 6f, SinkCheck, overlay);
        }

        readonly float[] _cands = new float[8];

        /// <summary>
        /// One decision (about <see cref="DecideMetres"/> of centre): in a turn, on round it; on a pass, toward the pass's
        /// line, its end turning in a hairpin onto the next pass, bending round whatever is in the way. Returns false (and
        /// <paramref name="p"/> is as it was) when no way on fits.
        /// </summary>
        bool Advance(Ribbon r, ref Pen p, byte sink, Dictionary<long, Claim> overlay)
        {
            if (p.Mode == TurnMode) return Go(r, ref p, 0, DecideMetres, sink, overlay);
            float a = Vector2.Dot(p.P - p.O, p.A);
            bool far = p.PassDir > 0 ? a >= p.L - r.Rfp : a <= r.Rfp;
            // A full band: on past its far end into the next band, its folds stepping back the other way.
            if (far && p.PassDir > 0 && Mathf.Abs(p.LineB - p.BandB) >= BandMetres - 2f * r.RH)
            {
                p.O += p.A * (p.L + 2f * FoldGapMetres);
                p.B = -p.B; p.LineB = -p.LineB; p.BandB = p.LineB;
                p.Band++;
                p.L = PassLength(r, p.Band);
                a = Vector2.Dot(p.P - p.O, p.A);
                far = false;
            }
            bool runOn = false;
            if (far)
            {
                bool hairpin = HairpinFits(r, p, overlay);
                if (hairpin && p.RunOn < 2f)
                {
                    var q = p;
                    StartHairpin(r, ref q);
                    if (Advance(r, ref q, sink, overlay)) { p = q; return true; }
                    return false;
                }
                if (hairpin)
                {
                    // It ran on past the pass's end (a corridor with no room to fold): the pass starts afresh here.
                    p.O += p.A * (p.PassDir > 0 ? a - r.Rfp : a - (p.L - r.Rfp));
                    p.RunOn = 0;
                    p.PassFrom = p.N * SampleMetres;
                    a = Vector2.Dot(p.P - p.O, p.A);
                }
                else runOn = true;
            }
            // Toward a point a little ahead on the pass's line (pure pursuit), else the nearest bend that fits.
            var dir = p.Dir;
            var target = p.O + p.A * (a + p.PassDir * LookMetres) + p.B * p.LineB;
            var to = target - p.P;
            float dist = Mathf.Max(to.magnitude, 0.5f);
            float alpha = Mathf.Atan2(Cross(dir, to), Vector2.Dot(dir, to));
            float kpp = Mathf.Clamp(2f * Mathf.Sin(alpha) / dist, -r.KMax, r.KMax);
            int n = Candidates(r, kpp);
            float strayK = float.NaN;
            bool long_ = p.N * SampleMetres - p.PassFrom >= MinPassMetres;
            for (int c = 0; c < n; c++)
            {
                float k = _cands[c];
                var q = p;
                if (!Sweep(r, ref q, k, DecideMetres, SinkCheck, overlay)) continue;
                if (!Escape(r, q, overlay, out bool hairpinThen)) continue;
                if (Mathf.Abs(Vector2.Dot(q.P - q.O, q.B) - q.LineB) > r.RH * StrayRuns)
                {
                    if (float.IsNaN(strayK)) strayK = k;
                    continue;
                }
                // A hairpin in hand now but not a step on: fold now (the pass ends here), not into a corner.
                if (!hairpinThen && !runOn && long_ && HairpinFits(r, p, overlay))
                {
                    var t = p;
                    StartHairpin(r, ref t);
                    if (Advance(r, ref t, sink, overlay)) { p = t; return true; }
                }
                Go(r, ref p, k, DecideMetres, sink, overlay);
                if (runOn) p.RunOn += DecideMetres;
                return true;
            }
            // Blocked ahead: fold here (the pass is cut short) rather than stray off the pass's line.
            if (!runOn && long_ && HairpinFits(r, p, overlay))
            {
                var q = p;
                StartHairpin(r, ref q);
                if (Advance(r, ref q, sink, overlay)) { p = q; return true; }
            }
            if (!float.IsNaN(strayK))
            {
                Go(r, ref p, strayK, DecideMetres, sink, overlay);
                if (runOn) p.RunOn += DecideMetres;
                return true;
            }
            return false;
        }

        /// <summary>The bends to try, nearest the one wanted first (a fixed order, so ties are stable).</summary>
        int Candidates(Ribbon r, float want)
        {
            int n = 0;
            _cands[n++] = want;
            foreach (float f in new[] { 0f, 1f / 3f, -1f / 3f, 2f / 3f, -2f / 3f, 1f, -1f })
            {
                float k = f * r.KMax;
                bool dup = false;
                for (int j = 0; j < n; j++) if (Mathf.Abs(_cands[j] - k) < 1e-4f) dup = true;
                if (!dup) _cands[n++] = k;
            }
            // Insertion sort by distance from the wanted bend (the wanted one stays first).
            for (int i = 2; i < n; i++)
            {
                float v = _cands[i];
                int j = i - 1;
                while (j >= 1 && Mathf.Abs(_cands[j] - want) > Mathf.Abs(v - want)) { _cands[j + 1] = _cands[j]; j--; }
                _cands[j + 1] = v;
            }
            return n;
        }

        /// <summary>How far the planner gets from <paramref name="start"/> in <paramref name="metres"/>, laying nobody.</summary>
        float TrialMetres(Ribbon r, Pen start, float metres)
        {
            _overlay.Clear();
            var p = start;
            int goal = start.N + Mathf.RoundToInt(metres / SampleMetres);
            for (int guard = 0; p.N < goal && guard < 4 * metres; guard++)
                if (!Advance(r, ref p, SinkTrial, _overlay)) break;
            _overlay.Clear();
            return (p.N - start.N) * SampleMetres;
        }

        /// <summary>
        /// Stuck: another way on from here, the one that gets furthest (at least <see cref="UnstickMinMetres"/>) in a trial:
        /// a hairpin the other way, a quarter turn either way (its folds stepping on or back), or a fresh frame straight on.
        /// </summary>
        bool Unstick(Ribbon r)
        {
            var p = r.Pen;
            var h = p.Dir;
            var options = new List<Pen>(8);
            if (p.Mode == PassMode)
            {
                var o = p;
                o.B = -o.B; o.LineB = -o.LineB; o.BandB = -o.BandB;
                StartHairpin(r, ref o);
                options.Add(o);
            }
            foreach (int sign in new[] { 1, -1 })
                foreach (float bs in new[] { 1f, -1f })
                {
                    var o = p;
                    o.Mode = PassMode;
                    StartQuarter(r, ref o, sign, h * bs);
                    options.Add(o);
                }
            foreach (float bs in new[] { 1f, -1f })
            {
                var o = p;
                o.Band++;
                MakeFrame(r, ref o, h, Left(h) * bs);
                options.Add(o);
            }
            float best = UnstickMinMetres - 1e-3f;
            bool found = false;
            foreach (var o in options)
            {
                float m = TrialMetres(r, o, UnstickMetres);
                if (m > best) { best = m; r.Pen = o; found = true; }
            }
            if (found) r.Reframes++;
            return found;
        }

        // ================= full width through a gap =================

        /// <summary>
        /// Boxed in: the shortest way over cells where the whole ribbon fits (its footprint clear round each) to ground
        /// where it goes on for <see cref="WalkTrialMetres"/>, followed full width (a curvature-limited pursuit of the way,
        /// tried first, then laid), so the line winds through the gap rather than narrowing to single file.
        /// </summary>
        bool Guide(Ribbon r, bool soft)
        {
            var tail = r.Pen.P;
            var ahead = r.Pen.Dir;
            float reach = MaxReachMetres * MaxReachMetres;
            int n0 = r.Pen.N;
            var parent = new Dictionary<long, long>();
            var depth = new Dictionary<long, int>();
            var queue = new Queue<(int, int)>();
            bool Fits(int i, int j, int dep) => DiscOk(r, Grid.Centre(i, j), r.Half, n0 + 5 + dep * 5, soft, null);
            float r0 = r.Half + 1f;
            int rc = Mathf.CeilToInt(r0 / CellMetres), ti = Grid.Cell(tail.x), tj = Grid.Cell(tail.y);
            for (int dj = -rc; dj <= rc; dj++)
                for (int di = -rc; di <= rc; di++)
                {
                    int i = ti + di, j = tj + dj;
                    var d = Grid.Centre(i, j) - tail;
                    if (d.magnitude > r0 || Vector2.Dot(d, ahead) < 0.25f || !Fits(i, j, 0)) continue;
                    long k = Key(i, j);
                    parent[k] = long.MinValue; depth[k] = 0;
                    queue.Enqueue((i, j));
                }
            int maxDepth = Mathf.CeilToInt(MaxWalkMetres / CellMetres), visited = 0, tried = 0;
            var failed = new List<Vector2>();
            while (queue.Count > 0 && visited < WalkSearchCells / 2 && tried < WalkTargets)
            {
                var (i, j) = queue.Dequeue();
                visited++;
                long key = Key(i, j);
                int dep = depth[key];
                var c = Grid.Centre(i, j);
                bool near = (c - tail).sqrMagnitude < 25f;
                foreach (var f in failed) if ((f - c).sqrMagnitude < WalkTriedMetres * WalkTriedMetres) { near = true; break; }
                if (!near && dep >= 8 && (dep & 1) == 0 && DiscOk(r, c, r.Half, int.MaxValue / 2, soft, null))
                {
                    tried++;
                    var cells = new List<Vector2>();
                    for (long at = key; at != long.MinValue; at = parent[at]) cells.Add(Grid.Centre((int)(at >> 32), (int)(uint)at));
                    cells.Reverse();
                    var path = new List<Vector2> { tail };
                    path.AddRange(cells);
                    path = Chaikin(Chaikin(path));
                    if (Track(r, path, soft)) { r.Walks++; return true; }
                    failed.Add(c);
                }
                if (dep >= maxDepth) continue;
                for (int nb = 0; nb < 8; nb++)
                {
                    int ni = i + Ni[nb], nj = j + Nj[nb];
                    long nk = Key(ni, nj);
                    if (parent.ContainsKey(nk)) continue;
                    if ((Grid.Centre(ni, nj) - r.Load).sqrMagnitude > reach || !Fits(ni, nj, dep + 1)) continue;
                    parent[nk] = key; depth[nk] = dep + 1;
                    queue.Enqueue((ni, nj));
                }
            }
            return false;
        }

        readonly List<float> _track = new List<float>();

        /// <summary>
        /// Follows <paramref name="path"/> full width from the line's end (pure pursuit 3 m ahead, the nearest bend that
        /// fits), then needs <see cref="WalkTrialMetres"/> on from its end; all tried first, then laid. False: nothing laid.
        /// </summary>
        bool Track(Ribbon r, List<Vector2> path, bool soft)
        {
            bool wasSoft = r.Soft;
            r.Soft = soft;
            _overlay.Clear();
            var p = r.Pen;
            p.Mode = PassMode;
            _track.Clear();
            int idx = 0;
            var end = path[path.Count - 1];
            bool ok = false;
            for (int guard = 0; guard < 4 * MaxWalkMetres; guard++)
            {
                if ((p.P - end).magnitude < 1.5f) { ok = true; break; }
                while (idx < path.Count - 1 && (path[idx] - p.P).magnitude < LookMetres) idx++;
                var to = path[idx] - p.P;
                var dir = p.Dir;
                float alpha = Mathf.Atan2(Cross(dir, to), Vector2.Dot(dir, to));
                float kpp = Mathf.Clamp(2f * Mathf.Sin(alpha) / Mathf.Max(to.magnitude, 0.5f), -r.KMax, r.KMax);
                int n = Candidates(r, kpp);
                bool moved = false;
                for (int c = 0; c < n && !moved; c++)
                {
                    // Only bends near the one wanted: never away from the way.
                    if (c > 0 && Mathf.Abs(_cands[c] - kpp) > r.KMax) continue;
                    var q = p;
                    if (!Sweep(r, ref q, _cands[c], DecideMetres, SinkCheck, _overlay)) continue;
                    Sweep(r, ref p, _cands[c], DecideMetres, SinkTrial, _overlay);
                    _track.Add(_cands[c]);
                    moved = true;
                }
                if (!moved) break;
            }
            Pen best = default;
            if (ok)
            {
                // The ribbon must go on from there: a fresh frame straight on, its folds stepping either way.
                ok = false;
                float bestM = WalkTrialMetres - 1e-3f;
                var h = p.Dir;
                foreach (float bs in new[] { 1f, -1f })
                {
                    var o = p;
                    o.Band++;
                    MakeFrame(r, ref o, h, Left(h) * bs);
                    var keep = new Dictionary<long, Claim>(_overlay);
                    float m = TrialFrom(r, o, WalkTrialMetres, keep);
                    if (m > bestM) { bestM = m; best = o; ok = true; }
                }
            }
            _overlay.Clear();
            r.Soft = wasSoft;
            if (!ok) return false;
            // Lay it as tried.
            r.Soft = soft;
            var pen = r.Pen;
            pen.Mode = PassMode;
            foreach (float k in _track) Sweep(r, ref pen, k, DecideMetres, SinkCommit, null);
            pen.Band = best.Band;
            MakeFrame(r, ref pen, pen.Dir, best.B);
            r.Pen = pen;
            if (soft) r.SoftFrom = r.Length;
            return true;
        }

        /// <summary>As <see cref="TrialMetres"/>, on top of the claims a trial so far has made (<paramref name="overlay"/>).</summary>
        float TrialFrom(Ribbon r, Pen start, float metres, Dictionary<long, Claim> overlay)
        {
            var p = start;
            int goal = start.N + Mathf.RoundToInt(metres / SampleMetres);
            for (int guard = 0; p.N < goal && guard < 4 * metres; guard++)
                if (!Advance(r, ref p, SinkTrial, overlay)) break;
            return (p.N - start.N) * SampleMetres;
        }

        // ================= single file =================

        /// <summary>
        /// Boxed in: the shortest way over open cells (8-connected, at most <see cref="MaxWalkMetres"/>, half a metre off
        /// every line's ground when <paramref name="offLines"/>) to ground where the ribbon goes on full width for <see cref="WalkTrialMetres"/>, laid single
        /// file; the ribbon widens again there. <paramref name="soft"/>: over trees or steep ground too.
        /// </summary>
        bool Walk(Ribbon r, bool soft, bool offLines)
        {
            var g = _grids[r.Lift];
            var tail = r.Pen.P;
            float reach = MaxReachMetres * MaxReachMetres;
            bool Ground(int i, int j)
            {
                if (_claims.ContainsKey(Key(i, j)) || r.Dead.Contains(Key(i, j))) return false;
                byte st = g.State(i, j);
                return st == Open || (soft && st == Soft);
            }
            bool NearALine(int i, int j)
            {
                for (int n = 0; n < 4; n++) if (_claims.ContainsKey(Key(i + Ni[n], j + Nj[n]))) return true;
                return false;
            }
            var parent = new Dictionary<long, long>();
            var depth = new Dictionary<long, int>();
            var queue = new Queue<(int, int)>();
            float r0 = r.Half + 1f, leave = r0 + CellMetres;
            int rc = Mathf.CeilToInt(r0 / CellMetres);
            int ti = Grid.Cell(tail.x), tj = Grid.Cell(tail.y);
            var ahead = r.Pen.Dir;
            // Off the line's end ahead of it first (a way off to the side would cross its own last rows), else anywhere.
            for (int pass = 0; pass < 2 && queue.Count == 0; pass++)
                for (int dj = -rc; dj <= rc; dj++)
                    for (int di = -rc; di <= rc; di++)
                    {
                        int i = ti + di, j = tj + dj;
                        var d = Grid.Centre(i, j) - tail;
                        if (d.magnitude > r0 || !Ground(i, j) || (pass == 0 && Vector2.Dot(d, ahead) < 0)) continue;
                        long k = Key(i, j);
                        if (parent.ContainsKey(k)) continue;
                        parent[k] = long.MinValue; depth[k] = 0;
                        queue.Enqueue((i, j));
                    }
            int maxDepth = Mathf.CeilToInt(MaxWalkMetres / CellMetres), visited = 0, tried = 0;
            var far = int.MaxValue / 2;
            var failed = new List<Vector2>();
            while (queue.Count > 0 && visited < WalkSearchCells && tried < WalkTargets)
            {
                var (i, j) = queue.Dequeue();
                visited++;
                long key = Key(i, j);
                int dep = depth[key];
                var c = Grid.Centre(i, j);
                bool near = false;
                foreach (var f in failed) if ((f - c).sqrMagnitude < WalkTriedMetres * WalkTriedMetres) { near = true; break; }
                if (!near && dep >= 4 && (dep & 1) == 0 && DiscOk(r, c, r.Half, far, soft, null))
                {
                    tried++;
                    // The way it heads there: from a few cells back along the path.
                    long back = key;
                    for (int s = 0; s < 4 && parent[back] != long.MinValue; s++) back = parent[back];
                    var from = Grid.Centre((int)(back >> 32), (int)(uint)back);
                    var h = (c - from).sqrMagnitude > 1e-4f ? (c - from).normalized : (c - tail).normalized;
                    var pen = new Pen { P = c, H = Mathf.Atan2(h.y, h.x), N = r.Pen.N + dep * 7, Band = r.Pen.Band + 1 };
                    bool wasSoft = r.Soft;
                    r.Soft = soft;
                    Pen best = default;
                    float bestM = WalkTrialMetres - 1e-3f;
                    bool ok = false;
                    foreach (float bs in new[] { 1f, -1f })
                    {
                        var o = pen;
                        MakeFrame(r, ref o, h, Left(h) * bs);
                        float m = TrialMetres(r, o, WalkTrialMetres);
                        if (m > bestM) { bestM = m; best = o; ok = true; }
                    }
                    r.Soft = wasSoft;
                    if (ok)
                    {
                        var path = new List<Vector2> { tail };
                        var cells = new List<Vector2>();
                        for (long at = key; at != long.MinValue; at = parent[at]) cells.Add(Grid.Centre((int)(at >> 32), (int)(uint)at));
                        cells.Reverse();
                        path.AddRange(cells);
                        LayFile(r, Chaikin(Chaikin(path)));
                        best.P = r.Samples[r.Samples.Count - 1].P;
                        best.N = r.Samples.Count - 1;
                        MakeFrame(r, ref best, h, best.B);
                        r.Pen = best;
                        r.Soft = soft;
                        r.Walks++;
                        return true;
                    }
                    failed.Add(c);
                }
                if (dep >= maxDepth) continue;
                for (int n = 0; n < 8; n++)
                {
                    int ni = i + Ni[n], nj = j + Nj[n];
                    long nk = Key(ni, nj);
                    if (parent.ContainsKey(nk) || !Ground(ni, nj)) continue;
                    if ((Grid.Centre(ni, nj) - r.Load).sqrMagnitude > reach) continue;
                    if (offLines && (Grid.Centre(ni, nj) - tail).sqrMagnitude > leave * leave && NearALine(ni, nj)) continue;
                    parent[nk] = key; depth[nk] = dep + 1;
                    queue.Enqueue((ni, nj));
                }
            }
            return false;
        }

        /// <summary>A polyline with its corners cut (Chaikin), its ends kept.</summary>
        static List<Vector2> Chaikin(List<Vector2> p)
        {
            if (p.Count < 3) return p;
            var o = new List<Vector2>(p.Count * 2) { p[0] };
            for (int k = 0; k + 1 < p.Count; k++)
            {
                o.Add(Vector2.Lerp(p[k], p[k + 1], 0.25f));
                o.Add(Vector2.Lerp(p[k], p[k + 1], 0.75f));
            }
            o.Add(p[p.Count - 1]);
            return o;
        }

        /// <summary>Lays a single-file stretch of centre along <paramref name="path"/> (from the line's end), taking its ground.</summary>
        void LayFile(Ribbon r, List<Vector2> path)
        {
            var last = r.Samples[r.Samples.Count - 1].P;
            float carry = 0;
            for (int k = 1; k < path.Count; k++)
            {
                var a = path[k - 1]; var b = path[k];
                float len = (b - a).magnitude;
                if (len < 1e-5f) continue;
                var t = (b - a) / len;
                float at = SampleMetres - carry;
                while (at <= len + 1e-5f)
                {
                    var p = a + t * at;
                    int n = r.Samples.Count;
                    r.Samples.Add(new Sample { P = p, T = t, File = true });
                    if (n % DiscSamples == 0) ClaimDisc(r, p, FileHalfMetres, n, null);
                    last = p;
                    at += SampleMetres;
                }
                carry = len - (at - SampleMetres);
            }
            int end = r.Samples.Count - 1;
            ClaimDisc(r, last, FileHalfMetres, end, null);
        }

        // ================= the ground =================

        /// <summary>Eight ways round a disc's rim (here, not in another part: partial classes' statics start in no set order).</summary>
        static readonly Vector2[] Rim =
        {
            new Vector2(1, 0), new Vector2(0.70710678f, 0.70710678f), new Vector2(0, 1), new Vector2(-0.70710678f, 0.70710678f),
            new Vector2(-1, 0), new Vector2(-0.70710678f, -0.70710678f), new Vector2(0, -1), new Vector2(0.70710678f, -0.70710678f),
        };

        /// <summary>
        /// Whether a cell may be stood on by line <paramref name="r"/> at its centre's sample <paramref name="n"/>: nobody's
        /// and open (or soft, over trees), or the line's own, taken within its last few metres (its own footprint behind it).
        /// </summary>
        bool CellOk(Ribbon r, Grid g, int i, int j, int n, bool soft, Dictionary<long, Claim> overlay)
        {
            long k = Key(i, j);
            if ((overlay != null && overlay.TryGetValue(k, out var c)) || _claims.TryGetValue(k, out c))
                return c.Lift == r.Lift && c.N >= n - r.Window;
            if (r.Dead.Count > 0 && r.Dead.Contains(k)) return false;
            byte st = g.State(i, j);
            return st == Open || (soft && st == Soft);
        }

        /// <summary>Whether every cell whose centre lies within <paramref name="half"/> of <paramref name="c"/> may be stood on.</summary>
        bool DiscOk(Ribbon r, Vector2 c, float half, int n, bool soft, Dictionary<long, Claim> overlay)
        {
            if ((c - r.Load).sqrMagnitude > MaxReachMetres * MaxReachMetres) return false;
            var g = _grids[r.Lift];
            int ci = Grid.Cell(c.x), cj = Grid.Cell(c.y);
            if (!CellOk(r, g, ci, cj, n, soft, overlay)) return false;
            // Eight points round the rim first: most spots fail there, cheaply.
            float rim = half - CellMetres * 0.5f;
            for (int d = 0; d < 8; d++)
            {
                var q = c + Rim[d] * rim;
                if (!CellOk(r, g, Grid.Cell(q.x), Grid.Cell(q.y), n, soft, overlay)) return false;
            }
            int rc = Mathf.CeilToInt(half / CellMetres);
            float h2 = half * half;
            for (int dj = -rc; dj <= rc; dj++)
                for (int di = -rc; di <= rc; di++)
                {
                    if ((Grid.Centre(ci + di, cj + dj) - c).sqrMagnitude > h2) continue;
                    if (!CellOk(r, g, ci + di, cj + dj, n, soft, overlay)) return false;
                }
            return true;
        }

        /// <summary>Takes every cell within <paramref name="half"/> of <paramref name="c"/> nobody has yet, for line <paramref name="r"/>.</summary>
        void ClaimDisc(Ribbon r, Vector2 c, float half, int n, Dictionary<long, Claim> overlay)
        {
            int ci = Grid.Cell(c.x), cj = Grid.Cell(c.y), rc = Mathf.CeilToInt(half / CellMetres);
            float h2 = half * half;
            var into = overlay ?? _claims;
            for (int dj = -rc; dj <= rc; dj++)
                for (int di = -rc; di <= rc; di++)
                {
                    if ((Grid.Centre(ci + di, cj + dj) - c).sqrMagnitude > h2) continue;
                    long k = Key(ci + di, cj + dj);
                    if (_claims.ContainsKey(k) || (overlay != null && overlay.ContainsKey(k))) continue;
                    into[k] = new Claim { Lift = r.Lift, N = n };
                    if (overlay == null) { r.ClaimKeys.Add(k); r.ClaimNs.Add(n); }
                }
        }
    }
}
