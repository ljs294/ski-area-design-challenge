using System;

namespace MountainPlanner.Domain.Measure
{
    /// <summary>
    /// Steepness as a grade in percent (rise over run × 100; 100% is 45°), the one form the player sees (owner,
    /// 2026-10-02). The trail bands follow the North American convention (green under 25%, blue 25–40%, black from
    /// 40%) with double black from 60%, the convention leaving that one open. The Slope angle layer and, later, trail
    /// ratings both use these bands.
    /// </summary>
    public static class SlopeBands
    {
        public const double BluePercent = 25, BlackPercent = 40, DoubleBlackPercent = 60;
        /// <summary>Flatter than this faces no way in particular (the Exposure layer's grey).</summary>
        public const double FlatPercent = 10;

        public enum Band { Green = 0, Blue = 1, Black = 2, DoubleBlack = 3 }

        public static readonly string[] Names = { "Easiest", "More difficult", "Most difficult", "Experts only" };

        public static Band Of(double percent) =>
            percent < BluePercent ? Band.Green : percent < BlackPercent ? Band.Blue : percent < DoubleBlackPercent ? Band.Black : Band.DoubleBlack;

        public static double PercentFromRise(double risePerMetre) => Math.Abs(risePerMetre) * 100;
        public static double PercentFromDegrees(double degrees) => Math.Tan(degrees * Math.PI / 180) * 100;
        public static double DegreesFromPercent(double percent) => Math.Atan(percent / 100) * 180 / Math.PI;

        public static readonly string[] CompassPoints = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        /// <summary>The compass point (eight) of a bearing in degrees clockwise from true north.</summary>
        public static string CompassPoint(double bearingDegrees)
        {
            double b = ((bearingDegrees % 360) + 360) % 360;
            return CompassPoints[(int)Math.Floor((b + 22.5) / 45) % 8];
        }
    }
}
