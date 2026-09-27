#nullable enable
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Acquisition.IO
{
    /// <summary>Random access to the bytes of a file, local or remote.</summary>
    public interface IByteSource
    {
        string Name { get; }
        Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct);
    }

    /// <summary>Counts bytes and requests as they arrive, for progress (U6). Thread-safe.</summary>
    public sealed class TransferMeter
    {
        long _requests, _bytes, _cachedBytes;
        public long Requests => Interlocked.Read(ref _requests);
        /// <summary>Bytes delivered, whether downloaded or served from the resume cache.</summary>
        public long Bytes => Interlocked.Read(ref _bytes);
        /// <summary>The part of <see cref="Bytes"/> that came from the resume cache.</summary>
        public long CachedBytes => Interlocked.Read(ref _cachedBytes);
        public long DownloadedBytes => Bytes - CachedBytes;

        public void AddDownloaded(long bytes) { Interlocked.Increment(ref _requests); Interlocked.Add(ref _bytes, bytes); }
        public void AddStreamed(long bytes) => Interlocked.Add(ref _bytes, bytes);
        public void CompleteRequest() => Interlocked.Increment(ref _requests);
        public void AddCached(long bytes) { Interlocked.Add(ref _bytes, bytes); Interlocked.Add(ref _cachedBytes, bytes); }
    }

    public static class Http
    {
        public const string UserAgent = "SkiAreaDesignChallenge/0.1 (+https://github.com/ljs294/ski-area-design-challenge)";
        public static readonly HttpClient Client = CreateClient();

        static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        /// <summary>
        /// Blocks every request when true. Tests set it to prove that opening a resort never touches the
        /// network (task 06: the game plays offline after the first download).
        /// </summary>
        public static bool NetworkDisabled;

        /// <summary>A request with up to four attempts and exponential backoff; non-success statuses retry too.</summary>
        public static async Task<byte[]> GetBytesAsync(Func<HttpRequestMessage> build, TransferMeter? meter, CancellationToken ct)
        {
            if (NetworkDisabled) throw new InvalidOperationException("Network access is disabled.");
            Exception? last = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (attempt > 0) await Task.Delay(500 * (1 << attempt), ct).ConfigureAwait(false);
                try
                {
                    using (var request = build())
                    using (var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        // Count bytes as they stream in, so progress moves during large transfers (U6).
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var body = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            int n;
                            while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                            {
                                body.Write(buffer, 0, n);
                                meter?.AddStreamed(n);
                            }
                            meter?.CompleteRequest();
                            return body.ToArray();
                        }
                    }
                }
                catch (HttpRequestException ex) { last = ex; }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }
            }
            throw new IOException("The request failed after 4 attempts.", last);
        }
    }

    /// <summary>A remote file read with HTTP range requests (S3 and similar).</summary>
    public sealed class HttpRangeSource : IByteSource
    {
        readonly TransferMeter? _meter;

        public HttpRangeSource(string url, TransferMeter? meter = null)
        {
            Name = url;
            _meter = meter;
        }

        public string Name { get; }

        public Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct) =>
            Http.GetBytesAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Name);
                request.Headers.Range = new RangeHeaderValue(offset, offset + length - 1);
                return request;
            }, _meter, ct);
    }

    /// <summary>
    /// The resume cache: every byte range fetched from a remote file is kept on disk, so an interrupted
    /// download continues where it stopped and a re-run makes no requests at all (0.3 §6). Files are
    /// written to a temporary name and renamed, so a kill never leaves a torn entry.
    /// </summary>
    public sealed class DiskCache
    {
        readonly string _root;

        public DiskCache(string root)
        {
            _root = root;
            Directory.CreateDirectory(root);
        }

        public string Root => _root;

        public IByteSource Wrap(IByteSource inner, TransferMeter? meter = null) => new Cached(this, inner, meter);

        /// <summary>A cached whole-response download (service exports), keyed by its request.</summary>
        public async Task<byte[]> GetOrAddAsync(string key, Func<Task<byte[]>> fetch, TransferMeter? meter)
        {
            string path = PathFor(key, "response");
            if (File.Exists(path))
            {
                byte[] hit = File.ReadAllBytes(path);
                meter?.AddCached(hit.Length);
                return hit;
            }
            byte[] data = await fetch().ConfigureAwait(false);
            Write(path, data);
            return data;
        }

        string PathFor(string name, string part)
        {
            string dir = Path.Combine(_root, Hash(name).Substring(0, 2), Hash(name));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, part + ".bin");
        }

        static void Write(string path, byte[] data)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, data);
            if (File.Exists(path)) File.Delete(temp);
            else File.Move(temp, path);
        }

        static string Hash(string text)
        {
            using (var sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var sb = new StringBuilder(40);
                for (int i = 0; i < 20; i++) sb.Append(h[i].ToString("x2"));
                return sb.ToString();
            }
        }

        sealed class Cached : IByteSource
        {
            readonly DiskCache _cache;
            readonly IByteSource _inner;
            readonly TransferMeter? _meter;

            public Cached(DiskCache cache, IByteSource inner, TransferMeter? meter)
            {
                _cache = cache;
                _inner = inner;
                _meter = meter;
            }

            public string Name => _inner.Name;

            public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken ct)
            {
                string path = _cache.PathFor(_inner.Name, offset + "-" + length);
                if (File.Exists(path))
                {
                    byte[] hit = File.ReadAllBytes(path);
                    if (hit.Length == length)
                    {
                        _meter?.AddCached(hit.Length);
                        return hit;
                    }
                }
                byte[] data = await _inner.ReadAsync(offset, length, ct).ConfigureAwait(false);
                Write(path, data);
                return data;
            }
        }
    }

    /// <summary>An in-memory file, for service responses and recorded test fixtures.</summary>
    public sealed class MemoryByteSource : IByteSource
    {
        readonly byte[] _data;
        readonly long _baseOffset;

        /// <param name="baseOffset">File offset of <paramref name="data"/>[0], for partial recordings.</param>
        public MemoryByteSource(byte[] data, string name = "memory", long baseOffset = 0)
        {
            _data = data;
            Name = name;
            _baseOffset = baseOffset;
        }

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

        public SparseByteSource(string name, params (long Offset, byte[] Data)[] ranges)
        {
            Name = name;
            _ranges = ranges;
        }

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
