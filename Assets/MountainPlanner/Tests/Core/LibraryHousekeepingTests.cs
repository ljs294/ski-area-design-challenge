using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MountainPlanner.Persistence;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task P2-04: the library's actions (rename, delete, Free space), disk use, and the cache versions that games
    /// on different branches share. Everything runs on a scratch copy of the v1 format fixture, or on fake caches.
    /// </summary>
    public sealed class LibraryHousekeepingTests
    {
        const string JacksonHole = "5792676e513f5302", CrystalFolder = "3d310fa515bffb49";

        string _root;

        [SetUp]
        public void SetUp()
        {
            string source = TestData.Folder(Path.Combine("formats", "v1-library"));
            _root = Path.Combine(Path.GetTempPath(), "mp-housekeeping-" + Guid.NewGuid().ToString("N"));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_root, file.Substring(source.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target);
            }
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        string Package(string folder) => Path.Combine(ResortLibrary.ResortsFolder(_root), folder);

        LibraryEntry Entry(string folder) => ResortLibrary.Scan(_root).Single(e => Path.GetFileName(e.Folder) == folder);

        /// <summary>A fake cache folder of a given version with <paramref name="bytes"/> of tiles.</summary>
        static string FakeCache(string package, int version, int bytes)
        {
            string folder = TerrainCache.FolderFor(package, version);
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, "t0_0.h16"), new byte[bytes]);
            File.WriteAllText(Path.Combine(folder, TerrainCache.ManifestFile), "{ \"CacheVersion\": " + version + " }");
            return folder;
        }

        static string Hash(string path)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(path)));
        }

        // ---------- rename ----------

        [Test]
        public void ARenameIsKeptInTheViewStateAndSurvivesARestart()
        {
            string manifest = Path.Combine(Package(JacksonHole), ResortPackage.ManifestFile);
            string before = Hash(manifest);
            var jh = Entry(JacksonHole);
            Assert.That(jh.Name, Is.EqualTo("Jackson Hole"));
            Assert.That(jh.OriginalName, Is.EqualTo("Jackson Hole"));

            Assert.That(ResortLibrary.Rename(jh, "  Teton   Village\t"), Is.True);
            Assert.That(jh.Name, Is.EqualTo("Teton Village"), "spaces tidied");

            // A restart is a fresh scan of the disk.
            var again = Entry(JacksonHole);
            Assert.That(again.Name, Is.EqualTo("Teton Village"));
            Assert.That(again.OriginalName, Is.EqualTo("Jackson Hole"), "the demo and -site still find it");
            Assert.That(again.PackageId, Is.EqualTo(JacksonHole), "the id doesn't move");
            Assert.That(Hash(manifest), Is.EqualTo(before), "the package is immutable: the manifest isn't touched");
            Assert.That(ResortLibrary.DisplayName(Package(JacksonHole), ResortPackage.ReadManifest(Package(JacksonHole))), Is.EqualTo("Teton Village"));

            // The view state kept everything else it had: the rename migrated it from v1 to v2.
            var file = JObject.Parse(File.ReadAllText(Path.Combine(Package(JacksonHole), ViewState.FileName)));
            Assert.That((int)file["Version"], Is.EqualTo(2));
            Assert.That((string)file["Name"], Is.EqualTo("Teton Village"));
            var view = ViewState.Load(Package(JacksonHole));
            Assert.That(view.Camera.TargetX, Is.EqualTo(120.5));
            Assert.That(view.Bookmarks.Single().Name, Is.EqualTo("Corbet's Couloir"));
            Assert.That(view.SecondOfDay, Is.EqualTo(16 * 3600));
            Assert.That(view.Layers["forest"], Is.False);

            // An area with no view.json gets one; the original name clears the player's own.
            var crystal = Entry(CrystalFolder);
            Assert.That(ResortLibrary.Rename(crystal, "Crystal North"), Is.True);
            Assert.That(Entry(CrystalFolder).Name, Is.EqualTo("Crystal North"));
            Assert.That(ResortLibrary.Rename(Entry(CrystalFolder), "Crystal Mountain"), Is.True);
            Assert.That(ViewState.Load(Package(CrystalFolder)).Name, Is.Empty, "back to the downloaded name");
            Assert.That(Entry(CrystalFolder).Name, Is.EqualTo("Crystal Mountain"));

            // The library sorts by the new name.
            Assert.That(ResortLibrary.Scan(_root).Select(e => e.Name), Is.EqualTo(new[] { "Crystal Mountain", "Teton Village" }));
        }

        [Test]
        public void ARenameWithNoNameOrOverANewerViewStateWritesNothing()
        {
            var jh = Entry(JacksonHole);
            string viewPath = Path.Combine(Package(JacksonHole), ViewState.FileName);
            string before = Hash(viewPath);
            Assert.That(ResortLibrary.Rename(jh, "   "), Is.False);
            Assert.That(ResortLibrary.Rename(jh, "\n\t"), Is.False);
            Assert.That(Hash(viewPath), Is.EqualTo(before));

            var o = JObject.Parse(File.ReadAllText(viewPath));
            o["Version"] = 9;
            File.WriteAllText(viewPath, o.ToString());
            before = Hash(viewPath);
            var refused = Entry(JacksonHole);
            Assert.That(refused.RenameRefusal, Does.Contain("view state was saved by a newer version"));
            Assert.That(ResortLibrary.Rename(refused, "Teton Village"), Is.False);
            Assert.That(Hash(viewPath), Is.EqualTo(before), "never written over a newer file");
            Assert.That(Entry(JacksonHole).Name, Is.EqualTo("Jackson Hole"));
        }

        [TestCase("Jackson Hole", "Jackson Hole")]
        [TestCase("  Big  Sky \r\n", "Big Sky")]
        [TestCase("a\u0000b", "a b")]
        [TestCase("", "")]
        [TestCase(null, "")]
        public void NamesAreTidied(string name, string expected) => Assert.That(ResortLibrary.NormalizeName(name), Is.EqualTo(expected));

        [Test]
        public void LongNamesAreCutWithoutSplittingACharacter()
        {
            Assert.That(ResortLibrary.NormalizeName(new string('x', 80)), Has.Length.EqualTo(ResortLibrary.MaxNameLength));
            string emoji = new string('x', ResortLibrary.MaxNameLength - 1) + "\U0001F3BF";   // the ski straddles the limit
            Assert.That(ResortLibrary.NormalizeName(emoji), Is.EqualTo(new string('x', ResortLibrary.MaxNameLength - 1)));
        }

        // ---------- disk use ----------

        [Test]
        public void DiskUseMatchesTheFolderSizes()
        {
            string jh = Package(JacksonHole);
            FakeCache(jh, TerrainCache.Version, 5000);
            FakeCache(jh, TerrainCache.Version - 1, 3000);
            FakeCache(jh, TerrainCache.Version + 1, 700);
            Directory.CreateDirectory(Path.Combine(jh, "notes"));
            File.WriteAllBytes(Path.Combine(jh, "notes", "a.bin"), new byte[123]);

            var entries = ResortLibrary.Scan(_root);
            Assert.That(entries.All(e => !e.Measured && e.BytesOnDisk == 0), "a scan reads only the small files");
            ResortLibrary.Measure(entries);
            foreach (var e in entries)
            {
                long expected = Directory.GetFiles(e.Folder, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                Assert.That(e.Measured, Is.True);
                Assert.That(e.BytesOnDisk, Is.EqualTo(expected), e.Name);
                Assert.That(e.Disk.Total, Is.EqualTo(expected));
            }
            var use = entries.Single(e => e.PackageId == JacksonHole).Disk;
            long cacheJson(int v) => new FileInfo(Path.Combine(TerrainCache.FolderFor(jh, v), TerrainCache.ManifestFile)).Length;
            Assert.That(use.Cache, Is.EqualTo(5000 + cacheJson(TerrainCache.Version)));
            Assert.That(use.OlderCaches, Is.EqualTo(3000 + cacheJson(TerrainCache.Version - 1)));
            Assert.That(use.NewerCaches, Is.EqualTo(700 + cacheJson(TerrainCache.Version + 1)));
            Assert.That(use.Package, Is.EqualTo(new FileInfo(Path.Combine(jh, ResortPackage.ManifestFile)).Length
                                                + new FileInfo(Path.Combine(jh, ViewState.FileName)).Length + 123));
            Assert.That(ResortLibrary.Freeable(entries), Is.EqualTo(use.OlderCaches));
        }

        // ---------- delete ----------

        [Test]
        public void DeleteRemovesTheWholeAreaAndSaysWhatItFreed()
        {
            string jh = Package(JacksonHole);
            FakeCache(jh, TerrainCache.Version, 4000);
            long size = Directory.GetFiles(jh, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

            Assert.That(ResortLibrary.TryRemove(Entry(JacksonHole), out long freed), Is.True);
            Assert.That(freed, Is.EqualTo(size));
            Assert.That(Directory.Exists(jh), Is.False);
            Assert.That(Directory.GetDirectories(ResortLibrary.ResortsFolder(_root)).Select(Path.GetFileName), Is.EqualTo(new[] { CrystalFolder }), "no trash left");
            Assert.That(ResortLibrary.Scan(_root).Select(e => e.Name), Is.EqualTo(new[] { "Crystal Mountain" }), "and after a restart");
        }

        [Test]
        public void AnAreaThatsOpenIsNeverDeleted()
        {
            string jh = Package(JacksonHole);
            FakeCache(jh, TerrainCache.Version, 4000);
            var entry = Entry(JacksonHole);
            using (TerrainCache.Hold(jh))
            {
                Assert.That(ResortLibrary.TryRemove(entry, out long freed), Is.False);
                Assert.That(freed, Is.Zero);
                Assert.That(File.Exists(Path.Combine(jh, ResortPackage.ManifestFile)), Is.True, "not one file gone");
                Assert.That(File.Exists(Path.Combine(TerrainCache.FolderFor(jh), "t0_0.h16")), Is.True);
            }
            Assert.That(ResortLibrary.TryRemove(entry, out _), Is.True, "closed, it can go");
        }

        [Test]
        public void ATrashFolderLeftByAnInterruptedDeleteIsNeverListedAndFreeSpaceSweepsIt()
        {
            string trash = Path.Combine(ResortLibrary.ResortsFolder(_root), ".trash-" + JacksonHole + "-0");
            Directory.Move(Package(JacksonHole), trash);
            Assert.That(ResortLibrary.Scan(_root).Select(e => e.Name), Is.EqualTo(new[] { "Crystal Mountain" }));
            Assert.That(ResortLibrary.FreeSpace(_root), Is.GreaterThan(0));
            Assert.That(Directory.Exists(trash), Is.False);
        }

        // ---------- cache versions ----------

        [Test]
        public void ABuildKeepsNewerVersionsAndTheNewestOlderOne()
        {
            string jh = Package(JacksonHole);
            int v = TerrainCache.Version;
            foreach (int version in new[] { v - 3, v - 2, v - 1, v, v + 1, v + 4 }) FakeCache(jh, version, 100);
            Directory.CreateDirectory(Path.Combine(jh, "cache-vX"));   // not a version: left alone

            Assert.That(TerrainCache.Prune(jh, v), Is.EqualTo(new[] { v - 3, v - 2 }));
            Assert.That(TerrainCache.Versions(jh), Is.EqualTo(new[] { v - 1, v, v + 1, v + 4 }), "the newest two are kept, and a newer game's are never touched");
            Assert.That(Directory.Exists(Path.Combine(jh, "cache-vX")), Is.True);

            // A game on v+1 tidies the same way: v-1 goes, v (the newest older one for it) stays.
            Assert.That(TerrainCache.Prune(jh, v + 1), Is.EqualTo(new[] { v - 1 }));
            Assert.That(TerrainCache.Versions(jh), Is.EqualTo(new[] { v, v + 1, v + 4 }));
        }

        [Test]
        public void ACacheThatsOpenIsNeverDeleted()
        {
            string jh = Package(JacksonHole);
            int v = TerrainCache.Version;
            string old = FakeCache(jh, v - 5, 100);
            string closed = FakeCache(jh, v - 1, 100);
            long closedBytes = Directory.GetFiles(closed).Sum(f => new FileInfo(f).Length);
            using (TerrainCache.Hold(jh, v - 5))
            {
                Assert.That(TerrainCache.InUse(old), Is.True);
                Assert.That(TerrainCache.Prune(jh, v), Is.Empty);
                Assert.That(TerrainCache.FreeOlderVersions(jh, v), Is.EqualTo(closedBytes), "Free space takes the closed one only");
                Assert.That(File.Exists(Path.Combine(old, "t0_0.h16")), Is.True, "the open cache is whole");
            }
            Assert.That(TerrainCache.InUse(old), Is.False, "a lease lets go when disposed");
            Assert.That(TerrainCache.Prune(jh, v), Is.Empty, "the only older one is now the newest older one");
            Assert.That(TerrainCache.FreeOlderVersions(jh, v), Is.GreaterThan(0));
            Assert.That(TerrainCache.Versions(jh), Is.Empty);
        }

        [Test]
        public void HoldingADeletedAreaThrowsAndLeavesNoStub()
        {
            string jh = Package(JacksonHole);
            Assert.That(ResortLibrary.TryRemove(Entry(JacksonHole), out _), Is.True);
            Assert.Throws<DirectoryNotFoundException>(() => TerrainCache.Hold(jh), "another window deleted it");
            Assert.That(Directory.Exists(jh), Is.False, "no empty Resorts/<id> folder that would swallow a fresh download");
        }

        [Test]
        public void AFreshDownloadReplacesAStubFolderWithNoManifest()
        {
            string jh = Package(JacksonHole);
            string built = Path.Combine(_root, "incoming");
            Directory.CreateDirectory(built);
            File.Copy(Path.Combine(jh, ResortPackage.ManifestFile), Path.Combine(built, ResortPackage.ManifestFile));
            Directory.Delete(jh, true);
            Directory.CreateDirectory(Path.Combine(jh, "cache-v" + TerrainCache.Version));   // a stub, as an older build could leave
            string target = ResortLibrary.Add(_root, built);
            Assert.That(target, Is.EqualTo(jh));
            Assert.That(File.Exists(Path.Combine(jh, ResortPackage.ManifestFile)), Is.True, "the download landed");
            Assert.That(ResortLibrary.Scan(_root).Any(e => e.PackageId == JacksonHole), Is.True);
        }

        [Test]
        public void ADeleteCanLeaveTheSlowPartToAWorker()
        {
            string jh = Package(JacksonHole);
            FakeCache(jh, TerrainCache.Version, 4000);
            var entry = Entry(JacksonHole);
            ResortLibrary.Measure(new[] { entry });
            Assert.That(ResortLibrary.TryRemove(entry, out long freed, sweep: false), Is.True);
            Assert.That(freed, Is.EqualTo(entry.BytesOnDisk));
            Assert.That(Directory.Exists(jh), Is.False, "gone from the library at once");
            Assert.That(ResortLibrary.Scan(_root).Select(e => e.Name), Is.EqualTo(new[] { "Crystal Mountain" }));
            Assert.That(ResortLibrary.LeftoverBytes(_root), Is.EqualTo(freed), "its files wait in a .trash- folder");
            Assert.That(ResortLibrary.SweepTrash(ResortLibrary.ResortsFolder(_root)), Is.EqualTo(freed));
            Assert.That(ResortLibrary.LeftoverBytes(_root), Is.Zero);
        }

        [Test]
        public void ARenameWhileTheViewStateIsLockedLosesNothing()
        {
            string viewPath = Path.Combine(Package(JacksonHole), ViewState.FileName);
            string before = Hash(viewPath);
            var entry = Entry(JacksonHole);
            using (new FileStream(viewPath, FileMode.Open, FileAccess.Read, FileShare.None))   // another program has it
                Assert.That(ResortLibrary.Rename(entry, "Teton Village"), Is.False);
            Assert.That(entry.RenameRefusal, Does.Contain("can't be read just now"));
            Assert.That(Hash(viewPath), Is.EqualTo(before), "camera, bookmarks and layers kept");
        }

        [Test]
        public void ManyGamesCanHoldOneCacheAtOnce()
        {
            string jh = Package(JacksonHole);
            using (TerrainCache.Hold(jh))
            using (TerrainCache.Hold(jh))
                Assert.That(TerrainCache.InUse(TerrainCache.FolderFor(jh)), Is.True);
            Assert.That(TerrainCache.InUse(TerrainCache.FolderFor(jh)), Is.False);
        }

        [Test]
        public void FreeSpaceRemovesOnlyOlderCachesAndSaysHowMuch()
        {
            string jh = Package(JacksonHole), crystal = Package(CrystalFolder);
            int v = TerrainCache.Version;
            FakeCache(jh, v, 1000);
            FakeCache(jh, v - 1, 2000);
            FakeCache(crystal, v - 2, 3000);
            FakeCache(crystal, v + 1, 4000);
            var entries = ResortLibrary.Scan(_root, measure: true);
            long freeable = ResortLibrary.Freeable(entries);
            long packages = entries.Sum(e => e.Disk.Package);

            Assert.That(ResortLibrary.FreeSpace(_root), Is.EqualTo(freeable));
            Assert.That(TerrainCache.Versions(jh), Is.EqualTo(new[] { v }));
            Assert.That(TerrainCache.Versions(crystal), Is.EqualTo(new[] { v + 1 }), "a newer game's cache stays");
            var after = ResortLibrary.Scan(_root, measure: true);
            Assert.That(ResortLibrary.Freeable(after), Is.Zero);
            Assert.That(after.Sum(e => e.Disk.Package), Is.EqualTo(packages), "the areas themselves are untouched");
            Assert.That(ResortLibrary.FreeSpace(_root), Is.Zero, "nothing left to free");
        }
    }

    /// <summary>Task P2-04: real cache builds by two games on different cache versions, sharing one package.</summary>
    public sealed class SharedCacheVersionTests
    {
        [Test]
        public void TwoGamesOnDifferentCacheVersionsKeepEachOthersCaches()
        {
            string root = Path.Combine(Path.GetTempPath(), "mp-shared-cache-" + Guid.NewGuid().ToString("N"));
            try
            {
                string package = LibraryTests.BuildPackage(root, "Shared", 0);
                var m = ResortPackage.ReadManifest(package);
                int mine = TerrainCache.Version, theirs = TerrainCache.Version + 1;

                TerrainCache.Build(package, m, null, default, null, mine);
                string ourFolder = TerrainCache.FolderFor(package, mine);
                var ours = Directory.GetFiles(ourFolder).ToDictionary(Path.GetFileName, f => (File.ReadAllBytes(f), File.GetLastWriteTimeUtc(f)));
                TerrainCache.Build(package, m, null, default, null, theirs);   // the other branch's game opens the same area
                Assert.That(TerrainCache.IsCurrent(package, m), Is.True, "our cache survived their build: no rebuild for us");
                foreach (var kv in ours)
                {
                    string path = Path.Combine(ourFolder, kv.Key);
                    Assert.That(File.ReadAllBytes(path), Is.EqualTo(kv.Value.Item1), kv.Key + " untouched");
                    Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(kv.Value.Item2), kv.Key + " not rewritten");
                }
                string theirFolder = TerrainCache.FolderFor(package, theirs);
                var theirFiles = Directory.GetFiles(theirFolder).ToDictionary(Path.GetFileName, File.GetLastWriteTimeUtc);
                TerrainCache.Build(package, m, null, default, null, mine);     // ours rebuilding (say, after a crash) leaves theirs
                Assert.That(TerrainCache.Versions(package), Is.EqualTo(new[] { mine, theirs }));
                foreach (var kv in theirFiles)
                    Assert.That(File.GetLastWriteTimeUtc(Path.Combine(theirFolder, kv.Key)), Is.EqualTo(kv.Value), kv.Key + " of theirs untouched");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
