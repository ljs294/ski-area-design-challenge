#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MountainPlanner.Acquisition.Weather.Qc
{
    /// <summary>
    /// NASA POWER's screen (QC plan): fill values are already missing (<see cref="PowerClient"/>); out-of-range values and
    /// stuck series become missing too (WX-16: never zero rain or calm wind). Then the cross-source check: POWER's daily
    /// temperature anomalies must follow Daymet's, or its cell is on the wrong side of a divide (Check anchor).
    /// </summary>
    public static class PowerScreen
    {
        public const string Source = "NASA POWER";

        static readonly (string Name, double Min, double Max)[] Ranges =
        {
            ("T2M", WeatherQcLimits.PowerTempMinC, WeatherQcLimits.PowerTempMaxC),
            ("T2MDEW", WeatherQcLimits.PowerDewMinC, WeatherQcLimits.PowerTempMaxC),
            ("RH2M", 0, 100.5),
            ("WS10M", 0, WeatherQcLimits.PowerWindMaxMs),
            ("WS50M", 0, WeatherQcLimits.PowerWindMaxMs),
            ("WD50M", 0, 360),
            ("PRECTOTCORR", 0, WeatherQcLimits.PowerPrecipMaxMmH),
            ("ALLSKY_SFC_SW_DWN", 0, WeatherQcLimits.PowerShortwaveMax),
            ("CLRSKY_SFC_SW_DWN", 0, WeatherQcLimits.PowerShortwaveMax),
            ("CLOUD_AMT", 0, 100),
            ("PS", WeatherQcLimits.PowerPressureMinKpa, WeatherQcLimits.PowerPressureMaxKpa),
        };

        public static void Apply(WeatherSourceRecord record, WeatherQcLog log)
        {
            var p = record.Power;
            long missing = 0, total = 0, outOfRange = 0;
            foreach (var (name, min, max) in Ranges)
            {
                if (!p.Values.TryGetValue(name, out var v)) { log.Report(Source, "ranges", name + " is missing from the answer."); continue; }
                for (int i = 0; i < v.Count; i++)
                {
                    total++;
                    if (double.IsNaN(v[i])) { missing++; continue; }
                    if (v[i] < min || v[i] > max) { v[i] = double.NaN; outOfRange++; }
                }
            }
            log.Add(Source, "fill values", total, missing, "the service's −999: missing, never zero");
            log.Add(Source, "ranges", total - missing, outOfRange, "each parameter's physical range");
            long flat = Flatline(p, "T2M") + Flatline(p, "WS50M");
            log.Add(Source, "flatlines", 2L * p.Count, flat, $"T2M or WS50M identical for {WeatherQcLimits.PowerFlatlineHours} h or more");
            if (record.Reference != null) CompareWithDaymet(record, log);
        }

        /// <summary>Makes runs of ≥ <see cref="WeatherQcLimits.PowerFlatlineHours"/> identical values missing; returns how many.</summary>
        public static long Flatline(PowerHours p, string name)
        {
            if (!p.Values.TryGetValue(name, out var v)) return 0;
            long n = 0;
            int start = 0;
            for (int i = 1; i <= v.Count; i++)
            {
                bool same = i < v.Count && !double.IsNaN(v[i]) && v[i] == v[start];
                if (same) continue;
                if (i - start >= WeatherQcLimits.PowerFlatlineHours && !double.IsNaN(v[start]))
                {
                    for (int k = start; k < i; k++) v[k] = double.NaN;
                    n += i - start;
                }
                start = i;
            }
            return n;
        }

        /// <summary>
        /// November to March, the daily mean temperature anomaly (each source against its own mean for the third) and the
        /// wet-day agreement. A correlation under <see cref="WeatherQcLimits.PowerDaymetMinCorrelation"/> flags the anchor.
        /// </summary>
        static void CompareWithDaymet(WeatherSourceRecord record, WeatherQcLog log)
        {
            var daily = PowerDaily(record.Power);
            var reference = record.Reference!;
            var tmax = reference.Series("tmax");
            var tmin = reference.Series("tmin");
            var prcp = reference.Series("prcp");
            var pairs = new List<(int Third, double D, double P, bool DWet, bool PWet)>();
            for (int i = 0; i < reference.Days.Count; i++)
            {
                int day = reference.Days[i];
                if (day < record.FirstDay || day > record.LastDay || reference.RejectedDays.Contains(day)) continue;
                int m = CivilDay.LabelMonth(day);
                if (m < 1 || m > 5) continue;   // November to March
                if (!daily.TryGetValue(day, out var pd) || pd.Hours < WeatherQcLimits.PowerMinHoursForShape) continue;
                pairs.Add((CivilDay.Third(day), (tmax[i] + tmin[i]) / 2, pd.MeanT, prcp[i] >= 1.27, pd.Precip >= 1.27));
            }
            if (pairs.Count < 100) { log.Report(Source, "against Daymet", "Too few winter days to compare."); return; }
            var dMean = pairs.GroupBy(q => q.Third).ToDictionary(g => g.Key, g => g.Average(q => q.D));
            var pMean = pairs.GroupBy(q => q.Third).ToDictionary(g => g.Key, g => g.Average(q => q.P));
            var a = pairs.Select(q => q.D - dMean[q.Third]).ToList();
            var b = pairs.Select(q => q.P - pMean[q.Third]).ToList();
            double r = Correlation(a, b);
            int dWet = pairs.Count(q => q.DWet), both = pairs.Count(q => q.DWet && q.PWet);
            string note = string.Format(CultureInfo.InvariantCulture,
                                        "winter daily temperature anomalies r = {0:F2} over {1} days; POWER wet on {2:P0} of Daymet's wet days",
                                        r, pairs.Count, dWet == 0 ? 0 : (double)both / dWet);
            if (r < WeatherQcLimits.PowerDaymetMinCorrelation) log.Flag(Source, "against Daymet", note + ": POWER's cell may be on the wrong side of a divide.");
            else log.Add(Source, "against Daymet", pairs.Count, 0, note);
        }

        public struct PowerDay
        {
            public double MeanT, Precip;
            public int Hours;
        }

        /// <summary>POWER's calendar days (midnight to midnight, local solar time): mean temperature over valid hours, precipitation sum.</summary>
        public static Dictionary<int, PowerDay> PowerDaily(PowerHours p)
        {
            var result = new Dictionary<int, PowerDay>();
            var t = p.Series("T2M");
            var pr = p.Series("PRECTOTCORR");
            for (int i = 0; i < p.Count; i++)
            {
                int day = p.Days[i];
                result.TryGetValue(day, out var d);
                if (!double.IsNaN(t[i])) { d.MeanT += t[i]; d.Hours++; }
                if (!double.IsNaN(pr[i])) d.Precip += pr[i];
                result[day] = d;
            }
            foreach (int day in result.Keys.ToList())
            {
                var d = result[day];
                if (d.Hours > 0) d.MeanT /= d.Hours;
                result[day] = d;
            }
            return result;
        }

        public static double Correlation(IList<double> a, IList<double> b)
        {
            double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < a.Count; i++)
            {
                sab += (a[i] - ma) * (b[i] - mb);
                saa += (a[i] - ma) * (a[i] - ma);
                sbb += (b[i] - mb) * (b[i] - mb);
            }
            return saa <= 0 || sbb <= 0 ? 0 : sab / Math.Sqrt(saa * sbb);
        }
    }
}
