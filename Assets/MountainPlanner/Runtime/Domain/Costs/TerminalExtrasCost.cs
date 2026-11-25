using System;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Costs
{
    /// <summary>
    /// Task 4.50 slice 2 (owner 2026-11-25: the huts' and ramps' cost goes into the lift's price): two lines on a lift's
    /// price, joined with <see cref="With"/> after its site works. <b>Huts</b>: the operator hut at each end
    /// (<see cref="TerminalExtras"/>; 2025 dollars from the hut cost sheet, the lift-cost method: large $90k, small $40k,
    /// mini $25k, a surface tow's $20k), plus a fixed grip's stop gate and pedestal ($2k). <b>Unload ramp</b>: a fixed
    /// grip's top (drive or return; the Monta drive's wide ramp) raised 5 ft or more, $600 per m² of deck above the ground plus $2k (unloadramp.json cost).
    /// Self-contained: nothing else in Domain/Costs depends on it.
    /// </summary>
    public static class TerminalExtrasCost
    {
        public const string Huts = "Huts", Ramp = "Unload ramp";
        public const long LargeHutUsd = 90_000, SmallHutUsd = 40_000, MiniHutUsd = 25_000, TowHutUsd = 20_000;
        public const long GateAndPedestalUsd = 2_000;
        public const double RampUsdPerSquareMetre = 600;
        public const long RampFixedUsd = 2_000;

        public static long HutUsd(HutKind k) => k switch
        {
            HutKind.Large => LargeHutUsd,
            HutKind.Small => SmallHutUsd,
            HutKind.Mini => MiniHutUsd,
            HutKind.Tow => TowHutUsd,
            _ => 0,
        };

        /// <summary>Both ends' huts, and a fixed grip's stop gate and pedestal.</summary>
        public static long HutsUsd(LiftType type)
        {
            if (type == null) return 0;
            long usd = 0;
            foreach (bool top in new[] { false, true })
            {
                var e = TerminalExtras.For(type, top);
                usd += HutUsd(e.Hut);
                if (e.UnloadEnd) usd += GateAndPedestalUsd;
            }
            return usd;
        }

        /// <summary>The unload ramp for a fixed grip's top standing <paramref name="topRaise"/> over its ground, metres (0 under 5 ft).</summary>
        public static long RampUsd(LiftType type, double topRaise)
        {
            var e = TerminalExtras.For(type, true);
            if (!TerminalExtras.RampShown(e, topRaise)) return 0;
            return RampFixedUsd + LiftCost.Dollars(TerminalExtras.RampDeckSquareMetres(e.Width, topRaise, e.Wide) * RampUsdPerSquareMetre);
        }

        /// <summary>A lift's price with its Huts line, and its Unload ramp line when it has one.</summary>
        public static LiftCostBreakdown With(LiftCostBreakdown lift, LiftType type, double topRaise)
        {
            if (lift == null) return null;
            var cost = lift.With(Huts, HutsUsd(type));
            long ramp = RampUsd(type, topRaise);
            return ramp > 0 ? cost.With(Ramp, ramp) : cost;
        }
    }
}
