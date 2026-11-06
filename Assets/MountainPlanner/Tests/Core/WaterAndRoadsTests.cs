using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Saves;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Get water and roads (task P3-16, BB6): OpenStreetMap layers added to an area downloaded without them, and the area
    // moved to its new id with its saves and recent entry, whole at every point a crash could stop it.
    public sealed class WaterAndRoadsTests
    {
        // A pond and two roads near the Jackson Hole test site's centre (43.593, -110.848).
        const string Overpass = @"{""elements"":[
 {""type"":""way"",""tags"":{""natural"":""water""},""geometry"":[{""lat"":43.5925,""lon"":-110.8490},{""lat"":43.5925,""lon"":-110.8480},{""lat"":43.5932,""lon"":-110.8480},{""lat"":43.5932,""lon"":-110.8490},{""lat"":43.5925,""lon"":-110.8490}]},
 {""type"":""way"",""tags"":{""highway"":""secondary"",""name"":""Moose-Wilson Road""},""geometry"":[{""lat"":43.590,""lon"":-110.852},{""lat"":43.596,""lon"":-110.844}]},
 {""type"":""way"",""tags"":{""highway"":""track""},""geometry"":[{""lat"":43.594,""lon"":-110.850},{""lat"":43.595,""lon"":-110.846}]}
]}";

        const string Opened = "2026-11-01T10:00:00Z";
        const string Created = "2026-10-20T08:00:00Z";
        const string FixtureArea = "5792676e513f5302";   // the v7 save fixture's package id

        sealed class Crash : Exception { }

        string _root = "", _cache = "";

        [SetUp]
        public void SetUp()
        {
            string id = Guid.NewGuid().ToString("N");
            _root = Path.Combine(Path.GetTempPath(), "mp-water-roads-" + id);
            _cache = Path.Combine(Path.GetTempPath(), "mp-water-roads-cache-" + id);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (string f in new[] { _root, _cache })
                if (Directory.Exists(f)) Directory.Delete(f, true);
        }

        static SiteSquare Site => SiteSquare.Create(new AlbersPoint(-1188828, 2381976), 2);

        /// <summary>A package in the library like one downloaded while OpenStreetMap was down; <paramref name="roads"/> gives it roads only.</summary>
        string Package(bool roads = false, string name = "Jackson Hole")
        {
            string resorts = ResortLibrary.ResortsFolder(_root);
            string folder = Path.Combine(resorts, "_incoming-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var site = Site;
            var m = new PackageManifest
            {
                Site = new SiteInfo { Name = name, CentreX = site.Centre.X, CentreY = site.Centre.Y, SizeMetres = site.SizeMetres, RingMetres = (int)SiteSquare.RingMetres },
                Quality = new QualityInfo { Score = 100 }, Flora = new FloraQualityInfo { Score = 83 }, CreatedUtc = Created, Tool = AcquisitionPipeline.ToolName,
            };
            var values = Enumerable.Repeat(2000f, 64 * 64).ToArray();
            ResortPackage.AddLayer(folder, m, AcquisitionPipeline.CoreLayer, new GridHeader(GridValueType.Float32, 64, 64, 0, 64, 1), values);
            ResortPackage.AddLayer(folder, m, AcquisitionPipeline.RingLayer, new GridHeader(GridValueType.Float32, 64, 64, 0, 64, 2), values);
            OsmLayers.Add(m, folder, site, new OsmLayers.Result { Failure = "Overpass was down" });
            if (roads) RoadsFile.Add(folder, m, new[] { new Road { Class = "track", Points = { new AlbersPoint(site.Centre.X, site.Centre.Y), new AlbersPoint(site.Centre.X + 50, site.Centre.Y) } } });
            ResortPackage.WriteManifest(folder, m);
            return ResortLibrary.Add(_root, folder);
        }

        /// <summary>The v7 save fixture as one of the area's saves (with its backup), and the area opened once.</summary>
        void SavesAndRecent(string packageId)
        {
            string fixture = Path.Combine(TestData.Folder(Path.Combine("formats", "v7-save")), "Saves", FixtureArea, "save-001");
            string save = Path.Combine(SaveStore.AreaFolder(_root, packageId), "save-001");
            Directory.CreateDirectory(Path.Combine(save, SaveStore.TilesFolder));
            foreach (string tile in Directory.GetFiles(Path.Combine(fixture, SaveStore.TilesFolder)))
                File.Copy(tile, Path.Combine(save, SaveStore.TilesFolder, Path.GetFileName(tile)));
            string json = File.ReadAllText(Path.Combine(fixture, SaveFile.FileName)).Replace(FixtureArea, packageId);
            File.WriteAllText(Path.Combine(save, SaveFile.FileName), json);
            File.WriteAllText(Path.Combine(save, SaveFile.BackupName), json);
            Assert.That(RecentResorts.Touch(_root, packageId, Opened), Is.True);
        }

        OsmFeatures Osm(Func<int> onFetch = null) => new OsmFeatures(new DiskCache(_cache), new TransferMeter(), (url, ct) =>
        {
            onFetch?.Invoke();
            return Task.FromResult(Encoding.UTF8.GetBytes(Overpass));
        });

        string Backfill(string folder, Action<RekeyStep> step = null, OsmFeatures osm = null) =>
            OsmBackfill.RunAsync(_root, folder, osm ?? Osm(), new TransferMeter(), null, CancellationToken.None, step).GetAwaiter().GetResult();

        /// <summary>The library as the player sees it: one row, one id, its saves listed and opening, Continue finding it.</summary>
        void AssertWhole(string folder, string expectedId, string context)
        {
            var rows = ResortLibrary.Scan(_root);
            Assert.That(rows, Has.Count.EqualTo(1), context + ": one library row");
            Assert.That(rows[0].PackageId, Is.EqualTo(expectedId), context + ": its id");
            Assert.That(PackageValidator.Validate(folder), Is.Empty, context + ": the package is sound");
            var saves = SaveStore.List(_root, expectedId);
            Assert.That(saves, Has.Count.EqualTo(1), context + ": its save is listed under its id");
            var open = OpenDesign.Load(_root, saves[0].Folder, expectedId, "");
            Assert.That(open.MigratedFrom, Is.EqualTo(7), context + ": the save keeps its version, so it still migrates and regrades");
            Assert.That(open.NeedsRegrade, Is.True, context + ": a v7 save is still graded again as it opens");
            Assert.That(RecentResorts.Load(_root).Latest(rows.Select(r => r.PackageId)), Is.EqualTo(expectedId), context + ": Continue finds it");
        }

        [Test]
        public void GettingWaterAndRoadsMovesTheAreaAndItsSavesToTheNewId()
        {
            string folder = Package();
            var before = ResortPackage.ReadManifest(folder);
            Assert.That(WaterAndRoads.Needed(before), Is.True);
            Assert.That(ResortLibrary.Scan(_root)[0].NeedsWaterAndRoads, Is.True, "Manage Areas offers it");
            SavesAndRecent(before.PackageId);

            string id = Backfill(folder);
            var after = ResortPackage.ReadManifest(folder);
            Assert.That(id, Is.Not.EqualTo(before.PackageId));
            Assert.That(after.PackageId, Is.EqualTo(id));
            Assert.That(WaterAndRoads.LayerIds.All(l => after.Layers.Any(x => x.Id == l)), "water, developed land and roads are in it");
            Assert.That(ResortLibrary.Scan(_root)[0].NeedsWaterAndRoads, Is.False);
            Assert.That(after.Provenance.Where(p => p.Provider == WaterAndRoads.Provider).Select(p => p.Layer), Has.None.EqualTo(WaterAndRoads.NoneLayer), "no longer 'unavailable'");
            Assert.That(after.Attribution.Count(a => a == OsmLayers.Attribution), Is.EqualTo(1));
            Assert.That(after.CreatedUtc, Is.EqualTo(Created), "the demo's newest-Jackson-Hole pick doesn't change");
            Assert.That(after.Tool, Is.EqualTo(before.Tool));
            foreach (var l in before.Layers) Assert.That(after.Layers.Single(x => x.Id == l.Id).Sha256, Is.EqualTo(l.Sha256), $"{l.Id} is untouched");
            AssertWhole(folder, id, "afterwards");
            Assert.That(Directory.Exists(SaveStore.AreaFolder(_root, before.PackageId)), Is.False, "the old id's saves are cleaned up");
            Assert.That(RecentResorts.Load(_root).Opened.Keys, Is.EquivalentTo(new[] { id }), "one recent entry, opened when the old one was");
            Assert.That(RecentResorts.Load(_root).Opened[id], Is.EqualTo(Opened));
            Assert.That(File.ReadAllText(Path.Combine(SaveStore.AreaFolder(_root, id), "save-001", SaveFile.BackupName)), Does.Contain("\"PackageId\": \"" + id + "\""), "the backup is moved too");

            Assert.That(Backfill(folder), Is.EqualTo(id), "a second time there's nothing to do");
        }

        [Test]
        public void ACrashAtAnyPointLeavesOneWholeArea()
        {
            string expected;
            {
                string folder = Package();
                expected = Backfill(folder);
                TearDown();
            }
            foreach (RekeyStep at in Enum.GetValues(typeof(RekeyStep)))
            {
                string folder = Package();
                string oldId = ResortPackage.ReadManifest(folder).PackageId;
                SavesAndRecent(oldId);
                Assert.That(() => Backfill(folder, s => { if (s == at) throw new Crash(); }), Throws.InstanceOf<Crash>());
                bool committed = at >= RekeyStep.Committed;
                AssertWhole(folder, committed ? expected : oldId, $"a crash at {at}");
                Assert.That(ResortLibrary.Scan(_root)[0].NeedsWaterAndRoads, Is.EqualTo(!committed), $"a crash at {at}: offered again only before the commit");

                Assert.That(Backfill(folder), Is.EqualTo(expected), $"after a crash at {at}, trying again finishes it");
                AssertWhole(folder, expected, $"finished after a crash at {at}");
                TearDown();
            }
        }

        [Test]
        public void LayersAlreadyThereAreNeverRewritten()
        {
            string folder = Package(roads: true);
            var before = ResortPackage.ReadManifest(folder);
            Assert.That(WaterAndRoads.Needed(before), Is.True, "roads only still needs water");
            byte[] roads = File.ReadAllBytes(Path.Combine(folder, RoadsFile.FileName));
            Backfill(folder);
            var after = ResortPackage.ReadManifest(folder);
            Assert.That(File.ReadAllBytes(Path.Combine(folder, RoadsFile.FileName)), Is.EqualTo(roads), "roads.json byte for byte");
            Assert.That(after.Layers.Single(l => l.Id == RoadsFile.LayerId).Sha256, Is.EqualTo(before.Layers.Single(l => l.Id == RoadsFile.LayerId).Sha256));
            Assert.That(after.Layers.Any(l => l.Id == WaterAndRoads.CoreLayer) && after.Layers.Any(l => l.Id == WaterAndRoads.RingLayer));
        }

        [Test]
        public void StrippingWaterAndRoadsThenGettingThemBringsTheSameIdBack()
        {
            string folder = Package();
            var m = ResortPackage.ReadManifest(folder);
            var site = Site;
            var full = OsmLayers.FetchAsync(Osm(), site, null, new TransferMeter(), CancellationToken.None).GetAwaiter().GetResult();
            OsmLayers.Add(m, folder, site, full);
            ResortPackage.WriteManifest(folder, m);
            string original = m.PackageId;

            m.Layers.RemoveAll(l => WaterAndRoads.LayerIds.Contains(l.Id));
            OsmLayers.Add(m, folder, site, new OsmLayers.Result { Failure = "Overpass was down" });
            ResortPackage.WriteManifest(folder, m);
            Assert.That(m.PackageId, Is.Not.EqualTo(original));
            Assert.That(Backfill(folder), Is.EqualTo(original), "the same response gives the package it was downloaded as");
        }

        [Test]
        public void TheSameAreaInTheLibraryAlreadyIsRefusedAndNothingChanges()
        {
            string first = Package();
            string other = Path.Combine(ResortLibrary.ResortsFolder(_root), "copy");
            Directory.CreateDirectory(other);
            foreach (string f in Directory.GetFiles(first)) File.Copy(f, Path.Combine(other, Path.GetFileName(f)));
            Backfill(other);
            var before = File.ReadAllText(Path.Combine(first, ResortPackage.ManifestFile));
            Assert.That(() => Backfill(first), Throws.InstanceOf<AreaRefusedException>());
            Assert.That(File.ReadAllText(Path.Combine(first, ResortPackage.ManifestFile)), Is.EqualTo(before));
            Assert.That(WaterAndRoads.LayerIds.Select(l => l == RoadsFile.LayerId ? RoadsFile.FileName : l + ".grid").Where(f => File.Exists(Path.Combine(first, f))), Is.Empty,
                        "no unlisted layer files are left behind");
        }

        [Test]
        public void ANewerSaveIsRefusedWithoutTouchingTheNetwork()
        {
            string folder = Package();
            string id = ResortPackage.ReadManifest(folder).PackageId;
            string save = Path.Combine(SaveStore.AreaFolder(_root, id), "save-001");
            Directory.CreateDirectory(save);
            File.WriteAllText(Path.Combine(save, SaveFile.FileName), "{ \"Version\": 99, \"Name\": \"From the future\", \"PackageId\": \"" + id + "\" }");
            int fetches = 0;
            Assert.That(() => Backfill(folder, osm: Osm(() => ++fetches)), Throws.InstanceOf<AreaRefusedException>().With.Message.Contains("newer version"));
            Assert.That(fetches, Is.Zero);
            Assert.That(ResortPackage.ReadManifest(folder).PackageId, Is.EqualTo(id));
        }

        [Test]
        public void ADownloadOfTheSameAreaLaterJoinsTheOneThatGotItsWaterAndRoads()
        {
            string folder = Package();
            string id = Backfill(folder);
            Assert.That(Path.GetFileName(folder), Is.Not.EqualTo(id), "the folder keeps its old name");

            string incoming = Path.Combine(_root, "Downloads", "build");
            Directory.CreateDirectory(incoming);
            foreach (string f in Directory.GetFiles(folder)) File.Copy(f, Path.Combine(incoming, Path.GetFileName(f)));
            Assert.That(ResortLibrary.Add(_root, incoming), Is.EqualTo(folder), "same id: the library's copy");
            Assert.That(Directory.Exists(incoming), Is.False, "the new copy is discarded");
            Assert.That(ResortLibrary.Scan(_root), Has.Count.EqualTo(1));
        }

        [Test]
        public void TheLayerNamesAgreeAcrossAssemblies()
        {
            Assert.That(OsmLayers.LayerIds, Is.EqualTo(new[] { AcquisitionPipeline.OsmCoreLayer, AcquisitionPipeline.OsmRingLayer, RoadsFile.LayerId }));
            Assert.That(OsmLayers.SkippedReason, Is.EqualTo(WaterAndRoads.SkippedReason));
        }
    }
}
