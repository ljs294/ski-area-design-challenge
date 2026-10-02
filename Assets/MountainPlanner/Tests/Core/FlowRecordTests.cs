using System;
using System.IO;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>Task 14: the quality bands, the paused-download records and the recently-opened file.</summary>
    public sealed class QualityBandTests
    {
        [TestCase(100, QualityBand.Excellent)]
        [TestCase(90, QualityBand.Excellent)]
        [TestCase(89, QualityBand.Good)]
        [TestCase(75, QualityBand.Good)]
        [TestCase(74, QualityBand.Fair)]
        [TestCase(50, QualityBand.Fair)]
        [TestCase(49, QualityBand.Limited)]
        [TestCase(0, QualityBand.Limited)]
        public void BandsFollowTheSpec(int score, QualityBand band) => Assert.That(QualityBands.Of(score), Is.EqualTo(band), "0.4 S5");

        [Test]
        public void TheNumberAndTheWordAlwaysAppearTogether()
        {
            Assert.That(QualityBands.Describe(97), Is.EqualTo("97 / 100 · Excellent"));
            Assert.That(QualityBands.Describe(71), Is.EqualTo("71 / 100 · Fair"));
            Assert.That(QualityBands.Caveat(QualityBand.Fair), Is.EqualTo("Parts of this area use coarser data."));
            Assert.That(QualityBands.Caveat(QualityBand.Excellent), Is.Empty);
        }
    }

    public sealed class PendingDownloadTests
    {
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-pending-" + TestContext.CurrentContext.Test.ID);

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        static PendingDownload Crystal(string started = "2026-10-02T10:00:00Z") => new PendingDownload
        {
            Id = PendingDownloads.IdFor("Crystal Mountain", 46.935, -121.474, 2), Name = "Crystal Mountain",
            Latitude = 46.935, Longitude = -121.474, SizeKm = 2, StartedUtc = started,
        };

        [Test]
        public void TheSameRequestAlwaysMapsToTheSameId()
        {
            string a = PendingDownloads.IdFor("Crystal Mountain", 46.935, -121.474, 2);
            Assert.That(PendingDownloads.IdFor("  Crystal Mountain ", 46.935, -121.474, 2), Is.EqualTo(a));
            Assert.That(a, Does.StartWith("crystal-mountain-"));
            Assert.That(PendingDownloads.IdFor("Crystal Mountain", 46.935, -121.474, 2.1), Is.Not.EqualTo(a), "another size is another download");
            Assert.That(PendingDownloads.IdFor("Crystal Mountain", 46.936, -121.474, 2), Is.Not.EqualTo(a), "another place is another download");
            Assert.That(PendingDownloads.IdFor("???", 1, 2, 3), Does.StartWith("site-"));
        }

        [Test]
        public void ARecordSurvivesAQuitAndIsGoneOnceRemoved()
        {
            var d = Crystal();
            d.LastOverall = 0.38;
            d.LastStage = "Forest";
            PendingDownloads.Save(_root, d);
            var list = PendingDownloads.List(_root);
            Assert.That(list, Has.Count.EqualTo(1));
            Assert.That(list[0].Name, Is.EqualTo("Crystal Mountain"));
            Assert.That(list[0].LastOverall, Is.EqualTo(0.38));
            Assert.That(list[0].LastStage, Is.EqualTo("Forest"));
            Assert.That(PendingDownloads.BuildFolder(_root, d), Does.StartWith(PendingDownloads.FolderOf(_root, d)));

            PendingDownloads.Remove(_root, d);
            Assert.That(PendingDownloads.List(_root), Is.Empty);
            Assert.That(Directory.Exists(PendingDownloads.FolderOf(_root, d)), Is.False, "the partial build goes with it");
        }

        [Test]
        public void UnreadableOrForeignRecordsAreSkipped()
        {
            PendingDownloads.Save(_root, Crystal());
            string broken = Path.Combine(PendingDownloads.Folder(_root), "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, PendingDownloads.RecordFile), "{ not json");
            string future = Path.Combine(PendingDownloads.Folder(_root), "future");
            Directory.CreateDirectory(future);
            File.WriteAllText(Path.Combine(future, PendingDownloads.RecordFile), "{\"Version\": 99, \"Id\": \"future\"}");
            Assert.That(PendingDownloads.List(_root), Has.Count.EqualTo(1));
        }

        [Test]
        public void TheLibraryDoesNotListPartialBuilds()
        {
            // A build writes its manifest before the terrain is prepared; it lives outside Resorts, so S2 never shows it half-done.
            var d = Crystal();
            PendingDownloads.Save(_root, d);
            Assert.That(PendingDownloads.BuildFolder(_root, d), Does.Not.StartWith(ResortLibrary.ResortsFolder(_root)));
        }

        [Test]
        public void RecentResortsRemembersTheLatestPresentMountain()
        {
            RecentResorts.Touch(_root, "aaa", "2026-10-01T09:00:00Z");
            RecentResorts.Touch(_root, "bbb", "2026-10-02T09:00:00Z");
            var r = RecentResorts.Load(_root);
            Assert.That(r.Latest(new[] { "aaa", "bbb" }), Is.EqualTo("bbb"));
            Assert.That(r.Latest(new[] { "aaa" }), Is.EqualTo("aaa"), "a deleted mountain isn't continued");
            Assert.That(r.Latest(Array.Empty<string>()), Is.Null);
            File.WriteAllText(Path.Combine(_root, RecentResorts.FileName), "garbage");
            Assert.That(RecentResorts.Load(_root).Opened, Is.Empty, "a bad file is never an error");
        }
    }
}
