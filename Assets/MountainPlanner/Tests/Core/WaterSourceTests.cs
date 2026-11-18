using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14 PR B: the water sources (WS-02…WS-11, SI-10, SI-13): the stage–storage curve, the catchments, and the water
    /// each source holds hour by hour. Engine-free; each test names its p4-02 rule.
    /// </summary>
    public sealed class WaterSourceTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        const ulong PondId = 0x40, DamId = 0x50, LakeId = 0x20;
        static readonly LabelCalendar Cal = new LabelCalendar(P);

        // ================= fixtures =================

        /// <summary>A bowl of 400 m²: 20 rings of 20 points from 0.5 m to 3.35 m under full pool.</summary>
        internal static StageCurve Bowl()
        {
            var b = new StageCurve.Builder();
            for (int i = 0; i < 400; i++) b.Add(0.5 + (i % 20) * 0.15);
            return b.Build();
        }

        /// <summary>The lake tests' four bodies plus a pond and a reservoir.</summary>
        internal static WaterBodyTable Table() =>
            LakeTests.FourBodies()
                .WithPond(PondId, new[] { 200, 201, 216, 217 }, 60, 6500, WaterBodyKind.Pond)
                .WithPond(DamId, new[] { 230, 231, 246, 247 }, 150, 6510, WaterBodyKind.Reservoir);

        /// <summary>A pond, a reservoir on a 1 cfs stream and the 30 ft lake as sources.</summary>
        internal static WaterSources Sources(WaterBodyTable t, bool pondEmpty = false, bool pondNatural = true, int springGpm = 0,
                                             int damMilliCfs = 1000, bool damEmpty = false, bool withPond = true)
        {
            var list = new System.Collections.Generic.List<WaterSource>();
            if (withPond) list.Add(new WaterSource(PondId, WaterBodyKind.Pond, t.IndexOf(PondId), 1_981_200, Bowl(), 0, springGpm, pondNatural, pondEmpty));
            list.Add(new WaterSource(DamId, WaterBodyKind.Reservoir, t.IndexOf(DamId), 1_984_248, Bowl(), damMilliCfs, 0, true, damEmpty));
            int lake = t.IndexOf(LakeId);
            list.Add(new WaterSource(LakeId, WaterBodyKind.Lake, lake, 1_981_200, StageCurve.Prism(t[lake].AreaSquareMetres, 30L * 304_800), 0, 0, true, false));
            return new WaterSources(list.ToArray(), t.Width, t.Depth, null);
        }

        static SimDesign Empty() => new SimDesign(1, 1, 77, DayHours.Default, Array.Empty<LiftInput>(), 1);

        internal static SimWorld World(WaterSources s = null, SimWeather weather = null)
        {
            var t = Table();
            var w = new SimWorld(P, Empty(), SimSite.None, 2026, weather: weather);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, t));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, s ?? Sources(t)));
            return w;
        }

        static void RunTo(SimWorld w, long faceMs) => SeasonRunner.Run(w, null, faceMs, null);

        static int Index(SimWorld w, ulong id) => w.Sources.IndexOf(id);

        static void Draw(SimWorld w, ulong id, int gpm) => w.ApplyNow(SimCommand.Of(SimCommandKind.SetTestDraw, 0, new TestDraw(id, gpm)));

        static void AssertBalanced(SimWorld w)
        {
            long all = 0;
            for (int i = 0; i < w.SourceCount; i++)
            {
                Assert.That(w.SourceLedgerOf(i).Held, Is.EqualTo(w.HeldMilliOf(i)), $"source {i}'s ledger balances");
                all += w.HeldMilliOf(i);
            }
            Assert.That(w.ResortWaterLedger.Held, Is.EqualTo(all), "the resort's ledger balances");
        }

        // ================= WS-02: the curve =================

        [Test]
        public void WS02_TheCurveHoldsTheBowlExactlyAtEveryStep()
        {
            var c = Bowl();
            double cubic = 0;
            for (int i = 0; i < 400; i++) cubic += 0.5 + (i % 20) * 0.15;
            Assert.That(c.CapacityGallons, Is.EqualTo((long)Math.Round(cubic * StageCurve.GallonsPerCubicMetre)));
            for (int j = 0; j <= c.Steps; j++)
            {
                double down = j * StageCurve.StepMicrons / 1e6, v = 0;
                int area = 0;
                for (int i = 0; i < 400; i++) { double d = 0.5 + (i % 20) * 0.15; if (d > down) { v += d - down; area++; } }
                Assert.That(c.GallonsAtStep(j), Is.EqualTo((long)Math.Round(v * StageCurve.GallonsPerCubicMetre)).Within(1), $"step {j}");
                Assert.That(c.AreaAtStep(j), Is.EqualTo(area).Within(20), $"area at step {j}");
            }
            Assert.That(c.GallonsAtStep(c.Steps), Is.Zero, "nothing under the floor");
            Assert.That(c.DepthMicrons, Is.GreaterThanOrEqualTo(3_350_000L));
        }

        [Test]
        public void WS02_LevelAndWaterGoBothWays()
        {
            var c = Bowl();
            for (long g = 0; g <= c.CapacityGallons; g += c.CapacityGallons / 97)
            {
                long below = c.BelowFullMicrons(g);
                Assert.That(c.GallonsBelowFull(below), Is.EqualTo(g).Within(c.CapacityGallons / 1000 + 2), $"{g} gal");
            }
            Assert.That(c.BelowFullMicrons(c.CapacityGallons), Is.Zero);
            Assert.That(c.BelowFullMicrons(0), Is.EqualTo(c.DepthMicrons));
            long last = -1;
            for (long g = c.CapacityGallons; g >= 0; g -= 1000) { long b = c.BelowFullMicrons(g); Assert.That(b, Is.GreaterThanOrEqualTo(last)); last = b; }
        }

        [Test]
        public void WS02_ALakeIsAPrism()
        {
            var c = StageCurve.Prism(10_000, 3_048_000);   // 1 ha, 10 ft deep
            Assert.That(c.CapacityGallons, Is.EqualTo((long)Math.Round(10_000 * 3.048 * StageCurve.GallonsPerCubicMetre)));
            Assert.That(c.GallonsBelowFull(914_400), Is.EqualTo((long)Math.Round(10_000 * (3.048 - 0.9144) * StageCurve.GallonsPerCubicMetre)).Within(2));
            Assert.That(c.AreaAt(c.CapacityGallons / 2), Is.EqualTo(10_000));
        }

        // ================= WS-04: catchments =================

        [Test]
        public void WS04_TheLandAboveAPondDrainsIntoIt()
        {
            // A 9 × 9 cone rising from a pit at the centre; the field's edge is higher still, so everything drains to the pit.
            const int n = 9;
            var h = new int[n * n];
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) h[y * n + x] = 1000 * (Math.Abs(x - 4) + Math.Abs(y - 4));
            var c = Catchments.Of(n, n, h);
            var mask = new byte[n * n];
            mask[4 * n + 4] = 2;   // body 1, a pond, at the pit
            var drains = c.DrainsTo(mask, new byte[] { 0, 1 });
            // The field's edge is an outlet of its own (what lies past it is unknown); everything inside drains to the pond.
            for (int y = 1; y < n - 1; y++) for (int x = 1; x < n - 1; x++) Assert.That(drains[y * n + x], Is.EqualTo(1), $"cell {x},{y} drains to the pond");
            // Not a source: it drains to nothing.
            Assert.That(c.DrainsTo(mask, new byte[] { 0, 0 })[0], Is.Zero);
        }

        [Test]
        public void WS04_AStreamTakesTheWaterBelowIt()
        {
            // A slope falling to the south edge; a stream across row 5 (body 0) and a pond on row 2 (body 1).
            const int w = 8, d = 10;
            var h = new int[w * d];
            for (int y = 0; y < d; y++) for (int x = 0; x < w; x++) h[y * w + x] = 100_000 - y * 1000 + Math.Abs(x - 3) * 10;
            var c = Catchments.Of(w, d, h);
            var mask = new byte[w * d];
            for (int x = 0; x < w; x++) mask[5 * w + x] = 1;
            mask[2 * w + 3] = 2;
            var drains = c.DrainsTo(mask, new byte[] { 0, 1 });
            Assert.That(drains[1 * w + 3], Is.EqualTo(1), "above the pond: the pond's");
            Assert.That(drains[8 * w + 3], Is.Zero, "below the stream: nobody's (it drains off the edge)");
            Assert.That(drains[3 * w + 0], Is.Zero, "beside the pond, below its line: the stream's");
        }

        [Test]
        public void WS04_TheSameHeightsAlwaysDrainTheSameWay()
        {
            var h = new int[40 * 30];
            for (int i = 0; i < h.Length; i++) h[i] = (int)(DesignKeyLike(i) % 5000);   // rough, with many ties
            var mask = new byte[h.Length];
            for (int i = 0; i < h.Length; i += 97) mask[i] = (byte)(1 + i % 3);
            var of = new byte[] { 0, 1, 2 };
            var a = Catchments.Of(40, 30, h).DrainsTo(mask, of);
            var b = Catchments.Of(40, 30, (int[])h.Clone()).DrainsTo((byte[])mask.Clone(), of);
            Assert.That(b, Is.EqualTo(a));
        }

        static ulong DesignKeyLike(int i) { unchecked { ulong x = (ulong)i * 0x9E3779B97F4A7C15UL; x ^= x >> 29; return x * 0xBF58476D1CE4E5B9UL; } }

        // ================= the hour =================

        [Test]
        public void WS03_AStreamFillsAReservoirByTheMonth()
        {
            var w = World(Sources(Table(), damEmpty: true));
            int dam = Index(w, DamId);
            Assert.That(w.HeldMilliOf(dam), Is.Zero, "WS-10: it starts empty");
            RunTo(w, Cal.December1FaceDay * Day + 12 * Hour);
            long before = w.SourceLedgerOf(dam).Inflow;
            RunTo(w, Cal.December1FaceDay * Day + 13 * Hour);
            int seconds = (int)Domain.Weather.WeatherMath.DivRound(P.SeasonCalendarDays * 3600L, P.SeasonDays);
            long want = 1000L * P.InflowSeason[11] / 1000 * 7_480_519L * seconds / 1_000_000;
            Assert.That(w.SourceLedgerOf(dam).Inflow - before, Is.EqualTo(want), "an hour of 1 cfs × December's 0.6, in calendar time");
            AssertBalanced(w);
        }

        [Test]
        public void WS05_TheTestDrawStopsAtMinPool()
        {
            var w = World(Sources(Table(), damMilliCfs: 0));
            int pond = Index(w, PondId);
            Draw(w, PondId, TestDraw.MaxGpm);
            RunTo(w, 5 * Day);
            long cap = Bowl().CapacityGallons;
            long floor = cap * P.MinPoolPermille / 1000;
            Assert.That(w.HeldGallonsOf(pond), Is.InRange(floor - 600, floor), "it keeps MinPool back (evaporation still takes a little)");
            Assert.That(w.SourceLedgerOf(pond).Draw, Is.GreaterThan(0));
            int lows = 0;
            for (long q = 0; q < w.Events.Count; q++)
                if (w.Events.TryGet(q, out var e) && e.Type == SimEventType.SourceAtMinPool && e.Entity == pond) lows++;
            Assert.That(lows, Is.InRange(1, 5), "an alert at most once a face day");
            AssertBalanced(w);
        }

        [Test]
        public void WS05_ALakeStopsAtItsDrawdownLimit()
        {
            var w = World();
            int lake = Index(w, LakeId);
            Draw(w, LakeId, TestDraw.MaxGpm);
            RunTo(w, 40 * Day);
            var curve = w.Sources[lake].Curve;
            long floor = curve.GallonsBelowFull((long)P.LakeDrawdownMm * 1000);
            Assert.That(w.HeldGallonsOf(lake), Is.InRange(floor - floor / 500, floor), "the draw stops there (evaporation still takes a little)");
            AssertBalanced(w);
        }

        [Test]
        public void WS06_AFullReservoirSpills()
        {
            var w = World(Sources(Table(), damMilliCfs: 50_000));
            int dam = Index(w, DamId);
            RunTo(w, 3 * Day);
            Assert.That(w.HeldGallonsOf(dam), Is.EqualTo(Bowl().CapacityGallons));
            Assert.That(w.SourceLedgerOf(dam).Spill, Is.GreaterThan(0));
            AssertBalanced(w);
        }

        [Test]
        public void WS07_OpenWaterEvaporates()
        {
            var w = World(Sources(Table(), damMilliCfs: 0));
            int pond = Index(w, PondId);
            for (long h = 0; h < 24; h++) w.HourStepSources(h);   // open water (no weather here to freeze it)
            long lost = w.SourceLedgerOf(pond).Evaporation;
            // 0.01 in a calendar day over ~400 m², 121/42 calendar days a face day: about 0.3 m³ = 80 gal.
            Assert.That(lost / SimWorld.MilliPerGallon, Is.InRange(60, 100));
            AssertBalanced(w);
        }

        [Test]
        public void WS09_APumpedOnlyPondTakesNoSpring()
        {
            var t = Table();
            var w = World(Sources(t, pondNatural: false, springGpm: 500, pondEmpty: true));
            RunTo(w, 2 * Day);
            Assert.That(w.HeldMilliOf(Index(w, PondId)), Is.Zero, "pumped only: nothing comes in by itself");
            var n = World(Sources(t, springGpm: 500, pondEmpty: true));
            RunTo(n, 2 * Day);
            Assert.That(n.SourceLedgerOf(Index(n, PondId)).Spring, Is.GreaterThan(0), "a natural pond takes its spring");
        }

        [Test]
        public void WS10_ANewSourceStartsFullOrEmptyAndAnEditKeepsItsWater()
        {
            var t = Table();
            var w = World(Sources(t, withPond: false, damMilliCfs: 0));
            RunTo(w, 2 * Day);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, Sources(t, pondEmpty: true, damMilliCfs: 0)));
            Assert.That(w.HeldMilliOf(Index(w, PondId)), Is.Zero, "built empty");
            long dam = w.HeldMilliOf(Index(w, DamId));
            // An edit (a new spring) keeps the reservoir's and the pond's water.
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, Sources(t, pondEmpty: true, springGpm: 100, damMilliCfs: 0)));
            Assert.That(w.HeldMilliOf(Index(w, DamId)), Is.EqualTo(dam));
            Assert.That(w.HeldMilliOf(Index(w, PondId)), Is.Zero);
            AssertBalanced(w);
        }

        [Test]
        public void WS11_ATransferStopsAtTheSourcesFloorAndTheTargetsFullPool()
        {
            var w = World(Sources(Table(), pondEmpty: true, pondNatural: false, damMilliCfs: 0));
            int pond = Index(w, PondId), lake = Index(w, LakeId), dam = Index(w, DamId);
            long cap = Bowl().CapacityGallons;
            Assert.That(w.TransferWater(lake, pond, cap / 4), Is.EqualTo(cap / 4));
            Assert.That(w.TransferWater(lake, pond, cap * 10), Is.EqualTo(cap - cap / 4), "only to full pool");
            Assert.That(w.HeldGallonsOf(pond), Is.EqualTo(cap));
            long floor = cap * P.MinPoolPermille / 1000;
            Assert.That(w.TransferWater(dam, pond, 1), Is.Zero, "the pond is full");
            long moved = w.TransferWater(pond, dam, cap);
            Assert.That(moved, Is.Zero, "the reservoir is full too");
            AssertBalanced(w);
            Assert.That(w.ResortWaterLedger.PumpedIn, Is.EqualTo(w.ResortWaterLedger.PumpedOut), "pumped in and out cancel");
            Assert.That(floor, Is.GreaterThan(0));
        }

        [Test]
        public void SI10_TheLedgerBalancesEveryHourOfASeason()
        {
            var w = World(Sources(Table(), springGpm: 200, damMilliCfs: 2000), SimWeatherTests.Golden());
            Draw(w, DamId, 1500);
            Draw(w, LakeId, 3000);
            SeasonRunner.Run(w, null, Cal.YearFaceDays * Day, (h, x) => AssertBalanced(w));
        }

        [Test]
        public void SI13_ARemovedSourceIsAStatedLoss()
        {
            var t = Table();
            var w = World(Sources(t));
            long pond = w.HeldMilliOf(Index(w, PondId));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, Sources(t, withPond: false)));
            Assert.That(w.ResortWaterLedger.Removed, Is.EqualTo(pond));
            AssertBalanced(w);
        }

        [Test]
        public void WS_TheHourAllocatesNothing()
        {
            var w = World();
            Draw(w, PondId, 1000);
            w.HourStepSources(100);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (long h = 101; h < 3000; h++) w.HourStepSources(h);
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }

        [Test]
        public void WS_TheWaterIsTheSameAtAnyStep()
        {
            var a = World(Sources(Table(), springGpm: 200, damMilliCfs: 2000), SimWeatherTests.Golden());
            var b = World(Sources(Table(), springGpm: 200, damMilliCfs: 2000), SimWeatherTests.Golden());
            Draw(a, DamId, 1500); Draw(b, DamId, 1500);
            SeasonRunner.Run(a, null, 40 * Day, null);
            SeasonRunner.Run(b, null, 40 * Day, null, maxStep: 1);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
        }

        [Test]
        public void WS_AReloadKeepsTheWater()
        {
            var a = World(Sources(Table(), springGpm: 200, damMilliCfs: 2000), SimWeatherTests.Golden());
            Draw(a, LakeId, 2000);
            SeasonRunner.Run(a, null, 30 * Day + 5 * Hour, null);
            var b = SimSidecar.Read(a.SaveState(), P, a.Design, SimSite.None, out _, SimWeatherTests.Golden());
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the records hash the same before the sources come back");
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, Table()));
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, Sources(Table(), springGpm: 200, damMilliCfs: 2000)));
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the same sources change nothing");
            SeasonRunner.Run(a, null, 50 * Day, null);
            SeasonRunner.Run(b, null, 50 * Day, null);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the same future");
        }

        [Test]
        public void WS10_AnOlderSavesSourcesStartFull()
        {
            // The v9 fixture (no water block) gets its sources after the load: all of them full, the empty ones too.
            byte[] v9 = System.IO.File.ReadAllBytes(System.IO.Path.Combine(TestData.Folder(System.IO.Path.Combine("formats", "v9-sim")), "golden.sim"));
            var b = SimSidecar.Read(v9, P, SeasonRunner.GoldenDesign(1, false), SnowFieldTests.Site, out _, SimWeatherTests.Golden());
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, Table()));
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, Sources(Table(), pondEmpty: true)));
            Assert.That(b.HeldGallonsOf(b.Sources.IndexOf(PondId)), Is.EqualTo(Bowl().CapacityGallons));
        }

        // ================= WS-04 with the snow (4.07's conditions) =================

        [Test]
        public void WS04_TheCellsRunoffAddsUpToTheSnowsLedger()
        {
            var w = SnowFieldTests.World(P);
            SnowFieldTests.RunTo(w, 4, 1, P.IdleTickFactor);
            int hours = 0, wet = 0;
            long today = w.SnowToday.Runoff;
            int day = w.SnowToday.Day;
            while (hours < 72)
            {
                bool flight = w.SnowInFlight;
                w.Step(1);
                if (flight && !w.SnowInFlight)
                {
                    // A landing: the hour's ledger joined today's (or began a new day's).
                    long hour = w.SnowToday.Day == day ? w.SnowToday.Runoff - today : w.SnowToday.Runoff;
                    long sum = 0;
                    foreach (int r in w.Snow.Field.RunoffHour) sum += r;
                    Assert.That(sum, Is.EqualTo(hour), $"landing {hours}: the cells add up to the ledger");
                    if (sum > 0) wet++;
                    today = w.SnowToday.Runoff; day = w.SnowToday.Day;
                    hours++;
                }
            }
            Assert.That(wet, Is.GreaterThan(0), "April runs off");
        }
    }
}
