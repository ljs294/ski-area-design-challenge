using System;
using System.Diagnostics;
using System.Linq;
using MountainPlanner.Domain.Sky;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2, step 1 (measure before building): what A1's proven snow costs on the worker over a whole 11 km site
    /// (1,375 × 1,375 cells of 8 m), every cell under a mid-winter pack, through real January hours of Jackson Hole's
    /// frozen rows. Times the weather's fill, the batch read, the slope sun and the energy step, on one thread and on four
    /// (a fixed split, as the game would use), and the month skip as it costs today with the weather's inputs at the same
    /// size. A report, not a gate: its numbers set BG-02 v4.
    /// </summary>
    [Explicit, Category("SnowReport")]
    public sealed class SnowCostReportTests
    {
        const int W = 1375, D = 1375, Band = 64;
        static readonly Pacing P = Pacing.Default;

        static int[] Feet()
        {
            var feet = new int[W * D];
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++)
                {
                    double x = i * 8.0, y = j * 8.0;
                    double m = 0.5 + 0.5 * Math.Sin(x / 1500) * Math.Cos(y / 2100);
                    feet[j * W + i] = 6300 + (int)(3500 * m + 180 * Math.Sin(x / 97 + y / 131) + 60 * Math.Cos(x / 41 - y / 53));
                }
            return feet;
        }

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        [Test]
        public void ReportTheCostOfAFaceHour()
        {
            string rt = Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET";
            Say($"[cost] runtime: {rt} {Environment.Version}, {Environment.ProcessorCount} cores");
            var feet = Feet();
            var sw = Stopwatch.StartNew();
            var t = WeatherTerrain.Build(W, D, feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2, SimWeather.TerrainTuning(P));
            double buildMs = sw.Elapsed.TotalMilliseconds;

            // Unit normals (millionths) from the heights, once.
            var nx = new int[W * D]; var ny = new int[W * D]; var nz = new int[W * D];
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++)
                {
                    int c = j * W + i;
                    double dx = (feet[j * W + Math.Min(W - 1, i + 1)] - feet[j * W + Math.Max(0, i - 1)]) * 0.3048 / 16;
                    double dy = (feet[Math.Min(D - 1, j + 1) * W + i] - feet[Math.Max(0, j - 1) * W + i]) * 0.3048 / 16;
                    double len = Math.Sqrt(dx * dx + dy * dy + 1);
                    nx[c] = (int)(-dx / len * SunVector.One); ny[c] = (int)(-dy / len * SunVector.One); nz[c] = (int)(1 / len * SunVector.One);
                }

            var packs = new SnowPack[W * D];
            for (int c = 0; c < packs.Length; c++)
                packs[c] = new SnowPack { Swe = 300_000, Density = 300_000, SurfaceTemp = -500, DeepTemp = -50_000, Albedo = 800_000 };

            var rows = SimWeatherTests.Frozen(WeatherFixtures.JacksonHole);
            var tuning = WeatherTuning.Default;
            int faceSeconds = (int)Math.Round((double)tuning.CalendarDays / tuning.FaceDays * 3600);
            var gen = new WeatherGenerator(rows, tuning);
            var h = new WeatherHour();
            gen.Reset(0x534E_4F57_0000_0001UL, 2026);
            for (int i = 0; i < 32 * 24; i++) gen.Step(ref h);   // about January 1

            var fields = new WeatherFields();
            int bands = (D + Band - 1) / Band;
            var buffers = new WeatherCell[4][];
            for (int k = 0; k < 4; k++) buffers[k] = new WeatherCell[Band * W];

            double fillMs = 0, oneMs = 0, fourMs = 0, readMs = 0;
            int hours = 12, wet = 0;
            for (int hr = 0; hr < 2 * hours; hr++)
            {
                gen.Step(ref h);
                if (h.Liquid > 0) wet++;
                sw.Restart();
                fields.Fill(hr, h, t, rows.ZrefFeet, P, 0);
                fillMs += sw.Elapsed.TotalMilliseconds;
                int doy = 1 + hr / 8;
                var sun = SunVector.At(43_590_000, -110_830_000, doy, h.HourOfDay * 3600 + 1800);
                int clear = sun.AboveHorizon ? h.Clearness : 0;
                bool four = hr >= hours;

                // The read alone, for the split.
                if (!four)
                {
                    sw.Restart();
                    for (int b = 0; b < bands; b++) fields.ReadRows(b * Band, Math.Min(D, b * Band + Band), buffers[0]);
                    readMs += sw.Elapsed.TotalMilliseconds;
                }

                sw.Restart();
                void RunBand(int b, WeatherCell[] buf)
                {
                    int y0 = b * Band, y1 = Math.Min(D, y0 + Band);
                    fields.ReadRows(y0, y1, buf);
                    for (int j = y0; j < y1; j++)
                        for (int i = 0; i < W; i++)
                        {
                            int c = j * W + i;
                            ref var wc = ref buf[(j - y0) * W + i];
                            var (beam, diffuse) = SlopeSun.At(sun, clear, nx[c], ny[c], nz[c], 900, true);
                            int water = Math.Max(0, wc.Liquid) * 254;
                            int snow = water > 0 ? water * wc.SnowPermille / 1000 : 0;
                            var x = new SnowInput
                            {
                                Seconds = faceSeconds, Air = wc.Temp, Dew = wc.Dew, WetBulb = wc.WetBulb, Feet = wc.Feet, Wind = wc.Wind,
                                Cloud = h.Cloud, Beam = beam, Diffuse = diffuse, OpenSky = 900, Canopy = 0, Snow = snow,
                                SnowDensity = 10_000 / WeatherAir.SnowRatioTenths(wc.WetBulb), Rain = water - snow,
                            };
                            SnowEnergy.Step(ref packs[c], x, SnowTuning.Default);
                        }
                }
                if (!four) for (int b = 0; b < bands; b++) RunBand(b, buffers[0]);
                else
                {
                    // Four dedicated threads, a fixed split (band b on thread b mod 4), as the worker would run them.
                    var threads = new System.Threading.Thread[4];
                    for (int k = 0; k < 4; k++)
                    {
                        int kk = k;
                        threads[k] = new System.Threading.Thread(() => { for (int b = kk; b < bands; b += 4) RunBand(b, buffers[kk]); });
                        threads[k].Start();
                    }
                    foreach (var th in threads) th.Join();
                }
                if (four) fourMs += sw.Elapsed.TotalMilliseconds; else oneMs += sw.Elapsed.TotalMilliseconds;
            }

            // The month skip as it costs today with the weather's inputs at this size (the snow costs nothing an hour today).
            var world = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026, weather: SimWeatherTests.Golden());
            world.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            sw.Restart();
            long month = (long)Math.Round(30.0 * tuning.FaceDays / tuning.CalendarDays * 24) * SimWorld.HourMs;
            while (world.FaceMs < 70L * 24 * SimWorld.HourMs) world.Step(P.IdleTickFactor);   // into the winter first
            double warmMs = sw.Elapsed.TotalMilliseconds;
            long start = world.FaceMs;
            sw.Restart();
            while (world.FaceMs < start + month) world.Step(P.IdleTickFactor);
            double todayMonthMs = sw.Elapsed.TotalMilliseconds;
            int monthHours = (int)(month / SimWorld.HourMs);

            double perHour1 = oneMs / hours, perHour4 = fourMs / hours, fill = fillMs / (2 * hours), read = readMs / hours;
            double ns = perHour1 * 1e6 / (W * D);
            Say($"[cost] {W}×{D} cells ({W * D / 1e6:F2} M), {wet} wet hours of {2 * hours}; weather inputs built in {buildMs:F0} ms");
            Say($"[cost] per face hour: fill {fill:F1} ms, read {read:F0} ms, read+sun+energy {perHour1:F0} ms on 1 thread ({ns:F0} ns a cell), {perHour4:F0} ms on 4");
            Say($"[cost] skip to morning (16 face hours): {16 * perHour1 / 1000:F1} s on 1, {16 * perHour4 / 1000:F1} s on 4");
            Say($"[cost] skip a month ({monthHours} face hours): {monthHours * perHour1 / 1000:F0} s on 1, {monthHours * perHour4 / 1000:F0} s on 4, every cell snowy");
            Say($"[cost] month skip today, with the weather at this size: {todayMonthMs / 1000:F2} s (warm-up {warmMs / 1000:F1} s)");
            Say($"[cost] memory: packs {packs.Length * 28 / 1e6:F0} MB");
            Assert.Pass();
        }

        /// <summary>
        /// The Coordinator's condition for WX-02 v3 (2026-11-14): the hourly fill and a one-week jump at 11 km, timed. The fill
        /// over 48 real January hours (mean and worst); then a whole world with the live snow, its field seeded to a metre
        /// everywhere in January, jumping a face week as a skip does (the idle steps). <c>MP_COST_TAG</c> names the run; the
        /// numbers go to test-results/snow-a2/cost-&lt;tag&gt;.json for the report.
        /// </summary>
        [Test]
        public void ReportTheFillAndAWeekJump()
        {
            string tag = Environment.GetEnvironmentVariable("MP_COST_TAG") ?? "run";
            var feet = Feet();
            var mm = new int[W * D];
            for (int c = 0; c < mm.Length; c++) mm[c] = (int)Math.Round(feet[c] * 304.8);
            var sw = Stopwatch.StartNew();
            var t = WeatherTerrain.Build(W, D, feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2, SimWeather.TerrainTuning(P));
            double weatherMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var snowTerrain = Domain.Snow.SnowTerrain.Build(W, D, mm, new byte[W * D], t, (int[])P.WindExposure.Clone());
            double snowTerrainMs = sw.Elapsed.TotalMilliseconds;

            // The fill, hour by hour.
            var rows = SimWeatherTests.Frozen(WeatherFixtures.JacksonHole);
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            var h = new WeatherHour();
            gen.Reset(0x534E_4F57_0000_0001UL, 2026);
            for (int k = 0; k < 32 * 24; k++) gen.Step(ref h);
            var fields = new WeatherFields();
            fields.Fill(0, h, t, rows.ZrefFeet, P, 0);   // the first sizes the tables
            double fillSum = 0, fillMost = 0;
            const int fills = 48;
            for (int k = 0; k < fills; k++)
            {
                gen.Step(ref h);
                sw.Restart();
                fields.Fill(k + 1, h, t, rows.ZrefFeet, P, 0);
                double ms = sw.Elapsed.TotalMilliseconds;
                fillSum += ms; fillMost = Math.Max(fillMost, ms);
            }

            // The week: the weather alone to January, then the snow's inputs and a metre of seeded snow everywhere.
            var world = new SimWorld(P, SeasonRunner.GoldenDesign(1, false), SimSite.None, 2026, weather: SimWeatherTests.Golden());
            world.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, t));
            while (world.FaceMs < 70L * 24 * SimWorld.HourMs) world.Step(P.IdleTickFactor);
            world.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowTerrain, 0, snowTerrain));
            var seed = new ushort[W * D];
            for (int c = 0; c < seed.Length; c++) seed[c] = 1000;
            world.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 0, 0, W, D, seed)));
            long dayStart = world.FaceMs + SimWorld.DayMs - world.FaceMs % SimWorld.DayMs;
            while (world.FaceMs < dayStart) world.Step(P.IdleTickFactor);   // one warm-up stretch to the next midnight
            world.JoinSnow();
            long start = world.FaceMs;
            var days = new double[7];
            sw.Restart();
            for (int d = 0; d < 7; d++)
            {
                var day = Stopwatch.StartNew();
                long end = start + (d + 1) * SimWorld.DayMs;
                while (world.FaceMs < end) world.Step(P.IdleTickFactor);
                days[d] = day.Elapsed.TotalMilliseconds;
            }
            world.JoinSnow();
            double weekMs = sw.Elapsed.TotalMilliseconds;
            // The whole field folded (FNV-1a over every pack's numbers): a speed-up must leave it exactly the same.
            ulong fold = 14695981039346656037UL;
            void Mix(int v) { unchecked { fold = (fold ^ (uint)v) * 1099511628211UL; } }
            for (int y = 0; y < D; y++)
                for (int x = 0; x < W; x++)
                {
                    var pk = world.Snow.PackAt(x, y);
                    Mix(pk.Swe); Mix(pk.Liquid); Mix(pk.Density); Mix(pk.SurfaceTemp); Mix(pk.DeepTemp); Mix(pk.Albedo); Mix(pk.SurfaceLiquid); Mix(world.Snow[x, y]);
                }
            long memory = GC.GetTotalMemory(true);
            Array.Sort(days);

            Say($"[cost:{tag}] {W}×{D} cells; weather inputs {weatherMs:F0} ms, snow inputs {snowTerrainMs:F0} ms");
            Say($"[cost:{tag}] the hourly fill: mean {fillSum / fills:F2} ms, worst {fillMost:F2} ms over {fills} January hours");
            Say($"[cost:{tag}] a face week's jump with the live snow (a metre seeded): {weekMs / 1000:F1} s; a face day {days[3] / 1000:F2} s " +
                $"median, {days[6] / 1000:F2} s worst; the snow's water at the end {world.SnowToday.End / 1000.0 / (W * D):F0} mm a cell");
            Say($"[cost:{tag}] managed memory after the week: {memory / 1e6:F0} MB; the field's fold 0x{fold:X16}");
            var json = System.FormattableString.Invariant(
                $"{{\"tag\":\"{tag}\",\"cells\":{W * D},\"fillMeanMs\":{fillSum / fills:F3},\"fillWorstMs\":{fillMost:F3},\"weekS\":{weekMs / 1000:F2},\"dayMedianS\":{days[3] / 1000:F3},\"dayWorstS\":{days[6] / 1000:F3},\"memoryMB\":{memory / 1e6:F0},\"fold\":\"{fold:X16}\"}}");
            string dir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(TestData.Folder("formats")), "..", "test-results", "snow-a2");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, $"cost-{tag}.json"), json);
            Assert.Pass();
        }
    }
}
