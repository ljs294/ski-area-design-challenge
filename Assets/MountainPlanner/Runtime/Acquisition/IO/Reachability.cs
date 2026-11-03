#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.Providers;

namespace MountainPlanner.Acquisition.IO
{
    /// <summary>
    /// Whether the other map services a download uses still answer (task P3-16, BB1): tells a lost connection from one
    /// server being blocked or down. Any HTTP answer counts, even an error status: the network carried it.
    /// </summary>
    public static class Reachability
    {
        /// <summary>The services asked: hosts every download already uses, on different networks.</summary>
        public static readonly string[] Services =
        {
            WorldCoverTiles.Bucket,
            Dep3Service.Service,
            S1mTiles.Bucket,
        };

        /// <summary>How long the services together get to answer.</summary>
        public static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        /// <summary>
        /// True as soon as any of <paramref name="services"/> (by default <see cref="Services"/>, less
        /// <paramref name="exceptHost"/>) answers; false if none does within <paramref name="limit"/>. Offline mode throws
        /// <see cref="NetworkDisabledException"/> and cancelling <paramref name="ct"/> throws, so neither reads as unreachable.
        /// </summary>
        public static async Task<bool> OthersAnswerAsync(CancellationToken ct, string? exceptHost = null,
                                                         IReadOnlyList<string>? services = null, TimeSpan? limit = null)
        {
            if (Http.NetworkDisabled) throw new NetworkDisabledException();
            using (var window = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                window.CancelAfter(limit ?? Limit);
                var asks = (services ?? Services)
                    .Where(url => exceptHost == null || !string.Equals(new Uri(url).Host, exceptHost, StringComparison.OrdinalIgnoreCase))
                    .Select(url => AnswersAsync(url, window.Token)).ToList();
                while (asks.Count > 0)
                {
                    var done = await Task.WhenAny(asks).ConfigureAwait(false);
                    asks.Remove(done);
                    if (await done.ConfigureAwait(false))
                    {
                        window.Cancel();   // the rest needn't finish
                        return true;
                    }
                }
                ct.ThrowIfCancellationRequested();
                return false;
            }
        }

        static async Task<bool> AnswersAsync(string url, CancellationToken token)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, url))
                using (await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
