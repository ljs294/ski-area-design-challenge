using System;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Domain.Weather
{
    /// <summary>
    /// WX-31 v2's integer toolkit: keyed draws (SI-06), inverse lookup in quantile tables and in one fixed standard-normal
    /// table, and fixed-point powers and roots. No <c>Math.Exp</c>, <c>Pow</c>, <c>Sqrt</c> or trig on the generator's
    /// path, so the same seed gives the same bytes on every runtime (WX-33).
    /// </summary>
    public static class WeatherMath
    {
        /// <summary>A keyed 64-bit draw (SI-06): (table version, season seed, tag, a, b).</summary>
        public static ulong Key(ulong seed, ulong tag, long a, long b = 0) =>
            DesignKey.Hash(seed ^ (WeatherKey.TableVersion * 0x9E3779B97F4A7C15UL), tag, unchecked((ulong)a), unchecked((ulong)b));

        /// <summary>A uniform draw in [0, 10,000) from a key's top bits: the quantile tables' resolution.</summary>
        public static int Per10k(ulong key) => (int)((key >> 32) * 10_000UL >> 32);

        /// <summary>A uniform draw in [0, 1,000).</summary>
        public static int Permille(ulong key) => (int)((key >> 32) * 1_000UL >> 32);

        /// <summary>A uniform integer in [lo, hi].</summary>
        public static int Between(ulong key, int lo, int hi) => lo + (int)((key >> 32) * (ulong)(hi - lo + 1) >> 32);

        static short[] BuildNormal()
        {
            var t = new short[256];
            for (int i = 0; i < 128; i++)
            {
                t[i] = LowerHalf[i];
                t[255 - i] = (short)-LowerHalf[i];
            }
            return t;
        }

        /// <summary>
        /// The one fixed table WX-31 allows: the standard normal at Φ⁻¹((i + 0.5) ÷ 256) × 1,000 for i = 0…127, written out
        /// (from Python's statistics.NormalDist, 2026-11-08) so no runtime computes it; the upper half mirrors it.
        /// </summary>
        static readonly short[] LowerHalf =
        {
            -2886,-2521,-2335,-2207,-2107,-2024,-1953,-1891,-1836,-1785,-1739,-1696,-1656,-1619,-1584,-1550,
            -1518,-1488,-1459,-1431,-1405,-1379,-1354,-1330,-1306,-1284,-1262,-1240,-1219,-1199,-1179,-1160,
            -1141,-1122,-1104,-1086,-1069,-1052,-1035,-1018,-1002, -986, -970, -954, -939, -924, -909, -894,
             -880, -866, -851, -837, -824, -810, -796, -783, -770, -757, -744, -731, -718, -706, -693, -681,
             -668, -656, -644, -632, -620, -608, -597, -585, -573, -562, -550, -539, -528, -517, -505, -494,
             -483, -472, -461, -451, -440, -429, -418, -408, -397, -386, -376, -365, -355, -345, -334, -324,
             -313, -303, -293, -283, -273, -262, -252, -242, -232, -222, -212, -202, -192, -182, -172, -162,
             -152, -142, -133, -123, -113, -103,  -93,  -83,  -74,  -64,  -54,  -44,  -34,  -24,  -15,   -5,
        };

        // Declared after LowerHalf: static initialisers run in text order.
        static readonly short[] Normal = BuildNormal();

        /// <summary>
        /// A standard normal draw × 1,000. Entry i sits at u = (i + 0.5) ÷ 256, so the key's top 24 bits are read as
        /// x = 256u − 0.5 in Q16 and interpolated between the entries either side (clamped at the ends): u and 1 − u give
        /// opposite values, so the draws' mean is exactly zero.
        /// </summary>
        public static int Normal1000(ulong key)
        {
            long x16 = (long)(key >> 40) - 32768;   // 24 bits of u × 256, in Q16, less half an entry
            if (x16 <= 0) return Normal[0];
            int i = (int)(x16 >> 16);
            if (i >= 255) return Normal[255];
            int frac = (int)(x16 & 0xFFFF);
            return Normal[i] + (int)(((long)(Normal[i + 1] - Normal[i]) * frac) >> 16);
        }

        /// <summary>The share of the standard normal below z (× 1,000) in 1/10,000, by binary search of the same table (the copula, WX-24 v2).</summary>
        public static int NormalCdf10k(int z1000)
        {
            int lo = 0, hi = 255;
            if (z1000 <= Normal[0]) return 20;
            if (z1000 >= Normal[255]) return 9980;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (Normal[mid] <= z1000) lo = mid; else hi = mid;
            }
            int a = Normal[lo], b = Normal[hi];
            long frac = b > a ? ((long)(z1000 - a) << 16) / (b - a) : 0;
            // Entry i sits at (i + 0.5) / 256.
            long p = ((2L * lo + 1) << 16) + 2 * frac;   // in 1/(512 × 65,536)
            return (int)(p * 10_000 / (512L << 16));
        }

        /// <summary>
        /// Inverse lookup in a quantile table (WX-31 v2): <paramref name="u10k"/> in [0, 10,000), linear between the points of
        /// <see cref="WeatherRows.QuantilePoints"/>, from an implicit 0 at p = 0, clamped at the last point (99.9 %).
        /// </summary>
        public static int FromQuantiles(int[] table, int offset, int u10k)
        {
            var p = WeatherRows.QuantilePoints;
            int prevP = 0, prevV = 0;
            for (int i = 0; i < p.Length; i++)
            {
                int v = table[offset + i];
                if (u10k <= p[i]) return prevV + (int)((long)(v - prevV) * (u10k - prevP) / Math.Max(1, p[i] - prevP));
                prevP = p[i];
                prevV = v;
            }
            return prevV;
        }

        /// <summary>A share drawn from a histogram in ‰ (any total): the bin whose cumulative share holds the draw.</summary>
        public static int FromHistogram(int[] hist, int offset, int count, ulong key)
        {
            long total = 0;
            for (int i = 0; i < count; i++) total += Math.Max(0, hist[offset + i]);
            if (total <= 0) return (int)((key >> 32) * (ulong)count >> 32);
            long pick = (long)((key >> 32) * (ulong)total >> 32);
            for (int i = 0; i < count; i++)
            {
                pick -= Math.Max(0, hist[offset + i]);
                if (pick < 0) return i;
            }
            return count - 1;
        }

        public const long Q32One = 1L << 32;

        /// <summary>x × y in Q32, x and y in [0, 2 × 2³²), truncated.</summary>
        public static long MulQ32Exact(long x, long y)
        {
            // 64 × 64 → 128 in parts, so values up to 2 × 2³² never overflow.
            ulong a = (ulong)x, b = (ulong)y;
            ulong aHi = a >> 32, aLo = a & 0xFFFFFFFFUL, bHi = b >> 32, bLo = b & 0xFFFFFFFFUL;
            ulong mid = aHi * bLo + aLo * bHi;
            ulong lo = aLo * bLo;
            return (long)((aHi * bHi << 32) + mid + (lo >> 32));
        }

        /// <summary>x^n in Q32 by squaring, x in [0, 1].</summary>
        public static long PowQ32(long x, int n)
        {
            long result = Q32One, b = x;
            while (n > 0)
            {
                if ((n & 1) != 0) result = MulQ32Exact(result, b);
                b = MulQ32Exact(b, b);
                n >>= 1;
            }
            return result;
        }

        /// <summary>x^(1/n) in Q32, x in [0, 1]: the largest r with r^n ≤ x, by bisection.</summary>
        public static long RootQ32(long x, int n)
        {
            if (n <= 1) return x;
            long lo = 0, hi = Q32One;
            while (hi - lo > 1)
            {
                long mid = lo + ((hi - lo) >> 1);
                if (PowQ32(mid, n) <= x) lo = mid; else hi = mid;
            }
            return lo;
        }

        /// <summary>φ^(c ÷ s) in Q32: the persistence per real hour raised to the face hour (WX-17 v2, WX-21) in integers.</summary>
        public static long PowRatioQ32(long phi, int c, int s)
        {
            int g = Gcd(c, s);
            return RootQ32(PowQ32(phi, c / g), s / g);
        }

        /// <summary>⌊√x⌋ for x ≥ 0.</summary>
        public static long ISqrt(long x)
        {
            if (x <= 0) return 0;
            long r = (long)Math.Sqrt(x);   // a guess only; corrected exactly below, so the result is the same everywhere
            while (r * r > x) r--;
            while ((r + 1) * (r + 1) <= x) r++;
            return r;
        }

        static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        public static long Clamp(long v, long lo, long hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>Integer division rounding half away from zero.</summary>
        public static long DivRound(long a, long b)
        {
            if (b < 0) { a = -a; b = -b; }
            return a >= 0 ? (a + b / 2) / b : -((-a + b / 2) / b);
        }
    }

    /// <summary>
    /// SI-06: the weather's keyed-draw tags (task 4.05), in the simulation's register (<c>SimKey</c> lists them too). A tag
    /// is never reused. <see cref="TableVersion"/> is in every key, so a rule change that re-reads draws bumps it.
    /// </summary>
    public static class WeatherKey
    {
        public const ulong TableVersion = 1;
        const ulong Base = 0x5758_0000_0000_0000UL;   // "WX"
        public const ulong State = Base + 1, Storm = Base + 2, Anomaly = Base + 3, Wind = Base + 4, Sky = Base + 5,
                           Forecast = Base + 6, SeasonCharacter = Base + 7, Almanac = Base + 8, Pattern = Base + 9, Burst = Base + 10;
    }
}
