using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Weather;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A1, the season report (demo 112): every weather site's frozen rows, as the game ships them, played for
    /// <see cref="Seeds"/> seasons at the game's clock through <see cref="SnowEnergy"/> on open flat ground at the site's
    /// base, mid and summit reference heights. Writes the depth through the winter and the first day each height reaches
    /// 6 in and 12 in (SN-20's cover lines) to <c>test-results/snow-pillows/season.json</c>, which the pillow report draws.
    /// A report, not a gate: it shows whether a mountain can open in December on natural snow (plan question 3).
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowSeasonReportTests
    {
        const int Seeds = 20, SeasonDays = 304;   // October 1 to July 31
        const int OctoberFirst = 274;           // day of the year in a common year

        [Test]
        public void WriteTheSeasonReport()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"seeds\":" + Seeds + ",\"sites\":[\n");
            var tuning = WeatherTuning.Default;
            double faceHour = (double)tuning.CalendarDays / tuning.FaceDays;
            int faceSeconds = (int)Math.Round(faceHour * 3600);
            var sites = WeatherFixtures.Sites;
            for (int si = 0; si < sites.Length; si++)
            {
                string site = sites[si];
                var rows = SimWeatherTests.Frozen(site);
                var (lat, lon, name) = SiteOf(site);
                int[] feet = { rows.BaseFeet, rows.MidFeet, rows.SummitFeet };
                var depth = new double[3][][];
                for (int z = 0; z < 3; z++) depth[z] = new double[Seeds][];
                var gen = new WeatherGenerator(rows, tuning);
                var h = new WeatherHour();
                int hours = WeatherGenerator.HoursInYear(tuning);
                for (int s = 0; s < Seeds; s++)
                {
                    gen.Reset(0x534E_4F57_0000_0000UL + (ulong)s, 2026);
                    var packs = new SnowPack[3];
                    for (int z = 0; z < 3; z++) depth[z][s] = new double[SeasonDays];
                    for (int i = 0; i < hours; i++)
                    {
                        gen.Step(ref h);
                        int day = (int)((i + 0.5) * faceHour / 24);
                        if (day >= SeasonDays) break;
                        int doy = (OctoberFirst + day - 1) % 365 + 1;
                        var sun = SunVector.At(lat, lon, doy, h.HourOfDay * 3600 + 1800);
                        var (beam, diffuse) = SlopeSun.At(sun, sun.AboveHorizon ? h.Clearness : 0, 0, 0, SunVector.One, 1000, true);
                        for (int z = 0; z < 3; z++)
                        {
                            var p = WeatherPoint.At(h, rows.ZrefFeet, feet[z]);
                            int wb = WeatherAir.WetBulb(p.Temp, p.Dew, feet[z]);
                            int water = Math.Max(0, p.Liquid) * 254;
                            int snow = water > 0 ? water * WeatherAir.SnowPermille(wb) / 1000 : 0;
                            var x = new SnowInput
                            {
                                Seconds = faceSeconds, Air = p.Temp, Dew = p.Dew, WetBulb = wb, Feet = feet[z],
                                Wind = h.Wind * 850 / 1000, Cloud = h.Cloud, Beam = beam, Diffuse = diffuse, OpenSky = 1000, Canopy = 0,
                                Snow = snow, SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(wb), Rain = water - snow,
                            };
                            SnowEnergy.Step(ref packs[z], x, SnowTuning.Default);
                            depth[z][s][day] = packs[z].DepthMm / 25.4;
                        }
                    }
                }

                sb.Append(string.Format(inv, "{{\"site\":\"{0}\",\"name\":\"{1}\",\"heights\":[", site, name.Replace("\"", "'")));
                string[] words = { "base", "mid", "summit" };
                var line = new StringBuilder($"[season] {name}:");
                for (int z = 0; z < 3; z++)
                {
                    var d = depth[z];
                    int[] first6 = d.Select(x => First(x, 6)).ToArray(), first12 = d.Select(x => First(x, 12)).ToArray();
                    sb.Append(z == 0 ? "" : ",").Append(string.Format(inv, "{{\"label\":\"{0}\",\"feet\":{1},", words[z], feet[z]));
                    Series(sb, "median", Enumerable.Range(0, SeasonDays).Select(t => Quantile(d.Select(x => x[t]), 0.5)));
                    sb.Append(',');
                    Series(sb, "p10", Enumerable.Range(0, SeasonDays).Select(t => Quantile(d.Select(x => x[t]), 0.1)));
                    sb.Append(',');
                    Series(sb, "p90", Enumerable.Range(0, SeasonDays).Select(t => Quantile(d.Select(x => x[t]), 0.9)));
                    sb.Append(",\"first6\":[").Append(string.Join(",", first6)).Append("],\"first12\":[").Append(string.Join(",", first12)).Append("]}");
                    line.Append($" {words[z]} {feet[z]:N0} ft: 6 in by {Day(Median(first6))}, 12 in by {Day(Median(first12))};");
                }
                sb.Append("]}").Append(si + 1 < sites.Length ? ",\n" : "\n");
                Console.WriteLine(line.ToString().TrimEnd(';'));
            }
            sb.Append("]}\n");
            string dir = Path.Combine(RepoRoot(), "test-results", "snow-pillows");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "season.json"), sb.ToString());
            Console.WriteLine("[season] wrote " + Path.Combine(dir, "season.json"));
        }

        /// <summary>The first day (from October 1) the depth reaches <paramref name="inches"/> and stays for 3 days; −1 never.</summary>
        static int First(double[] depth, double inches)
        {
            for (int t = 0; t + 2 < depth.Length; t++)
                if (depth[t] >= inches && depth[t + 1] >= inches && depth[t + 2] >= inches) return t;
            return -1;
        }

        /// <summary>The median first day, a season that never gets there counting as last.</summary>
        static int Median(int[] days)
        {
            var v = days.Select(x => x < 0 ? int.MaxValue : x).OrderBy(x => x).ToArray();
            int m = v[v.Length / 2];
            return m == int.MaxValue ? -1 : m;
        }

        static string Day(int d) => d < 0 ? "never (median)" : new DateTime(2025, 10, 1).AddDays(d).ToString("MMM d", CultureInfo.InvariantCulture);

        static double Quantile(IEnumerable<double> values, double q)
        {
            var v = values.OrderBy(x => x).ToArray();
            double pos = q * (v.Length - 1);
            int lo = (int)Math.Floor(pos), hi = Math.Min(v.Length - 1, lo + 1);
            return v[lo] + (v[hi] - v[lo]) * (pos - lo);
        }

        static void Series(StringBuilder sb, string key, IEnumerable<double> v) =>
            sb.Append('"').Append(key).Append("\":[").Append(string.Join(",", v.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture)))).Append(']');

        static (int LatMicro, int LonMicro, string Name) SiteOf(string site)
        {
            string json = Encoding.UTF8.GetString(TestData.Bytes(TestData.Folder("weather/" + site), "site.json"));
            double Num(string key) => double.Parse(Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?[0-9.]+)").Groups[1].Value, CultureInfo.InvariantCulture);
            string name = Regex.Match(json, "\"Name\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value;
            return ((int)Math.Round(Num("Latitude") * 1e6), (int)Math.Round(Num("Longitude") * 1e6), name);
        }

        /// <summary>The repository: TestData's parent, as the pillow check finds it.</summary>
        static string RepoRoot() => Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName;
    }
}
