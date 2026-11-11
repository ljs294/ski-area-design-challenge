using System.IO;
using System.Linq;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Editor;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05, PR A2: the bundled demo is read-only in the player, so it carries its weather rows when its manifest
    /// lists them, and a package from before the weather bundles as before. PR B1: every build ships the typical
    /// mountain's two rows, which an area without its own weather plays.
    /// </summary>
    public sealed class DemoBundleWeatherTests
    {
        /// <summary>The repository's TestData (EditMode runs in the project folder).</summary>
        static string Folder(string name) => Path.GetFullPath(Path.Combine("TestData", name));

        [Test]
        public void TheDemoCarriesItsWeatherRows()
        {
            var m = ResortPackage.ReadManifest(Folder("formats/v2-package"));
            Assert.That(DemoBundle.PackageFiles(m), Has.Member(WeatherRowsFile.FileName));
        }

        [Test]
        public void APackageWithoutWeatherBundlesAsBefore()
        {
            var m = ResortPackage.ReadManifest(Folder("jackson-hole-2km"));
            Assert.That(m.Weather, Is.Null);
            var files = DemoBundle.PackageFiles(m).ToList();
            Assert.That(files, Is.EqualTo(new[] { ResortPackage.ManifestFile }.Concat(m.Layers.Select(l => l.File)).Distinct().ToList()));
        }

        [Test]
        public void EveryBuildShipsTheTypicalMountainsRows()
        {
            string root = Path.Combine(Path.GetTempPath(), "mp-typical-weather-" + System.Diagnostics.Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(root);
                string exe = Path.Combine(root, "Game.exe");
                DemoBundle.AddTypicalWeather(exe);
                string folder = Path.Combine(DemoBundle.StreamingAssetsOf(exe), "Weather");
                foreach (string file in new[] { WeatherGeneric.WestFile, WeatherGeneric.EastFile })
                {
                    string path = Path.Combine(folder, file);
                    Assert.That(File.Exists(path), file);
                    Assert.That(WeatherRowsFile.FromBytes(File.ReadAllBytes(path)).ContentHash(), Is.Not.Zero, file);
                }
                var west = WeatherRowsFile.FromBytes(File.ReadAllBytes(Path.Combine(folder, WeatherGeneric.WestFile)));
                var jh = WeatherRowsFile.FromBytes(File.ReadAllBytes(Folder("weather/jackson-hole-2km/weather.rows")));
                Assert.That(west.ContentHash(), Is.EqualTo(jh.ContentHash()), "West is the frozen Jackson Hole rows");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
