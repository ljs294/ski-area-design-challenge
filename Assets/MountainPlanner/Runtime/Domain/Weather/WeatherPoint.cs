using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// One elevation's weather from an hour's parameter set (WX-36 v2–WX-38 v2 and WX-03 v2, without the cold pool and the
    /// orographic factor): what the reference points (WX-35) and the calibration's plays read. The map's tables
    /// (Simulation) apply the same rules per elevation bin, with the pool and the orography on top.
    /// </summary>
    public readonly struct WeatherPoint
    {
        /// <summary>Air and dew point (0.1 °F), wet bulb (°F), liquid (0.01 in), snow share (0–1), new snow (0.1 in).</summary>
        public readonly int Temp, Dew;
        public readonly double WetBulbF, SnowShare;
        public readonly int Liquid, SnowTenthsIn;

        WeatherPoint(int temp, int dew, double wetBulb, double share, int liquid, int snow)
        {
            Temp = temp; Dew = dew; WetBulbF = wetBulb; SnowShare = share; Liquid = liquid; SnowTenthsIn = snow;
        }

        public bool Rain => Liquid > 0 && SnowShare < 1;

        /// <summary>The hour at <paramref name="feet"/>, from zref (<paramref name="zrefFeet"/>).</summary>
        public static WeatherPoint At(in WeatherHour h, int zrefFeet, int feet)
        {
            int dz = feet - zrefFeet;
            int temp = h.Temp - (int)WeatherMath.DivRound((long)h.Lapse * dz, 1000);
            int dew = Math.Min(temp, h.Dew - (int)WeatherMath.DivRound((long)h.DewLapse * dz, 1000));
            long factor = WeatherMath.Clamp(1000 + WeatherMath.DivRound((long)h.PrecipGradient * dz, 100), 0, 4000);
            int liquid = (int)WeatherMath.DivRound(h.Liquid * factor, 1000);
            return From(temp, dew, liquid, feet);
        }

        /// <summary>Phase and snow for an air temperature, dew point (0.1 °F) and liquid (0.01 in) at a height (ft, WX-37 v2).</summary>
        public static WeatherPoint From(int temp, int dew, int liquid, int feet)
        {
            double t = temp / 10.0, d = dew / 10.0;
            double wb = Psychrometrics.WetBulbF(t, Psychrometrics.RelativeHumidity(t, d), feet);
            double share = Psychrometrics.SnowShare(wb);
            int snow = liquid <= 0 || share <= 0 ? 0 : (int)Math.Round(liquid * share * Psychrometrics.SnowToLiquid(wb) / 10.0);
            return new WeatherPoint(temp, dew, wb, share, liquid, snow);
        }
    }
}
