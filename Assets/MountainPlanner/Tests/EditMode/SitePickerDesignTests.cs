using System.IO;
using System.Linq;
using MountainPlanner.Domain.Geo;
using MountainPlanner.UI.Picker;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.UIElements;

namespace MountainPlanner.Tests
{
    // Task 13: the picker follows the accepted UI rules (game-ui-direction.md UI-6 and §5, 0.4 §1 and §7).
    public sealed class SitePickerDesignTests
    {
        const string Uss = "Assets/MountainPlanner/Art/UI/SitePicker.uss";
        const string Uxml = "Assets/MountainPlanner/Art/UI/SitePicker.uxml";

        [Test]
        public void SurveyorsOrangeIsLeftToThePlan()
        {
            // §5: plans are surveyor's orange, "used for nothing else" (not focus, not state).
            string uss = File.ReadAllText(Uss).ToLowerInvariant();
            Assert.That(uss, Does.Not.Contain("ff6a1f").And.Not.Contain("e35b14").And.Not.Contain("--flag"));
        }

        [Test]
        public void TheWindowHasOneFilledButtonAndNoCameraButtons()
        {
            var tree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(Uxml).CloneTree();
            var buttons = tree.Query<Button>().ToList();
            Assert.That(buttons.Count(b => b.ClassListContains("go")), Is.EqualTo(1), "one commit (Download) per window");
            Assert.That(tree.Q<Button>("retry").ClassListContains("ghost"), Is.True, "Retry is a ghost, not a second commit");
            Assert.That(buttons.Any(b => b.name.StartsWith("zoom")), Is.False, "UI-10: the wheel and + − zoom, no camera buttons");
        }

        [TestCase(100, "Excellent")]
        [TestCase(90, "Excellent")]
        [TestCase(89, "Good")]
        [TestCase(75, "Good")]
        [TestCase(63, "Fair")]
        [TestCase(48, "Limited")]
        public void TheScoreComesWithItsWord(int score, string word)
        {
            Assert.That(PickerText.QualityWord(score), Is.EqualTo(word), "the same bands as the HUD's quality badge");
        }

        [Test]
        public void ASiteThatIsNotAll1mIsWarnedInWords()
        {
            var all1m = new SiteEstimate(100, new double[] { 0.9, 0.1, 0, 0 }, 85_000_000, 40, false);
            Assert.That(PickerText.Warning(all1m, imperial: false), Is.Null);
            var crystal = new SiteEstimate(63, new double[] { 0, 0.09, 0.90, 0.01 }, 390_000_000, 92, false);
            Assert.That(PickerText.Warning(crystal, imperial: false), Is.EqualTo("Not all 1 m: 91% of this site is ~3 m or ~10 m terrain, which shows less detail."));
            Assert.That(PickerText.Sources(crystal, imperial: false), Is.EqualTo("9% 1 m lidar · 90% ~3 m · 1% ~10 m"));
            // Resolutions follow the units like every other figure (owner, 2026-10-03).
            Assert.That(PickerText.Warning(crystal, imperial: true), Is.EqualTo("Not all 3 ft: 91% of this site is ~10 ft or ~33 ft terrain, which shows less detail."));
            Assert.That(PickerText.Sources(crystal, imperial: true), Is.EqualTo("9% 3 ft lidar · 90% ~10 ft · 1% ~33 ft"));
            Assert.That(PickerText.SourceLabel(MountainPlanner.Domain.Terrain.TerrainSource.S1m, imperial: true), Is.EqualTo("3 ft S1M"));
            Assert.That(PickerText.Download(crystal), Is.EqualTo("about 390 MB, 2 min"));
            var rough = new SiteEstimate(30, new double[] { 0, 0, 0, 1 }, 200_000_000, 60, true);
            Assert.That(PickerText.Warning(rough, imperial: false), Does.StartWith("The terrain data here couldn't be checked"));
        }

        [TestCase(84_000_000, "85 MB")]
        [TestCase(2_000_000, "5 MB")]
        [TestCase(387_000_000, "390 MB")]
        public void DownloadSizesAreRoundedLikeEstimates(long bytes, string text)
        {
            Assert.That(PickerText.Megabytes(bytes), Is.EqualTo(text));
        }
    }
}
