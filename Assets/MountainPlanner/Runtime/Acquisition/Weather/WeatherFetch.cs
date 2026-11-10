#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Domain.Geo;
using Newtonsoft.Json;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>What a weather download asks for: the box (as the package states it) and the year it is built in.</summary>
    public sealed class WeatherSiteRequest
    {
        public string Name { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double CentreX { get; set; }
        public double CentreY { get; set; }
        public int SizeMetres { get; set; }
        public int RingMetres { get; set; }
        /// <summary>The calendar year of the build: the newest year asked for (the services answer with what they have).</summary>
        public int BuildYear { get; set; }
        /// <summary>
        /// Our 10 m terrain's mean elevation over each lattice pixel's footprint, in the lattice's order (NaN where the
        /// package's heights don't reach), for the Daymet georeference check (QC). Recorded with the fixture.
        /// </summary>
        public List<double> OurPixelElevations { get; set; } = new List<double>();
        /// <summary>WX-35's reference points from the box's own heights (ft): base = 10th percentile, summit = 95th; 0 = not set.</summary>
        public int BaseFeet { get; set; }
        public int SummitFeet { get; set; }

        public GeoPoint Centre => new GeoPoint(Latitude, Longitude);
        public AlbersPoint AlbersCentre => new AlbersPoint(CentreX, CentreY);
        public double HalfExtentMetres => SizeMetres / 2.0 + RingMetres;

        public const string FileName = "site.json";
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
        public static WeatherSiteRequest FromJson(string json) => JsonConvert.DeserializeObject<WeatherSiteRequest>(json)
                                                                 ?? throw new InvalidDataException("site.json is empty.");
    }

    /// <summary>Everything the weather sources returned for a site, parsed, before any screening (WX-10, WX-14).</summary>
    public sealed class WeatherSourceRecord
    {
        public WeatherSiteRequest Site = new WeatherSiteRequest();
        /// <summary>The reference pixel: the one holding the box's centre (WX-14); null outside Daymet (WX-13).</summary>
        public DaymetPixel? Reference;
        /// <summary>The lattice pixels, the reference pixel first; parallel to <see cref="LatticeCentres"/>.</summary>
        public readonly List<DaymetPixel> Lattice = new List<DaymetPixel>();
        public readonly List<(double X, double Y)> LatticeCentres = new List<(double X, double Y)>();
        /// <summary>Each lattice pixel's place in <see cref="DaymetGrid.Lattice"/> (a refused pixel leaves a gap), for
        /// <see cref="WeatherSiteRequest.OurPixelElevations"/>.</summary>
        public readonly List<int> LatticeSlots = new List<int>();
        public PowerHours Power = new PowerHours();
        public readonly List<SnotelSeries> Snotel = new List<SnotelSeries>();
        /// <summary>Why there is no SNOTEL comparison, when there isn't one ("no station within 15 km", "unavailable").</summary>
        public string SnotelNote = "";
        public bool OutsideDaymet => Reference == null;
        /// <summary>WX-11 v1: the window's first and last label years (named by October's year, as LabelCalendar names them).</summary>
        public int FirstLabelYear, LastLabelYear;

        public int FirstDay => CivilDay.Of(FirstLabelYear, 10, 1);
        public int LastDay => CivilDay.Of(LastLabelYear + 1, 9, 30);
    }

    /// <summary>
    /// Fetches a site's weather record (task 4.05): Daymet's reference pixel and lattice, NASA POWER's hours at the centre, and
    /// the nearest SNOTEL stations (optional: a failure there is a note, never a failed download).
    /// </summary>
    public static class WeatherFetch
    {
        /// <summary>WX-11 v1: label years in the window.</summary>
        public const int WindowYears = 20;

        public static async Task<WeatherSourceRecord> FetchAsync(IWeatherResponses source, WeatherSiteRequest site, IProgress<(int Done, int Total)>? progress,
                                                                 CancellationToken ct)
        {
            var record = new WeatherSourceRecord { Site = site };
            int askFirst = site.BuildYear - WindowYears - 2, askLast = site.BuildYear;
            var lattice = DaymetGrid.Lattice(site.AlbersCentre, site.HalfExtentMetres);
            int total = lattice.Count + (WindowYears + 3) + 3, done = 0;
            void Step() => progress?.Report((++done, total));

            // 1. The reference pixel, all seven variables: it fixes the window (Daymet lags POWER).
            DaymetPixel? reference = null;
            try
            {
                var c = lattice[0];
                reference = await DaymetClient.GetAsync(source, "daymet/ref.csv", DaymetGrid.Inverse(c.X, c.Y), DaymetClient.ReferenceVariables,
                                                        askFirst, askLast, ct).ConfigureAwait(false);
            }
            catch (WeatherSourceRefusedException) { }   // WX-13: outside Daymet; POWER alone, flagged limited
            Step();
            record.Reference = reference;

            int lastComplete = reference != null ? LastCompleteYear(reference) : site.BuildYear - 1;
            // WX-11 v1: the 20 most recent complete label years both sources cover. Label year L runs October L to September L+1.
            record.LastLabelYear = lastComplete - 1;
            record.FirstLabelYear = record.LastLabelYear - WindowYears + 1;
            int firstYear = record.FirstLabelYear, lastYear = record.LastLabelYear + 1;

            // 2. The lattice (WX-14), the reference pixel's data reused.
            if (reference != null)
            {
                record.Lattice.Add(reference);
                record.LatticeCentres.Add(lattice[0]);
                record.LatticeSlots.Add(0);
                for (int i = 1; i < lattice.Count; i++)
                {
                    var (x, y) = lattice[i];
                    string key = string.Format(CultureInfo.InvariantCulture, "daymet/px{0:0}_{1:0}.csv", x, y);
                    try
                    {
                        var px = await DaymetClient.GetAsync(source, key, DaymetGrid.Inverse(x, y), DaymetClient.LatticeVariables,
                                                             askFirst, askLast, ct).ConfigureAwait(false);
                        record.Lattice.Add(px);
                        record.LatticeCentres.Add((x, y));
                        record.LatticeSlots.Add(i);
                    }
                    catch (WeatherSourceRefusedException) { }   // a pixel over the sea or past the grid's edge
                    Step();
                }
            }

            // 3. POWER at the box's centre, a calendar year a request.
            record.Power = await PowerClient.GetAsync(source, site.Centre, firstYear, lastYear, ct).ConfigureAwait(false);
            done += lastYear - firstYear + 2;
            progress?.Report((done, total));

            // 4. SNOTEL, a cross-check only.
            if (reference != null)
            {
                try
                {
                    var series = await SnotelClient.GetAsync(source, site.Centre, reference.ElevationMetres, record.FirstDay, record.LastDay, ct)
                                                   .ConfigureAwait(false);
                    record.Snotel.AddRange(series);
                    if (series.Count == 0) record.SnotelNote = "No SNOTEL station within 15 km and 300 m of the reference elevation.";
                }
                catch (Exception ex) when (ex is IOException || ex is WeatherSourceRefusedException || ex is JsonException)
                {
                    record.SnotelNote = "SNOTEL was unavailable: " + ex.Message;
                }
            }
            else record.SnotelNote = "Outside Daymet: no SNOTEL comparison.";
            progress?.Report((total, total));
            return record;
        }

        /// <summary>The newest calendar year Daymet answered whole (it publishes whole years; 365 days, December 30 last in a leap year).</summary>
        static int LastCompleteYear(DaymetPixel p)
        {
            int last = p.Days[p.Days.Count - 1];
            var d = CivilDay.Date(last);
            bool whole = (d.Month == 12 && d.Day == 31) || (d.Month == 12 && d.Day == 30 && DateTime.IsLeapYear(d.Year));
            return whole ? d.Year : d.Year - 1;
        }

        /// <summary>Opens a recorded site: <c>site.json</c> plus the gzipped answers beside it.</summary>
        public static Task<WeatherSourceRecord> LoadRecordingAsync(string folder, CancellationToken ct = default)
        {
            var site = WeatherSiteRequest.FromJson(File.ReadAllText(Path.Combine(folder, WeatherSiteRequest.FileName), Encoding.UTF8));
            return FetchAsync(new FolderWeatherResponses(folder), site, null, ct);
        }

        /// <summary>WX-35: the box's 10th and 95th percentile heights (ft), from its core heights (metres; NaN skipped).</summary>
        public static (int BaseFeet, int SummitFeet) ReferenceFeet(float[] coreHeights)
        {
            var h = new List<float>(coreHeights.Length);
            foreach (float v in coreHeights) if (!float.IsNaN(v)) h.Add(v);
            if (h.Count == 0) return (0, 0);
            h.Sort();
            double At(double p) => h[(int)Math.Min(h.Count - 1, Math.Floor(p * (h.Count - 1)))];
            return ((int)Math.Round(At(0.10) / 0.3048), (int)Math.Round(At(0.95) / 0.3048));
        }

        /// <summary>
        /// Our terrain's mean over each lattice pixel's footprint (QC's georeference check), from any height function in Albers
        /// metres that answers NaN outside the package.
        /// </summary>
        public static List<double> PixelElevations(WeatherSiteRequest site, Func<double, double, double> heightAt)
        {
            var result = new List<double>();
            foreach (var (x, y) in DaymetGrid.Lattice(site.AlbersCentre, site.HalfExtentMetres))
            {
                double sum = 0;
                int n = 0, all = 0;
                foreach (var a in DaymetGrid.Footprint(x, y))
                {
                    all++;
                    double h = heightAt(a.X, a.Y);
                    if (double.IsNaN(h)) continue;
                    sum += h;
                    n++;
                }
                result.Add(n * 10 >= all * 9 ? sum / n : double.NaN);   // 90 % of the footprint inside, or no check
            }
            return result;
        }
    }
}
