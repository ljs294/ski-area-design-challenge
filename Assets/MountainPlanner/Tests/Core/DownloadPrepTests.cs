using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-16 PR D0, the groundwork for held-up alerts and getting water and roads later: players' server names, the
    /// failing host on a request error, quiet time counted from the last byte, an atomic manifest and re-addable OSM layers.
    /// </summary>
    public sealed class DownloadPrepTests
    {
        [Test]
        public void EveryServerADownloadUsesHasAPlayersName()
        {
            Assert.That(Servers.NameOf(S1mTiles.Bucket), Is.EqualTo(Servers.UsgsElevation));
            Assert.That(Servers.NameOf(Dep3Service.Service), Is.EqualTo(Servers.UsgsElevation));
            Assert.That(Servers.NameOf(WorldCoverTiles.Bucket), Is.EqualTo(Servers.EsaLandCover));
            Assert.That(Servers.NameOf(CanopyTiles.Bucket), Is.EqualTo(Servers.MetaCanopy));
            Assert.That(Servers.NameOf(BigmapService.Service), Is.EqualTo(Servers.UsdaSpecies));
            foreach (string mirror in OsmFeatures.Servers)
                Assert.That(Servers.NameOf(new Uri(mirror).Host), Is.EqualTo(Servers.OpenStreetMap), "a host works as well as a URL");
            Assert.That(Servers.NameOf("example.org"), Is.EqualTo("example.org"), "an unknown host is named as it is");
        }

        [Test]
        public void TheWaitingHintNamesTheServerAndCountsFromTheLastByte()
        {
            double now = 0;
            var meter = new TransferMeter();
            var tracker = new ProgressTracker(null, meter, () => now);
            tracker.DefineStage("Terrain", 1);
            tracker.BeginStage("Terrain");
            tracker.BeginStep("downloading sector", 1, 4, () => 0.5, Servers.UsgsElevation);
            now = 3;
            var quiet = tracker.Snapshot();
            Assert.That(quiet.Detail, Does.Contain("waiting for USGS elevation (3 s)"));
            Assert.That(quiet.Server, Is.EqualTo(Servers.UsgsElevation));
            Assert.That(quiet.QuietSeconds, Is.EqualTo(3).Within(1e-9));

            // A byte arrives: no longer waiting; a stall mid-transfer counts from that byte, not from the step's start.
            meter.AddStreamed(1000);
            now = 4;
            Assert.That(tracker.Snapshot().Detail, Does.Not.Contain("waiting"));
            now = 8;
            var stalled = tracker.Snapshot();
            Assert.That(stalled.QuietSeconds, Is.EqualTo(4).Within(1e-9));
            Assert.That(stalled.Detail, Does.Contain("waiting for USGS elevation (4 s)"));

            // Asking another server starts its quiet time afresh.
            tracker.SetServer(Servers.OpenStreetMap);
            var next = tracker.Snapshot();
            Assert.That(next.Server, Is.EqualTo(Servers.OpenStreetMap));
            Assert.That(next.QuietSeconds, Is.Zero);
            Assert.That(tracker.Snapshot(finished: true).Server, Is.Null);
        }

        [Test]
        public void StepsThatUseNoNetworkNeverWaitOnAServer()
        {
            double now = 0;
            var tracker = new ProgressTracker(null, new TransferMeter(), () => now);
            tracker.DefineStage("Building", 1);
            tracker.BeginStage("Building");
            tracker.BeginStep("compressing heights", 1, 2, () => 0.3);
            now = 60;
            var snapshot = tracker.Snapshot();
            Assert.That(snapshot.Detail, Does.Not.Contain("waiting"));
            Assert.That(snapshot.Server, Is.Null);
        }

        [Test]
        public void AFailedRequestSaysWhichServerItWas()
        {
            // A port nothing listens on: refused straight away. Synchronous: Http never comes back to the caller's thread.
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            string url = $"http://127.0.0.1:{((IPEndPoint)closed.LocalEndpoint).Port}/";
            closed.Stop();
            Exception failure = null;
            try { Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), null, CancellationToken.None, attempts: 1).GetAwaiter().GetResult(); }
            catch (Exception e) { failure = e; }
            Assert.That(failure, Is.InstanceOf<IOException>(), "the type callers classify by is unchanged");
            Assert.That(NetworkFailure.HostOf(failure), Is.EqualTo("127.0.0.1"));
            Assert.That(NetworkFailure.HostOf(new InvalidOperationException("wrapped", failure)), Is.EqualTo("127.0.0.1"), "found down the chain");
            Assert.That(NetworkFailure.HostOf(new IOException("no host")), Is.Null);
            Assert.That(NetworkFailure.HostOf(null), Is.Null);
        }

        /// <summary>PR D: a server that sends its headers and then nothing ends the attempt at the stall limit.</summary>
        [Test]
        public void ABodyThatStopsArrivingEndsTheAttempt()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var held = new System.Collections.Generic.List<TcpClient>();
            var serving = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    lock (held) held.Add(client);
                    var stream = client.GetStream();
                    await stream.ReadAsync(new byte[4096], 0, 4096).ConfigureAwait(false);
                    byte[] start = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\nfirst bytes");
                    await stream.WriteAsync(start, 0, start.Length).ConfigureAwait(false);
                    // ...and nothing more: the connection stays open.
                }
                catch (Exception) { /* stopped */ }
            });
            string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
            var meter = new TransferMeter();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Exception failure = null;
            try
            {
                Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), meter, CancellationToken.None, attempts: 1, stall: TimeSpan.FromSeconds(1))
                    .GetAwaiter().GetResult();
            }
            catch (Exception e) { failure = e; }
            finally
            {
                listener.Stop();
                lock (held) foreach (var c in held) c.Dispose();
            }
            Assert.That(failure, Is.InstanceOf<IOException>());
            Assert.That(failure.InnerException, Is.InstanceOf<TimeoutException>().With.Message.Contains("No data for 1 s"));
            Assert.That(NetworkFailure.IsTimeout(failure), Is.True);
            Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10), "the stall limit, not the 5 min request limit");
            Assert.That(meter.Bytes, Is.GreaterThan(0), "the first bytes arrived");
        }

        [Test]
        public void AServerThatFailsWhileOthersAnswerIsDownNotTheConnection()
        {
            var refused = new IOException("The request failed.", new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));
            refused.Data[NetworkFailure.HostKey] = new Uri(Dep3Service.Service).Host;
            string left = "none";
            Exception Diagnose(Exception e, bool up)
            {
                left = "none";
                return NetworkFailure.DiagnoseAsync(e, (host, ct) => { left = host; return System.Threading.Tasks.Task.FromResult(up); }, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }

            var down = Diagnose(refused, true);
            Assert.That(down, Is.InstanceOf<ServerUnavailableException>());
            Assert.That(((ServerUnavailableException)down).Server, Is.EqualTo(Servers.UsgsElevation));
            Assert.That(left, Is.EqualTo(new Uri(Dep3Service.Service).Host), "the failing server itself isn't asked");
            Assert.That(NetworkFailure.IsNoConnection(down), Is.False, "never read as a lost connection");
            Assert.That(NetworkFailure.RetryAfter(down), Is.EqualTo(NetworkFailure.BusyWaitSeconds));

            Assert.That(Diagnose(refused, false), Is.SameAs(refused), "nothing answers: the connection");
            var stalled = new IOException("The request failed.", new TimeoutException("No data for 60 s."));
            Assert.That(Diagnose(stalled, true), Is.InstanceOf<ServerUnavailableException>(), "a stall is diagnosed too");
            Assert.That(((ServerUnavailableException)Diagnose(stalled, true)).Server, Is.Null, "no host recorded");
            Assert.That(NetworkFailure.RetryAfter(stalled), Is.EqualTo(NetworkFailure.NoConnectionWaitSeconds), "the CLI waits out a stall");

            // Failures that aren't about reaching a server are never diagnosed.
            var notFound = new IOException("The request failed.", new HttpRequestException("404 (Not Found)"));
            Assert.That(Diagnose(notFound, true), Is.SameAs(notFound));
            Assert.That(left, Is.EqualTo("none"), "not even asked");
            var offline = new NetworkDisabledException();
            Assert.That(Diagnose(offline, true), Is.SameAs(offline));

            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Assert.That(() => NetworkFailure.DiagnoseAsync(refused, (h, ct) => System.Threading.Tasks.Task.FromCanceled<bool>(ct), cancelled.Token).GetAwaiter().GetResult(),
                            Throws.InstanceOf<OperationCanceledException>());
            }
        }

        [Test]
        public void SkippingWaterAndRoadsLeavesThemOutWithoutTheNetwork()
        {
            var site = SiteSquare.Create(new AlbersPoint(-1188828, 2381976), 2);
            int fetches = 0;
            string cache = Path.Combine(Path.GetTempPath(), "mp-osm-skip-" + Guid.NewGuid().ToString("N"));
            try
            {
                var osm = new OsmFeatures(new DiskCache(cache), new TransferMeter(), (url, ct) =>
                {
                    Interlocked.Increment(ref fetches);
                    return System.Threading.Tasks.Task.Delay(Timeout.Infinite, ct).ContinueWith<byte[]>(t => throw new OperationCanceledException(ct), System.Threading.Tasks.TaskScheduler.Default);
                });
                using (var skip = new CancellationTokenSource())
                {
                    skip.Cancel();
                    var already = OsmLayers.FetchAsync(osm, site, null, new TransferMeter(), CancellationToken.None, skip.Token).GetAwaiter().GetResult();
                    Assert.That(already.Failure, Is.EqualTo(OsmLayers.SkippedReason));
                    Assert.That(fetches, Is.Zero, "skipped before it began: no request");
                }
                using (var skip = new CancellationTokenSource())
                {
                    var running = System.Threading.Tasks.Task.Run(() => OsmLayers.FetchAsync(osm, site, null, new TransferMeter(), CancellationToken.None, skip.Token));
                    for (int i = 0; i < 500 && Volatile.Read(ref fetches) == 0; i++) Thread.Sleep(10);
                    skip.Cancel();
                    var result = running.GetAwaiter().GetResult();
                    Assert.That(result.Failure, Is.EqualTo(OsmLayers.SkippedReason), "a skip while it waits on Overpass");
                    Assert.That(result.Core, Is.Null);
                }
            }
            finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
        }

        [Test]
        public void TheManifestIsReplacedWholeAndLeavesNoTemporaryFile()
        {
            string folder = Path.Combine(Path.GetTempPath(), "mp-manifest-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var m = new PackageManifest { Site = new SiteInfo { Name = "First", SizeMetres = 2000, RingMetres = 3000 } };
                ResortPackage.WriteManifest(folder, m);
                string first = File.ReadAllText(Path.Combine(folder, ResortPackage.ManifestFile));
                Assert.That(first, Does.EndWith("}\n"), "the same bytes as before: JSON and one newline, no BOM");
                Assert.That(File.ReadAllBytes(Path.Combine(folder, ResortPackage.ManifestFile))[0], Is.EqualTo((byte)'{'));

                m.Site.Name = "Second";
                ResortPackage.WriteManifest(folder, m);
                Assert.That(ResortPackage.ReadManifest(folder).Site.Name, Is.EqualTo("Second"), "an existing manifest is replaced");
                Assert.That(Directory.GetFiles(folder).Select(Path.GetFileName), Is.EquivalentTo(new[] { ResortPackage.ManifestFile }));
            }
            finally { Directory.Delete(folder, true); }
        }

        [Test]
        public void AddingWaterAndRoadsAgainKeepsOneEntryAndOneAttribution()
        {
            var site = SiteSquare.Create(new AlbersPoint(-1188828, 2381976), 2);
            var m = new PackageManifest();
            m.Attribution.Add(OsmLayers.Attribution);
            OsmLayers.Add(m, "", site, new OsmLayers.Result { Failure = "Overpass was down" });
            OsmLayers.Add(m, "", site, new OsmLayers.Result { Failure = "still down" });
            var osm = m.Provenance.Where(p => p.Provider == OsmLayers.Provider).ToList();
            Assert.That(osm, Has.Count.EqualTo(1), "the earlier entry is replaced, not added to");
            Assert.That(osm[0].Layer, Is.EqualTo(OsmLayers.NoneLayer));
            Assert.That(osm[0].Items, Is.EqualTo(new[] { "still down" }));
            Assert.That(m.Attribution.Count(a => a == OsmLayers.Attribution), Is.EqualTo(1));
            Assert.That(OsmLayers.LayerIds, Is.EquivalentTo(new[] { "osm-core", "osm-ring", "roads" }));
        }
    }
}
