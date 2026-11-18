using System;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Water;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>Task 4.14's streams and water names (WS-03, WS-08, LK-10): the inflow table, the OSM lines and the package's water.json (manifest v3).</summary>
    public sealed class StreamsTests
    {
        string _temp = "";

        [SetUp] public void SetUp() => _temp = Path.Combine(Path.GetTempPath(), "mp-streams-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }

        [Test]
        public void WS03_TheInflowTableAndItsSeasons()
        {
            Assert.That(StreamFlow.BaseCfs(2), Is.EqualTo(1));
            Assert.That(StreamFlow.BaseCfs(3), Is.EqualTo(1));
            Assert.That(StreamFlow.BaseCfs(6.5), Is.EqualTo(3.5).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(13), Is.EqualTo(11).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(26), Is.EqualTo(35).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(50), Is.EqualTo(106).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(100), Is.EqualTo(353).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(200), Is.EqualTo(1060).Within(1e-12));
            Assert.That(StreamFlow.BaseCfs(201), Is.EqualTo(2650));
            Assert.That(StreamFlow.BaseCfs(8), Is.EqualTo(3.5 + 7.5 * 1.5 / 6.5).Within(1e-12), "linear between rows: the default stream");
            Assert.That(new[] { 12, 1, 2, 3, 4, 10, 11 }.Select(StreamFlow.SeasonFactor), Is.EqualTo(new[] { 0.6, 0.6, 0.6, 1.0, 1.5, 0.8, 0.8 }));
            Assert.That(StreamFlow.DefaultWidthMetres("stream") / 0.3048, Is.EqualTo(8).Within(1e-9));
            Assert.That(StreamFlow.DefaultWidthMetres("river") / 0.3048, Is.EqualTo(46).Within(1e-9));
        }

        const string Overpass = @"{""elements"":[
 {""type"":""way"",""id"":4001,""tags"":{""waterway"":""stream"",""name"":""Twin Creek""},""geometry"":[{""lat"":43.6,""lon"":-110.85},{""lat"":43.59,""lon"":-110.85}]},
 {""type"":""way"",""id"":4002,""tags"":{""waterway"":""river"",""width"":""12 m""},""geometry"":[{""lat"":43.6,""lon"":-110.84},{""lat"":43.59,""lon"":-110.83}]},
 {""type"":""way"",""id"":4003,""tags"":{""waterway"":""stream"",""tunnel"":""culvert""},""geometry"":[{""lat"":43.6,""lon"":-110.82},{""lat"":43.59,""lon"":-110.82}]},
 {""type"":""way"",""id"":4004,""tags"":{""highway"":""track""},""geometry"":[{""lat"":43.6,""lon"":-110.81},{""lat"":43.59,""lon"":-110.81}]},
 {""type"":""way"",""id"":4005,""tags"":{""natural"":""water"",""name"":""Moose Pond""},""geometry"":[{""lat"":43.6,""lon"":-110.8},{""lat"":43.6,""lon"":-110.79},{""lat"":43.59,""lon"":-110.79},{""lat"":43.59,""lon"":-110.8},{""lat"":43.6,""lon"":-110.8}]},
 {""type"":""way"",""id"":4006,""tags"":{""natural"":""water""},""geometry"":[{""lat"":43.58,""lon"":-110.8},{""lat"":43.58,""lon"":-110.79},{""lat"":43.57,""lon"":-110.79},{""lat"":43.58,""lon"":-110.8}]}
]}";

        static System.Collections.Generic.List<WaterName> Names() => OsmFeatures.WaterNames(OsmFeatures.Parse(Encoding.UTF8.GetBytes(Overpass)));

        [Test]
        public void LK10_NamedWaterAreasKeepTheirNameAndAPointInside()
        {
            var names = Names();
            Assert.That(names.Select(n => n.Name), Is.EqualTo(new[] { "Moose Pond" }), "unnamed water is left out");
            var ring = OsmFeatures.Parse(Encoding.UTF8.GetBytes(Overpass)).Single(s => s.Name == "Moose Pond").Parts;
            double minX = ring[0].Min(p => p.X), maxX = ring[0].Max(p => p.X), minY = ring[0].Min(p => p.Y), maxY = ring[0].Max(p => p.Y);
            var at = names[0].Inside;
            Assert.That(at.X, Is.InRange(minX, maxX));
            Assert.That(at.Y, Is.InRange(minY, maxY));
        }

        [Test]
        public void LK10_AnInteriorPointFindsTheWaterInsideAnUShapedLake()
        {
            // A U: the box's middle column is land, so the point must land in an arm.
            var u = new System.Collections.Generic.List<AlbersPoint>
            {
                new AlbersPoint(0, 0), new AlbersPoint(30, 0), new AlbersPoint(30, 30), new AlbersPoint(20, 30), new AlbersPoint(20, 10),
                new AlbersPoint(10, 10), new AlbersPoint(10, 30), new AlbersPoint(0, 30), new AlbersPoint(0, 0),
            };
            var p = WaterName.InteriorPoint(new[] { u }).Value;
            Assert.That(p.X < 10 || p.X > 20, Is.True, $"({p.X}, {p.Y}) is in an arm, not the bay");
            Assert.That(WaterName.InteriorPoint(new[] { new System.Collections.Generic.List<AlbersPoint> { new AlbersPoint(0, 5), new AlbersPoint(9, 5) } }), Is.Null);
        }

        [Test]
        public void WS08_TheStreamLinesAreParsedWithTheirWidths()
        {
            var s = OsmFeatures.ParseStreams(Encoding.UTF8.GetBytes(Overpass));
            Assert.That(s.Select(x => $"{x.Way} {x.Class} {x.Name} {x.WidthMetres:0.###} {x.WidthFromOsm}"),
                Is.EqualTo(new[] { "4001 stream Twin Creek 2.438 False", "4002 river  12 True" }), "culverts and roads are left out");
            Assert.That(s[0].Points.Count, Is.EqualTo(2));
        }

        [Test]
        public void WS08_StreamsRoundTripAndLeaveThePackageIdAlone()
        {
            Directory.CreateDirectory(_temp);
            var m = ResortPackage.ReadManifest(CopyVersionTwo(_temp));
            string before = ResortPackage.ComputeId(m);
            WaterFile.Add(_temp, m, OsmFeatures.ParseStreams(Encoding.UTF8.GetBytes(Overpass)), Names());
            ResortPackage.WriteManifest(_temp, m);
            var back = ResortPackage.ReadManifest(_temp);
            Assert.That(back.PackageId, Is.EqualTo(before), "streams are outside the id: no rekey, no save moves");
            Assert.That(back.Water.Streams, Is.EqualTo(2));
            Assert.That(back.Water.Names, Is.EqualTo(1));
            var s = WaterFile.Read(_temp, back);
            Assert.That(s.Select(x => x.Way), Is.EqualTo(new ulong[] { 4001, 4002 }));
            var n = WaterFile.ReadNames(_temp, back);
            Assert.That(n.Select(x => x.Name), Is.EqualTo(new[] { "Moose Pond" }));
            Assert.That(n[0].Inside.X, Is.EqualTo(Math.Round(Names()[0].Inside.X, 2)), "to the centimetre");
            File.AppendAllText(Path.Combine(_temp, WaterFile.FileName), " ");
            Assert.Throws<InvalidDataException>(() => WaterFile.Read(_temp, back), "a changed file is refused");
            Assert.Throws<InvalidDataException>(() => WaterFile.ReadNames(_temp, back));
        }

        [Test]
        public void TheVersionThreePackageFixtureReadsBack()
        {
            string dir = TestData.Folder("formats/v3-package");
            var m = ResortPackage.ReadManifest(dir);
            Assert.That(m.FormatVersion, Is.EqualTo(3));
            Assert.That(m.PackageId, Is.EqualTo(ResortPackage.ComputeId(m)));
            Assert.That(m.Water, Is.Not.Null);
            Assert.That(WaterFile.Read(dir, m).Select(x => x.Name), Is.EqualTo(new[] { "Twin Creek", "" }));
            Assert.That(WaterFile.ReadNames(dir, m).Select(x => x.Name), Is.EqualTo(new[] { "Moose Pond" }));
            var v2 = ResortPackage.ReadManifest(TestData.Folder("formats/v2-package"));
            Assert.That(v2.Water, Is.Null, "a v2 area has no water.json: rasterised streams only, no names");
            Assert.That(WaterFile.Read(TestData.Folder("formats/v2-package"), v2), Is.Empty);
            Assert.That(m.PackageId, Is.EqualTo(v2.PackageId), "the same area, the same id");
        }

        /// <summary>Writes TestData/formats/v3-package (once; never edited after merge): the v2 fixture plus water.json.</summary>
        [Test, Explicit("Writes the frozen v3 package fixture (once; never edited after merge)")]
        public void WriteTheVersionThreePackageFixture()
        {
            string fixtures = Path.Combine(Path.GetDirectoryName(TestData.Folder("formats/v2-package")), "v3-package");
            Directory.CreateDirectory(fixtures);
            var m = ResortPackage.ReadManifest(CopyVersionTwo(fixtures));
            WaterFile.Add(fixtures, m, OsmFeatures.ParseStreams(Encoding.UTF8.GetBytes(Overpass)), Names());
            ResortPackage.WriteManifest(fixtures, m);
        }

        static string CopyVersionTwo(string to)
        {
            string from = TestData.Folder("formats/v2-package");
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            return to;
        }
    }
}
