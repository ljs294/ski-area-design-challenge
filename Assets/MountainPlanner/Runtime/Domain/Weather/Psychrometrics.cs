using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// The shared humidity and phase functions (WX-37, WX-03 v2, SN-05), in °F. Float work: the calibration calls them per
    /// record hour, the simulation once per table entry per face hour (WX-02 v2), never per cell (SI-09).
    /// </summary>
    public static class Psychrometrics
    {
        public static double CToF(double c) => c * 1.8 + 32;
        public static double FToC(double f) => (f - 32) / 1.8;

        /// <summary>Saturation vapour pressure over water (Pa), Magnus with Alduchov and Eskridge's constants.</summary>
        public static double SaturationPa(double tempC) => 610.94 * Math.Exp(17.625 * tempC / (tempC + 243.04));

        /// <summary>The dew point (°C) of a vapour pressure (Pa); the inverse of <see cref="SaturationPa"/>.</summary>
        public static double DewPointC(double vapourPa)
        {
            double g = Math.Log(Math.Max(vapourPa, 1e-3) / 610.94);
            return 243.04 * g / (17.625 - g);
        }

        /// <summary>Relative humidity (%) from air and dew point (°F), 0–100.</summary>
        public static double RelativeHumidity(double tempF, double dewF)
        {
            double t = FToC(tempF), d = FToC(Math.Min(dewF, tempF));
            return Math.Max(0, Math.Min(100, 100 * SaturationPa(d) / SaturationPa(t)));
        }

        /// <summary>
        /// WX-37: the wet bulb (°F) by Stull's formula (2011), from air temperature (°F) and RH (%). Stull is fitted for −4 to
        /// 122 °F (−20 to 50 °C) and RH 5–99 %; outside it the inputs are clamped: RH ≥ 99 % gives the air temperature; below
        /// −4 °F the wet bulb is the air minus the depression Stull gives at −4 °F and the same RH; never above the air.
        /// </summary>
        public static double WetBulbF(double tempF, double rh)
        {
            if (rh >= 99) return tempF;
            rh = Math.Max(5, rh);
            if (tempF < -4) return tempF - (-4 - StullF(-4, rh));
            return Math.Min(tempF, StullF(Math.Min(tempF, 122), rh));
        }

        static double StullF(double tempF, double rh)
        {
            double t = FToC(tempF);
            double tw = t * Math.Atan(0.151977 * Math.Sqrt(rh + 8.313659)) + Math.Atan(t + rh) - Math.Atan(rh - 1.676331)
                        + 0.00391838 * Math.Pow(rh, 1.5) * Math.Atan(0.023101 * rh) - 4.686035;
            return CToF(tw);
        }

        /// <summary>
        /// WX-03 v3 (owner 2026-11-09, the weather review): all snow at a wet bulb at or below 32.0 °F (0 °C), all rain above
        /// 35.6 °F (2 °C), half and half at 1 °C. v2 sat 1 °C colder; against the SNOTEL pillows (the one check independent of
        /// Daymet) it made less snow at Stevens Pass than the pillow held even with no melt at all.
        /// </summary>
        public const double SnowWetBulbF = 32.0, RainWetBulbF = 35.6;

        /// <summary>WX-03 v3: the snow share of an hour's liquid at a wet bulb (0–1), falling linearly between the thresholds.</summary>
        public static double SnowShare(double wetBulbF)
        {
            if (wetBulbF <= SnowWetBulbF) return 1;
            if (wetBulbF > RainWetBulbF) return 0;
            return (RainWetBulbF - wetBulbF) / (RainWetBulbF - SnowWetBulbF);
        }

        /// <summary>SN-05 v1: the snow-to-liquid ratio, clamp(8 + 0.8 × (30 − wet bulb °F) ÷ 1.8, 8, 18).</summary>
        public static double SnowToLiquid(double wetBulbF) => Math.Max(8, Math.Min(18, 8 + 0.8 * (30 - wetBulbF) / 1.8));
    }
}
