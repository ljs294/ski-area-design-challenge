using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Costs
{
    /// <summary>The chairlift families with a price: fixed-grip doubles, triples and quads, detachable quads and sixes.</summary>
    public enum LiftFamily { FixedDouble, FixedTriple, FixedQuad, DetachQuad, DetachSix }

    /// <summary>
    /// One family's price (2025 US dollars): the two terminals, then so much a foot of slope length and a foot of vertical
    /// rise. Cost = Terminals + PerFootLength × slope length + PerFootRise × rise.
    /// </summary>
    public sealed class LiftPrice
    {
        public LiftFamily Family { get; }
        public string Name { get; }
        public double TerminalsUsd { get; }
        public double PerFootLengthUsd { get; }
        public double PerFootRiseUsd { get; }
        /// <summary>Where the numbers come from, in a line.</summary>
        public string Basis { get; }

        public LiftPrice(LiftFamily family, string name, double terminals, double perFootLength, double perFootRise, string basis)
        {
            Family = family; Name = name; TerminalsUsd = terminals; PerFootLengthUsd = perFootLength; PerFootRiseUsd = perFootRise; Basis = basis;
        }
    }

    /// <summary>
    /// The lift price table (owner, 2026-11-04): Terminals + a × slope length + b × vertical rise, in 2025 dollars. Fitted
    /// to the construction costs in Vermont Act 250 filings from 2005 to 2026 (Schedule A, the lift's own line only: chair
    /// barns, maintenance buildings, roads and grading listed apart are left out), each brought to 2025 by the CPI-U annual
    /// average (2025 = 321.9). Length and rise climb together in the data, so the fit holds the rise rate at a set share of
    /// the length rate; a free fit gives negative rates. The family comes from the type's seats and grip, so a new maker's
    /// type prices itself. Gondolas, trams, detachable eights and surface lifts have no price yet.
    /// </summary>
    public static class LiftPrices
    {
        /// <summary>The year the prices are in.</summary>
        public const int DollarYear = 2025;

        public static readonly LiftPrice FixedQuad = new LiftPrice(LiftFamily.FixedQuad, "Fixed-grip quad", 1_300_000, 300, 600,
            "Six fixed-grip quads, 2015–2024 (rms about 20%); a hard-site quad in 2025 came in at about twice the fit");
        public static readonly LiftPrice FixedTriple = new LiftPrice(LiftFamily.FixedTriple, "Fixed-grip triple", 1_100_000, 255, 505,
            "One fixed-grip triple (2009) at 0.86 of the quad");
        public static readonly LiftPrice FixedDouble = new LiftPrice(LiftFamily.FixedDouble, "Fixed-grip double", 950_000, 220, 440,
            "No filings: 0.75 of the quad");
        public static readonly LiftPrice DetachQuad = new LiftPrice(LiftFamily.DetachQuad, "Detachable quad", 4_000_000, 670, 340,
            "Three detachable quads, 2011–2026 (within 7%)");
        public static readonly LiftPrice DetachSix = new LiftPrice(LiftFamily.DetachSix, "Detachable six", 5_000_000, 670, 340,
            "Five six-packs, 2014–2025 (three within 9%); the detachable quad's line rates");

        /// <summary>Every row, cheapest family first.</summary>
        public static readonly IReadOnlyList<LiftPrice> All = new[] { FixedDouble, FixedTriple, FixedQuad, DetachQuad, DetachSix };

        /// <summary>The price for a type, or null when its family has none yet (gondolas, trams, eights, surface lifts).</summary>
        public static LiftPrice For(LiftType type)
        {
            if (type == null || type.Tow) return null;   // surface lifts: no price yet (owner Q4, v0.3.2)
            string carrier = LiftCatalogIds.Carrier(type.CatalogId) ?? "";
            if (carrier.StartsWith("gondola-", StringComparison.Ordinal) || carrier.StartsWith("tram-", StringComparison.Ordinal)) return null;
            if (type.Detachable)
                return type.Seats == 4 ? DetachQuad : type.Seats == 6 ? DetachSix : null;
            return type.Seats switch
            {
                2 => FixedDouble,
                3 => FixedTriple,
                4 => FixedQuad,
                _ => null,
            };
        }
    }
}
