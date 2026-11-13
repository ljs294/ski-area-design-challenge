using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// WX-03 v3's check against the SNOTEL pillows, kept as a report (task 4.05 PR A3; before it, a hand note of 2026-11-09).
    /// At each western site's pillows, the record's hours are carried from zref to the pillow's height by the site's own
    /// rows (the night or day lapse, the dew-point lapse, the precipitation gradient, as WX-41's powder count carries them
    /// to mid-mountain), and their snow share by the phase thresholds is summed from October 1 to April 1. WX-03 v3's
    /// criterion: the record's snowfall sits at or above what the pillow holds on April 1 (the pillow is snowfall less
    /// melt). The reference pack (the fidelity tests' melt model) is reported beside it. Explicit: it writes
    /// <c>test-results/weather-pillows/&lt;tag&gt;.json</c> (<c>MP_PILLOW_TAG</c>, default "run") and prints one line a pillow.
    /// </summary>
    [Explicit, Category("WeatherReport")]
    public sealed class WeatherPillowTests
    {
        /// <summary>Stull (2011), the wet bulb WX-37 v1 used: kept here only so the report can show what changed.</summary>
        public static double StullWetBulbF(double tempF, double rh)
        {
            if (rh >= 99) return tempF;
            rh = Math.Max(5, rh);
            double S(double f)
            {
                double t = Psychrometrics.FToC(f);
                double tw = t * Math.Atan(0.151977 * Math.Sqrt(rh + 8.313659)) + Math.Atan(t + rh) - Math.Atan(rh - 1.676331)
                            + 0.00391838 * Math.Pow(rh, 1.5) * Math.Atan(0.023101 * rh) - 4.686035;
                return Psychrometrics.CToF(tw);
            }
            if (tempF < -4) return tempF - Math.Max(0, -4 - S(-4));
            return Math.Min(tempF, S(Math.Min(tempF, 122)));
        }

        sealed class Pillow
        {
            public string Site = "", Station = "";
            public double Feet, Zref, PillowIn, SnowStullIn, SnowGameIn, PackGameIn, GaugeIn, GaugeSnowStullIn, GaugeSnowGameIn;
            public int Winters;
        }

        [Test]
        public void WriteThePillowCheck()
        {
            var all = new List<Pillow>();
            foreach (string site in WeatherFixtures.Sites)
            {
                var (record, _, b) = WeatherFixtures.Screened(site);
                if (record.Snotel.Count == 0) continue;
                var rows = WeatherFixtures.Calibrated(site).Rows;
                var states = WeatherStates.Find(b);
                foreach (var s in record.Snotel)
                    all.Add(Measure(site, s, b, rows, states));
            }

            var sb = new StringBuilder("[\n");
            for (int i = 0; i < all.Count; i++)
            {
                var p = all[i];
                Console.WriteLine($"[pillow] {p.Site} {p.Station} at {p.Feet:0} ft (zref {p.Zref:0}): {p.Winters} winters, pillow Apr 1 {p.PillowIn:0.0} in; " +
                                  $"record snowfall Oct 1-Apr 1 {p.SnowGameIn:0.0} in (Stull {p.SnowStullIn:0.0}), reference pack {p.PackGameIn:0.0} in; " +
                                  $"its gauge {p.GaugeIn:0.0} in, as snow {p.GaugeSnowGameIn:0.0} in (Stull {p.GaugeSnowStullIn:0.0}): " +
                                  (p.GaugeSnowGameIn >= p.PillowIn ? "at or above" : "BELOW"));
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "  {{\"site\":\"{0}\",\"station\":\"{1}\",\"feet\":{2:0},\"zref\":{3:0},\"winters\":{4},\"pillow\":{5:0.00},\"snowStull\":{6:0.00},\"snowGame\":{7:0.00},\"packGame\":{8:0.00},\"gauge\":{9:0.00},\"gaugeSnowStull\":{10:0.00},\"gaugeSnowGame\":{11:0.00}}}{12}\n",
                    p.Site, p.Station, p.Feet, p.Zref, p.Winters, p.PillowIn, p.SnowStullIn, p.SnowGameIn, p.PackGameIn, p.GaugeIn, p.GaugeSnowStullIn, p.GaugeSnowGameIn, i + 1 < all.Count ? "," : ""));
            }
            sb.Append("]\n");
            string tag = Environment.GetEnvironmentVariable("MP_PILLOW_TAG");
            if (string.IsNullOrEmpty(tag)) tag = "run";
            string root = Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName;
            string dir = Path.Combine(root, "test-results", "weather-pillows");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, tag + ".json"), sb.ToString());
            Assert.That(all.Count, Is.GreaterThanOrEqualTo(4), "the four western sites have pillows");
        }

        /// <summary>
        /// PR A3's comparison, for the owner's report: each site's rows before (<c>MP_ROWS_BEFORE</c>, a folder of
        /// <c>&lt;site&gt;.rows</c>, played with Stull) against today's frozen rows (played with WX-37 v2). Per site: the
        /// precipitation gradient by third, the powder threshold, the played November–April snowfall at mid-mountain and the
        /// summit (100 seeds); and the wet bulb's error from the psychrometric equation, Stull against v2, by height. Writes
        /// <c>test-results/weather-a3/report.json</c>.
        /// </summary>
        [Test]
        public void WriteTheCalibrationChange()
        {
            string before = Environment.GetEnvironmentVariable("MP_ROWS_BEFORE");
            if (string.IsNullOrEmpty(before)) Assert.Ignore("MP_ROWS_BEFORE: a folder with each site's rows from before the change (<site>.rows)");
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder("{\"sites\":[\n");
            for (int si = 0; si < WeatherFixtures.Sites.Length; si++)
            {
                string site = WeatherFixtures.Sites[si];
                var old = WeatherRowsFile.FromBytes(File.ReadAllBytes(Path.Combine(before, site + ".rows")));
                var now = WeatherRowsFile.FromBytes(TestData.Bytes(TestData.Folder("weather/" + site), WeatherRowsFile.FileName));
                var (oldMid, oldTop) = Season(old, true);
                var (newMid, newTop) = Season(now, false);
                sb.Append(string.Format(inv,
                    "{{\"site\":\"{0}\",\"zref\":{1},\"mid\":{2},\"summit\":{3},\"gradientBefore\":[{4}],\"gradientAfter\":[{5}],\"powderBefore\":{6},\"powderAfter\":{7},\"midSnowBefore\":{8:0.0},\"midSnowAfter\":{9:0.0},\"topSnowBefore\":{10:0.0},\"topSnowAfter\":{11:0.0}}}{12}\n",
                    site, now.ZrefFeet, now.MidFeet, now.SummitFeet, string.Join(",", old.PrecipGradient), string.Join(",", now.PrecipGradient),
                    old.PowderDayTenthsIn, now.PowderDayTenthsIn, oldMid, newMid, oldTop, newTop, si + 1 < WeatherFixtures.Sites.Length ? "," : ""));
                Console.WriteLine($"[a3] {site}: Dec–Mar gradient {Mean(old.PrecipGradient):0.0} → {Mean(now.PrecipGradient):0.0} ‰/100 ft; powder {old.PowderDayTenthsIn / 10.0} → {now.PowderDayTenthsIn / 10.0} in; " +
                                  $"Nov–Apr snow mid {oldMid:0} → {newMid:0} in, summit {oldTop:0} → {newTop:0} in");
            }
            sb.Append("],\"wetBulb\":[\n");
            bool first = true;
            foreach (int feet in new[] { 0, 6200, 10000 })
                for (int f = -10; f <= 40; f += 5)
                    foreach (int rh in new[] { 20, 40, 60, 80, 95 })
                    {
                        double reference = WeatherFieldsTests.PsychrometricWetBulbF(f, rh, Psychrometrics.StandardPressurePa(feet) / 100);
                        sb.Append(first ? "" : ",\n").Append(string.Format(inv, "{{\"feet\":{0},\"temp\":{1},\"rh\":{2},\"stull\":{3:0.00},\"v2\":{4:0.00}}}",
                            feet, f, rh, StullWetBulbF(f, rh) - reference, Psychrometrics.WetBulbF(f, rh, feet) - reference));
                        first = false;
                    }
            sb.Append("\n]}\n");
            string root = Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName;
            string dir = Path.Combine(root, "test-results", "weather-a3");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "report.json"), sb.ToString());
        }

        static double Mean(int[] perThird)
        {
            double s = 0;
            for (int t = 6; t < 18; t++) s += perThird[t];
            return s / 12;
        }

        /// <summary>100 played seasons' mean November–April new snow (in) at mid-mountain and the summit, Stull's or v2's wet bulb.</summary>
        static (double Mid, double Top) Season(WeatherRows rows, bool stull)
        {
            var gen = new WeatherGenerator(rows, WeatherTuning.Default);
            int hours = WeatherGenerator.HoursInYear(WeatherTuning.Default);
            var h = new WeatherHour();
            double mid = 0, top = 0;
            const int seeds = 100;
            for (int s = 0; s < seeds; s++)
            {
                gen.Reset(0x4133_5245_5000_0000UL + (ulong)s, 2026);
                for (int i = 0; i < hours; i++)
                {
                    gen.Step(ref h);
                    if (h.Liquid <= 0 || h.Third < 3 || h.Third >= 21) continue;
                    mid += SnowIn(h, rows.ZrefFeet, rows.MidFeet, stull);
                    top += SnowIn(h, rows.ZrefFeet, rows.SummitFeet, stull);
                }
            }
            return (mid / seeds, top / seeds);
        }

        /// <summary><see cref="WeatherPoint.At"/>'s new snow (in), with Stull's wet bulb or WX-37 v2's.</summary>
        static double SnowIn(in WeatherHour h, int zref, int feet, bool stull)
        {
            if (!stull) return WeatherPoint.At(h, zref, feet).SnowTenthsIn / 10.0;
            int dz = feet - zref;
            int temp = h.Temp - (int)WeatherMath.DivRound((long)h.Lapse * dz, 1000);
            int dew = Math.Min(temp, h.Dew - (int)WeatherMath.DivRound((long)h.DewLapse * dz, 1000));
            long factor = WeatherMath.Clamp(1000 + WeatherMath.DivRound((long)h.PrecipGradient * dz, 100), 0, 4000);
            int liquid = (int)WeatherMath.DivRound(h.Liquid * factor, 1000);
            double t = temp / 10.0, wb = StullWetBulbF(t, Psychrometrics.RelativeHumidity(t, dew / 10.0));
            double share = Psychrometrics.SnowShare(wb);
            return liquid <= 0 || share <= 0 ? 0 : Math.Round(liquid * share * Psychrometrics.SnowToLiquid(wb) / 10.0) / 10.0;
        }

        static Pillow Measure(string site, SnotelSeries s, BlendedRecord b, WeatherRows rows, RecordStates states)
        {
            int dz = (int)Math.Round(s.Station.ElevationFeet) - rows.ZrefFeet;
            var p = new Pillow { Site = site, Station = s.Station.Triplet, Feet = s.Station.ElevationFeet, Zref = rows.ZrefFeet };
            double pillowSum = 0, stullSum = 0, gameSum = 0, packSum = 0, gaugeSum = 0, gaugeStullSum = 0, gaugeGameSum = 0;
            int nh = Math.Min(b.HourCount, states.State.Length);
            for (int d = 0; d < states.DayOfLabelYear.Length; d++)
            {
                if (states.DayOfLabelYear[d] != 0) continue;
                var oct1 = CivilDay.Date(b.Days[d]);
                int apr1 = CivilDay.Of(oct1.Year + 1, 4, 1);
                int days = apr1 - b.Days[d];
                if ((d + days) * 24 > nh) continue;
                if (!s.Days.TryGetValue(apr1, out var day) || double.IsNaN(day.Wteq)) continue;

                double stull = 0, game = 0, gauge = 0, gaugeStull = 0, gaugeGame = 0;
                var pack = new WeatherFidelityTests.ReferenceSnow();
                bool missing = false;
                // The day's snow share, liquid-weighted over its wet hours (by hour when the record was dry that day).
                double dayL = 0, dayStull = 0, dayGame = 0, allStull = 0, allGame = 0; int hoursIn = 0;
                for (int k = d * 24; k < (d + days) * 24; k++)
                {
                    if (k % 24 == 0) { dayL = 0; dayStull = 0; dayGame = 0; allStull = 0; allGame = 0; hoursIn = 0; }
                    if (float.IsNaN(b.T[k])) { missing = true; continue; }
                    int t = states.HourThird(k);
                    bool isNight = states.State[k] != WeatherRows.Storm && (k % 24 < 7 || k % 24 >= 17);
                    int lapse = isNight ? rows.LapseNight[t] : rows.LapseDay[t];
                    double tempF = Psychrometrics.CToF(b.T[k]) - (double)lapse * dz / 10_000;
                    double dewF = Psychrometrics.CToF(float.IsNaN(b.Td[k]) ? b.T[k] : b.Td[k]) - (double)rows.DewLapse[t] * dz / 10_000;
                    dewF = Math.Min(tempF, dewF);
                    double liquidIn = float.IsNaN(b.P[k]) ? 0 : b.P[k] / 25.4 * Math.Max(0, 1 + rows.PrecipGradient[t] * dz / 100_000.0);
                    double rh = Psychrometrics.RelativeHumidity(tempF, dewF);
                    double shareGame = liquidIn > 0 ? Psychrometrics.SnowShare(GameWetBulbF(tempF, rh, s.Station.ElevationFeet)) : 0;
                    if (liquidIn > 0)
                    {
                        stull += liquidIn * Psychrometrics.SnowShare(StullWetBulbF(tempF, rh));
                        game += liquidIn * shareGame;
                    }
                    pack.Step(liquidIn * shareGame, tempF, CivilDay.Date(b.Days[k / 24]).DayOfYear, 1);
                    double sStull = Psychrometrics.SnowShare(StullWetBulbF(tempF, rh)), sGame = Psychrometrics.SnowShare(GameWetBulbF(tempF, rh, s.Station.ElevationFeet));
                    allStull += sStull; allGame += sGame; hoursIn++;
                    if (liquidIn > 0) { dayL += liquidIn; dayStull += liquidIn * sStull; dayGame += liquidIn * sGame; }
                    if (k % 24 == 23 && hoursIn > 0)
                    {
                        int civil = b.Days[k / 24];
                        if (!s.Days.TryGetValue(civil, out var today) || !s.Days.TryGetValue(civil - 1, out var yesterday) || double.IsNaN(today.Prec) || double.IsNaN(yesterday.Prec))
                            continue;
                        double inc = civil == b.Days[d] ? Math.Max(0, today.Prec) : Math.Max(0, today.Prec - yesterday.Prec);   // PREC restarts each October 1
                        gauge += inc;
                        gaugeStull += inc * (dayL > 0 ? dayStull / dayL : allStull / hoursIn);
                        gaugeGame += inc * (dayL > 0 ? dayGame / dayL : allGame / hoursIn);
                    }
                }
                if (missing) continue;
                p.Winters++;
                pillowSum += day.Wteq; stullSum += stull; gameSum += game; packSum += pack.Pack;
                gaugeSum += gauge; gaugeStullSum += gaugeStull; gaugeGameSum += gaugeGame;
            }
            if (p.Winters > 0) { p.PillowIn = pillowSum / p.Winters; p.SnowStullIn = stullSum / p.Winters; p.SnowGameIn = gameSum / p.Winters; p.PackGameIn = packSum / p.Winters; p.GaugeIn = gaugeSum / p.Winters; p.GaugeSnowStullIn = gaugeStullSum / p.Winters; p.GaugeSnowGameIn = gaugeGameSum / p.Winters; }
            return p;
        }

        /// <summary>The game's wet bulb at a height (WX-37).</summary>
        static double GameWetBulbF(double tempF, double rh, double feet) => Psychrometrics.WetBulbF(tempF, rh, feet);
    }
}
