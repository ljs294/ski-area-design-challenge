using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Costs
{
    /// <summary>One line of a cost, whole US dollars.</summary>
    public readonly struct CostLine
    {
        public string Name { get; }
        public long Usd { get; }
        public CostLine(string name, long usd) { Name = name; Usd = usd; }
        public override string ToString() => $"{Name} ${Usd:#,0}";
    }

    /// <summary>A cost as lines and their total (whole 2025 US dollars): a lift's (<see cref="LiftCostBreakdown"/>) or a trail's.</summary>
    public class CostBreakdown
    {
        readonly CostLine[] _lines;

        public IReadOnlyList<CostLine> Lines => _lines;
        public long TotalUsd { get; }

        internal CostBreakdown(CostLine[] lines)
        {
            _lines = lines;
            long total = 0;
            foreach (var line in lines) total += line.Usd;
            TotalUsd = total;
        }

        /// <summary>The same cost with one more line.</summary>
        public CostBreakdown With(string name, long usd) => new CostBreakdown(Plus(name, usd));

        /// <summary>The lines with one more at the end.</summary>
        protected CostLine[] Plus(string name, long usd)
        {
            var lines = new CostLine[_lines.Length + 1];
            Array.Copy(_lines, lines, _lines.Length);
            lines[_lines.Length] = new CostLine(name, usd);
            return lines;
        }

        /// <summary>A line's dollars, or 0 when there is no such line.</summary>
        public long Line(string name)
        {
            foreach (var line in _lines) if (line.Name == name) return line.Usd;
            return 0;
        }
    }

    /// <summary>
    /// A lift's cost as lines and their total (2025 US dollars). The lift itself is Terminals, Length and Rise; the site
    /// works (task 4.01: Grading and Clearing, <see cref="SiteCost"/>) join with <see cref="With"/> and the total follows.
    /// </summary>
    public sealed class LiftCostBreakdown : CostBreakdown
    {
        public LiftPrice Price { get; }

        internal LiftCostBreakdown(LiftPrice price, CostLine[] lines) : base(lines) { Price = price; }

        /// <summary>The same cost with one more line.</summary>
        public new LiftCostBreakdown With(string name, long usd) => new LiftCostBreakdown(Price, Plus(name, usd));
    }

    /// <summary>
    /// What a lift costs to build, worked out from its design (owner, 2026-11-04): the family's terminals, plus its rate a
    /// foot of slope length and a foot of vertical rise (<see cref="LiftPrices"/>). Pure and deterministic; nothing saved.
    /// </summary>
    public static class LiftCost
    {
        public const string Terminals = "Terminals", Length = "Length", Rise = "Rise";
        const double MetresPerFoot = 0.3048;

        /// <summary>
        /// The cost of a lift of <paramref name="type"/> whose rope runs <paramref name="slopeLengthMetres"/> along the slope
        /// and climbs <paramref name="riseMetres"/> (a falling line costs its length alone); null when the type has no price.
        /// </summary>
        public static LiftCostBreakdown Of(LiftType type, double slopeLengthMetres, double riseMetres)
        {
            var price = LiftPrices.For(type);
            if (price == null) return null;
            double lengthFt = Math.Max(0, slopeLengthMetres) / MetresPerFoot, riseFt = Math.Max(0, riseMetres) / MetresPerFoot;
            return new LiftCostBreakdown(price, new[]
            {
                new CostLine(Terminals, Dollars(price.TerminalsUsd)),
                new CostLine(Length, Dollars(price.PerFootLengthUsd * lengthFt)),
                new CostLine(Rise, Dollars(price.PerFootRiseUsd * riseFt)),
            });
        }

        /// <summary>The same from the line across (<paramref name="runMetres"/>) and the rise.</summary>
        public static LiftCostBreakdown OfLine(LiftType type, double runMetres, double riseMetres) =>
            Of(type, Math.Sqrt(runMetres * runMetres + riseMetres * riseMetres), riseMetres);

        internal static long Dollars(double usd) => (long)Math.Round(usd, MidpointRounding.AwayFromZero);
    }
}
