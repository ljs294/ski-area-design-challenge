namespace MountainPlanner.Domain.Costs
{
    /// <summary>
    /// The site-works rates (task 4.01, owner 2026-11-06), 2025 US dollars: grading by the cubic yard of cut and of fill,
    /// clearing by the acre of forest. Public state DOT average bid prices, brought to 2025 by the CPI-U annual average,
    /// then <see cref="PrivateShare"/> of that (owner: a private resort pays about 20% less than a public bid). The share
    /// is applied once, here, and only to these public-bid rates.
    /// </summary>
    public static class SoilPrices
    {
        /// <summary>What a private party pays of a public bid price (owner, 2026-11-06).</summary>
        public const double PrivateShare = 0.8;

        /// <summary>Common excavation, the public bid in 2025 dollars a cubic yard.</summary>
        public const double CutPublicUsdPerCubicYard = 15.0;
        /// <summary>Embankment placed and compacted, the public bid in 2025 dollars a cubic yard.</summary>
        public const double FillPublicUsdPerCubicYard = 15.0;
        /// <summary>Clearing forest, the public bid in 2025 dollars an acre.</summary>
        public const double ClearingPublicUsdPerAcre = 9_250.0;

        public const double CutUsdPerCubicYard = CutPublicUsdPerCubicYard * PrivateShare;
        public const double FillUsdPerCubicYard = FillPublicUsdPerCubicYard * PrivateShare;
        public const double ClearingUsdPerAcre = ClearingPublicUsdPerAcre * PrivateShare;

        public const string CutBasis = "Common excavation: Wisconsin DOT FY2024 average $8.65/yd³ and VTrans two-year average $20.76/yd³; "
                                     + "$15 taken for mountain ground, ×0.8 private";
        public const string FillBasis = "Embankment in place: no state's per-yd³ average found consistent; set equal to cut, ×0.8 private";
        public const string ClearingBasis = "Clearing: Arkansas DOT 2023–24 weighted average $9,025/acre, to 2025 by CPI-U; stumps left "
                                          + "(grubbing goes with the grading), ×0.8 private";

        /// <summary>Cubic metres in a cubic yard and square metres in an acre (exact).</summary>
        public const double CubicMetresPerCubicYard = 0.764554857984, SquareMetresPerAcre = 4046.8564224;
    }
}
