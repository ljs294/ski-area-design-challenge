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

        /// <summary>How long a response body may send nothing before the attempt fails (task P3-16, BB5).</summary>
        public static TimeSpan StallLimit = TimeSpan.FromSeconds(60);

        /// <summary>
        /// A request with up to <paramref name="attempts"/> attempts (four by default) and exponential backoff; non-success
        /// statuses retry too. A <paramref name="timeout"/> bounds each attempt, body included, and fails it with a
        /// <see cref="TimeoutException"/>, so a slow server is never mistaken for the player cancelling (task P3-16).
        /// Once the headers are in, a body that sends nothing for <paramref name="stall"/> (<see cref="StallLimit"/> by
        /// default) fails the attempt the same way: a server that stops mid-transfer never holds a download for ever.
        /// </summary>
        public static async Task<byte[]> GetBytesAsync(Func<HttpRequestMessage> build, TransferMeter? meter, CancellationToken ct,
                                                       int attempts = 4, TimeSpan? timeout = null, TimeSpan? stall = null)
        {
            if (NetworkDisabled) throw new NetworkDisabledException();
            Exception? last = null;
            string? host = null;
            var quietLimit = stall ?? StallLimit;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0) await Task.Delay(500 * (1 << attempt), ct).ConfigureAwait(false);
                using (var limit = timeout.HasValue ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null)
                {
                    limit?.CancelAfter(timeout!.Value);
                    var token = limit?.Token ?? ct;
                    CancellationTokenSource? quiet = null;
                    try
                    {
                        using (var request = build())
                        using (var response = await SendAsync(request, token, h => host = h).ConfigureAwait(false))
                        {
                            // Re-armed on every read; some runtimes' response streams ignore the token, so closing the
                            // response is what ends a stalled read.
                            quiet = CancellationTokenSource.CreateLinkedTokenSource(token);
                            quiet.CancelAfter(quietLimit);
                            using (quiet.Token.Register(response.Dispose))
                            {
                                response.EnsureSuccessStatusCode();
                                // Count bytes as they stream in, so progress moves during large transfers (U6).
                                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var body = new MemoryStream())
                                {
                                    var buffer = new byte[81920];
                                    int n;
                                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length, quiet.Token).ConfigureAwait(false)) > 0)
                                    {
                                        quiet.CancelAfter(quietLimit);
                                        body.Write(buffer, 0, n);
                                        meter?.AddStreamed(n);
                                    }
                                    quiet.Token.ThrowIfCancellationRequested();
                                    meter?.CompleteRequest();
                                    return body.ToArray();
                                }
                            }
                        }
                    }
                    catch (Exception) when (limit != null && limit.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        last = new TimeoutException($"No answer within {timeout!.Value.TotalSeconds:F0} s.");
                    }
                    catch (Exception) when (quiet != null && quiet.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        last = new TimeoutException($"No data for {quietLimit.TotalSeconds:F0} s.");
                    }
                    catch (HttpRequestException ex) { last = ex; }
                    catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { last = ex; }
                    finally { quiet?.Dispose(); }
                }
            }
            var failed = new IOException(attempts == 1 ? "The request failed." : $"The request failed after {attempts} attempts.", last);
            if (host != null) failed.Data[NetworkFailure.HostKey] = host;   // which server it was (task P3-16, BB5)
            throw failed;
        }

        static Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token, Action<string> asking)
        {
            if (request.RequestUri != null) asking(request.RequestUri.Host);
            return Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
    }

    /// <summary>
    /// One map service isn't answering while the others do (task P3-16, BB5): the player's connection is fine, and the
    /// download waits for that server rather than for the network. It carries no inner exception, so it never reads as a
    /// lost connection.
    /// </summary>
    public sealed class ServerUnavailableException : Exception
    {
        /// <summary>The <see cref="Servers"/> name of the server, or null when the failure didn't say which.</summary>
        public string? Server { get; }
        public string? Host { get; }

        public ServerUnavailableException(string? server, string? host)
            : base((server ?? "A map service") + " isn't answering, though other map services do.")
        {
            Server = server;
            Host = host;
        }
    }

    /// <summary>A request made while <see cref="Http.NetworkDisabled"/> is on (offline mode, or -offline).</summary>
    public sealed class NetworkDisabledException : InvalidOperationException
    {
        public NetworkDisabledException() : base("Network access is disabled.") { }
    }

    /// <summary>What kind of network failure an exception is (task P2-06): the download waits out a lost connection but stops on anything else.</summary>
    public static class NetworkFailure
    {
        /// <summary>The <see cref="Exception.Data"/> key under which a failed request records its server's host.</summary>
        public const string HostKey = "Host";

        /// <summary>The host of the server that failed, if any exception in the chain recorded it.</summary>
        public static string? HostOf(Exception? e)
        {
            for (; e != null; e = e.InnerException)
                if (e.Data[HostKey] is string host) return host;
            return null;
        }

        /// <summary>Offline mode refused the request somewhere in the chain.</summary>
        public static bool IsOfflineMode(Exception? e)
        {
            for (; e != null; e = e.InnerException)
                if (e is NetworkDisabledException) return true;
            return false;
        }

        /// <summary>
        /// The server answered "busy, try later": 429, 502, 503 or 504 (a gateway or the service behind it). USGS's
        /// elevation service does this now and then; it passes.
        /// </summary>
        public static bool IsServiceBusy(Exception? e)
        {
            for (; e != null; e = e.InnerException)
                if (e is HttpRequestException h)
                    foreach (string code in BusyCodes)
                        if (h.Message.IndexOf(code, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        static readonly string[] BusyCodes = { "429", "502", "503", "504" };

        /// <summary>How long to wait before trying again after a lost connection, and after a busy service (P2-06).</summary>
        public const double NoConnectionWaitSeconds = 15, BusyWaitSeconds = 60;

        /// <summary>
        /// Seconds to wait before trying again after <paramref name="e"/>: a busy service or a lost connection passes,
        /// so it's worth waiting; offline mode and every other failure aren't (−1).
        /// </summary>
        public static double RetryAfter(Exception? e) =>
            IsOfflineMode(e) ? -1 : IsServiceBusy(e) || IsServerDown(e) ? BusyWaitSeconds : IsNoConnection(e) || IsTimeout(e) ? NoConnectionWaitSeconds : -1;

        /// <summary>A <see cref="ServerUnavailableException"/> somewhere in the chain.</summary>
        public static bool IsServerDown(Exception? e)
        {
            for (; e != null; e = e.InnerException)
                if (e is ServerUnavailableException) return true;
            return false;
        }

        /// <summary>
        /// Tells a server that is down or stalled from a lost connection (task P3-16, BB5). For a failure that couldn't
        /// connect or ran out of time, asks <paramref name="othersAnswer"/> (given the failing host, to leave out) whether
        /// the other map services answer: if they do, the failure becomes a <see cref="ServerUnavailableException"/>
        /// naming the server; otherwise, and for any other failure, <paramref name="e"/> comes back as it is.
        /// Cancelling <paramref name="ct"/> throws.
        /// </summary>
        public static async Task<Exception> DiagnoseAsync(Exception e, Func<string?, CancellationToken, Task<bool>> othersAnswer, CancellationToken ct)
        {
            if (IsOfflineMode(e) || IsServerDown(e) || !(IsNoConnection(e) || IsTimeout(e))) return e;
            string? host = HostOf(e);
            bool up;
            try { up = await othersAnswer(host, ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { return e; }
            return up ? new ServerUnavailableException(host != null ? Servers.NameOf(host) : null, host) : e;
        }

        /// <summary>
        /// Runs <paramref name="run"/> until it finishes, waiting out a busy service or a lost connection between tries
        /// as the game's downloads do (task P3-01, for the acquire CLI): the resume cache keeps what arrived, so a try
        /// picks up where the last stopped. Any other failure, and cancellation, end it. <paramref name="waiting"/> hears
        /// each wait (the failure, the seconds, the try that failed, from 1).
        /// </summary>
        public static async Task<T> RetryAsync<T>(Func<CancellationToken, Task<T>> run, Action<Exception, double, int>? waiting,
                                                  CancellationToken ct, Func<TimeSpan, CancellationToken, Task>? delay = null)
        {
            delay ??= Task.Delay;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await run(ct).ConfigureAwait(false);
                }
                catch (Exception e) when (!ct.IsCancellationRequested && RetryAfter(e) > 0)
                {
                    double seconds = RetryAfter(e);
                    waiting?.Invoke(e, seconds, attempt);
                    await delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// The request ran out of time: our own per-attempt limit (<see cref="TimeoutException"/>), HttpClient's
        /// (<see cref="TaskCanceledException"/>) or the socket's. The server may simply be slow, so this alone says
        /// nothing about the player's connection (task P3-16).
        /// </summary>
        public static bool IsTimeout(Exception? e)
        {
            for (; e != null; e = e.InnerException)
                if (e is TimeoutException || e is TaskCanceledException
                    || e is System.Net.WebException w && w.Status == System.Net.WebExceptionStatus.Timeout) return true;
            return false;
        }

        /// <summary>
        /// The server couldn't be reached at all, or the connection dropped or timed out: no DNS, no route, a reset
        /// socket. A server that answered with an error status is not this.
        /// </summary>
        public static bool IsNoConnection(Exception? e)
        {
            for (; e != null; e = e.InnerException)
            {
                switch (e)
                {
                    case System.Net.Sockets.SocketException _: return true;
                    case System.Net.WebException w:
                        if (w.Status == System.Net.WebExceptionStatus.NameResolutionFailure || w.Status == System.Net.WebExceptionStatus.ConnectFailure
                            || w.Status == System.Net.WebExceptionStatus.ConnectionClosed || w.Status == System.Net.WebExceptionStatus.ReceiveFailure
                            || w.Status == System.Net.WebExceptionStatus.SendFailure || w.Status == System.Net.WebExceptionStatus.Timeout
                            || w.Status == System.Net.WebExceptionStatus.KeepAliveFailure) return true;
                        break;
                    case TaskCanceledException _: return true;   // HttpClient's timeout; our own cancellation never reaches here as a failure
                    case HttpRequestException h when h.InnerException is IOException: return true;
                }
            }
            return false;
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
