#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Providers
{
    public enum OsmKind
    {
        Water,
        Developed,
    }

    /// <summary>One OpenStreetMap shape in Albers metres: an area (even-odd rings) or a line with a width.</summary>
    public sealed class OsmShape
    {
        public OsmKind Kind;
        public bool IsArea;
        public double WidthMetres;
        public List<List<AlbersPoint>> Parts = new List<List<AlbersPoint>>();
        public string Tag = "";
    }

    /// <summary>
    /// No Overpass server gave a result, though the network is up (busy, slow, an error page or remark). The layer is
    /// optional, so the download goes on without it (task P3-16). Deliberately carries no inner exception, so nothing
    /// mistakes it for a lost connection.
    /// </summary>
    public sealed class OsmUnavailableException : InvalidOperationException
    {
        public OsmUnavailableException(string message) : base(message) { }
    }

    /// <summary>
    /// Water and developed land from OpenStreetMap (0.3 §4.4), via the Overpass API: lakes, rivers and
    /// streams; buildings, roads, parking and built-up land use. One query per site; the response is
    /// cached on disk, so a resumed or repeated download gives the same package. The same response also gives the
    /// roads themselves, paved and unpaved, as centre lines (<see cref="ParseRoads"/>, task 12d).
    /// Data © OpenStreetMap contributors, ODbL.
    /// </summary>
    public sealed class OsmFeatures
    {
        public static readonly string[] Servers =
        {
            "https://overpass-api.de/api/interpreter",
            "https://overpass.private.coffee/api/interpreter",
            "https://maps.mail.ru/osm/tools/overpass/api/interpreter",
        };

        /// <summary>
        /// Road widths in metres by highway class for the developed raster; unlisted classes (paths, tracks) are skipped
        /// there. The road layer (task 12d) uses <see cref="RoadRules"/>, which adds tracks.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, double> RoadWidths = new Dictionary<string, double>
        {
            ["motorway"] = 20, ["trunk"] = 14, ["primary"] = 11, ["secondary"] = 9, ["tertiary"] = 7.5,
            ["motorway_link"] = 7, ["trunk_link"] = 7, ["primary_link"] = 7, ["secondary_link"] = 6.5, ["tertiary_link"] = 6,
            ["unclassified"] = 6, ["residential"] = 6.5, ["living_street"] = 5, ["service"] = 4.5,
        };

        static readonly HashSet<string> BuiltLanduse = new HashSet<string> { "commercial", "industrial", "retail", "garages", "railway" };

        public static readonly IReadOnlyDictionary<string, double> WaterwayWidths = new Dictionary<string, double>
        {
            ["river"] = 14, ["canal"] = 6, ["stream"] = 2.5,
        };

        /// <summary>
        /// How long one mirror gets to answer, body included (task P3-16). One try each: Overpass is optional, so a
        /// slow or busy mirror costs at most this before the next is tried, and the step at most three times it.
        /// </summary>
        public static readonly TimeSpan MirrorTimeout = TimeSpan.FromSeconds(75);

        /// <summary>Test only (the -overpass flag): one server in place of <see cref="Servers"/>, e.g. one that never answers.</summary>
        public static string? ServerOverride;

        /// <summary>Fetches one Overpass URL. Tests replace it; the game's makes one attempt within <see cref="MirrorTimeout"/>.</summary>
        internal delegate Task<byte[]> Fetch(string url, CancellationToken ct);

        readonly DiskCache _cache;
        readonly TransferMeter _meter;
        readonly Fetch _fetch;
        readonly Func<CancellationToken, Task<bool>> _othersAnswer;

        public OsmFeatures(DiskCache cache, TransferMeter meter) : this(cache, meter, null) { }

        /// <summary>
        /// Tests pass <paramref name="fetch"/>, and <paramref name="othersAnswer"/> for whether the other map services
        /// answer (<see cref="Reachability"/>); a fake fetch without it treats them as unreachable, so tests never touch
        /// the network.
        /// </summary>
        internal OsmFeatures(DiskCache cache, TransferMeter meter, Fetch? fetch, Func<CancellationToken, Task<bool>>? othersAnswer = null)
        {
            _cache = cache;
            _meter = meter;
            _fetch = fetch ?? ((url, ct) => Http.GetBytesAsync(() => new HttpRequestMessage(HttpMethod.Get, url), meter, ct, attempts: 1, timeout: MirrorTimeout));
            _othersAnswer = othersAnswer ?? (fetch == null ? (Func<CancellationToken, Task<bool>>)(ct => Reachability.OthersAnswerAsync(ct)) : _ => Task.FromResult(false));
        }

        public static string Query(double south, double west, double north, double east)
        {
            string b = string.Format(CultureInfo.InvariantCulture, "({0:F5},{1:F5},{2:F5},{3:F5})", south, west, north, east);
            return "[out:json][timeout:60];(" +
                   $"way[natural=water]{b};relation[natural=water]{b};" +
                   $"way[waterway~\"^(riverbank|dock)$\"]{b};" +
                   $"way[landuse~\"^(reservoir|basin)$\"]{b};relation[landuse~\"^(reservoir|basin)$\"]{b};" +
                   $"way[waterway~\"^(river|stream|canal)$\"]{b};" +
                   $"way[highway~\"^(motorway|trunk|primary|secondary|tertiary|unclassified|residential|living_street|service|track)(_link)?$\"]{b};" +
                   $"way[building]{b};relation[building]{b};" +
                   $"way[landuse~\"^(commercial|industrial|retail|garages|railway)$\"]{b};" +
                   $"relation[landuse~\"^(commercial|industrial|retail|garages|railway)$\"]{b};" +
                   $"way[amenity=parking]{b};way[aeroway~\"^(runway|taxiway|apron)$\"]{b};" +
                   ");out geom;";
        }

        /// <summary>
        /// The Overpass response for a box (cached), trying each server in turn. When none gives a result it throws
        /// <see cref="OsmUnavailableException"/>, and the package is built without the layer; only when every server
        /// failed to connect and the other map services don't answer either does it throw a connection failure, so the
        /// download waits for the network (task P3-16: a slow, busy or blocked Overpass used to look like a lost
        /// connection, and the download waited for it for ever).
        /// </summary>
        public async Task<byte[]> DownloadAsync(AlbersBox box, CancellationToken ct)
        {
            var corners = new[] { new AlbersPoint(box.West, box.South), new AlbersPoint(box.West, box.North), new AlbersPoint(box.East, box.South), new AlbersPoint(box.East, box.North) }
                .Select(Albers6350.Inverse).ToList();
            double pad = 0.001;
            string query = Query(corners.Min(p => p.Latitude) - pad, corners.Min(p => p.Longitude) - pad,
                                 corners.Max(p => p.Latitude) + pad, corners.Max(p => p.Longitude) + pad);
            return await _cache.GetOrAddAsync("osm-overpass:" + query, () => FirstResultAsync(query, ct), _meter).ConfigureAwait(false);
        }

        async Task<byte[]> FirstResultAsync(string query, CancellationToken ct)
        {
            var failures = new List<string>();
            Exception? connection = null;
            bool allFailedToConnect = true;
            foreach (string server in ServerOverride != null ? new[] { ServerOverride } : Servers)
            {
                string host = new Uri(server).Host;
                try
                {
                    byte[] body = await _fetch(server + "?data=" + Uri.EscapeDataString(query), ct).ConfigureAwait(false);
                    // Overpass answers errors with 200 and an HTML or "remark" body: only accept real results.
                    var json = JObject.Parse(Encoding.UTF8.GetString(body));
                    if (json["elements"] is JArray && json["remark"] == null) return body;
                    allFailedToConnect = false;
                    failures.Add(host + ": " + (string?)json["remark"]);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException) && !NetworkFailure.IsOfflineMode(ex))
                {
                    // It answered, or connected and then ran out of time: Overpass's trouble, not the player's network.
                    if (NetworkFailure.IsNoConnection(ex) && !NetworkFailure.IsTimeout(ex)) connection ??= ex;
                    else allFailedToConnect = false;
                    failures.Add(host + ": " + Tidy(ex.GetBaseException().Message));
                }
            }
            if (allFailedToConnect && connection != null)
            {
                // Not one mirror could be reached. That's a lost connection only if the other map services are gone too;
                // if they answer, Overpass itself is blocked or down, and the layer is left out (the owner's A1).
                if (!await _othersAnswer(ct).ConfigureAwait(false)) throw new InvalidOperationException("OpenStreetMap (Overpass) is unavailable.", connection);
                failures.Add("couldn't connect to any server, though other map services answered");
            }
            throw new OsmUnavailableException("OpenStreetMap (Overpass) is unavailable (" + string.Join("; ", failures) + ").");
        }

        /// <summary>
        /// An error message fit for the provenance and the quality card: Windows pads socket errors with a line break and
        /// NUL characters, so control characters go and the spaces they leave collapse.
        /// </summary>
        internal static string Tidy(string message) =>
            string.Join(" ", new string(message.Select(c => char.IsControl(c) ? ' ' : c).ToArray())
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>
        /// The roads in an Overpass response (task 12d): every highway way <see cref="RoadRules.IsRoad"/> keeps, above
        /// ground, with its surface, width (OSM `width` when sensible) and name. Tunnels and road areas are left out.
        /// </summary>
        public static List<Road> ParseRoads(byte[] response)
        {
            var roads = new List<Road>();
            var root = JObject.Parse(Encoding.UTF8.GetString(response));
            foreach (var e in (JArray?)root["elements"] ?? new JArray())
            {
                if ((string?)e["type"] != "way" || !(e["tags"] is JObject tags)) continue;
                string Tag(string key) => (string?)tags[key] ?? "";
                string highway = Tag("highway");
                if (!RoadRules.IsRoad(highway) || Tag("area") == "yes") continue;
                if (Tag("tunnel") is string tunnel && tunnel != "" && tunnel != "no") continue;
                if (Tag("location") == "underground") continue;
                var points = Points(e["geometry"] as JArray);
                if (points.Count < 2) continue;
                roads.Add(new Road
                {
                    Class = highway, Surface = RoadRules.SurfaceOf(highway, Tag("surface")),
                    WidthMetres = ParseWidth(Tag("width"), RoadRules.Widths[highway]), Name = Tag("name"), Points = points,
                });
            }
            return roads;
        }

        /// <summary>Turns an Overpass JSON response into water and developed shapes.</summary>
        public static List<OsmShape> Parse(byte[] response)
        {
            var shapes = new List<OsmShape>();
            var root = JObject.Parse(Encoding.UTF8.GetString(response));
            foreach (var e in (JArray?)root["elements"] ?? new JArray())
            {
                var tags = e["tags"] as JObject;
                if (tags == null) continue;
                string Tag(string key) => (string?)tags[key] ?? "";
                if (Tag("tunnel") is string tunnel && tunnel != "" && tunnel != "no") continue;
                if (Tag("location") == "underground") continue;

                string type = (string?)e["type"] ?? "";
                var shape = Classify(Tag);
                if (shape == null) continue;
                if (type == "way")
                {
                    var line = Points(e["geometry"] as JArray);
                    if (line.Count < 2) continue;
                    if (shape.IsArea && (line.Count < 4 || !line[0].Equals(line[line.Count - 1]))) continue; // an unclosed area is bad data
                    shape.Parts.Add(line);
                }
                else if (type == "relation")
                {
                    if (!shape.IsArea) continue;
                    foreach (var member in (JArray?)e["members"] ?? new JArray())
                    {
                        string role = (string?)member["role"] ?? "";
                        if ((string?)member["type"] != "way" || (role != "outer" && role != "inner")) continue;
                        var part = Points(member["geometry"] as JArray);
                        if (part.Count >= 2) shape.Parts.Add(part);
                    }
                    // Multipolygon members may be open pieces of a ring; even-odd filling only needs the
                    // pieces to close up as a whole, so join them into one edge soup ring by ring.
                    shape.Parts = JoinOpenParts(shape.Parts);
                }
                if (shape.Parts.Count > 0) shapes.Add(shape);
            }
            return shapes;
        }

        static OsmShape? Classify(Func<string, string> tag)
        {
            if (tag("natural") == "water" || tag("waterway") == "riverbank" || tag("waterway") == "dock" ||
                tag("landuse") == "reservoir" || tag("landuse") == "basin")
                return new OsmShape { Kind = OsmKind.Water, IsArea = true, Tag = "water" };
            if (WaterwayWidths.TryGetValue(tag("waterway"), out double ww))
                return new OsmShape { Kind = OsmKind.Water, IsArea = false, WidthMetres = ParseWidth(tag("width"), ww), Tag = "waterway=" + tag("waterway") };
            if (RoadWidths.TryGetValue(tag("highway"), out double rw))
            {
                if (tag("area") == "yes") return null;
                return new OsmShape { Kind = OsmKind.Developed, IsArea = false, WidthMetres = ParseWidth(tag("width"), rw), Tag = "highway=" + tag("highway") };
            }
            if (tag("aeroway") == "runway") return new OsmShape { Kind = OsmKind.Developed, IsArea = false, WidthMetres = ParseWidth(tag("width"), 30), Tag = "aeroway=runway" };
            if (tag("aeroway") == "taxiway") return new OsmShape { Kind = OsmKind.Developed, IsArea = false, WidthMetres = ParseWidth(tag("width"), 15), Tag = "aeroway=taxiway" };
            if (tag("building") != "" && tag("building") != "no") return new OsmShape { Kind = OsmKind.Developed, IsArea = true, Tag = "building" };
            // Residential land use is left out: it outlines whole subdivisions and ranches, mostly yards and trees.
            if (tag("amenity") == "parking" || tag("aeroway") == "apron" || BuiltLanduse.Contains(tag("landuse")))
                return new OsmShape { Kind = OsmKind.Developed, IsArea = true, Tag = tag("landuse") != "" ? "landuse=" + tag("landuse") : "parking" };
            return null;
        }

        static double ParseWidth(string text, double fallback)
        {
            string t = text.Replace("m", "").Trim();
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0.5 && w < 200 ? w : fallback;
        }

        static List<AlbersPoint> Points(JArray? geometry)
        {
            var points = new List<AlbersPoint>();
            if (geometry == null) return points;
            foreach (var g in geometry)
            {
                if (g.Type != JTokenType.Object) continue;
                double lat = (double)g["lat"]!, lon = (double)g["lon"]!;
                points.Add(Albers6350.Forward(new GeoPoint(lat, lon)));
            }
            return points;
        }

        /// <summary>Joins open member ways that share end points into longer chains (closed rings where possible).</summary>
        static List<List<AlbersPoint>> JoinOpenParts(List<List<AlbersPoint>> parts)
        {
            var closed = parts.Where(p => p.Count >= 4 && p[0].Equals(p[p.Count - 1])).ToList();
            var open = parts.Where(p => !(p.Count >= 4 && p[0].Equals(p[p.Count - 1]))).Select(p => new List<AlbersPoint>(p)).ToList();
            while (open.Count > 0)
            {
                var chain = open[0];
                open.RemoveAt(0);
                bool grew = true;
                while (grew && !chain[0].Equals(chain[chain.Count - 1]))
                {
                    grew = false;
                    for (int i = 0; i < open.Count; i++)
                    {
                        var p = open[i];
                        if (p[0].Equals(chain[chain.Count - 1])) chain.AddRange(p.Skip(1));
                        else if (p[p.Count - 1].Equals(chain[chain.Count - 1])) chain.AddRange(Enumerable.Reverse(p).Skip(1));
                        else if (p[p.Count - 1].Equals(chain[0])) chain.InsertRange(0, p.Take(p.Count - 1));
                        else if (p[0].Equals(chain[0])) chain.InsertRange(0, Enumerable.Reverse(p).Take(p.Count - 1));
                        else continue;
                        open.RemoveAt(i);
                        grew = true;
                        break;
                    }
                }
                closed.Add(chain);
            }
            return closed;
        }

        /// <summary>Rasterizes shapes onto a grid: band 0 water, band 1 developed (two separate arrays).</summary>
        public static (byte[] Water, byte[] Developed) Rasterize(IReadOnlyList<OsmShape> shapes, GridSpec grid)
        {
            var water = new byte[grid.CellCount];
            var developed = new byte[grid.CellCount];
            var box = grid.Bounds;
            foreach (var s in shapes)
            {
                if (!Touches(s, box)) continue;
                var target = s.Kind == OsmKind.Water ? water : developed;
                if (s.IsArea) VectorRaster.FillPolygon(target, grid, s.Parts);
                else foreach (var part in s.Parts) VectorRaster.StrokeLine(target, grid, part, s.WidthMetres);
            }
            return (water, developed);
        }

        static bool Touches(OsmShape s, AlbersBox box)
        {
            double pad = s.WidthMetres;
            foreach (var part in s.Parts)
                foreach (var p in part)
                    if (p.X >= box.West - pad && p.X <= box.East + pad && p.Y >= box.South - pad && p.Y <= box.North + pad) return true;
            // A huge area could contain the box without a vertex inside it; test the box centre crossing count.
            return s.IsArea && s.Parts.Sum(part => part.Count) > 0 && ContainsPoint(s.Parts, new AlbersPoint((box.West + box.East) / 2, (box.South + box.North) / 2));
        }

        static bool ContainsPoint(List<List<AlbersPoint>> rings, AlbersPoint p)
        {
            bool inside = false;
            foreach (var ring in rings)
                for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
                    if ((ring[i].Y > p.Y) != (ring[j].Y > p.Y) &&
                        p.X < (ring[j].X - ring[i].X) * (p.Y - ring[i].Y) / (ring[j].Y - ring[i].Y) + ring[i].X)
                        inside = !inside;
            return inside;
        }

        /// <summary>Interleaves water and developed coverage into one two-band layer (water first).</summary>
        public static byte[] Interleave(byte[] water, byte[] developed)
        {
            var result = new byte[water.Length * 2];
            for (int i = 0; i < water.Length; i++)
            {
                result[2 * i] = water[i];
                result[2 * i + 1] = developed[i];
            }
            return result;
        }
    }
}
