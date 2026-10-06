using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using MountainPlanner.Persistence;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task 08 (T11): the frozen formats. The Phase 1 files in TestData/formats/v1-library open unchanged; newer
    /// files are refused with a clear message and never overwritten; older ones migrate a step at a time.
    /// </summary>
    public sealed class FormatFreezeTests
    {
        const string JacksonHole = "5792676e513f5302", CrystalId = "5689fc951ec180f0", CrystalFolder = "3d310fa515bffb49";
        const string Sugarloaf = "sugarloaf-21248405";

        string _root;

        [SetUp]
        public void SetUp()
        {
            string source = TestData.Folder(Path.Combine("formats", "v1-library"));
            _root = Path.Combine(Path.GetTempPath(), "mp-formats-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>Every file under the data folder with its SHA-256, to prove a read changed nothing.</summary>
        Dictionary<string, string> Snapshot()
        {
            using (var sha = SHA256.Create())
                return Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                                .ToDictionary(f => f.Substring(_root.Length + 1), f => Convert.ToBase64String(sha.ComputeHash(File.ReadAllBytes(f))));
        }

        static void SetVersion(string path, string field, int version)
        {
            var o = JObject.Parse(File.ReadAllText(path));
            o[field] = version;
            File.WriteAllText(path, o.ToString(Formatting.Indented));
        }

        [Test]
        public void ThePhaseOneLibraryOpensUnchanged()
        {
            var before = Snapshot();

            Assert.That(LibraryIndex.VersionOf(_root), Is.EqualTo(1), "no library.json: a Phase 1 data folder is layout v1");
            Assert.That(LibraryIndex.Refusal(_root), Is.Null);
            var newer = new List<LibraryEntry>();
            var entries = ResortLibrary.Scan(_root, newer);
            Assert.That(newer, Is.Empty);
            Assert.That(entries.Select(e => e.Name), Is.EqualTo(new[] { "Crystal Mountain", "Jackson Hole" }));
            var crystal = entries[0];
            Assert.That(crystal.PackageId, Is.EqualTo(CrystalId));
            Assert.That(Path.GetFileName(crystal.Folder), Is.EqualTo(CrystalFolder), "the folder name needn't match the id");
            Assert.That(crystal.SizeKm, Is.EqualTo(5));
            Assert.That(crystal.TerrainScore, Is.EqualTo(48), "3DEP fallbacks: 60% 10 m, 24% 3 m, 16% 1 m");
            Assert.That(crystal.CreatedUtc, Is.EqualTo("2026-09-30T16:21:21Z"), "date strings stay exactly as written");
            Assert.That(crystal.Refusal, Is.Empty);
            Assert.That(entries[1].PackageId, Is.EqualTo(JacksonHole));
            Assert.That(entries[1].Latitude, Is.EqualTo(43.593002));

            var recent = RecentResorts.Load(_root);
            Assert.That(recent.Refusal, Is.Empty);
            Assert.That(recent.Opened, Has.Count.EqualTo(2));
            Assert.That(recent.Opened[JacksonHole], Is.EqualTo("2026-10-03T18:22:05Z"));
            Assert.That(recent.Latest(entries.Select(e => e.PackageId)), Is.EqualTo(JacksonHole), "Continue opens Jackson Hole");

            var paused = PendingDownloads.List(_root).Single();
            Assert.That(paused.Id, Is.EqualTo(Sugarloaf));
            Assert.That(paused.Name, Is.EqualTo("Sugarloaf"));
            Assert.That(paused.SizeKm, Is.EqualTo(3));
            Assert.That(paused.StartedUtc, Is.EqualTo("2026-10-03T21:04:11Z"));
            Assert.That(paused.LastOverall, Is.EqualTo(0.384));
            Assert.That(paused.LastStage, Is.EqualTo("Forest"));
            Assert.That(PendingDownloads.IdFor(paused.Name, paused.Latitude, paused.Longitude, paused.SizeKm), Is.EqualTo(Sugarloaf), "resuming finds the same folder");

            var view = ViewState.Load(Package(JacksonHole));
            Assert.That(view.Refusal, Is.Empty);
            Assert.That(view.Version, Is.EqualTo(1));
            Assert.That(view.Camera.TargetX, Is.EqualTo(120.5));
            Assert.That(view.Camera.TargetZ, Is.EqualTo(-340.25));
            Assert.That(view.Camera.Distance, Is.EqualTo(2800));
            Assert.That(view.Camera.Pitch, Is.EqualTo(32.5));
            Assert.That(view.Bookmarks.Single().Name, Is.EqualTo("Corbet's Couloir"));
            Assert.That(view.Bookmarks[0].Camera.Distance, Is.EqualTo(600));
            Assert.That(view.SecondOfDay, Is.EqualTo(16 * 3600));
            Assert.That(view.Layers["forest"], Is.False);
            Assert.That(view.Layers["snow"], Is.True);
            Assert.That(ViewState.Load(Package(CrystalFolder)).Camera.Distance, Is.EqualTo(3500), "no view.json: defaults");

            Assert.That(Snapshot(), Is.EqualTo(before), "opening a Phase 1 library writes nothing");
        }

        [TestCase(JacksonHole)]
        [TestCase(CrystalFolder)]
        public void PhaseOneManifestsReadExactlyAsBeforeTheFreeze(string folder)
        {
            string path = Path.Combine(Package(folder), ResortPackage.ManifestFile);
            var settings = new JsonSerializerSettings { Culture = CultureInfo.InvariantCulture, FloatFormatHandling = FloatFormatHandling.Symbol };
            // The Phase 1 reader, verbatim: a straight deserialise.
            var direct = JsonConvert.DeserializeObject<PackageManifest>(File.ReadAllText(path), settings);
            var read = ResortPackage.ReadManifest(Package(folder));

            Assert.That(JsonConvert.SerializeObject(read, settings), Is.EqualTo(JsonConvert.SerializeObject(direct, settings)), "every field reads as it did");
            Assert.That(read.FormatVersion, Is.EqualTo(1));
            Assert.That(ResortPackage.ComputeId(read), Is.EqualTo(read.PackageId), "package ids don't move");
            Assert.That(read.Attribution, Is.Not.Empty);
            Assert.That(read.Layers.Select(l => l.Id), Does.Contain("heights-core"));
        }

        [Test]
        public void ANewerPackageIsRefusedAndListedButNeverRead()
        {
            string jh = Package(JacksonHole);
            string manifest = Path.Combine(jh, ResortPackage.ManifestFile);
            SetVersion(manifest, nameof(PackageManifest.FormatVersion), 2);
            byte[] bytes = File.ReadAllBytes(manifest);

            var e = Assert.Throws<FormatTooNewException>(() => ResortPackage.ReadManifest(jh));
            Assert.That(e.Message, Is.EqualTo("This area package was saved by a newer version of Mountain Planner (format 2; this version reads up to 1). Update the game to open it."));
            Assert.That(e, Is.InstanceOf<IOException>(), "callers that skip unreadable packages skip this too");
            Assert.That(e.Found, Is.EqualTo(2));
            Assert.That(e.Supported, Is.EqualTo(PackageManifest.CurrentFormat));

            var newer = new List<LibraryEntry>();
            var entries = ResortLibrary.Scan(_root, newer);
            Assert.That(entries.Select(x => x.Name), Is.EqualTo(new[] { "Crystal Mountain" }), "it can't be opened");
            var row = newer.Single();
            Assert.That(row.Name, Is.EqualTo("Jackson Hole"), "named from where v1 kept the name");
            Assert.That(row.Folder, Is.EqualTo(jh));
            Assert.That(row.Refusal, Is.EqualTo(e.Message));
            Assert.That(row.BytesOnDisk, Is.GreaterThan(0), "its size shows, so deleting it says what it frees");
            Assert.That(PackageValidator.Validate(jh).Single(), Does.Contain("newer version of Mountain Planner"));
            Assert.That(File.ReadAllBytes(manifest), Is.EqualTo(bytes));

            // A newer manifest with no name where v1 kept it falls back to the folder name.
            var o = JObject.Parse(File.ReadAllText(manifest));
            o.Remove("Site");
            File.WriteAllText(manifest, o.ToString());
            newer.Clear();
            ResortLibrary.Scan(_root, newer);
            Assert.That(newer.Single().Name, Is.EqualTo(JacksonHole));
        }

        [Test]
        public void NewerRecordsAreRefusedAndNeverOverwritten()
        {
            string recentPath = Path.Combine(_root, RecentResorts.FileName);
            string viewPath = Path.Combine(Package(JacksonHole), ViewState.FileName);
            string downloadPath = Path.Combine(PendingDownloads.Folder(_root), Sugarloaf, PendingDownloads.RecordFile);
            SetVersion(recentPath, "Version", 7);
            SetVersion(viewPath, "Version", 7);
            SetVersion(downloadPath, "Version", 7);
            var before = Snapshot();

            var recent = RecentResorts.Load(_root);
            Assert.That(recent.Opened, Is.Empty, "never misread");
            Assert.That(recent.Refusal, Does.Contain("recently opened list was saved by a newer version").And.Contain("format 7"));
            Assert.That(RecentResorts.Touch(_root, CrystalId, "2026-10-06T10:00:00Z"), Is.False);

            var view = ViewState.Load(Package(JacksonHole));
            Assert.That(view.Camera.Distance, Is.EqualTo(3500), "defaults, not the newer file's values");
            Assert.That(view.Refusal, Does.Contain("view state was saved by a newer version"));
            Assert.That(ViewState.Save(Package(JacksonHole), new ViewState()), Is.False);

            Assert.That(PendingDownloads.List(_root), Is.Empty, "a newer game's paused download is left for that game");
            var again = new PendingDownload { Id = Sugarloaf, Name = "Sugarloaf", StartedUtc = "2026-10-06T10:00:00Z" };
            Assert.That(PendingDownloads.Save(_root, again), Is.False);

            Assert.That(Snapshot(), Is.EqualTo(before), "nothing written over a newer file");

            // The same calls still write when the file is ours.
            Assert.That(ViewState.Save(Package(CrystalFolder), new ViewState()), Is.True);
            Assert.That(PendingDownloads.Save(_root, new PendingDownload { Id = "stowe-1", Name = "Stowe" }), Is.True);
        }

        [Test]
        public void ANewerLibraryLayoutListsNothingAndAcceptsNothing()
        {
            File.WriteAllText(Path.Combine(_root, LibraryIndex.FileName), "{ \"Version\": 2 }\n");
            Assert.That(LibraryIndex.VersionOf(_root), Is.EqualTo(2));
            Assert.That(LibraryIndex.Refusal(_root), Is.EqualTo("This library was saved by a newer version of Mountain Planner (format 2; this version reads up to 1). Update the game to open it."));
            var newer = new List<LibraryEntry>();
            Assert.That(ResortLibrary.Scan(_root, newer), Is.Empty, "a layout this game doesn't know isn't guessed at");
            Assert.That(newer, Is.Empty);
            Assert.That(PendingDownloads.List(_root), Is.Empty);
            var before = Snapshot();
            Assert.That(RecentResorts.Touch(_root, JacksonHole, "2026-10-06T10:00:00Z"), Is.False);
            Assert.That(PendingDownloads.Save(_root, new PendingDownload { Id = "stowe-1", Name = "Stowe" }), Is.False);
            Assert.That(Snapshot(), Is.EqualTo(before), "nothing written into a newer game's library");

            string built = Path.Combine(_root, "incoming");
            Directory.CreateDirectory(built);
            File.Copy(Path.Combine(Package(JacksonHole), ResortPackage.ManifestFile), Path.Combine(built, ResortPackage.ManifestFile));
            Assert.That(() => ResortLibrary.Add(_root, built), Throws.InstanceOf<FormatTooNewException>().With.Message.Contain("newer version"));
            Assert.That(Directory.Exists(built), Is.True, "the new package isn't moved into a library this game can't use");
        }

        [Test]
        public void AddingAPackageMarksTheLibraryAsLayoutOne()
        {
            string root = Path.Combine(_root, "fresh");
            string built = Path.Combine(_root, "incoming");
            Directory.CreateDirectory(built);
            File.Copy(Path.Combine(Package(JacksonHole), ResortPackage.ManifestFile), Path.Combine(built, ResortPackage.ManifestFile));

            string target = ResortLibrary.Add(root, built);
            Assert.That(Path.GetFileName(target), Is.EqualTo(JacksonHole));
            string marker = Path.Combine(root, LibraryIndex.FileName);
            Assert.That(JObject.Parse(File.ReadAllText(marker))["Version"].Value<int>(), Is.EqualTo(LibraryIndex.CurrentVersion));
            Assert.That(LibraryIndex.VersionOf(root), Is.EqualTo(1));
            Assert.That(ResortLibrary.Scan(root).Single().PackageId, Is.EqualTo(JacksonHole));
        }

        [Test]
        public void EveryFormatIsAtVersionOne()
        {
            // Phase 1's files are v1 of each format. Bumping one means a migration step and a v2 fixture (0.3 §5).
            Assert.That(PackageManifest.Migrations.Current, Is.EqualTo(1));
            Assert.That(LibraryIndex.Migrations.Current, Is.EqualTo(1));
            Assert.That(RecentResorts.Migrations.Current, Is.EqualTo(1));
            Assert.That(ViewState.Migrations.Current, Is.EqualTo(1));
            Assert.That(PendingDownload.Migrations.Current, Is.EqualTo(1));
            Assert.That(ResortPackage.IdScheme, Is.EqualTo(1));
        }
    }

    /// <summary>Task 08: the migration mechanics every frozen format shares.</summary>
    public sealed class VersionedJsonTests
    {
        sealed class Shape
        {
            public int Version { get; set; }
            public int B { get; set; }
            public int C { get; set; }
            public string When { get; set; } = "";
        }

        // v1 {a} → v2 renames a to b → v3 adds c = 2b.
        static readonly VersionedJson Three = new VersionedJson("test file", "Version", 3,
            o => { o["b"] = o["a"]; o.Remove("a"); },
            o => o["c"] = o["b"].Value<int>() * 2);

        static readonly JsonSerializerSettings Settings = new JsonSerializerSettings();

        [Test]
        public void OldFilesMigrateAStepAtATime()
        {
            var fromOne = Three.Read<Shape>("{ \"Version\": 1, \"a\": 5 }", Settings, out int was);
            Assert.That(was, Is.EqualTo(1));
            Assert.That((fromOne.Version, fromOne.B, fromOne.C), Is.EqualTo((3, 5, 10)));

            var fromTwo = Three.Read<Shape>("{ \"Version\": 2, \"b\": 4 }", Settings, out was);
            Assert.That(was, Is.EqualTo(2));
            Assert.That((fromTwo.Version, fromTwo.B, fromTwo.C), Is.EqualTo((3, 4, 8)), "only the steps it needs");

            var current = Three.Read<Shape>("{ \"Version\": 3, \"b\": 1, \"c\": 9 }", Settings);
            Assert.That((current.B, current.C), Is.EqualTo((1, 9)), "a current file is read as it is");
        }

        [Test]
        public void AMissingVersionIsTheFirstVersionNeverTheCurrentOne()
        {
            var o = Three.Read<Shape>("{ \"a\": 3 }", Settings, out int was);
            Assert.That(was, Is.EqualTo(VersionedJson.FirstVersion));
            Assert.That((o.Version, o.B, o.C), Is.EqualTo((3, 3, 6)));
        }

        [Test]
        public void NewerAndNonsenseVersionsAreRefused()
        {
            var e = Assert.Throws<FormatTooNewException>(() => Three.Read<Shape>("{ \"Version\": 4 }", Settings));
            Assert.That(e.Message, Is.EqualTo("This test file was saved by a newer version of Mountain Planner (format 4; this version reads up to 3). Update the game to open it."));
            Assert.Throws<InvalidDataException>(() => Three.Read<Shape>("{ \"Version\": \"2\" }", Settings), "a version is a whole number");
            Assert.Throws<InvalidDataException>(() => Three.Read<Shape>("{ \"Version\": 0 }", Settings));
            Assert.Throws<InvalidDataException>(() => Three.Read<Shape>("{ \"Version\": 1.5 }", Settings));
            Assert.Throws<InvalidDataException>(() => Three.Read<Shape>("[1, 2]", Settings), "not an object");
            Assert.Throws<JsonReaderException>(() => Three.Read<Shape>("{ \"Version\": 3 } trailing", Settings));
        }

        [Test]
        public void EachVersionAfterTheFirstNeedsExactlyOneStep()
        {
            Assert.Throws<ArgumentException>(() => new VersionedJson("x", "Version", 2));
            Assert.Throws<ArgumentException>(() => new VersionedJson("x", "Version", 1, o => { }));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VersionedJson("x", "Version", 0));
        }

        [Test]
        public void DateLikeStringsStayExactlyAsWritten()
        {
            // Newtonsoft's default would parse this into a DateTime and write it back in another form.
            var o = Three.Read<Shape>("{ \"Version\": 3, \"When\": \"2026-10-03T18:22:05Z\" }", Settings);
            Assert.That(o.When, Is.EqualTo("2026-10-03T18:22:05Z"));
        }
    }
}
