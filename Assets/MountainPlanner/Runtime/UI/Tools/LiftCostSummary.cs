using System;
using MountainPlanner.Domain.Costs;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The lift building panel's Cost row (owner, 2026-11-04): the lift's total in 2025 dollars ($3.1M, $850k), from
    /// <see cref="LiftCost"/>. The soil works will join the total later. Made when the build changes, never per frame.
    /// </summary>
    public static class LiftCostSummary
    {
        /// <summary>The total for a line <paramref name="run"/> across and <paramref name="rise"/> up, metres; "no price yet"
        /// for a type without one.</summary>
        public static string Cost(LiftType type, double run, double rise)
        {
            var cost = LiftCost.OfLine(type, run, rise);
            return cost == null ? "no price yet" : Money(cost.TotalUsd);
        }

        /// <summary>The rule text beside the row: the parts and the price year.</summary>
        public static string Rule(LiftType type, double run, double rise)
        {
            var cost = LiftCost.OfLine(type, run, rise);
            if (cost == null) return "Lift costs: gondolas, trams, eights and surface lifts have no price yet";
            return $"{LiftPrices.DollarYear} dollars: terminals {Money(cost.Line(LiftCost.Terminals))} + length {Money(cost.Line(LiftCost.Length))}"
                 + $" + rise {Money(cost.Line(LiftCost.Rise))}; soil works later";
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
