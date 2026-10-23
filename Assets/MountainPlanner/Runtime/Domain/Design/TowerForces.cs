using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// TW-04 and TW-05 (task 09): the rope's load on a head, per rope side and load case (RP-04), and the head that carries
    /// it. Each rope side's train shares its bend and load equally over its sheaves (RP-02). The uphill rope is loaded or
    /// empty and the downhill rope empty; the empty uphill and downhill ropes hang alike, so two cases cover all three.
    ///
    /// Signs: a bend or load is positive where the rope turns up over the head (it pulls up, onto hold-down sheaves above
    /// it) and negative where it turns down (it presses down on support sheaves below it).
    /// </summary>
    public static class TowerForces
    {
        /// <summary>LC-02: at most 1,500 lbf (6.67 kN) on one sheave.</summary>
        public const double SheaveLimitNewtons = 6672.3;
        /// <summary>LC-02: at most 4.5° of bend over one sheave.</summary>
        public const double SheaveBendLimit = 4.5 * Math.PI / 180;
        /// <summary>LC-02 (US code): a group's load stays at least half one sheave's limit, 750 lbf (3.34 kN), in every case,
        /// or the rope isn't held seated.</summary>
        public const double GroupMinimumNewtons = SheaveLimitNewtons / 2;

        /// <summary>The rope's load on one rope side's train in one case: 2T·sin(δ/2), signed as the bend.</summary>
        public static double GroupLoad(double tension, double bend) => 2 * tension * Math.Sin(bend / 2);

        /// <summary>
        /// The fewest sheaves that carry a bend <paramref name="bend"/> at <paramref name="tension"/> within LC-02's two
        /// limits (TW-05): the bend one sheave takes is the smaller of 4.5° and 2·asin(1,500 lbf ÷ 2T).
        /// </summary>
        public static int SheavesFor(double tension, double bend)
        {
            double b = Math.Abs(bend);
            if (b <= 0) return 0;
            int byBend = (int)Math.Ceiling(b / SheaveBendLimit - 1e-9);
            int byLoad = (int)Math.Ceiling(Math.Abs(GroupLoad(tension, b)) / SheaveLimitNewtons - 1e-9);
            return Math.Max(byBend, byLoad);
        }

        /// <summary>
        /// The heads a type's tower kit has (TW-04 v2, LT41). <see cref="Quad"/>: support s4, s6, b8 (8 sheaves at most),
        /// hold-down d8, combination c8 (4 + 4). <see cref="Wide"/> (the Sessellift D6, G6): support s4, s6, then s10 (flat:
        /// a heavy rope's load on a gentle bend) or b10 (a breakover's curve), hold-down d10, combination c8.
        /// </summary>
        public sealed class HeadKit
        {
            public static readonly HeadKit Quad = new HeadKit(false), Wide = new HeadKit(true);
            public readonly bool Ten;
            HeadKit(bool ten) { Ten = ten; }
            /// <summary>The most sheaves a support or hold-down head of this kit carries.</summary>
            public int MaxSheaves => Ten ? 10 : 8;
        }

        /// <summary>What a head carries, in both load cases, and the head that carries it.</summary>
        public readonly struct Loads
        {
            /// <summary>The tension at the head in each case (RP-03), newtons.</summary>
            public readonly double TensionLoaded, TensionEmpty;
            /// <summary>The bend over the head in each case, radians (signed: + pulls up).</summary>
            public readonly double BendLoaded, BendEmpty;
            /// <summary>One rope side's group load in each case, newtons (signed: + pulls up).</summary>
            public readonly double GroupLoaded, GroupEmpty;
            public readonly LiftRope.Head Head;

            public Loads(double tensionLoaded, double tensionEmpty, double bendLoaded, double bendEmpty, HeadKit kit = null)
            {
                TensionLoaded = tensionLoaded;
                TensionEmpty = tensionEmpty;
                BendLoaded = bendLoaded;
                BendEmpty = bendEmpty;
                GroupLoaded = GroupLoad(tensionLoaded, bendLoaded);
                GroupEmpty = GroupLoad(tensionEmpty, bendEmpty);
                Head = Choose(tensionLoaded, tensionEmpty, bendLoaded, bendEmpty, kit);
            }

            /// <summary>How many sheaves the head carries on each rope side: support below the rope, hold-down above it.</summary>
            public int SupportSheaves => SupportSheavesOf(Head);
            public int HoldDownSheaves => HoldDownSheavesOf(Head);

            /// <summary>
            /// TW-11's gauge for one case: the load on one sheave as a share of LC-02's limit, from −1 (presses down at
            /// the limit) to +1 (pulls up at the limit); beyond them it's over. A too-sharp head reads against eight sheaves.
            /// </summary>
            public double Gauge(bool loaded)
            {
                double group = loaded ? GroupLoaded : GroupEmpty;
                int n = group > 0 ? HoldDownSheaves : SupportSheaves;
                if (n == 0) n = 8;
                return group / n / SheaveLimitNewtons;
            }
        }

        /// <summary>
        /// TW-04 with TW-05: the rope presses down in both cases with at least the group minimum: the smallest support
        /// head (s4, s6, b8) whose sheaves carry it; it pulls up in both with the minimum: a hold-down (d8); otherwise (it
        /// flips between the cases, or a group falls within 750 lbf of zero) a combination (c8), four sheaves each way.
        /// More than its sheaves can carry is <see cref="LiftRope.Head.TooSharp"/> (TW-06).
        /// </summary>
        public static LiftRope.Head Choose(double tensionLoaded, double tensionEmpty, double bendLoaded, double bendEmpty, HeadKit kit = null)
        {
            kit ??= HeadKit.Quad;
            double gl = GroupLoad(tensionLoaded, bendLoaded), ge = GroupLoad(tensionEmpty, bendEmpty);
            int need = Math.Max(SheavesFor(tensionLoaded, bendLoaded), SheavesFor(tensionEmpty, bendEmpty));
            if (gl <= -GroupMinimumNewtons && ge <= -GroupMinimumNewtons)
            {
                if (need <= 4) return LiftRope.Head.S4;
                if (need <= 6) return LiftRope.Head.S6;
                if (!kit.Ten) return need <= 8 ? LiftRope.Head.B8 : LiftRope.Head.TooSharp;
                if (need > 10) return LiftRope.Head.TooSharp;
                // The wide kit: a bend six sheaves' worth of 4.5° could take sits on the flat s10 (the load is what needs the
                // sheaves); a sharper one on the curved b10.
                double bend = Math.Max(Math.Abs(bendLoaded), Math.Abs(bendEmpty));
                return bend <= 6 * SheaveBendLimit ? LiftRope.Head.S10 : LiftRope.Head.B10;
            }
            if (gl >= GroupMinimumNewtons && ge >= GroupMinimumNewtons)
                return need <= 8 && !kit.Ten ? LiftRope.Head.D8 : need <= 10 && kit.Ten ? LiftRope.Head.D10 : LiftRope.Head.TooSharp;
            // A combination: each way carries its own case on four sheaves.
            return need <= 4 ? LiftRope.Head.C8 : LiftRope.Head.TooSharp;
        }

        /// <summary>How many sheaves a head carries below the rope (support) and above it (hold-down), per rope side.</summary>
        public static int SupportSheavesOf(LiftRope.Head head) => head switch
        {
            LiftRope.Head.S4 or LiftRope.Head.C8 => 4,
            LiftRope.Head.S6 => 6,
            LiftRope.Head.B8 => 8,
            LiftRope.Head.S10 or LiftRope.Head.B10 => 10,
            _ => 0,
        };
        public static int HoldDownSheavesOf(LiftRope.Head head) => head switch
        {
            LiftRope.Head.D8 => 8,
            LiftRope.Head.D10 => 10,
            LiftRope.Head.C8 => 4,
            _ => 0,
        };
        /// <summary>A support head (sheaves below the rope only).</summary>
        public static bool IsSupport(LiftRope.Head head) => HoldDownSheavesOf(head) == 0 && SupportSheavesOf(head) > 0;
        /// <summary>A hold-down head (sheaves above the rope only).</summary>
        public static bool IsHoldDown(LiftRope.Head head) => head == LiftRope.Head.D8 || head == LiftRope.Head.D10;

        /// <summary>
        /// TW-05 at a terminal's own row (TW-16 v3): the rope turns from level in the station to <paramref name="angle"/>
        /// (the end's frame) over the row's sheaves. True when each sheave stays within LC-02's limits.
        /// </summary>
        public static bool RowCarries(TerminalEnd end, double tension, double angle) =>
            end.Sheaves <= 0 || SheavesFor(tension, angle) <= end.Sheaves;
    }
}
