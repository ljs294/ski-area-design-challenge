using System;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Cover;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    // Roads (task 12d): which OSM ways count, their surface and width, the package's roads.json, and how roads take
    // their share of the ground cover.
    public sealed class RoadTests
    {
        const string Overpass = @"{""elements"":[
 {""type"":""way"",""tags"":{""highway"":""secondary"",""name"":""Moose-Wilson Road"",""width"":""8 m""},""geometry"":[{""lat"":43.59,""lon"":-110.85},{""lat"":43.591,""lon"":-110.849}]},
 {""type"":""way"",""tags"":{""highway"":""track"",""tracktype"":""grade3""},""geometry"":[{""lat"":43.592,""lon"":-110.85},{""lat"":43.593,""lon"":-110.85}]},
 {""type"":""way"",""tags"":{""highway"":""residential"",""surface"":""gravel""},""geometry"":[{""lat"":43.594,""lon"":-110.85},{""lat"":43.595,""lon"":-110.85}]},
 {""type"":""way"",""tags"":{""highway"":""track"",""surface"":""asphalt""},""geometry"":[{""lat"":43.596,""lon"":-110.85},{""lat"":43.597,""lon"":-110.85}]},
 {""type"":""way"",""tags"":{""highway"":""footway""},""geometry"":[{""lat"":43.59,""lon"":-110.86},{""lat"":43.591,""lon"":-110.86}]},
 {""type"":""way"",""tags"":{""highway"":""primary"",""tunnel"":""yes""},""geometry"":[{""lat"":43.59,""lon"":-110.87},{""lat"":43.591,""lon"":-110.87}]},
 {""type"":""way"",""tags"":{""building"":""yes""},""geometry"":[{""lat"":43.59,""lon"":-110.88},{""lat"":43.5901,""lon"":-110.88},{""lat"":43.5901,""lon"":-110.8801},{""lat"":43.59,""lon"":-110.88}]}
]}";

        [Test]
        public void SurfacesFollowTheTagThenTheClass()
        {
            Assert.That(RoadRules.SurfaceOf("secondary", ""), Is.EqualTo(RoadSurface.Paved));
            Assert.That(RoadRules.SurfaceOf("track", ""), Is.EqualTo(RoadSurface.Unpaved));
            Assert.That(RoadRules.SurfaceOf("residential", "gravel"), Is.EqualTo(RoadSurface.Unpaved));
            Assert.That(RoadRules.SurfaceOf("track", "asphalt"), Is.EqualTo(RoadSurface.Paved));
            Assert.That(RoadRules.IsRoad("footway") || RoadRules.IsRoad("path") || RoadRules.IsRoad("cycleway"), Is.False, "paths are not roads");
            Assert.That(RoadRules.IsRoad("track"), Is.True, "unpaved tracks are (owner, 2026-10-02)");
        }

        [Test]
        public void OverpassGivesTheRoadsAboveGround()
        {
            var roads = OsmFeatures.ParseRoads(Encoding.UTF8.GetBytes(Overpass));
            Assert.That(roads.Select(r => r.Class), Is.EqualTo(new[] { "secondary", "track", "residential", "track" }), "no footway, tunnel or building");
            Assert.That(roads[0].Name, Is.EqualTo("Moose-Wilson Road"));
            Assert.That(roads[0].WidthMetres, Is.EqualTo(8), "OSM width wins");
            Assert.That(roads[1].WidthMetres, Is.EqualTo(RoadRules.Widths["track"]));
            Assert.That(roads.Select(r => r.Surface), Is.EqualTo(new[] { RoadSurface.Paved, RoadSurface.Unpaved, RoadSurface.Unpaved, RoadSurface.Paved }));
            Assert.That(roads.All(r => r.Points.Count == 2));
        }

        [Test]
        public void TheDevelopedRasterIsUnchangedByTracks()
        {
            // Tracks joined the Overpass query for the road layer; the water and developed shapes must not change.
            var shapes = OsmFeatures.Parse(Encoding.UTF8.GetBytes(Overpass));
            Assert.That(shapes.Select(s => s.Tag), Is.EqualTo(new[] { "highway=secondary", "highway=residential", "building" }));
        }

        [Test]
        public void RoadsFileRoundTripsAndIsStable()
        {
            string dir = Path.Combine(Path.GetTempPath(), "mp-roads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var roads = OsmFeatures.ParseRoads(Encoding.UTF8.GetBytes(Overpass));
                var a = new PackageManifest();
                RoadsFile.Add(dir, a, roads);
                byte[] first = File.ReadAllBytes(Path.Combine(dir, RoadsFile.FileName));
                var b = new PackageManifest();
                RoadsFile.Add(dir, b, Enumerable.Reverse(roads));
                Assert.That(File.ReadAllBytes(Path.Combine(dir, RoadsFile.FileName)), Is.EqualTo(first), "the same roads in any order give the same file");
                Assert.That(b.Layers.Single().Sha256, Is.EqualTo(a.Layers.Single().Sha256));

                var back = RoadsFile.Read(dir, a);
                Assert.That(back.Count, Is.EqualTo(roads.Count));
                var secondary = back.Single(r => r.Class == "secondary");
                Assert.That(secondary.Name, Is.EqualTo("Moose-Wilson Road"));
                Assert.That(secondary.Points[0].X, Is.EqualTo(roads[0].Points[0].X).Within(0.005), "to the centimetre");

                File.AppendAllText(Path.Combine(dir, RoadsFile.FileName), " ");
                Assert.Throws<InvalidDataException>(() => RoadsFile.Read(dir, a), "a changed file fails its hash");
                Assert.That(RoadsFile.Read(dir, new PackageManifest()), Is.Empty, "a package from before task 12d has no roads");
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }

        static (double[] W, double Snow) Classify(CoverSample s)
        {
            var w = new double[GroundCover.Layers];
            GroundCover.Classify(s, w, out double snow);
            return (w, snow);
        }

        [Test]
        public void RoadsComeOutOfTheDevelopedLandThatHeldThem()
        {
            // A paved road the developed raster already covered: all of it becomes road.
            var paved = Classify(new CoverSample { OsmDeveloped = 1, RoadPaved = 1, WcGrass = 1, SlopeDegrees = 5 });
            Assert.That(paved.W[(int)GroundLayer.PavedRoad], Is.EqualTo(1).Within(1e-9));
            Assert.That(paved.W[(int)GroundLayer.Developed], Is.EqualTo(0).Within(1e-9));
            // A track across a meadow: half road at its anti-aliased edge, half grass.
            var track = Classify(new CoverSample { RoadUnpaved = 0.5, WcGrass = 1, SlopeDegrees = 5 });
            Assert.That(track.W[(int)GroundLayer.UnpavedRoad], Is.EqualTo(0.5).Within(1e-9));
            Assert.That(track.W[(int)GroundLayer.Grass], Is.EqualTo(0.5).Within(1e-9));
            Assert.That(track.W.Sum(), Is.EqualTo(1).Within(1e-9));
            // No roads (a package from before task 12d): exactly the old five weights, roads zero.
            var none = Classify(new CoverSample { OsmDeveloped = 0.3, WcTrees = 1, Canopy = 1, CanopyWeight = 1, SlopeDegrees = 10 });
            Assert.That(none.W[(int)GroundLayer.Developed], Is.EqualTo(0.3).Within(1e-9));
            Assert.That(none.W[(int)GroundLayer.PavedRoad] + none.W[(int)GroundLayer.UnpavedRoad], Is.EqualTo(0));
            // Roads never take the water's place.
            var bridge = Classify(new CoverSample { OsmWater = 1, RoadPaved = 1 });
            Assert.That(bridge.W[(int)GroundLayer.Water], Is.EqualTo(1).Within(1e-9));
            Assert.That(bridge.W[(int)GroundLayer.PavedRoad], Is.EqualTo(0).Within(1e-9));
        }
    }
}
