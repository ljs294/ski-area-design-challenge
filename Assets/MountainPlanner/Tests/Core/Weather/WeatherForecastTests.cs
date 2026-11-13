using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR C: the forecast (WX-05 v3, WX-54 v2), the named events (WX-41), the alerts (WX-42) and the crowd's two
    /// weather lines (WX-44).
    /// </summary>
    public sealed class WeatherForecastTests
    {
        static readonly Pacing P = Pacing.Default;
        const long Day = SimWorld.DayMs, Hour = SimWorld.HourMs;
        const int Seeds = 50;

        static SimDesign Resort(params LiftInput[] lifts) => new SimDesign(1, 1, 77, DayHours.Default, lifts, 1);

        static LiftInput Quad(ulong id, int topFeet) =>
            new LiftInput(id, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topFeet: topFeet);

        static void RunTo(SimWorld w, long faceMs, Action<SimWorld> eachHour = null)
        {
            long last = w.HourIndex;
            while (w.FaceMs < faceMs)
            {
                w.Step(P.IdleTickFactor);
                if (w.HourIndex != last) { last = w.HourIndex; eachHour?.Invoke(w); }
            }
        }

        /// <summary>WX-54 v2's measures over one site's 50 seeds, December to March, every evening's sheet.</summary>
        internal sealed class Skill
        {
            public int High1, High1In, High7, High7In, Side7, Side7Right;
            public int Storms7, Storms7Wet;
            public long Chance1, Chance3, Chance7; public int ChanceN1, ChanceN3, ChanceN7;
            public int Storms3, Storms3Wrong, Storms1, Storms1Bust, WindowMisses, Phantoms4, Phantoms57, DryCards57;
            public double Ms; public int Issues;
            /// <summary>By lead in face days (1–7): the high's absolute error (0.1 °F) summed, the cards, those within 5 °F, and true storms' chance (‰) summed.</summary>
            public readonly long[] ErrSum = new long[8], StormChance = new long[8];
            public readonly int[] ErrN = new int[8], Within2 = new int[8], Within5 = new int[8], StormN = new int[8], StormWet = new int[8];
            public double Share(int a, int n) => n == 0 ? double.NaN : (double)a / n;
            public override string ToString() =>
                $"high ±2 °F a day out {Share(High1In, High1):P1}; ±5 °F at a week {Share(High7In, High7):P1}; right side at a week {Share(Side7Right, Side7):P1}; " +
                $"storm card wet at a week {Share(Storms7Wet, Storms7):P1}; chance {Chance1 / 10.0 / Math.Max(1, ChanceN1):F1} / {Chance3 / 10.0 / Math.Max(1, ChanceN3):F1} / {Chance7 / 10.0 / Math.Max(1, ChanceN7):F1} %; " +
                $"wrong day at 3 {Share(Storms3Wrong, Storms3):P1}; busts {Share(Storms1Bust, Storms1):P1}; window misses {WindowMisses}; phantoms ≤4 days {Phantoms4}, 5–7 {Share(Phantoms57, DryCards57):P1} of dry cards; " +
                $"{Ms / Math.Max(1, Issues):F2} ms an issue";
        }

        internal static Skill Measure(string site, int seeds = Seeds, Pacing pacing = null)
        {
            var p = pacing ?? P;
            var rows = SimWeatherTests.Frozen(site);
            var wx = SimWeather.Own(rows);
            var w = new SimWorld(p, Resort(Quad(1, 0)), SimSite.None, 2026, weather: wx);
            var cal = new LabelCalendar(p);
            var k = new Skill();
            var clock = new System.Diagnostics.Stopwatch();
            for (int seed = 1; seed <= seeds; seed++)
            {
                ulong s = (ulong)seed * 0x9E3779B97F4A7C15UL;
                var g = new WeatherGenerator(w.Weather.Rows, SimWeather.Tuning(p));
                g.Reset(s, 2026);
                var scratch = new WeatherHour();
                for (int day = cal.December1FaceDay; day < cal.April1FaceDay - 7; day++)
                {
                    long issue = (long)day * 24 + 16;
                    while (g.Hour < issue + 1) g.Step(ref scratch);
                    clock.Start();
                    var sheet = w.IssueForTest(g, s, 2026, issue, evening: true);
                    clock.Stop();
                    k.Issues++;
                    Tally(w, sheet, k);
                }
            }
            k.Ms = clock.Elapsed.TotalMilliseconds;
            return k;
        }

        static void Tally(SimWorld w, ForecastSheet sheet, Skill k)
        {
            for (int c = 0; c < sheet.DayCount; c++)
            {
                var card = sheet.Cards[c];
                int lead = card.FaceDay - sheet.IssueFaceDay;
                int err = Math.Abs(card.HighBase - w.TruthHighBase(c));
                if (lead >= 1 && lead <= 7) { k.ErrSum[lead] += err; k.ErrN[lead]++; if (err <= 20) k.Within2[lead]++; if (err <= 50) k.Within5[lead]++; }
                if (lead == 1) { k.High1++; if (err <= 20) k.High1In++; }
                if (lead == 7)
                {
                    k.High7++; if (err <= 50) k.High7In++;
                    int clim = w.ClimateHighBase(card.FaceDay), truth = w.TruthHighBase(c);
                    if (truth != clim) { k.Side7++; if (Math.Sign(card.HighBase - clim) == Math.Sign(truth - clim)) k.Side7Right++; }
                }
                if (!w.TruthWet(c) && card.Words == ForecastWords.Unsettled) { if (lead <= 4) k.Phantoms4++; else k.Phantoms57++; }
                if (!w.TruthWet(c) && lead >= 5) k.DryCards57++;
            }
            for (int s = 0; s < w.TruthStormCount; s++)
            {
                int main = w.TruthStormMain(s);
                if (main < 0 || !w.TruthWet(main)) continue;
                var card = sheet.Cards[main];
                int lead = card.FaceDay - sheet.IssueFaceDay;
                bool wet = card.Words != ForecastWords.Fair && card.Words != ForecastWords.Unknown;
                if (!wet) k.WindowMisses++;
                if (lead >= 1 && lead <= 7) { k.StormN[lead]++; k.StormChance[lead] += card.ChancePermille; if (wet) k.StormWet[lead]++; }
                if (lead == 7) { k.Storms7++; if (wet) k.Storms7Wet++; }
                if (lead == 1) { k.Chance1 += card.ChancePermille; k.ChanceN1++; }
                if (lead == 3) { k.Chance3 += card.ChancePermille; k.ChanceN3++; }
                if (lead == 7) { k.Chance7 += card.ChancePermille; k.ChanceN7++; }
                if (lead == 3) { k.Storms3++; if (!card.StormFirm) k.Storms3Wrong++; }
                if (lead == 1)
                {
                    k.Storms1++;
                    int truth = w.TruthLiquid(main);
                    if (truth < card.LiquidLo || truth > card.LiquidHi) k.Storms1Bust++;
                }
            }
        }

        // ---------- the sheet in the world ----------

        /// <summary>Every field of the sheet, as text: what "the same sheet" means.</summary>
        internal static string Text(ForecastSheet s)
        {
            var b = new System.Text.StringBuilder();
            b.Append(s.Known).Append(' ').Append(s.IssueHour).Append(' ').Append(s.Evening).Append(' ').Append(s.Outlook).Append(s.OutlookFirm).Append(s.OutlookDay).Append('|');
            foreach (var f in typeof(ForecastDay).GetFields())
                for (int k = 0; k < s.DayCount; k++) b.Append(f.GetValue(s.Cards[k])).Append(',');
            foreach (var f in typeof(ForecastHour).GetFields())
                for (int i = 0; i < s.HourCount; i++) b.Append(f.GetValue(s.Hourly[i])).Append(',');
            b.Append('|').Append(string.Join(",", s.GunWindowHours)).Append('|').Append(string.Join(",", s.TonightSnow)).Append('|').Append(string.Join(",", s.TomorrowSnow));
            b.Append('|').Append(s.TomorrowSummitGust).Append('|').Append(s.NextThawDay).Append('|').Append(string.Join(",", s.LiftGustTenths));
            return b.ToString();
        }

        static SimWorld Season(SimWeather wx, ulong seed = 4242)
        {
            var w = new SimWorld(P, Resort(Quad(1, 0), Quad(2, 99_000)), SimSite.None, 2026, seed, weather: wx);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            return w;
        }

        static List<SimEvent> Collect(SimWorld w, long until, SimEventType type)
        {
            var list = new List<SimEvent>();
            long seq = w.Events.Count;
            while (w.FaceMs < until)
            {
                w.Step(P.IdleTickFactor);
                for (long q = seq; q < w.Events.Count; q++) if (w.Events.TryGet(q, out var e) && e.Type == type) list.Add(e);
                seq = w.Events.Count;
            }
            return list;
        }

        [Test]
        public void SI23_TheForecastAndItsEventsAllocateNothing()
        {
            // Days of ticks with weather, each with an evening sheet, a landing refresh, events and alerts: 0 bytes (SI-23, WX-51).
            var w = Season(SimWeatherTests.Golden());
            var s = new SimSnapshot(P);
            var cal = new LabelCalendar(P);
            long start = (long)cal.December1FaceDay * Day;
            while (w.FaceMs < start + 2 * Day) w.Step(P.IdleTickFactor, s);   // warm: the first sheets, the run-ahead's own state
            try { GC.GetAllocatedBytesForCurrentThread(); }
            catch (Exception e) when (e is NotImplementedException || e is PlatformNotSupportedException) { Assert.Ignore("no per-thread allocation counter here"); return; }
            // Counted around each step, so the test's own long loop (which Release recompiles mid-run) isn't in the sum.
            int issued = 0;
            long seq = w.Events.Count, bytes = 0;
            while (w.FaceMs < start + 6 * Day)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                w.Step(P.IdleTickFactor, s);
                bytes += GC.GetAllocatedBytesForCurrentThread() - before;
                for (long q = seq; q < w.Events.Count; q++) if (w.Events.TryGet(q, out var e) && e.Type == SimEventType.ForecastIssued) issued++;
                seq = w.Events.Count;
            }
            Assert.That(issued, Is.GreaterThanOrEqualTo(7), "four days of sheets and refreshes");
            Assert.That(bytes, Is.Zero, "bytes allocated over four face days");
        }

        [Test]
        public void TheSheetComesAtTheStopAndAgainAtTheLanding()
        {
            var w = Season(SimWeatherTests.Golden());
            var cal = new LabelCalendar(P);
            long d = cal.December1FaceDay + 2;
            var issued = Collect(w, (d + 1) * Day + 12 * Hour, SimEventType.ForecastIssued);
            Assert.That(issued.Count, Is.GreaterThanOrEqualTo(2 * (int)(d + 1) - 1));
            // The evening sheet of day d, then its refresh on the morning of d + 1: the same target days, nearer.
            var w2 = Season(SimWeatherTests.Golden());
            RunTo(w2, d * Day + DayHours.Default.StopSecond * 1000L + 1);
            var evening = new ForecastSheet(P.MaxLifts);
            evening.CopyFrom(w2.Forecast);
            Assert.That(evening.Evening, Is.True);
            Assert.That(evening.IssueFaceDay, Is.EqualTo(d));
            RunTo(w2, (d + 1) * Day + (DayHours.Default.OpenSecond - P.SkipLandsBeforeOpenMinutes * 60) * 1000L + 1);
            var morning = w2.Forecast;
            Assert.That(morning.Evening, Is.False);
            Assert.That(morning.Cards[0].FaceDay, Is.EqualTo(evening.Cards[0].FaceDay), "the same target days");
            for (int k = 0; k < morning.DayCount; k++)
            {
                Assert.That(morning.Cards[k].LeadHours, Is.LessThan(evening.Cards[k].LeadHours));
                int spread = evening.Cards[k].HighBaseHi - evening.Cards[k].HighBase;
                Assert.That(Math.Abs(morning.Cards[k].HighBase - evening.Cards[k].HighBase), Is.LessThanOrEqualTo(spread + 10), "shifts a little, never jumps");
            }
        }

        [Test]
        public void WX32_ASavedForecastComesBackTheSame()
        {
            var wx = SimWeatherTests.Golden();
            var cal = new LabelCalendar(P);
            var a = Season(wx);
            RunTo(a, (cal.December1FaceDay + 5) * Day + 22 * Hour);   // 10 PM, after the evening sheet
            Assert.That(a.Forecast.Known && a.Forecast.Evening, Is.True);
            byte[] bytes = a.SaveState();
            Assert.That(SimSidecar.VersionOf(bytes), Is.EqualTo(6));
            var b = SimSidecar.Read(bytes, P, a.Design, SimSite.None, out _, wx);
            Assert.That(b.Hash(), Is.EqualTo(a.Hash()));
            Assert.That(Text(b.Forecast), Is.EqualTo(Text(a.Forecast)), "the sheet is made again exactly from its issue");
            var ha = new List<ulong>();
            var hb = new List<ulong>();
            long until = (cal.December1FaceDay + 9) * Day;
            RunTo(a, until, x => ha.Add(x.HourHash));
            RunTo(b, until, x => hb.Add(x.HourHash));
            Assert.That(hb, Is.EqualTo(ha), "alerts, events and the crowd's lines replay");
            Assert.That(Text(b.Forecast), Is.EqualTo(Text(a.Forecast)));
        }

        [Test]
        public void WX42_AnAlertGoesOutOncePerDay()
        {
            var w = Season(SimWeatherTests.Golden());
            var cal = new LabelCalendar(P);
            var alerts = Collect(w, (long)cal.April1FaceDay * Day, SimEventType.WeatherAlert);
            Assert.That(alerts.Count, Is.GreaterThan(5), "a winter has alerts");
            var seen = new Dictionary<int, long>();
            foreach (var e in alerts)
            {
                int kind = e.Detail;
                if (seen.TryGetValue(kind, out long last)) Assert.That(e.Value, Is.GreaterThan(last + 1), $"{(WeatherEventKind)kind}: one per day, a day's cooldown");
                seen[kind] = e.Value;
                Assert.That(new[] { WeatherEventKind.BigStorm, WeatherEventKind.Rain, WeatherEventKind.FreezingRain,
                    WeatherEventKind.Thaw, WeatherEventKind.ColdSnap, WeatherEventKind.WindEvent }, Does.Contain((WeatherEventKind)kind));
            }
        }

        [Test]
        public void WX41_AWinterNamesItsWeather()
        {
            var w = Season(SimWeatherTests.Golden());
            var cal = new LabelCalendar(P);
            var began = Collect(w, (long)cal.April1FaceDay * Day, SimEventType.WeatherEventBegan);
            var count = new int[16];
            foreach (var e in began) count[e.Detail]++;
            var line = new System.Text.StringBuilder();
            foreach (WeatherEventKind k in Enum.GetValues(typeof(WeatherEventKind))) line.Append(k).Append(' ').Append(count[(int)k]).Append(", ");
            TestContext.Progress.WriteLine(line.ToString());
            Assert.That(count[(int)WeatherEventKind.Storm], Is.GreaterThan(5));
            Assert.That(count[(int)WeatherEventKind.PowderDay], Is.GreaterThan(0));
            Assert.That(count[(int)WeatherEventKind.WindEvent], Is.GreaterThan(0));
            // Inversions and freezing rain need the map's pool (static inputs): this world has none, so neither happens.
            Assert.That(count[(int)WeatherEventKind.Inversion], Is.Zero);
            Assert.That(count[(int)WeatherEventKind.FreezingRain], Is.Zero);
            Assert.That(count[(int)WeatherEventKind.StormOfTheSeason], Is.LessThanOrEqualTo(count[(int)WeatherEventKind.BigStorm]));
        }

        [Test]
        public void WX44_ThePowderMorningAndTheForecastMoveTheCrowd()
        {
            var w = Season(SimWeatherTests.Golden());
            var cal = new LabelCalendar(P);
            int powder = 0, rain = 0, flat = 0;
            int p = w.Weather.Rows.PowderDayTenthsIn;
            long landing = (DayHours.Default.OpenSecond - P.SkipLandsBeforeOpenMinutes * 60) * 1000L;
            for (int day = cal.December1FaceDay; day < cal.April1FaceDay; day++)
            {
                // The evening sheet the crowd will read, then the morning it reads it.
                RunTo(w, (day - 1) * Day + DayHours.Default.StopSecond * 1000L + 1);
                var sheet = w.Forecast;
                int k = sheet.CardOf(day);
                bool rainy = k >= 0 && sheet.Cards[k].RainWhileOpen;
                if (rainy) Assert.That(sheet.Cards[k].RainAtBase, "rain while open is rain at the base");
                RunTo(w, day * Day + landing + 1);
                int snow = w.OvernightSnowMid;
                int expected = 1000 + (int)((long)(P.PowderPull - 1000) * Math.Min(snow, 2 * p) / (2 * p));
                Assert.That(w.PowderPullToday, Is.EqualTo(expected), $"day {day}: {snow / 10.0} in overnight");
                if (snow >= p) powder++;
                if (snow == 0) { Assert.That(w.PowderPullToday, Is.EqualTo(1000)); flat++; }
                if (rainy) { Assert.That(w.ForecastPullToday, Is.LessThanOrEqualTo(P.ForecastPullRain)); rain++; }
            }
            Assert.That(powder, Is.GreaterThan(0), "a winter has powder mornings");
            Assert.That(flat, Is.GreaterThan(0));
        }

        [Test]
        public void WX44_WithoutWeatherTheLinesStayFlat()
        {
            var w = new SimWorld(P, GuestFixture.Resort(), SimSite.None, 2026, 7_777UL);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            RunTo(w, 3 * Day + 12 * Hour);
            Assert.That(w.PowderPullToday, Is.EqualTo(1000));
            Assert.That(w.ForecastPullToday, Is.EqualTo(1000));
            Assert.That(w.Forecast.Known, Is.False);
        }

        [Test, Explicit("Measures WX-54 v2 at every site (a minute)"), Category("WeatherReport")]
        public void WriteTheForecastSkill()
        {
            // MP_PACING: an override's Name = value lines, separated by ';' (a what-if for the report).
            string text = Environment.GetEnvironmentVariable("MP_PACING");
            var p = string.IsNullOrEmpty(text) ? P : Pacing.Parse(text.Replace(';', '\n'));
            foreach (var site in WeatherFixtures.Sites) TestContext.Progress.WriteLine($"{site}: {Measure(site, Seeds, p)}");
        }

        [TestCaseSource(typeof(WeatherFixtures), nameof(WeatherFixtures.Sites))]
        public void WX54_TheForecastTeasesTheTruth(string site)
        {
            var k = Measure(site);
            TestContext.Progress.WriteLine($"{site}: {k}");
            Assert.That(k.Share(k.High1In, k.High1), Is.GreaterThanOrEqualTo(0.90), "the high within 2 °F a day out");
            Assert.That(k.Share(k.High7In, k.High7), Is.GreaterThanOrEqualTo(0.90), "the high within 5 °F at a week");
            Assert.That(k.Share(k.Side7Right, k.Side7), Is.GreaterThanOrEqualTo(0.85), "the right side of normal at a week");
            Assert.That(k.Share(k.Storms7Wet, k.Storms7), Is.GreaterThanOrEqualTo(0.95), "a true storm's 7-day card shows precipitation");
            Assert.That(k.Chance7 / 10.0 / k.ChanceN7, Is.InRange(70.0, 90.0), "chance at a week");
            Assert.That(k.Chance3 / 10.0 / k.ChanceN3, Is.InRange(85.0, 95.0), "chance at 3 days");
            Assert.That(k.Chance1 / 10.0 / k.ChanceN1, Is.InRange(92.0, 98.0), "chance the evening before");
            Assert.That(k.WindowMisses, Is.Zero, "a storm's day is never shown fair");
            Assert.That(k.Share(k.Storms3Wrong, k.Storms3), Is.LessThanOrEqualTo(0.05), "the wrong day at 3 days");
            Assert.That(k.Share(k.Storms1Bust, k.Storms1), Is.InRange(0.02, 0.08), "busts the evening before");
            Assert.That(k.Phantoms4, Is.Zero, "no phantom inside 4 days");
            Assert.That(k.Ms / k.Issues, Is.LessThan(2.0), "WX-51: an issue under 2 ms");
        }
    }
}
