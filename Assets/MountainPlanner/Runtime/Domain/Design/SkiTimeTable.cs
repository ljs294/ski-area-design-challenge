using System;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// GU-10: how long a reference skier of a trail's band takes over a piece of it, station by station. Each 25 ft station
    /// contributes its own length over the band's base speed, slowed or quickened by how steep it is against the grade that
    /// speed assumes, clamped 0.4–1.3, so a dot slows on the runout instead of crossing it at the headwall's speed.
    ///
    /// The simulation takes the total (<see cref="TotalMs"/>: one number per edge, its exit tick); the renderer takes the
    /// table (<see cref="Fill"/>) and maps a skier's progress through it. Both come from this one loop, so a dot reaches
    /// the bottom on the tick the worker says it does. Moved here unchanged from the App's trail facts (task 4.13).
    ///
    /// GU-10 v3 (Guests Engine v0.1, owner 2026-11-09: skiers move with the steepness of the slope as well as their
    /// ability): the skier's own band shapes the curve. <see cref="RunCapPercent"/> is how much a skier lets the skis run
    /// where it steepens (the 1.3 above, for an expert), and past the band's <see cref="ComfortGrade"/> a skier
    /// snowploughs, side-slips and traverses: × clamp(2 − grade / comfort, 0.5, 1). An expert's table is the reference
    /// table exactly, so the reference total (routing, paths) never moved.
    /// </summary>
    public static class SkiTimeTable
    {
        /// <summary>The total, in ms, for the piece [startMm, endMm] of a trail whose rating is <paramref name="r"/>.</summary>
        public static int TotalMs(TrailRating.Result r, byte band, int startMm, int endMm, int lengthMm) =>
            Walk(r, band, Reference, startMm, endMm, lengthMm, null, null, null, out _);

        /// <summary>GU-10 v3: the same total for a skier of <paramref name="skierBand"/> (0 beginner to 3 expert).</summary>
        public static int TotalMs(TrailRating.Result r, byte band, byte skierBand, int startMm, int endMm, int lengthMm) =>
            Walk(r, band, skierBand, startMm, endMm, lengthMm, null, null, null, out _);

        /// <summary>
        /// The table for the same piece: <paramref name="alongM"/>[k] metres from the piece's start is reached after
        /// <paramref name="ms"/>[k] ms. Entry 0 is (0, 0) and the last is (length, total). Returns the count written, at most
        /// the arrays' length (a piece longer than the arrays keeps its total in the last entry).
        /// </summary>
        public static int Fill(TrailRating.Result r, byte band, int startMm, int endMm, int lengthMm, float[] alongM, float[] ms) =>
            Fill(r, band, Reference, startMm, endMm, lengthMm, alongM, ms, null);

        /// <summary>
        /// GU-10 v3: the table for a skier of <paramref name="skierBand"/>. <paramref name="caution"/>[k], when given, is the
        /// caution (0.5 to 1) of the station that ends at entry k (1 at entry 0): how much the skier holds back there,
        /// which the screen draws as a traverse.
        /// </summary>
        public static int Fill(TrailRating.Result r, byte band, byte skierBand, int startMm, int endMm, int lengthMm,
                               float[] alongM, float[] ms, float[] caution)
        {
            if (alongM == null || ms == null || alongM.Length < 2 || ms.Length < alongM.Length || (caution != null && caution.Length < alongM.Length))
                throw new ArgumentException("The table needs two entries at least.");
            Walk(r, band, skierBand, startMm, endMm, lengthMm, alongM, ms, caution, out int count);
            return count;
        }

        /// <summary>The skier band whose table is the reference (GU-10 v1/v2's one table): an expert lets it run and fears nothing.</summary>
        public const byte Reference = 3;

        static int Walk(TrailRating.Result r, byte band, byte skierBand, int startMm, int endMm, int lengthMm,
                        float[] alongM, float[] tableMs, float[] caution, out int count)
        {
            int tenths = BandSpeedTenths(band), design = BandDesignGrade(band);
            double cap = RunCapPercent(skierBand) / 100.0;
            int comfort = ComfortGrade(skierBand);
            count = 0;
            Put(alongM, tableMs, caution, ref count, 0, 0, 1);
            if (r == null || r.Along == null || r.Along.Length < 2) return Flat(alongM, tableMs, caution, ref count, lengthMm, tenths);
            double start = startMm / 1000.0, end = endMm / 1000.0;
            double ms = 0, covered = 0;
            for (int k = 0; k + 1 < r.Along.Length; k++)
            {
                double a = r.Along[k], b = r.Along[k + 1];
                double from = Math.Max(a, start), to = Math.Min(b, end);
                if (to <= from) continue;
                double run = to - from;
                double gradePercent = b > a && r.Surface != null && k + 1 < r.Surface.Length
                    ? Math.Abs(r.Surface[k] - r.Surface[k + 1]) / (b - a) * 100 : design;
                double hold = Caution(gradePercent, comfort);
                double metresPerSecond = tenths * 0.044704 * Steepness(gradePercent, design, cap) * hold;
                ms += run / Math.Max(0.1, metresPerSecond) * 1000;
                covered += run;
                Put(alongM, tableMs, caution, ref count, covered, ms, hold);
            }
            if (covered < 1) { count = 0; Put(alongM, tableMs, caution, ref count, 0, 0, 1); return Flat(alongM, tableMs, caution, ref count, lengthMm, tenths); }
            // Any part of the piece the stations didn't cover goes at the band's own speed.
            double restMm = lengthMm - covered * 1000;
            if (restMm > 0) ms += FlatMs((int)restMm, tenths);
            int total = (int)Math.Max(1, Math.Min(int.MaxValue, Math.Round(ms)));
            Last(alongM, tableMs, caution, ref count, Math.Max(covered, lengthMm / 1000.0), total);
            return total;
        }

        /// <summary>
        /// GU-10 v3: a station's speed against the trail band's base speed, for a skier of <paramref name="skierBand"/>: the
        /// pitch against the band's design grade, clamped 0.4 to the skier's run cap, times the skier's caution there.
        /// </summary>
        public static double StationFactor(byte trailBand, byte skierBand, double gradePercent) =>
            Steepness(gradePercent, BandDesignGrade(trailBand), RunCapPercent(skierBand) / 100.0) * Caution(gradePercent, ComfortGrade(skierBand));

        static double Steepness(double gradePercent, int design, double cap)
        {
            double factor = gradePercent / design;
            return factor < 0.4 ? 0.4 : factor > cap ? cap : factor;
        }

        /// <summary>GU-10 v3: 1 up to the comfort grade, then 2 − grade / comfort, never under <see cref="CautionFloor"/>. No comfort (0): 1.</summary>
        public static double Caution(double gradePercent, int comfort)
        {
            if (comfort <= 0 || gradePercent <= comfort) return 1;
            double c = 2 - gradePercent / comfort;
            return c < CautionFloor ? CautionFloor : c > 1 ? 1 : c;
        }

        static int Flat(float[] alongM, float[] tableMs, float[] caution, ref int count, int lengthMm, int tenths)
        {
            int total = FlatMs(lengthMm, tenths);
            Last(alongM, tableMs, caution, ref count, lengthMm / 1000.0, total);
            return total;
        }

        static void Put(float[] alongM, float[] tableMs, float[] caution, ref int count, double along, double ms, double hold)
        {
            if (alongM == null) return;
            if (count >= alongM.Length) { count = alongM.Length; return; }
            alongM[count] = (float)along; tableMs[count] = (float)ms;
            if (caution != null) caution[count] = (float)hold;
            count++;
        }

        static void Last(float[] alongM, float[] tableMs, float[] caution, ref int count, double along, double ms)
        {
            if (alongM == null) return;
            bool overwrite = true;
            if (count >= alongM.Length) count = alongM.Length - 1;
            else if (count > 1 && alongM[count - 1] >= along - 1e-3) count--;   // the last station is the end: overwrite it
            else overwrite = false;
            // The end keeps the caution of the last station walked (an overwritten entry already holds it).
            if (caution != null && !overwrite) caution[count] = count == 0 ? 1f : caution[count - 1];
            alongM[count] = (float)along; tableMs[count] = (float)ms;
            count++;
        }

        static int FlatMs(int lengthMm, int tenths) =>
            (int)Math.Max(1, lengthMm * 10_000L / (tenths * 4_470L));

        /// <summary>GU-10: a band's base speed, tenths of a mph (green 13, blue 20, black 25, double black 27).</summary>
        public static int BandSpeedTenths(byte band) => band == 0 ? 130 : band == 1 ? 200 : band == 2 ? 250 : 270;
        /// <summary>GU-10: the grade, percent, the band's base speed assumes.</summary>
        public static int BandDesignGrade(byte band) => band == 0 ? 15 : band == 1 ? 32 : band == 2 ? 50 : 70;
        /// <summary>
        /// GU-10 v3: how far a skier of this band lets the skis run where a pitch steepens, percent of the trail's base
        /// speed (beginner 100, intermediate 115, advanced and expert 130: the old cap for everyone).
        /// </summary>
        public static int RunCapPercent(byte skierBand) => skierBand == 0 ? 100 : skierBand == 1 ? 115 : 130;
        /// <summary>GU-10 v3: the grade, percent, past which a skier of this band holds back (beginner 18, intermediate 32, advanced 50; expert none, 0).</summary>
        public static int ComfortGrade(byte skierBand) => skierBand == 0 ? 18 : skierBand == 1 ? 32 : skierBand == 2 ? 50 : 0;
        /// <summary>GU-10 v3: the slowest a frightened skier goes, as a share of what the pitch would give.</summary>
        public const double CautionFloor = 0.5;
    }
}
