using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-16 (BB1): a download hung at 87% because a slow or refusing Overpass mirror looked like a lost connection,
    /// so the optional water-and-roads step made the whole download wait and resume for ever. Now only a real lost
    /// connection waits: every mirror failed to connect and the other map services don't answer either. Anything else
    /// leaves the layer out, and each mirror gets one bounded try.
    /// </summary>
    public sealed class OsmDownloadTests
    {
        static readonly byte[] Result = Encoding.UTF8.GetBytes("{\"elements\":[]}");
        static readonly AlbersBox Box = new AlbersBox(-1_000_000, 2_000_000, -998_000, 2_002_000);
        string _cache = "";

        [SetUp]
        public void SetUp()
        {
            _cache = Path.Combine(Path.GetTempPath(), "mp-osm-test-" + Guid.NewGuid().ToString("N"));
            OsmFeatures.ServerOverride = null;
        }

        [TearDown]
        public void TearDown()
        {
            OsmFeatures.ServerOverride = null;
            if (Directory.Exists(_cache)) Directory.Delete(_cache, true);
        }

        OsmFeatures With(Func<int, Task<byte[]>> answer, List<string> asked = null, Func<CancellationToken, Task<bool>> othersAnswer = null)
        {
            int call = 0;
            return new OsmFeatures(new DiskCache(_cache), new TransferMeter(), (url, ct) =>
            {
                asked?.Add(new Uri(url).Host);
                return answer(call++);
            }, othersAnswer);
        }

        static Task<byte[]> Fail(Exception e) => Task.FromException<byte[]>(e);
        static Exception TimedOut() => new IOException("The request failed.", new TimeoutException("No answer within 75 s."));
        static Exception Refused() => new IOException("The request failed.", new HttpRequestException("refused", new SocketException()));

        /// <summary>What Windows reports after about 21 s when a host never accepts the connection (blocked or unroutable).</summary>
        static Exception NeverConnected() => new IOException("The request failed.",
            new HttpRequestException("A connection attempt failed because the connected party did not properly respond after a period of time.\r\n\0\0\0",
                                     new SocketException((int)SocketError.TimedOut)));

        /// <summary>A reachability check that answers <paramref name="up"/> and counts how often it was asked.</summary>
        static Func<CancellationToken, Task<bool>> Others(bool up, int[] asked) => _ => { asked[0]++; return Task.FromResult(up); };

        [Test]
        public void MirrorsThatTimeOutLeaveTheLayerOutRatherThanWait()
        {
            var asked = new List<string>();
            var osm = With(_ => Fail(TimedOut()), asked);
            var e = Assert.ThrowsAsync<OsmUnavailableException>(() => osm.DownloadAsync(Box, CancellationToken.None));
            Assert.That(asked, Has.Count.EqualTo(OsmFeatures.Servers.Length), "each mirror is tried once");
            Assert.That(NetworkFailure.IsNoConnection(e), Is.False, "not a lost connection: the download goes on");
            Assert.That(NetworkFailure.RetryAfter(e), Is.LessThan(0));
            Assert.That(e.Message, Does.Contain("overpass-api.de"), "the provenance names what each mirror said");
        }

        [Test]
        public void BusyOrRemarkingMirrorsLeaveTheLayerOutToo()
        {
            var osm = With(i => i switch
            {
                0 => Fail(new IOException("The request failed.", new HttpRequestException("Response status code does not indicate success: 504 (Gateway Timeout)."))),
                1 => Task.FromResult(Encoding.UTF8.GetBytes("{\"elements\":[],\"remark\":\"runtime error: Query timed out\"}")),
                _ => Task.FromResult(Encoding.UTF8.GetBytes("<html>rate limited</html>")),
            });
            var e = Assert.ThrowsAsync<OsmUnavailableException>(() => osm.DownloadAsync(Box, CancellationToken.None));
            Assert.That(NetworkFailure.IsServiceBusy(e), Is.False, "Overpass being busy doesn't hold up the download");
        }

        [Test]
        public void OnlyALostConnectionWaitsForTheNetwork()
        {
            var checks = new int[1];
            var osm = With(_ => Fail(Refused()), othersAnswer: Others(false, checks));
            var e = Assert.ThrowsAsync<InvalidOperationException>(() => osm.DownloadAsync(Box, CancellationToken.None));
            Assert.That(e, Is.Not.InstanceOf<OsmUnavailableException>());
            Assert.That(NetworkFailure.IsNoConnection(e), Is.True, "a lost connection waits (P2-06)");
            Assert.That(checks[0], Is.EqualTo(1), "the other map services were asked once");

            // A fake fetch without a check counts the other services as unreachable, so tests never touch the network.
            Assert.That(NetworkFailure.IsNoConnection(Assert.ThrowsAsync<InvalidOperationException>(
                () => With(_ => Fail(Refused())).DownloadAsync(Box, CancellationToken.None))), Is.True);
        }

        /// <summary>
        /// The v0.3.1 hold (2026-11-02): Overpass blocked or unroutable, so no mirror ever accepts the connection, while the
        /// other map services work. That used to wait and resume at 87% for ever; now the layer is left out.
        /// </summary>
        [Test]
        public void MirrorsThatNeverConnectWhileOtherServicesAnswerLeaveTheLayerOut()
        {
            var checks = new int[1];
            var asked = new List<string>();
            var osm = With(_ => Fail(NeverConnected()), asked, Others(true, checks));
            var e = Assert.ThrowsAsync<OsmUnavailableException>(() => osm.DownloadAsync(Box, CancellationToken.None));
            Assert.That(asked, Has.Count.EqualTo(OsmFeatures.Servers.Length), "every mirror was tried");
            Assert.That(checks[0], Is.EqualTo(1));
            Assert.That(NetworkFailure.IsNoConnection(e), Is.False, "not a lost connection: the download goes on");
            Assert.That(e.Message, Does.Contain("other map services answered"), "the provenance says why");
            Assert.That(e.Message.Any(char.IsControl), Is.False, "Windows' NUL padding stays out of the provenance");

            // The -overpass demo's unroutable address, the same way.
            OsmFeatures.ServerOverride = "http://10.255.255.1/api/interpreter";
            Assert.ThrowsAsync<OsmUnavailableException>(() => With(_ => Fail(NeverConnected()), null, Others(true, checks)).DownloadAsync(Box, CancellationToken.None));
        }

        [Test]
        public void TheOtherServicesAreOnlyAskedWhenNoMirrorCouldBeReached()
        {
            // One mirror that connected and timed out, or answered with a remark, means the network is up: no need to ask.
            var checks = new int[1];
            Assert.ThrowsAsync<OsmUnavailableException>(() => With(i => Fail(i == 0 ? TimedOut() : Refused()), null, Others(false, checks)).DownloadAsync(Box, CancellationToken.None));
            Assert.ThrowsAsync<OsmUnavailableException>(() => With(i => i == 0
                ? Task.FromResult(Encoding.UTF8.GetBytes("{\"elements\":[],\"remark\":\"runtime error\"}"))
                : Fail(NeverConnected()), null, Others(false, checks)).DownloadAsync(Box, CancellationToken.None));
            Assert.That(checks[0], Is.Zero);
        }

        [Test]
        public async Task TheFirstRealResultIsUsedAndCached()
        {
            var asked = new List<string>();
            var osm = With(i => i == 0 ? Task.FromResult(Encoding.UTF8.GetBytes("{\"remark\":\"error: busy\"}")) : Task.FromResult(Result), asked);
            Assert.That(await osm.DownloadAsync(Box, CancellationToken.None), Is.EqualTo(Result));
            Assert.That(asked, Has.Count.EqualTo(2));

            var offline = With(_ => Fail(new InvalidOperationException("must not be asked")));
            Assert.That(await offline.DownloadAsync(Box, CancellationToken.None), Is.EqualTo(Result), "a resumed download reads it back");
        }

        [Test]
        public async Task AFailureIsNotCachedSoTheNextDownloadTriesAgain()
        {
            Assert.ThrowsAsync<OsmUnavailableException>(() => With(_ => Fail(TimedOut())).DownloadAsync(Box, CancellationToken.None));
            Assert.That(await With(_ => Task.FromResult(Result)).DownloadAsync(Box, CancellationToken.None), Is.EqualTo(Result));
        }

        [Test]
        public void CancellingAndOfflineModeGoStraightThrough()
        {
            Assert.ThrowsAsync<TaskCanceledException>(() => With(_ => Fail(new TaskCanceledException())).DownloadAsync(Box, CancellationToken.None));
            var asked = new List<string>();
            Assert.ThrowsAsync<NetworkDisabledException>(() => With(_ => Fail(new NetworkDisabledException()), asked).DownloadAsync(Box, CancellationToken.None));
            Assert.That(asked, Has.Count.EqualTo(1), "offline mode stops at the first mirror");
        }

        [Test]
        public void TheOverrideReplacesTheMirrors()
        {
            OsmFeatures.ServerOverride = "http://10.255.255.1/api/interpreter";
            var asked = new List<string>();
            Assert.ThrowsAsync<OsmUnavailableException>(() => With(_ => Fail(TimedOut()), asked).DownloadAsync(Box, CancellationToken.None));
            Assert.That(asked, Is.EqualTo(new[] { "10.255.255.1" }));
        }

        [Test]
        public void TheServerIsAskedToGiveUpBeforeTheMirrorDoes()
        {
            Assert.That(OsmFeatures.Query(43, -110, 43.1, -109.9), Does.StartWith("[out:json][timeout:60];"));
            Assert.That(OsmFeatures.MirrorTimeout.TotalSeconds, Is.GreaterThan(60).And.LessThanOrEqualTo(90));
        }

        /// <summary>
        /// Synchronous on purpose: Unity runs tests on its main thread, so awaiting there, or an async lambda inside
        /// Assert.That, can wait on that thread for ever. <see cref="Reachability"/> never comes back to the caller's
        /// thread, so blocking on it is safe in both runners.
        /// </summary>
        [Test]
        public void AnyAnswerFromAnotherServiceMeansTheNetworkIsUp()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var serving = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        using (var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false))
                        using (var stream = client.GetStream())
                        {
                            await stream.ReadAsync(new byte[4096], 0, 4096).ConfigureAwait(false);
                            byte[] reply = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                            await stream.WriteAsync(reply, 0, reply.Length).ConfigureAwait(false);
                        }
                    }
                }
                catch (Exception) { /* stopped */ }
            });
            // A port nothing listens on: refused straight away.
            var closed = new TcpListener(IPAddress.Loopback, 0);
            closed.Start();
            string nobody = $"http://127.0.0.1:{((IPEndPoint)closed.LocalEndpoint).Port}/";
            closed.Stop();
            try
            {
                string answers = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
                var limit = TimeSpan.FromSeconds(5);
                bool Ask(string except, params string[] services) =>
                    Reachability.OthersAnswerAsync(CancellationToken.None, except, services, limit).GetAwaiter().GetResult();
                Assert.That(Ask(null, nobody, answers), Is.True, "an error status still came over the network");
                Assert.That(Ask(null, nobody), Is.False);
                Assert.That(Ask("127.0.0.1", answers), Is.False,
                            "the failing server's own host doesn't count");
                Assert.That(Reachability.Services, Has.Length.GreaterThanOrEqualTo(3), "two are left after leaving one out");

                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    Assert.That(Caught(Reachability.OthersAnswerAsync(cancelled.Token, services: new[] { answers }, limit: limit)),
                                Is.InstanceOf<OperationCanceledException>(), "pausing isn't 'unreachable'");
                }
                Http.NetworkDisabled = true;
                Assert.That(Caught(Reachability.OthersAnswerAsync(CancellationToken.None, services: new[] { answers }, limit: limit)),
                            Is.InstanceOf<NetworkDisabledException>(), "offline mode stays offline mode");
            }
            finally
            {
                Http.NetworkDisabled = false;
                listener.Stop();
            }
        }

        /// <summary>What a task ended with, waited for without an async lambda (see above).</summary>
        static Exception Caught(Task task)
        {
            try { task.GetAwaiter().GetResult(); return null; }
            catch (Exception e) { return e; }
        }

        /// <summary>A server that accepts the connection and never answers: the per-attempt limit ends each try.</summary>
        [Test]
        public void AnAttemptLimitAndTimeoutBoundASilentServer()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var held = new List<TcpClient>();
            int accepted = 0;
            var accepting = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                        lock (held) held.Add(client);
                        Interlocked.Increment(ref accepted);
                    }
                }
                catch (Exception) { /* stopped */ }
            });
            try
            {
                string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/api/interpreter";
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var e = Assert.ThrowsAsync<IOException>(() => Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), null,
                    CancellationToken.None, attempts: 2, timeout: TimeSpan.FromMilliseconds(400)));
                Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10), "bounded, not HttpClient's 5 minutes");
                Assert.That(e.InnerException, Is.InstanceOf<TimeoutException>());
                Assert.That(NetworkFailure.IsTimeout(e), Is.True);
                Assert.That(Volatile.Read(ref accepted), Is.EqualTo(2), "two attempts");
            }
            finally
            {
                listener.Stop();
                lock (held) foreach (var c in held) c.Dispose();
            }
        }
    }
}
