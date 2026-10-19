using System;
using System.Collections.Generic;
using System.Globalization;

namespace MountainPlanner.Domain.Design
{
    /// <summary>One reason a lift can't be built as it stands, with the rule that says so (task 09: a line nothing fits is
    /// red with a named reason, never a silent failure). <see cref="AlongMetres"/> is where along the line, or −1.</summary>
    public readonly struct LiftProblem
    {
        public readonly string Rule;
        public readonly string Message;
        public readonly double AlongMetres;
        public LiftProblem(string rule, string message, double along) { Rule = rule; Message = message; AlongMetres = along; }
        public override string ToString() => $"{Rule}: {Message}";
    }

    /// <summary>The reasons a solved lift is red, in the order the tool shows them (imperial first, SH rules).</summary>
    public static class LiftProblems
    {
        const double Ft = 0.3048, NewtonsPerLbf = 4.448222;

        static string Feet(double metres) => (metres / Ft).ToString("N0", CultureInfo.InvariantCulture) + " ft";
        static string Feet1(double metres) => (metres / Ft).ToString("N1", CultureInfo.InvariantCulture) + " ft";
        static string Deg(double radians) => (radians * 180 / Math.PI).ToString("N1", CultureInfo.InvariantCulture) + "°";
        static string Lbf(double newtons) => (newtons / NewtonsPerLbf).ToString("N0", CultureInfo.InvariantCulture) + " lbf";

        public static List<LiftProblem> For(LiftRope rope, double bottomPadStep = 0, double topPadStep = 0)
        {
            var list = new List<LiftProblem>();
            var type = rope.Type;
            // RP-05: the whole lift.
            if (!rope.TensionOk)
                list.Add(new LiftProblem("RP-05", $"Too much rise for this lift: tension at the top {rope.TopTensionShare.ToString("P0", CultureInfo.InvariantCulture)} of rating", -1));
            // TW-16 v3: each end's row takes its rope.
            double exit = Steepest(rope.BottomExitLoaded, rope.BottomExitEmpty, type.Bottom);
            if (!type.Bottom.Takes(rope.BottomExitLoaded) || !type.Bottom.Takes(rope.BottomExitEmpty))
                list.Add(new LiftProblem("TW-16", $"The rope leaves the bottom terminal at {Deg(exit)}; its row takes {Deg(type.Bottom.AngleMin)} to {Deg(type.Bottom.AngleMax)}", 0));
            double entry = Steepest(-rope.TopEntryLoaded, -rope.TopEntryEmpty, type.Top);
            if (!type.Top.Takes(-rope.TopEntryLoaded) || !type.Top.Takes(-rope.TopEntryEmpty))
                list.Add(new LiftProblem("TW-16", $"The rope reaches the top terminal at {Deg(Math.Abs(entry))}; its row takes up to {Deg(Math.Max(Math.Abs(type.Top.AngleMin), Math.Abs(type.Top.AngleMax)))}", rope.LengthMetres));
            // TW-05 at the rows.
            if (!rope.RowsOk)
            {
                bool bottom = !TowerForces.RowCarries(type.Bottom, type.CounterweightNewtons, rope.BottomExitLoaded)
                              || !TowerForces.RowCarries(type.Bottom, type.CounterweightNewtons, rope.BottomExitEmpty);
                var end = bottom ? type.Bottom : type.Top;
                double t = bottom ? type.CounterweightNewtons : rope.TopTensionLoaded;
                double a = bottom ? rope.BottomExitLoaded : rope.TopEntryLoaded;
                list.Add(new LiftProblem("TW-05", $"The {(bottom ? "bottom" : "top")} terminal's row would carry {Lbf(Math.Abs(TowerForces.GroupLoad(t, a)) / Math.Max(1, end.Sheaves))} a sheave; each takes 1,500 lbf",
                                         bottom ? 0 : rope.LengthMetres));
            }
            // TW-06: heads too sharp.
            foreach (var tower in rope.Towers)
                if (tower.Head == LiftRope.Head.TooSharp)
                    list.Add(new LiftProblem("TW-06", tower.BendLoaded > 0
                        ? "Too sharp for one hold-down: add one before the rise"
                        : "Too sharp for one breakover: add one on the crest", tower.BaseS));
            // TW-01 v2.
            if (!rope.ClearanceOk)
                list.Add(new LiftProblem("TW-01", $"The chairs fall {Feet1(rope.Shortfall)} short of the clearance at {Feet(rope.ShortfallS)}", rope.ShortfallS));
            // LF-12: a pad that can't reach the ground within its envelope.
            if (bottomPadStep >= 0.05) list.Add(new LiftProblem("LF-12", $"The bottom terminal's pad can't reach the ground: a {Feet(bottomPadStep)} step", 0));
            if (topPadStep >= 0.05) list.Add(new LiftProblem("LF-12", $"The top terminal's pad can't reach the ground: a {Feet(topPadStep)} step", rope.LengthMetres));
            return list;
        }

        // The angle furthest outside (or nearest the edge of) the end's range.
        static double Steepest(double a, double b, TerminalEnd end) =>
            Outside(a, end) >= Outside(b, end) ? a : b;
        static double Outside(double v, TerminalEnd end) => Math.Max(end.AngleMin - v, v - end.AngleMax);
    }
}
