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

        /// <summary>Every part the mockup names (data-ui) exists in the HUD with the same name, so the parity check can measure it.</summary>
        [Test]
        public void EveryMockupPartHasItsElement()
        {
            string mockup = File.ReadAllText(Mockup), uxml = File.ReadAllText(Uxml);
            var named = new HashSet<string>(Regex.Matches(uxml, "name=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value));
            // Parts the HUD makes in code: the tool tiles, the legend's lines and the Analysis line.
            var made = new[] { "tool-", "analysis-empty" };
            var missing = Regex.Matches(mockup, "data-ui=\"([a-z0-9-]+)(?:\\$\\{[^}]*\\})?([a-z0-9-]*)\"").Cast<Match>()
                .Select(m => m.Groups[1].Value + m.Groups[2].Value)
                .Where(n => !n.EndsWith("-") && !named.Contains(n) && !made.Any(n.StartsWith))
                .Distinct().ToList();
            Assert.That(missing, Is.Empty, "mockup parts with no element of that name in Hud.uxml");
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
