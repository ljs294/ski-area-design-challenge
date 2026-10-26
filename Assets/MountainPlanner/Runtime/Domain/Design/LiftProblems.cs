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

        /// <summary>
        /// TW-12 (LT40): why automatic placement left the chairs short, one red reason for each stretch of
        /// <paramref name="rope"/> that falls short of TW-01 v2 outside what LT34's level hints put right (up to
        /// <paramref name="hintedTo"/> from the bottom, from <paramref name="hintedFrom"/> to the top). In order: the ground under
        /// one rope's chairs standing above a centreline the rope clears (a rock beside the line); the ground near a terminal
        /// standing above the steepest line its row takes the rope on (a terminal below a crest, TW-16); where the search gave
        /// up (<paramref name="gaveUp"/>: towers at TW-02's limit, a crest too sharp with no room, TW-06); else the stretch
        /// itself. <paramref name="line"/> is the ground as built; the levels are the terminals' (load and unload).
        /// </summary>
        public static List<LiftProblem> Unplaced(LiftRope rope, LineGround line, double bottomLevel, double topLevel,
                                                 IReadOnlyList<TowerPlacement.GiveUp> gaveUp, double hintedTo, double hintedFrom)
        {
            var list = new List<LiftProblem>();
            var type = rope.Type;
            double length = rope.LengthMetres, zone = DesignUnits.StationZoneMm / 1000.0, drop = type.ChairDropMetres;
            double Centre(double s) => line.Centre[Math.Max(0, Math.Min(line.Count - 1, (int)Math.Round(s)))];
            // The highest the rope can be at s and still leave the bottom row, or reach the top row, at its steepest (TW-16).
            double bottomExitS = type.Bottom.ExitAlong, bottomExitY = bottomLevel + type.Bottom.ExitRise;
            double topExitS = length - type.Top.ExitAlong, topExitY = topLevel + type.Top.ExitRise;
            double bottomSteep = Math.Tan(type.Bottom.AngleMax);
            double topSteep = Math.Tan(Math.Max(Math.Abs(type.Top.AngleMin), Math.Abs(type.Top.AngleMax)));
            // Stretches closer than TW-03's 50 ft are one place: no tower could stand between them. Those wholly within what a
            // level hint puts right stay out of it.
            var gaps = new List<ClearanceGap>();
            foreach (var g in LiftClearance.Gaps(rope))
            {
                if (g.ToMetres <= hintedTo || g.FromMetres >= hintedFrom) continue;
                var last = gaps.Count > 0 ? gaps[gaps.Count - 1] : default;
                if (gaps.Count > 0 && g.FromMetres - last.ToMetres <= DesignUnits.TowerGapMm / 1000.0)
                    gaps[gaps.Count - 1] = new ClearanceGap(last.FromMetres, g.ToMetres, Math.Max(last.WorstMetres, g.WorstMetres));
                else gaps.Add(g);
            }
            bool crestNamedB = false, crestNamedT = false;
            bool bottomTakes = type.Bottom.Takes(rope.BottomExitLoaded) && type.Bottom.Takes(rope.BottomExitEmpty);
            bool topTakes = type.Top.Takes(-rope.TopEntryLoaded) && type.Top.Takes(-rope.TopEntryEmpty);
            foreach (var gap in gaps)
            {
                double worstS = gap.FromMetres, worst = double.MinValue, beside = 0, besideS = gap.FromMetres;
                double overB = 0, crestBS = -1, overT = 0, crestTS = -1;
                for (double s = gap.FromMetres; s <= gap.ToMetres + 1e-9; s += 1)
                {
                    if (!rope.ClearanceAt(s, out double clear, out double need)) continue;
                    if (need - clear > worst) { worst = need - clear; worstS = s; }
                    // Under the chairs higher than at the centre, where the centreline alone would clear.
                    double side = line.UnderChairs(s) - Centre(s);
                    if (clear + side >= need - LiftRope.ClearanceTolerance && side > beside) { beside = side; besideS = s; }
                    double g = Centre(s) + drop + need;
                    // Above the steepest line the terminal's row takes the rope on (TW-16), with the chairs' room below it.
                    double aboveB = g - (bottomExitY + bottomSteep * (s - bottomExitS)), aboveT = g - (topExitY + topSteep * (topExitS - s));
                    if (s <= 2 * zone && aboveB > overB) { overB = aboveB; crestBS = s; }
                    if (s >= length - 2 * zone && aboveT > overT) { overT = aboveT; crestTS = s; }
                }
                // Put right by raising or lowering a terminal (LT34's hint): nothing more to say.
                if (worstS <= hintedTo || worstS >= hintedFrom) continue;
                bool crestT = crestTS >= 0 && Centre(crestTS) > topLevel, crestB = crestBS >= 0 && Centre(crestBS) > bottomLevel;
                // A crest the terminal's row can't take the rope over is the reason before any rock beside the line.
                bool besideFirst = !(crestT && !topTakes) && !(crestB && !bottomTakes);
                string message;
                if (besideFirst && beside > LiftRope.Clearance)
                {
                    message = $"No towers clear the ground at {Feet(besideS)}: the ground under the chairs stands {Feet(beside)} above the line there; move the line";
                    worstS = besideS;
                }
                else if (crestT)
                {
                    if (crestNamedT) continue;
                    crestNamedT = true;
                    message = $"The ground {Feet(length - crestTS)} before the top terminal stands {Feet(Centre(crestTS) - topLevel)} above it: the rope can't come down into the terminal (TW-16); move the terminal";
                    worstS = crestTS;
                }
                else if (crestB)
                {
                    if (crestNamedB) continue;
                    crestNamedB = true;
                    message = $"The ground {Feet(crestBS)} past the bottom terminal stands {Feet(Centre(crestBS) - bottomLevel)} above it: the rope can't climb out of the terminal (TW-16); move the terminal";
                    worstS = crestBS;
                }
                else if (beside > LiftRope.Clearance)
                {
                    message = $"No towers clear the ground at {Feet(besideS)}: the ground under the chairs stands {Feet(beside)} above the line there; move the line";
                    worstS = besideS;
                }
                else
                {
                    // Where the search gave up nearest the stretch, within a span of it.
                    int found = -1;
                    double nearest = 250 * Ft;   // TW-12's longest span
                    for (int i = 0; i < gaveUp.Count; i++)
                    {
                        double d = Math.Max(0, Math.Max(gap.FromMetres - gaveUp[i].S, gaveUp[i].S - gap.ToMetres));
                        if (d <= nearest) { nearest = d; found = i; }
                    }
                    var kind = found >= 0 ? gaveUp[found].Kind : TowerPlacement.GiveUpKind.Rounds;
                    message = found >= 0 && kind == TowerPlacement.GiveUpKind.TowerLimit
                        ? $"No towers clear the ground at {Feet(worstS)}: it would take towers over {Feet(type.TowerMaxMetres)} (TW-02); move the line"
                        : found >= 0 && kind == TowerPlacement.GiveUpKind.TooSharp
                            ? $"No towers clear the ground at {Feet(worstS)}: the crest at {Feet(gaveUp[found].S)} is too sharp for any tower head, with no room for another tower (TW-06); move the line"
                            : $"No towers clear the ground at {Feet(worstS)}: move a tower or the line";
                }
                list.Add(new LiftProblem("TW-12", message, worstS));
            }
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
