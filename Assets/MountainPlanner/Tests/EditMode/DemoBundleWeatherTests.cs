using System.IO;
using System.Linq;
using MountainPlanner.Editor;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05, PR A2: the bundled demo is read-only in the player, so it carries its weather rows when its manifest
    /// lists them, and a package from before the weather bundles as before.
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
    }
}
