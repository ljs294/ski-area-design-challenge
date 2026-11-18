using System;
using System.Diagnostics;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2: the live snow in the world, on a synthetic 2 km mountain under Jackson Hole's frozen rows: it builds
    /// from bare October ground, the daily ledger balances to the µm, the bytes don't depend on the snow's thread count or
    /// the step size, and a world without the snow's inputs stays as it was.
    /// </summary>
    public sealed class SnowFieldTests
    {
        const int W = 256, D = 256;

        internal sealed class Mountain
        {
            public int[] Mm, Feet;
            public byte[] Canopy;
            public WeatherTerrain Weather;
            public SnowTerrain Snow;
        }

        static Mountain _mountain;
        internal static Mountain Shared => _mountain ??= Build(Pacing.Default);

        /// <summary>A 2 km mountain from 6,600 to 9,300 ft: a main ridge, side gullies, a forest band on the lower slopes.</summary>
        internal static Mountain Build(Pacing p, int W = SnowFieldTests.W, int D = SnowFieldTests.D)
        {
            var m = new Mountain { Mm = new int[W * D], Feet = new int[W * D], Canopy = new byte[W * D] };
            double span = W * 8.0;
            for (int j = 0; j < D; j++)
                for (int i = 0; i < W; i++)
                {
                    double x = i * 8.0, y = j * 8.0;
                    double ridge = Math.Exp(-Math.Pow((x - 0.537 * span) / (0.254 * span), 2));
                    double metres = 2010 + 800 * ridge * (0.55 + 0.45 * y / (D * 8.0)) + 25 * Math.Sin(y / 70) * ridge + 12 * Math.Cos(x / 45 + y / 90);
                    int c = j * W + i;
                    m.Mm[c] = (int)Math.Round(metres * 1000);
                    m.Feet[c] = (int)Math.Round(metres / 0.3048);
                    m.Canopy[c] = (byte)(metres < 2350 ? 200 : metres < 2500 ? 90 : 0);
                }
            m.Weather = WeatherTerrain.Build(W, D, m.Feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(W, D, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            return m;
        }

        internal static readonly SimSite Site = new SimSite(43.59, -110.83);

        internal static SimWorld World(Pacing p, bool snow = true, Mountain m = null)
        {
            m ??= Shared;
            var w = new SimWorld(p, SeasonRunner.GoldenDesign(1, false), Site, 2026, weather: SimWeatherTests.Golden());
            SendInputs(w, m, snow);
            return w;
        }

        /// <summary>The area's inputs, as App sends them after a new game or a load.</summary>
        internal static void SendInputs(SimWorld w, Mountain m, bool snow = true)
        {
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, m.Weather));
            if (snow) w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowTerrain, 0, m.Snow.Clone()));
        }

        /// <summary>Steps into the next hour's flight (the roll has happened, the landing hasn't).</summary>
        internal static void IntoFlight(SimWorld w)
        {
            while (!w.SnowInFlight) w.Step(1);
            w.Step(1); w.Step(1);
            Assert.That(w.SnowInFlight, Is.True);
        }

        /// <summary>Steps to the start of a face day (label month and day), checking each finished day's ledger on the way.</summary>
        internal static int RunTo(SimWorld w, int month, int day, int maxTicks, Action<SnowLedger> eachDay = null)
        {
            int days = 0, last = w.SnowLastDay.Day;
            while (true)
            {
                int j = (int)(w.FaceMs / SimWorld.DayMs) % w.Calendar.YearFaceDays;
                // The face calendar skips labels (42 face days for December to March): the first day on or after the date.
                if (w.FaceMs % SimWorld.DayMs == 0 && Order(w.Calendar.Month(j), w.Calendar.Day(j)) >= Order(month, day)) return days;
                w.Step(maxTicks);
                if (w.SnowLastDay.Day != last)
                {
                    last = w.SnowLastDay.Day;
                    days++;
                    eachDay?.Invoke(w.SnowLastDay);
                }
            }
        }

        static int Order(int month, int day) => (month >= 10 ? month - 10 : month + 2) * 40 + day;

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        [Test]
        public void SN_ANewGameStartsBareAndTheSnowBuildsByJanuary()
        {
            var w = World(Pacing.Default);
            Assert.That(w.Snow.Live, Is.True);
            Assert.That(w.SnowToday.End, Is.Zero, "October 1 is bare");
            var sw = Stopwatch.StartNew();
            int bad = 0;
            long worst = 0;
            int days = RunTo(w, 1, 10, Pacing.Default.IdleTickFactor, l =>
            {
                if (l.Balance != 0) { bad++; worst = Math.Max(worst, Math.Abs(l.Balance)); }
            });
            double s = sw.Elapsed.TotalSeconds;
            var today = w.SnowToday;
            int top = 0, low = 0;
            var feet = Shared.Feet;
            for (int c = 0; c < feet.Length; c++) { if (feet[c] > feet[top]) top = c; if (feet[c] < feet[low]) low = c; }
            double summitIn = w.Snow[top % W, top / W] / 25.4, baseIn = w.Snow[low % W, low / W] / 25.4;
            int types = 0;
            var seen = new bool[11];
            for (int j = 0; j < D; j += 4) for (int i = 0; i < W; i += 4) seen[(int)w.Snow.TypeAt(i, j)] = true;
            foreach (bool b in seen) if (b) types++;
            Say($"[snow] {days} face days to Jan 10 in {s:F1} s; water on the field {today.End / 1e6 / (W * D) * 1000:F0} mm a cell; " +
                $"summit {summitIn:F0} in, base {baseIn:F0} in; {types} types seen; ledger misses {bad} (worst {worst} µm)");
            Assert.That(bad, Is.Zero, "every day's ledger balances");
            Assert.That(today.End, Is.GreaterThan(0), "snow by January");
            Assert.That(summitIn, Is.GreaterThan(baseIn), "more snow up high");
        }

        [Test]
        public void BG02_OneSnowThreadGivesTheSameSnowAsFour()
        {
            var one = World(Pacing.Parse("SnowThreads = 1"));
            var four = World(Pacing.Parse("SnowThreads = 4"));
            RunTo(one, 12, 1, Pacing.Default.IdleTickFactor);
            RunTo(four, 12, 1, Pacing.Default.IdleTickFactor);
            Assert.That(four.Snow.TileHashes, Is.EqualTo(one.Snow.TileHashes));
            Assert.That(four.SnowToday, Is.EqualTo(one.SnowToday));
            Assert.That(one.SnowToday.End, Is.GreaterThan(0));
        }

        [Test]
        public void BG02_EveryStepSizeLandsTheSnowAtTheSameTick()
        {
            // One tick a step against the idle steps: the landing is a boundary both stop on, so the states agree.
            var a = World(Pacing.Default);
            var b = World(Pacing.Default);
            RunTo(a, 11, 20, Pacing.Default.IdleTickFactor);
            RunTo(b, 11, 20, Pacing.Default.IdleTickFactor);
            long end = a.FaceMs + 30 * SimWorld.HourMs;
            while (a.FaceMs < end) a.Step(1);
            while (b.FaceMs < end) b.Step(Pacing.Default.IdleTickFactor);
            Assert.That(b.Tick, Is.EqualTo(a.Tick));
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
        }

        [Test]
        public void SN_AWorldWithoutTheSnowsInputsKeepsItsStaticDepths()
        {
            var w = World(Pacing.Default, snow: false);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 0, 0, 2, 1, new ushort[] { 900, 1200 })));
            RunTo(w, 10, 5, Pacing.Default.IdleTickFactor);
            Assert.That(w.Snow.Live, Is.False);
            Assert.That((w.Snow[0, 0], w.Snow[1, 0]), Is.EqualTo(((ushort)900, (ushort)1200)));
        }

        [Test]
        public void SI12_ASaveMidHourOpensToTheSameStateAndTheSameFuture()
        {
            var a = World(Pacing.Default);
            RunTo(a, 12, 10, Pacing.Default.IdleTickFactor);
            IntoFlight(a);
            byte[] bytes = a.SaveState();
            Assert.That(SimSidecar.VersionOf(bytes), Is.EqualTo(SimSidecar.Version));
            var b = SimSidecar.Read(bytes, Pacing.Default, SeasonRunner.GoldenDesign(1, false), Site, out _, SimWeatherTests.Golden());
            SendInputs(b, Shared);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the same state, the hour still in flight");
            Assert.That(b.SnowInFlight, Is.True);
            long end = a.FaceMs + 2 * SimWorld.DayMs;
            while (a.FaceMs < end) a.Step(Pacing.Default.IdleTickFactor);
            while (b.FaceMs < end) b.Step(Pacing.Default.IdleTickFactor);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "and the same two days after");
            Assert.That(b.Snow.TileHashes, Is.EqualTo(a.Snow.TileHashes));
            Assert.That(bytes.Length, Is.LessThan(3_000_000), $"the save is {bytes.Length:N0} bytes");
            TestContext.Progress.WriteLine($"[snow] a mid-December save of {W}×{D} cells: {bytes.Length:N0} bytes");
        }

        [Test]
        public void SN_ASeedBecomesPackedPowderAndTheLedgerCountsIt()
        {
            var w = World(Pacing.Default);
            var mm = new ushort[16 * 16];
            for (int k = 0; k < mm.Length; k++) mm[k] = 1000;
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, 100, 100, 16, 16, mm)));
            Assert.That(w.Snow.TypeAt(105, 105), Is.EqualTo(SnowType.PP));
            Assert.That(w.Snow[105, 105], Is.EqualTo(1000));
            Assert.That(w.SnowToday.Seeded, Is.EqualTo(256L * 1000 * Pacing.Default.SeedDensity));
            Assert.That(w.SnowToday.Balance, Is.Zero);
        }
    }
}
