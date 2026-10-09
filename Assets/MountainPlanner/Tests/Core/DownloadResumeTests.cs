using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-06: a download killed after Building resumes at Preparing, with no network at all; anything less than a
    /// sound package for the same request runs the whole pipeline again (whose downloads come from the disk cache:
    /// <see cref="ResumeCacheTests"/>).
    /// </summary>
    public sealed class DownloadResumeTests
    {
        string _folder;

        [SetUp]
        public void CopyJacksonHole()
        {
            string source = TestData.Folder("jackson-hole-2km");
            foreach (string f in Directory.GetFiles(source, "*.grid")) TestData.Bytes(source, Path.GetFileName(f));
            _folder = Path.Combine(Path.GetTempPath(), "mp-resume-" + Guid.NewGuid().ToString("N"), "build");
            Directory.CreateDirectory(_folder);
            foreach (string f in Directory.GetFiles(source)) File.Copy(f, Path.Combine(_folder, Path.GetFileName(f)));
        }

        [TearDown]
        public void Clean()
        {
            Http.NetworkDisabled = false;
            string root = Path.GetDirectoryName(_folder);
            if (root != null && Directory.Exists(root)) Directory.Delete(root, true);
        }

        SiteRequest SameRequest()
        {
            var m = ResortPackage.ReadManifest(_folder);
            return new SiteRequest { Name = m.Site.Name, Centre = new GeoPoint(m.Site.Latitude, m.Site.Longitude), SizeKm = m.Site.SizeMetres / 1000.0 };
        }

        sealed class Sink : IProgress<AcquisitionProgress>
        {
            public readonly List<AcquisitionProgress> Seen = new List<AcquisitionProgress>();
            public void Report(AcquisitionProgress p) { lock (Seen) Seen.Add(p); }
        }

        [Test]
        public async Task AFinishedBuildResumesAtPreparingWithNoNetwork()
        {
            Http.NetworkDisabled = true;   // any download would throw
            var sink = new Sink();
            var cacheRoot = Path.Combine(Path.GetDirectoryName(_folder), "download-cache");
            var manifest = await new AcquisitionPipeline(cacheRoot).RunAsync(SameRequest(), _folder, sink, CancellationToken.None);

            Assert.That(manifest.PackageId, Is.EqualTo(ResortPackage.ReadManifest(_folder).PackageId), "the package as it was built");
            Assert.That(TerrainCache.IsCurrent(_folder, manifest), Is.True, "Preparing ran");
            Assert.That(Directory.Exists(cacheRoot) ? Directory.GetFiles(cacheRoot, "*", SearchOption.AllDirectories) : new string[0], Is.Empty, "nothing was downloaded");
            Assert.That(sink.Seen.Select(p => p.Stage).Where(n => n.Length > 0).Distinct(), Is.EquivalentTo(new[] { AcquisitionPipeline.StagePrepare, "Done" }),
                        "no earlier stage ran");
            var first = sink.Seen.First(p => p.Stage.Length > 0);
            Assert.That(first.Stage, Is.EqualTo(AcquisitionPipeline.StagePrepare), "it starts at Preparing");
            Assert.That(first.StageIndex, Is.EqualTo(AcquisitionPipeline.AllStages.Length), "every earlier stage counts as done");
            Assert.That(first.Overall, Is.GreaterThan(0.7), "the bar starts near the end, not at 0");
            Assert.That(sink.Seen.Last().Finished, Is.True);
        }

        [Test]
        public void OnlyASoundPackageForTheSameRequestCounts()
        {
            var request = SameRequest();
            Assert.That(AcquisitionPipeline.FinishedBuild(request, _folder), Is.Not.Null);

            var renamed = new SiteRequest { Name = "Somewhere Else", Centre = request.Centre, SizeKm = request.SizeKm };
            Assert.That(AcquisitionPipeline.FinishedBuild(renamed, _folder), Is.Null, "another name");
            var bigger = new SiteRequest { Name = request.Name, Centre = request.Centre, SizeKm = 5 };
            Assert.That(AcquisitionPipeline.FinishedBuild(bigger, _folder), Is.Null, "another square");

            // A layer cut short by the kill: the id no longer matches the contents.
            string grid = Path.Combine(_folder, "cover.grid");
            byte[] bytes = File.ReadAllBytes(grid);
            File.WriteAllBytes(grid, bytes.Take(bytes.Length / 2).ToArray());
            Assert.That(AcquisitionPipeline.FinishedBuild(request, _folder), Is.Null, "a damaged layer");

            File.WriteAllText(Path.Combine(_folder, ResortPackage.ManifestFile), "{ \"FormatVersion\": 1, \"Site\": { \"Na");
            Assert.That(AcquisitionPipeline.FinishedBuild(request, _folder), Is.Null, "a half-written manifest");
        }

        [Test]
        public void ATrackerStartedAtALaterStageCountsTheEarlierOnesDone()
        {
            var seen = new List<AcquisitionProgress>();
            using (var tracker = new ProgressTracker(null, new TransferMeter()))
            {
                tracker.DefineStage("A", 1);
                tracker.DefineStage("B", 1);
                tracker.DefineStage("C", 2);
                tracker.BeginStage("C");
                seen.Add(tracker.Snapshot());
                tracker.BeginStep("x", 1, 1, () => 0.5);
                seen.Add(tracker.Snapshot());
            }
            Assert.That(seen[0].Overall, Is.EqualTo(0.5).Within(1e-9));
            Assert.That(seen[1].Overall, Is.EqualTo(0.75).Within(1e-9));
        }
    }
}
