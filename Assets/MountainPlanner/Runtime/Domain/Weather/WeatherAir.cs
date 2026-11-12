using System;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-37 and WX-03 v3 in integers, for the simulation (task 4.05 PR B2): relative humidity, wet bulb, phase and snow
    /// ratio from an air temperature and dew point in 0.1 °F. Two lookup tables, generated once from
    /// <see cref="Psychrometrics"/> (Magnus, Stull) and kept as literals in <see cref="WeatherAirTables"/>, so no exp or atan
    /// runs in the game at all (SI-09) and every runtime and platform reads the same numbers; a test checks the literals
    /// still equal the generator's. Calibration keeps the float functions (its frozen rows were made with them).
    /// </summary>
    public static class WeatherAir
    {
        // Saturation vapour pressure (mPa) at whole °F from EsMinF; linear between.
        const int EsMinF = -80, EsMaxF = 130;
        // Wet bulb (0.1 °F) at whole °F from WbMinF and whole % RH; bilinear between.
        const int WbMinF = -60, WbMaxF = 110, RhSteps = 101;
        static readonly long[] Es = WeatherAirTables.Es;
        static readonly short[] Wb = WeatherAirTables.Wb;

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
        public static short[] BuildWb()
        {
            int temps = WbMaxF - WbMinF + 1;
            var t = new short[temps * RhSteps];
            for (int i = 0; i < temps; i++)
                for (int r = 0; r < RhSteps; r++)
                    t[i * RhSteps + r] = (short)Math.Round(Psychrometrics.WetBulbF(WbMinF + i, r) * 10);
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

        /// <summary>WX-37's wet bulb (0.1 °F) from air (0.1 °F) and RH (‰), bilinear in the table; never above the air.</summary>
        public static int WetBulb(int temp, int rhPermille)
        {
            if (rhPermille >= 990) return temp;
            int lo = WbMinF * 10, hi = WbMaxF * 10;
            // Outside the table the depression at its edge carries on (WX-37 clamps the same way below −4 °F).
            int t = Math.Max(lo, Math.Min(hi, temp));
            int off = t - lo, i = Math.Min(off / 10, WbMaxF - WbMinF - 1), fi = off - i * 10;
            int rh = Math.Max(0, Math.Min(1000, rhPermille));
            int r = Math.Min(rh / 10, RhSteps - 2), fr = rh - r * 10;
            long a = Wb[i * RhSteps + r], b = Wb[i * RhSteps + r + 1], c = Wb[(i + 1) * RhSteps + r], d = Wb[(i + 1) * RhSteps + r + 1];
            long top = a * (10 - fr) + b * fr, bottom = c * (10 - fr) + d * fr;
            int wb = (int)WeatherMath.DivRound(top * (10 - fi) + bottom * fi, 100);
            wb += temp - t;   // the depression stays the edge's past the table
            return Math.Min(wb, temp);
        }

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
            foreach (short v in Wb) h = (h ^ (ushort)v) * 1099511628211UL;
            return h;
        }
    }
}
