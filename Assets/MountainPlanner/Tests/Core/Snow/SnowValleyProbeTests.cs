using System;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2, the owner's finding 2 (2026-11-15): the valley floor went green in late November and stayed green
    /// into December. The probe follows three cells hour by hour from bare October 1 to December 15, each on its own 2 km
    /// crop of the Jackson Hole package under its frozen rows: the flat valley floor 500 m north of the valley lake (in the
    /// ring, 2.3 km south of the centre), a cell at the base's height, and the deepest pile (the shed cone under the cliff,
    /// 1.5 km west). Each runs twice: the snow as it is, and with thin snow keeping its brightness down to 5 mm of water.
    /// Writes <c>test-results/snow-a2/valley/probe.json</c>; <c>tools/snow-a2/valley.mjs</c> draws it.
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowValleyProbeTests
    {
        const int W = 256, D = 256;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static void Say(string s) => TestContext.Progress.WriteLine(s);

        /// <summary>The runs: the label and the Pacing lines that make it.</summary>
        internal static readonly (string Label, string Pacing)[] Runs =
            { ("before: bright from 20 mm of water", "SnowThinUm = 20000"), ("after: bright from 5 mm", "SnowThinUm = 5000") };

        [Test]
        public void FollowTheValleyTheBaseAndThePile()
        {
            string root = Path.GetFullPath(Path.Combine(TestData.Folder("formats"), "..", "..", "test-results", "snow-a2", "valley"));
            Directory.CreateDirectory(root);
            string jh = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(jh, "*.grid")) TestData.Bytes(jh, Path.GetFileName(f));

            var points = new StringBuilder();
            void Add(string json) { if (points.Length > 0) points.Append(','); points.Append(json); }

            // 1. The valley floor: the flat cell nearest 500 m north of the valley lake's centroid (912 m E, 2,792 m S).
            var valley = SnowRunoutProbeTests.Crop(jh, east: 912, north: -2792);
            int vc = NearestFlat(valley, W / 2, D / 2 + 62, 2);
            Add(Point("Valley floor", "500 m north of the valley lake, 2.3 km south of the centre", valley, vc, 12, 15));

            // 2. The base's height: the 10th percentile of the centre crop's middle (as WeatherRows takes the base), the
            // gentlest cell there nearest the centre.
            var centre = SnowRunoutProbeTests.Crop(jh);
            int bc = AtBaseHeight(centre);
            Add(Point("Base height", "the lowest tenth of the resort's middle, nearest the centre", centre, bc, 12, 15));

            // 3. The deepest pile: the shed cone under the cliff, 1.5 km west and 0.8 km north (199 in in the game's
            // mid-November). Its cell is the crop's deepest within 320 m of its middle on February 15, followed to March 15.
            var cliff = SnowRunoutProbeTests.Crop(jh, east: -1512, north: 800);
            int pc = Deepest(cliff, 2, 15, 40);
            Add(Point("Deepest pile", "the shed cone under the cliff, 1.5 km west", cliff, pc, 3, 15));

            string json = "{\"made\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv) + "\",\"runs\":[" +
                          string.Join(",", Array.ConvertAll(Runs, r => "\"" + r.Label + "\"")) + "],\"points\":[" + points + "]}";
            File.WriteAllText(Path.Combine(root, "probe.json"), json);
            Say($"[valley] wrote {Path.Combine(root, "probe.json")} ({json.Length / 1024} KB)");
        }

        internal static SimWorld World(Pacing p, SnowFieldTests.Mountain m)
        {
            var w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, m);
            return w;
        }

        static string Point(string name, string where, SnowFieldTests.Mountain m, int c, int toMonth, int toDay)
        {
            int x = c % W, y = c / W;
            Say($"[valley] {name}: cell {x},{y} ({(x - W / 2) * 8} m E, {(y - D / 2) * 8} m N of its crop's middle), {m.Feet[c]} ft, " +
                $"grade {Grade(m.Snow, c)}%, height over the valley floor {m.Weather.Hvf[c]} ft");
            var runs = new StringBuilder();
            foreach (var (label, pacing) in Runs)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var p = Pacing.Parse(pacing);
                var w = World(p, m);
                var hours = new StringBuilder();
                var row = new WeatherCell[W];
                var s = new SimSnapshot(p);
                long lastHour = -1;
                int white = 0, whiteNew = 0, bad = 0, lastDay = w.SnowLastDay.Day;
                while (!AtOrPast(w, toMonth, toDay))
                {
                    w.Step(p.IdleTickFactor);
                    if (w.SnowLastDay.Day != lastDay) { lastDay = w.SnowLastDay.Day; if (w.SnowLastDay.Balance != 0) bad++; }
                    long hour = w.FaceMs / SimWorld.HourMs;
                    if (hour == lastHour) continue;
                    lastHour = hour;
                    w.JoinSnow();
                    w.Write(s);
                    var pk = w.Snow.PackAt(x, y);
                    s.WeatherFields.ReadRowsSmooth(y, y + 1, row, m.Snow.BinPlace, m.Snow.Exposure, m.Snow.Canopy);
                    var wc = row[x];
                    int j = (int)(w.FaceMs / SimWorld.DayMs) % w.Calendar.YearFaceDays;
                    int fell = wc.Known && wc.LiquidUm > 0 ? (int)((long)wc.LiquidUm * wc.SnowPermille / 1000) : 0;
                    int rain = wc.Known && wc.LiquidUm > 0 ? wc.LiquidUm - fell : 0;
                    if (pk.DepthMm >= 75) white++;   // half of today's full cover (15 cm)
                    if (pk.DepthMm >= 25) whiteNew++; // half of 5 cm
                    if (hours.Length > 0) hours.Append(',');
                    hours.Append(string.Format(Inv,
                        "[{0},{1},{2},{3:F1},{4:F1},{5},{6},{7},{8},{9},{10},{11},{12}]",
                        j, w.Calendar.Month(j), w.Calendar.Day(j) * 100 + (int)(hour % 24), wc.Temp / 10.0, wc.WetBulb / 10.0, wc.Shortwave,
                        fell, rain, pk.Swe, pk.DepthMm, pk.DensityKg, Brightness(pk, p), (int)w.Snow.TypeAt(x, y)));
                }
                Say($"[valley]   {label}: {clock.Elapsed.TotalSeconds:F1} s; hours drawn at least half white: {white} at 15 cm full cover, " +
                    $"{whiteNew} at 5 cm; unbalanced days {bad}");
                Assert.That(bad, Is.Zero, $"{name}, {label}: every day's ledger balances");
                if (runs.Length > 0) runs.Append(',');
                runs.Append("{\"label\":\"" + label + "\",\"hours\":[" + hours + "]}");
            }
            return string.Format(Inv, "{{\"name\":\"{0}\",\"where\":\"{1}\",\"feet\":{2},\"grade\":{3},\"hvf\":{4},\"runs\":[{5}]}}",
                                 name, where, m.Feet[c], Grade(m.Snow, c), m.Weather.Hvf[c], runs);
        }

        /// <summary>The brightness the energy uses (‰): the snow's own, blended toward bare ground's under SnowThinUm of water
        /// (SnowEnergy.Step's thin-snow rule); 0 with no snow.</summary>
        static int Brightness(in Domain.Weather.SnowPack pk, Pacing p)
        {
            if (pk.Swe <= 0) return 0;
            long own = pk.AlbedoPermille;
            return pk.Swe >= p.SnowThinUm ? (int)own : (int)(p.AlbedoGround + (own - p.AlbedoGround) * pk.Swe / p.SnowThinUm);
        }

        /// <summary>True at the start of a face day on or after the label date (the face calendar skips labels).</summary>
        internal static bool AtOrPast(SimWorld w, int month, int day)
        {
            int j = (int)(w.FaceMs / SimWorld.DayMs) % w.Calendar.YearFaceDays;
            return w.FaceMs % SimWorld.DayMs == 0 && Order(w.Calendar.Month(j), w.Calendar.Day(j)) >= Order(month, day);
        }

        static int Order(int month, int day) => (month >= 10 ? month - 10 : month + 2) * 40 + day;

        static int NearestFlat(SnowFieldTests.Mountain m, int x0, int y0, int maxGrade)
        {
            for (int r = 0; r < W / 2; r++)
                for (int b = -r; b <= r; b++)
                    for (int a = -r; a <= r; a++)
                    {
                        if (Math.Max(Math.Abs(a), Math.Abs(b)) != r) continue;
                        int x = x0 + a, y = y0 + b;
                        if (x < 8 || y < 8 || x >= W - 8 || y >= D - 8) continue;
                        if (Grade(m.Snow, y * W + x) <= maxGrade) return y * W + x;
                    }
            throw new InvalidOperationException("no flat cell");
        }

        static int AtBaseHeight(SnowFieldTests.Mountain m)
        {
            var box = new System.Collections.Generic.List<int>();
            for (int j = D / 5; j < D * 4 / 5; j++)
                for (int i = W / 5; i < W * 4 / 5; i++) box.Add(m.Feet[j * W + i]);
            box.Sort();
            int baseFeet = box[box.Count / 10], best = -1;
            long bestScore = long.MaxValue;
            for (int j = D / 5; j < D * 4 / 5; j++)
                for (int i = W / 5; i < W * 4 / 5; i++)
                {
                    int c = j * W + i;
                    if (Math.Abs(m.Feet[c] - baseFeet) > 40) continue;
                    // The gentlest first (the base's lower slopes are steep), then the nearest the centre.
                    long score = Grade(m.Snow, c) * 1_000_000L + (long)(i - W / 2) * (i - W / 2) + (long)(j - D / 2) * (j - D / 2);
                    if (score < bestScore) { bestScore = score; best = c; }
                }
            if (best < 0) throw new InvalidOperationException("no cell at the base's height");
            Say($"[valley] the base: {baseFeet} ft (the 10th percentile of the crop's middle)");
            return best;
        }

        static int Deepest(SnowFieldTests.Mountain m, int month, int day, int within)
        {
            var w = World(Pacing.Default, m);
            SnowFieldTests.RunTo(w, month, day, Pacing.Default.IdleTickFactor);
            w.JoinSnow();
            int best = -1, deepest = -1;
            for (int j = D / 2 - within; j <= D / 2 + within; j++)
                for (int i = W / 2 - within; i <= W / 2 + within; i++)
                    if (w.Snow[i, j] > deepest) { deepest = w.Snow[i, j]; best = j * W + i; }
            Say($"[valley] the deepest pile on {month}/{day}: {deepest / 25.4:F0} in");
            return best;
        }

        internal static int Grade(SnowTerrain t, int c)
        {
            long x = t.NormalX[c], y = t.NormalY[c], s = x * x + y * y, up = 127L * 127 - s;
            return up <= 0 ? 100_000 : (int)Math.Min(100_000, Math.Sqrt((double)s / up) * 100);
        }
    }
}
