using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Costs
{
    /// <summary>The lift families with a price: fixed-grip doubles, triples and quads, detachable quads and sixes, and (task
    /// 4.01) the surface lifts: T-bar, J-bar and the carpets, single or double, open or covered.</summary>
    public enum LiftFamily { FixedDouble, FixedTriple, FixedQuad, DetachQuad, DetachSix, TBar, JBar, Carpet, CarpetCovered, DoubleCarpet, DoubleCarpetCovered }

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
    /// type prices itself. Gondolas, trams and detachable eights have no price yet.
    /// Surface lifts (task 4.01, researched 2026-11-06; thin public data, owner to confirm at the demo): T-bars and J-bars
    /// from resort project figures, carpets from two municipal installs brought to 2025 by CPI-U and ×0.8 (public
    /// projects, owner's private-party share). A carpet's rise costs nothing extra (115 ft at most).
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

        public static readonly LiftPrice TBar = new LiftPrice(LiftFamily.TBar, "T-bar", 400_000, 110, 220,
            "Mt. Pisgah's Doppelmayr T-bar about $375k bought, plus $100k electrical; about $350k for a 1,500 ft T-bar (2017); 2020s projects $0.3–1.2M");
        public static readonly LiftPrice JBar = new LiftPrice(LiftFamily.JBar, "J-bar", 340_000, 95, 190,
            "No filings: 0.85 of the T-bar (one rider a bar, lighter line)");
        public static readonly LiftPrice Carpet = new LiftPrice(LiftFamily.Carpet, "Carpet", 90_000, 790, 0,
            "Fort McCoy 840 ft $704k (2018) and Emery Park 920 ft $850k (2019), to 2025 by CPI-U, ×0.8 private");
        public static readonly LiftPrice CarpetCovered = new LiftPrice(LiftFamily.CarpetCovered, "Covered carpet", 90_000, 1_140, 0,
            "The carpet plus a gallery, about $350 a foot");
        public static readonly LiftPrice DoubleCarpet = new LiftPrice(LiftFamily.DoubleCarpet, "Double carpet", 140_000, 1_350, 0,
            "No filings: two belts on one bed, 1.7 of the single's line rate");
        public static readonly LiftPrice DoubleCarpetCovered = new LiftPrice(LiftFamily.DoubleCarpetCovered, "Covered double carpet", 140_000, 1_950, 0,
            "The double carpet plus a wide gallery, about $600 a foot");

        /// <summary>Every row, the chairs cheapest first, then the surface lifts.</summary>
        public static readonly IReadOnlyList<LiftPrice> All = new[]
        {
            FixedDouble, FixedTriple, FixedQuad, DetachQuad, DetachSix, TBar, JBar, Carpet, CarpetCovered, DoubleCarpet, DoubleCarpetCovered,
        };

        /// <summary>The price for a type, or null when its family has none yet (gondolas, trams, eights).</summary>
        public static LiftPrice For(LiftType type)
        {
            if (type == null) return null;
            if (type.Tow) return type.Seats == 1 ? JBar : TBar;
            if (type.Belt)
            {
                bool two = LiftCatalogIds.IsDoubleBelt(type.CatalogId);
                bool covered = LiftCatalogIds.IsCovered(type.CatalogId);
                return two ? (covered ? DoubleCarpetCovered : DoubleCarpet) : covered ? CarpetCovered : Carpet;
            }
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
