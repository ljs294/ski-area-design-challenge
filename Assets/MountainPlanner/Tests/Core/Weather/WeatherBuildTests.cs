using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Persistence;
using Newtonsoft.Json;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05, PR A2: the one weather build the download, Get weather and <c>acquire weather-build</c> share. The recorded
    /// Jackson Hole answers through it give the frozen rows byte for byte, so the real path is the tested path.
    /// </summary>
    public sealed class WeatherBuildTests
    {
        static readonly Lazy<WeatherBuildResult> Built = new Lazy<WeatherBuildResult>(() =>
        {
            string rec = TestData.Folder("weather/" + WeatherFixtures.JacksonHole);
            var site = WeatherSiteRequest.FromJson(File.ReadAllText(Path.Combine(rec, WeatherSiteRequest.FileName)));
            return WeatherBuild.BuildAsync(new FolderWeatherResponses(rec), site, null, CancellationToken.None, "tests").GetAwaiter().GetResult();
        });

        string _temp = "";

        [SetUp] public void SetUp() => _temp = Path.Combine(Path.GetTempPath(), "mp-weather-build-" + Guid.NewGuid().ToString("N"));
        [TearDown] public void TearDown() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }

        /// <summary>
        /// The Jackson Hole 2 km test package, skipping the test where its heights are Git LFS pointers: CI fetches only the
        /// small fixtures (repo-checks.yml), and the weather's site request is built from the heights.
        /// </summary>
        internal static string PackageWithHeights()
        {
            string from = TestData.Folder(WeatherFixtures.JacksonHole);
            TestData.Bytes(from, "heights-core.grid");
            TestData.Bytes(from, "heights-ring.grid");
            return from;
        }

        /// <summary>A copy of the Jackson Hole 2 km test package (format 1, no weather), the recording's own area.</summary>
        internal static string CopyPackage(string to)
        {
            string from = PackageWithHeights();
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
            return to;
        }

        [Test]
        public void TheRecordedAnswersGiveTheFrozenRowsByteForByte()
        {
            var r = Built.Value;
            Assert.That(r.HasRows);
            byte[] frozen = File.ReadAllBytes(Path.Combine(TestData.Folder("weather/" + WeatherFixtures.JacksonHole), WeatherRowsFile.FileName));
            Assert.That(WeatherRowsFile.ToBytes(r.Rows), Is.EqualTo(frozen), "the build path is the tested path");

            var info = r.Info;
            Assert.That(new[] { "Clean", "Limited", "Check anchor" }, Has.Member(info.Verdict));
            Assert.That(info.LastLabelYear - info.FirstLabelYear + 1, Is.EqualTo(WeatherFetch.WindowYears));
            Assert.That(info.RowsVersion, Is.EqualTo(WeatherRowsFile.Version));
            Assert.That(info.Sources.Select(s => s.Name), Is.SupersetOf(new[] { WeatherBuild.ProviderDaymet, WeatherBuild.ProviderPower }));
            Assert.That(info.Sources.Single(s => s.Name == WeatherBuild.ProviderDaymet).Citation, Does.Contain("doi.org"), "Daymet's own citation, never a typed copy");
            Assert.That(info.Qc, Is.Not.Empty);
            Assert.That(info.QcLimits["Version"], Is.GreaterThanOrEqualTo(1));
            Assert.That(info.Checks.Single(c => c.Name.StartsWith("WX-18 storm days", StringComparison.Ordinal)).Result, Is.EqualTo("pass"));
            Assert.That(info.ReferenceElevationMetres, Is.GreaterThan(2000));
        }

        [Test]
        public void TheSiteRequestComesFromThePackagesOwnHeights()
        {
            string pkg = PackageWithHeights();
            var site = WeatherBuild.SiteFor(pkg, ResortPackage.ReadManifest(pkg), 2026);
            var recorded = WeatherSiteRequest.FromJson(File.ReadAllText(Path.Combine(TestData.Folder("weather/" + WeatherFixtures.JacksonHole), WeatherSiteRequest.FileName)));
            // The pixel means are sums of doubles, which Unity's Mono and .NET round apart in the last digits (1e-11 m); they
            // only feed the georeference check (a 100 m limit), so compare them to a micrometre and everything else exactly.
            Assert.That(site.OurPixelElevations, Is.EqualTo(recorded.OurPixelElevations).Within(1e-6), "the recording was made from this package");
            site.OurPixelElevations = recorded.OurPixelElevations;
            Assert.That(site.ToJson(), Is.EqualTo(recorded.ToJson()), "the recording was made from this package");
        }

        [Test]
        public void AddingTheWeatherKeepsTheIdLayersAndEveryOtherFile()
        {
            string pkg = CopyPackage(_temp);
            var before = Directory.GetFiles(pkg).ToDictionary(Path.GetFileName, f => WeatherRowsFile.Sha256(File.ReadAllBytes(f)));
            var m = ResortPackage.ReadManifest(pkg);
            string id = m.PackageId;
            string layers = JsonConvert.SerializeObject(m.Layers);

            WeatherBuild.AddTo(pkg, m, Built.Value);
            ResortPackage.WriteManifest(pkg, m);

            var read = ResortPackage.ReadManifest(pkg);
            Assert.That(read.PackageId, Is.EqualTo(id), "the weather isn't part of the id: no saves move");
            Assert.That(JsonConvert.SerializeObject(read.Layers), Is.EqualTo(layers));
            Assert.That(read.FormatVersion, Is.EqualTo(2));
            Assert.That(read.Weather.RowsSha256, Is.EqualTo(WeatherRowsFile.Sha256(File.ReadAllBytes(Path.Combine(pkg, WeatherRowsFile.FileName)))));
            Assert.That(PackageValidator.Validate(pkg), Is.Empty);
            foreach (var kv in before)
                if (kv.Key != ResortPackage.ManifestFile)
                    Assert.That(WeatherRowsFile.Sha256(File.ReadAllBytes(Path.Combine(pkg, kv.Key))), Is.EqualTo(kv.Value), kv.Key + " is untouched");
            Assert.That(Directory.GetFiles(pkg).Select(Path.GetFileName).Except(before.Keys), Is.EqualTo(new[] { WeatherRowsFile.FileName }));

            // Credits shows the weather with no change of its own.
            var sources = CreditsReader.Build(new[] { ("Jackson Hole", read) });
            var history = sources.Single(s => s.Line == WeatherBuild.HistoryLine);
            Assert.That(history.What, Is.EqualTo("Weather history"));
            Assert.That(history.Providers, Does.Contain(WeatherBuild.ProviderDaymet));
            Assert.That(sources.Single(s => s.Line == WeatherBuild.HoursLine).Providers, Does.Contain(WeatherBuild.ProviderPower));
            Assert.That(sources.Where(s => s.Line.Length == 0).Select(s => s.Who), Has.None.StartsWith("Daymet").And.None.StartsWith("NASA"),
                        "every weather provider joins its line");
        }

        [Test]
        public void ABrokenRowsFileFailsTheValidator()
        {
            string pkg = CopyPackage(_temp);
            var m = ResortPackage.ReadManifest(pkg);
            WeatherBuild.AddTo(pkg, m, Built.Value);
            ResortPackage.WriteManifest(pkg, m);
            string rows = Path.Combine(pkg, WeatherRowsFile.FileName);
            byte[] bytes = File.ReadAllBytes(rows);
            bytes[bytes.Length / 2] ^= 0x5A;
            File.WriteAllBytes(rows, bytes);
            Assert.That(PackageValidator.Validate(pkg), Has.Some.Contains("weather.rows is corrupt"));
            File.Delete(rows);
            Assert.That(PackageValidator.Validate(pkg), Has.Some.EqualTo("weather.rows is missing."));
        }

        /// <summary>A recording with Daymet refused everywhere: a site outside its grid.</summary>
        sealed class NoDaymet : IWeatherResponses
        {
            readonly FolderWeatherResponses _inner;
            public NoDaymet(string root) { _inner = new FolderWeatherResponses(root); }
            public Task<byte[]> GetAsync(string key, string url, CancellationToken ct) =>
                key.StartsWith("daymet/", StringComparison.Ordinal) ? throw new WeatherSourceRefusedException("Outside the Daymet grid.") : _inner.GetAsync(key, url, ct);
        }

        [Test]
        public async Task OutsideDaymetTheAreaIsDoneWithNoRows()
        {
            string rec = TestData.Folder("weather/" + WeatherFixtures.JacksonHole);
            var site = WeatherSiteRequest.FromJson(File.ReadAllText(Path.Combine(rec, WeatherSiteRequest.FileName)));
            var r = await WeatherBuild.BuildAsync(new NoDaymet(rec), site, null, CancellationToken.None);
            Assert.That(r.HasRows, Is.False);
            Assert.That(r.Info.Verdict, Is.EqualTo(WeatherBuild.NoHistory));
            Assert.That(r.Info.Limited);

            string pkg = CopyPackage(_temp);
            var m = ResortPackage.ReadManifest(pkg);
            WeatherBuild.AddTo(pkg, m, r);
            ResortPackage.WriteManifest(pkg, m);
            var read = ResortPackage.ReadManifest(pkg);
            Assert.That(WeatherBuild.Needed(read), Is.False, "Get weather doesn't ask again");
            Assert.That(read.Weather.RowsFile, Is.Empty);
            Assert.That(File.Exists(Path.Combine(pkg, WeatherRowsFile.FileName)), Is.False);
            Assert.That(read.Attribution, Has.None.StartsWith("Weather"));
            Assert.That(PackageValidator.Validate(pkg), Is.Empty);
        }

        [Test]
        public void TheVersionTwoFixtureReadsBack()
        {
            string dir = TestData.Folder("formats/v2-package");
            var m = ResortPackage.ReadManifest(dir);
            Assert.That(m.FormatVersion, Is.EqualTo(2));
            Assert.That(m.PackageId, Is.EqualTo(ResortPackage.ComputeId(m)));
            Assert.That(m.Weather, Is.Not.Null);
            Assert.That(m.Weather.RowsVersion, Is.EqualTo(1));
            Assert.That(m.Attribution, Does.Contain(WeatherBuild.HistoryLine));
            Assert.That(WeatherRowsFile.Read(dir, m.Weather.RowsSha256), Is.Not.Null, "the rows match the manifest's hash");
        }

        /// <summary>Writes TestData/formats/v2-package (once; never edited after merge): the manifest and its weather.rows.</summary>
        [Test, Explicit("Writes the frozen v2 package fixture (once; never edited after merge)")]
        public void WriteTheVersionTwoPackageFixture()
        {
            string pkg = CopyPackage(_temp);
            var m = ResortPackage.ReadManifest(pkg);
            WeatherBuild.AddTo(pkg, m, Built.Value);
            ResortPackage.WriteManifest(pkg, m);
            string fixtures = Path.Combine(Path.GetDirectoryName(TestData.Folder("formats/v1-weather-rows")), "v2-package");
            Directory.CreateDirectory(fixtures);
            foreach (string f in new[] { ResortPackage.ManifestFile, WeatherRowsFile.FileName })
                File.Copy(Path.Combine(pkg, f), Path.Combine(fixtures, f), true);
        }
    }
}
