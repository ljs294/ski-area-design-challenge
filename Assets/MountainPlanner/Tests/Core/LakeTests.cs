using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Water;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.09: the water-body map (LK-01 v2, LK-02) and the lakes' ice (LK-03 v2…LK-08 v2).</summary>
    public sealed class LakeTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        static WaterBodyTuning T => SimWater.Tuning(P);

        // ================= fixtures =================

        /// <summary>
        /// The golden's four bodies on a 16 × 16 grid, all with their surface at one height (6,500 ft, Jackson Hole's valley;
        /// the report puts them at each site's base) so only the depth tells them apart:
        /// the streams body, a 6 ft pond, a 30 ft lake and a 120 ft lake. No air cell: they read the hour at their height.
        /// </summary>
        internal static WaterBodyTable FourBodies(int surfaceFeet = 6500)
        {
            var mask = new byte[16 * 16];
            for (int k = 0; k < 16; k++) mask[k] = 1;              // a stream along the south edge
            for (int k = 32; k < 64; k++) mask[k] = 2;             // the pond
            for (int k = 64; k < 128; k++) mask[k] = 3;            // the 30 ft lake
            for (int k = 128; k < 256; k++) mask[k] = 4;           // the 120 ft lake
            var bodies = new[]
            {
                new WaterBody(WaterBodyKind.Streams, 0, 16, -1, 0, 0, surfaceFeet, 0, P.StreamsDepth, false),
                new WaterBody(WaterBodyKind.Lake, 0x10, 32, -1, 0, 0, surfaceFeet, 25_000, 60, false),
                new WaterBody(WaterBodyKind.Lake, 0x20, 64, -1, 0, 0, surfaceFeet, 36_000, 300, false),
                new WaterBody(WaterBodyKind.Lake, 0x30, 128, -1, 0, 0, surfaceFeet, 51_000, 1200, false),
            };
            return WaterBodyTable.Of(16, 16, 0, 0, mask, bodies, P.MaxPonds);
        }

        static SimDesign Empty() => new SimDesign(1, 1, 77, DayHours.Default, Array.Empty<LiftInput>(), 1);

        static SimWorld World(WaterBodyTable t = null, SimWeather weather = null)
        {
            var w = new SimWorld(P, Empty(), SimSite.None, 2026, weather: weather);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, t ?? FourBodies()));
            return w;
        }

        /// <summary>A share grid with discs, rectangles and lines painted in (255 = water).</summary>
        sealed class Grid
        {
            public readonly int W, D;
            public readonly byte[] Share;
            public readonly int[] Feet;
            public Grid(int w, int d, int feet = 1000)
            {
                W = w; D = d; Share = new byte[w * d]; Feet = new int[w * d];
                for (int k = 0; k < Feet.Length; k++) Feet[k] = feet;
            }
            public Grid Disc(int ci, int cj, int r)
            {
                for (int j = 0; j < D; j++)
                    for (int i = 0; i < W; i++)
                        if ((i - ci) * (i - ci) + (j - cj) * (j - cj) <= r * r) Share[j * W + i] = 255;
                return this;
            }
            public Grid Rect(int i0, int j0, int i1, int j1)
            {
                for (int j = j0; j < j1; j++)
                    for (int i = i0; i < i1; i++) Share[j * W + i] = 255;
                return this;
            }
            public WaterBodyTable Build(int originX = 0, int originY = 0, WaterBodyTuning? t = null) =>
                WaterBodyTable.Build(W, D, Share, Feet, originX, originY, t ?? T);
            public int Body(WaterBodyTable t, int i, int j) => t.BodyAt(j * W + i);
        }

        static int Lakes(WaterBodyTable t)
        {
            int n = 0;
            for (int b = 0; b < t.Count; b++) if (t[b].Kind == WaterBodyKind.Lake) n++;
            return n;
        }

        // ================= LK-01 v2: the map =================

        [Test]
        public void LK01_AStreamJoiningTwoPondsLeavesTwoLakesAndTheStreams()
        {
            var g = new Grid(120, 60).Disc(25, 30, 8).Disc(95, 30, 8).Rect(33, 30, 87, 31);
            var t = g.Build();
            Assert.That(Lakes(t), Is.EqualTo(2));
            Assert.That(t[WaterBodyTable.Streams].Kind, Is.EqualTo(WaterBodyKind.Streams));
            int a = g.Body(t, 25, 30), b = g.Body(t, 95, 30);
            Assert.That(a, Is.GreaterThan(0)); Assert.That(b, Is.GreaterThan(0)); Assert.That(a, Is.Not.EqualTo(b));
            Assert.That(g.Body(t, 60, 30), Is.EqualTo(WaterBodyTable.Streams), "the stream between them");
            Assert.That(g.Body(t, 60, 40), Is.EqualTo(-1), "land");
        }

        [Test]
        public void LK01_APolygonRiverIsTheStreamsNotALake()
        {
            // 32 m wide (4 cells) and 1.2 km long: wide enough to survive the erosion, but long and thin.
            var g = new Grid(170, 20).Rect(10, 8, 160, 12);
            var t = g.Build();
            Assert.That(Lakes(t), Is.Zero);
            Assert.That(g.Body(t, 80, 10), Is.EqualTo(WaterBodyTable.Streams));
        }

        [Test]
        public void LK01_ABayStaysWithItsLake()
        {
            // A 2-cell-wide bay 6 cells long off a round lake: gone in the erosion, grown back from the lake.
            var g = new Grid(60, 60).Disc(30, 30, 10).Rect(40, 29, 44, 31);
            var t = g.Build();
            Assert.That(Lakes(t), Is.EqualTo(1));
            Assert.That(g.Body(t, 43, 30), Is.EqualTo(g.Body(t, 30, 30)));
        }

        [Test]
        public void LK01_APondUnderAnAcreIsTheStreams()
        {
            var small = new Grid(40, 40).Rect(10, 10, 17, 18).Build();   // 56 cells = 3,584 m², 0.89 acre
            Assert.That(Lakes(small), Is.Zero);
            var big = new Grid(40, 40).Rect(10, 10, 19, 18).Build();     // 72 cells = 4,608 m², 1.14 acres
            Assert.That(Lakes(big), Is.EqualTo(1));
        }

        [Test]
        public void LK01_IdsStayPutWhenTheGridShifts()
        {
            var a = new Grid(80, 80).Disc(30, 30, 8).Disc(55, 50, 6).Build();
            // The same lakes on a grid whose origin is 3 cells further west and 2 cells south.
            var b = new Grid(83, 82).Disc(33, 32, 8).Disc(58, 52, 6).Build(-3 * 8000, -2 * 8000);
            var ida = new List<ulong>(); var idb = new List<ulong>();
            for (int k = 1; k < a.Count; k++) ida.Add(a[k].Id);
            for (int k = 1; k < b.Count; k++) idb.Add(b[k].Id);
            Assert.That(ida.Count, Is.EqualTo(2));
            Assert.That(idb, Is.EqualTo(ida));
            Assert.That(ida[0], Is.Not.EqualTo(ida[1]));
        }

        [Test]
        public void LK02_DepthFollowsTheShore()
        {
            // A 20-cell disc (160 m, 525 ft radius); the land rises 0.1 ft per ft from the shore (100 ‰).
            var g = new Grid(80, 80).Disc(40, 40, 20);
            for (int j = 0; j < 80; j++)
                for (int i = 0; i < 80; i++)
                {
                    double d = Math.Sqrt((i - 40) * (i - 40) + (j - 40) * (j - 40)) - 20;
                    if (d > 0) g.Feet[j * 80 + i] = 1000 + (int)Math.Round(d * 26.25 * 0.1);
                }
            var t = g.Build();
            Assert.That(Lakes(t), Is.EqualTo(1));
            var lake = t[1];
            double r = Math.Sqrt(lake.AreaSquareMetres / Math.PI) / 0.3048;
            Assert.That(lake.DepthTenthsFt / 10.0, Is.EqualTo(0.1 * r / 3).Within(3), "median slope × radius ÷ 3");
            Assert.That(lake.SurfaceFeet, Is.EqualTo(1000));
            Assert.That(lake.Clipped, Is.False);
            // Flat land all round: the 1.6 ft floor.
            var flat = new Grid(80, 80).Disc(40, 40, 20).Build();
            Assert.That(flat[1].DepthTenthsFt, Is.EqualTo(P.LakeDepthMin));
            // Steep land: capped at radius ÷ 3.
            var steep = new Grid(80, 80, 1000).Disc(40, 40, 20);
            for (int k = 0; k < steep.Feet.Length; k++) if (steep.Share[k] == 0) steep.Feet[k] = 3000;
            var s = steep.Build();
            Assert.That(s[1].DepthTenthsFt / 10.0, Is.EqualTo(Math.Min(P.LakeDepthMax / 10.0, Math.Sqrt(s[1].AreaSquareMetres / Math.PI) / 0.3048 / 3)).Within(1));
        }

        [Test]
        public void LK01_ALakeTheRingCutsIsClipped()
        {
            var t = new Grid(40, 40).Disc(0, 20, 10).Build();
            Assert.That(Lakes(t), Is.EqualTo(1));
            Assert.That(t[1].Clipped, Is.True);
        }

        [Test]
        public void LK01_TheSmallestLakesBeyondTheLimitJoinTheStreams()
        {
            var p = Pacing.Parse("MaxLakes = 2");
            var g = new Grid(120, 40).Disc(15, 20, 9).Disc(50, 20, 7).Disc(90, 20, 5);
            var t = g.Build(t: SimWater.Tuning(p));
            Assert.That(Lakes(t), Is.EqualTo(2));
            Assert.That(g.Body(t, 90, 20), Is.EqualTo(WaterBodyTable.Streams), "the smallest");
        }

        [Test]
        public void LK01_APondComesAsANewTable()
        {
            var t = new Grid(40, 40).Disc(20, 20, 8).Build();
            var cells = new[] { 0, 1, 40, 41 };
            var u = t.WithPond(0xABC, cells, 120, 990);
            Assert.That(u.Count, Is.EqualTo(t.Count + 1));
            Assert.That(u[u.Count - 1].Kind, Is.EqualTo(WaterBodyKind.Pond));
            Assert.That(u.BodyAt(41), Is.EqualTo(u.Count - 1));
            Assert.That(t.BodyAt(41), Is.EqualTo(-1), "the old table is unchanged");
            Assert.That(u.Hash, Is.Not.EqualTo(t.Hash));
        }

        [Test]
        public void LK01_TheMapIsTheSameEveryTime()
        {
            var g = new Grid(120, 60).Disc(25, 30, 8).Disc(95, 30, 8).Rect(33, 30, 87, 31);
            Assert.That(g.Build().Hash, Is.EqualTo(g.Build().Hash));
        }

        // ================= LK-03…LK-08: the ice =================

        static int HoursToFreeze(SimWorld w, int b, int temp, int max = 100_000)
        {
            for (int h = 1; h <= max; h++)
            {
                w.StepLake(b, w.WaterTable[b], temp, 0, 0, 30);
                if (w.FrozenOf(b)) return h;
            }
            return -1;
        }

        [Test]
        public void LK03_TheLagGrowsWithDepth()
        {
            var w = World();
            int pond = HoursToFreeze(w, 1, 290), lake = HoursToFreeze(w, 2, 290);
            // 2 calendar °F-days per foot; a face hour at 29 °F is 3 × 121 ÷ 42 ÷ 24 calendar °F-days.
            double perHour = 3.0 * P.SeasonCalendarDays / P.SeasonDays / 24;
            Assert.That(pond, Is.EqualTo((int)Math.Ceiling(P.LakeLagPerFoot / 10.0 * 6 / perHour)).Within(1));
            Assert.That(lake, Is.EqualTo((int)Math.Ceiling(P.LakeLagPerFoot / 10.0 * 30 / perHour)).Within(1));
        }

        [Test]
        public void LK03_WarmHoursGiveTheHeatBack()
        {
            var w = World();
            int straight = HoursToFreeze(World(), 1, 290);
            for (int h = 0; h < straight / 2; h++) w.StepLake(1, w.WaterTable[1], 290, 0, 0, 30);
            for (int h = 0; h < straight / 2; h++) w.StepLake(1, w.WaterTable[1], 350, 0, 0, 30);   // as warm as it was cold, longer
            Assert.That(w.FrozenOf(1), Is.False);
            Assert.That(HoursToFreeze(w, 1, 290), Is.EqualTo(straight).Within(1), "the lag starts again from nothing");
        }

        [Test]
        public void LK04_IceGrowsByStefansLaw()
        {
            var w = World();
            HoursToFreeze(w, 2, 200);
            for (int h = 0; h < 24 * 20; h++) w.StepLake(2, w.WaterTable[2], 200, 0, 0, 30);
            // 20 face days at 20 °F: 12 × 20 × 121 ÷ 42 = 691 calendar °F-days → 0.8 × √691 = 21 in.
            double fdd = 12.0 * 20 * P.SeasonCalendarDays / P.SeasonDays;
            Assert.That(w.IceOf(2) / 100.0, Is.EqualTo(P.IceAlpha / 100.0 * Math.Sqrt(fdd)).Within(0.2));
        }

        [Test]
        public void LK04_IceStillGrowsUnderDeepSnow()
        {
            var w = World();
            HoursToFreeze(w, 2, 200);
            for (int h = 0; h < 24 * 10; h++) w.StepLake(2, w.WaterTable[2], 200, 0, 0, 30);
            // A foot of snow water lands: the cap holds it at 24 in of snow.
            w.StepLake(2, w.WaterTable[2], 200, 1200, 1000, 30);
            Assert.That(w.IceSnowOf(2), Is.EqualTo(P.IceSnowMax));
            int before = w.IceOf(2);
            for (int h = 0; h < 24 * 10; h++) w.StepLake(2, w.WaterTable[2], 200, 0, 0, 30);
            Assert.That(w.IceOf(2), Is.GreaterThan(before), "no hour rounds the growth away");
            // Insulated: slower than bare ice would have grown.
            var bare = World();
            HoursToFreeze(bare, 2, 200);
            for (int h = 0; h < 24 * 20; h++) bare.StepLake(2, bare.WaterTable[2], 200, 0, 0, 30);
            Assert.That(w.IceOf(2), Is.LessThan(bare.IceOf(2)));
            Assert.That(w.LakeStateOf(2), Is.EqualTo(WaterSurfaceState.SnowCoveredIce));
            Assert.That(bare.LakeStateOf(2), Is.EqualTo(WaterSurfaceState.Ice));
        }

        [Test]
        public void LK08_ThinNewIceReadsOpenButKeepsGrowing()
        {
            var w = World();
            HoursToFreeze(w, 1, 300);
            Assert.That(w.FrozenOf(1), Is.True);
            Assert.That(w.LakeStateOf(1), Is.EqualTo(WaterSurfaceState.OpenWater), "under an inch");
            w.StepLake(1, w.WaterTable[1], 300, 0, 0, 30);
            Assert.That(w.FrozenOf(1), Is.True, "freezing doesn't reset itself");
            for (int h = 0; h < 24 * 5; h++) w.StepLake(1, w.WaterTable[1], 200, 0, 0, 30);
            Assert.That(w.LakeStateOf(1), Is.EqualTo(WaterSurfaceState.Ice));
        }

        [Test]
        public void LK06_ASlowThawStillThaws()
        {
            var w = World();
            HoursToFreeze(w, 2, 200);
            for (int h = 0; h < 24 * 10; h++) w.StepLake(2, w.WaterTable[2], 200, 0, 0, 30);
            int before = w.IceOf(2);
            for (int h = 0; h < 24 * 2; h++) w.StepLake(2, w.WaterTable[2], 330, 0, 0, 30);   // 33 °F
            Assert.That(w.IceOf(2), Is.LessThan(before));
        }

        [Test]
        public void LK06_TheStreamsOpenBeforeADeepLake()
        {
            var w = World();
            // Sixty face days at 10 °F, then 40 °F until everything is open.
            for (int h = 0; h < 24 * 60; h++)
                for (int b = 0; b < 4; b++) w.StepLake(b, w.WaterTable[b], 100, 0, 0, (short)(h / 24));
            for (int b = 0; b < 4; b++) Assert.That(w.FrozenOf(b), Is.True, $"body {b} froze");
            Assert.That(w.IceOf(0), Is.LessThanOrEqualTo(P.StreamsIceMax), "moving water caps the streams' ice");
            var opened = new int[4];
            for (int h = 0; h < 24 * 200; h++)
                for (int b = 0; b < 4; b++)
                {
                    if (opened[b] > 0) continue;
                    w.StepLake(b, w.WaterTable[b], 400, 0, 0, (short)(60 + h / 24));
                    if (!w.FrozenOf(b)) opened[b] = h + 1;
                }
            Assert.That(opened, Has.All.GreaterThan(0));
            Assert.That(opened[0], Is.LessThan(opened[1]), "the streams before the pond");
            Assert.That(opened[1], Is.LessThan(opened[3]), "the pond before the deep lake");
        }

        [Test]
        public void LK08_ASeeSawOctoberDoesNotFlicker()
        {
            var w = World();
            int flips = 0;
            bool was = false;
            for (int d = 0; d < 20; d++)
                for (int h = 0; h < 24; h++)
                {
                    w.StepLake(0, w.WaterTable[0], d % 2 == 0 ? 250 : 380, 0, 0, (short)d);
                    if (w.FrozenOf(0) != was) { was = !was; flips++; }
                }
            Assert.That(flips, Is.LessThanOrEqualTo(20), "at most one change a day");
        }

        [Test]
        public void LK05_SnowOnOpenWaterIsASink()
        {
            var w = World();
            w.StepLake(3, w.WaterTable[3], 300, 50, 1000, 30);
            Assert.That(w.IceSnowOf(3), Is.Zero);
            Assert.That(w.SinkOf(3), Is.EqualTo(5000));
        }

        [Test]
        public void LK_TheHourAllocatesNothing()
        {
            var w = World();
            for (int b = 0; b < 4; b++) w.StepLake(b, w.WaterTable[b], 200, 10, 900, 30);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int h = 0; h < 1000; h++)
                for (int b = 0; b < 4; b++) w.StepLake(b, w.WaterTable[b], h % 48 < 24 ? 200 : 360, 10, 900, (short)(h / 24));
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.Zero);
        }

        // ================= the world: reload, the table, the golden =================

        [Test]
        public void LK_AReloadMidWinterGivesTheSameFuture()
        {
            var wx = SimWeatherTests.Golden();
            var cal = new LabelCalendar(P);
            var a = World(weather: wx);
            SeasonRunner.Run(a, null, (cal.December1FaceDay + 20) * Day + 5 * Hour, null);
            Assert.That(a.FrozenOf(0), Is.True, "the streams froze by late December");
            var b = SimSidecar.Read(a.SaveState(), P, a.Design, SimSite.None, out _, wx);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "the records hash the same before the table comes back");
            b.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, FourBodies()));
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()), "and after");
            var ha = new List<ulong>(); var hb = new List<ulong>();
            long until = (cal.December1FaceDay + 30) * Day;
            SeasonRunner.Run(a, null, until, (i, x) => ha.Add(x));
            SeasonRunner.Run(b, null, until, (i, x) => hb.Add(x));
            Assert.That(hb, Is.EqualTo(ha));
        }

        [Test]
        public void LK_AChangedTableKeepsItsLakesById()
        {
            var w = World();
            for (int h = 0; h < 24 * 30; h++)
                for (int b = 0; b < 4; b++) w.StepLake(b, w.WaterTable[b], 150, 0, 0, 30);
            int ice = w.IceOf(3);
            // A backfill drops the pond and nudges the deep lake's centroid by 20 m (inside half its radius): it keeps its ice.
            var old = FourBodies();
            var mask = (byte[])old.Mask.Clone();
            for (int k = 0; k < mask.Length; k++) mask[k] = mask[k] == 2 ? (byte)1 : mask[k] == 3 ? (byte)2 : mask[k] == 4 ? (byte)3 : mask[k];
            var moved = new WaterBody(WaterBodyKind.Lake, 0x31, 128, -1, 20_000, 0, 6500, 51_000, 1200, false);
            var t = WaterBodyTable.Of(16, 16, 0, 0, mask, new[] { old[0], old[2], moved }, P.MaxPonds);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, t));
            Assert.That(w.LakeCount, Is.EqualTo(3));
            Assert.That(w.LakeIdOf(2), Is.EqualTo(0x31UL));
            Assert.That(w.IceOf(2), Is.EqualTo(ice));
        }

        /// <summary>A generated Jackson Hole label year (the frozen rows, seed as the golden's) with the four bodies: each body's day.</summary>
        static (SimWorld World, ulong Fold, int[,] Daily) PlayTheYear(SimWeather wx, WaterBodyTable t, int surfaceFeet = 6500)
        {
            var w = World(t, wx);
            var cal = new LabelCalendar(P);
            int days = cal.YearFaceDays;
            var daily = new int[days, t.Count * 2];
            var h = new StateHasher();
            SeasonRunner.Run(w, null, days * Day - Hour, (i, x) =>
            {
                h.Add(i); h.Add(x);
                if ((i + 1) % 24 != 0) return;
                int d = (int)(i / 24);
                for (int b = 0; b < t.Count; b++) { daily[d, 2 * b] = w.IceOf(b); daily[d, 2 * b + 1] = w.IceSnowOf(b); }
            });
            return (w, h.Value, daily);
        }

        /// <summary>The exit golden: the generated winter freezes and thaws each lake on its own pinned dates.</summary>
        public const ulong LakeWinterFold = 0xA3444D79FA83272FUL;   // pinned at 4.09 PR A (Pacing v8)
        /// <summary>Face days of the label year (0 = October 1, 21 = December 1, 63 = April 1): streams, 6 ft pond, 30 ft and 120 ft lakes.</summary>
        static readonly int[] IceOn = { 2, 7, 14, 21 }, IceOut = { 70, 68, 75, 78 };

        [Test]
        public void LK_AGeneratedWinterFreezesEachLakeOnItsOwnDates()
        {
            var (w, fold, _) = PlayTheYear(SimWeatherTests.Golden(), FourBodies());
            var on = new int[4]; var off = new int[4];
            for (int b = 0; b < 4; b++) { on[b] = w.IceOnOf(b); off[b] = w.IceOutOf(b); }
            TestContext.Progress.WriteLine($"lake winter fold: 0x{fold:X16}; ice on {string.Join(", ", on)}; ice out {string.Join(", ", off)}");
            for (int b = 0; b < 4; b++)
            {
                Assert.That(on[b], Is.GreaterThanOrEqualTo(0), $"body {b} froze");
                Assert.That(off[b], Is.GreaterThan(on[b]), $"body {b} opened after it froze");
            }
            Assert.That(on[0], Is.LessThan(on[3]), "the streams freeze before the deep lake");
            Assert.That(off[0], Is.LessThan(off[3]), "and open before it");
            Assert.That(on, Is.EqualTo(IceOn), "pinned ice-on days");
            Assert.That(off, Is.EqualTo(IceOut), "pinned ice-out days");
            Assert.That(fold, Is.EqualTo(LakeWinterFold), "a behaviour change to the lakes needs an approved re-pin");
        }

        // ================= the report (demo 101) =================

        /// <summary>
        /// The chart report's data: every body's ice and snow each face day of a generated year, as CSV in
        /// <c>test-results/lakes</c>, for Jackson Hole (West) and an East site. <c>tools/lakes/report.py</c> draws it.
        /// </summary>
        [Test, Explicit("Demo 101: the lakes report's data")]
        public void LK_WriteTheReportData()
        {
            string root = Path.GetFullPath(Path.Combine(TestData.Folder("formats"), "..", "..", "test-results", "lakes"));
            Directory.CreateDirectory(root);
            var cal = new LabelCalendar(P);
            foreach (var (name, site) in new[] { ("jackson-hole", WeatherFixtures.JacksonHole), ("jackson-nh", WeatherFixtures.JacksonNh) })
            {
                var wx = SimWeather.Own(SimWeatherTests.Frozen(site));
                var t = FourBodies(wx.BaseFeet);
                var (w, _, daily) = PlayTheYear(wx, t);
                var csv = new StringBuilder("day,month,dom");
                string[] names = { "streams", "pond 6 ft", "lake 30 ft", "lake 120 ft" };
                foreach (var n in names) csv.Append($",{n} ice in,{n} snow in");
                csv.Append('\n');
                for (int d = 0; d < cal.YearFaceDays; d++)
                {
                    csv.Append(d).Append(',').Append(cal.Month(d)).Append(',').Append(cal.Day(d));
                    for (int b = 0; b < t.Count; b++) csv.Append(',').Append(daily[d, 2 * b] / 100.0).Append(',').Append(daily[d, 2 * b + 1] / 10.0);
                    csv.Append('\n');
                }
                File.WriteAllText(Path.Combine(root, name + ".csv"), csv.ToString());
                var dates = new StringBuilder("body,ice_on_day,ice_out_day\n");
                for (int b = 0; b < t.Count; b++) dates.Append(names[b]).Append(',').Append(w.IceOnOf(b)).Append(',').Append(w.IceOutOf(b)).Append('\n');
                File.WriteAllText(Path.Combine(root, name + "-dates.csv"), dates.ToString());
            }
        }
    }
}
