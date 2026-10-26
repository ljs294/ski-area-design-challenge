using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One stretch along a lift line where the chairs fall short of TW-01 v2: from and to (metres along the line from
    /// the bottom bullwheel) and the worst shortfall in it (metres).</summary>
    public readonly struct ClearanceGap
    {
        public readonly double FromMetres, ToMetres, WorstMetres;
        public ClearanceGap(double from, double to, double worst) { FromMetres = from; ToMetres = to; WorstMetres = worst; }
        public override string ToString() => $"{FromMetres:F0}–{ToMetres:F0} m, {WorstMetres:F2} m short";
    }

    /// <summary>
    /// TW-01 v2 for a stored lift over any ground (task 09; for P3-08's TF-11, which blocks a trail that would push a lift's
    /// chairs below 15 ft): the rope from the lift's stored towers and levels, checked every metre under each rope across
    /// the chair, in both load cases (RP-04), to TW-08's 0.1 ft.
    /// </summary>
    public static class LiftClearance
    {
        /// <summary>
        /// The stretches where <paramref name="lift"/>'s chairs fall short over <paramref name="ground"/>; empty when they
        /// never do. <paramref name="siteX"/>, <paramref name="siteY"/> is the site's centre in <paramref name="ground"/>'s
        /// frame (design points are millimetres from it, TE5).
        /// </summary>
        public static List<ClearanceGap> Failing(IHeightSource ground, Lift lift, LiftType type, double siteX, double siteY) =>
            Gaps(Rope(ground, lift, type, siteX, siteY));

        /// <summary>The stretches where <paramref name="rope"/>'s chairs fall short of TW-01 v2, every metre; empty when they
        /// never do.</summary>
        public static List<ClearanceGap> Gaps(LiftRope rope)
        {
            var gaps = new List<ClearanceGap>();
            double from = -1, to = 0, worst = 0;
            for (double s = 0; s <= rope.LengthMetres; s += 1)
            {
                bool short_ = rope.ClearanceAt(s, out double clear, out double need) && need - clear > LiftRope.ClearanceTolerance;
                if (short_)
                {
                    if (from < 0) { from = s; worst = 0; }
                    to = s;
                    worst = Math.Max(worst, need - clear);
                }
                else if (from >= 0)
                {
                    gaps.Add(new ClearanceGap(from, to, worst));
                    from = -1;
                }
            }
            if (from >= 0) gaps.Add(new ClearanceGap(from, to, worst));
            return gaps;
        }

        /// <summary>The lift's rope over <paramref name="ground"/> from its stored towers and levels (its levels derived from
        /// the ground at its origins where it has none, as a v1 save's lift).</summary>
        public static LiftRope Rope(IHeightSource ground, Lift lift, LiftType type, double siteX, double siteY)
        {
            double bx = siteX + lift.Bottom.XMetres, by = siteY + lift.Bottom.YMetres;
            double tx = siteX + lift.Top.XMetres, ty = siteY + lift.Top.YMetres;
            var levels = lift.Levels ?? LiftBuild.GroundLevels(type, ground, bx, by, tx, ty);
            double length = Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by));
            return LiftRope.Solve(type, ground, bx, by, tx, ty, lift.Towers, lift.Stance,
                                  type.Bottom.ExitAlong, levels.BottomMetres + type.Bottom.ExitRise,
                                  type.Top.ExitAlong, levels.TopMetres + type.Top.ExitRise);
        }
    }
}
