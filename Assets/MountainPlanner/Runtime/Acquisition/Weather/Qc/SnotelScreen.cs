#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition.Weather.Qc
{
    /// <summary>One station's winter (November to April) after screening, against Daymet's reference pixel.</summary>
    public sealed class SnotelWinter
    {
        public int LabelYear;
        public int Days, CleanDays;
        /// <summary>Inches: the PREC accumulator's increments, the SWE gains, Daymet's liquid on the same days.</summary>
        public double Prec, SweGain, Daymet;
        public double PeakSwe;
        public int PeakDay;
        public bool Usable, Undercatch;
        public double SnotelPrecip => Math.Max(Prec, SweGain);
        public double Ratio => SnotelPrecip > 0 ? Daymet / SnotelPrecip : double.NaN;
    }

    /// <summary>
    /// SNOTEL's screen (QC plan: "SNOTEL is notoriously janky"). SNOTEL is never an input to the rows (WX-10); this screen
    /// exists so the cross-check (WX-07 v3: Daymet ÷ SNOTEL winter precipitation, flagged outside 0.7–1.4) means something.
    /// It removes, never repairs; it compares medians of per-winter ratios, so one bad winter can't decide a flag; and it
    /// uses only WTEQ and PREC (SNOTEL's temperatures carry a known sensor-change bias and are never read).
    /// </summary>
    public static class SnotelScreen
    {
        public const string Source = "SNOTEL";
        const double MmPerIn = 25.4;

        public static List<WeatherStationInfo> Apply(WeatherSourceRecord record, WeatherQcLog log, Dictionary<string, List<SnotelWinter>>? winters = null)
        {
            var infos = new List<WeatherStationInfo>();
            if (record.Snotel.Count == 0)
            {
                log.Report(Source, "stations", record.SnotelNote.Length > 0 ? record.SnotelNote : "No station.");
                return infos;
            }
            var daymet = DaymetDays(record);
            var ratios = new List<double>();
            foreach (var s in record.Snotel)
            {
                var w = Screen(s, daymet, record.FirstLabelYear, record.LastLabelYear, log);
                winters?.Add(s.Station.Triplet, w);
                var usable = w.Where(x => x.Usable && !double.IsNaN(x.Ratio)).ToList();
                var info = new WeatherStationInfo
                {
                    Triplet = s.Station.Triplet, Name = s.Station.Name, DistanceMetres = Math.Round(s.Station.DistanceMetres),
                    ElevationMetres = Math.Round(s.Station.ElevationFeet * 0.3048), GhcnId = s.Station.GhcnId,
                    UsableWinters = usable.Count, UndercatchWinters = usable.Count(x => x.Undercatch),
                };
                if (usable.Count >= WeatherQcLimits.SnotelMinWinters)
                {
                    info.MedianRatio = Math.Round(DaymetScreen.Median(usable.Select(x => x.Ratio)), 3);
                    ratios.Add(info.MedianRatio);
                }
                else info.Note = $"Only {usable.Count} clean winters (needs {WeatherQcLimits.SnotelMinWinters}): no ratio.";
                if (info.GhcnId.Length > 0)
                    info.Note = (info.Note + " Daymet's inputs (GHCN-Daily " + info.GhcnId + ") include this station, so agreement is weak evidence.").Trim();
                infos.Add(info);
            }
            if (ratios.Count == 0)
            {
                log.Report(Source, "ratio", "Not enough clean SNOTEL winters: no comparison, no flag.");
                return infos;
            }
            double median = DaymetScreen.Median(ratios);
            string note = string.Format(CultureInfo.InvariantCulture, "Daymet ÷ SNOTEL winter precipitation, median over {0} station(s): {1:F2}", ratios.Count, median);
            if (median < WeatherQcLimits.SnotelRatioLow || median > WeatherQcLimits.SnotelRatioHigh)
                log.Flag(Source, "ratio", note + string.Format(CultureInfo.InvariantCulture, ", outside {0:F1}–{1:F1}: the site's anchor may be off.",
                                                             WeatherQcLimits.SnotelRatioLow, WeatherQcLimits.SnotelRatioHigh));
            else log.Add(Source, "ratio", ratios.Count, 0, note);
            return infos;
        }

        public struct DaymetDay
        {
            public double PrcpIn, TmaxC;
        }

        static Dictionary<int, DaymetDay> DaymetDays(WeatherSourceRecord record)
        {
            var map = new Dictionary<int, DaymetDay>();
            var r = record.Reference;
            if (r == null) return map;
            var prcp = r.Series("prcp");
            var tmax = r.Series("tmax");
            for (int i = 0; i < r.Days.Count; i++)
                if (!r.RejectedDays.Contains(r.Days[i])) map[r.Days[i]] = new DaymetDay { PrcpIn = prcp[i] / MmPerIn, TmaxC = tmax[i] };
            return map;
        }

        /// <summary>Screens one station and sums its winters. Internal days and flags as the service gave them.</summary>
        public static List<SnotelWinter> Screen(SnotelSeries s, Dictionary<int, DaymetDay> daymet, int firstLabelYear, int lastLabelYear, WeatherQcLog log)
        {
            var days = s.Days.Keys.ToList();
            int n = days.Count;
            var prec = new double[n];
            var swe = new double[n];
            var precOk = new bool[n];
            var sweOk = new bool[n];
            long flagged = 0;
            for (int i = 0; i < n; i++)
            {
                var d = s.Days[days[i]];
                prec[i] = d.Prec;
                swe[i] = d.Wteq;
                precOk[i] = !double.IsNaN(d.Prec) && GoodFlags(d.PrecQc, d.PrecQa);
                sweOk[i] = !double.IsNaN(d.Wteq) && d.Wteq >= 0 && GoodFlags(d.WteqQc, d.WteqQa);
                if (!double.IsNaN(d.Prec) && !precOk[i]) flagged++;
                if (!double.IsNaN(d.Wteq) && !sweOk[i]) flagged++;
            }
            string tag = s.Station.Triplet;
            log.Add(Source, "flags " + tag, 2L * n, flagged, "kept NRCS valid (V) and edited (E) values, approved or provisional; dropped suspect, estimated and raw");

            // PREC: the water-year accumulator, read by its high-water mark.
            var precInc = new double[n];
            long resets = 0, spikes = 0;
            double hw = double.NaN;
            for (int i = 0; i < n; i++)
            {
                if (!precOk[i]) continue;
                if (double.IsNaN(hw)) { hw = prec[i]; continue; }
                double v = prec[i];
                if (v < hw - WeatherQcLimits.SnotelResetIn) { hw = v; resets++; continue; }
                if (v <= hw) continue;   // evaporation and antifreeze noise: zero
                double inc = v - hw;
                if (inc > WeatherQcLimits.SnotelSpikeIn && FallsBack(prec, precOk, i, hw + inc / 2, days))
                {
                    precOk[i] = false;
                    spikes++;
                    continue;
                }
                precInc[i] = inc;
                hw = v;
            }
            log.Add(Source, "PREC accumulator " + tag, n, spikes, $"{resets} reset(s) read as resets, never as negative rain; spikes over {WeatherQcLimits.SnotelSpikeIn} in dropped");

            // SWE: jumps without precipitation, midwinter drops in the cold, a stuck pillow.
            long jumps = 0, drops = 0, stuck = 0;
            var sweInc = new double[n];
            for (int i = 1; i < n; i++)
            {
                if (!sweOk[i] || !sweOk[i - 1] || days[i] != days[i - 1] + 1) continue;
                double inc = swe[i] - swe[i - 1];
                double rain = Daymet(daymet, days[i]).PrcpIn + Daymet(daymet, days[i] - 1).PrcpIn;
                if (inc > WeatherQcLimits.SnotelSweJumpIn && rain < inc / 2) { sweOk[i] = false; jumps++; continue; }
                int m = CivilDay.LabelMonth(days[i]);
                if (inc < -WeatherQcLimits.SnotelSweDropIn && m >= 2 && m <= 4
                    && Daymet(daymet, days[i]).TmaxC < 0 && Daymet(daymet, days[i] - 1).TmaxC < 0)
                {
                    sweOk[i] = false;
                    drops++;
                    continue;
                }
                if (inc > 0) sweInc[i] = inc;
            }
            for (int i = 0; i < n;)
            {
                int j = i + 1;
                while (j < n && sweOk[i] && sweOk[j] && swe[j] == swe[i] && days[j] == days[j - 1] + 1) j++;
                int m = CivilDay.LabelMonth(days[i]);
                if (sweOk[i] && swe[i] > 0 && j - i >= WeatherQcLimits.SnotelStuckDays && m >= 2 && m <= 5)
                {
                    // The station's own gauge decides: a flat pillow in a dry spell is real (Sierra Januaries); a flat pillow
                    // while its own PREC rises is stuck. Daymet decides only where the gauge has no clean days in the run.
                    // The run's first day is the day the pillow reached its value: its gauge increment is the storm that
                    // put the snow there, so the evidence starts the day after.
                    double gauge = 0, fell = 0;
                    int gaugeDays = 0;
                    for (int k = i + 1; k < j; k++)
                    {
                        fell += Daymet(daymet, days[k]).PrcpIn;
                        if (precOk[k]) { gauge += precInc[k]; gaugeDays++; }
                    }
                    double evidence = gaugeDays * 2 >= j - i - 1 ? gauge : fell;
                    if (evidence >= WeatherQcLimits.SnotelStuckPrecipIn)
                    {
                        for (int k = i; k < j; k++) { sweOk[k] = false; sweInc[k] = 0; }
                        stuck += j - i;
                    }
                }
                i = j;
            }
            log.Add(Source, "SWE pillow " + tag, n, jumps + drops + stuck,
                    $"{jumps} jump(s) without precipitation, {drops} midwinter drop(s) in the cold (bridging), {stuck} stuck day(s)");

            // Winters, November to April.
            var winters = new List<SnotelWinter>();
            for (int y = firstLabelYear; y <= lastLabelYear; y++)
            {
                int first = CivilDay.Of(y, 11, 1), last = CivilDay.Of(y + 1, 4, 30);
                var w = new SnotelWinter { LabelYear = y, Days = last - first + 1 };
                for (int i = 0; i < n; i++)
                {
                    int day = days[i];
                    if (day < first || day > last) continue;
                    if (precOk[i] && sweOk[i]) w.CleanDays++;
                    w.Prec += precInc[i];
                    w.SweGain += sweInc[i];
                    if (sweOk[i] && swe[i] > w.PeakSwe) { w.PeakSwe = swe[i]; w.PeakDay = day; }
                }
                for (int day = first; day <= last; day++) w.Daymet += Daymet(daymet, day).PrcpIn;
                w.Usable = w.CleanDays >= w.Days * WeatherQcLimits.SnotelUsableDayShare;
                w.Undercatch = w.SweGain > 0 && w.Prec < (1 - WeatherQcLimits.SnotelUndercatchShare) * w.SweGain;
                winters.Add(w);
            }
            int usable = winters.Count(w => w.Usable), under = winters.Count(w => w.Usable && w.Undercatch);
            log.Add(Source, "winters " + tag, winters.Count, winters.Count - usable,
                    $"usable with ≥ {WeatherQcLimits.SnotelUsableDayShare:P0} clean days: {usable}; gauge undercatch (PREC below the SWE gain) in {under}");
            return winters;
        }

        static DaymetDay Daymet(Dictionary<int, DaymetDay> map, int day) => map.TryGetValue(day, out var d) ? d : default;

        /// <summary>NRCS's flags: QC valid or edited; QA approved, provisional or unstated.</summary>
        public static bool GoodFlags(char qc, char qa) => (qc == 'V' || qc == 'E') && (qa == 'A' || qa == 'P' || qa == ' ');

        static bool FallsBack(double[] v, bool[] ok, int i, double below, List<int> days)
        {
            for (int k = i + 1; k < v.Length && days[k] - days[i] <= WeatherQcLimits.SnotelSpikeDays; k++)
                if (ok[k] && v[k] < below) return true;
            return false;
        }
    }
}
