using System;
using System.Collections.Generic;
using System.Globalization;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One reason a lift is red or amber, with the rule that says so (task 09: a line nothing fits is red with a
    /// named reason, never a silent failure). <see cref="Blocks"/>: red, it can't be built (LC-09: chair clearance, tower
    /// spacing, an unfinished gap, the rise limit); otherwise an amber warning. <see cref="AlongMetres"/> is where along the
    /// line, or −1.</summary>
    public readonly struct LiftProblem
    {
        public readonly string Rule;
        public readonly string Message;
        public readonly double AlongMetres;
        public readonly bool Blocks;
        public LiftProblem(string rule, string message, double along, bool blocks = true)
        {
            Rule = rule;
            Message = message;
            AlongMetres = along;
            Blocks = blocks;
        }
        public override string ToString() => $"{(Blocks ? "" : "warning ")}{Rule}: {Message}";
    }

    /// <summary>The reasons a solved lift is red or amber, in the order the tool shows them (imperial first, SH rules).</summary>
    public static class LiftProblems
    {
        const double Ft = 0.3048, NewtonsPerLbf = 4.448222;
        /// <summary>LC-09 (owner, task 09, LT14): terminal ground varying more than 40 ft under a terminal is amber.</summary>
        public const double SiteWarningMetres = 40 * Ft;

        static string Feet(double metres) => (metres / Ft).ToString("N0", CultureInfo.InvariantCulture) + " ft";
        static string Feet1(double metres) => (metres / Ft).ToString("N1", CultureInfo.InvariantCulture) + " ft";
        static string Deg(double radians) => (radians * 180 / Math.PI).ToString("N1", CultureInfo.InvariantCulture) + "°";
        static string Lbf(double newtons) => (newtons / NewtonsPerLbf).ToString("N0", CultureInfo.InvariantCulture) + " lbf";

        public static List<LiftProblem> For(LiftRope rope, double bottomPadStep = 0, double topPadStep = 0,
                                            double bottomSiteVariation = 0, double topSiteVariation = 0)
        {
            var list = new List<LiftProblem>();
            var type = rope.Type;
            // LC-07 (v2 of RP-05, LT13): the rise limit blocks; tension no longer does.
            if (!rope.RiseOk)
                list.Add(new LiftProblem("LC-07", $"Too much rise for a {type.Name.ToLowerInvariant()}: {Feet(rope.RiseMetres)}, {Feet(type.RiseLimitMetres)} at most", -1));
            // TW-01 v2 blocks.
            if (!rope.ClearanceOk)
                list.Add(new LiftProblem("TW-01", $"The chairs fall {Feet1(rope.Shortfall)} short of the clearance at {Feet(rope.ShortfallS)}", rope.ShortfallS));
            // Amber (LC-09): the rope's angle into each station (TW-16 v3).
            double exit = Steepest(rope.BottomExitLoaded, rope.BottomExitEmpty, type.Bottom);
            if (!type.Bottom.Takes(rope.BottomExitLoaded) || !type.Bottom.Takes(rope.BottomExitEmpty))
                list.Add(new LiftProblem("TW-16", $"The rope leaves the bottom terminal at {Deg(exit)}; its row takes {Deg(type.Bottom.AngleMin)} to {Deg(type.Bottom.AngleMax)}", 0, false));
            double entry = Steepest(-rope.TopEntryLoaded, -rope.TopEntryEmpty, type.Top);
            if (!type.Top.Takes(-rope.TopEntryLoaded) || !type.Top.Takes(-rope.TopEntryEmpty))
                list.Add(new LiftProblem("TW-16", $"The rope reaches the top terminal at {Deg(Math.Abs(entry))}; its row takes up to {Deg(Math.Max(Math.Abs(type.Top.AngleMin), Math.Abs(type.Top.AngleMax)))}", rope.LengthMetres, false));
            // Amber: TW-05 at the rows.
            if (!rope.RowsOk)
            {
                bool bottom = !TowerForces.RowCarries(type.Bottom, type.CounterweightNewtons, rope.BottomExitLoaded)
                              || !TowerForces.RowCarries(type.Bottom, type.CounterweightNewtons, rope.BottomExitEmpty);
                var end = bottom ? type.Bottom : type.Top;
                double t = bottom ? type.CounterweightNewtons : rope.TopTensionLoaded;
                double a = bottom ? rope.BottomExitLoaded : rope.TopEntryLoaded;
                list.Add(new LiftProblem("TW-05", $"The {(bottom ? "bottom" : "top")} terminal's row would carry {Lbf(Math.Abs(TowerForces.GroupLoad(t, a)) / Math.Max(1, end.Sheaves))} a sheave; each takes 1,500 lbf",
                                         bottom ? 0 : rope.LengthMetres, false));
            }
            // Amber: over-loaded heads, one line (LT14).
            int sharp = 0, upward = 0;
            double first = -1;
            foreach (var tower in rope.Towers)
                if (tower.Head == LiftRope.Head.TooSharp)
                {
                    sharp++;
                    if (tower.BendLoaded > 0) upward++;
                    if (first < 0) first = tower.BaseS;
                }
            if (sharp > 0)
                list.Add(new LiftProblem("TW-06", sharp == 1
                    ? (upward == 1 ? "A tower is over its sheaves' limits: add a hold-down before the rise" : "A tower is over its sheaves' limits: add a breakover on the crest")
                    : $"{sharp} towers are over their sheaves' limits: add breakovers on crests and hold-downs before rises", first, false));
            // Amber: terminal ground (LF-08, 40 ft) and a pad that can't reach the ground (LF-12).
            if (bottomSiteVariation > SiteWarningMetres) list.Add(new LiftProblem("LF-08", $"The ground under the bottom terminal varies {Feet(bottomSiteVariation)}", 0, false));
            if (topSiteVariation > SiteWarningMetres) list.Add(new LiftProblem("LF-08", $"The ground under the top terminal varies {Feet(topSiteVariation)}", rope.LengthMetres, false));
            if (bottomPadStep >= 0.05) list.Add(new LiftProblem("LF-12", $"The bottom terminal's pad can't reach the ground: a {Feet(bottomPadStep)} step", 0, false));
            if (topPadStep >= 0.05) list.Add(new LiftProblem("LF-12", $"The top terminal's pad can't reach the ground: a {Feet(topPadStep)} step", rope.LengthMetres, false));
            return list;
        }

        /// <summary>LC-09: only the blocking reasons.</summary>
        public static bool AnyBlocks(List<LiftProblem> problems)
        {
            foreach (var p in problems) if (p.Blocks) return true;
            return false;
        }

        // The angle furthest outside (or nearest the edge of) the end's range.
        static double Steepest(double a, double b, TerminalEnd end) =>
            Outside(a, end) >= Outside(b, end) ? a : b;
        static double Outside(double v, TerminalEnd end) => Math.Max(end.AngleMin - v, v - end.AngleMax);
    }
}
