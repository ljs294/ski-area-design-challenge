#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json.Linq;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// NASA POWER's hours at one point (WX-10), in local solar time, keyed by <see cref="CivilDay"/> and hour 0–23. A value
    /// the service marks missing (its fill value, −999) is <see cref="double.NaN"/> here and stays missing (WX-16).
    /// </summary>
    public sealed class PowerHours
    {
        public double Latitude, Longitude;
        /// <summary>The cell's elevation as the header states it (MERRA-2's smoothed average), for the report only.</summary>
        public double CellElevationMetres;
        /// <summary>The API's version string as it reported it ("v2.10.4"), for the data reference (WX-12).</summary>
        public string ApiVersion = "";
        public readonly List<int> Days = new List<int>();
        public readonly List<int> Hours = new List<int>();
        public readonly Dictionary<string, List<double>> Values = new Dictionary<string, List<double>>();

        public List<double> Series(string parameter) => Values[parameter];
        public int Count => Hours.Count;
    }

    /// <summary>The NASA POWER hourly point API (no account or key): one request per calendar year, ≤ 15 parameters.</summary>
    public static class PowerClient
    {
        public const string Service = "https://power.larc.nasa.gov/api/temporal/hourly/point";
        public const double FillValue = -999;
        public static readonly string[] Parameters =
        {
            "T2M", "T2MDEW", "RH2M", "WS10M", "WS50M", "WD50M", "PRECTOTCORR", "ALLSKY_SFC_SW_DWN", "CLRSKY_SFC_SW_DWN", "CLOUD_AMT", "PS",
        };

        public static string Url(GeoPoint p, int year) =>
            string.Format(CultureInfo.InvariantCulture,
                          "{0}?parameters={1}&community=RE&longitude={2:0.######}&latitude={3:0.######}&start={4}0101&end={4}1231&format=CSV&time-standard=LST",
                          Service, string.Join(",", Parameters), p.Longitude, p.Latitude, year);

        /// <summary>A one-day JSON request: only for the version the API reports (the CSV header doesn't state it).</summary>
        public static string VersionUrl(GeoPoint p, int year) =>
            string.Format(CultureInfo.InvariantCulture,
                          "{0}?parameters=T2M&community=RE&longitude={1:0.######}&latitude={2:0.######}&start={3}0101&end={3}0101&format=JSON",
                          Service, p.Longitude, p.Latitude, year);

        public static async Task<PowerHours> GetAsync(IWeatherResponses source, GeoPoint p, int firstYear, int lastYear, CancellationToken ct)
        {
            var all = new PowerHours();
            foreach (string name in Parameters) all.Values[name] = new List<double>();
            for (int year = firstYear; year <= lastYear; year++)
            {
                byte[] body = await source.GetAsync($"power/{year}.csv", Url(p, year), ct).ConfigureAwait(false);
                ParseInto(all, body);
            }
            byte[] version = await source.GetAsync("power/version.json", VersionUrl(p, lastYear), ct).ConfigureAwait(false);
            all.ApiVersion = (string?)JObject.Parse(Encoding.UTF8.GetString(version))["header"]?["api"]?["version"] ?? "";
            return all;
        }

        /// <summary>Appends one year's CSV: a <c>-BEGIN HEADER-</c> block, then <c>YEAR,MO,DY,HR,…</c>.</summary>
        public static void ParseInto(PowerHours into, byte[] body)
        {
            using (var reader = new StringReader(Encoding.UTF8.GetString(body)))
            {
                string? line;
                int[]? map = null;
                List<double>[]? lists = null;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (map == null)
                    {
                        if (line.StartsWith("Location:", StringComparison.Ordinal))
                        {
                            string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                            into.Latitude = double.Parse(f[2], CultureInfo.InvariantCulture);
                            into.Longitude = double.Parse(f[4], CultureInfo.InvariantCulture);
                        }
                        else if (line.StartsWith("Elevation from MERRA-2:", StringComparison.Ordinal))
                        {
                            int eq = line.IndexOf('=');
                            if (eq > 0) into.CellElevationMetres = double.Parse(line.Substring(eq + 1).Trim().Split(' ')[0], CultureInfo.InvariantCulture);
                        }
                        else if (line.StartsWith("YEAR,MO,DY,HR", StringComparison.Ordinal))
                        {
                            string[] cols = line.Split(',');
                            map = new int[cols.Length];
                            lists = new List<double>[cols.Length];
                            for (int c = 4; c < cols.Length; c++)
                            {
                                if (!into.Values.TryGetValue(cols[c], out var list)) into.Values[cols[c]] = list = new List<double>();
                                lists[c] = list;
                            }
                        }
                        continue;
                    }
                    string[] v = line.Split(',');
                    if (v.Length != map.Length) throw new InvalidDataException("A POWER row has the wrong number of columns: " + line);
                    into.Days.Add(CivilDay.Of(int.Parse(v[0], CultureInfo.InvariantCulture), int.Parse(v[1], CultureInfo.InvariantCulture),
                                              int.Parse(v[2], CultureInfo.InvariantCulture)));
                    into.Hours.Add(int.Parse(v[3], CultureInfo.InvariantCulture));
                    for (int c = 4; c < v.Length; c++)
                    {
                        double x = double.Parse(v[c], CultureInfo.InvariantCulture);
                        lists![c].Add(x <= FillValue + 0.5 ? double.NaN : x);
                    }
                }
                if (map == null) throw new InvalidDataException("The POWER answer has no data rows.");
            }
        }
    }
}
