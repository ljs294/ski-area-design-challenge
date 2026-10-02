#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;

namespace MountainPlanner.Acquisition.Picker
{
    public enum BaseMap { Topo, Imagery }

    /// <summary>
    /// USGS National Map basemap tiles for the picker (0.3 §6, public domain): 256 px XYZ tiles, kept
    /// in memory only (the most recent <see cref="Capacity"/>), at most <see cref="Parallel"/> requests
    /// at a time, and only for tiles the map is showing.
    /// </summary>
    public sealed class MapTileSource
    {
        public const string Attribution = "Map: USGS The National Map";
        public const int MinZoom = 4;
        public const int MaxZoom = 16;
        public const int Capacity = 384;
        public const int Parallel = 4;

        public static string Url(BaseMap map, int zoom, int x, int y) =>
            $"https://basemap.nationalmap.gov/arcgis/rest/services/{(map == BaseMap.Topo ? "USGSTopo" : "USGSImageryOnly")}/MapServer/tile/{zoom}/{y}/{x}";

        readonly NominatimClient.Transport _send;
        readonly SemaphoreSlim _parallel = new SemaphoreSlim(Parallel);
        readonly Dictionary<string, LinkedListNode<(string Key, byte[]? Bytes)>> _index = new Dictionary<string, LinkedListNode<(string, byte[]?)>>();
        readonly LinkedList<(string Key, byte[]? Bytes)> _recent = new LinkedList<(string, byte[]?)>();
        readonly object _lock = new object();

        public MapTileSource(NominatimClient.Transport? send = null)
        {
            _send = send ?? GetAsync;
        }

        /// <summary>A tile's bytes from memory, if it was fetched recently. Null bytes mean "no tile there".</summary>
        public bool TryGetCached(BaseMap map, int zoom, int x, int y, out byte[]? bytes)
        {
            lock (_lock)
            {
                if (_index.TryGetValue(Url(map, zoom, x, y), out var node))
                {
                    _recent.Remove(node);
                    _recent.AddFirst(node);
                    bytes = node.Value.Bytes;
                    return true;
                }
            }
            bytes = null;
            return false;
        }

        /// <summary>The tile's JPEG or PNG bytes, or null where USGS has no tile. Throws <see cref="IOException"/> when offline.</summary>
        public async Task<byte[]?> FetchAsync(BaseMap map, int zoom, int x, int y, CancellationToken ct)
        {
            if (TryGetCached(map, zoom, x, y, out var hit)) return hit;
            string url = Url(map, zoom, x, y);
            byte[]? bytes;
            await _parallel.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bytes = await _send(url, ct).ConfigureAwait(false);
            }
            finally
            {
                _parallel.Release();
            }
            if (bytes != null && bytes.Length == 0) bytes = null;
            lock (_lock)
            {
                if (!_index.ContainsKey(url))
                {
                    _index[url] = _recent.AddFirst((url, bytes));
                    while (_recent.Count > Capacity)
                    {
                        _index.Remove(_recent.Last!.Value.Key);
                        _recent.RemoveLast();
                    }
                }
            }
            return bytes;
        }

        /// <summary>One GET; a 404 is an empty tile (returned as no bytes), any other failure is offline.</summary>
        internal static async Task<byte[]> GetAsync(string url, CancellationToken ct)
        {
            if (Http.NetworkDisabled) throw new IOException("Network access is disabled.");
            try
            {
                using (var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound) return Array.Empty<byte>();
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex) { throw new IOException("The map is unavailable.", ex); }
        }
    }
}
