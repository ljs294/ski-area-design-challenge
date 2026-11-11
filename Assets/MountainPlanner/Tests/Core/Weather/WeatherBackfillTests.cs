using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Weather;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.05, PR A2: Get weather on an area from before the weather, and the download's Weather stage. The area keeps its
    /// id and every other file; a run cut short leaves it as it was; the stage never holds a download up.
    /// </summary>
    public sealed class WeatherBackfillTests
    {
        const int BuildYear = 2026;   // the recording's own
        string _root = "", _pkg = "";

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "mp-weather-backfill-" + Guid.NewGuid().ToString("N"));
            _pkg = WeatherBuildTests.CopyPackage(Path.Combine(_root, "Resorts", "jackson-hole-2km"));
        }

        [TearDown]
        public void TearDown()
        {
            Http.NetworkDisabled = false;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        static IWeatherResponses Recording() => new FolderWeatherResponses(TestData.Folder("weather/" + WeatherFixtures.JacksonHole));

        Dictionary<string, string> Hashes() =>
            Directory.GetFiles(_pkg).ToDictionary(Path.GetFileName, f => WeatherRowsFile.Sha256(File.ReadAllBytes(f)));

        sealed class Sink : IProgress<AcquisitionProgress>
        {
            public readonly List<AcquisitionProgress> Seen = new List<AcquisitionProgress>();
            public void Report(AcquisitionProgress p) { lock (Seen) Seen.Add(p); }
        }

        [Test]
        public async Task GetWeatherAddsItAndTouchesNothingElse()
        {
            var before = Hashes();
            string id = ResortPackage.ReadManifest(_pkg).PackageId;
            var sink = new Sink();

            Assert.That(await WeatherBackfill.RunAsync(_pkg, Recording(), new TransferMeter(), sink, CancellationToken.None, BuildYear), Is.True);

            var m = ResortPackage.ReadManifest(_pkg);
            Assert.That(m.PackageId, Is.EqualTo(id), "same id: no save or recent entry moves");
            Assert.That(WeatherBuild.Needed(m), Is.False);
            Assert.That(PackageValidator.Validate(_pkg), Is.Empty);
            var after = Hashes();
            Assert.That(after.Keys.Except(before.Keys), Is.EqualTo(new[] { WeatherRowsFile.FileName }));
            foreach (var kv in before.Where(kv => kv.Key != ResortPackage.ManifestFile))
                Assert.That(after[kv.Key], Is.EqualTo(kv.Value), kv.Key + " is untouched");
            Assert.That(File.ReadAllBytes(Path.Combine(_pkg, WeatherRowsFile.FileName)),
                        Is.EqualTo(File.ReadAllBytes(Path.Combine(TestData.Folder("weather/" + WeatherFixtures.JacksonHole), WeatherRowsFile.FileName))));
            Assert.That(sink.Seen.Select(p => p.Detail), Has.Some.Contains(WeatherBuild.FetchStep).IgnoreCase.And.Some.Contains(WeatherBuild.CalibrateStep).IgnoreCase);
            Assert.That(sink.Seen.Last().Finished);

            byte[] manifest = File.ReadAllBytes(Path.Combine(_pkg, ResortPackage.ManifestFile));
            Assert.That(await WeatherBackfill.RunAsync(_pkg, Recording(), new TransferMeter(), null, CancellationToken.None, BuildYear), Is.False, "a second run does nothing");
            Assert.That(File.ReadAllBytes(Path.Combine(_pkg, ResortPackage.ManifestFile)), Is.EqualTo(manifest));
        }

        /// <summary>
        /// Owner finding F1: the download card's Cancel stops Get weather in the middle of the calibration (not after its
        /// 15 s), the area is byte for byte as it was, and the bar moved through the calibration rather than sitting still.
        /// </summary>
        [Test]
        public void CancelDuringTheCalibrationStopsItAndChangesNothing()
        {
            var before = Hashes();
            using var cancel = new CancellationTokenSource();
            var calibrating = new List<double>();
            var sink = new CancelWhen(p =>
            {
                if (!p.Detail.Contains(WeatherBuild.CalibrateStep, StringComparison.OrdinalIgnoreCase)) return;
                lock (calibrating) calibrating.Add(p.Overall);
                if (p.Overall > WeatherBuild.FetchShare + (1 - WeatherBuild.FetchShare) * 0.5) cancel.Cancel();
            });

            Assert.That(async () => await WeatherBackfill.RunAsync(_pkg, Recording(), new TransferMeter(), sink, cancel.Token, BuildYear),
                        Throws.InstanceOf<OperationCanceledException>());

            Assert.That(Hashes(), Is.EqualTo(before), "nothing written: the manifest is the commit point");
            Assert.That(WeatherBuild.Needed(ResortPackage.ReadManifest(_pkg)), "Get weather is still offered");
            Assert.That(calibrating.Distinct().Count(), Is.GreaterThanOrEqualTo(4), "the bar moves during the calibration");
            Assert.That(calibrating, Is.Ordered, "and never backwards");
            Assert.That(calibrating.Min(), Is.LessThan(WeatherBuild.FetchShare + 0.05), "the calibration starts where the fetching ended, not at 85 %");
        }

        sealed class CancelWhen : IProgress<AcquisitionProgress>
        {
            readonly Action<AcquisitionProgress> _seen;
            public CancelWhen(Action<AcquisitionProgress> seen) { _seen = seen; }
            public void Report(AcquisitionProgress p) => _seen(p);
        }

        [Test]
        public async Task ARunCutShortLeavesTheAreaAsItWasAndTheNextOneFinishes()
        {
            byte[] manifest = File.ReadAllBytes(Path.Combine(_pkg, ResortPackage.ManifestFile));
            Assert.ThrowsAsync<IOException>(() => WeatherBackfill.RunAsync(_pkg, Recording(), new TransferMeter(), null, CancellationToken.None, BuildYear,
                                                                            beforeCommit: () => throw new IOException("the power went")));
            Assert.That(File.ReadAllBytes(Path.Combine(_pkg, ResortPackage.ManifestFile)), Is.EqualTo(manifest), "the manifest is the commit point");
            Assert.That(WeatherBuild.Needed(ResortPackage.ReadManifest(_pkg)), "still no weather");
            Assert.That(PackageValidator.Validate(_pkg), Is.Empty, "an unlisted weather.rows harms nothing");

            Assert.That(await WeatherBackfill.RunAsync(_pkg, Recording(), new TransferMeter(), null, CancellationToken.None, BuildYear), Is.True);
            Assert.That(PackageValidator.Validate(_pkg), Is.Empty);
        }

        [Test]
        public void OfflineItFailsBeforeWritingAnything()
        {
            Http.NetworkDisabled = true;
            var before = Hashes();
            string cache = Path.Combine(_root, "download-cache");
            Assert.ThrowsAsync<NetworkDisabledException>(() => WeatherBackfill.RunAsync(_pkg, cache, null, CancellationToken.None, BuildYear));
            Assert.That(Hashes(), Is.EqualTo(before));
        }

        SiteRequest SameRequest()
        {
            var m = ResortPackage.ReadManifest(_pkg);
            return new SiteRequest { Name = m.Site.Name, Centre = new GeoPoint(m.Site.Latitude, m.Site.Longitude), SizeKm = m.Site.SizeMetres / 1000.0 };
        }

        [Test]
        public async Task AResumedBuildFromBeforeTheWeatherGetsItThenPrepares()
        {
            var sink = new Sink();
            var pipeline = new AcquisitionPipeline(Path.Combine(_root, "download-cache"), Recording());
            var manifest = await pipeline.RunAsync(SameRequest(), _pkg, sink, CancellationToken.None);

            Assert.That(manifest.Weather, Is.Not.Null);
            Assert.That(ResortPackage.ReadManifest(_pkg).Weather.RowsSha256, Is.EqualTo(manifest.Weather.RowsSha256));
            Assert.That(PackageValidator.Validate(_pkg), Is.Empty);
            Assert.That(TerrainCache.IsCurrent(_pkg, manifest), Is.True, "Preparing ran after the weather (same id, so the cache matches)");
            Assert.That(sink.Seen.Select(p => p.Stage).Where(n => n.Length > 0).Distinct(),
                        Is.EqualTo(new[] { AcquisitionPipeline.StageWeather, AcquisitionPipeline.StagePrepare, "Done" }));
        }

        /// <summary>Weather services that fail after everything else downloaded.</summary>
        sealed class Down : IWeatherResponses
        {
            public Task<byte[]> GetAsync(string key, string url, CancellationToken ct) => throw new IOException("503 Service Unavailable");
        }

        [Test]
        public async Task TheWeatherFailingNeverHoldsADownloadUp()
        {
            var sink = new Sink();
            var pipeline = new AcquisitionPipeline(Path.Combine(_root, "download-cache"), new Down());
            var manifest = await pipeline.RunAsync(SameRequest(), _pkg, sink, CancellationToken.None);

            Assert.That(manifest.Weather, Is.Null, "finished without it: Get weather adds it later");
            Assert.That(pipeline.LastWeatherFailure, Does.Contain("503"));
            Assert.That(sink.Seen.Select(p => p.Detail), Has.Some.Contains("weather unavailable"));
            Assert.That(TerrainCache.IsCurrent(_pkg, manifest), Is.True);
            Assert.That(File.Exists(Path.Combine(_pkg, WeatherRowsFile.FileName)), Is.False);
        }

        [Test]
        public void CancellingTheDownloadDuringTheWeatherStopsIt()
        {
            using (var cts = new CancellationTokenSource())
            {
                cts.Cancel();
                var pipeline = new AcquisitionPipeline(Path.Combine(_root, "download-cache"), Recording());
                Assert.That(() => pipeline.RunAsync(SameRequest(), _pkg, null, cts.Token), Throws.InstanceOf<OperationCanceledException>());
            }
            Assert.That(WeatherBuild.Needed(ResortPackage.ReadManifest(_pkg)), "nothing was added");
        }
    }
}
