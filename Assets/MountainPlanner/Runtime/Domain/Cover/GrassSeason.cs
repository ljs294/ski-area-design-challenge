using System;

namespace MountainPlanner.Domain.Cover
{
    /// <summary>
    /// Task 4.28: the grass's colour through the year, as a multiplier on the summer grass (1, 1, 1 from late June to late
    /// August): dormant straw until spring, a brown-green first flush, fresh green, summer, then yellowing, gold and straw
    /// again before the snow. A look, not a simulation: nothing reads it but the ground's shader, which also runs the season
    /// later up high (<see cref="DaysLaterPerMetre"/>) and by the ground's moisture.
    /// </summary>
    public static class GrassSeason
    {
        public const int Days = 365;
        /// <summary>The day the shader turns from "spring comes later up high" to "autumn comes earlier up high".</summary>
        public const int Midsummer = 196;
        /// <summary>Spring climbs about 30 m a day (Hopkins' rule of thumb): days later per metre above the lowest ground.</summary>
        public const float DaysLaterPerMetre = 1f / 30;

        // Day of the year (northern, at 44° of latitude), then red, green, blue. Spring days move later and autumn days earlier
        // toward the pole, two days a degree.
        static readonly float[] Stops =
        {
            0, 1.16f, 0.88f, 0.60f,     // winter: dormant straw
            100, 1.16f, 0.88f, 0.60f,
            128, 1.06f, 0.93f, 0.66f,   // the first green through last year's straw
            152, 0.98f, 1.06f, 0.84f,   // fresh spring green
            178, 1, 1, 1,               // summer
            240, 1, 1, 1,
            262, 1.10f, 1.01f, 0.80f,   // yellowing
            288, 1.30f, 1.00f, 0.58f,   // gold
            318, 1.18f, 0.89f, 0.60f,   // straw
            365, 1.16f, 0.88f, 0.60f,
        };
        const int SpringFirst = 1, SpringLast = 3, AutumnFirst = 6, AutumnLast = 8;

        /// <summary>The day on the northern calendar: south of the equator the seasons run half a year apart.</summary>
        public static int NorthernDay(int dayOfYear, double latitude)
        {
            int d = Math.Min(Days, Math.Max(1, dayOfYear));
            return latitude >= 0 ? d : (d + 182 - 1) % Days + 1;
        }

        /// <summary>The multiplier on <paramref name="northernDay"/> (0–365, fractions allowed) at a latitude (degrees).</summary>
        public static (float R, float G, float B) Tint(double northernDay, double latitude)
        {
            double shift = Math.Min(20, Math.Max(-20, (Math.Abs(latitude) - 44) * 2));
            double day = Math.Min(Days, Math.Max(0, northernDay));
            int stops = Stops.Length / 4;
            double before = 0;
            for (int k = 1; k < stops; k++)
            {
                double at = Stops[k * 4] + (k >= SpringFirst && k <= SpringLast ? shift : k >= AutumnFirst && k <= AutumnLast ? -shift : 0);
                if (day <= at || k == stops - 1)
                {
                    double t = at > before ? Math.Min(1, Math.Max(0, (day - before) / (at - before))) : 1;
                    int a = (k - 1) * 4, b = k * 4;
                    return ((float)(Stops[a + 1] + (Stops[b + 1] - Stops[a + 1]) * t),
                            (float)(Stops[a + 2] + (Stops[b + 2] - Stops[a + 2]) * t),
                            (float)(Stops[a + 3] + (Stops[b + 3] - Stops[a + 3]) * t));
                }
                before = at;
            }
            return (1, 1, 1);
        }
    }
}
