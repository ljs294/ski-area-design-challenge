#nullable enable
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// Where the weather sources' answers come from (WX-10, task 4.05): the services themselves, through the download cache,
    /// or a recorded folder (the fixtures under <c>TestData/weather/</c>). Each answer has a stable <c>key</c> (a relative
    /// path such as <c>daymet/ref.csv</c>), so a recording replays exactly what the live run saw, whoever fetched it.
    /// </summary>
    public interface IWeatherResponses
    {
        /// <summary>The answer for <paramref name="key"/>; <paramref name="url"/> is what a live source asks.</summary>
        Task<byte[]> GetAsync(string key, string url, CancellationToken ct);
    }

    /// <summary>A service answered with an error the request can't fix (outside Daymet, a station with no data).</summary>
    public sealed class WeatherSourceRefusedException : InvalidOperationException
    {
        public WeatherSourceRefusedException(string message) : base(message) { }
    }

    /// <summary>The services, through <see cref="DiskCache"/> so an interrupted download resumes (U6).</summary>
    public sealed class LiveWeatherResponses : IWeatherResponses
    {
        readonly DiskCache _cache;
        readonly TransferMeter? _meter;

        public LiveWeatherResponses(DiskCache cache, TransferMeter? meter)
        {
            _cache = cache;
            _meter = meter;
        }

        public Task<byte[]> GetAsync(string key, string url, CancellationToken ct) =>
            _cache.GetOrAddAsync("weather:" + url, () => FetchAsync(url, ct), _meter);

        async Task<byte[]> FetchAsync(string url, CancellationToken ct)
        {
            try
            {
                return await Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), _meter, ct,
                                                timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            }
            catch (IOException ex) when (ex.InnerException is HttpRequestException h && IsRefusal(h))
            {
                // Daymet answers 400 outside its tiles; retrying can't help, and the caller decides (WX-13).
                throw new WeatherSourceRefusedException(h.Message);
            }
        }

        static bool IsRefusal(HttpRequestException ex) =>
            ex.Message.Contains("400") || ex.Message.Contains("404") || ex.Message.Contains("422");
    }

    /// <summary>
    /// A recorded folder: <c>&lt;root&gt;/&lt;key&gt;</c>, or <c>&lt;key&gt;.gz</c> (the fixtures are stored gzipped). A key the
    /// recording lacks is a refusal, as the live service would have refused it.
    /// </summary>
    public sealed class FolderWeatherResponses : IWeatherResponses
    {
        readonly string _root;

        public FolderWeatherResponses(string root) => _root = root;

        public string Root => _root;

        public Task<byte[]> GetAsync(string key, string url, CancellationToken ct)
        {
            string path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) return Task.FromResult(File.ReadAllBytes(path));
            if (File.Exists(path + ".gz")) return Task.FromResult(Gunzip(File.ReadAllBytes(path + ".gz")));
            if (File.Exists(path + ".refused")) throw new WeatherSourceRefusedException(File.ReadAllText(path + ".refused"));
            throw new WeatherSourceRefusedException($"The recording has no {key}.");
        }

        public static byte[] Gunzip(byte[] gz)
        {
            using (var input = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                input.CopyTo(output);
                return output.ToArray();
            }
        }
    }

    /// <summary>Passes every answer through and writes it, gzipped, under a folder: how the fixtures are recorded.</summary>
    public sealed class RecordingWeatherResponses : IWeatherResponses
    {
        readonly IWeatherResponses _inner;
        readonly string _root;

        public RecordingWeatherResponses(IWeatherResponses inner, string root)
        {
            _inner = inner;
            _root = root;
        }

        public async Task<byte[]> GetAsync(string key, string url, CancellationToken ct)
        {
            string path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            try
            {
                byte[] data = await _inner.GetAsync(key, url, ct).ConfigureAwait(false);
                using (var file = File.Create(path + ".gz"))
                using (var gz = new GZipStream(file, CompressionLevel.Optimal))
                    gz.Write(data, 0, data.Length);
                return data;
            }
            catch (WeatherSourceRefusedException ex)
            {
                File.WriteAllText(path + ".refused", ex.Message);
                throw;
            }
        }
    }
}
