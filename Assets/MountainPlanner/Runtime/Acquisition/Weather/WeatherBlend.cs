#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// WX-15 v2's blended record at the reference pixel: one hourly record anchored to Daymet's days, on the label calendar's
    /// 365-day year (February 29 dropped from both sources; a leap year's missing December 31 filled from December 30).
    /// Hours are in local solar time; hour <c>h</c> of record day <c>d</c> is <c>d × 24 + h</c>. NaN is missing (WX-16).
    /// </summary>
    public sealed class BlendedRecord
    {
        public int FirstLabelYear, LastLabelYear;
        /// <summary>The record's days, as <see cref="CivilDay"/>s, in order.</summary>
        public int[] Days = Array.Empty<int>();
        // Daymet's days (°C, mm, Pa, kg/m²); NaN for a dropped day.
        public double[] Tmax = Array.Empty<double>(), Tmin = Array.Empty<double>(), Prcp = Array.Empty<double>(), Vp = Array.Empty<double>(), Swe = Array.Empty<double>();
        // Hours (°C, mm/h, m/s, degrees, %, 0–1).
        public float[] T = Array.Empty<float>(), Td = Array.Empty<float>(), P = Array.Empty<float>(), Wind = Array.Empty<float>(), Dir = Array.Empty<float>(),
                       Cloud = Array.Empty<float>(), Clearness = Array.Empty<float>(), Rh = Array.Empty<float>();
        /// <summary>WX-15 v3: the hours POWER's day was shifted by to match Daymet's observation day (0 to 24, every 3).</summary>
        public int LagHours;
        /// <summary>The correlation at each tried lag, for the manifest.</summary>
        public readonly Dictionary<int, double> LagCorrelations = new Dictionary<int, double>();
        /// <summary>Days whose temperature took the third's mean shape (too few POWER hours), and whose dew point stayed flat.</summary>
        public int FallbackShapeDays, FlatDewDays;
        /// <summary>WX-15 v3: wet Daymet days whose lagged POWER window was dry, so the total went to its humid hours (RH ≥ 85 %),
        /// or evenly over the day when none was humid.</summary>
        public int HumidShareDays, EvenShareDays;
        public int DayCount => Days.Length;
        public int HourCount => Days.Length * 24;
        public bool DayMissing(int d) => double.IsNaN(Tmax[d]);

        /// <summary>Label years cut out of the window (WX-07 v3(b)'s leave-one-out); none in a package build.</summary>
        public readonly SortedSet<int> SkippedLabelYears = new SortedSet<int>();
        public int YearCount => LastLabelYear - FirstLabelYear + 1 - SkippedLabelYears.Count;
        public IEnumerable<int> LabelYears()
        {
            for (int y = FirstLabelYear; y <= LastLabelYear; y++) if (!SkippedLabelYears.Contains(y)) yield return y;
        }

        /// <summary>
        /// WX-07 v3(b): the record with one label year (October 1 to September 30) cut out, the years either side joined at
        /// the cut. The hours go with their days, so the lag (WX-15 v2) still lines them up.
        /// </summary>
        public BlendedRecord WithoutLabelYear(int labelYear)
        {
            var keep = new List<int>();
            for (int d = 0; d < Days.Length; d++) if (CivilDay.LabelYear(Days[d]) != labelYear) keep.Add(d);
            var r = new BlendedRecord { FirstLabelYear = FirstLabelYear, LastLabelYear = LastLabelYear, LagHours = LagHours,
                                        FallbackShapeDays = FallbackShapeDays, FlatDewDays = FlatDewDays,
                                        HumidShareDays = HumidShareDays, EvenShareDays = EvenShareDays };
            foreach (int y in SkippedLabelYears) r.SkippedLabelYears.Add(y);
            r.SkippedLabelYears.Add(labelYear);
            foreach (var kv in LagCorrelations) r.LagCorrelations[kv.Key] = kv.Value;
            r.Days = keep.Select(d => Days[d]).ToArray();
            double[] D(double[] a) => keep.Select(d => a[d]).ToArray();
            float[] H(float[] a)
            {
                var o = new float[keep.Count * 24];
                for (int i = 0; i < keep.Count; i++) Array.Copy(a, keep[i] * 24, o, i * 24, 24);
                return o;
            }
            r.Tmax = D(Tmax); r.Tmin = D(Tmin); r.Prcp = D(Prcp); r.Vp = D(Vp); r.Swe = D(Swe);
            r.T = H(T); r.Td = H(Td); r.P = H(P); r.Wind = H(Wind); r.Dir = H(Dir); r.Cloud = H(Cloud); r.Clearness = H(Clearness); r.Rh = H(Rh);
            return r;
        }
    }

    /// <summary>WX-15 v2: Daymet's day, POWER's hours. Run at package build, off the game's threads.</summary>
    public static class WeatherBlend
    {
        /// <summary>
        /// WX-15 v3 (the weather review, 2026-11-09): every 3 hours from 0 to 24. v2 tried 0, 12 and 24 only; the best shift was
        /// 3–6 hours at most sites, and a shift earlier than 0 matched worse at every one.
        /// </summary>
        public static readonly int[] Lags = { 0, 3, 6, 9, 12, 15, 18, 21, 24 };

        public static BlendedRecord Build(WeatherSourceRecord src)
        {
            var b = new BlendedRecord { FirstLabelYear = src.FirstLabelYear, LastLabelYear = src.LastLabelYear };
            var days = new List<int>();
            for (int day = src.FirstDay; day <= src.LastDay; day++) if (!CivilDay.IsLeapDay(day)) days.Add(day);
            b.Days = days.ToArray();
            int nd = b.Days.Length, nh = nd * 24;
            b.Tmax = Fill(nd); b.Tmin = Fill(nd); b.Prcp = Fill(nd); b.Vp = Fill(nd); b.Swe = Fill(nd);
            b.T = FillF(nh); b.Td = FillF(nh); b.P = FillF(nh); b.Wind = FillF(nh); b.Dir = FillF(nh); b.Cloud = FillF(nh); b.Clearness = FillF(nh); b.Rh = FillF(nh);

            DaymetDays(src, b);
            var power = PowerIndex(src.Power, b);
            ShapeTemperature(src.Power, power, b);
            Humidity(src.Power, power, b);
            WindAndSky(src.Power, power, b);
            b.LagHours = ChooseLag(src.Power, power, b);
            Precipitation(src.Power, power, b);
            return b;
        }

        static double[] Fill(int n) { var a = new double[n]; for (int i = 0; i < n; i++) a[i] = double.NaN; return a; }
        static float[] FillF(int n) { var a = new float[n]; for (int i = 0; i < n; i++) a[i] = float.NaN; return a; }

        static void DaymetDays(WeatherSourceRecord src, BlendedRecord b)
        {
            var r = src.Reference;
            if (r == null) return;   // WX-13: the POWER-only build fills the days from POWER (WeatherCalibration)
            var at = new Dictionary<int, int>();
            for (int i = 0; i < r.Days.Count; i++) at[r.Days[i]] = i;
            var tmax = r.Series("tmax"); var tmin = r.Series("tmin"); var prcp = r.Series("prcp"); var vp = r.Series("vp"); var swe = r.Series("swe");
            for (int d = 0; d < b.DayCount; d++)
            {
                int day = b.Days[d];
                if (!at.TryGetValue(day, out int i))
                {
                    // Daymet's leap year has no December 31: filled from December 30 (WX-15 v2).
                    var date = CivilDay.Date(day);
                    if (date.Month == 12 && date.Day == 31 && DateTime.IsLeapYear(date.Year) && at.TryGetValue(day - 1, out int j)) i = j;
                    else continue;
                }
                if (r.RejectedDays.Contains(r.Days[i])) continue;
                b.Tmax[d] = tmax[i]; b.Tmin[d] = tmin[i]; b.Prcp[d] = prcp[i]; b.Vp[d] = vp[i]; b.Swe[d] = swe[i];
            }
        }

        /// <summary>For each record hour, POWER's index (−1 when POWER has no such hour).</summary>
        static int[] PowerIndex(PowerHours p, BlendedRecord b)
        {
            var dayOf = new Dictionary<int, int>();
            for (int d = 0; d < b.DayCount; d++) dayOf[b.Days[d]] = d;
            var idx = new int[b.HourCount];
            for (int i = 0; i < idx.Length; i++) idx[i] = -1;
            for (int i = 0; i < p.Count; i++)
                if (dayOf.TryGetValue(p.Days[i], out int d) && p.Hours[i] >= 0 && p.Hours[i] < 24) idx[d * 24 + p.Hours[i]] = i;
            return idx;
        }

        /// <summary>WX-15 v2 temperature: POWER's shape stretched onto Daymet's max and min; a thin day takes its third's mean shape.</summary>
        static void ShapeTemperature(PowerHours p, int[] idx, BlendedRecord b)
        {
            var t2m = p.Series("T2M");
            var shapes = new double[b.DayCount][];
            var thirdSum = new double[36, 24];
            var thirdN = new int[36];
            for (int d = 0; d < b.DayCount; d++)
            {
                var s = DayShape(t2m, idx, d);
                shapes[d] = s!;
                if (s == null) continue;
                int third = CivilDay.Third(b.Days[d]);
                for (int h = 0; h < 24; h++) thirdSum[third, h] += s[h];
                thirdN[third]++;
            }
            for (int d = 0; d < b.DayCount; d++)
            {
                if (b.DayMissing(d)) continue;
                var s = shapes[d];
                if (s == null)
                {
                    b.FallbackShapeDays++;
                    int third = CivilDay.Third(b.Days[d]);
                    s = new double[24];
                    for (int h = 0; h < 24; h++) s[h] = thirdN[third] > 0 ? thirdSum[third, h] / thirdN[third] : 0.5 - 0.5 * Math.Cos((h - 3) * Math.PI / 12);
                    Normalise(s);
                }
                double lo = b.Tmin[d], hi = b.Tmax[d];
                for (int h = 0; h < 24; h++) b.T[d * 24 + h] = (float)(lo + s[h] * (hi - lo));
            }
        }

        /// <summary>A day's 0–1 shape from POWER, linear across short gaps; null below the minimum of valid hours.</summary>
        static double[]? DayShape(List<double> v, int[] idx, int d)
        {
            var s = new double[24];
            int valid = 0;
            for (int h = 0; h < 24; h++)
            {
                int i = idx[d * 24 + h];
                s[h] = i < 0 ? double.NaN : v[i];
                if (!double.IsNaN(s[h])) valid++;
            }
            if (valid < Qc.WeatherQcLimits.PowerMinHoursForShape) return null;
            Interpolate(s);
            Normalise(s);
            return s;
        }

        static void Interpolate(double[] s)
        {
            for (int h = 0; h < s.Length; h++)
            {
                if (!double.IsNaN(s[h])) continue;
                int a = h - 1, c = h + 1;
                while (a >= 0 && double.IsNaN(s[a])) a--;
                while (c < s.Length && double.IsNaN(s[c])) c++;
                if (a < 0 && c >= s.Length) s[h] = 0;
                else if (a < 0) s[h] = s[c];
                else if (c >= s.Length) s[h] = s[a];
                else s[h] = s[a] + (s[c] - s[a]) * (h - a) / (double)(c - a);
            }
        }

        static void Normalise(double[] s)
        {
            double lo = s.Min(), hi = s.Max();
            for (int h = 0; h < s.Length; h++) s[h] = hi > lo ? (s[h] - lo) / (hi - lo) : 0.5;
        }

        /// <summary>
        /// WX-15 v2 humidity: POWER's hourly dew point shifted so the day's mean vapour pressure equals Daymet's, never above
        /// the hour's temperature (the shift is found with the cap applied, so the cap doesn't move the mean).
        /// </summary>
        static void Humidity(PowerHours p, int[] idx, BlendedRecord b)
        {
            var dew = p.Series("T2MDEW");
            var raw = new double[24];
            for (int d = 0; d < b.DayCount; d++)
            {
                if (b.DayMissing(d)) continue;
                int valid = 0;
                for (int h = 0; h < 24; h++)
                {
                    int i = idx[d * 24 + h];
                    raw[h] = i < 0 ? double.NaN : dew[i];
                    if (!double.IsNaN(raw[h])) valid++;
                }
                if (valid < Qc.WeatherQcLimits.PowerMinHoursForShape)
                {
                    b.FlatDewDays++;
                    double flat = Psychrometrics.DewPointC(b.Vp[d]);
                    for (int h = 0; h < 24; h++) raw[h] = flat;
                }
                else Interpolate(raw);
                double target = b.Vp[d];
                double lo = -60, hi = 60;
                for (int it = 0; it < 60; it++)
                {
                    double mid = (lo + hi) / 2;
                    if (MeanVapour(raw, mid, b, d) < target) lo = mid; else hi = mid;
                }
                double shift = (lo + hi) / 2;
                for (int h = 0; h < 24; h++)
                {
                    int k = d * 24 + h;
                    double td = Math.Min(raw[h] + shift, b.T[k]);
                    b.Td[k] = (float)td;
                    b.Rh[k] = (float)Psychrometrics.RelativeHumidity(Psychrometrics.CToF(b.T[k]), Psychrometrics.CToF(td));
                }
            }
        }

        static double MeanVapour(double[] raw, double shift, BlendedRecord b, int d)
        {
            double sum = 0;
            for (int h = 0; h < 24; h++) sum += Psychrometrics.SaturationPa(Math.Min(raw[h] + shift, b.T[d * 24 + h]));
            return sum / 24;
        }

        /// <summary>WX-15 v2 wind and sky: POWER's 50 m wind, cloud amount around the clock, daytime clearness (all-sky ÷ clear-sky).</summary>
        static void WindAndSky(PowerHours p, int[] idx, BlendedRecord b)
        {
            var ws = p.Series("WS50M"); var wd = p.Series("WD50M"); var cl = p.Series("CLOUD_AMT");
            var all = p.Series("ALLSKY_SFC_SW_DWN"); var clr = p.Series("CLRSKY_SFC_SW_DWN");
            for (int k = 0; k < b.HourCount; k++)
            {
                int i = idx[k];
                if (i < 0) continue;
                b.Wind[k] = (float)ws[i];
                b.Dir[k] = (float)wd[i];
                b.Cloud[k] = (float)cl[i];
                if (!double.IsNaN(clr[i]) && clr[i] > 10 && !double.IsNaN(all[i])) b.Clearness[k] = (float)Math.Max(0, Math.Min(1, all[i] / clr[i]));
            }
        }

        /// <summary>WX-15 v2: the lag (0, +12, +24 h) whose POWER 24-hour sums correlate best with Daymet's daily totals.</summary>
        static int ChooseLag(PowerHours p, int[] idx, BlendedRecord b)
        {
            var pr = p.Series("PRECTOTCORR");
            int best = 0;
            double bestR = double.NegativeInfinity;
            foreach (int lag in Lags)
            {
                var a = new List<double>();
                var c = new List<double>();
                for (int d = 0; d < b.DayCount; d++)
                {
                    if (b.DayMissing(d)) continue;
                    double sum = 0;
                    bool ok = true;
                    for (int h = 0; h < 24 && ok; h++)
                    {
                        int k = d * 24 + lag + h;
                        if (k >= b.HourCount || idx[k] < 0 || double.IsNaN(pr[idx[k]])) ok = false;
                        else sum += pr[idx[k]];
                    }
                    if (!ok) continue;
                    a.Add(b.Prcp[d]);
                    c.Add(sum);
                }
                double r = a.Count > 30 ? Qc.PowerScreen.Correlation(a, c) : 0;
                b.LagCorrelations[lag] = Math.Round(r, 4);
                if (r > bestR + 1e-9) { bestR = r; best = lag; }
            }
            return best;
        }

        /// <summary>
        /// WX-15 v2 precipitation: Daymet's day total shared over its lagged POWER window's wet hours in proportion; if POWER
        /// is dry that day, over the window's hours with RH ≥ 85 % (else evenly); if Daymet is dry, the day is dry.
        /// </summary>
        static void Precipitation(PowerHours p, int[] idx, BlendedRecord b)
        {
            var pr = p.Series("PRECTOTCORR");
            for (int k = 0; k < b.HourCount; k++) if (!b.DayMissing(k / 24)) b.P[k] = 0;
            var w = new double[24];
            for (int d = 0; d < b.DayCount; d++)
            {
                if (b.DayMissing(d)) continue;
                double total = b.Prcp[d];
                int start = d * 24 + b.LagHours;
                if (total <= 0) continue;
                double sum = 0;
                int humid = 0;
                for (int h = 0; h < 24; h++)
                {
                    int k = start + h;
                    w[h] = k < b.HourCount && idx[k] >= 0 && !double.IsNaN(pr[idx[k]]) ? Math.Max(0, pr[idx[k]]) : 0;
                    sum += w[h];
                    if (k < b.HourCount && !float.IsNaN(b.Rh[k]) && b.Rh[k] >= 85) humid++;
                }
                if (sum <= 0) { if (humid > 0) b.HumidShareDays++; else b.EvenShareDays++; }
                for (int h = 0; h < 24; h++)
                {
                    int k = start + h;
                    if (k >= b.HourCount) break;
                    double share = sum > 0 ? w[h] / sum
                                 : humid > 0 ? (!float.IsNaN(b.Rh[k]) && b.Rh[k] >= 85 ? 1.0 / humid : 0)
                                 : 1.0 / 24;
                    if (float.IsNaN(b.P[k])) b.P[k] = 0;
                    b.P[k] += (float)(total * share);
                }
            }
        }
    }
}
