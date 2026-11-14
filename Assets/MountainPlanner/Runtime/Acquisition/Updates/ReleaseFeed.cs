#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Acquisition.Updates
{
    /// <summary>An open asset download: the stream, and whether it starts at the offset asked for (false: from byte 0).</summary>
    public sealed class AssetStream : IDisposable
    {
        public Stream Stream = Stream.Null;
        public bool Resumed;
        /// <summary>The bytes still to come, or -1 when the server didn't say.</summary>
        public long Remaining = -1;
        IDisposable? _owner;

        public AssetStream(Stream stream, bool resumed, long remaining, IDisposable? owner = null)
        {
            Stream = stream;
            Resumed = resumed;
            Remaining = remaining;
            _owner = owner;
        }

        public void Dispose()
        {
            Stream.Dispose();
            _owner?.Dispose();
            _owner = null;
        }
    }

    /// <summary>Where releases come from: GitHub (the game and the launcher), or a local folder (demos and tests).</summary>
    public interface IReleaseFeed
    {
        /// <summary>The newest release, as GitHub's /releases/latest (no drafts, no pre-releases).</summary>
        Task<ReleaseInfo> LatestAsync(CancellationToken ct);
        /// <summary>One release by version (the launcher's --version).</summary>
        Task<ReleaseInfo> TaggedAsync(GameVersion version, CancellationToken ct);
        /// <summary>Every release that can be installed, newest first (the launcher's version picker).</summary>
        Task<List<ReleaseInfo>> AllAsync(CancellationToken ct);
        /// <summary>A small asset's text (the .sha256).</summary>
        Task<string> ReadTextAsync(ReleaseAsset asset, CancellationToken ct);
        /// <summary>An asset from <paramref name="offset"/> on, to resume a download that dropped.</summary>
        Task<AssetStream> OpenAsync(ReleaseAsset asset, long offset, CancellationToken ct);
        /// <summary>For logs: "api.github.com" or the folder.</summary>
        string Name { get; }
    }

    /// <summary>
    /// GitHub Releases for ljs294/ski-area-design-challenge (task 4.23, UP4). It asks api.github.com and downloads from
    /// github.com, which hands release files to its own download host (release-assets.githubusercontent.com; owner OK
    /// 2026-11-13). No token, no cookies and nothing about the player or the PC: only the fixed User-Agent.
    /// </summary>
    public sealed class GitHubFeed : IReleaseFeed
    {
        public const string Repository = "ljs294/ski-area-design-challenge";
        public const string Api = "https://api.github.com/repos/" + Repository + "/releases";
        /// <summary>The same as Acquisition.IO.Http.UserAgent (a Core test keeps them equal); repeated so the launcher needs only this folder.</summary>
        public const string UserAgent = "SkiAreaDesignChallenge/0.1 (+https://github.com/ljs294/ski-area-design-challenge)";

        /// <summary>How long the check may take before the game or launcher carries on without it.</summary>
        public static TimeSpan CheckTimeout = TimeSpan.FromSeconds(5);
        /// <summary>How long a download may send nothing before it fails.</summary>
        public static TimeSpan StallLimit = TimeSpan.FromSeconds(60);

        static readonly Lazy<HttpClient> Client = new Lazy<HttpClient>(() =>
        {
            var handler = new HttpClientHandler { AllowAutoRedirect = true, UseCookies = false };
            var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        });

        public string Name => "api.github.com";

        public Task<ReleaseInfo> LatestAsync(CancellationToken ct) => ReleaseAsync(Api + "/latest", ct);

        public Task<ReleaseInfo> TaggedAsync(GameVersion version, CancellationToken ct) => ReleaseAsync(Api + "/tags/" + version.Tag, ct);

        /// <summary>The newest 30 releases (GitHub's first page); older ones aren't offered.</summary>
        public async Task<List<ReleaseInfo>> AllAsync(CancellationToken ct) =>
            ReleaseInfo.ParseList(await AskAsync(Api + "?per_page=30", ct).ConfigureAwait(false));

        async Task<ReleaseInfo> ReleaseAsync(string url, CancellationToken ct) =>
            ReleaseInfo.Parse(await AskAsync(url, ct).ConfigureAwait(false));

        async Task<string> AskAsync(string url, CancellationToken ct)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(CheckTimeout);
                var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                try
                {
                    using (var response = await Client.Value.SendAsync(request, limit.Token).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException("GitHub has no such release.");
                        if (!response.IsSuccessStatusCode) throw new UpdateException($"GitHub answered {(int)response.StatusCode}.");
                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new UpdateException($"GitHub didn't answer within {CheckTimeout.TotalSeconds:0} s.");
                }
            }
        }

        public async Task<string> ReadTextAsync(ReleaseAsset asset, CancellationToken ct)
        {
            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                limit.CancelAfter(StallLimit);
                using (var response = await Client.Value.GetAsync(asset.Url, limit.Token).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode) throw new UpdateException($"{asset.Name}: GitHub answered {(int)response.StatusCode}.");
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
        }

        public async Task<AssetStream> OpenAsync(ReleaseAsset asset, long offset, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, asset.Url);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            var response = await Client.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new UpdateException($"{asset.Name}: GitHub answered {(int)response.StatusCode}.");
            }
            bool resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            long remaining = response.Content.Headers.ContentLength ?? -1;
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return new AssetStream(stream, resumed, remaining, response);
        }
    }

    /// <summary>
    /// A folder that plays GitHub (demos and tests): <c>latest.json</c> and <c>v0.3.1.json</c> in GitHub's release shape,
    /// with each asset's <c>browser_download_url</c> a file name in the same folder.
    /// </summary>
    public sealed class FolderFeed : IReleaseFeed
    {
        readonly string _folder;

        public FolderFeed(string folder) { _folder = Path.GetFullPath(folder); }

        public string Name => _folder;

        public Task<ReleaseInfo> LatestAsync(CancellationToken ct) => Task.FromResult(Read("latest.json"));

        public Task<ReleaseInfo> TaggedAsync(GameVersion version, CancellationToken ct) => Task.FromResult(Read(version.Tag + ".json"));

        /// <summary>Every vX.Y.Z.json in the folder that can be installed, newest first.</summary>
        public Task<List<ReleaseInfo>> AllAsync(CancellationToken ct)
        {
            if (!Directory.Exists(_folder)) throw new UpdateException($"The feed folder {_folder} doesn't exist.");
            var found = new List<ReleaseInfo>();
            foreach (string file in Directory.GetFiles(_folder, "v*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                ReleaseInfo info;
                try { info = Read(Path.GetFileName(file)); }
                catch (FormatException) { continue; }
                if (!info.Draft && !info.Prerelease && !found.Exists(f => f.Version.CompareTo(info.Version) == 0)) found.Add(info);
            }
            found.Sort((a, b) => b.Version.CompareTo(a.Version));
            return Task.FromResult(found);
        }

        ReleaseInfo Read(string file)
        {
            string path = Path.Combine(_folder, file);
            if (!File.Exists(path)) throw new UpdateException($"The feed has no {file}.");
            var info = ReleaseInfo.Parse(File.ReadAllText(path));
            info.Zip.Url = Local(info.Zip.Url);
            info.Sha.Url = Local(info.Sha.Url);
            return info;
        }

        string Local(string url)
        {
            string name = url.Substring(url.LastIndexOf('/') + 1);
            return Path.Combine(_folder, name);
        }

        public Task<string> ReadTextAsync(ReleaseAsset asset, CancellationToken ct)
        {
            if (!File.Exists(asset.Url)) throw new UpdateException($"The feed has no {asset.Name}.");
            return Task.FromResult(File.ReadAllText(asset.Url));
        }

        public Task<AssetStream> OpenAsync(ReleaseAsset asset, long offset, CancellationToken ct)
        {
            if (!File.Exists(asset.Url)) throw new UpdateException($"The feed has no {asset.Name}.");
            var stream = new FileStream(asset.Url, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            if (offset > 0 && offset < stream.Length) stream.Seek(offset, SeekOrigin.Begin);
            bool resumed = offset > 0 && stream.Position == offset;
            return Task.FromResult(new AssetStream(stream, resumed, stream.Length - stream.Position));
        }
    }

    /// <summary>An update step that failed in a way the player is told about; the installed version is untouched.</summary>
    public sealed class UpdateException : Exception
    {
        public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
    }
}
