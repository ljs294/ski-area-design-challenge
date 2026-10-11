using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests.Core
{
    /// <summary>
    /// Task P2-07, S9: the credits list every source of every area in the library, the built-in demo included,
    /// generated from the packages' manifests (attribution lines and provenance).
    /// </summary>
    public sealed class CreditsTests
    {
        const string JacksonHole = "5792676e513f5302", CrystalMountain = "3d310fa515bffb49";

        string _scratch, _data, _bundled;

        [SetUp]
        public void SetUp()
        {
            _scratch = Path.Combine(Path.GetTempPath(), "mp-credits-" + Guid.NewGuid().ToString("N"));
            _data = Path.Combine(_scratch, "data");
            _bundled = Path.Combine(_scratch, "game", BundledAreas.FolderName);
            Directory.CreateDirectory(_data);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, true);
        }

        /// <summary>A v1 fixture package copied into <paramref name="root"/>/&lt;id&gt;.</summary>
        static string Copy(string id, string root)
        {
            string source = Path.Combine(TestData.Folder(Path.Combine("formats", "v1-library")), "Resorts", id);
            string target = Path.Combine(root, id);
            Directory.CreateDirectory(target);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)));
            return target;
        }

        /// <summary>The acceptance check: for each area, each attribution line and each used provider is credited, naming that area.</summary>
        static void AssertEverySourceCredited(List<LibraryEntry> entries, List<CreditSource> credits)
        {
            Assert.That(entries, Is.Not.Empty);
            foreach (var e in entries)
            {
                var m = ResortPackage.ReadManifest(e.Folder);
                foreach (string line in m.Attribution)
                {
                    var s = credits.SingleOrDefault(c => c.Line == line.Trim());
                    Assert.That(s, Is.Not.Null, $"{e.Name}: \"{line}\" isn't in the credits");
                    Assert.That(s.Areas.Any(a => a.StartsWith(e.Name, StringComparison.Ordinal)), $"\"{line}\" doesn't name {e.Name}");
                }
                foreach (var p in m.Provenance.Where(p => p.Layer != CreditsReader.UnusedLayer))
                {
                    var s = credits.FirstOrDefault(c => c.Providers.Contains(p.Provider));
                    Assert.That(s, Is.Not.Null, $"{e.Name}: provider {p.Provider} isn't in the credits");
                    Assert.That(s.Areas.Any(a => a.StartsWith(e.Name, StringComparison.Ordinal)), $"{p.Provider} doesn't name {e.Name}");
                    Assert.That(s.Products, Does.Contain(p.Product));
                }
            }
        }

        [Test]
        public void Every_source_of_every_area_in_the_library_is_credited()
        {
            Copy(JacksonHole, ResortLibrary.ResortsFolder(_data));
            Copy(CrystalMountain, ResortLibrary.ResortsFolder(_data));
            var entries = ResortLibrary.ScanWithBundled(_data, _bundled);
            var credits = CreditsReader.Read(entries);
            AssertEverySourceCredited(entries, credits);
            // One row per line, however many areas share it.
            Assert.That(credits.Select(c => c.Line), Is.Unique);
            var elevation = credits.Single(c => c.What == "Elevation");
            Assert.That(elevation.Areas, Is.EquivalentTo(new[] { "Crystal Mountain", "Jackson Hole" }));
            Assert.That(elevation.Providers, Is.EqualTo(new[] { "USGS 3DEP" }));
        }

        [Test]
        public void The_built_in_demo_is_credited_too()
        {
            Copy(CrystalMountain, ResortLibrary.ResortsFolder(_data));
            Copy(JacksonHole, _bundled);
            var entries = ResortLibrary.ScanWithBundled(_data, _bundled);
            Assert.That(entries.Any(e => e.Bundled), "the demo is in the list");
            AssertEverySourceCredited(entries, CreditsReader.Read(entries));
        }

        [Test]
        public void The_jackson_hole_test_package_is_credited()
        {
            string folder = TestData.Folder("jackson-hole-2km");
            var entries = new List<LibraryEntry> { new LibraryEntry { Name = "Jackson Hole", Folder = folder } };
            AssertEverySourceCredited(entries, CreditsReader.Read(entries));
        }

        [Test]
        public void Only_areas_that_use_a_source_are_named()
        {
            // The v1 Jackson Hole fixture records no OpenStreetMap provenance, but its line credits OSM; Crystal Mountain has both.
            Copy(JacksonHole, ResortLibrary.ResortsFolder(_data));
            Copy(CrystalMountain, ResortLibrary.ResortsFolder(_data));
            var credits = CreditsReader.Read(ResortLibrary.ScanWithBundled(_data, _bundled));
            var osm = credits.Single(c => c.Who.Contains("OpenStreetMap"));
            Assert.That(osm.Licence, Is.EqualTo("ODbL"));
            Assert.That(osm.Products, Has.Count.EqualTo(1), "only Crystal Mountain's provenance names a product");
        }

        [Test]
        public void An_empty_library_has_no_sources() =>
            Assert.That(CreditsReader.Read(ResortLibrary.ScanWithBundled(_data, _bundled)), Is.Empty);

        [Test]
        public void A_provider_no_line_names_gets_its_own_row_and_an_unused_one_none()
        {
            var m = new PackageManifest();
            m.Attribution.Add("Elevation: U.S. Geological Survey, 3D Elevation Program (public domain).");
            m.Provenance.Add(new ProvenanceInfo { Layer = "heights-core", Provider = "USGS 3DEP", Product = "S1M" });
            m.Provenance.Add(new ProvenanceInfo { Layer = "snow", Provider = "NOAA", Product = "SNODAS" });
            m.Provenance.Add(new ProvenanceInfo { Layer = CreditsReader.UnusedLayer, Provider = "OpenStreetMap contributors", Product = "Unavailable" });
            var credits = CreditsReader.Build(new[] { ("Alta", m) });
            Assert.That(credits, Has.Count.EqualTo(2));
            Assert.That(credits[1].Who, Is.EqualTo("NOAA"));
            Assert.That(credits[1].Products, Is.EqualTo(new[] { "SNODAS" }));
            Assert.That(credits[1].Areas, Is.EqualTo(new[] { "Alta" }));
            Assert.That(credits.Any(c => c.Providers.Contains("OpenStreetMap contributors")), Is.False);
        }

        [Test]
        public void Areas_with_one_name_are_told_apart()
        {
            var entries = new List<LibraryEntry>();
            void Area(string name, string id, int size)
            {
                var m = new PackageManifest { PackageId = id };
                m.Site.SizeMetres = size;
                m.Attribution.Add("Elevation: USGS (public domain).");
                string folder = Path.Combine(_scratch, id);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, ResortPackage.ManifestFile), Newtonsoft.Json.JsonConvert.SerializeObject(m));
                entries.Add(new LibraryEntry { Name = name, Folder = folder });
            }
            Area("Jackson Hole", "a", 2000);
            Area("Jackson Hole", "b", 5000);
            Area("Jackson Hole", "c", 5000);
            Area("Jackson Hole", "c", 5000);   // the same package twice is one area
            Area("Alta", "d", 2000);
            var credits = CreditsReader.Read(entries);
            Assert.That(credits.Single().Areas, Is.EqualTo(new[] { "Jackson Hole · 2 km", "Jackson Hole · 5 km", "Jackson Hole · 5 km (2)", "Alta" }));
        }

        [Test]
        public void A_package_that_cant_be_read_is_reported_and_skipped()
        {
            var failed = new List<string>();
            var entries = new List<LibraryEntry> { new LibraryEntry { Name = "Gone", Folder = Path.Combine(_scratch, "missing") } };
            Assert.That(CreditsReader.Read(entries, (e, _) => failed.Add(e.Name)), Is.Empty);
            Assert.That(failed, Is.EqualTo(new[] { "Gone" }));
        }

        [TestCase("Elevation: U.S. Geological Survey, 3D Elevation Program (public domain).", "Elevation", "U.S. Geological Survey, 3D Elevation Program", "Public domain")]
        [TestCase("Water and roads: © OpenStreetMap contributors (ODbL).", "Water and roads", "© OpenStreetMap contributors", "ODbL")]
        [TestCase("Land cover: © ESA WorldCover project 2021, contains modified Copernicus Sentinel data (2021) processed by the ESA WorldCover consortium (CC BY 4.0).",
                  "Land cover", "© ESA WorldCover project 2021, contains modified Copernicus Sentinel data (2021) processed by the ESA WorldCover consortium", "CC BY 4.0")]
        [TestCase("Some data, no licence", "", "Some data, no licence", "")]
        [TestCase("© Someone: a long credit with a colon in it", "", "© Someone: a long credit with a colon in it", "")]
        public void Lines_split_into_what_who_and_licence(string line, string what, string who, string licence) =>
            Assert.That(CreditsReader.Split(line), Is.EqualTo((what, who, licence)));
    }
}
