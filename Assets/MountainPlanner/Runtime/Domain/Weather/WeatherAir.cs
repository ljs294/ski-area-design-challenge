using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-37 v2 and WX-03 v3 in integers, for the simulation (task 4.05 PR B2, PR A3): relative humidity, wet bulb, phase
    /// and snow ratio from an air temperature and dew point in 0.1 °F and the point's height. Two lookup tables, generated
    /// once from <see cref="Psychrometrics"/> (Magnus's saturation pressure, the standard atmosphere's pressure) and kept as
    /// literals in <see cref="WeatherAirTables"/>, so no exp or pow runs in the game at all (SI-09) and every runtime and
    /// platform reads the same numbers; a test checks the literals still equal the generator's. The wet bulb solves the
    /// psychrometric equation in integers, as <see cref="Psychrometrics.WetBulbF"/> does in doubles for calibration.
    /// </summary>
    public static class WeatherAir
    {
        // Saturation vapour pressure (mPa) at whole °F from EsMinF; linear between.
        const int EsMinF = -80, EsMaxF = 130;
        // The standard atmosphere's pressure (Pa) every PressureStepFeet from PressureMinFeet; linear between.
        const int PressureMinFeet = -2000, PressureMaxFeet = 16000, PressureStepFeet = 500;
        static readonly long[] Es = WeatherAirTables.Es;
        static readonly int[] Pressure = WeatherAirTables.PressurePa;

        /// <summary>WX-03 v3's thresholds, 0.1 °F: all snow at or below 32.0 °F, all rain above 35.6 °F.</summary>
        public const int SnowWetBulb = 320, RainWetBulb = 356, Freezing = 320;

        /// <summary>The generator's (<see cref="WeatherAirTables"/> holds its output as literals; a test checks they still agree).</summary>
        public static long[] BuildEs()
        {
            var t = new long[EsMaxF - EsMinF + 1];
            for (int i = 0; i < t.Length; i++)
                t[i] = (long)Math.Round(Psychrometrics.SaturationPa(Psychrometrics.FToC(EsMinF + i)) * 1000);
            return t;
        }

        /// <summary>The generator's, as <see cref="BuildEs"/>.</summary>
        public static int[] BuildPressure()
        {
            var t = new int[(PressureMaxFeet - PressureMinFeet) / PressureStepFeet + 1];
            for (int i = 0; i < t.Length; i++)
                t[i] = (int)Math.Round(Psychrometrics.StandardPressurePa(PressureMinFeet + i * PressureStepFeet));
            return t;
        }

        /// <summary>Saturation vapour pressure, mPa, at a temperature in 0.1 °F (clamped to the table).</summary>
        static long SaturationMilliPa(int tenthsF)
        {
            int lo = EsMinF * 10, hi = EsMaxF * 10;
            if (tenthsF <= lo) return Es[0];
            if (tenthsF >= hi) return Es[Es.Length - 1];
            int off = tenthsF - lo, i = off / 10, f = off % 10;
            return Es[i] + (Es[i + 1] - Es[i]) * f / 10;
        }

        /// <summary>Relative humidity (‰, 0–1000) from air and dew point (0.1 °F); a dew point above the air counts as the air.</summary>
        public static int RhPermille(int temp, int dew)
        {
            long es = SaturationMilliPa(temp);
            long e = SaturationMilliPa(Math.Min(dew, temp));
            return es <= 0 ? 1000 : (int)Math.Max(0, Math.Min(1000, (e * 1000 + es / 2) / es));
        }

        /// <summary>The standard atmosphere's pressure, Pa, at a height in feet (clamped to the table).</summary>
        static long PressurePa(int feet)
        {
            if (feet <= PressureMinFeet) return Pressure[0];
            if (feet >= PressureMaxFeet) return Pressure[Pressure.Length - 1];
            int off = feet - PressureMinFeet, i = off / PressureStepFeet, f = off % PressureStepFeet;
            return Pressure[i] + (long)(Pressure[i + 1] - Pressure[i]) * f / PressureStepFeet;
        }

        /// <summary>
        /// WX-37 v2's wet bulb (0.1 °F) from air and dew point (0.1 °F) at a height (ft): the psychrometric equation
        /// e = Es(Tw) − γ·P·(T − Tw) solved by bisection on whole tenths between the dew point and the air, the nearer
        /// tenth kept. Integers only; never above the air or below the dew point.
        /// </summary>
        public static int WetBulb(int temp, int dew, int feet)
        {
            if (dew >= temp) return temp;
            long e = SaturationMilliPa(dew), p = PressurePa(feet);
            int lo = dew, hi = temp;
            long gLo = Excess(lo, temp, e, p), gHi = Excess(hi, temp, e, p);
            while (hi - lo > 1)
            {
                int mid = lo + (hi - lo) / 2;
                long g = Excess(mid, temp, e, p);
                if (g > 0) { hi = mid; gHi = g; } else { lo = mid; gLo = g; }
            }
            return -gLo < gHi ? lo : hi;
        }

        /// <summary>
        /// Es(Tw) − e − γ·P·(T − Tw), mPa, rising with Tw: ≤ 0 at the dew point, ≥ 0 at the air. γ·P·ΔT in mPa is
        /// 0.66 × (1 + 0.00115 × Tw °C) × P (Pa) × ΔT (°C), with ΔT = (T − Tw) ÷ 18 and Tw °C = (Tw − 320) ÷ 18 in 0.1 °F.
        /// </summary>
        static long Excess(int tw, int temp, long e, long p) =>
            SaturationMilliPa(tw) - e - 66L * p * (temp - tw) * (360_000 + 23L * (tw - 320)) / 648_000_000;

        /// <summary>WX-03 v3: the snow share of the liquid (‰) at a wet bulb (0.1 °F), falling linearly from 32.0 to 35.6 °F.</summary>
        public static int SnowPermille(int wetBulb)
        {
            if (wetBulb <= SnowWetBulb) return 1000;
            if (wetBulb > RainWetBulb) return 0;
            return (RainWetBulb - wetBulb) * 1000 / (RainWetBulb - SnowWetBulb);
        }

        /// <summary>SN-05 v1: the snow-to-liquid ratio in tenths, clamp(8 + 0.8 × (30 − wet bulb °F) ÷ 1.8, 8, 18).</summary>
        public static int SnowRatioTenths(int wetBulb) => Math.Max(80, Math.Min(180, 80 + (int)WeatherMath.DivRound(4L * (300 - wetBulb), 9)));

        /// <summary>New snow (0.1 in) from liquid (0.01 in), its snow share (‰) and the wet bulb (0.1 °F).</summary>
        public static int SnowTenthsIn(int liquid, int snowPermille, int wetBulb) =>
            liquid <= 0 || snowPermille <= 0 ? 0 : (int)WeatherMath.DivRound((long)liquid * snowPermille * SnowRatioTenths(wetBulb), 100_000);

        /// <summary>FNV-1a over both tables: pinned by a test on CoreCLR and Mono, so a runtime that rounds one entry differently fails.</summary>
        public static ulong TableHash()
        {
            ulong h = 14695981039346656037UL;
            foreach (long v in Es) h = (h ^ (ulong)v) * 1099511628211UL;
            foreach (int v in Pressure) h = (h ^ (uint)v) * 1099511628211UL;
            return h;
        }
    }
}
