using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.DataSpike.IO
{
    /// <summary>Random access to the bytes of a file, local or remote.</summary>
    public interface IByteSource
    {
        string Name { get; }
        Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct);
    }

    /// <summary>Request and byte counters shared by the spike's HTTP sources, for the report.</summary>
    public sealed class TransferStats
    {
        long _requests, _bytes;
        public long Requests => Interlocked.Read(ref _requests);
        public long Bytes => Interlocked.Read(ref _bytes);
        public void Add(long bytes) { Interlocked.Increment(ref _requests); Interlocked.Add(ref _bytes, bytes); }
        public void Reset() { Interlocked.Exchange(ref _requests, 0); Interlocked.Exchange(ref _bytes, 0); }
    }

    public static class Http
    {
        public const string UserAgent = "SkiAreaDesignChallenge-DataSpike/0.1 (+https://github.com/ljs294/ski-area-design-challenge)";
        public static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        /// <summary>GET with up to three attempts and exponential backoff.</summary>
        public static async Task<byte[]> GetBytesAsync(Func<HttpRequestMessage> build, TransferStats? stats, CancellationToken ct)
        {
            Exception? last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (attempt > 0) await Task.Delay(500 * (1 << attempt), ct).ConfigureAwait(false);
                try
                {
                    using (var request = build())
                    using (var response = await Client.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        byte[] body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        stats?.Add(body.Length);
                        return body;
                    }
                }
                catch (HttpRequestException ex) { last = ex; }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }
            }
            throw new InvalidOperationException("HTTP request failed after 3 attempts", last);
        }
    }

    /// <summary>A remote file read with HTTP Range requests (S3 and similar).</summary>
    public sealed class HttpRangeSource : IByteSource
    {
        readonly string _url;
        readonly TransferStats? _stats;

        public HttpRangeSource(string url, TransferStats? stats = null) { _url = url; _stats = stats; }
        public string Name => _url;

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            return Http.GetBytesAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, _url);
                request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                return request;
            }, _stats, ct);
        }
    }

    /// <summary>An in-memory file, for service responses and recorded test fixtures.</summary>
    public sealed class MemoryByteSource : IByteSource
    {
        readonly byte[] _data;
        readonly long _baseOffset;

        /// <param name="baseOffset">File offset of <paramref name="data"/>[0], for partial recordings.</param>
        public MemoryByteSource(byte[] data, string name = "memory", long baseOffset = 0) { _data = data; Name = name; _baseOffset = baseOffset; }
        public string Name { get; }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            long start = offset - _baseOffset;
            if (start < 0 || start + length > _data.Length)
                throw new ArgumentOutOfRangeException(nameof(offset), $"{Name}: bytes {offset}+{length} not available");
            var result = new byte[length];
            Buffer.BlockCopy(_data, (int)start, result, 0, length);
            return Task.FromResult(result);
        }
    }

    /// <summary>A recording made of several non-overlapping byte ranges of one file (test fixtures).</summary>
    public sealed class SparseByteSource : IByteSource
    {
        readonly (long Offset, byte[] Data)[] _ranges;
        public SparseByteSource(string name, params (long Offset, byte[] Data)[] ranges) { Name = name; _ranges = ranges; }
        public string Name { get; }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
        {
            foreach (var (start, data) in _ranges)
            {
                if (offset >= start && offset + length <= start + data.Length)
                {
                    var result = new byte[length];
                    Buffer.BlockCopy(data, (int)(offset - start), result, 0, length);
                    return Task.FromResult(result);
                }
            }
            throw new ArgumentOutOfRangeException(nameof(offset), $"{Name}: bytes {offset}+{length} not recorded");
        }
    }
}
