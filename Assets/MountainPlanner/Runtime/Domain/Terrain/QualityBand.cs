namespace MountainPlanner.Domain.Terrain
{
    /// <summary>The words that go with a 0–100 data-quality score (0.4 S5, T18).</summary>
    public enum QualityBand
    {
        Limited = 0,
        Fair = 1,
        Good = 2,
        Excellent = 3,
    }

    /// <summary>
    /// Bands for the quality card and library: 90–100 Excellent, 75–89 Good, 50–74 Fair, below 50 Limited.
    /// The number and the word always appear together.
    /// </summary>
    public static class QualityBands
    {
        public static QualityBand Of(int score) =>
            score >= 90 ? QualityBand.Excellent : score >= 75 ? QualityBand.Good : score >= 50 ? QualityBand.Fair : QualityBand.Limited;

        public static string Word(QualityBand band)
        {
            switch (band)
            {
                case QualityBand.Excellent: return "Excellent";
                case QualityBand.Good: return "Good";
                case QualityBand.Fair: return "Fair";
                default: return "Limited";
            }
        }

        /// <summary>The plain-words caveat under a lower score, or "" when none is needed.</summary>
        public static string Caveat(QualityBand band)
        {
            switch (band)
            {
                case QualityBand.Fair: return "Parts of this area use coarser data.";
                case QualityBand.Limited: return "Much of this area uses coarse data, so small features may be missing.";
                default: return "";
            }
        }

        /// <summary>"97 / 100 · Excellent".</summary>
        public static string Describe(int score) => score + " / 100 · " + Word(Of(score));
    }
}
