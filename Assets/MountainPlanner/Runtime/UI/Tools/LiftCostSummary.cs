using System;
using MountainPlanner.Domain.Costs;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The building panels' Cost rows (owner, 2026-11-04; task 4.01): a lift's total in 2025 dollars ($3.1M, $850k) from
    /// <see cref="LiftCost"/> with its site works (<see cref="SiteCost"/>: Grading and Clearing), and a trail's site works
    /// alone. Made when the build changes, never per frame.
    /// </summary>
    public static class LiftCostSummary
    {
        /// <summary>The total for a line <paramref name="run"/> across and <paramref name="rise"/> up, metres, with its site
        /// works; "no price yet" for a type without one.</summary>
        public static string Cost(LiftType type, double run, double rise, in SiteWorks works)
        {
            var cost = Breakdown(type, run, rise, works);
            return cost == null ? "no price yet" : Money(cost.TotalUsd);
        }

        /// <summary>The rule text beside the row: the parts and the price year.</summary>
        public static string Rule(LiftType type, double run, double rise, in SiteWorks works)
        {
            var cost = Breakdown(type, run, rise, works);
            if (cost == null) return "Lift costs: gondolas, trams and eights have no price yet";
            return $"{LiftPrices.DollarYear} dollars: terminals {Money(cost.Line(LiftCost.Terminals))} + length {Money(cost.Line(LiftCost.Length))}"
                 + $" + rise {Money(cost.Line(LiftCost.Rise))} + {Works(cost, works, true)}";
        }

        /// <summary>A trail's site works: Clearing always, Grading when graded (owner, 2026-11-06).</summary>
        public static string Trail(in SiteWorks works, bool graded) => Money(SiteCost.Trail(works, graded).TotalUsd);

        public static string TrailRule(in SiteWorks works, bool graded) =>
            $"{LiftPrices.DollarYear} dollars: {Works(SiteCost.Trail(works, graded), works, graded)}";

        static LiftCostBreakdown Breakdown(LiftType type, double run, double rise, in SiteWorks works) =>
            SiteCost.With(LiftCost.OfLine(type, run, rise), works);

        static string Works(CostBreakdown cost, in SiteWorks w, bool graded)
        {
            string clearing = $"clearing {Money(cost.Line(SiteCost.Clearing))} ({ToolFormat.Area(w.ForestSquareMetres)} of forest)";
            if (!graded) return clearing;
            return $"grading {Money(cost.Line(SiteCost.Grading))} (cut {ToolFormat.Volume(w.CutCubicMetres)}, fill {ToolFormat.Volume(w.FillCubicMetres)})"
                 + $" + {clearing}";
        }

        /// <summary>$3.1M, $12M, $850k, $4,200.</summary>
        public static string Money(long usd)
        {
            double a = Math.Abs((double)usd);
            string sign = usd < 0 ? "−" : "";
            if (a >= 9_995_000) return $"{sign}${a / 1e6:0}M";
            if (a >= 999_500) return $"{sign}${a / 1e6:0.0}M";
            if (a >= 10_000) return $"{sign}${a / 1e3:0}k";
            return $"{sign}${a:#,0}";
        }
    }
}
