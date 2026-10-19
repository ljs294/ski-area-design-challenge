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

            public Loads(double tensionLoaded, double tensionEmpty, double bendLoaded, double bendEmpty)
            {
                TensionLoaded = tensionLoaded;
                TensionEmpty = tensionEmpty;
                BendLoaded = bendLoaded;
                BendEmpty = bendEmpty;
                GroupLoaded = GroupLoad(tensionLoaded, bendLoaded);
                GroupEmpty = GroupLoad(tensionEmpty, bendEmpty);
                Head = Choose(tensionLoaded, tensionEmpty, bendLoaded, bendEmpty);
            }

            /// <summary>How many sheaves the head carries on each rope side: support below the rope, hold-down above it.</summary>
            public int SupportSheaves => Head == LiftRope.Head.S4 || Head == LiftRope.Head.C8 ? 4 : Head == LiftRope.Head.S6 ? 6 : Head == LiftRope.Head.B8 ? 8 : 0;
            public int HoldDownSheaves => Head == LiftRope.Head.D8 ? 8 : Head == LiftRope.Head.C8 ? 4 : 0;

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
        public static LiftRope.Head Choose(double tensionLoaded, double tensionEmpty, double bendLoaded, double bendEmpty)
        {
            double gl = GroupLoad(tensionLoaded, bendLoaded), ge = GroupLoad(tensionEmpty, bendEmpty);
            int need = Math.Max(SheavesFor(tensionLoaded, bendLoaded), SheavesFor(tensionEmpty, bendEmpty));
            if (gl <= -GroupMinimumNewtons && ge <= -GroupMinimumNewtons)
                return need <= 4 ? LiftRope.Head.S4 : need <= 6 ? LiftRope.Head.S6 : need <= 8 ? LiftRope.Head.B8 : LiftRope.Head.TooSharp;
            if (gl >= GroupMinimumNewtons && ge >= GroupMinimumNewtons)
                return need <= 8 ? LiftRope.Head.D8 : LiftRope.Head.TooSharp;
            // A combination: each way carries its own case on four sheaves.
            return need <= 4 ? LiftRope.Head.C8 : LiftRope.Head.TooSharp;
        }

        /// <summary>
        /// TW-05 at a terminal's own row (TW-16 v3): the rope turns from level in the station to <paramref name="angle"/>
        /// (the end's frame) over the row's sheaves. True when each sheave stays within LC-02's limits.
        /// </summary>
        public static bool RowCarries(TerminalEnd end, double tension, double angle) =>
            end.Sheaves <= 0 || SheavesFor(tension, angle) <= end.Sheaves;
    }
}
