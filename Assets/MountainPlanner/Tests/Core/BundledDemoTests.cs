using System;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task P2-03: the demo built into the game (StreamingAssets/Demo). It's listed beside the library, read in place and
    /// never written: no rename, delete or Free space. A library copy of the same package wins.
    /// </summary>
    public sealed class BundledDemoTests
    {
        const string JacksonHole = "5792676e513f5302";

        string _scratch, _data, _bundled;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "mp-bundled-" + Guid.NewGuid().ToString("N"));
            _data = Path.Combine(_scratch, "data");
            _bundled = Path.Combine(_scratch, "game", "StreamingAssets", BundledAreas.FolderName);
            Directory.CreateDirectory(_data);
            CopyPackage(_bundled);
        }

        [TearDown]
        public void TearDown()
        {
            if (!Directory.Exists(_scratch)) return;
            foreach (string f in Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_scratch, true);
        }

        /// <summary>The v1 fixture's Jackson Hole package, copied into <paramref name="root"/>/&lt;id&gt;.</summary>
        string CopyPackage(string root)
        {
            string source = Path.Combine(TestData.Folder(Path.Combine("formats", "v1-library")), "Resorts", JacksonHole);
            string target = Path.Combine(root, JacksonHole);
            Directory.CreateDirectory(target);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            return target;
        }

        /// <summary>A cache this game counts as current (no tiles), as the build step leaves one.</summary>
        static void CurrentCache(string package)
        {
            string folder = TerrainCache.FolderFor(package);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, TerrainCache.ManifestFile),
                "{ \"CacheVersion\": " + TerrainCache.Version + ", \"PackageId\": \"" + JacksonHole + "\", \"Tiles\": [] }");
        }

        /// <summary>Every file under a folder with its size and write time: proof that nothing was written.</summary>
        static string Snapshot(string folder) =>
            string.Join("\n", Directory.GetFileSystemEntries(folder, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
                                       .Select(p => p + "|" + (File.Exists(p) ? new FileInfo(p).Length + "|" + File.GetLastWriteTimeUtc(p).Ticks : "dir")));

        [Test]
        public void AnEmptyLibraryStillListsTheBuiltInDemo()
        {
            CurrentCache(Path.Combine(_bundled, JacksonHole));
            var entries = ResortLibrary.ScanWithBundled(_data, _bundled);
            Assert.That(entries, Has.Count.EqualTo(1));
            var demo = entries[0];
            Assert.That(demo.Bundled, Is.True);
            Assert.That(demo.OriginalName, Is.EqualTo("Jackson Hole"));
            Assert.That(demo.Refusal, Is.Empty, "it opens");
            Assert.That(demo.CacheReady, Is.True);
            Assert.That(demo.RenameRefusal, Is.EqualTo(BundledAreas.Refusal));
            Assert.That(ResortLibrary.Scan(_data), Is.Empty, "the library itself stays empty");
        }

        [Test]
        public void TheBuiltInDemoIsNeverWritten()
        {
            string package = Path.Combine(_bundled, JacksonHole);
            CurrentCache(package);
            foreach (string f in Directory.GetFiles(package, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.ReadOnly);
            string before = Snapshot(_bundled);
            var demo = ResortLibrary.ScanWithBundled(_data, _bundled).Single();

            Assert.That(ResortLibrary.Rename(demo, "Teton Village"), Is.False);
            Assert.That(demo.Name, Is.EqualTo("Jackson Hole"));
            Assert.That(ResortLibrary.TryRemove(demo, out long freed), Is.False);
            Assert.That(freed, Is.Zero);
            Assert.That(ResortLibrary.FreeSpace(_data), Is.Zero);
            ResortLibrary.SetDisk(demo, new DiskUse { OlderCaches = 1000 });
            Assert.That(ResortLibrary.Freeable(new[] { demo }), Is.Zero, "Free space never counts it");
            Assert.That(Snapshot(_bundled), Is.EqualTo(before), "no file in the game's folder was added, changed or removed");
        }

        [Test]
        public void ALibraryCopyOfTheSamePackageWins()
        {
            CurrentCache(Path.Combine(_bundled, JacksonHole));
            string own = CopyPackage(ResortLibrary.ResortsFolder(_data));
            var entries = ResortLibrary.ScanWithBundled(_data, _bundled);
            Assert.That(entries, Has.Count.EqualTo(1), "listed once");
            Assert.That(entries[0].Bundled, Is.False);
            Assert.That(entries[0].Folder, Is.EqualTo(own), "the copy the player can rename and delete");
        }

        [Test]
        public void ABuiltInDemoWithoutACurrentCacheIsRefused()
        {
            var demo = ResortLibrary.ScanWithBundled(_data, _bundled).Single();
            Assert.That(demo.CacheReady, Is.False);
            Assert.That(demo.Refusal, Is.EqualTo(BundledAreas.StaleCache), "it can't be built in a read-only folder");
        }

        [Test]
        public void NoBuiltInFolderMeansNoBuiltInAreas()
        {
            Assert.That(BundledAreas.Scan(null), Is.Empty);
            Assert.That(BundledAreas.Scan(Path.Combine(_scratch, "missing")), Is.Empty);
            Assert.That(ResortLibrary.ScanWithBundled(_data, null), Is.Empty);
        }

        [Test]
        public void ContainsKnowsTheGamesOwnFolders()
        {
            string package = Path.Combine(_bundled, JacksonHole);
            Assert.That(BundledAreas.Contains(_bundled, package), Is.True);
            Assert.That(BundledAreas.Contains(_bundled, package + Path.DirectorySeparatorChar), Is.True);
            Assert.That(BundledAreas.Contains(_bundled, _bundled), Is.False, "the root itself isn't a package");
            Assert.That(BundledAreas.Contains(_bundled, _bundled + "-other"), Is.False, "a sibling with a longer name");
            Assert.That(BundledAreas.Contains(_bundled, Path.Combine(_data, "Resorts", JacksonHole)), Is.False);
            Assert.That(BundledAreas.Contains(null, package), Is.False);
        }
    }
}
