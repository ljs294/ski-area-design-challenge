#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Weather
{
    public sealed class SnotelStation
    {
        public string Triplet = "", Name = "";
        public double Latitude, Longitude, ElevationFeet;
        public string BeginDate = "", EndDate = "";
        /// <summary>Metres from the site's centre, filled when chosen.</summary>
        public double DistanceMetres;
        /// <summary>The GHCN-Daily id when NOAA's station search lists this station, so Daymet may already use it (QC note).</summary>
        public string GhcnId = "";
    }

    /// <summary>
    /// A SNOTEL station's daily snow water equivalent (WTEQ) and water-year accumulated precipitation (PREC), in inches, with
    /// NRCS's own flags per value (<c>qcFlag</c>, <c>qaFlag</c>) kept beside each, so the screen (QC) can read them. A day the
    /// service doesn't list is absent; a listed day without a value is NaN.
    /// </summary>
    public sealed class SnotelSeries
    {
        public SnotelStation Station = new SnotelStation();
        public readonly SortedDictionary<int, SnotelDay> Days = new SortedDictionary<int, SnotelDay>();
    }

    public struct SnotelDay
    {
        public double Wteq, Prec;
        public char WteqQc, WteqQa, PrecQc, PrecQa;
    }

    /// <summary>
    /// The NRCS Air and Water Database REST service (WX-10: free, no key, Western US) and NOAA's station search (to say
    /// whether Daymet's GHCN-Daily inputs include the station). SNOTEL is a cross-check only, never an input to the rows.
    /// </summary>
    public static class SnotelClient
    {
        public const string Service = "https://wcc.sc.egov.usda.gov/awdbRestApi/services/v1";
        public const string NoaaSearch = "https://www.ncei.noaa.gov/access/services/search/v1/data";

        public static string StationsUrl => Service + "/stations?stationTriplets=*:*:SNTL&activeOnly=true";

        public static string DataUrl(string triplet, int firstDay, int lastDay) =>
            string.Format(CultureInfo.InvariantCulture, "{0}/data?stationTriplets={1}&elements=WTEQ,PREC&duration=DAILY&beginDate={2:yyyy-MM-dd}&endDate={3:yyyy-MM-dd}&returnFlags=true",
                          Service, triplet, CivilDay.Date(firstDay), CivilDay.Date(lastDay));

        public static string GhcnUrl(SnotelStation s) =>
            string.Format(CultureInfo.InvariantCulture, "{0}?dataset=daily-summaries&bbox={1:0.0000},{2:0.0000},{3:0.0000},{4:0.0000}&limit=100",
                          NoaaSearch, s.Latitude + 0.02, s.Longitude - 0.02, s.Latitude - 0.02, s.Longitude + 0.02);

        public static List<SnotelStation> ParseStations(byte[] body)
        {
            var list = new List<SnotelStation>();
            foreach (var t in JArray.Parse(Encoding.UTF8.GetString(body)))
            {
                if (t["latitude"] == null || t["longitude"] == null || t["elevation"] == null) continue;
                list.Add(new SnotelStation
                {
                    Triplet = (string?)t["stationTriplet"] ?? "",
                    Name = (string?)t["name"] ?? "",
                    Latitude = (double)t["latitude"]!,
                    Longitude = (double)t["longitude"]!,
                    ElevationFeet = (double)t["elevation"]!,
                    BeginDate = (string?)t["beginDate"] ?? "",
                    EndDate = (string?)t["endDate"] ?? "",
                });
            }
            return list;
        }

        /// <summary>
        /// WX-10 and the QC plan: up to <paramref name="max"/> stations within <paramref name="radiusMetres"/> of the centre and
        /// ±<paramref name="dzMetres"/> of <paramref name="zrefMetres"/>, nearest first, that began before the window's end.
        /// </summary>
        public static List<SnotelStation> Choose(IEnumerable<SnotelStation> all, GeoPoint centre, double zrefMetres, int firstDay,
                                                 double radiusMetres = 15_000, double dzMetres = 300, int max = 3)
        {
            var c = Albers6350.Forward(centre);
            var chosen = new List<SnotelStation>();
            foreach (var s in all)
            {
                var p = Albers6350.Forward(new GeoPoint(s.Latitude, s.Longitude));
                double d = Math.Sqrt((p.X - c.X) * (p.X - c.X) + (p.Y - c.Y) * (p.Y - c.Y));
                if (d > radiusMetres) continue;
                if (Math.Abs(s.ElevationFeet * 0.3048 - zrefMetres) > dzMetres) continue;
                s.DistanceMetres = d;
                chosen.Add(s);
            }
            return chosen.OrderBy(s => s.DistanceMetres).ThenBy(s => s.Triplet, StringComparer.Ordinal).Take(max).ToList();
        }

        public static SnotelSeries ParseData(byte[] body, SnotelStation station)
        {
            var series = new SnotelSeries { Station = station };
            var root = JArray.Parse(Encoding.UTF8.GetString(body));
            foreach (var st in root)
                foreach (var el in st["data"] ?? new JArray())
                {
                    string code = (string?)el["stationElement"]?["elementCode"] ?? "";
                    if (code != "WTEQ" && code != "PREC") continue;
                    foreach (var v in el["values"] ?? new JArray())
                    {
                        string? date = (string?)v["date"];
                        if (date == null) continue;
                        var dt = DateTime.ParseExact(date.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                        int day = CivilDay.Of(dt.Year, dt.Month, dt.Day);
                        if (!series.Days.TryGetValue(day, out var d)) d = new SnotelDay { Wteq = double.NaN, Prec = double.NaN };
                        double value = v["value"] == null || v["value"]!.Type == JTokenType.Null ? double.NaN : (double)v["value"]!;
                        char qc = Flag((string?)v["qcFlag"]), qa = Flag((string?)v["qaFlag"]);
                        if (code == "WTEQ") { d.Wteq = value; d.WteqQc = qc; d.WteqQa = qa; }
                        else { d.Prec = value; d.PrecQc = qc; d.PrecQa = qa; }
                        series.Days[day] = d;
                    }
                }
            return series;
        }

        static char Flag(string? f) => string.IsNullOrEmpty(f) ? ' ' : f![0];

        /// <summary>The GHCN-Daily "USS…" id NOAA lists within about 1 km of the station, if any.</summary>
        public static string MatchGhcn(byte[] body, SnotelStation s)
        {
            var root = JObject.Parse(Encoding.UTF8.GetString(body));
            foreach (var r in root["results"] ?? new JArray())
            {
                var st = r["stations"]?.FirstOrDefault();
                string id = (string?)st?["id"] ?? "";
                if (!id.StartsWith("USS", StringComparison.Ordinal)) continue;
                var coords = r["location"]?["coordinates"];
                if (coords == null) continue;
                double lon = (double)coords[0]!, lat = (double)coords[1]!;
                if (Math.Abs(lat - s.Latitude) < 0.012 && Math.Abs(lon - s.Longitude) < 0.016) return id;
            }
            return "";
        }

        public static async Task<List<SnotelSeries>> GetAsync(IWeatherResponses source, GeoPoint centre, double zrefMetres, int firstDay, int lastDay,
                                                              CancellationToken ct)
        {
            byte[] stations = await source.GetAsync("snotel/stations.json", StationsUrl, ct).ConfigureAwait(false);
            var result = new List<SnotelSeries>();
            foreach (var s in Choose(ParseStations(stations), centre, zrefMetres, firstDay))
            {
                string safe = s.Triplet.Replace(':', '_');
                byte[] data = await source.GetAsync($"snotel/{safe}.json", DataUrl(s.Triplet, firstDay, lastDay), ct).ConfigureAwait(false);
                try
                {
                    byte[] ghcn = await source.GetAsync($"snotel/{safe}-ghcn.json", GhcnUrl(s), ct).ConfigureAwait(false);
                    s.GhcnId = MatchGhcn(ghcn, s);
                }
                catch (WeatherSourceRefusedException) { }   // the note is optional; the comparison stands without it
                result.Add(ParseData(data, s));
            }
            return result;
        }
    }
}
