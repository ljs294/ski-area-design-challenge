#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// One Daymet V4 pixel's days (WX-10): the single-pixel extraction service's CSV, parsed as the service gave it. Days are
    /// keyed by calendar date (<see cref="CivilDay"/>), never by index (WX-15 v2): Daymet's 365-day year keeps February 29
    /// and drops December 31 in a leap year.
    /// </summary>
    public sealed class DaymetPixel
    {
        public double Latitude, Longitude;
        /// <summary>The pixel's Lambert Conformal Conic coordinates as the service reports them (its identity on the grid).</summary>
        public double LccX, LccY;
        public int Tile;
        /// <summary>The pixel's elevation in Daymet's own DEM (metres): the rows' reference elevation at the reference pixel.</summary>
        public double ElevationMetres;
        /// <summary>The citation and version lines exactly as the service returned them (WX-12: never a typed copy).</summary>
        public string Citation = "", Version = "";
        public readonly List<int> Days = new List<int>();
        public readonly Dictionary<string, List<double>> Values = new Dictionary<string, List<double>>();
        /// <summary>Days the QC screen made missing (WX-16: a day without Daymet is dropped whole), by <see cref="CivilDay"/>.</summary>
        public readonly HashSet<int> RejectedDays = new HashSet<int>();

        public List<double> Series(string variable) => Values.TryGetValue(variable, out var v) ? v : throw new KeyNotFoundException(variable);
        public bool Has(string variable) => Values.ContainsKey(variable);
        public int FirstYear => Days.Count == 0 ? 0 : CivilDay.Year(Days[0]);
        public int LastYear => Days.Count == 0 ? 0 : CivilDay.Year(Days[Days.Count - 1]);
    }

    /// <summary>
    /// The Daymet single-pixel service (ORNL DAAC; no account or key). The reference pixel takes all seven variables; the
    /// lattice pixels (WX-14) take only what the elevation regressions read.
    /// </summary>
    public static class DaymetClient
    {
        public const string Service = "https://daymet.ornl.gov/single-pixel/api/data";
        public static readonly string[] ReferenceVariables = { "tmax", "tmin", "prcp", "vp", "srad", "swe", "dayl" };
        public static readonly string[] LatticeVariables = { "tmax", "tmin", "prcp", "vp" };

        public static string Url(GeoPoint p, IEnumerable<string> variables, int firstYear, int lastYear) =>
            string.Format(CultureInfo.InvariantCulture, "{0}?lat={1:0.######}&lon={2:0.######}&vars={3}&start={4}-01-01&end={5}-12-31",
                          Service, p.Latitude, p.Longitude, string.Join(",", variables), firstYear, lastYear);

        public static async Task<DaymetPixel> GetAsync(IWeatherResponses source, string key, GeoPoint p, IEnumerable<string> variables,
                                                      int firstYear, int lastYear, CancellationToken ct)
        {
            byte[] body = await source.GetAsync(key, Url(p, variables, firstYear, lastYear), ct).ConfigureAwait(false);
            return Parse(body);
        }

        /// <summary>Parses the service's CSV: a header of <c>Name: value</c> lines, then <c>year,yday,var (unit),…</c>.</summary>
        public static DaymetPixel Parse(byte[] body)
        {
            var px = new DaymetPixel();
            string[] columns = Array.Empty<string>();
            int[] slot = Array.Empty<int>();
            var lists = new List<List<double>>();
            using (var reader = new StringReader(Encoding.UTF8.GetString(body)))
            {
                string? line;
                bool data = false;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    if (!data)
                    {
                        if (line.StartsWith("year,", StringComparison.Ordinal))
                        {
                            columns = line.Split(',');
                            slot = new int[columns.Length];
                            for (int c = 2; c < columns.Length; c++)
                            {
                                string name = columns[c].Split(' ')[0];
                                var list = new List<double>();
                                px.Values[name] = list;
                                lists.Add(list);
                                slot[c] = lists.Count - 1;
                            }
                            data = true;
                        }
                        else Header(px, line);
                        continue;
                    }
                    string[] f = line.Split(',');
                    if (f.Length != columns.Length) throw new InvalidDataException("A Daymet row has the wrong number of columns: " + line);
                    int year = int.Parse(f[0], CultureInfo.InvariantCulture);
                    int yday = (int)double.Parse(f[1], CultureInfo.InvariantCulture);
                    px.Days.Add(CivilDay.FromDaymet(year, yday));
                    for (int c = 2; c < f.Length; c++) lists[slot[c]].Add(double.Parse(f[c], CultureInfo.InvariantCulture));
                }
                if (!data) throw new InvalidDataException("The Daymet answer has no data rows.");
            }
            return px;
        }

        static void Header(DaymetPixel px, string line)
        {
            if (line.StartsWith("Latitude:", StringComparison.Ordinal))
            {
                // "Latitude: 43.593  Longitude: -110.848"
                string[] f = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                px.Latitude = double.Parse(f[1], CultureInfo.InvariantCulture);
                px.Longitude = double.Parse(f[3], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("X & Y on Lambert Conformal Conic:", StringComparison.Ordinal))
            {
                string[] f = line.Substring(line.IndexOf(':') + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                px.LccX = double.Parse(f[0], CultureInfo.InvariantCulture);
                px.LccY = double.Parse(f[1], CultureInfo.InvariantCulture);
            }
            else if (line.StartsWith("Tile:", StringComparison.Ordinal))
                px.Tile = int.Parse(line.Substring(5).Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("Elevation:", StringComparison.Ordinal))
                px.ElevationMetres = double.Parse(line.Substring(10).Trim().Split(' ')[0], CultureInfo.InvariantCulture);
            else if (line.StartsWith("How to cite:", StringComparison.Ordinal))
                px.Citation = line.Substring(12).Trim();
            else if (line.IndexOf("Daymet Software Version", StringComparison.Ordinal) >= 0)
                px.Version = line.Trim();
        }
    }

    /// <summary>
    /// Days as integers: days since 2000-01-01 (the proleptic Gregorian calendar), so two sources line up by date (WX-15 v2).
    /// </summary>
    public static class CivilDay
    {
        static readonly DateTime Epoch = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static int Of(int year, int month, int day) => (int)(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc) - Epoch).TotalDays;

        /// <summary>Daymet's year day: in a leap year day 60 is February 29 and there is no day 366 (December 31 is dropped).</summary>
        public static int FromDaymet(int year, int yday) => Of(year, 1, 1) + yday - 1;

        public static DateTime Date(int day) => Epoch.AddDays(day);
        public static int Year(int day) => Date(day).Year;
        public static int Month(int day) => Date(day).Month;
        public static int DayOfMonth(int day) => Date(day).Day;
        public static bool IsLeapDay(int day) { var d = Date(day); return d.Month == 2 && d.Day == 29; }

        /// <summary>
        /// The label year a day belongs to, named as <c>LabelCalendar</c> names it: by October's year, so September 2006 is in
        /// label year 2005 (WX-11: October 1 to September 30).
        /// </summary>
        public static int LabelYear(int day) { var d = Date(day); return d.Month >= 10 ? d.Year : d.Year - 1; }

        /// <summary>
        /// The third of the month (WX-17 v2's rows), 0–35 from October early: days 1–10 early, 11–20 mid, the rest late, as
        /// <c>LabelCalendar.Third</c> splits them.
        /// </summary>
        public static int Third(int day)
        {
            var d = Date(day);
            int monthIndex = (d.Month + 2) % 12;   // October 0 … September 11
            int t = d.Day <= 10 ? 0 : d.Day <= 20 ? 1 : 2;
            return monthIndex * 3 + t;
        }

        /// <summary>Month index from October (0) to September (11).</summary>
        public static int LabelMonth(int day) => (Month(day) + 2) % 12;
    }
}
