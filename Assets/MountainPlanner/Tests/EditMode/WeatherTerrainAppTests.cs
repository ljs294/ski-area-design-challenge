using System;
using System.IO;
using MountainPlanner.App;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05 PR B2 (the audit's M5): the app's own builder of the weather's static inputs
    /// (<see cref="ResortOpener.BuildWeatherTerrain"/>) on real terrain, the Jackson Hole 2 km fixture's heights on the 8 m
    /// grid over its ring: valleys sheltered and ridges exposed, the orographic factor moving snow without adding any,
    /// and the inputs a copy of the heights they were built from, so earthwork applied to that array afterwards
    /// (<c>SnowElevation</c> follows the player's edits) can't reach the weather (WX-08).
    /// </summary>
    public sealed class WeatherTerrainAppTests
    {
        static string JacksonHole2Km()
        {
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir != null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "TestData", "jackson-hole-2km");
                if (File.Exists(Path.Combine(candidate, "heights-ring.grid")) && new FileInfo(Path.Combine(candidate, "heights-ring.grid")).Length > 1_000_000)
                    return candidate;
            }
            Assert.Ignore("TestData/jackson-hole-2km's heights are not available here.");
            return null;
        }

        [Test]
        public void TheAppsInputsOnRealTerrain()
        {
            string dir = JacksonHole2Km();
            var m = ResortPackage.ReadManifest(dir);
            var origin = new AlbersPoint(m.Site.CentreX, m.Site.CentreY);
            var square = SiteSquare.Create(origin, m.Site.SizeMetres / 1000.0);
            var ringBox = square.Ring;
            var coreBox = square.Core;
            var ring = new Rect((float)(ringBox.West - origin.X), (float)(ringBox.South - origin.Y), (float)ringBox.Width, (float)ringBox.Height);
            var core = new Rect((float)(coreBox.West - origin.X), (float)(coreBox.South - origin.Y), (float)coreBox.Width, (float)coreBox.Height);
            const int cell = WeatherTerrain.CellMetres;
            int width = Mathf.CeilToInt(ring.width / cell), depth = Mathf.CeilToInt(ring.height / cell);
            var elevation = new float[width * depth];
            using (var heights = new PackageHeights(dir, m))
                for (int j = 0; j < depth; j++)
                    for (int i = 0; i < width; i++)
                        elevation[j * width + i] = (float)heights.At(ringBox.West + (i + 0.5) * cell, ringBox.South + (j + 0.5) * cell);

            var t = ResortOpener.BuildWeatherTerrain(elevation, width, depth, ring, core);
            Assert.That((t.Width, t.Depth), Is.EqualTo((width, depth)));
            Assert.That(t.BinFeet, Is.EqualTo(WeatherTerrain.MinBinFeet));
            Assert.That(t.CellAt(0, 0), Is.EqualTo(depth / 2 * width + width / 2), "the site's centre is the grid's middle cell");

            // The lowest and highest cells of the box: a valley floor is sheltered, the top exposed.
            int low = -1, high = -1;
            var classes = new int[8];
            for (int j = 0; j < depth; j++)
                for (int i = 0; i < width; i++)
                {
                    int c = j * width + i;
                    if (t.Bin[c] == WeatherTerrain.NoData) continue;
                    classes[t.Exposure[c]]++;
                    float x = ring.xMin + (i + 0.5f) * cell, z = ring.yMin + (j + 0.5f) * cell;
                    if (x < core.xMin || x > core.xMax || z < core.yMin || z > core.yMax) continue;
                    if (low < 0 || elevation[c] < elevation[low]) low = c;
                    if (high < 0 || elevation[c] > elevation[high]) high = c;
                }
            Debug.Log($"[WeatherTerrainAppTests] classes {string.Join(", ", classes)}; low hvf {t.Hvf[low] * 10} ft class {t.Exposure[low]}, high hvf {t.Hvf[high] * 10} ft class {t.Exposure[high]}; hvf_ref {t.HvfRefFeet} ft");
            Assert.That(t.Exposure[low], Is.LessThan(t.Exposure[high]));
            Assert.That(t.Hvf[low], Is.LessThan(t.Hvf[high]));
            int used = 0;
            foreach (int n in classes) if (n > 0) used++;
            Assert.That(used, Is.GreaterThanOrEqualTo(5), "real terrain spreads over the classes");

            // The factor's mean over the box's 512 m cells is 1.0 for every wind, and it isn't flat.
            int coarse = t.CoarseWidth * t.CoarseDepth;
            for (int o = 0; o < 8; o++)
            {
                long sum = 0;
                int n = 0, min = int.MaxValue, max = 0;
                for (int j = 0; j < t.CoarseDepth; j++)
                    for (int i = 0; i < t.CoarseWidth; i++)
                    {
                        float x = ring.xMin + (i * 64 + 32) * cell, z = ring.yMin + (j * 64 + 32) * cell;
                        if (x < core.xMin || x >= core.xMax || z < core.yMin || z >= core.yMax) continue;
                        int v = t.Orographic[o * coarse + j * t.CoarseWidth + i];
                        sum += v; n++;
                        min = Mathf.Min(min, v); max = Mathf.Max(max, v);
                    }
                Assert.That(n, Is.GreaterThan(8));
                Assert.That((double)sum / n, Is.EqualTo(1000).Within(8), $"octant {o}");
                Assert.That(max - min, Is.GreaterThan(50), $"octant {o}: a mountain has a windward side");
            }

            // WX-08: earthwork changes the array afterwards (it becomes SnowElevation); the inputs don't follow.
            ulong before = t.Hash;
            byte binAtHigh = t.Bin[high];
            for (int k = 0; k < elevation.Length; k++) elevation[k] -= 200;
            Assert.That(t.Hash, Is.EqualTo(before));
            Assert.That(t.Bin[high], Is.EqualTo(binAtHigh));
            Assert.That(ResortOpener.BuildWeatherTerrain(elevation, width, depth, ring, core).MinFeet, Is.LessThan(t.MinFeet), "a rebuild would have followed the edit: so the game never rebuilds");
        }
    }
}
