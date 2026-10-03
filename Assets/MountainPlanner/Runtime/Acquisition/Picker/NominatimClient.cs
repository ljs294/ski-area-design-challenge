#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Picker
{
    /// <summary>One search result: a name, its full address line, a point and its bounding box.</summary>
    public sealed class Place
    {
        public string Name = "";
        public string DisplayName = "";
        public GeoPoint Location;
        public double South, North, West, East;
    }

    /// <summary>
    /// Place search and the name suggestion through OpenStreetMap's Nominatim (0.3 §6), under its usage
    /// policy: the game's identifying User-Agent, at most one request per second (searches and reverse
    /// lookups share the gate), searches only when the player presses Enter (the picker never calls it
    /// as they type), and the attribution shown on the map.
    /// </summary>
    public sealed class NominatimClient
    {
        public const string Endpoint = "https://nominatim.openstreetmap.org";
        public const string Attribution = "Search © OpenStreetMap contributors (Nominatim)";
        public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

        /// <summary>Sends one GET and returns the body. Tests replace it with recorded fixtures.</summary>
        public delegate Task<byte[]> Transport(string url, CancellationToken ct);

        readonly Transport _send;
        readonly RateGate _gate;
        int _reverseGeneration;

        public NominatimClient(Transport? send = null, IPickerClock? clock = null)
        {
            _send = send ?? SendOnceAsync;
            _gate = new RateGate(MinInterval, clock ?? SystemPickerClock.Instance);
        }

        /// <summary>Up to five places in the United States matching <paramref name="query"/>.</summary>
        public async Task<IReadOnlyList<Place>> SearchAsync(string query, CancellationToken ct)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) throw new ArgumentException("Type a place to search for.", nameof(query));
            string url = $"{Endpoint}/search?q={Uri.EscapeDataString(query)}&format=jsonv2&limit=5&countrycodes=us&accept-language=en";
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return ParseSearch(Encoding.UTF8.GetString(await _send(url, ct).ConfigureAwait(false)));
        }

        /// <summary>
        /// A suggested name for a site centred at <paramref name="point"/>, from one reverse lookup, or
        /// null if the lookup found nothing useful. If the player moves the square again while this
        /// call waits its turn, it is dropped without a request (only the newest placement counts).
        /// </summary>
        public async Task<string?> SuggestNameAsync(GeoPoint point, CancellationToken ct)
        {
            int generation = Interlocked.Increment(ref _reverseGeneration);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _reverseGeneration)) return null;
            string url = string.Format(CultureInfo.InvariantCulture,
                "{0}/reverse?lat={1:R}&lon={2:R}&format=jsonv2&zoom=17&accept-language=en", Endpoint, point.Latitude, point.Longitude);
            return SuggestName(Encoding.UTF8.GetString(await _send(url, ct).ConfigureAwait(false)));
        }

        public static IReadOnlyList<Place> ParseSearch(string json)
        {
            var places = new List<Place>();
            foreach (var item in JArray.Parse(json))
            {
                var box = item["boundingbox"] as JArray;
                var p = new Place
                {
                    Name = (string?)item["name"] ?? "",
                    DisplayName = (string?)item["display_name"] ?? "",
                    Location = new GeoPoint(ParseDouble(item["lat"]), ParseDouble(item["lon"])),
                };
                if (p.Name.Length == 0) p.Name = p.DisplayName.Split(',')[0].Trim();
                if (box != null && box.Count == 4)
                {
                    p.South = ParseDouble(box[0]); p.North = ParseDouble(box[1]);
                    p.West = ParseDouble(box[2]); p.East = ParseDouble(box[3]);
                }
                else
                {
                    p.South = p.North = p.Location.Latitude;
                    p.West = p.East = p.Location.Longitude;
                }
                places.Add(p);
            }
            return places;
        }

        // Features worth naming a mountain after, when the lookup lands on one.
        static readonly HashSet<string> NamedCategories = new HashSet<string> { "natural", "leisure", "landuse", "tourism", "place" };
        // Otherwise the nearest settlement or locality in the address, smallest first.
        static readonly string[] AddressKeys = { "peak", "locality", "hamlet", "village", "town", "city", "municipality", "county" };

        /// <summary>The name rule: a named natural or recreation feature, else the nearest settlement, else the county.</summary>
        public static string? SuggestName(string json)
        {
            if (!(JToken.Parse(json) is JObject o)) return null;
            if (o["error"] != null) return null;
            string name = (string?)o["name"] ?? "";
            if (name.Length > 0 && NamedCategories.Contains((string?)o["category"] ?? "")) return name;
            if (o["address"] is JObject address)
                foreach (string key in AddressKeys)
                {
                    string? value = (string?)address[key];
                    if (!string.IsNullOrWhiteSpace(value)) return value!.Trim();
                }
            return name.Length > 0 && (string?)o["category"] != "highway" ? name : null;
        }

        static double ParseDouble(JToken? token) =>
            token == null ? 0 : double.Parse((string)token!, NumberStyles.Float, CultureInfo.InvariantCulture);

        /// <summary>One attempt, no retries: a retry would be a second request the player didn't ask for.</summary>
        static async Task<byte[]> SendOnceAsync(string url, CancellationToken ct)
        {
            if (Http.NetworkDisabled) throw new IOException("Network access is disabled.");
            try
            {
                using (var response = await Http.Client.GetAsync(url, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ct.IsCancellationRequested && !(ex is OperationCanceledException)) { throw new OperationCanceledException("Cancelled.", ex, ct); }
            catch (HttpRequestException ex) { throw new IOException("Search is unavailable.", ex); }
            catch (System.Net.WebException ex) { throw new IOException("Search is unavailable.", ex); }
        }
    }
}
