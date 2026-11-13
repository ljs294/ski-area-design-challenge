using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// The shared humidity and phase functions (WX-37 v2, WX-03 v3, SN-05), in °F. Float work: the calibration calls them per
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
        /// The standard atmosphere's pressure (Pa) at a height (ft above sea level): 101,325 × (1 − 6.8756·10⁻⁶ × ft)^5.2559.
        /// WX-37 v2 reads the wet bulb at it (a day's own pressure moves the wet bulb by under 0.1 °F).
        /// </summary>
        public static double StandardPressurePa(double feet) => 101_325 * Math.Pow(1 - 6.8756e-6 * feet, 5.2559);

        /// <summary>WX-37 v2's psychrometer coefficient, per °C: γ = 6.60·10⁻⁴ × (1 + 0.00115 × Tw).</summary>
        public static double PsychrometerCoefficient(double wetBulbC) => 6.60e-4 * (1 + 0.00115 * wetBulbC);

        /// <summary>
        /// WX-37 v2 (owner 2026-11-12, task 4.05 PR A3): the wet bulb (°F) from air temperature (°F), RH (%) and the point's
        /// height (ft), by the psychrometric equation e = Es(Tw) − γ·P·(T − Tw) (ventilated, over water, at the standard
        /// atmosphere's pressure there), solved by bisection between the dew point and the air. v1 used Stull's formula
        /// (2011), which ignores pressure: up to about 4 °F warm in dry cold air at 10,000 ft and 0.3–1.1 °F cold in humid air
        /// near freezing. Never above the air or below the dew point; saturated air is its own wet bulb.
        /// </summary>
        public static double WetBulbF(double tempF, double rh, double feet)
        {
            if (rh >= 100) return tempF;
            double t = FToC(tempF);
            double e = SaturationPa(t) * Math.Max(0.1, rh) / 100;
            double p = StandardPressurePa(feet);
            double lo = DewPointC(e), hi = t;
            for (int k = 0; k < 50; k++)
            {
                double tw = (lo + hi) / 2;
                if (SaturationPa(tw) - PsychrometerCoefficient(tw) * p * (t - tw) - e > 0) hi = tw; else lo = tw;
            }
            return Math.Min(tempF, CToF((lo + hi) / 2));
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
