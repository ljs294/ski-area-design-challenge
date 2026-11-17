using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Demo 111 (task 4.07 PR A2): the report's data. A season of live snow, from bare October 1 to April 30, on the 2 km
    /// round Jackson Hole's centre (the test package) and, when <c>MP_SNOW_SECOND</c> names one ("Area name|weather rows",
    /// read-only from <c>MP_SNOW_AREAS</c>), a second area: each face day the depth at the base, mid-mountain and the
    /// summit, the cover, the deepest cell and the ledger; the depth and type maps on five dates; and how the mean sampler
    /// moves the weather's terrain (SN-01 v2) against the old one post a cell. <c>tools/snow-a2/report.mjs</c> draws it.
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowA2ReportTests
    {
        const int W = 256, D = 256, Map = 128;
        static readonly (int Month, int Day, string Label)[] Dates =
            { (11, 15, "Nov 15"), (12, 15, "Dec 15"), (1, 15, "Jan 15"), (3, 1, "Mar 1"), (4, 15, "Apr 15") };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static void Say(string s) { TestContext.Progress.WriteLine(s); }

        [Test]
        public void WriteTheReportData()
        {
            string root = Path.GetFullPath(Path.Combine(TestData.Folder("formats"), "..", "..", "test-results", "snow-a2"));
            Directory.CreateDirectory(root);
            var sites = new List<string>();
            string jh = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(jh, "*.grid")) TestData.Bytes(jh, Path.GetFileName(f));
            sites.Add(Site("Jackson Hole", jh, WeatherFixtures.JacksonHole, new SimSite(43.59, -110.83)));

            string second = Environment.GetEnvironmentVariable("MP_SNOW_SECOND"), areas = Environment.GetEnvironmentVariable("MP_SNOW_AREAS");
            if (!string.IsNullOrEmpty(second) && !string.IsNullOrEmpty(areas) && Directory.Exists(areas))
            {
                var parts = second.Split('|');
                foreach (string dir in Directory.GetDirectories(areas))
                {
                    if (!File.Exists(Path.Combine(dir, "manifest.json"))) continue;
                    var man = Persistence.ResortPackage.ReadManifest(dir);
                    if (man.Site.Name != parts[0]) continue;
                    var site = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(Path.Combine(TestData.Folder("weather/" + parts[1]), "site.json")));
                    sites.Add(Site(parts[0], dir, parts[1], new SimSite((double)site["Latitude"], (double)site["Longitude"])));
                    break;
                }
                if (sites.Count == 1) Say($"[snow-a2] no area named {parts[0]} under {areas}: the report has Jackson Hole only");
            }
            string json = "{\"made\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv) + "\",\"sites\":[" + string.Join(",", sites) + "]}";
            File.WriteAllText(Path.Combine(root, "report.json"), json);
            Say($"[snow-a2] wrote {Path.Combine(root, "report.json")} ({json.Length / 1024} KB)");
        }

        static string Site(string name, string dir, string rows, SimSite place)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var m = SnowRunoutProbeTests.Crop(dir);
            var p = Pacing.Default;
            var w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), place, 2026, weather: SimWeather.Own(SimWeatherTests.Frozen(rows)));
            SnowFieldTests.SendInputs(w, m);

            // The bands: the lowest tenth of the cells, the middle tenth and the highest tenth by height.
            var byHeight = new int[W * D];
            for (int c = 0; c < byHeight.Length; c++) byHeight[c] = c;
            Array.Sort(byHeight, (a, b) => m.Mm[a] != m.Mm[b] ? m.Mm[a].CompareTo(m.Mm[b]) : a.CompareTo(b));
            int tenth = byHeight.Length / 10;
            int[] Band(int from) { var b = new int[tenth]; Array.Copy(byHeight, from, b, 0, tenth); return b; }
            int[] low = Band(0), mid = Band(byHeight.Length / 2 - tenth / 2), high = Band(byHeight.Length - tenth);
            double Feet(int[] b) { double s = 0; foreach (int c in b) s += m.Feet[c]; return s / b.Length; }
            double Depth(int[] b) { double s = 0; foreach (int c in b) s += w.Snow[c % W, c / W]; return s / b.Length / 25.4; }
            // SN-08 v2: the band's bulk density, kg/m³ (its water over its depth, µm of water a mm of snow), 0 with no snow.
            double Density(int[] b)
            {
                long swe = 0, depth = 0;
                foreach (int c in b) { var pk = w.Snow.PackAt(c % W, c / W); if (pk.Swe > 0) { swe += pk.Swe; depth += pk.DepthMm; } }
                return depth < 10L * b.Length / 100 ? 0 : (double)swe / depth;   // under 0.1 mm a cell on average: none to speak of
            }

            var days = new StringBuilder();
            int bad = 0;
            double cells = 1000.0 * W * D;
            void Day(SnowLedger l)
            {
                int j = l.Day % w.Calendar.YearFaceDays;
                w.JoinSnow();
                long sum = 0, deepest = 0; int covered = 0;
                for (int y = 0; y < D; y++)
                    for (int x = 0; x < W; x++)
                    {
                        long d = w.Snow[x, y];
                        sum += d; if (d > deepest) deepest = d; if (d >= 150) covered++;
                    }
                if (l.Balance != 0) bad++;
                if (days.Length > 0) days.Append(',');
                days.Append(string.Format(Inv,
                    "{{\"label\":\"{0} {1}\",\"base\":{2:F1},\"mid\":{3:F1},\"summit\":{4:F1},\"mean\":{5:F1},\"covered\":{6:F1},\"deepest\":{7:F0}," +
                    "\"fell\":{8:F2},\"canopy\":{9:F2},\"melt\":{10:F2},\"runoff\":{11:F2},\"sublimated\":{12:F2},\"water\":{13:F2},\"ground\":{14:F2},\"balance\":{15},\"offMap\":{16:F2},\"dBase\":{17:F0},\"dMid\":{18:F0},\"dSummit\":{19:F0}}}",
                    MonthName(w.Calendar.Month(j)), w.Calendar.Day(j), Depth(low), Depth(mid), Depth(high), sum / (double)(W * D) / 25.4,
                    100.0 * covered / (W * D), deepest / 25.4, l.Fell / cells, l.Canopy / cells, l.Melt / cells, l.Runoff / cells,
                    l.Sublimated / cells, l.ToWater / cells, l.End / cells, l.Balance, l.OffMap / cells, Density(low), Density(mid), Density(high)));
            }

            var maps = new StringBuilder();
            int saveBytes = 0;
            foreach (var (month, day, label) in Dates)
            {
                SnowFieldTests.RunTo(w, month, day, p.IdleTickFactor, Day);
                // A save in mid-winter: the whole sim sidecar, its snow block as the game writes it.
                if (month == 1) saveBytes = w.SaveState().Length;
                if (maps.Length > 0) maps.Append(',');
                var depth = new StringBuilder(); var type = new StringBuilder();
                for (int y = Map - 1; y >= 0; y--)   // north at the top
                    for (int x = 0; x < Map; x++)
                    {
                        long s = 0;
                        for (int b = 0; b < 2; b++) for (int a = 0; a < 2; a++) s += w.Snow[2 * x + a, 2 * y + b];
                        if (depth.Length > 0) { depth.Append(','); type.Append(','); }
                        depth.Append((s / 4.0 / 25.4).ToString("F1", Inv));
                        type.Append((int)w.Snow.TypeAt(2 * x, 2 * y));
                    }
                maps.Append($"{{\"label\":\"{label}\",\"size\":{Map},\"depth\":[{depth}],\"type\":[{type}]}}");
            }
            SnowFieldTests.RunTo(w, 4, 30, p.IdleTickFactor, Day);

            // SN-01 v2: the weather's terrain from the mean of each cell against one post at its centre.
            var old = SnowRunoutProbeTests.Crop(dir, nearest: true);
            int bins = 0, exp = 0, hvf = 0, present = 0; long dExp = 0; int maxExp = 0;
            for (int c = 0; c < W * D; c++)
            {
                if (m.Weather.Bin[c] == WeatherTerrain.NoData) continue;
                present++;
                if (m.Weather.Bin[c] != old.Weather.Bin[c]) bins++;
                if (m.Weather.Hvf[c] != old.Weather.Hvf[c]) hvf++;
                int e = Math.Abs(m.Weather.Exposure[c] - old.Weather.Exposure[c]);
                if (e != 0) exp++;
                dExp += e; maxExp = Math.Max(maxExp, e);
            }
            double meanFeet = 0, oldFeet = 0;
            for (int c = 0; c < W * D; c++) { meanFeet += m.Feet[c]; oldFeet += old.Feet[c]; }
            Say($"[snow-a2] {name}: season in {clock.Elapsed.TotalSeconds:F1} s, {bad} unbalanced days; save {saveBytes / 1024} KB; " +
                $"weather terrain: bins moved in {100.0 * bins / present:F1}% of cells, exposure in {100.0 * exp / present:F1}% (mean {dExp / (double)present:F2}, most {maxExp})");
            Assert.That(bad, Is.Zero, $"{name}: every day's ledger balances");
            return string.Format(Inv,
                "{{\"name\":\"{0}\",\"cells\":{1},\"cellMetres\":8,\"bands\":{{\"base\":{2:F0},\"mid\":{3:F0},\"summit\":{4:F0}}},\"days\":[{5}],\"maps\":[{6}]," +
                "\"saveBytes\":{7},\"weatherShift\":{{\"cells\":{8},\"bins\":{9},\"hvf\":{10},\"exposure\":{11},\"exposureMean\":{12:F3},\"exposureMost\":{13},\"meanFeetShift\":{14:F2}}}}}",
                name, W * D, Feet(low), Feet(mid), Feet(high), days, maps, saveBytes, present, bins, hvf, exp, dExp / (double)present, maxExp,
                (meanFeet - oldFeet) / (W * D));
        }

        static string MonthName(int month) => CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName(month);
    }
}
