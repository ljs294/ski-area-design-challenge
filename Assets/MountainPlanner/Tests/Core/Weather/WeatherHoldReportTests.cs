using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
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

        /// <summary>
        /// PR B2's re-run (demo 94 round 2): the drawn resort's six lifts (task P3-11) on the Jackson Hole 5 km package, its heights
        /// on the 8 m grid as the game samples them, the static inputs built from them, and the frozen Jackson Hole rows for
        /// five winters at each WindScale, twice: with B1's height rule and with each lift's top-station cell (exposure class,
        /// steered speed). Writes test-results/weather-holds/steered.json. MP_JH5_PACKAGE names the package folder (local only).
        /// </summary>
        [Test]
        public void WriteTheSteeredHoldReport()
        {
            string dir = Environment.GetEnvironmentVariable("MP_JH5_PACKAGE");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) Assert.Ignore("set MP_JH5_PACKAGE to the Jackson Hole 5 km package folder");
            string output = Environment.GetEnvironmentVariable("WEATHER_HOLDS_OUT");
            output = string.IsNullOrEmpty(output)
                ? Path.Combine(Directory.GetParent(Directory.GetParent(TestData.Folder("weather")).FullName).FullName, "test-results", "weather-holds", "steered.json")
                : Path.Combine(Path.GetDirectoryName(output), "steered.json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));

            using var resort = new DrawnResortTests.Loaded(dir);
            var m = ResortPackage.ReadManifest(dir);
            var origin = new AlbersPoint(m.Site.CentreX, m.Site.CentreY);
            var square = SiteSquare.Create(origin, m.Site.SizeMetres / 1000.0);
            var ring = square.Ring;
            var core = square.Core;
            const int cell = WeatherTerrain.CellMetres;
            int width = (int)Math.Ceiling(ring.Width / cell), depth = (int)Math.Ceiling(ring.Height / cell);
            var feet = new int[width * depth];
            var inside = new System.Collections.Generic.List<float>();
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    double x = ring.West + (i + 0.5) * cell, y = ring.South + (j + 0.5) * cell;
                    double h = resort.Heights.At(x, y);
                    feet[j * width + i] = double.IsNaN(h) ? int.MinValue : (int)Math.Round(h / 0.3048);
                    if (!double.IsNaN(h) && x >= core.West && x <= core.East && y >= core.South && y <= core.North) inside.Add((float)h);
                }
            int Col(double x) => Math.Max(0, Math.Min(width, (int)Math.Ceiling((x - ring.West) / cell - 0.5)));
            int Row(double y) => Math.Max(0, Math.Min(depth, (int)Math.Ceiling((y - ring.South) / cell - 0.5)));
            int ri = (int)Math.Floor((origin.X - ring.West) / cell), rj = (int)Math.Floor((origin.Y - ring.South) / cell);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var terrain = WeatherTerrain.Build(width, depth, feet, (int)Math.Round((ring.West - origin.X) * 1000), (int)Math.Round((ring.South - origin.Y) * 1000),
                                               (Col(core.West), Row(core.South), Col(core.East), Row(core.North)), rj * width + ri, SimWeather.TerrainTuning(Pacing.Default));
            TestContext.Progress.WriteLine($"static inputs: {width}×{depth} cells in {clock.ElapsedMilliseconds} ms, hvf_ref {terrain.HvfRefFeet} ft");
            var (baseFeet, summitFeet) = WeatherFetch.ReferenceFeet(inside.ToArray());

            var doc = resort.History.Document;
            var sorted = doc.Lifts.OrderBy(l => l.Id.Value).ToArray();
            var lifts = new LiftInput[sorted.Length];
            for (int k = 0; k < lifts.Length; k++)
            {
                var a = SimDesign.Input(sorted[k]);
                int top = terrain.CellAt(a.TopXMm, a.TopYMm);
                int topFeet = a.TopFeet > 0 ? a.TopFeet : top >= 0 ? terrain.BinCentreFeet(terrain.Bin[top]) : summitFeet;
                lifts[k] = new LiftInput(a.Id, true, LiftSchedule.Daily, null, a.Kind, a.Seats, a.IntervalMs, a.RideMs, topFeet: topFeet, topMm: (a.TopXMm, a.TopYMm));
            }
            var wx = SimWeather.Own(SimWeatherTests.Frozen(WeatherFixtures.JacksonHole)).WithPoints(baseFeet, summitFeet);

            var json = new StringBuilder("{\"years\":").Append(Years).Append(",\"baseFeet\":").Append(baseFeet).Append(",\"summitFeet\":").Append(summitFeet)
                .Append(",\"lifts\":[");
            for (int k = 0; k < lifts.Length; k++)
            {
                int top = terrain.CellAt(lifts[k].TopXMm, lifts[k].TopYMm);
                if (k > 0) json.Append(',');
                json.Append("{\"name\":\"").Append(sorted[k].Name.Replace("\"", "'")).Append("\",\"catalog\":\"").Append(sorted[k].CatalogId)
                    .Append("\",\"kind\":\"").Append(lifts[k].Kind).Append("\",\"topFeet\":").Append(lifts[k].TopFeet)
                    .Append(",\"hvfFeet\":").Append(top >= 0 ? terrain.Hvf[top] * 10 : -1).Append(",\"exposure\":").Append(top >= 0 ? terrain.Exposure[top] : -1)
                    .Append(",\"limitMph\":").Append(SimWorld.HoldLimitMph(Pacing.Default, lifts[k])).Append('}');
            }
            json.Append("],\"scales\":[");
            for (int s = 0; s < Scales.Length; s++)
            {
                var p = Pacing.Parse("WindScale = " + Scales[s]);
                if (s > 0) json.Append(',');
                json.Append("{\"windScale\":").Append(Scales[s]);
                foreach (bool steered in new[] { false, true })
                {
                    var held = HeldHours(p, wx, lifts, steered ? terrain : null);
                    json.Append(steered ? ",\"steered\":[" : ",\"heightRule\":[").Append(string.Join(",", held.Select(v => Num(v)))).Append(']');
                    TestContext.Progress.WriteLine($"WindScale {Scales[s]}, {(steered ? "steered" : "height rule")}: " +
                                                   string.Join(", ", sorted.Select((l, k) => $"{l.Name} {held[k]:F1}")));
                }
                json.Append('}');
            }
            json.Append("]}");
            File.WriteAllText(output, json.ToString());
            TestContext.Progress.WriteLine("wrote " + output);
        }

        /// <summary>Each lift's mean held face hours a season, December to March, over <see cref="Years"/> winters.</summary>
        static double[] HeldHours(Pacing P, SimWeather wx, LiftInput[] lifts, WeatherTerrain terrain)
        {
            var w = new SimWorld(P, new SimDesign(1, 1, 77, DayHours.Default, lifts, 1), SimSite.None, FirstYear, weather: wx);
            if (terrain != null) w.ApplyNow(SimCommand.Of(SimCommandKind.SetWeatherTerrain, 0, terrain));
            var s = new SimSnapshot(P);
            long yearMs = (long)new LabelCalendar(P).YearFaceDays * SimWorld.DayMs;
            var held = new double[lifts.Length];
            long lastHour = w.HourIndex;
            while (w.FaceMs < Years * yearMs)
            {
                if (!w.Open) w.ApplyNow(SimCommand.Of(SimCommandKind.SetMountainOpen, 1));
                w.Step(P.IdleTickFactor);
                if (w.HourIndex == lastHour) continue;
                lastHour = w.HourIndex;
                if (w.FaceMs / yearMs >= Years || w.Phase != SeasonPhase.Season) continue;
                w.Write(s);
                if (s.Month < 12 && s.Month > 3) continue;
                for (int k = 0; k < lifts.Length; k++) if (s.LiftStatus[k] == LiftOpStatus.OnHold) held[k]++;
            }
            for (int k = 0; k < held.Length; k++) held[k] /= Years;
            return held;
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
