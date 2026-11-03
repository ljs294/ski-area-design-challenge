using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App.Flow;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-16, BB5: a download held up by a server for two minutes tells the player once, names the server, and offers
    /// Try now, Pause for later and, at water and roads, Skip; a server that isn't answering waits longer each time.
    /// </summary>
    public sealed class DownloadHeldUpTests
    {
        static readonly string[] Stages = { "Terrain", "Forest", "Water and roads", "Building" };
        string _root;

        [SetUp]
        public void SetUp() => _root = Path.Combine(Path.GetTempPath(), "mp-held-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        /// <summary>Reports a step whose server has gone quiet, then waits until cancelled; counts its runs.</summary>
        class Stuck : ISiteDownloader
        {
            public int Runs;
            public string Stage = "Terrain", Server = Servers.UsgsElevation;
            public double Overall = 0.3, Quiet = 30;
            public IProgress<DownloadStatus> Progress;
            public CancellationToken Skip;

            public async Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Runs);
                Progress = progress;
                progress.Report(Status(d.Name, 0));
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            }

            public DownloadStatus Status(string name, long downloaded) => new DownloadStatus
            {
                Name = name, Stages = Stages, StageIndex = Array.IndexOf(Stages, Stage) + 1, StageCount = Stages.Length, Stage = Stage,
                Overall = Overall, Downloaded = downloaded, Server = Server, QuietSeconds = Quiet, Detail = Stage + ": waiting",
            };
        }

        /// <summary>The same, able to leave water and roads out, as the real pipeline is.</summary>
        sealed class SkippingStuck : Stuck, ISkippingDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct, CancellationToken skip)
            {
                Skip = skip;
                return DownloadAsync(d, build, progress, ct);
            }
        }

        /// <summary>Fails every try with one server not answering.</summary>
        sealed class Down : ISiteDownloader
        {
            public int Runs;

            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct)
            {
                Interlocked.Increment(ref Runs);
                progress.Report(new DownloadStatus { Name = d.Name, Stages = Stages, StageIndex = 1, StageCount = Stages.Length, Stage = "Terrain", Overall = 0.1 });
                return Task.FromException<string>(new ServerUnavailableException(Servers.UsgsElevation, "elevation.nationalmap.gov"));
            }
        }

        static PendingDownload Site() => DownloadService.Request(
            PickedSite.Create("Crystal Mountain", Albers6350.Forward(new GeoPoint(46.935, -121.474)), 2, false, default), "2026-11-03T10:00:00Z");

        static void PumpUntil(DownloadService s, Func<bool> done)
        {
            for (int i = 0; i < 500 && !done(); i++)
            {
                Thread.Sleep(10);
                s.Pump(0.01);
            }
            Assert.That(done(), Is.True, "timed out");
        }

        /// <summary>Pumps <paramref name="seconds"/> of game time in one-second frames.</summary>
        static void PumpFor(DownloadService s, int seconds)
        {
            for (int i = 0; i < seconds; i++) s.Pump(1);
        }

        [Test]
        public void HeldUpForTwoMinutesTellsThePlayerOnceAndNamesTheServer()
        {
            var stuck = new Stuck();
            var service = new DownloadService(_root, stuck, () => "now");
            var alerts = new List<string>();
            service.HeldUp += alerts.Add;
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);

            PumpFor(service, 118);
            Assert.That(alerts, Is.Empty, "not before two minutes");
            Assert.That(service.View.Held, Is.False);
            PumpFor(service, 3);
            Assert.That(alerts, Has.Count.EqualTo(1));
            Assert.That(alerts[0], Does.Contain("Crystal Mountain's download is held up: USGS elevation isn't answering"));
            Assert.That(service.View.Held, Is.True);
            Assert.That(service.View.HeldLine, Is.EqualTo("USGS elevation hasn't answered for 2 min (1 try)."));
            Assert.That(service.View.BarLabel, Does.StartWith("Held up · USGS elevation"));
            Assert.That(service.View.Pill, Does.Contain("held up · USGS elevation"));
            Assert.That(FlowScreens.PillState(service.View), Is.EqualTo("pill--held"));
            Assert.That(service.View.CanSkip, Is.False, "Terrain has no stand-in");

            PumpFor(service, 70);
            Assert.That(alerts, Has.Count.EqualTo(1), "once per held-up spell");
            Assert.That(service.View.HeldLine, Does.Contain("3 min"));

            // Bytes arrive: it moved, so the card goes back to normal and a new spell starts from nothing.
            stuck.Progress.Report(stuck.Status("Crystal Mountain", 5000));
            service.Pump(0.01);
            Assert.That(service.View.Held, Is.False);
            Assert.That(service.HeldSeconds, Is.LessThan(1));
            Assert.That(FlowScreens.PillState(service.View), Is.EqualTo(""));
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);
        }

        [Test]
        public void OnlyAStepWaitingOnAQuietServerCounts()
        {
            var building = new Stuck { Stage = "Building", Server = null, Quiet = 600 };
            var service = new DownloadService(_root, building, () => "now");
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);
            PumpFor(service, 300);
            Assert.That(service.View.Held, Is.False, "building uses no network");
            Assert.That(service.HeldSeconds, Is.Zero);
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);

            var busy = new Stuck { Quiet = 5 };
            service = new DownloadService(_root, busy, () => "now");
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);
            PumpFor(service, 300);
            Assert.That(service.HeldSeconds, Is.Zero, "a server that sent something 5 s ago is working");
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);
        }

        [Test]
        public void AServerThatIsntAnsweringWaitsLongerEachTime()
        {
            var down = new Down();
            var service = new DownloadService(_root, down, () => "now");
            DownloadProblem waited = null;
            service.WaitStarted += p => waited = p;
            service.Start(Site());
            PumpUntil(service, () => waited != null);
            Assert.That(waited.Kind, Is.EqualTo(ProblemKind.ServerDown));
            Assert.That(waited.Server, Is.EqualTo(Servers.UsgsElevation));
            Assert.That(waited.Title, Is.EqualTo("USGS elevation isn't answering"));
            Assert.That(service.View.Pill, Does.Contain("waiting for USGS elevation"), "not \"waiting for connection\"");
            Assert.That(service.View.Detail, Does.StartWith("USGS elevation isn't answering. Trying again in 60 s"));

            service.Pump(DownloadService.ServerDownWaits[0] - 1);
            Assert.That(down.Runs, Is.EqualTo(1), "not yet");
            service.Pump(1.5);
            PumpUntil(service, () => down.Runs == 2 && service.Waiting);
            Assert.That(service.View.Detail, Does.Contain("120 s"), "the next wait is longer");
            Assert.That(service.View.Held, Is.False, "about a minute so far");
            PumpFor(service, 60);
            Assert.That(down.Runs, Is.EqualTo(2));
            Assert.That(service.View.Held, Is.True, "two minutes held up across the tries");
            Assert.That(service.View.HeldLine, Does.Contain("(2 tries)").And.Contain("Your connection is fine"));
            service.Cancel(keep: false);
        }

        [Test]
        public void OfflineModeIsNeverHeldUp()
        {
            var service = new DownloadService(_root, new OfflineFake(), () => "now");
            DownloadProblem waited = null;
            service.WaitStarted += p => waited = p;
            service.Start(Site());
            PumpUntil(service, () => waited != null);
            Assert.That(waited.Kind, Is.EqualTo(ProblemKind.OfflineMode));
            PumpFor(service, 600);
            Assert.That(service.View.Held, Is.False, "the player chose offline mode");
            service.Cancel(keep: false);
        }

        sealed class OfflineFake : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> progress, CancellationToken ct) =>
                Task.FromException<string>(new NetworkDisabledException());
        }

        [Test]
        public void TryNowStartsAHeldUpStepAgain()
        {
            var stuck = new Stuck();
            var service = new DownloadService(_root, stuck, () => "now");
            bool stopped = false;
            service.Stopped += _ => stopped = true;
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);
            PumpFor(service, 125);
            Assert.That(service.View.Held, Is.True);

            service.TryNow();
            PumpUntil(service, () => stuck.Runs == 2);
            Assert.That(stopped, Is.False, "a new try, not a pause");
            Assert.That(service.Running, Is.True);
            Assert.That(service.Current, Is.Not.Null);
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);
        }

        [Test]
        public void SkipIsOfferedOnlyWhenWaterAndRoadsHoldItUp()
        {
            var osm = new SkippingStuck { Stage = "Water and roads", Server = Servers.OpenStreetMap };
            var service = new DownloadService(_root, osm, () => "now");
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);
            PumpFor(service, 121);
            Assert.That(service.View.Held && service.View.CanSkip, Is.True);
            Assert.That(service.View.HeldLine, Does.StartWith("OpenStreetMap hasn't answered"));

            service.SkipOptional();
            Assert.That(osm.Skip.IsCancellationRequested, Is.True, "the pipeline hears it");
            Assert.That(service.View.Held, Is.False);
            Assert.That(service.CanSkipOptional, Is.False, "once");
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);

            // A plain downloader can't leave anything out, even at water and roads.
            var plain = new Stuck { Stage = "Water and roads", Server = Servers.OpenStreetMap };
            service = new DownloadService(_root, plain, () => "now");
            service.Start(Site());
            PumpUntil(service, () => service.View.StageNames.Count == Stages.Length);
            PumpFor(service, 121);
            Assert.That(service.View.Held, Is.True);
            Assert.That(service.View.CanSkip, Is.False);
            service.Cancel(keep: false);
            PumpUntil(service, () => !service.Active);
        }

        [Test]
        public void FailuresSayWhichServerItWas()
        {
            Assert.That(DownloadErrors.Classify(new ServerUnavailableException(Servers.EsaLandCover, "esa-worldcover.s3.eu-central-1.amazonaws.com")),
                        Is.EqualTo(ProblemKind.ServerDown));
            Assert.That(DownloadErrors.ServerOf(new ServerUnavailableException(Servers.EsaLandCover, null)), Is.EqualTo(Servers.EsaLandCover));
            var failed = new IOException("The request failed.", new TimeoutException("No data for 60 s."));
            failed.Data[NetworkFailure.HostKey] = "prd-tnm.s3.amazonaws.com";
            Assert.That(DownloadErrors.ServerOf(failed), Is.EqualTo(Servers.UsgsElevation));
            Assert.That(DownloadErrors.Classify(failed), Is.EqualTo(ProblemKind.NoConnection), "out of time with nothing else answering: the connection");
            Assert.That(DownloadProblem.Of(ProblemKind.ServerDown, 0, Servers.EsaLandCover).Waits, Is.True);
        }
    }
}
