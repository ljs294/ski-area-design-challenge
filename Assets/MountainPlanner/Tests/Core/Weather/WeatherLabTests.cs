using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The Weather Lab's data (owner, 2026-11-09: "save this web page as the weather lab for looking at future changes"):
    /// every site's frozen rows played as the game would, 20 sample seasons week by week plus the range of 200, and the
    /// record's own winters at zref through the same illustrative pack. Not a check: it writes the JSON that
    /// <c>tools/weather-lab/build.mjs</c> turns into the page (demo 97). Explicit, so no suite runs it.
    /// </summary>
    [Explicit, Category("WeatherLab")]
    public sealed class WeatherLabTests
    {
        const int Samples = 20, BandSeeds = 200;
        const int FaceDays = 87;                        // October 1 to about June 9: 29 game weeks of 3 face days
        const ulong SampleSeed = 0x5341_4D50_0000_0000UL;

        static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            [WeatherFixtures.JacksonHole] = "Jackson Hole", [WeatherFixtures.OlympicValley] = "Olympic Valley",
            [WeatherFixtures.StevensPass] = "Stevens Pass", [WeatherFixtures.WinterPark] = "Winter Park",
            [WeatherFixtures.JacksonNh] = "Jackson NH", [WeatherFixtures.Snowshoe] = "Snowshoe", [WeatherFixtures.SnowRidge] = "Snow Ridge",
        };

        /// <summary>
        /// An illustrative snowpack for the lab (4.07 owns the game's): the reference model's water (snowpack temperature
        /// and a melt factor that follows the sun's season, as <c>WeatherFidelityTests.ReferenceSnow</c>) plus a depth that
        /// settles toward a density of 0.30 cold, 0.38 near freezing and 0.45 wet.
        /// </summary>
        sealed class Pack
        {
            const double MeltWinter = 0.0011, MeltSummer = 0.0044, IceHeat = 0.0035, Follow = 48;
            public double Swe, Temp = 32, Depth;

            public void Step(double snowWater, double newDepth, double tempF, int dayOfYear, double hours, bool rain)
            {
                if (snowWater > 0) { Temp = (Temp * Swe + Math.Min(32, tempF) * snowWater) / (Swe + snowWater); Swe += snowWater; Depth += newDepth; }
                if (Swe <= 0) { Swe = 0; Temp = 32; Depth = 0; return; }
                if (tempF < 32) Temp += (tempF - Temp) * Math.Min(1, hours / Follow);
                double factor = MeltWinter + (MeltSummer - MeltWinter) * (1 - Math.Cos(2 * Math.PI * (dayOfYear + 10) / 365.0)) / 2;
                double energy = factor * hours * Math.Max(0, tempF - 32), cold = Swe * IceHeat * (32 - Temp);
                double rho = Depth > 0 ? Swe / Depth : 0.3;
                if (energy <= cold) Temp += energy / (Swe * IceHeat);
                else { Swe = Math.Max(0, Swe - (energy - cold)); Temp = 32; }
                bool wet = tempF >= 32 || rain;
                double target = wet ? 0.45 : tempF >= 28 ? 0.38 : 0.30, tau = wet ? 36 : 96;
                if (rho < target) rho += (target - rho) * (1 - Math.Exp(-hours / tau));
                if (Swe <= 0.001) { Swe = 0; Temp = 32; Depth = 0; }
                else Depth = Swe / rho;
            }
        }

        static string F(double v, int digits = 1) => Math.Round(v, digits).ToString(CultureInfo.InvariantCulture);

        [Test]
        public void WriteTheLabData()
        {
            string rowsRoot = TestData.Folder("weather");
            string output = Environment.GetEnvironmentVariable("WEATHER_LAB_OUT");
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Directory.GetParent(Directory.GetParent(rowsRoot).FullName).FullName, "Temp", "weather-lab", "data.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            var json = new StringBuilder("{\"sites\":[");
            for (int i = 0; i < WeatherFixtures.Sites.Length; i++)
            {
                string site = WeatherFixtures.Sites[i];
                if (i > 0) json.Append(',');
                Site(site, json);
                TestContext.Progress.WriteLine($"  {Names[site]} done");
            }
            json.Append("]}");
            File.WriteAllText(output, json.ToString());
            TestContext.Progress.WriteLine($"Weather Lab data: {output} ({json.Length / 1024} KB)");
        }

        static void Site(string site, StringBuilder json)
        {
            byte[] bytes = TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName);
            var rows = WeatherRowsFile.FromBytes(bytes);   // the frozen rows: what the game plays
            var tuning = WeatherTuning.Default;
            double fh = (double)tuning.CalendarDays / tuning.FaceDays;
            int[] feet = { rows.BaseFeet, rows.ZrefFeet, rows.MidFeet, rows.SummitFeet };
            string[] names = { "base", "ref", "mid", "summit" };
            int weeks = FaceDays / 3, hours = FaceDays * 24;

            json.Append("{\"id\":\"").Append(site).Append("\",\"name\":\"").Append(Names[site]).Append("\",\"rows\":\"")
                .Append(WeatherRowsFile.Sha256(bytes).Substring(0, 12)).Append("\",\"faceHour\":").Append(F(fh, 4))
                .Append(",\"feet\":[").Append(string.Join(",", feet)).Append("],\"samples\":[");

            var bandDepth = new List<double>[4, weeks];
            var bandSnow = new List<double>[4, weeks];
            for (int e = 0; e < 4; e++)
                for (int w = 0; w < weeks; w++) { bandDepth[e, w] = new List<double>(); bandSnow[e, w] = new List<double>(); }

            var gen = new WeatherGenerator(rows, tuning);
            var h = new WeatherHour();
            for (int s = 0; s < BandSeeds; s++)
            {
                gen.Reset(SampleSeed + (ulong)s, 2026);
                var packs = new[] { new Pack(), new Pack(), new Pack(), new Pack() };
                var weekSnow = new double[4];
                double[] newSnow = new double[4], rain = new double[4];
                double baseHi = -999, baseLo = 999, topHi = -999, topLo = 999, gust = 0, cloud = 0;
                int stormHours = 0;
                bool detail = s < Samples;
                var days = new StringBuilder();
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref h);
                    int dayOfYear = (274 + (int)((i + 0.5) * fh / 24) - 1) % 365 + 1;
                    for (int e = 0; e < 4; e++)
                    {
                        var p = WeatherPoint.At(h, rows.ZrefFeet, feet[e]);
                        double snowWater = p.Liquid * p.SnowShare / 100.0, rainWater = p.Liquid * (1 - p.SnowShare) / 100.0;
                        packs[e].Step(snowWater, p.SnowTenthsIn / 10.0, p.Temp / 10.0, dayOfYear, fh, rainWater > 0.005);
                        newSnow[e] += p.SnowTenthsIn / 10.0; rain[e] += rainWater; weekSnow[e] += p.SnowTenthsIn / 10.0;
                        if (e == 0) { baseHi = Math.Max(baseHi, p.Temp / 10.0); baseLo = Math.Min(baseLo, p.Temp / 10.0); }
                        if (e == 3) { topHi = Math.Max(topHi, p.Temp / 10.0); topLo = Math.Min(topLo, p.Temp / 10.0); }
                    }
                    if (h.HourOfDay >= 8 && h.HourOfDay < 17) gust = Math.Max(gust, h.Wind / 10.0 * h.GustFactor / 1000.0);
                    cloud += h.Cloud / 1000.0;
                    if (h.State == WeatherRows.Storm) stormHours++;
                    if ((i + 1) % 24 != 0) continue;

                    int d = i / 24;
                    if (detail)
                    {
                        // Per face day: [new snow, rain, depth, water] × base, ref, mid, summit; base hi, lo; summit hi, lo; gust; cloud %; storm hours.
                        days.Append(d > 0 ? ",[" : "[");
                        for (int e = 0; e < 4; e++)
                            days.Append(F(newSnow[e])).Append(',').Append(F(rain[e], 2)).Append(',').Append(F(packs[e].Depth)).Append(',').Append(F(packs[e].Swe, 2)).Append(',');
                        days.Append(F(baseHi)).Append(',').Append(F(baseLo)).Append(',').Append(F(topHi)).Append(',').Append(F(topLo)).Append(',')
                            .Append(F(gust)).Append(',').Append(F(cloud / 24 * 100, 0)).Append(',').Append(stormHours).Append(']');
                    }
                    Array.Clear(newSnow, 0, 4); Array.Clear(rain, 0, 4);
                    baseHi = -999; baseLo = 999; topHi = -999; topLo = 999; gust = 0; cloud = 0; stormHours = 0;
                    if ((d + 1) % 3 == 0)
                    {
                        for (int e = 0; e < 4; e++) { bandDepth[e, d / 3].Add(packs[e].Depth); bandSnow[e, d / 3].Add(weekSnow[e]); }
                        Array.Clear(weekSnow, 0, 4);
                    }
                }
                if (detail) json.Append(s > 0 ? "," : "").Append("{\"seed\":").Append(s).Append(",\"days\":[").Append(days).Append("]}");
            }
            json.Append("],\"band\":{");
            for (int e = 0; e < 4; e++)
                json.Append(e > 0 ? "," : "").Append('"').Append(names[e]).Append("\":{\"depth\":").Append(Percentiles(bandDepth, e, weeks))
                    .Append(",\"snow\":").Append(Percentiles(bandSnow, e, weeks)).Append('}');
            json.Append("},\"record\":[");
            Record(site, fh, weeks, json);
            json.Append("]}");
        }

        static string Percentiles(List<double>[,] band, int e, int weeks)
        {
            var o = new StringBuilder("[");
            for (int w = 0; w < weeks; w++)
            {
                var l = band[e, w];
                l.Sort();
                double Q(double q) => l[(int)Math.Round(q * (l.Count - 1))];
                o.Append(w > 0 ? ",[" : "[").Append(F(Q(0.1))).Append(',').Append(F(Q(0.5))).Append(',').Append(F(Q(0.9))).Append(']');
            }
            return o.Append(']').ToString();
        }

        /// <summary>The record's winters at zref through the same pack, cut at the game weeks' ends in calendar time.</summary>
        static void Record(string site, double fh, int weeks, StringBuilder json)
        {
            var b = WeatherFixtures.Screened(site).Blend;
            bool first = true;
            for (int d0 = 0; d0 + 365 <= b.DayCount; d0++)
            {
                var date = CivilDay.Date(b.Days[d0]);
                if (date.Month != 10 || date.Day != 1) continue;
                var pack = new Pack();
                var row = new StringBuilder();
                double snow = 0;
                int w = 0;
                for (int k = d0 * 24; k < (d0 + 365) * 24 && w < weeks; k++)
                {
                    if (!float.IsNaN(b.T[k]))
                    {
                        int t = (int)Math.Round(Psychrometrics.CToF(b.T[k]) * 10);
                        int dew = float.IsNaN(b.Td[k]) ? t - 20 : (int)Math.Round(Psychrometrics.CToF(b.Td[k]) * 10);
                        int liquid = float.IsNaN(b.P[k]) ? 0 : (int)Math.Round(b.P[k] / 0.254);
                        var p = WeatherPoint.From(t, Math.Min(t, dew), liquid);
                        double rainWater = p.Liquid * (1 - p.SnowShare) / 100.0;
                        pack.Step(p.Liquid * p.SnowShare / 100.0, p.SnowTenthsIn / 10.0, t / 10.0, CivilDay.Date(b.Days[k / 24]).DayOfYear, 1, rainWater > 0.005);
                        snow += p.SnowTenthsIn / 10.0;
                    }
                    if (k - d0 * 24 + 1 >= (w + 1) * 3 * 24 * fh)
                    {
                        row.Append(w > 0 ? ",[" : "[").Append(F(pack.Depth)).Append(',').Append(F(snow)).Append(']');
                        snow = 0;
                        w++;
                    }
                }
                json.Append(first ? "" : ",").Append("{\"year\":").Append(date.Year).Append(",\"weeks\":[").Append(row).Append("]}");
                first = false;
            }
        }
    }
}
