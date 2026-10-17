using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-01 (P2-12 tester finding 1): the acquire CLI waits out a busy USGS service or a dropped connection and
    /// tries again, as the game's downloads do (P2-06), instead of stopping after Http's four quick tries.
    /// </summary>
    public sealed class NetworkRetryTests
    {
        static HttpRequestException Status(int code) =>
            new HttpRequestException($"Response status code does not indicate success: {code} (Bad Gateway).");

        [Test]
        public void BusyServicesAndLostConnectionsAreWorthWaitingFor()
        {
            Assert.That(NetworkFailure.RetryAfter(Status(502)), Is.EqualTo(NetworkFailure.BusyWaitSeconds));
            Assert.That(NetworkFailure.RetryAfter(new InvalidOperationException("tile", Status(503))), Is.EqualTo(NetworkFailure.BusyWaitSeconds), "wrapped");
            Assert.That(NetworkFailure.RetryAfter(new HttpRequestException("no route", new SocketException())), Is.EqualTo(NetworkFailure.NoConnectionWaitSeconds));
            Assert.That(NetworkFailure.RetryAfter(Status(404)), Is.LessThan(0), "not found doesn't pass");
            Assert.That(NetworkFailure.RetryAfter(new NetworkDisabledException()), Is.LessThan(0), "offline mode is the player's choice");
            Assert.That(NetworkFailure.RetryAfter(new System.IO.InvalidDataException()), Is.LessThan(0));
        }

        [Test]
        public async Task ItWaitsAndTriesAgainUntilTheRunFinishes()
        {
            var waits = new List<double>();
            var heard = new List<int>();
            int tries = 0;
            int result = await NetworkFailure.RetryAsync(_ =>
            {
                tries++;
                if (tries == 1) throw Status(502);
                if (tries == 2) throw new HttpRequestException("reset", new SocketException());
                return Task.FromResult(42);
            }, (e, seconds, attempt) => heard.Add(attempt), CancellationToken.None,
            (wait, _) => { waits.Add(wait.TotalSeconds); return Task.CompletedTask; });

            Assert.That(result, Is.EqualTo(42));
            Assert.That(tries, Is.EqualTo(3));
            Assert.That(waits, Is.EqualTo(new[] { NetworkFailure.BusyWaitSeconds, NetworkFailure.NoConnectionWaitSeconds }));
            Assert.That(heard, Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void OtherFailuresAndCancellationEndIt()
        {
            int tries = 0;
            Assert.ThrowsAsync<HttpRequestException>(() => NetworkFailure.RetryAsync<int>(_ => { tries++; throw Status(404); },
                null, CancellationToken.None, (w, c) => Task.CompletedTask));
            Assert.That(tries, Is.EqualTo(1), "a 404 isn't tried again");

            using var cts = new CancellationTokenSource();
            tries = 0;
            Assert.ThrowsAsync<TaskCanceledException>(() => NetworkFailure.RetryAsync<int>(_ => { tries++; throw Status(502); },
                null, cts.Token, (w, c) => { cts.Cancel(); return Task.FromCanceled(c); }));
            Assert.That(tries, Is.EqualTo(1), "Ctrl+C during the wait stops it");
        }
    }
}
