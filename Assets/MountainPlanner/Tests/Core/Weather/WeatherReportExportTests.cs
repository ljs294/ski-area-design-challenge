using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 review aid (explicit): writes CSVs for the owner's charts to MP_WEATHER_EXPORT. Per site, the October–April
    /// snowfall and precipitation of 1,000 generated seasons and of the record's 20 winters, and three generated seasons
    /// (10th, 50th and 90th percentile of snowfall) plus the record's driest, middle and biggest winters, week by week.
    /// Snow comes from the same function for both (WeatherPoint at the reference elevation), so they compare like for like.
    /// MP_WEATHER_EXTRA names a recording folder for one more site (a spot check, not a fixture).
    /// </summary>
    [Explicit("writes review CSVs")]
    public sealed class WeatherReportExportTests
    {
        const int Seeds = 1000, Weeks = 30;   // October 1 – April 28

        [Test]
        public void Export()
        {
            string outDir = Environment.GetEnvironmentVariable("MP_WEATHER_EXPORT") ?? "";
            Assume.That(outDir, Is.Not.Empty, "set MP_WEATHER_EXPORT");
            Directory.CreateDirectory(outDir);
            var sites = WeatherFixtures.Sites.Select(s => (Name: s, Record: WeatherFixtures.Raw(s))).ToList();
            string extra = Environment.GetEnvironmentVariable("MP_WEATHER_EXTRA") ?? "";
            if (extra.Length > 0) sites.Add((Path.GetFileName(extra), WeatherFetch.LoadRecordingAsync(extra).GetAwaiter().GetResult()));

            var seasons = new StringBuilder("site,source,season,snow_in,precip_in\n");
            var weekly = new StringBuilder("site,source,label,week,snow_in,precip_in,rain_in,temp_f,storms\n");
            var summary = new StringBuilder("site,zref_ft,record_snow_mean,record_precip_mean,game_snow_mean,game_precip_mean,record_snow_cv,game_snow_cv,game_p01,game_p10,game_p50,game_p90,game_p99,record_min,record_max,dec_storms_p05,dec_storms_p95\n");
            foreach (var (name, record) in sites)
            {
                var log = new WeatherQcLog();
                DaymetScreen.Apply(record, log); PowerScreen.Apply(record, log); SnotelScreen.Apply(record, log);
                var blend = WeatherBlend.Build(record);
                var st = record.Site;
                var (rows, rep) = WeatherCalibration.Build(blend, record, new ReferencePoints(st.BaseFeet, st.SummitFeet), WeatherTuning.Default);
                TestContext.Progress.WriteLine($"{name}: QC {WeatherQcLog.Name(log.Verdict)}; zref {rows.ZrefFeet} ft; storms {rep.Storms}; " +
                    $"KS max {rep.Ks.Values.Max():F3}; limited: {string.Join("; ", rep.LimitedReasons)}");

                // The record, winter by winter and week by week.
                var states = WeatherStates.Find(blend);
                var recWeeks = new SortedDictionary<int, Week[]>();
                // The hour's liquid in 0.01 in carries its remainder on: rounded hour by hour, drizzle under 0.005 in an hour
                // never counted and the record's light-precipitation months read dry.
                double carry = 0;
                for (int d = 0; d < blend.DayCount; d++)
                {
                    int y = states.DayLabelYear[d], day = states.DayOfLabelYear[d], w = day / 7;
                    if (w >= Weeks) continue;
                    if (!recWeeks.TryGetValue(y, out var arr)) recWeeks[y] = arr = Enumerable.Range(0, Weeks).Select(_ => new Week()).ToArray();
                    for (int hh = 0; hh < 24; hh++)
                    {
                        int k = d * 24 + blend.LagHours + hh;
                        if (k >= blend.HourCount || float.IsNaN(blend.T[k])) continue;
                        int temp = (int)Math.Round((blend.T[k] * 1.8 + 32) * 10);
                        int dew = float.IsNaN(blend.Td[k]) ? temp - 20 : (int)Math.Round((blend.Td[k] * 1.8 + 32) * 10);
                        if (!float.IsNaN(blend.P[k])) carry += blend.P[k] / 0.254;
                        int liquid = (int)Math.Round(carry);
                        carry -= liquid;
                        arr[w].Add(WeatherPoint.From(temp, Math.Min(temp, dew), liquid), temp);
                    }
                }
                foreach (var s in states.Storms)
                {
                    int day = states.DayOfLabelYear[Math.Min(states.DayOfLabelYear.Length - 1, s.Start / 24)];
                    if (day / 7 < Weeks && recWeeks.TryGetValue(s.LabelYear, out var arr)) arr[day / 7].Storms++;
                }
                var recSeasons = recWeeks.Select(kv => (Year: kv.Key, Snow: kv.Value.Sum(x => x.Snow), Precip: kv.Value.Sum(x => x.Precip), Weeks: kv.Value)).ToList();
                foreach (var r in recSeasons) seasons.AppendLine(Csv(name, "record", r.Year, r.Snow, r.Precip));

                // The game, 1,000 seasons.
                var gen = new WeatherGenerator(rows, WeatherTuning.Default);
                int hours = WeatherGenerator.HoursInYear(WeatherTuning.Default);
                double realPerFace = WeatherTuning.Default.CalendarDays / (double)WeatherTuning.Default.FaceDays;
                var h = new WeatherHour();
                var game = new List<(int Seed, double Snow, double Precip, Week[] Weeks, int DecStorms)>();
                for (int s = 0; s < Seeds; s++)
                {
                    gen.Reset(0x5245_5649_4557_0000UL + (ulong)s, 2026);
                    var arr = Enumerable.Range(0, Weeks).Select(_ => new Week()).ToArray();
                    int ordinal = -1, dec = 0;
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref h);
                        int w = (int)(i * realPerFace / 24 / 7);
                        if (w >= Weeks) break;
                        // A face hour stands for C ÷ S real hours of weather: its liquid is already the whole stretch's.
                        arr[w].Add(WeatherPoint.From(h.Temp, h.Dew, h.Liquid), h.Temp, realPerFace);
                        if (h.StormOrdinal >= 0 && h.StormOrdinal != ordinal) { ordinal = h.StormOrdinal; arr[w].Storms++; if (h.Third / 3 == 2) dec++; }
                    }
                    game.Add((s, arr.Sum(x => x.Snow), arr.Sum(x => x.Precip), arr, dec));
                }
                foreach (var g in game) seasons.AppendLine(Csv(name, "game", g.Seed, g.Snow, g.Precip));

                var bySnow = game.OrderBy(g => g.Snow).ToList();
                double P(double q) => bySnow[(int)Math.Min(bySnow.Count - 1, Math.Floor(q * bySnow.Count))].Snow;
                foreach (var (label, q) in new[] { ("Game: low (10th percentile)", 0.10), ("Game: typical (median)", 0.50), ("Game: big (90th percentile)", 0.90) })
                {
                    var g = bySnow[(int)(q * bySnow.Count)];
                    for (int w = 0; w < Weeks; w++) weekly.AppendLine(WeekCsv(name, "game", label, w, g.Weeks[w]));
                }
                var recBySnow = recSeasons.OrderBy(r => r.Snow).ToList();
                foreach (var (label, idx) in new[] { ("Record: lowest winter", 0), ("Record: middle winter", recBySnow.Count / 2), ("Record: biggest winter", recBySnow.Count - 1) })
                {
                    var r = recBySnow[idx];
                    for (int w = 0; w < Weeks; w++) weekly.AppendLine(WeekCsv(name, "record", $"{label} ({r.Year}–{(r.Year + 1) % 100:00})", w, r.Weeks[w]));
                }
                double Cv(IEnumerable<double> x) { var a = x.ToArray(); double m = a.Average(); return Math.Sqrt(a.Sum(v => (v - m) * (v - m)) / (a.Length - 1)) / m; }
                var decs = game.Select(g => g.DecStorms).OrderBy(x => x).ToList();
                summary.AppendLine(string.Join(",", name, rows.ZrefFeet.ToString(CultureInfo.InvariantCulture),
                    F(recSeasons.Average(r => r.Snow)), F(recSeasons.Average(r => r.Precip)), F(game.Average(g => g.Snow)), F(game.Average(g => g.Precip)),
                    F(Cv(recSeasons.Select(r => r.Snow))), F(Cv(game.Select(g => g.Snow))),
                    F(P(0.01)), F(P(0.10)), F(P(0.50)), F(P(0.90)), F(P(0.99)), F(recBySnow[0].Snow), F(recBySnow[recBySnow.Count - 1].Snow),
                    decs[(int)(0.05 * decs.Count)].ToString(CultureInfo.InvariantCulture), decs[(int)(0.95 * decs.Count)].ToString(CultureInfo.InvariantCulture)));
            }
            File.WriteAllText(Path.Combine(outDir, "seasons.csv"), seasons.ToString());
            File.WriteAllText(Path.Combine(outDir, "weekly.csv"), weekly.ToString());
            File.WriteAllText(Path.Combine(outDir, "summary.csv"), summary.ToString());
            TestContext.Progress.WriteLine(summary.ToString());
        }

        sealed class Week
        {
            public double Snow, Precip, Rain, TempSum, TempN;
            public int Storms;
            public void Add(WeatherPoint p, int temp, double weight = 1)
            {
                Snow += p.SnowTenthsIn / 10.0; Precip += p.Liquid / 100.0; Rain += p.Liquid * (1 - p.SnowShare) / 100.0;
                TempSum += temp / 10.0 * weight; TempN += weight;
            }
        }

        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        static string Csv(string site, string source, int season, double snow, double precip) =>
            string.Join(",", site, source, season.ToString(CultureInfo.InvariantCulture), F(snow), F(precip));
        static string WeekCsv(string site, string source, string label, int week, Week w) =>
            string.Join(",", site, source, "\"" + label + "\"", week.ToString(CultureInfo.InvariantCulture), F(w.Snow), F(w.Precip), F(w.Rain), F(w.TempN > 0 ? w.TempSum / w.TempN : double.NaN), w.Storms.ToString(CultureInfo.InvariantCulture));
    }
}
