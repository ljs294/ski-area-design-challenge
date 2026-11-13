using System;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR C's report (demo 93): the forecast's skill by lead at every site at the week leans 0.65 and 0.85
    /// (WX-54 v2), the named events a winter brings (WX-41), and how the crowd's two weather lines move the days
    /// (WX-44). Writes test-results/weather-forecast/data.json; <c>tools/weather-forecast/report.mjs</c> draws it.
    /// </summary>
    [Explicit("Writes the forecast report's data (demo 93); several minutes in Release")]
    [Category("Report")]
    public sealed class WeatherForecastReportTests
    {
        static readonly Pacing P = Pacing.Default;
        static readonly int[] Leans = { 650, 850 };
        const int SkillSeeds = 50, Winters = 3;
        const long Day = SimWorld.HourMs * 24;
        static string Num(double v) => double.IsNaN(v) ? "null" : Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);

        [Test]
        public void WriteTheForecastReport()
        {
            string output = Environment.GetEnvironmentVariable("WEATHER_FORECAST_OUT");
            if (string.IsNullOrEmpty(output))
                output = Path.Combine(Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName, "test-results", "weather-forecast", "data.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var json = new StringBuilder("{\"skillSeeds\":").Append(SkillSeeds).Append(",\"winters\":").Append(Winters).Append(",\"leans\":[");
            for (int l = 0; l < Leans.Length; l++)
            {
                var p = Pacing.Parse("ForecastLeanDay7 = " + Leans[l]);
                if (l > 0) json.Append(',');
                json.Append("{\"lean\":").Append(Leans[l]).Append(",\"sites\":[");
                for (int i = 0; i < WeatherFixtures.Sites.Length; i++)
                {
                    var k = WeatherForecastTests.Measure(WeatherFixtures.Sites[i], SkillSeeds, p);
                    if (i > 0) json.Append(',');
                    json.Append("{\"site\":\"").Append(WeatherFixtures.Sites[i]).Append("\",\"meanErrF\":[");
                    for (int d = 1; d <= 7; d++) json.Append(d > 1 ? "," : "").Append(Num(k.ErrN[d] == 0 ? double.NaN : k.ErrSum[d] / 10.0 / k.ErrN[d]));
                    json.Append("],\"within2\":[");
                    for (int d = 1; d <= 7; d++) json.Append(d > 1 ? "," : "").Append(Num(k.Share(k.Within2[d], k.ErrN[d])));
                    json.Append("],\"within5\":[");
                    for (int d = 1; d <= 7; d++) json.Append(d > 1 ? "," : "").Append(Num(k.Share(k.Within5[d], k.ErrN[d])));
                    json.Append("],\"stormChance\":[");
                    for (int d = 1; d <= 7; d++) json.Append(d > 1 ? "," : "").Append(Num(k.StormN[d] == 0 ? double.NaN : k.StormChance[d] / 10.0 / k.StormN[d]));
                    json.Append("],\"stormWet\":[");
                    for (int d = 1; d <= 7; d++) json.Append(d > 1 ? "," : "").Append(Num(k.Share(k.StormWet[d], k.StormN[d])));
                    json.Append("],\"rightSide7\":").Append(Num(k.Share(k.Side7Right, k.Side7)));
                    json.Append(",\"wrongDay3\":").Append(Num(k.Share(k.Storms3Wrong, k.Storms3)));
                    json.Append(",\"busts\":").Append(Num(k.Share(k.Storms1Bust, k.Storms1)));
                    json.Append(",\"phantoms4\":").Append(k.Phantoms4);
                    json.Append(",\"phantoms57\":").Append(Num(k.Share(k.Phantoms57, k.DryCards57)));
                    json.Append(",\"msPerIssue\":").Append(Num(k.Ms / Math.Max(1, k.Issues))).Append('}');
                    TestContext.Progress.WriteLine($"[skill] lean {Leans[l]} {WeatherFixtures.Sites[i]}: {k}");
                }
                json.Append("]}");
            }
            json.Append("],\"events\":[");
            for (int i = 0; i < WeatherFixtures.Sites.Length; i++)
            {
                if (i > 0) json.Append(',');
                Winter(WeatherFixtures.Sites[i], json);
            }
            json.Append("]}");
            File.WriteAllText(output, json.ToString());
            TestContext.Progress.WriteLine("wrote " + output);
        }

        /// <summary>One site's winters (October 1 – April 1): events begun by kind, alerts, and the crowd's two lines each morning, December to March.</summary>
        static void Winter(string site, StringBuilder json)
        {
            var began = new int[16];
            var alerts = new int[16];
            int days = 0, powderDays = 0, forecastCut = 0, forecastLift = 0;
            long powderSum = 0, forecastSum = 0, bothSum = 0;
            var cal = new LabelCalendar(P);
            long landing = (DayHours.Default.OpenSecond - P.SkipLandsBeforeOpenMinutes * 60) * 1000L;
            var rows = SimWeatherTests.Frozen(site);
            for (int winter = 0; winter < Winters; winter++)
            {
                var w = new SimWorld(P, new SimDesign(1, 1, 77, DayHours.Default,
                        new[] { new LiftInput(1, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topFeet: 0) }, 1),
                    SimSite.None, 2026, 1000UL + (ulong)winter * 7919UL, weather: SimWeather.Own(rows));
                w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
                long seq = w.Events.Count;
                for (int day = 1; day < cal.April1FaceDay; day++)
                {
                    long until = day * Day + landing + 1;
                    while (w.FaceMs < until) w.Step(P.IdleTickFactor);
                    for (long q = seq; q < w.Events.Count; q++)
                        if (w.Events.TryGet(q, out var e))
                        {
                            if (e.Type == SimEventType.WeatherEventBegan) began[e.Detail]++;
                            else if (e.Type == SimEventType.WeatherAlert) alerts[e.Detail]++;
                        }
                    seq = w.Events.Count;
                    if (day < cal.December1FaceDay) continue;
                    days++;
                    int pp = w.PowderPullToday, fp = w.ForecastPullToday;
                    powderSum += pp; forecastSum += fp; bothSum += (long)pp * fp / 1000;
                    if (pp > 1000) powderDays++;
                    if (fp < 1000) forecastCut++;
                    if (fp > 1000) forecastLift++;
                }
            }
            json.Append("{\"site\":\"").Append(site).Append("\",\"began\":{");
            bool first = true;
            foreach (WeatherEventKind kind in Enum.GetValues(typeof(WeatherEventKind)))
            {
                if (kind == WeatherEventKind.None) continue;
                json.Append(first ? "" : ",").Append('"').Append(kind).Append("\":").Append(Num(began[(int)kind] / (double)Winters));
                first = false;
            }
            json.Append("},\"alerts\":{");
            first = true;
            foreach (WeatherEventKind kind in Enum.GetValues(typeof(WeatherEventKind)))
            {
                if (alerts[(int)kind] == 0) continue;
                json.Append(first ? "" : ",").Append('"').Append(kind).Append("\":").Append(Num(alerts[(int)kind] / (double)Winters));
                first = false;
            }
            json.Append("},\"crowd\":{\"days\":").Append(days / Winters)
                .Append(",\"powderMornings\":").Append(Num(powderDays / (double)Winters))
                .Append(",\"forecastCutDays\":").Append(Num(forecastCut / (double)Winters))
                .Append(",\"forecastLiftDays\":").Append(Num(forecastLift / (double)Winters))
                .Append(",\"meanPowder\":").Append(Num(powderSum / 1000.0 / Math.Max(1, days)))
                .Append(",\"meanForecast\":").Append(Num(forecastSum / 1000.0 / Math.Max(1, days)))
                .Append(",\"meanBoth\":").Append(Num(bothSum / 1000.0 / Math.Max(1, days))).Append("}}");
            TestContext.Progress.WriteLine($"[events] {site}: storms {began[(int)WeatherEventKind.Storm] / (double)Winters:F1}, powder days {began[(int)WeatherEventKind.PowderDay] / (double)Winters:F1}, crowd x{bothSum / 1000.0 / Math.Max(1, days):F3}");
        }
    }
}
