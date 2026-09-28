using System;
using System.Collections.Generic;
using System.Globalization;

namespace MountainPlanner.Domain.Flora
{
    /// <summary>What the flora score is made from, all measured from the package's own layers.</summary>
    public readonly struct FloraInputs
    {
        /// <summary>Share of the core with canopy data (no gaps), 0–1.</summary>
        public readonly double CanopyCoverage;
        /// <summary>Share of the ring's forest (WorldCover trees) with species data, 0–1.</summary>
        public readonly double SpeciesCoverage;
        /// <summary>Share of the core's 10 m cells where canopy and WorldCover agree on forest, 0–1.</summary>
        public readonly double Agreement;
        /// <summary>Share of the forest's biomass whose species has a real model (not a look-alike), 0–1.</summary>
        public readonly double ModelledShare;
        /// <summary>Year of the oldest imagery behind the flora layers.</summary>
        public readonly int DataYear;
        /// <summary>The year the package was built (passed in: Domain never reads the clock).</summary>
        public readonly int BuildYear;

        public FloraInputs(double canopyCoverage, double speciesCoverage, double agreement, double modelledShare, int dataYear, int buildYear)
        {
            CanopyCoverage = Clamp(canopyCoverage);
            SpeciesCoverage = Clamp(speciesCoverage);
            Agreement = Clamp(agreement);
            ModelledShare = Clamp(modelledShare);
            DataYear = dataYear;
            BuildYear = buildYear;
        }

        static double Clamp(double v) => double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;
    }

    /// <summary>
    /// The flora quality score and one-line summary shown beside the terrain score (F1): 0–100 from
    /// coverage (35%), agreement between canopy and WorldCover (25%), species fidelity (25%) and
    /// recency (15%). Agreement is a confidence signal, not a verdict on which source is right.
    /// </summary>
    public static class FloraQuality
    {
        public const double CoverageWeight = 0.35, AgreementWeight = 0.25, FidelityWeight = 0.25, RecencyWeight = 0.15;

        /// <summary>Canopy counts for 60% of coverage and species for 40%.</summary>
        public static double Coverage(FloraInputs f) => 0.6 * f.CanopyCoverage + 0.4 * f.SpeciesCoverage;

        /// <summary>100 for data up to two years old, then 6 points less per year, never below 20.</summary>
        public static double Recency(int dataYear, int buildYear)
        {
            int age = Math.Max(0, buildYear - dataYear);
            return Math.Max(20, 100 - 6 * Math.Max(0, age - 2));
        }

        public static IReadOnlyDictionary<string, double> Components(FloraInputs f) => new Dictionary<string, double>
        {
            ["coverage"] = Math.Round(100 * Coverage(f), 1),
            ["agreement"] = Math.Round(100 * f.Agreement, 1),
            ["speciesFidelity"] = Math.Round(100 * f.ModelledShare, 1),
            ["recency"] = Recency(f.DataYear, f.BuildYear),
        };

        public static int Score(FloraInputs f)
        {
            double score = CoverageWeight * 100 * Coverage(f) + AgreementWeight * 100 * f.Agreement
                         + FidelityWeight * 100 * f.ModelledShare + RecencyWeight * Recency(f.DataYear, f.BuildYear);
            return (int)Math.Round(score, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// For example: "Flora quality 84/100: canopy 1 m (2017–2020 imagery), species from BIGMAP 30 m,
        /// 93% of forest shown as its real species, sources agree on 61% of forest".
        /// </summary>
        public static string OneLiner(FloraInputs f, string canopy, string species)
        {
            string pct(double v) => ((int)Math.Round(100 * v, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";
            string gaps = f.CanopyCoverage < 0.995 ? $" ({pct(1 - f.CanopyCoverage)} without canopy data)" : "";
            return $"Flora quality {Score(f)}/100: {canopy}{gaps}, {species}, {pct(f.ModelledShare)} of forest shown as its real species, " +
                   $"sources agree on {pct(f.Agreement)} of forest";
        }
    }

    /// <summary>Species with a real model in the tree library, by FIA species code (from <see cref="SpeciesMap"/>).</summary>
    public static class TreeLibrary
    {
        public static readonly IReadOnlyCollection<int> ModelledSpecies = new HashSet<int>(SpeciesMap.ModelledCodes);
    }
}
