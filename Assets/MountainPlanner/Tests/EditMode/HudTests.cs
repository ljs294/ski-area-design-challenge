using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MountainPlanner.UI.Hud;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>The Trailhead HUD's words and its names against the mockup (task P2-02).</summary>
    public sealed class HudTests
    {
        const string Mockup = "docs/plans/prototypes/ui-layout.html";
        const string Uxml = "Assets/MountainPlanner/Art/UI/Hud.uxml";
        /// <summary>The mockup's photo bar (task P2-07) and the flow's windows it shows (Credits) live in their own documents.</summary>
        static readonly string[] OtherUxml =
        {
            "Assets/MountainPlanner/Art/UI/Photo/Resources/MountainPlannerPhoto/Photo.uxml",
            "Assets/MountainPlanner/Art/UI/Flow/Resources/MountainPlannerFlow/Flow.uxml",
            "Assets/MountainPlanner/Art/UI/Photo/Resources/MountainPlannerPhoto/Ride.uxml",   // the ride bar (task 4.11 PR B)
        };

        [TestCase(0, "12:00", "AM")]
        [TestCase(5 * 3600 + 27 * 60 + 47, "05:27", "AM")]
        [TestCase(10 * 3600 + 30 * 60, "10:30", "AM")]
        [TestCase(12 * 3600, "12:00", "PM")]
        [TestCase(16 * 3600 + 30 * 60, "04:30", "PM")]
        [TestCase(23 * 3600 + 59 * 60 + 59, "11:59", "PM")]
        public void TheClockReadsAsTheMockups(int second, string hm, string ampm)
        {
            Assert.That(HudText.HoursMinutes(second), Is.EqualTo(hm));
            Assert.That(HudText.AmPm(second), Is.EqualTo(ampm));
            Assert.That(HudText.Seconds(second), Is.EqualTo(":" + (second % 60).ToString("D2")));
        }

        // 4.21 findings F1, F4: one status word for trails, lifts and later structures; placeholders until 4.07 and 4.11.
        [Test]
        public void StatusWordsCoverEveryStateAndPlansReadPlanned()
        {
            Assert.That(MountainPlanner.UI.OperatingStatusWords.All.Select(MountainPlanner.UI.OperatingStatusWords.Word),
                Is.EqualTo(new[] { "Open", "Scheduled", "On hold", "Closed", "Planned" }));
            Assert.That(MountainPlanner.UI.OperatingStatusWords.Placeholder(false), Is.EqualTo(MountainPlanner.UI.OperatingStatus.Planned));
            Assert.That(MountainPlanner.UI.OperatingStatusWords.Placeholder(true), Is.EqualTo(MountainPlanner.UI.OperatingStatus.Closed));
            Assert.That(MountainPlanner.UI.OperatingStatusWords.Bins().Select(b => b.Item2), Is.EqualTo(MountainPlanner.UI.OperatingStatusWords.All.Select(MountainPlanner.UI.OperatingStatusWords.Word)));
        }

        // 4.21 finding F5: the lift card's subtitle, the type as players say it, for every carrier the menu offers.
        [TestCase("detach-six:sessellift", "Detachable Six Pack")]
        [TestCase("detach-quad:sessellift", "Detachable Quad")]
        [TestCase("sessellift-fgq4", "Fixed-Grip Quad")]
        [TestCase("monta-fg4", "Fixed-Grip Quad")]
        [TestCase("sessellift-stern3", "Fixed-Grip Triple")]
        [TestCase("fixed-double:monta", "Fixed-Grip Double")]
        [TestCase("monta-tbar", "T-Bar")]
        [TestCase("monta-jbar", "J-Bar")]
        [TestCase("glidepath-carpet", "Magic Carpet")]
        [TestCase("glidepath-carpet-covered", "Magic Carpet")]
        [TestCase("glidepath-carpet2", "Double Magic Carpet")]
        [TestCase("gondola-8:monta", "Gondola")]
        [TestCase("tram-100:monta", "Aerial Tram")]
        [TestCase("detach-eight:monta", "Detachable Eight")]
        public void TheLiftCardNamesItsType(string catalogId, string name) =>
            Assert.That(MountainPlanner.UI.Tools.LiftTypeMenu.TypeName(catalogId), Is.EqualTo(name));

        [Test]
        public void EveryCarrierHasATypeName()
        {
            foreach (var carrier in MountainPlanner.Domain.Design.LiftCatalogIds.Carriers)
                Assert.That(MountainPlanner.UI.Tools.LiftTypeMenu.TypeName(carrier + ":monta"), Is.Not.EqualTo("Lift"), carrier);
        }

        [Test]
        public void TwentyFourHourClock() => Assert.That(HudText.HoursMinutes(17 * 3600 + 5 * 60, twentyFour: true), Is.EqualTo("17:05"));

        [TestCase(2026, 244, 1, "Tue, Sep 1")]    // the mockup's day 1
        [TestCase(2026, 15, 137, "Thu, Jan 15")]  // the view's opening day; the mockup's p2 day 137
        [TestCase(2027, 1, 123, "Fri, Jan 1")]
        [TestCase(2028, 60, 182, "Tue, Feb 29")]  // a leap day
        [TestCase(2026, 243, 365, "Mon, Aug 31")] // the last day of the season that began 1 Sep 2025
        public void SeasonDaysAndDates(int year, int dayOfYear, int seasonDay, string date)
        {
            Assert.That(HudText.SeasonDay(year, dayOfYear), Is.EqualTo(seasonDay));
            Assert.That(HudText.Day(seasonDay), Is.EqualTo("Day " + seasonDay));
            Assert.That(HudText.Date(year, dayOfYear), Is.EqualTo(date));
        }

        [Test]
        public void TextIsMadeOnceAndKept()
        {
            Assert.That(HudText.HoursMinutes(3600), Is.SameAs(HudText.HoursMinutes(3600 + 59)));
            Assert.That(HudText.Date(2026, 15), Is.SameAs(HudText.Date(2026, 15)));
            Assert.That(HudText.Day(42), Is.SameAs(HudText.Day(42)));
        }

        [Test]
        public void TheSimulationsDayAndDateCells()
        {
            // Task 4.03 (TM-07, TM-08, TM-14): "Day 12" in the season, else the phase; the label date is a third of a month.
            Assert.That(HudText.SimDay(MountainPlanner.Simulation.SeasonPhase.Season, 12), Is.EqualTo("Day 12"));
            Assert.That(HudText.SimDay(MountainPlanner.Simulation.SeasonPhase.PreSeason, 0), Is.EqualTo("Pre-season"));
            Assert.That(HudText.SimDay(MountainPlanner.Simulation.SeasonPhase.Summer, 40), Is.EqualTo("Summer"));
            string date = HudText.SimDate(12, MountainPlanner.Simulation.MonthThird.Mid, MountainPlanner.Simulation.DayType.Weekend);
            Assert.That(date, Is.EqualTo("mid-Dec · Weekend"));
            Assert.That(HudText.SimDate(12, MountainPlanner.Simulation.MonthThird.Mid, MountainPlanner.Simulation.DayType.Weekend), Is.SameAs(date));
        }

        [Test]
        public void TheWeatherLineReadsBaseSummitAndTheSnowAhead()
        {
            Assert.That(HudText.WeatherLine(HudText.WholeDegrees(214, false), HudText.WholeDegrees(86, false), HudText.WholeSnow(38, false), false),
                        Is.EqualTo("21° · 9° · +4 in"));
            Assert.That(HudText.WeatherLine(22, 9, 0, false), Is.EqualTo("22° · 9°"), "no snow forecast: no snow part");
            Assert.That(HudText.WeatherLine(22, 9, -1, false), Is.EqualTo("22° · 9°"), "no forecast yet");
            int b = HudText.WholeDegrees(214, true), t = HudText.WholeDegrees(86, true);
            Assert.That((b, t), Is.EqualTo((-6, -13)));
            Assert.That(HudText.WholeSnow(38, true), Is.EqualTo(10));
            Assert.That(HudText.WeatherLine(b, t, 10, true), Is.EqualTo("\u22126° · \u221213° · +10 cm"), "a true minus sign");
            StringAssert.Contains("No snow forecast", HudText.WeatherTip(22, 9, 0, false));
            StringAssert.Contains("4 in of snow", HudText.WeatherTip(21, 9, 4, false));
            StringAssert.Contains("evening sheet", HudText.WeatherTip(21, 9, -1, false));
        }

        /// <summary>Every part the mockup names (data-ui) exists in the HUD with the same name, so the parity check can measure it.</summary>
        [Test]
        public void EveryMockupPartHasItsElement()
        {
            string mockup = File.ReadAllText(Mockup), uxml = File.ReadAllText(Uxml) + string.Concat(OtherUxml.Select(File.ReadAllText));
            var named = new HashSet<string>(Regex.Matches(uxml, "name=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value));
            // Parts made in code: the HUD's tool tiles, the legend's lines and the Analysis line; the credits' hero and sections;
            // the tool window and its review (P3-06, ToolWindow); Analysis › Network's line (task P3-10); the lift card (task 4.11).
            var made = new[] { "tool-", "toolwin", "review-", "analysis-empty", "credits-hero", "credits-data", "credits-picker", "credits-art", "credits-software", "network-", "lift-card" };
            var missing = Regex.Matches(mockup, "data-ui=\"([a-z0-9-]+)(?:\\$\\{[^}]*\\})?([a-z0-9-]*)\"").Cast<Match>()
                .Select(m => m.Groups[1].Value + m.Groups[2].Value)
                .Where(n => !n.EndsWith("-") && !named.Contains(n) && !made.Any(n.StartsWith))
                .Distinct().ToList();
            Assert.That(missing, Is.Empty, "mockup parts with no element of that name in Hud.uxml, Photo.uxml or Flow.uxml");
        }

        [Test]
        public void EverySymbolHasItsVectorImages()
        {
            foreach (var symbol in HudIconLayers.All)
                foreach (var layer in symbol.Value)
                    Assert.That(File.Exists($"Assets/MountainPlanner/Art/UI/Icons/Resources/HudIcons/{layer.Image}.svg"), Is.True, layer.Image);
            string mockup = File.ReadAllText(Mockup);
            int symbols = Regex.Matches(mockup, "<symbol id=").Count;
            Assert.That(HudIconLayers.All.Count, Is.EqualTo(symbols), "re-run node tools/ui-parity/extract-icons.mjs after changing the mockup's symbols");
        }
    }
}
