using System;
using System.Globalization;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR B1's hold report (demo 94): how often wind holds a lift in a season at each of the seven sites, so the
    /// owner can judge <c>Pacing.WindScale</c>. Each site plays its own frozen rows for five winters with three lifts: a
    /// fixed-grip quad at the base, and a fixed-grip quad and a detachable quad at the summit reference point, counted
    /// December to March (label months), when the lifts run. B1's
    /// exposure is the height lerp between the base (the sheltered class) and the summit (the exposed class), the same at
    /// every site: the steered, per-cell exposure is B-fields'. Writes test-results/weather-holds/data.json.
    /// </summary>
    [Explicit("Writes the hold report's data (demo 94); about two minutes")]
    [Category("Report")]
    public sealed class WeatherHoldReportTests
    {
        const int Years = 5, FirstYear = 2026;
        /// <summary>The <c>WindScale</c> values the report compares (‰): today's 1.0 and two calmer ones.</summary>
        static readonly int[] Scales = { 1000, 850, 700 };
        static string Num(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);

        [Test]
        public void WriteTheHoldReport()
        {
            string output = Environment.GetEnvironmentVariable("WEATHER_HOLDS_OUT");
            if (string.IsNullOrEmpty(output))
                output = Path.Combine(Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName, "test-results", "weather-holds", "data.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var json = new StringBuilder("{\"years\":").Append(Years).Append(",\"scales\":[");
            for (int k = 0; k < Scales.Length; k++)
            {
                var p = Pacing.Parse("WindScale = " + Scales[k]);
                if (k > 0) json.Append(',');
                json.Append("{\"windScale\":").Append(Scales[k]).Append(",\"sites\":[");
                for (int i = 0; i < WeatherFixtures.Sites.Length; i++)
                {
                    if (i > 0) json.Append(',');
                    Site(p, WeatherFixtures.Sites[i], json);
                }
                json.Append("]}");
            }
            json.Append("]}");
            File.WriteAllText(output, json.ToString());
            TestContext.Progress.WriteLine("wrote " + output);
        }

        static void Site(Pacing P, string site, StringBuilder json)
        {
            var wx = SimWeather.Own(SimWeatherTests.Frozen(site));
            var lifts = new[]
            {
                new LiftInput(1, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topFeet: wx.BaseFeet),
                new LiftInput(2, true, LiftSchedule.Daily, null, LiftKind.FixedGripChair, 4, 6000, 360_000, topFeet: wx.SummitFeet),
                new LiftInput(3, true, LiftSchedule.Daily, null, LiftKind.DetachableChair, 4, 6000, 360_000, topFeet: wx.SummitFeet),
            };
            var w = new SimWorld(P, new SimDesign(1, 1, 77, DayHours.Default, lifts, 1), SimSite.None, FirstYear, weather: wx);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
            var s = new SimSnapshot(P);
            long yearMs = (long)new LabelCalendar(P).YearFaceDays * SimWorld.DayMs;
            int[,] hours = new int[Years, 3], days = new int[Years, 3];
            int[] openHours = new int[Years];
            int[] gustBins = new int[16];   // the summit's top gust in open face hours, 5 mph bins (75+ in the last)
            bool[] heldToday = new bool[3];
            int lastDay = -1;
            long lastHour = w.HourIndex;
            while (w.FaceMs < Years * yearMs)
            {
                // October 1 closes the mountain (TM-11 v2); the player opens it again for the next season.
                if (!w.Open) w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
                w.Step(P.IdleTickFactor);
                if (w.HourIndex == lastHour) continue;
                lastHour = w.HourIndex;
                int y = (int)(w.FaceMs / yearMs);
                if (y >= Years || w.Phase != SeasonPhase.Season) continue;
                w.Write(s);
                if (s.Month < 12 && s.Month > 3) continue;   // the ski season, December to March, at every site alike
                int day = (int)(w.FaceMs / SimWorld.DayMs);
                if (day != lastDay) { Array.Clear(heldToday, 0, 3); lastDay = day; }
                bool operating = false;
                for (int k = 0; k < 3; k++)
                {
                    var st = s.LiftStatus[k];
                    if (st == LiftOpStatus.Open || st == LiftOpStatus.OnHold) operating = true;
                    if (st != LiftOpStatus.OnHold) continue;
                    hours[y, k]++;
                    if (!heldToday[k]) { heldToday[k] = true; days[y, k]++; }
                }
                if (!operating) continue;
                openHours[y]++;
                gustBins[Math.Min(15, s.LiftGustTenths[1] / 50)]++;
            }
            json.Append("{\"site\":\"").Append(site).Append("\",\"baseFeet\":").Append(wx.BaseFeet).Append(",\"summitFeet\":").Append(wx.SummitFeet)
                .Append(",\"limits\":[").Append(SimWorld.HoldLimitMph(P, lifts[0])).Append(',').Append(SimWorld.HoldLimitMph(P, lifts[1])).Append(',')
                .Append(SimWorld.HoldLimitMph(P, lifts[2])).Append("],\"seasons\":[");
            for (int y = 0; y < Years; y++)
            {
                if (y > 0) json.Append(',');
                json.Append("{\"winter\":").Append(FirstYear + y).Append(",\"openHours\":").Append(openHours[y])
                    .Append(",\"hours\":[").Append(hours[y, 0]).Append(',').Append(hours[y, 1]).Append(',').Append(hours[y, 2])
                    .Append("],\"days\":[").Append(days[y, 0]).Append(',').Append(days[y, 1]).Append(',').Append(days[y, 2]).Append("]}");
            }
            json.Append("],\"summitGust\":[").Append(string.Join(",", gustBins)).Append("]}");
            double fg = 0, dq = 0;
            for (int y = 0; y < Years; y++) { fg += hours[y, 1]; dq += hours[y, 2]; }
            TestContext.Progress.WriteLine($"WindScale {P.WindScale}, {site}: base {wx.BaseFeet} ft, summit {wx.SummitFeet} ft; held hours a season: summit fixed grip {Num(fg / Years)}, summit detachable {Num(dq / Years)}");
        }
    }
}
