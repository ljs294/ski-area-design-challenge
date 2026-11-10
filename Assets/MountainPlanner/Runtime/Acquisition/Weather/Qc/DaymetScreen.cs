#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace MountainPlanner.Acquisition.Weather.Qc
{
    /// <summary>
    /// Daymet's screen (QC plan): Daymet has no gaps but can be biased or the wrong pixel. Range checks drop a day whole
    /// (WX-16); the georeference check compares the reference pixel's elevation with our terrain over its footprint; the
    /// lattice and outlier-winter checks are reported, never removed (real extremes exist).
    /// </summary>
    public static class DaymetScreen
    {
        public const string Source = "Daymet";

        public static void Apply(WeatherSourceRecord record, WeatherQcLog log)
        {
            if (record.Reference == null)
            {
                log.Limit("Outside Daymet's coverage: the rows are built from NASA POWER alone (WX-13).");
                log.Report(Source, "coverage", "The service has no tile here.");
                return;
            }
            long days = 0, rejected = 0;
            foreach (var px in record.Lattice)
            {
                var (n, r) = Ranges(px, record.FirstDay, record.LastDay);
                days += n;
                rejected += r;
            }
            log.Add(Source, "ranges", days, rejected, "tmax ≥ tmin, −60 to 50 °C, prcp 0–500 mm, vp and swe ≥ 0; a failing day is dropped whole");
            Georeference(record, log);
            OutlierWinters(record.Reference, record, log);
            LatticeCoherence(record, log);
        }

        /// <summary>Marks every day of the window failing a range check; returns (days checked, days rejected).</summary>
        public static (long Checked, long Rejected) Ranges(DaymetPixel px, int firstDay, int lastDay)
        {
            long n = 0, r = 0;
            var tmax = px.Series("tmax");
            var tmin = px.Series("tmin");
            var prcp = px.Series("prcp");
            var vp = px.Has("vp") ? px.Series("vp") : null;
            var swe = px.Has("swe") ? px.Series("swe") : null;
            for (int i = 0; i < px.Days.Count; i++)
            {
                int day = px.Days[i];
                if (day < firstDay || day > lastDay) continue;
                n++;
                bool bad = double.IsNaN(tmax[i]) || double.IsNaN(tmin[i]) || double.IsNaN(prcp[i])
                           || tmax[i] < tmin[i] - 1e-9
                           || tmin[i] < WeatherQcLimits.DaymetTempMinC || tmax[i] > WeatherQcLimits.DaymetTempMaxC
                           || prcp[i] < 0 || prcp[i] > WeatherQcLimits.DaymetPrcpMaxMm
                           || (vp != null && (double.IsNaN(vp[i]) || vp[i] < 0))
                           || (swe != null && (double.IsNaN(swe[i]) || swe[i] < 0));
                if (bad && px.RejectedDays.Add(day)) r++;
            }
            return (n, r);
        }

        static void Georeference(WeatherSourceRecord record, WeatherQcLog log)
        {
            var ours = record.Site.OurPixelElevations;
            if (ours.Count == 0 || double.IsNaN(ours[0]))
            {
                log.Report(Source, "georeference", "Our terrain doesn't cover the reference pixel's footprint: not checked.");
                return;
            }
            double daymet = record.Reference!.ElevationMetres, diff = daymet - ours[0];
            string note = string.Format(CultureInfo.InvariantCulture,
                                        "Reference pixel: Daymet {0:F0} m, our terrain {1:F0} m over the same footprint ({2:+0;−0} m)", daymet, ours[0], diff);
            if (Math.Abs(diff) > WeatherQcLimits.GeoreferenceMetres) log.Flag(Source, "georeference", note + ": the wrong pixel, or a bad elevation.");
            else log.Add(Source, "georeference", 1, 0, note);
            // The other lattice pixels: reported in one line, for the elevation regressions' sake.
            int worst = -1;
            double worstDiff = 0;
            for (int k = 1; k < record.Lattice.Count; k++)
            {
                int slot = record.LatticeSlots[k];
                if (slot >= ours.Count || double.IsNaN(ours[slot])) continue;
                double d = record.Lattice[k].ElevationMetres - ours[slot];
                if (Math.Abs(d) > Math.Abs(worstDiff)) { worstDiff = d; worst = k; }
            }
            if (worst > 0)
                log.Add(Source, "lattice elevations", record.Lattice.Count - 1, 0,
                        string.Format(CultureInfo.InvariantCulture, "largest Daymet minus terrain difference {0:+0;−0} m", worstDiff),
                        Math.Abs(worstDiff) > WeatherQcLimits.GeoreferenceMetres * 2 ? "reported" : "ok");
        }

        /// <summary>Winter (October to April) liquid per label year at a pixel, mm; NaN for a winter with a rejected day.</summary>
        public static Dictionary<int, double> WinterTotals(DaymetPixel px, int firstDay, int lastDay)
        {
            var totals = new Dictionary<int, double>();
            var bad = new HashSet<int>();
            var prcp = px.Series("prcp");
            for (int i = 0; i < px.Days.Count; i++)
            {
                int day = px.Days[i];
                if (day < firstDay || day > lastDay) continue;
                int m = CivilDay.LabelMonth(day);
                if (m > 6) continue;   // October (0) to April (6)
                int y = CivilDay.LabelYear(day);
                if (px.RejectedDays.Contains(day)) { bad.Add(y); continue; }
                totals[y] = (totals.TryGetValue(y, out double t) ? t : 0) + prcp[i];
            }
            foreach (int y in bad) totals[y] = double.NaN;
            return totals;
        }

        static void OutlierWinters(DaymetPixel reference, WeatherSourceRecord record, WeatherQcLog log)
        {
            var totals = WinterTotals(reference, record.FirstDay, record.LastDay).Where(kv => !double.IsNaN(kv.Value)).ToList();
            if (totals.Count < 5) return;
            double median = Median(totals.Select(kv => kv.Value));
            double mad = Median(totals.Select(kv => Math.Abs(kv.Value - median)));
            var odd = totals.Where(kv => mad > 0 && Math.Abs(kv.Value - median) > WeatherQcLimits.OutlierWinterMads * mad)
                            .OrderBy(kv => kv.Key).ToList();
            string list = string.Join(", ", odd.Select(kv => string.Format(CultureInfo.InvariantCulture, "{0}–{1:00} {2:F1} in",
                                                                         kv.Key, (kv.Key + 1) % 100, kv.Value / 25.4)));
            log.Add(Source, "outlier winters", totals.Count, 0,
                    string.Format(CultureInfo.InvariantCulture, "median winter {0:F1} in{1}", median / 25.4, odd.Count > 0 ? "; far from it: " + list : ""),
                    odd.Count > 0 ? "reported" : "ok");
        }

        /// <summary>
        /// Each lattice pixel's mean winter liquid regressed on its Daymet elevation and grid x, y; a residual past 3σ is
        /// reported (a pixel that disagrees with its neighbours more than elevation explains).
        /// </summary>
        static void LatticeCoherence(WeatherSourceRecord record, WeatherQcLog log)
        {
            int n = record.Lattice.Count;
            if (n < 8) { log.Report(Source, "lattice coherence", $"Only {n} lattice pixels: not checked."); return; }
            var rows = new List<double[]>();
            var ys = new List<double>();
            for (int k = 0; k < n; k++)
            {
                var t = WinterTotals(record.Lattice[k], record.FirstDay, record.LastDay).Values.Where(v => !double.IsNaN(v)).ToList();
                if (t.Count == 0) continue;
                var (x, y) = record.LatticeCentres[k];
                rows.Add(new[] { 1, record.Lattice[k].ElevationMetres / 1000, (x - record.LatticeCentres[0].X) / 1000, (y - record.LatticeCentres[0].Y) / 1000 });
                ys.Add(t.Average());
            }
            var beta = LeastSquares.Solve(rows, ys);
            var residuals = rows.Select((r, i) => ys[i] - LeastSquares.Dot(beta, r)).ToList();
            double sd = Math.Sqrt(residuals.Sum(e => e * e) / Math.Max(1, residuals.Count - 4));
            int odd = residuals.Count(e => sd > 0 && Math.Abs(e) > WeatherQcLimits.LatticeResidualSigma * sd);
            log.Add(Source, "lattice coherence", residuals.Count, 0,
                    string.Format(CultureInfo.InvariantCulture, "winter liquid {0:+0.0;−0.0} in per 1,000 m; residual σ {1:F1} in; {2} pixel(s) past {3}σ",
                                  beta[1] / 25.4, sd / 25.4, odd, WeatherQcLimits.LatticeResidualSigma),
                    odd > 0 ? "reported" : "ok");
        }

        public static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToList();
            if (v.Count == 0) return double.NaN;
            return v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
        }
    }

    /// <summary>Ordinary least squares by the normal equations (a handful of columns), for the QC and calibration regressions.</summary>
    public static class LeastSquares
    {
        public static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
        }

        /// <summary>β minimising |Xβ − y|²; a singular system gives zeros past the first column.</summary>
        public static double[] Solve(IList<double[]> x, IList<double> y)
        {
            int p = x[0].Length;
            var a = new double[p, p + 1];
            for (int r = 0; r < x.Count; r++)
                for (int i = 0; i < p; i++)
                {
                    for (int j = 0; j < p; j++) a[i, j] += x[r][i] * x[r][j];
                    a[i, p] += x[r][i] * y[r];
                }
            for (int c = 0; c < p; c++)
            {
                int pivot = c;
                for (int r = c + 1; r < p; r++) if (Math.Abs(a[r, c]) > Math.Abs(a[pivot, c])) pivot = r;
                if (Math.Abs(a[pivot, c]) < 1e-12) continue;
                for (int j = 0; j <= p; j++) { double t = a[c, j]; a[c, j] = a[pivot, j]; a[pivot, j] = t; }
                for (int r = 0; r < p; r++)
                {
                    if (r == c) continue;
                    double f = a[r, c] / a[c, c];
                    for (int j = c; j <= p; j++) a[r, j] -= f * a[c, j];
                }
            }
            var beta = new double[p];
            for (int i = 0; i < p; i++) beta[i] = Math.Abs(a[i, i]) < 1e-12 ? 0 : a[i, p] / a[i, i];
            return beta;
        }
    }
}
