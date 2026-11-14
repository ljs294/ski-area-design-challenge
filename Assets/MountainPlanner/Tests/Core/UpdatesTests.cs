using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Updates;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task 4.23: the auto updater's engine-free half, which the game and tools/launcher share: versions, GitHub's
    /// release answer, the .sha256 file, the check, and installing a release into a folder per version.
    /// </summary>
    public sealed class UpdatesTests
    {
        string _scratch;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "mp-updates-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_scratch);
        }

        [TearDown]
        public void TearDown()
        {
            try { Directory.Delete(_scratch, true); } catch (IOException) { }
        }

        // ---- Versions ----

        [TestCase("v0.3.2", "0.3.2", false)]
        [TestCase("0.3.2", "0.3.2", false)]
        [TestCase("0.3.2+dev.7.abc1234", "0.3.2+dev.7.abc1234", true)]
        [TestCase("0.3.2+dev.7.abc1234.dirty", "0.3.2+dev.7.abc1234.dirty", true)]
        [TestCase("0.0.0", "0.0.0", true)]
        [TestCase("10.20.30", "10.20.30", false)]
        public void VersionsRead(string text, string shown, bool preview)
        {
            Assert.IsTrue(GameVersion.TryParse(text, out var v));
            Assert.AreEqual(shown, v.ToString());
            Assert.AreEqual(preview, v.IsPreview);
        }

        [TestCase("v0.1")]
        [TestCase("v0.2.0-alpha")]
        [TestCase("0.3")]
        [TestCase("0.3.x")]
        [TestCase("")]
        [TestCase(null)]
        [TestCase("unity-m1")]
        [TestCase("0.3.2+")]
        public void NonVersionsDoNot(string text) => Assert.IsFalse(GameVersion.TryParse(text, out _));

        [Test]
        public void VersionsCompareByNumber()
        {
            Assert.IsTrue(GameVersion.Parse("0.3.10") > GameVersion.Parse("0.3.9"));
            Assert.IsTrue(GameVersion.Parse("0.4.0") > GameVersion.Parse("0.3.99"));
            Assert.IsTrue(GameVersion.Parse("1.0.0") > GameVersion.Parse("0.99.99"));
            Assert.AreEqual(0, GameVersion.Parse("0.3.2+dev.1.abc").CompareTo(GameVersion.Parse("0.3.2")));
        }

        [Test]
        public void TheCheckSendsTheGamesOwnUserAgent() => Assert.AreEqual(Http.UserAgent, GitHubFeed.UserAgent);

        // ---- GitHub's answer ----

        [Test]
        public void TheRealV032AnswerReads()
        {
            string json = File.ReadAllText(Path.Combine(TestData.Folder("updates"), "latest-v0.3.2.json"));
            var info = ReleaseInfo.Parse(json);
            Assert.AreEqual("0.3.2", info.Version.ToString());
            Assert.AreEqual("https://github.com/ljs294/ski-area-design-challenge/releases/tag/v0.3.2", info.PageUrl);
            Assert.AreEqual("SkiAreaDesignChallenge-0.3.2-win64.zip", info.Zip.Name);
            Assert.AreEqual(400923513L, info.Zip.Size);
            Assert.AreEqual("https://github.com/ljs294/ski-area-design-challenge/releases/download/v0.3.2/SkiAreaDesignChallenge-0.3.2-win64.zip", info.Zip.Url);
            Assert.AreEqual("daea7ab75a6723ef794fa03482ff68fbf3f4861538b0ea93391a33c8ba036736", info.Zip.Sha256);
            Assert.AreEqual("SkiAreaDesignChallenge-0.3.2-win64.zip.sha256", info.Sha.Name);
            Assert.IsFalse(info.Draft);
            Assert.IsFalse(info.Prerelease);
        }

        [Test]
        public void AReleaseWithoutItsZipOrShaIsNeverOffered()
        {
            Assert.Throws<FormatException>(() => ReleaseInfo.Parse(Release("v0.3.3", zip: false)));
            Assert.Throws<FormatException>(() => ReleaseInfo.Parse(Release("v0.3.3", sha: false)));
            Assert.Throws<FormatException>(() => ReleaseInfo.Parse(Release("v0.1")));
            Assert.Throws<FormatException>(() => ReleaseInfo.Parse("[1, 2]"));
            Assert.Throws<FormatException>(() => ReleaseInfo.Parse("{\"tag_name\": "));
        }

        [Test]
        public void JsonReadsEscapesAndNesting()
        {
            var root = (Dictionary<string, object>)MiniJson.Parse("{\"a\": \"x\\\"y\\u00e9\\n\", \"b\": [1, -2.5e1, true, null, {}], \"c\": {\"d\": false}}");
            Assert.AreEqual("x\"y\u00e9\n", root["a"]);
            var b = (List<object>)root["b"];
            Assert.AreEqual(1.0, b[0]);
            Assert.AreEqual(-25.0, b[1]);
            Assert.AreEqual(true, b[2]);
            Assert.IsNull(b[3]);
            Assert.AreEqual(false, ((Dictionary<string, object>)root["c"])["d"]);
            Assert.Throws<FormatException>(() => MiniJson.Parse("{\"a\" 1}"));
            Assert.Throws<FormatException>(() => MiniJson.Parse("[1,]x"));
            Assert.Throws<FormatException>(() => MiniJson.Parse(new string('[', 100) + new string(']', 100)));
        }

        // ---- The .sha256 file ----

        [Test]
        public void TheShaFileReads()
        {
            const string hash = "daea7ab75a6723ef794fa03482ff68fbf3f4861538b0ea93391a33c8ba036736";
            Assert.AreEqual(hash, ShaFile.Parse(hash + "  SkiAreaDesignChallenge-0.3.2-win64.zip\r\n", "SkiAreaDesignChallenge-0.3.2-win64.zip"));
            Assert.AreEqual(hash, ShaFile.Parse(hash.ToUpperInvariant() + " *SkiAreaDesignChallenge-0.3.2-win64.zip", "SkiAreaDesignChallenge-0.3.2-win64.zip"));
            Assert.AreEqual(hash, ShaFile.Parse(hash, "any.zip"));
            Assert.Throws<FormatException>(() => ShaFile.Parse(hash + "  SkiAreaDesignChallenge-0.3.1-win64.zip", "SkiAreaDesignChallenge-0.3.2-win64.zip"));
            Assert.Throws<FormatException>(() => ShaFile.Parse("not a hash  x.zip", "x.zip"));
            Assert.Throws<FormatException>(() => ShaFile.Parse("", "x.zip"));
        }

        // ---- The check ----

        [TestCase("0.3.1", UpdateState.Available)]
        [TestCase("0.3.2", UpdateState.UpToDate)]
        [TestCase("0.4.0", UpdateState.UpToDate)]
        [TestCase("0.3.1+dev.4.abc1234", UpdateState.Preview)]
        [TestCase("0.0.0+dev.12.abc1234", UpdateState.Preview)]
        [TestCase("", UpdateState.Preview)]
        public void TheCheckDecides(string installed, UpdateState state)
        {
            var latest = ReleaseInfo.Parse(Release("v0.3.2"));
            Assert.AreEqual(state, UpdateCheck.Decide(installed, latest));
        }

        [Test]
        public void AnUnreachableFeedJustSaysSo()
        {
            var result = UpdateCheck.CheckAsync(new FolderFeed(Path.Combine(_scratch, "nowhere")), "0.3.1", CancellationToken.None).Result;
            Assert.AreEqual(UpdateState.Unreachable, result.State);
            Assert.IsNull(result.Latest);
            StringAssert.Contains("unreachable", result.Message);
        }

        [Test]
        public void APreviewNeverAsks()
        {
            var feed = new CountingFeed(new FolderFeed(_scratch));
            var result = UpdateCheck.CheckAsync(feed, "0.3.2+dev.1.abc1234", CancellationToken.None).Result;
            Assert.AreEqual(UpdateState.Preview, result.State);
            Assert.AreEqual(0, feed.Requests, "a preview build makes no request");
        }

        // ---- Installing ----

        [Test]
        public void InstallsKeepTheNewestTwoAndPlayTheNewest()
        {
            string feedDir = Feed("0.3.1", "0.3.2", "0.3.3");
            var feed = new FolderFeed(feedDir);
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            Install(feed, store, "0.3.1");
            Assert.AreEqual("0.3.1", store.Current?.ToString());
            Assert.IsNull(store.Previous);
            Assert.IsTrue(File.Exists(Path.Combine(store.FolderOf(GameVersion.Parse("0.3.1")), "SkiAreaDesignChallenge_Data", "data.bin")), "the top folder is stripped");

            Install(feed, store, "0.3.2");
            Assert.AreEqual("0.3.2", store.Current?.ToString());
            Assert.AreEqual("0.3.1", store.Previous?.ToString());

            Install(feed, store, "0.3.3");
            CollectionAssert.AreEqual(new[] { "0.3.3", "0.3.2" }, store.Installed().Select(v => v.ToString()).ToArray());
            Assert.IsFalse(Directory.Exists(store.FolderOf(GameVersion.Parse("0.3.1"))), "the third-newest is pruned");
            Assert.IsEmpty(Directory.GetFiles(store.Downloads), "the verified zips are gone once unzipped");
        }

        [Test]
        public void AnOlderReleasePickedFromTheListStaysInstalled()
        {
            string feedDir = Feed("0.3.0", "0.3.1", "0.3.2");
            var feed = new FolderFeed(feedDir);
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            Install(feed, store, "0.3.1");
            Install(feed, store, "0.3.2");
            Install(feed, store, "0.3.0");
            CollectionAssert.AreEqual(new[] { "0.3.2", "0.3.0" }, store.Installed().Select(v => v.ToString()).ToArray(),
                                      "the newest and the one just picked stay; the one between goes");
            Assert.AreEqual("0.3.2", store.Current?.ToString(), "the newest is still the default");
            store.Prune();
            CollectionAssert.AreEqual(new[] { "0.3.2", "0.3.0" }, store.Installed().Select(v => v.ToString()).ToArray(), "a plain prune keeps two");
        }

        [Test]
        public void TheReleasesListOffersOnlyInstallableReleasesNewestFirst()
        {
            string json = "[" + string.Join(", ",
                Release("v0.3.1"),
                Release("v0.3.2"),
                Release("v0.3.4").Replace("\"draft\": false", "\"draft\": true"),
                Release("v0.3.3").Replace("\"prerelease\": false", "\"prerelease\": true"),
                Release("v0.1"),
                Release("v0.3.0", zip: false),
                Release("v0.3.2")) + "]";
            var list = ReleaseInfo.ParseList(json);
            CollectionAssert.AreEqual(new[] { "0.3.2", "0.3.1" }, list.Select(r => r.Version.ToString()).ToArray());
            Assert.Throws<FormatException>(() => ReleaseInfo.ParseList(Release("v0.3.2")), "one release isn't a list");
        }

        [Test]
        public void TheFolderFeedListsItsReleases()
        {
            string feedDir = Feed("0.3.1", "0.3.2");
            var list = new FolderFeed(feedDir).AllAsync(CancellationToken.None).Result;
            CollectionAssert.AreEqual(new[] { "0.3.2", "0.3.1" }, list.Select(r => r.Version.ToString()).ToArray());
            Assert.IsTrue(File.Exists(list[1].Zip.Url), "asset addresses are the folder's own files");
        }

        [Test]
        public void ABadHashInstallsNothingAndKeepsTheOldVersion()
        {
            string feedDir = Feed("0.3.1", "0.3.2");
            var feed = new FolderFeed(feedDir);
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            Install(feed, store, "0.3.1");
            // A download damaged on the way: same size, one byte changed.
            string zip = Path.Combine(feedDir, ReleaseInfo.ZipName(GameVersion.Parse("0.3.2")));
            byte[] bytes = File.ReadAllBytes(zip);
            bytes[bytes.Length / 2] ^= 0xff;
            File.WriteAllBytes(zip, bytes);
            var e = Assert.Throws<AggregateException>(() => Install(feed, store, "0.3.2"));
            Assert.IsInstanceOf<UpdateException>(e.InnerException);
            StringAssert.Contains("didn't match its SHA-256", e.InnerException.Message);
            Assert.AreEqual("0.3.1", store.Current?.ToString());
            Assert.IsEmpty(Directory.GetFiles(store.Downloads), "the bad download is deleted");
            Assert.IsFalse(Directory.Exists(store.FolderOf(GameVersion.Parse("0.3.2"))));
        }

        [Test]
        public void ADigestThatDisagreesWithTheShaFileInstallsNothing()
        {
            string feedDir = Feed("0.3.2");
            string zip = Path.Combine(feedDir, ReleaseInfo.ZipName(GameVersion.Parse("0.3.2")));
            File.WriteAllText(Path.Combine(feedDir, "latest.json"), Release("v0.3.2", size: new FileInfo(zip).Length, digest: new string('1', 64)));
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            var e = Assert.Throws<AggregateException>(() => Install(new FolderFeed(feedDir), store, null));
            StringAssert.Contains("disagree", e.InnerException.Message);
            Assert.IsNull(store.Current);
        }

        [Test]
        public void ADroppedDownloadResumes()
        {
            string feedDir = Feed("0.3.2");
            var feed = new FolderFeed(feedDir);
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            var info = feed.LatestAsync(CancellationToken.None).Result;
            Directory.CreateDirectory(store.Downloads);
            byte[] zip = File.ReadAllBytes(info.Zip.Url);
            string partial = Path.Combine(store.Downloads, info.Zip.Name + VersionStore.PartialSuffix);
            File.WriteAllBytes(partial, zip.Take(zip.Length / 2).ToArray());
            var counting = new CountingFeed(feed);
            ReleaseInstaller.InstallAsync(counting, info, store, null, CancellationToken.None).Wait();
            Assert.AreEqual(zip.Length / 2, counting.FirstOffset, "the download carried on from the partial file");
            Assert.AreEqual("0.3.2", store.Current?.ToString());
        }

        [TestCase("SkiAreaDesignChallenge-0.3.2/../evil.txt")]
        [TestCase("SkiAreaDesignChallenge-0.3.2/sub/../../evil.txt")]
        [TestCase("Other/evil.txt")]
        [TestCase("evil.txt")]
        public void AnEntryOutsideTheVersionFolderInstallsNothing(string entry)
        {
            string feedDir = FeedWithEntry("0.3.2", entry);
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            var e = Assert.Throws<AggregateException>(() => Install(new FolderFeed(feedDir), store, null));
            Assert.IsInstanceOf<UpdateException>(e.InnerException);
            Assert.IsNull(store.Current);
            Assert.IsFalse(File.Exists(Path.Combine(store.Versions, "evil.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(store.Root, "evil.txt")));
            Assert.IsEmpty(Directory.GetDirectories(store.Versions), "the partial folder is removed");
        }

        [Test]
        public void AFolderWithoutTheMarkerNeverPlays()
        {
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            string half = store.FolderOf(GameVersion.Parse("0.3.9"));
            Directory.CreateDirectory(half);
            File.WriteAllText(Path.Combine(half, VersionStore.GameExe), "");
            Assert.IsNull(store.Current);
            File.WriteAllText(Path.Combine(half, VersionStore.Marker), "");
            Assert.AreEqual("0.3.9", store.Current?.ToString());
        }

        [Test]
        public void TheGameFindsItsLauncher()
        {
            var store = new VersionStore(Path.Combine(_scratch, "install"));
            string game = store.FolderOf(GameVersion.Parse("0.3.2"));
            Directory.CreateDirectory(game);
            Assert.IsNull(VersionStore.RootOfGame(game), "no Launcher.exe: a hand-unzipped game");
            File.WriteAllText(store.Launcher, "");
            Assert.AreEqual(store.Root, VersionStore.RootOfGame(game));
            Assert.IsNull(VersionStore.RootOfGame(_scratch));
        }

        [Test]
        public void InsideMeansInside()
        {
            // Built from the scratch folder, not "C:\...", so the test also runs where '\' isn't a separator (CI's Linux).
            string root = Path.Combine(_scratch, "SADC");
            Assert.IsTrue(VersionStore.IsInside(root, Path.Combine(root, "versions", "0.3.2")));
            Assert.IsTrue(VersionStore.IsInside(root, root + Path.DirectorySeparatorChar));
            Assert.IsTrue(VersionStore.IsInside(root, root.ToUpperInvariant()), "Windows paths ignore case");
            Assert.IsFalse(VersionStore.IsInside(root, root + "2"));
            Assert.IsFalse(VersionStore.IsInside(root, _scratch));
            Assert.IsFalse(VersionStore.IsInside("", _scratch));
        }

        // ---- Helpers ----

        void Install(IReleaseFeed feed, VersionStore store, string version)
        {
            var info = version == null ? feed.LatestAsync(CancellationToken.None).Result : feed.TaggedAsync(GameVersion.Parse(version), CancellationToken.None).Result;
            ReleaseInstaller.InstallAsync(feed, info, store, null, CancellationToken.None).Wait();
        }

        /// <summary>A local feed with a tiny release zip per version, the newest as latest.json.</summary>
        string Feed(params string[] versions) => Feed(versions, null);

        string FeedWithEntry(string version, string extra) => Feed(new[] { version }, extra);

        string Feed(string[] versions, string extra)
        {
            string dir = Path.Combine(_scratch, "feed");
            Directory.CreateDirectory(dir);
            foreach (string text in versions)
            {
                var v = GameVersion.Parse(text);
                string name = ReleaseInfo.ZipName(v), top = ReleaseInfo.TopFolder(v);
                string zip = Path.Combine(dir, name);
                using (var stream = new FileStream(zip, FileMode.Create))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    archive.CreateEntry(top + "/");
                    Entry(archive, top + "/SkiAreaDesignChallenge.exe", "game " + text);
                    Entry(archive, top + "/SkiAreaDesignChallenge_Data/data.bin", new string('d', 5000));
                    Entry(archive, top + "/README.txt", "readme");
                    if (extra != null) Entry(archive, extra, "evil");
                }
                string hash;
                using (var sha = SHA256.Create()) hash = ShaFile.Hex(sha.ComputeHash(File.ReadAllBytes(zip)));
                File.WriteAllText(zip + ".sha256", hash + "  " + name + "\n");
                File.WriteAllText(Path.Combine(dir, v.Tag + ".json"), Release(v.Tag, size: new FileInfo(zip).Length, digest: hash));
                File.WriteAllText(Path.Combine(dir, "latest.json"), Release(v.Tag, size: new FileInfo(zip).Length, digest: hash));
            }
            return dir;
        }

        static void Entry(ZipArchive archive, string name, string text)
        {
            using (var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false))) writer.Write(text);
        }

        static string Release(string tag, bool zip = true, bool sha = true, long size = 1000, string digest = null)
        {
            string number = tag.TrimStart('v');
            string name = $"SkiAreaDesignChallenge-{number}-win64.zip";
            var assets = new List<string>();
            string d = digest == null ? "" : $", \"digest\": \"sha256:{digest}\"";
            if (zip) assets.Add($"{{\"name\": \"{name}\", \"size\": {size}, \"browser_download_url\": \"https://github.com/x/releases/download/{tag}/{name}\"{d}}}");
            if (sha) assets.Add($"{{\"name\": \"{name}.sha256\", \"size\": 106, \"browser_download_url\": \"https://github.com/x/releases/download/{tag}/{name}.sha256\"}}");
            return $"{{\"tag_name\": \"{tag}\", \"html_url\": \"https://github.com/x/releases/tag/{tag}\", \"draft\": false, \"prerelease\": false, \"assets\": [{string.Join(", ", assets)}]}}";
        }

        /// <summary>Counts what a feed is asked, and the first download offset.</summary>
        sealed class CountingFeed : IReleaseFeed
        {
            readonly IReleaseFeed _inner;
            public int Requests;
            public long FirstOffset = -1;
            public CountingFeed(IReleaseFeed inner) { _inner = inner; }
            public string Name => _inner.Name;
            public Task<ReleaseInfo> LatestAsync(CancellationToken ct) { Requests++; return _inner.LatestAsync(ct); }
            public Task<ReleaseInfo> TaggedAsync(GameVersion version, CancellationToken ct) { Requests++; return _inner.TaggedAsync(version, ct); }
            public Task<List<ReleaseInfo>> AllAsync(CancellationToken ct) { Requests++; return _inner.AllAsync(ct); }
            public Task<string> ReadTextAsync(ReleaseAsset asset, CancellationToken ct) { Requests++; return _inner.ReadTextAsync(asset, ct); }
            public Task<AssetStream> OpenAsync(ReleaseAsset asset, long offset, CancellationToken ct)
            {
                Requests++;
                if (FirstOffset < 0) FirstOffset = offset;
                return _inner.OpenAsync(asset, offset, ct);
            }
        }
    }
}
