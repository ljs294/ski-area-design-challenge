using System;
using System.Diagnostics;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2: the live snow in a whole world. Earthwork leaves the snow where it lay; a skip to morning and a skip
    /// to the next third give the same bytes as stepping one tick at a time; an even slope across the weather's elevation
    /// bands shows no step in the snow.
    /// </summary>
    public sealed class SnowWorldTests
    {
        const int W = 128, D = 128;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static SimWorld World(SnowFieldTests.Mountain m)
        {
            var w = new SimWorld(Pacing.Default, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
            SnowFieldTests.SendInputs(w, m);
            return w;
        }

        static SnowFieldTests.Mountain _small;
        static SnowFieldTests.Mountain Small => _small ??= SnowFieldTests.Build(Pacing.Default, W, D);

        [Test]
        public void SN01_EarthworkLeavesTheSnowWhereItLay()
        {
            var m = Small;
            var edited = World(m);
            var plain = World(m);
            SnowFieldTests.RunTo(edited, 12, 12, Pacing.Default.IdleTickFactor);
            SnowFieldTests.RunTo(plain, 12, 12, Pacing.Default.IdleTickFactor);
            // An edit arrives with an hour in flight, as it does in the game. The field is read only once its threads are done.
            SnowFieldTests.IntoFlight(edited);
            SnowFieldTests.IntoFlight(plain);
            edited.JoinSnow();

            // A 4 m pit, 96 m across, cleared of trees: the shapes change, the snow lying there does not.
            const int i0 = 50, j0 = 50, i1 = 62, j1 = 62;
            var mm = (int[])m.Mm.Clone();
            var canopy = (byte[])m.Canopy.Clone();
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++) { mm[j * W + i] -= 4000; canopy[j * W + i] = 0; }
            var patch = m.Snow.Patch(mm, canopy, i0, j0, i1, j1);
            Assert.That(patch, Is.Not.Null);
            var before = new int[W * D];
            long inside = 0;
            for (int c = 0; c < W * D; c++) before[c] = edited.Snow.PackAt(c % W, c / W).Swe;
            for (int j = j0; j < j1; j++) for (int i = i0; i < i1; i++) inside += before[j * W + i];
            Assert.That(inside, Is.GreaterThan(0), "snow in the pit's footprint to keep");

            edited.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowTerrain, 0, patch));
            int moved = 0;
            for (int c = 0; c < W * D; c++) if (edited.Snow.PackAt(c % W, c / W).Swe != before[c]) moved++;
            Assert.That(moved, Is.Zero, "the edit lands the hour in flight as it would have and moves no snow");
            Assert.That(edited.Snow.Field.Pack[0].Swe, Is.EqualTo(plain.Snow.Field.Pack[0].Swe));

            // A day on: the ledger balances, and only the snow near the edit lives differently (the new shapes shed, drift
            // and melt their own way; a run-out can carry that a few hundred metres down the fall line).
            long end = edited.FaceMs + SimWorld.DayMs;
            while (edited.FaceMs < end) edited.Step(Pacing.Default.IdleTickFactor);
            while (plain.FaceMs < end) plain.Step(Pacing.Default.IdleTickFactor);
            edited.JoinSnow();
            plain.JoinSnow();
            int differ = 0, farthest = 0;
            long keptIn = 0;
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++)
                {
                    if (i >= i0 && i < i1 && j >= j0 && j < j1) keptIn += edited.Snow.PackAt(i, j).Swe;
                    if (edited.Snow.PackAt(i, j).Swe == plain.Snow.PackAt(i, j).Swe) continue;
                    differ++;
                    int dx = Math.Max(0, Math.Max(i0 - i, i - (i1 - 1))), dy = Math.Max(0, Math.Max(j0 - j, j - (j1 - 1)));
                    farthest = Math.Max(farthest, Math.Max(dx, dy));
                }
            Say($"[earthwork] {inside / 25_400.0 / ((i1 - i0) * (j1 - j0)):F2} in of water a cell in the pit's footprint, " +
                $"{keptIn / 25_400.0 / ((i1 - i0) * (j1 - j0)):F2} in a day later; {differ} cells differ from the unedited world, " +
                $"the farthest {farthest * 8} m from the pit");
            Assert.That(edited.SnowLastDay.Balance, Is.Zero, "the day's ledger balances");
            Assert.That(differ, Is.LessThan(W * D / 20), "the edit changes the snow near it, not the map's");
            Assert.That(keptIn, Is.GreaterThan(0), "the pit keeps its snow");
        }

        /// <summary>Runs <paramref name="skipping"/> as the host's skip does (the idle steps to the target), then steps
        /// <paramref name="ticking"/> one tick at a time to the same tick: the hashes agree.</summary>
        static (long Ticks, double Seconds) SkipAgainstTicks(SimWorld skipping, SimWorld ticking, SkipKind kind)
        {
            Assert.That(skipping.SkipTarget(kind, out long target), Is.True, $"{kind} allowed");
            while (skipping.FaceMs < target) skipping.Step(Pacing.Default.IdleTickFactor);
            long from = ticking.Tick;
            var sw = Stopwatch.StartNew();
            while (ticking.Tick < skipping.Tick) ticking.Step(1);
            Assert.That(ticking.Tick, Is.EqualTo(skipping.Tick));
            Assert.That(ticking.Hash(), Is.EqualTo(skipping.Hash()), $"{kind}: the same state");
            Assert.That(ticking.Snow.TileHashes, Is.EqualTo(skipping.Snow.TileHashes), $"{kind}: the same snow");
            Assert.That(ticking.SnowToday, Is.EqualTo(skipping.SnowToday), $"{kind}: the same ledger");
            return (ticking.Tick - from, sw.Elapsed.TotalSeconds);
        }

        [Test]
        public void BG02_ASkipToMorningTheNextThirdAndTheNextMonthGiveTheSameSnowAsTicking()
        {
            var a = World(Small);
            var b = World(Small);
            // December 3, as the golden weather's cover builds (the golden design is before its season all winter).
            SnowFieldTests.RunTo(a, 12, 3, Pacing.Default.IdleTickFactor);
            SnowFieldTests.RunTo(b, 12, 3, Pacing.Default.IdleTickFactor);
            // Into the evening, so the morning skip crosses the night's hours.
            long evening = a.FaceMs + 18 * SimWorld.HourMs;
            while (a.FaceMs < evening) a.Step(Pacing.Default.IdleTickFactor);
            while (b.Tick < a.Tick) b.Step(Pacing.Default.IdleTickFactor);
            var morning = SkipAgainstTicks(a, b, SkipKind.Morning);
            var third = SkipAgainstTicks(a, b, SkipKind.NextThird);
            var month = SkipAgainstTicks(a, b, SkipKind.NextMonth);   // TM-08 v2: before the season too
            Say($"[skips] to morning: {morning.Ticks:N0} ticks one at a time ({morning.Seconds:F1} s); to the next third: " +
                $"{third.Ticks:N0} ticks ({third.Seconds:F1} s); to the next month: {month.Ticks:N0} ticks ({month.Seconds:F1} s); " +
                $"the same bytes; {a.SnowToday.End / 1e6 / (W * D) * 1000:F0} mm of water a cell");
            Assert.That(a.SnowToday.End, Is.GreaterThan(0), "snow to compare");
        }

        [Test]
        public void SN_AnEvenSlopeAcrossTheWeathersBandsShowsNoStepInTheSnow()
        {
            // 64 × 384 cells (3 km) climbing 15 % to the south (a north face): about 460 m, across three of the weather's 500 ft bands and
            // dozens of its bins, with no ridge, no gully, no trees.
            const int w = 64, d = 384;
            var p = Pacing.Default;
            var m = new SnowFieldTests.Mountain { Mm = new int[w * d], Feet = new int[w * d], Canopy = new byte[w * d] };
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                {
                    double metres = 2300 + (d - 1 - j) * 8 * 0.15;
                    m.Mm[j * w + i] = (int)Math.Round(metres * 1000);
                    m.Feet[j * w + i] = (int)Math.Round(metres / 0.3048);
                }
            m.Weather = WeatherTerrain.Build(w, d, m.Feet, -w / 2 * 8000, -d / 2 * 8000, (w / 5, d / 5, w * 4 / 5, d * 4 / 5), d / 2 * w + w / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(w, d, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            var world = World(m);
            SnowFieldTests.RunTo(world, 12, 12, p.IdleTickFactor);
            // Each row's mean water; a step would be one row-to-row change far above its neighbours'.
            var row = new double[d];
            for (int j = 0; j < d; j++)
            {
                long s = 0;
                for (int i = 8; i < w - 8; i++) s += world.Snow.PackAt(i, j).Swe;
                row[j] = s / (double)(w - 16);
            }
            double worst = 0, sum = 0;
            int at = 0, n = 0;
            for (int j = 12; j < d - 13; j++)
            {
                double step = Math.Abs(row[j + 1] - row[j]);
                sum += step; n++;
                if (step > worst) { worst = step; at = j; }
            }
            double mean = sum / n;
            Say($"[slope] Dec 12, {row[d - 13] / 25_400:F2} in of water at the foot to {row[12] / 25_400:F2} in at the top; the biggest " +
                $"row-to-row change {worst:F0} µm at row {at}, the mean {mean:F0} µm");
            Assert.That(Math.Min(row[12], row[d - 13]), Is.GreaterThan(0), "snow the length of the slope");
            // A thaw on thin snow can split neighbours by about 1 mm of water (the weather's 0.1 °F wet bulb seeds it, SN-09 v3's
            // faster melt of thin snow triples it: 973 µm on December 1 here, under half an inch of snow on a 32 mm pack). A step
            // the eye could find on the map is a tenth of an inch of water.
            Assert.That(worst, Is.LessThanOrEqualTo(Math.Max(2540, 4 * mean)), "no step you could see, at a band or bin edge or anywhere");
        }

        /// <summary>The even north face of the slope test: 64 × 384 cells, 15 % from 2,300 m.</summary>
        static SnowFieldTests.Mountain EvenSlope(Pacing p, int w = 64, int d = 384)
        {
            var m = new SnowFieldTests.Mountain { Mm = new int[w * d], Feet = new int[w * d], Canopy = new byte[w * d] };
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                {
                    double metres = 2300 + (d - 1 - j) * 8 * 0.15;
                    m.Mm[j * w + i] = (int)Math.Round(metres * 1000);
                    m.Feet[j * w + i] = (int)Math.Round(metres / 0.3048);
                }
            m.Weather = WeatherTerrain.Build(w, d, m.Feet, -w / 2 * 8000, -d / 2 * 8000, (w / 5, d / 5, w * 4 / 5, d * 4 / 5), d / 2 * w + w / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(w, d, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            return m;
        }

        [TestCase(340, 0, TestName = "SN_TheWeathersReadUpAnEvenSlopeHasNoStep(near freezing)")]
        [TestCase(340, 120, TestName = "SN_TheWeathersReadUpAnEvenSlopeHasNoStep(a cold pool)")]
        [TestCase(250, 0, TestName = "SN_TheWeathersReadUpAnEvenSlopeHasNoStep(cold)")]
        public void SN_TheWeathersReadUpAnEvenSlopeHasNoStep(int temp, int pool)
        {
            // What the snow reads, row by row up the slope: the air, the wet bulb, the snow's share and the water. Each changes
            // by about the same small amount a row; a bin or band edge would show as one row's change far above the rest.
            const int w = 64, d = 384;
            var p = Pacing.Default;
            var m = EvenSlope(p, w, d);
            var f = new WeatherFields();
            var h = new WeatherHour
            {
                Temp = temp, Dew = temp - 8, Lapse = 35, DewLapse = 30, Pool = pool, Liquid = 20, PrecipGradient = 5,
                Wind = 150, GustFactor = 1500, Direction = 180, StormOrdinal = 0, StormDirection = 180,
            };
            f.Fill(0, h, m.Weather, 7500, p, 0);
            var row = new WeatherCell[w];
            var mean = new double[5, d];
            for (int j = 0; j < d; j++)
            {
                f.ReadRowsSmooth(j, j + 1, row, m.Snow.BinPlace, m.Snow.Exposure, m.Snow.Canopy);
                for (int i = 8; i < w - 8; i++)
                {
                    mean[0, j] += row[i].Temp; mean[1, j] += row[i].WetBulb; mean[2, j] += row[i].SnowPermille;
                    mean[3, j] += row[i].LiquidUm; mean[4, j] += row[i].FreezingRain ? 1000 : 0;
                }
                for (int k = 0; k < 5; k++) mean[k, j] /= w - 16;
            }
            string[] names = { "air (0.1 °F)", "wet bulb (0.1 °F)", "snow share (‰)", "water (µm)", "freezing rain (‰ of cells)" };
            for (int k = 0; k < 5; k++)
            {
                double worst = 0, sum = 0;
                int at = 0, n = 0;
                for (int j = 12; j < d - 13; j++)
                {
                    double s = Math.Abs(mean[k, j + 1] - mean[k, j]);
                    sum += s; n++;
                    if (s > worst) { worst = s; at = j; }
                }
                double avg = sum / n, span = Math.Abs(mean[k, d - 13] - mean[k, 12]);
                Say($"[read] {names[k]}: {mean[k, d - 13]:F1} at the foot to {mean[k, 12]:F1} at the top; the biggest row step {worst:F2} " +
                    $"at row {at} ({m.Feet[at * w]} ft), the mean {avg:F2}");
                if (worst > Math.Max(1.0, 4 * avg + span / 50))
                    for (int j = Math.Max(0, at - 4); j <= Math.Min(d - 1, at + 5); j++)
                    {
                        int c = j * w + w / 2;
                        Say($"[read]   row {j}: {mean[k, j]:F1}; bin {m.Weather.Bin[c]} place {m.Snow.BinPlace[c]} hvf {m.Weather.Hvf[c] * 10} ft");
                    }
                // The pool's share steps with the height above the floor, kept in 10 ft (WeatherTerrain.Hvf): one such step is allowed.
                double hvfStep = k <= 1 ? pool * 10.0 / p.PoolDepthFeet : k == 2 ? pool * 10.0 / p.PoolDepthFeet * 20 : 0;
                Assert.That(worst, Is.LessThanOrEqualTo(Math.Max(1.0, 4 * avg + span / 50) + hvfStep), $"{names[k]}: no step at a bin or band edge");
            }
        }

        /// <summary>Every thread's allocations so far (CoreCLR's GC.GetTotalAllocatedBytes; Unity's Mono hasn't it: −1).</summary>
        static long TotalAllocated()
        {
            var m = typeof(GC).GetMethod("GetTotalAllocatedBytes", new[] { typeof(bool) });
            return m == null ? -1 : (long)m.Invoke(null, new object[] { true });
        }

        [Test, Parallelizable(ParallelScope.None)] // task 0.4: counts the whole process's allocations; see AssemblyInfo.cs
        public void BG02_TheSnowsHoursAllocateNothingOnceWarm()
        {
            if (TotalAllocated() < 0) Assert.Ignore("needs CoreCLR's GC.GetTotalAllocatedBytes (the .NET test run has it)");
            // The hour runs on the snow's crew threads, so this counts the whole process: a face day of snowy December hours
            // (shedding, drifting, melting) after a warm-up day, against the same world without the snow's inputs (its weather,
            // events and snapshots allocate what they allocate; the snow adds nothing).
            long Bytes(bool snow, out long water)
            {
                var w = new SimWorld(Pacing.Default, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, 2026, weather: SimWeatherTests.Golden());
                SnowFieldTests.SendInputs(w, Small, snow);
                SnowFieldTests.RunTo(w, 12, 3, Pacing.Default.IdleTickFactor);
                long end = w.FaceMs + SimWorld.DayMs;
                while (w.FaceMs < end) w.Step(Pacing.Default.IdleTickFactor);
                w.JoinSnow();
                long before = TotalAllocated();
                end = w.FaceMs + SimWorld.DayMs;
                while (w.FaceMs < end) w.Step(Pacing.Default.IdleTickFactor);
                w.JoinSnow();
                long bytes = TotalAllocated() - before;
                water = w.SnowToday.End;
                return bytes;
            }
            long with = Bytes(true, out long water), without = Bytes(false, out _);
            Say($"[alloc] a snowy face day on {W} × {D} cells: {with:N0} bytes allocated with the snow, {without:N0} without it " +
                $"({(with - without) / 24.0:N0} a face hour for the snow); {water / 1e6 / (W * D) * 1000:F0} mm of water a cell");
            Assert.That(water, Is.GreaterThan(0), "snow to work on");
            Assert.That((with - without) / 24, Is.LessThan(256), "BG-02: the snow's hour allocates nothing (the test runner's noise aside)");
        }

        [Test, Category("Nightly"), Explicit("A whole season on the 2 km crop: the nightly job (repo-checks.yml sim-nightly)")]
        [Parallelizable(ParallelScope.None)] // task 0.4: watches the whole process's managed memory; see AssemblyInfo.cs
        public void SN_ASeasonOnJacksonHoleBalancesEveryDayAndHoldsItsMemory()
        {
            var m = SnowRunoutProbeTests.JacksonHole();
            var p = Pacing.Default;
            var w = SnowFieldTests.World(p, true, m);
            int bad = 0, days = 0;
            long worst = 0, december = -1, deepest = 0;
            var sw = Stopwatch.StartNew();
            SnowFieldTests.RunTo(w, 4, 30, p.IdleTickFactor, l =>
            {
                days++;
                if (l.Balance != 0) { bad++; worst = Math.Max(worst, Math.Abs(l.Balance)); }
                int j = (int)(w.FaceMs / SimWorld.DayMs) % w.Calendar.YearFaceDays;
                if (december < 0 && w.Calendar.Month(j) == 12) december = GC.GetTotalMemory(true);
            });
            long april = GC.GetTotalMemory(true);
            for (int c = 0; c < m.Mm.Length; c++) deepest = Math.Max(deepest, w.Snow.PackAt(c % 256, c / 256).Swe);
            Say($"[soak] {days} face days to April 30 in {sw.Elapsed.TotalSeconds:F0} s; ledger misses {bad} (worst {worst} µm); managed " +
                $"memory {december / 1e6:F1} MB in December, {april / 1e6:F1} MB at the end; the deepest cell {deepest / 25_400.0:F1} in of water");
            Assert.That(bad, Is.Zero, "every day's ledger balances, all season");
            Assert.That(april - december, Is.LessThan(8_000_000), "the managed memory doesn't grow through the season");
        }
    }
}
