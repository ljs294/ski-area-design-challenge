using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MountainPlanner.UI;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Task P2-01: one theme for every screen, the accepted mockup's tokens, the UI scale and the ultrawide column.
    public sealed class UiThemeTests
    {
        const string ThemeFolder = "Assets/MountainPlanner/Art/UI/Resources/MountainPlannerUI/";
        const string Mockup = "docs/plans/prototypes/ui-layout.html";
        static readonly string[] Sheets =
        {
            "Assets/MountainPlanner/Art/UI/Hud.uss",
            "Assets/MountainPlanner/Art/UI/SitePicker.uss",
            "Assets/MountainPlanner/Art/UI/Flow/Resources/MountainPlannerFlow/Flow.uss",
            ThemeFolder + "Base.uss",
        };

        static Dictionary<string, string> Tokens(string text) =>
            Regex.Matches(text, @"(?<![\w-])(--[a-z0-9-]+):\s*([^;}]+?)\s*(?:;|$)", RegexOptions.Multiline).Cast<Match>()
                .GroupBy(m => m.Groups[1].Value).ToDictionary(g => g.Key, g => g.First().Groups[2].Value.Trim());

        static Dictionary<string, string> Theme(string name) => Tokens(File.ReadAllText(ThemeFolder + "Theme-" + name + ".tss"));

        static string Block(string text, string selector)
        {
            int i = text.IndexOf(selector + " {", System.StringComparison.Ordinal);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), selector);
            return text.Substring(i, text.IndexOf('}', i) - i);
        }

        /// <summary>A colour as rgba bytes, so #fff, #ffffff and rgb(255, 255, 255) compare equal.</summary>
        static Color32 Parse(string css)
        {
            var rgb = Regex.Match(css, @"rgba?\(\s*(\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\s*\)");
            if (rgb.Success)
                return new Color32(byte.Parse(rgb.Groups[1].Value), byte.Parse(rgb.Groups[2].Value), byte.Parse(rgb.Groups[3].Value),
                    rgb.Groups[4].Success ? (byte)Mathf.RoundToInt(float.Parse(rgb.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) * 255) : (byte)255);
            Assert.That(ColorUtility.TryParseHtmlString(css, out var c), Is.True, css);
            return c;
        }

        [Test]
        public void BothThemesDefineEveryTokenTheScreensUse()
        {
            var dark = Theme("Dark");
            var light = Theme("Light");
            foreach (string sheet in Sheets)
            {
                string uss = File.ReadAllText(sheet);
                var own = Tokens(uss);
                foreach (string token in Regex.Matches(uss, @"var\((--[a-z0-9-]+)\)").Cast<Match>().Select(m => m.Groups[1].Value).Distinct())
                {
                    if (own.ContainsKey(token)) continue;   // a sheet's own constant, such as the title's sign colours
                    Assert.That(dark.ContainsKey(token), $"Theme-Dark defines {token} ({sheet})");
                    Assert.That(light.ContainsKey(token), $"Theme-Light defines {token} ({sheet})");
                }
            }
        }

        [Test]
        public void NoScreenKeepsItsOwnPalette()
        {
            var palette = new HashSet<string>(Theme("Dark").Keys);
            palette.Remove("--unity-cursor-color");
            palette.Remove("--unity-selection-color");
            foreach (string sheet in Sheets)
            {
                var redefined = Tokens(File.ReadAllText(sheet)).Keys.Where(palette.Contains).ToList();
                Assert.That(redefined, Is.Empty, $"{sheet} redefines theme tokens");
            }
        }

        [Test]
        public void EveryTextColourSetsTheOutlineToMatch()
        {
            // The light theme thickens text with a 0.3 px outline (linear blending thins dark-on-light text); the
            // outline must be the text's own colour or it would tint it.
            Assert.That(Regex.IsMatch(File.ReadAllText(ThemeFolder + "Theme-Light.tss"), @"-unity-text-outline-width:\s*0\.3px"), "the light theme's text weight");
            Assert.That(File.ReadAllText(ThemeFolder + "Theme-Dark.tss"), Does.Not.Contain("-unity-text-outline-width"));
            foreach (string sheet in Sheets)
                foreach (Match rule in Regex.Matches(File.ReadAllText(sheet), @"([^{}]*)\{([^{}]*)\}"))
                {
                    string body = rule.Groups[2].Value;
                    var colour = Regex.Match(body, @"(?:^|[\s;{])color:\s*([^;}]+?)\s*;");
                    if (!colour.Success) continue;
                    var outline = Regex.Match(body, @"-unity-text-outline-color:\s*([^;}]+?)\s*;");
                    Assert.That(outline.Success, $"{sheet}: {rule.Groups[1].Value.Trim()} sets color without -unity-text-outline-color");
                    if (!body.Contains("-unity-text-outline-width"))   // map labels draw their own halo
                        Assert.That(outline.Groups[1].Value, Is.EqualTo(colour.Groups[1].Value), $"{sheet}: {rule.Groups[1].Value.Trim()}");
                }
        }

        [TestCase("Dark", ".hud")]
        [TestCase("Light", ".hud.light")]
        public void TheThemesAreTheAcceptedMockups(string theme, string selector)
        {
            // The owner's accepted HUD mockup is the source of every colour; Unity's copy differs only where linear
            // blending forces it: the panels are solid (a 96% panel shows ~13% of a bright map in Unity).
            var mock = Tokens(Block(File.ReadAllText(Mockup), selector));
            var ours = Theme(theme);
            foreach (var (token, value) in mock.Select(p => (p.Key, p.Value)))
            {
                if (token == "--bar-shadow") continue;   // a CSS box shadow, not a colour
                Assert.That(ours.ContainsKey(token), $"Theme-{theme} has the mockup's {token}");
                var want = Parse(value);
                var got = Parse(ours[token]);
                if (token == "--panel")
                {
                    Assert.That(got.a, Is.EqualTo(255), "panels are solid");
                    want.a = 255;
                }
                Assert.That(got, Is.EqualTo(want), $"{token} in Theme-{theme}");
            }
        }

        [TestCase(0, 50)]
        [TestCase(49, 50)]
        [TestCase(52, 50)]
        [TestCase(53, 55)]
        [TestCase(100, 100)]
        [TestCase(108, 110)]
        [TestCase(150, 150)]
        [TestCase(400, 150)]
        public void TheUiScaleSnapsToFivePercentStepsFromHalfToOneAndAHalf(int asked, int snapped)
        {
            Assert.That(UiPreferences.SnapScale(asked), Is.EqualTo(snapped));
        }

        [TestCase(50, 2560, 1440)]
        [TestCase(100, 1280, 720)]   // the accepted mockup's stage: its pixel sizes are ours
        [TestCase(150, 853, 480)]
        public void TheScaleShrinksOrGrowsTheReferenceScreen(int percent, int width, int height)
        {
            Assert.That(UiPanels.Reference(percent), Is.EqualTo(new Vector2Int(width, height)));
        }

        [TestCase(1920, 1080, 1920)]   // 16:9: the whole width
        [TestCase(1280, 1024, 1280)]   // 5:4: the whole width
        [TestCase(2560, 1080, 1920)]   // 21:9
        [TestCase(3440, 1440, 2560)]   // 21:9
        [TestCase(5120, 1440, 2560)]   // 32:9
        public void FullScreenPanelsKeepToA16By9Column(int width, int height, int column)
        {
            Assert.That(StageColumn.WidthFor(width, height), Is.EqualTo(column).Within(0.01f));
        }

        [Test]
        public void AutoFollowsTheSun()
        {
            Assert.That(UiPreferences.ThemeFor(UiThemeChoice.Auto, daylight: true), Is.EqualTo(UiTheme.Light));
            Assert.That(UiPreferences.ThemeFor(UiThemeChoice.Auto, daylight: false), Is.EqualTo(UiTheme.Dark));
            Assert.That(UiPreferences.ThemeFor(UiThemeChoice.Dark, daylight: true), Is.EqualTo(UiTheme.Dark));
            Assert.That(UiPreferences.ThemeFor(UiThemeChoice.Light, daylight: false), Is.EqualTo(UiTheme.Light));
        }

        [Test]
        public void SunsetSwitchesAnAutoThemeAndOnlyThen()
        {
            var choice = UiPreferences.Choice;
            bool day = UiPreferences.Daylight;
            int changes = 0;
            void Count() => changes++;
            UiPreferences.Changed += Count;
            try
            {
                UiPreferences.SetDaylight(true);
                UiPreferences.SetChoice(UiThemeChoice.Auto, remember: false);
                changes = 0;
                UiPreferences.SetDaylight(true);
                Assert.That(changes, Is.Zero, "no change, no event");
                UiPreferences.SetDaylight(false);
                Assert.That(UiPreferences.Theme, Is.EqualTo(UiTheme.Dark));
                Assert.That(changes, Is.EqualTo(1));
                UiPreferences.SetChoice(UiThemeChoice.Light, remember: false);
                changes = 0;
                UiPreferences.SetDaylight(true);
                Assert.That(changes, Is.Zero, "a chosen theme ignores the sun");
            }
            finally
            {
                UiPreferences.Changed -= Count;
                UiPreferences.SetChoice(choice, remember: false);
                UiPreferences.SetDaylight(day);
            }
        }
    }
}
