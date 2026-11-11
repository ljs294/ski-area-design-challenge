namespace MountainPlanner.Acquisition.Weather.Qc
{
    /// <summary>
    /// The QC limits (task 4.05's data QC, owner 2026-11-08: "how will we QC data? SNOTEL is janky"). Build-time constants,
    /// not <c>Pacing</c>: they decide what the package is built from, never how the game plays. Every package records the
    /// values it was screened with (<see cref="WeatherQcLog.Limits"/>), so a later change never rewrites history silently.
    /// </summary>
    public static class WeatherQcLimits
    {
        /// <summary>A layout version for the limits: a change of any value below bumps it.</summary>
        public const int Version = 1;

        // ---- Daymet ----
        public const double DaymetTempMinC = -60, DaymetTempMaxC = 50;
        public const double DaymetPrcpMaxMm = 500;
        /// <summary>The reference pixel's Daymet elevation against our terrain's mean over its footprint: past this, Check anchor.</summary>
        public const double GeoreferenceMetres = 100;
        /// <summary>A lattice pixel's winter precipitation residual (elevation and x, y regression) past this many σ is reported.</summary>
        public const double LatticeResidualSigma = 3;
        /// <summary>A winter total past this many median absolute deviations from the median is reported (never removed).</summary>
        public const double OutlierWinterMads = 3;

        // ---- NASA POWER ----
        public const double PowerTempMinC = -60, PowerTempMaxC = 50, PowerDewMinC = -80;
        public const double PowerWindMaxMs = 75, PowerPrecipMaxMmH = 200, PowerShortwaveMax = 1400;
        public const double PowerPressureMinKpa = 40, PowerPressureMaxKpa = 110;
        /// <summary>Temperature or wind identical for this many hours or more is a stuck series: missing.</summary>
        public const int PowerFlatlineHours = 24;
        /// <summary>A day needs this many valid hours for its own shape; fewer takes the third's mean shape (WX-15 v2's fallback).</summary>
        public const int PowerMinHoursForShape = 20;
        /// <summary>The winter correlation of daily mean temperature anomalies, POWER against Daymet: below it, Check anchor.</summary>
        public const double PowerDaymetMinCorrelation = 0.7;

        // ---- SNOTEL (a cross-check only) ----
        /// <summary>A drop in the water-year PREC accumulator bigger than this (in) is a reset (October 1, a gauge service).</summary>
        public const double SnotelResetIn = 1.0;
        /// <summary>A one-day PREC or WTEQ jump bigger than this (in) that falls back within <see cref="SnotelSpikeDays"/> is a spike.</summary>
        public const double SnotelSpikeIn = 5.0;
        public const int SnotelSpikeDays = 3;
        /// <summary>A one-day SWE gain bigger than this (in) without matching Daymet precipitation is suspect (bridge release, drift).</summary>
        public const double SnotelSweJumpIn = 4.0;
        /// <summary>A midwinter one-day SWE drop bigger than this (in) while Daymet is below freezing is suspect (bridging).</summary>
        public const double SnotelSweDropIn = 3.0;
        /// <summary>
        /// The same non-zero SWE for this many days in December to March while the station's own gauge rises by at least
        /// <see cref="SnotelStuckPrecipIn"/> (Daymet only where the gauge has no clean days): stuck. A flat pillow in a dry
        /// spell is real (the Sierra's dry Januaries of 2007 and 2022 at every station).
        /// </summary>
        public const int SnotelStuckDays = 10;
        public const double SnotelStuckPrecipIn = 1.0;
        /// <summary>PREC below the winter's SWE gain by more than this share: the gauge undercaught.</summary>
        public const double SnotelUndercatchShare = 0.10;
        /// <summary>A winter (November to April) is usable with this share of clean days.</summary>
        public const double SnotelUsableDayShare = 0.90;
        /// <summary>The ratio needs this many usable winters, or no flag is raised.</summary>
        public const int SnotelMinWinters = 10;
        /// <summary>WX-07 v3: Daymet ÷ SNOTEL winter precipitation outside this range flags the anchor (report only).</summary>
        public const double SnotelRatioLow = 0.7, SnotelRatioHigh = 1.4;
    }
}
