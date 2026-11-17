using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Snow;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.07 PR A2, the owner's finding 2 (2026-11-15: "test at least one whale or big snow pile to make sure it works").
    /// SN-09 v4 keeps thin snow bright down to 5 mm of water (was 20 mm) and the ground reads fully white from 2 in (was
    /// 6 in); both touch only thin snow, so a big pile has to come out the same. A whale-shaped pile (4 m tall, 80 m long,
    /// 40 m wide, a cosine-squared mound of packed powder) is seeded on flat valley ground at 6,280 ft on November 19 and
    /// played to December 15 under Jackson Hole's frozen rows, with thin snow bright from 20 mm (before) and from 5 mm
    /// (after). Real whales (SN-26, SN-27) are task 4.16's; this is only a pile of the same shape.
    /// </summary>
    public sealed class SnowWhaleTests
    {
        const int W = 64, D = 64, Cx = 32, Cy = 32;
        const double Tall = 4.0, HalfLong = 40.0, HalfWide = 20.0;

        /// <summary>A flat 512 m valley floor at 6,280 ft, open ground (no canopy).</summary>
        internal static SnowFieldTests.Mountain Valley(Pacing p)
        {
            var m = new SnowFieldTests.Mountain { Mm = new int[W * D], Feet = new int[W * D], Canopy = new byte[W * D] };
            int mm = (int)Math.Round(6280 * 304.8);
            for (int c = 0; c < W * D; c++) { m.Mm[c] = mm; m.Feet[c] = 6280; }
            m.Weather = WeatherTerrain.Build(W, D, m.Feet, -W / 2 * 8000, -D / 2 * 8000, (W / 5, D / 5, W * 4 / 5, D * 4 / 5), D / 2 * W + W / 2,
                                             SimWeather.TerrainTuning(p));
            m.Snow = SnowTerrain.Build(W, D, m.Mm, m.Canopy, m.Weather, (int[])p.WindExposure.Clone());
            return m;
        }

        /// <summary>The whale's height (m) over a cell's centre: cos² of the distance across its ellipse, 0 outside it.</summary>
        internal static double Whale(int x, int y)
        {
            double dx = (x - Cx) * 8.0 / HalfLong, dy = (y - Cy) * 8.0 / HalfWide, r = Math.Sqrt(dx * dx + dy * dy);
            if (r >= 1) return 0;
            double c = Math.Cos(r * Math.PI / 2);
            return Tall * c * c;
        }

        /// <summary>One day's record: water (µm) and depth (mm) along the long axis (x = 32…63) and across it (y = 32…63).</summary>
        internal sealed class Day
        {
            public int Month, Date;
            public int[] LongSwe = new int[W - Cx], LongDepth = new int[W - Cx], AcrossSwe = new int[D - Cy], AcrossDepth = new int[D - Cy];
            public long Balance;
        }

        /// <summary>Plays the whale from its seeding to December 15 under <paramref name="p"/>, one record a face day.</summary>
        internal static List<Day> Play(Pacing p)
        {
            var m = Valley(p);
            var w = SnowValleyProbeTests.World(p, m);
            SnowFieldTests.RunTo(w, 11, 19, p.IdleTickFactor);
            w.JoinSnow();
            // The seed replaces what a cell holds, so a cell under the whale keeps its own snow where that's deeper.
            var mm = new ushort[W * D];
            for (int y = 0; y < D; y++)
                for (int x = 0; x < W; x++)
                    mm[y * W + x] = (ushort)Math.Max(w.Snow[x, y], (int)Math.Round(Whale(x, y) * 1000));
            int x0 = Cx - (int)(HalfLong / 8) - 1, y0 = Cy - (int)(HalfWide / 8) - 1, sw = 2 * ((int)(HalfLong / 8) + 1) + 1, sh = 2 * ((int)(HalfWide / 8) + 1) + 1;
            var rect = new ushort[sw * sh];
            for (int j = 0; j < sh; j++) for (int i = 0; i < sw; i++) rect[j * sw + i] = mm[(y0 + j) * W + x0 + i];
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetSnowpack, 0, new SimSnowpack(W, D, x0, y0, sw, sh, rect)));

            var days = new List<Day>();
            int last = w.SnowLastDay.Day;
            while (!SnowValleyProbeTests.AtOrPast(w, 12, 15))
            {
                w.Step(p.IdleTickFactor);
                if (w.SnowLastDay.Day == last) continue;
                last = w.SnowLastDay.Day;
                w.JoinSnow();
                int j = (int)(w.FaceMs / SimWorld.DayMs) % w.Calendar.YearFaceDays;
                var d = new Day { Month = w.Calendar.Month(j), Date = w.Calendar.Day(j), Balance = w.SnowLastDay.Balance };
                for (int k = 0; k < W - Cx; k++) { var pk = w.Snow.PackAt(Cx + k, Cy); d.LongSwe[k] = pk.Swe; d.LongDepth[k] = pk.DepthMm; }
                for (int k = 0; k < D - Cy; k++) { var pk = w.Snow.PackAt(Cx, Cy + k); d.AcrossSwe[k] = pk.Swe; d.AcrossDepth[k] = pk.DepthMm; }
                days.Add(d);
            }
            return days;
        }

        static readonly Pacing Before = Pacing.Parse("SnowThinUm = 20000");

        [Test]
        public void SN09v4_AWhalesMiddleMeltsTheSameAndItsEdgeTapersWithNoRing()
        {
            var after = Play(Pacing.Default);
            var before = Play(Before);
            Assert.That(after.Count, Is.EqualTo(before.Count).And.GreaterThanOrEqualTo(8), "the same face days, November 19 to December 15 (8 on the face calendar)");
            for (int n = 0; n < after.Count; n++)
            {
                var a = after[n];
                string when = $"{a.Month}/{a.Date}";
                Assert.That(a.Balance, Is.Zero, $"{when}: the day's water balances to the µm");
                Assert.That(before[n].Balance, Is.Zero, $"{when}: the day's water balances to the µm (before)");
                // The middle never comes near thin snow, so the rule's change can't reach it: the same to the µm.
                Assert.That(a.LongSwe[0], Is.GreaterThan(Pacing.Default.SnowThinUm * 20), $"{when}: the whale's middle is deep");
                Assert.That(a.LongSwe[0], Is.EqualTo(before[n].LongSwe[0]), $"{when}: the middle's water, after against before");
                Assert.That(a.LongDepth[0], Is.EqualTo(before[n].LongDepth[0]), $"{when}: the middle's depth, after against before");
                // From the middle outward the water and the depth only fall: no ring, step or halo round the pile (2 cm of slack
                // for the depth, where an old dense edge meets new light snow on the ground).
                Tapers(a.LongSwe, 0, $"{when}: water along the whale");
                Tapers(a.AcrossSwe, 0, $"{when}: water across the whale");
                Tapers(a.LongDepth, 20, $"{when}: depth along the whale");
                Tapers(a.AcrossDepth, 20, $"{when}: depth across the whale");
            }
            // The pile is still a pile on December 15 (it lost some water, but its middle stands metres deep).
            Assert.That(after[after.Count - 1].LongDepth[0], Is.GreaterThan(2000), "the whale's middle on December 15 (mm)");
            TestContext.Progress.WriteLine($"[whale] middle on 12/15: {after[after.Count - 1].LongDepth[0] / 25.4:F0} in, " +
                                           $"{after[after.Count - 1].LongSwe[0] / 1000.0:F0} mm of water");
        }

        static void Tapers(int[] outward, int slack, string what)
        {
            for (int k = 1; k < outward.Length; k++)
                if (outward[k] > outward[k - 1] + slack)
                    Assert.Fail($"{what}: {outward[k]} at {k * 8} m out is more than {outward[k - 1]} at {(k - 1) * 8} m");
        }
    }
}
