#nullable enable
using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>A storm found in the record (WX-18 v2), in the blended record's hours (end exclusive).</summary>
    public sealed class RecordStorm
    {
        public int Start, End, BuildingStart, ClearingEnd;
        /// <summary>Its liquid (mm): the blended hours of its wet days, which sum to Daymet's totals.</summary>
        public double TotalMm;
        /// <summary>The hours whose liquid <see cref="TotalMm"/> holds (the run split at the middle of each dry gap): wider than
        /// Start–End, so the flurry share must never count them again.</summary>
        public int LiquidFrom, LiquidTo;
        /// <summary>The third and label year of its start hour (WX-17 v2: events belong to their start).</summary>
        public int Third, LabelYear;
        /// <summary>Arrival direction (degrees from, first half of its hours) and the clearing's direction.</summary>
        public double Direction, ClearingDirection;
        public int Octant, Quarter, ClearingOctant;
        public int Pattern, Cycle;
        /// <summary>The straight-into-Building flag: no Fair spell before this storm.</summary>
        public bool BackToBack;
        public double Hours => End - Start;
    }

    /// <summary>A storm cycle (WX-18 v2): a Fair spell plus the storms that end it (back-to-back storms share a cycle).</summary>
    public sealed class RecordCycle
    {
        public int FairStart, FairEnd, End, Third, LabelYear, Pattern;
        public double LiquidMm, Storminess;
        public bool Truncated;
        public readonly List<int> Storms = new List<int>();
    }

    /// <summary>The record read as states: per hour, and as events.</summary>
    public sealed class RecordStates
    {
        public int[] State = Array.Empty<int>();
        public int[] StormOf = Array.Empty<int>();
        public readonly List<RecordStorm> Storms = new List<RecordStorm>();
        public readonly List<RecordCycle> Cycles = new List<RecordCycle>();
        /// <summary>Per blended day: its third (0–35), label year and day of the 365-day label year.</summary>
        public int[] DayThird = Array.Empty<int>(), DayLabelYear = Array.Empty<int>(), DayOfLabelYear = Array.Empty<int>();
        public int WetDays, DetectedWetDays;
        /// <summary>WX-18 v2's checks: detected storm days ÷ Daymet wet days, and the share of Clearing ends in the 10 daylight hours.</summary>
        public double StormDayRatio => WetDays == 0 ? 1 : (double)DetectedWetDays / WetDays;
        public double ClearingDaylightShare;
        public int HourThird(int k) => DayThird[Math.Min(DayThird.Length - 1, k / 24)];
        public int HourLabelYear(int k) => DayLabelYear[Math.Min(DayLabelYear.Length - 1, k / 24)];
    }

    /// <summary>
    /// WX-18 v2: storms are found by day on Daymet (a wet day is ≥ 0.05 in; a run of wet days is one storm unless POWER
    /// shows a dry gap of 12 hours or more inside it), then timed by hour on the blended hours (0.004 in/h, gaps of up to 3
    /// hours allowed). Building is the run before a storm with cloud above 60 % or a rising wind (24 h at most); Clearing
    /// the run after it until the cloud is under 60 % and the wind under the third's mean (36 h at most); Fair the rest.
    /// </summary>
    public static class WeatherStates
    {
        public const double WetDayMm = 1.27, ShapingMmPerHour = 0.1016, CloudyPercent = 60, RisingWindMs = 1.0;
        public const int SplitGapHours = 12, BridgeGapHours = 3, BuildingCapHours = 24, ClearingCapHours = 36;

        static readonly int[] MonthStart = { 0, 31, 61, 92, 123, 151, 182, 212, 243, 273, 304, 335, 365 };

        public static int DayOfLabelYear(int civilDay) =>
            MonthStart[CivilDay.LabelMonth(civilDay)] + CivilDay.Date(civilDay).Day - 1;

        public static RecordStates Find(BlendedRecord b)
        {
            var s = new RecordStates();
            int nd = b.DayCount, nh = b.HourCount, lag = b.LagHours;
            s.DayThird = new int[nd]; s.DayLabelYear = new int[nd]; s.DayOfLabelYear = new int[nd];
            for (int d = 0; d < nd; d++)
            {
                s.DayThird[d] = CivilDay.Third(b.Days[d]);
                s.DayLabelYear[d] = CivilDay.LabelYear(b.Days[d]);
                s.DayOfLabelYear[d] = DayOfLabelYear(b.Days[d]);
            }
            s.State = new int[nh];
            s.StormOf = new int[nh];
            for (int k = 0; k < nh; k++) s.StormOf[k] = -1;

            // The third's mean wind, for the Clearing's end.
            var windSum = new double[WeatherRows.Thirds];
            var windN = new int[WeatherRows.Thirds];
            for (int k = 0; k < nh; k++)
                if (!float.IsNaN(b.Wind[k])) { int t = s.HourThird(k); windSum[t] += b.Wind[k]; windN[t]++; }
            var windMean = new double[WeatherRows.Thirds];
            for (int t = 0; t < windMean.Length; t++) windMean[t] = windN[t] == 0 ? 0 : windSum[t] / windN[t];

            // Storms: runs of wet days, split at POWER's dry gaps of 12 h or more.
            var wet = new bool[nd];
            for (int d = 0; d < nd; d++) wet[d] = !double.IsNaN(b.Prcp[d]) && b.Prcp[d] >= WetDayMm;
            for (int d = 0; d < nd; d++) if (wet[d]) s.WetDays++;
            for (int d = 0; d < nd;)
            {
                if (!wet[d]) { d++; continue; }
                int d1 = d;
                while (d1 + 1 < nd && wet[d1 + 1]) d1++;
                TimeRun(b, s, d, d1, lag);
                d = d1 + 1;
            }

            // Clearings (bounded by the next storm), then Buildings (bounded by the previous Clearing).
            for (int i = 0; i < s.Storms.Count; i++)
            {
                var st = s.Storms[i];
                int limit = i + 1 < s.Storms.Count ? s.Storms[i + 1].Start : nh;
                int k = st.End;
                while (k < limit && k - st.End < ClearingCapHours && (Cloud(b, k) >= CloudyPercent || Wind(b, k) >= windMean[s.HourThird(k)])) k++;
                st.ClearingEnd = k;
            }
            for (int i = 0; i < s.Storms.Count; i++)
            {
                var st = s.Storms[i];
                int bound = i > 0 ? s.Storms[i - 1].ClearingEnd : 0;
                int k = st.Start;
                while (k - 1 >= bound && st.Start - k < BuildingCapHours && (Cloud(b, k - 1) > CloudyPercent || Rising(b, k - 1))) k--;
                st.BuildingStart = k;
                st.BackToBack = i > 0 && k == bound;
            }

            // Per-hour states.
            foreach (var st in s.Storms)
            {
                for (int k = st.BuildingStart; k < st.Start; k++) s.State[k] = WeatherRows.Building;
                for (int k = st.Start; k < st.End; k++) s.State[k] = WeatherRows.Storm;
                for (int k = st.End; k < st.ClearingEnd; k++) s.State[k] = WeatherRows.Clearing;
            }
            for (int i = 0; i < s.Storms.Count; i++)
                for (int k = s.Storms[i].Start; k < s.Storms[i].End; k++) s.StormOf[k] = i;

            // Directions: the first half of the storm's hours, wind-weighted; the clearing's whole run.
            foreach (var st in s.Storms)
            {
                st.Direction = MeanDirection(b, st.Start, st.Start + Math.Max(1, (st.End - st.Start + 1) / 2));
                st.Octant = Octant(st.Direction);
                st.Quarter = Quarter(st.Direction);
                st.ClearingDirection = st.ClearingEnd > st.End ? MeanDirection(b, st.End, st.ClearingEnd) : st.Direction;
                st.ClearingOctant = Octant(st.ClearingDirection);
            }

            // Detected storm days: wet days whose Daymet window holds a storm hour.
            for (int d = 0; d < nd; d++)
            {
                if (!wet[d]) continue;
                int a = d * 24 + lag, z = Math.Min(nh, a + 24);
                for (int k = Math.Max(0, a); k < z; k++)
                    if (s.State[k] == WeatherRows.Storm) { s.DetectedWetDays++; break; }
            }

            // Clearing ends in daylight (07–17 LST).
            int ends = 0, daylight = 0;
            foreach (var st in s.Storms)
            {
                if (st.ClearingEnd <= st.End || st.ClearingEnd >= nh) continue;
                ends++;
                int h = st.ClearingEnd % 24;
                if (h >= 7 && h < 17) daylight++;
            }
            s.ClearingDaylightShare = ends == 0 ? 0 : (double)daylight / ends;

            BuildCycles(b, s);
            return s;
        }

        static void TimeRun(BlendedRecord b, RecordStates s, int d0, int d1, int lag)
        {
            int nh = b.HourCount;
            int a = Math.Max(0, d0 * 24 + lag), z = Math.Min(nh, (d1 + 1) * 24 + lag);
            if (z <= a) return;
            bool anyShaped = false;
            for (int k = a; k < z; k++) if (P(b, k) >= ShapingMmPerHour) { anyShaped = true; break; }
            double threshold = anyShaped ? ShapingMmPerHour : 1e-9;

            // Wet hours grouped; gaps of 12 h or more split the run into storms.
            var groups = new List<(int Start, int End)>();
            int gs = -1, last = -1;
            for (int k = a; k < z; k++)
            {
                if (P(b, k) < threshold) continue;
                if (gs < 0) { gs = k; last = k; continue; }
                if (k - last - 1 >= SplitGapHours) { groups.Add((gs, last + 1)); gs = k; }
                last = k;
            }
            if (gs >= 0) groups.Add((gs, last + 1));
            if (groups.Count == 0) groups.Add((a, z));

            // Each storm's liquid: the run's hours split at the middle of each dry gap.
            for (int g = 0; g < groups.Count; g++)
            {
                int from = g == 0 ? a : (groups[g - 1].End + groups[g].Start) / 2;
                int to = g == groups.Count - 1 ? z : (groups[g].End + groups[g + 1].Start) / 2;
                double total = 0;
                for (int k = from; k < to; k++) total += P(b, k);
                var st = new RecordStorm { Start = groups[g].Start, End = Math.Max(groups[g].Start + 1, groups[g].End), TotalMm = total, LiquidFrom = from, LiquidTo = to };
                st.Third = s.HourThird(st.Start);
                st.LabelYear = s.HourLabelYear(st.Start);
                s.Storms.Add(st);
            }
        }

        static void BuildCycles(BlendedRecord b, RecordStates s)
        {
            RecordCycle? current = null;
            for (int i = 0; i < s.Storms.Count; i++)
            {
                var st = s.Storms[i];
                if (current == null || !st.BackToBack)
                {
                    int fairStart = i == 0 ? 0 : s.Storms[i - 1].ClearingEnd;
                    current = new RecordCycle
                    {
                        FairStart = fairStart, FairEnd = st.BuildingStart,
                        Third = s.HourThird(fairStart), LabelYear = s.HourLabelYear(fairStart),
                        Truncated = i == 0,
                    };
                    s.Cycles.Add(current);
                }
                current.Storms.Add(i);
                current.LiquidMm += st.TotalMm;
                current.End = st.ClearingEnd;
                st.Cycle = s.Cycles.Count - 1;
            }
            if (s.Cycles.Count > 0) s.Cycles[s.Cycles.Count - 1].Truncated = true;   // the record ends inside it

            // WX-57: storminess = liquid ÷ length in days ÷ the third's mean liquid per day.
            var liquid = new double[WeatherRows.Thirds];
            var days = new int[WeatherRows.Thirds];
            for (int d = 0; d < b.DayCount; d++)
            {
                if (double.IsNaN(b.Prcp[d])) continue;
                liquid[s.DayThird[d]] += b.Prcp[d];
                days[s.DayThird[d]]++;
            }
            for (int c = 0; c < s.Cycles.Count; c++)
            {
                var cy = s.Cycles[c];
                int end = c + 1 < s.Cycles.Count ? s.Cycles[c + 1].FairStart : cy.End;
                double lengthDays = Math.Max(1, end - cy.FairStart) / 24.0;
                double perDay = days[cy.Third] == 0 ? 0 : liquid[cy.Third] / days[cy.Third];
                cy.Storminess = perDay <= 0 ? 1 : cy.LiquidMm / lengthDays / perDay;
            }
            // WX-57 v2 (owner 2026-11-08): a cycle's pattern is the weather around it, the mean storminess of the cycles either
            // side, never its own. Labelled by its own, an Active cycle was Active because its storm was big or its gap short,
            // so the pattern's factors measured the label, not a regime: they sat at their clamps, and the generator, giving
            // an Active cycle both a short gap and a big storm, made 10-day totals half again the record's.
            for (int c = 0; c < s.Cycles.Count; c++)
            {
                var cy = s.Cycles[c];
                double sum = 0; int n = 0;
                if (c > 0) { sum += s.Cycles[c - 1].Storminess; n++; }
                if (c + 1 < s.Cycles.Count) { sum += s.Cycles[c + 1].Storminess; n++; }
                double around = n == 0 ? cy.Storminess : sum / n;
                cy.Pattern = around < 0.5 ? WeatherRows.Ridge : around > 1.5 ? WeatherRows.Active : WeatherRows.Zonal;
                foreach (int i in cy.Storms) s.Storms[i].Pattern = cy.Pattern;
            }
        }

        static double P(BlendedRecord b, int k) { float v = b.P[k]; return float.IsNaN(v) ? 0 : v; }
        static double Cloud(BlendedRecord b, int k) { float v = b.Cloud[k]; return float.IsNaN(v) ? 0 : v; }
        static double Wind(BlendedRecord b, int k) { float v = b.Wind[k]; return float.IsNaN(v) ? 0 : v; }
        static bool Rising(BlendedRecord b, int k) => k >= 3 && Wind(b, k) >= Wind(b, k - 3) + RisingWindMs;

        static double MeanDirection(BlendedRecord b, int from, int to)
        {
            double x = 0, y = 0;
            for (int k = from; k < to && k < b.HourCount; k++)
            {
                float d = b.Dir[k], w = b.Wind[k];
                if (float.IsNaN(d) || float.IsNaN(w)) continue;
                double r = d * Math.PI / 180;
                x += w * Math.Sin(r);
                y += w * Math.Cos(r);
            }
            if (x == 0 && y == 0) return 0;
            double deg = Math.Atan2(x, y) * 180 / Math.PI;
            return deg < 0 ? deg + 360 : deg;
        }

        public static int Octant(double degrees) => (int)Math.Floor((degrees + 22.5) / 45) % 8;
        public static int Quarter(double degrees) => (int)Math.Floor((degrees + 45) / 90) % 4;
    }
}
