#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.Weather.Qc;
using MountainPlanner.Domain.Weather;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>What <see cref="WeatherBuild.BuildAsync"/> made: the rows (null when the site has no history) and the Weather block.</summary>
    public sealed class WeatherBuildResult
    {
        public WeatherRows? Rows;
        public WeatherInfo Info = new WeatherInfo();
        public CalibrationReport? Report;
        public WeatherQcLog Log = new WeatherQcLog();
        public bool HasRows => Rows != null;
    }

    /// <summary>
    /// The one weather build (task 4.05, PR A2) every caller shares: the download's Weather stage, Get weather
    /// (<see cref="WeatherBackfill"/>) and <c>acquire weather-build</c>. Fetch, screen, blend, calibrate, then the rows and the
    /// manifest's Weather block, attribution and provenance. The same answers give the same rows, byte for byte: the recorded
    /// Jackson Hole answers give the frozen <c>TestData/weather/jackson-hole-2km/weather.rows</c>.
    /// </summary>
    public static class WeatherBuild
    {
        public const string FetchStep = "Fetching weather history", CalibrateStep = "Calibrating the weather";
        /// <summary>The calibrate step's share taken by the screens and the blend, before the calibration's plays.</summary>
        const double CalibrateBlendShare = 0.03;
        /// <summary>The verdict of a site with no history to calibrate from (outside Daymet's grid): PR B plays generic rows there.</summary>
        public const string NoHistory = "No history";

        /// <summary>Credits lines (Credits splits "What: Who (licence)."), written from each service's published terms.</summary>
        public const string HistoryLine = "Weather history: Thornton et al., Daymet Version 4 R1, ORNL DAAC (free to use with citation).";
        public const string HoursLine = "Weather hours: NASA Langley Research Center POWER Project, funded through the NASA Earth Science Division (free to use with acknowledgement).";
        public const string SnowCheckLine = "Snow check: USDA NRCS SNOTEL (public domain).";
        public const string ProviderDaymet = "Daymet", ProviderPower = "NASA POWER", ProviderSnotel = "SNOTEL";
        /// <summary>The provenance layer for the weather (it isn't a grid layer, so it never enters the package id).</summary>
        public const string ProvenanceLayer = "weather";

        /// <summary>
        /// The dev switch <c>-weatherrecording &lt;folder&gt;</c>: every weather answer comes from a recorded site (the fixtures
        /// under TestData/weather) instead of the services, so a demo or a test runs without the network. Null in play.
        /// </summary>
        public static string? ReplayFolder;

        /// <summary>Where a build's answers come from: the replay folder when set, else the services through the download cache.</summary>
        public static IWeatherResponses Source(IO.DiskCache cache, IO.TransferMeter? meter) =>
            ReplayFolder != null ? new FolderWeatherResponses(ReplayFolder) : (IWeatherResponses)new LiveWeatherResponses(cache, meter);

        /// <summary>
        /// The year a build asks its 20 winters back from: this year, or the recording's own under the replay switch (its
        /// answers stop where it was recorded, so a later year would ask for winters it doesn't hold).
        /// </summary>
        public static int BuildYearNow()
        {
            if (ReplayFolder == null) return DateTime.UtcNow.Year;
            return WeatherSiteRequest.FromJson(File.ReadAllText(Path.Combine(ReplayFolder, WeatherSiteRequest.FileName))).BuildYear;
        }

        /// <summary>An area still waiting for its weather: a v1 manifest, or one downloaded with Weather skipped.</summary>
        public static bool Needed(PackageManifest m) => m.Weather == null;

        /// <summary>
        /// The site request from the package's own heights (WX-35's reference points, the QC's georeference check): the box,
        /// the build year, our mean over each lattice pixel and the 10th / 95th percentile heights.
        /// </summary>
        public static WeatherSiteRequest SiteFor(string packageFolder, PackageManifest m, int buildYear)
        {
            var site = new WeatherSiteRequest
            {
                Name = m.Site.Name, Latitude = m.Site.Latitude, Longitude = m.Site.Longitude,
                CentreX = m.Site.CentreX, CentreY = m.Site.CentreY, SizeMetres = m.Site.SizeMetres, RingMetres = m.Site.RingMetres,
                BuildYear = buildYear,
            };
            float[] core = ResortPackage.ReadLayer(packageFolder, m, "heights-core", out var coreHeader);
            float[] ring = ResortPackage.ReadLayer(packageFolder, m, "heights-ring", out var ringHeader);
            var heights = new TerrainCache.HeightField(core, coreHeader, ring, ringHeader);
            double rw = ringHeader.West, rn = ringHeader.North, re = rw + ringHeader.Width * ringHeader.CellSize, rs = rn - ringHeader.Height * ringHeader.CellSize;
            site.OurPixelElevations = WeatherFetch.PixelElevations(site, (x, y) => x < rw || x > re || y < rs || y > rn ? double.NaN : heights.At(x, y));
            (site.BaseFeet, site.SummitFeet) = WeatherFetch.ReferenceFeet(core);
            return site;
        }

        /// <summary>
        /// Fetches and calibrates. <paramref name="progress"/> gets the step and its share done (0–1). Calibration takes about
        /// 15–20 s on one core and checks <paramref name="ct"/> between its parts. Nothing is written here.
        /// </summary>
        public static async Task<WeatherBuildResult> BuildAsync(IWeatherResponses source, WeatherSiteRequest site, IProgress<(string Step, double Share)>? progress,
                                                                CancellationToken ct, string builtBy = "")
        {
            var fetchProgress = progress == null ? null : new SyncProgress<(int Done, int Total)>(p => progress.Report((FetchStep, p.Total == 0 ? 0 : (double)p.Done / p.Total)));
            var record = await WeatherFetch.FetchAsync(source, site, fetchProgress, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            progress?.Report((CalibrateStep, 0));
            string accessed = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var result = new WeatherBuildResult();
            var info = result.Info;
            info.BuiltBy = builtBy + (builtBy.Length > 0 ? ", " : "") + "QC limits v" + WeatherQcLimits.Version.ToString(CultureInfo.InvariantCulture);
            info.FirstLabelYear = record.FirstLabelYear;
            info.LastLabelYear = record.LastLabelYear;
            if (record.OutsideDaymet)
            {
                // WX-13: no Daymet, no calibration (POWER alone can't place the storms). The area is done, with generic rows in PR B.
                info.RowsFile = "";
                info.Verdict = NoHistory;
                info.Limited = true;
                info.Reasons.Add("Outside Daymet's North America grid: there is no daily weather history to calibrate from.");
                return result;
            }

            var log = result.Log;
            DaymetScreen.Apply(record, log);
            PowerScreen.Apply(record, log);
            var stations = SnotelScreen.Apply(record, log);
            ct.ThrowIfCancellationRequested();
            var blend = WeatherBlend.Build(record);
            ct.ThrowIfCancellationRequested();
            progress?.Report((CalibrateStep, CalibrateBlendShare));
            // Each play reports its share; the cancel is checked there too, so Cancel never waits out the whole 20 s.
            var (rows, report) = WeatherCalibration.Build(blend, record, new ReferencePoints(site.BaseFeet, site.SummitFeet), WeatherTuning.Default,
                                                          progress: share =>
                                                          {
                                                              ct.ThrowIfCancellationRequested();
                                                              progress?.Report((CalibrateStep, CalibrateBlendShare + (1 - CalibrateBlendShare) * share));
                                                          });
            ct.ThrowIfCancellationRequested();
            result.Rows = rows;
            result.Report = report;

            var reference = record.Reference!;
            info.RowsVersion = WeatherRowsFile.Version;
            info.ReferenceLatitude = reference.Latitude;
            info.ReferenceLongitude = reference.Longitude;
            info.ReferenceElevationMetres = reference.ElevationMetres;
            info.ObservationLagHours = blend.LagHours;
            info.Reasons.AddRange(log.LimitedReasons);
            info.Reasons.AddRange(report.LimitedReasons.Where(r => !info.Reasons.Contains(r)));
            info.Limited = info.Reasons.Count > 0;
            var verdict = log.Verdict == WeatherVerdict.Clean && info.Limited ? WeatherVerdict.Limited : log.Verdict;
            info.Verdict = WeatherQcLog.Name(verdict);
            info.Reasons.AddRange(log.AnchorReasons);   // report only: the player sees the limited line, never these

            info.Sources.Add(new WeatherSourceInfo { Name = ProviderDaymet, Service = DaymetClient.Service, Version = reference.Version,
                                                     Citation = reference.Citation, AccessedUtc = accessed });
            info.Sources.Add(new WeatherSourceInfo { Name = ProviderPower, Service = PowerClient.Service, Version = record.Power.ApiVersion,
                                                     Citation = $"POWER Project's Hourly {record.Power.ApiVersion} version on {accessed}", AccessedUtc = accessed });
            if (record.Snotel.Count > 0)
                info.Sources.Add(new WeatherSourceInfo { Name = ProviderSnotel, Service = SnotelClient.Service, Version = "AWDB REST v1",
                                                         Citation = "USDA NRCS National Water and Climate Center, SNOTEL", AccessedUtc = accessed });
            info.Qc.AddRange(log.Entries);
            info.QcLimits = WeatherQcLog.Limits();
            info.Stations.AddRange(stations);
            if (record.SnotelNote.Length > 0) info.Stations.Add(new WeatherStationInfo { Note = record.SnotelNote });

            // The calibration's own checks (WX-18, WX-31 v2) and what it fitted, for the report and problem reports.
            info.Checks.Add(new WeatherCheckInfo { Name = "WX-18 storm days / Daymet wet days", Value = report.StormDayRatio, Low = 0.9, High = double.PositiveInfinity,
                                                   Result = report.StormDayRatio >= 0.9 ? "pass" : "fail" });
            info.Checks.Add(new WeatherCheckInfo { Name = "WX-18 clearing ends in daylight", Value = report.ClearingDaylightShare, Low = 0, High = 0.6,
                                                   Result = report.ClearingDaylightShare < 0.6 ? "pass" : "fail" });
            foreach (var kv in report.Ks.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                double gate = WeatherCalibration.KsCritical(report.KsCount[kv.Key]);
                info.Checks.Add(new WeatherCheckInfo { Name = "WX-31 KS " + kv.Key, Value = kv.Value, Low = 0, High = gate, Result = kv.Value < gate ? "pass" : "fail",
                                                       Note = "n " + report.KsCount[kv.Key].ToString(CultureInfo.InvariantCulture) });
            }
            info.Checks.Add(new WeatherCheckInfo { Name = "WX-41 powder mornings a season, record", Value = report.PowderPerSeasonRecord });
            info.Checks.Add(new WeatherCheckInfo { Name = "WX-41 powder mornings a season, game", Value = report.PowderPerSeasonGame });
            foreach (var kv in report.Fits.OrderBy(k => k.Key, StringComparer.Ordinal))
                info.Checks.Add(new WeatherCheckInfo { Name = "fit " + kv.Key, Value = kv.Value });
            foreach (var kv in report.Levels)
                info.Checks.Add(new WeatherCheckInfo { Name = "pooled " + kv.Key, Note = kv.Value });
            progress?.Report((CalibrateStep, 1));
            return result;
        }

        /// <summary>
        /// Writes the rows beside the manifest and puts the Weather block, credits and provenance into <paramref name="m"/>. The
        /// caller writes the manifest (its commit point). Layers, the id and every other file stay as they are.
        /// </summary>
        public static void AddTo(string packageFolder, PackageManifest m, WeatherBuildResult r)
        {
            if (r.Rows != null)
            {
                r.Info.RowsFile = WeatherRowsFile.FileName;
                r.Info.RowsSha256 = WeatherRowsFile.Write(packageFolder, r.Rows);
            }
            m.Weather = r.Info;
            m.Provenance.RemoveAll(p => p.Layer == ProvenanceLayer);
            if (r.Rows == null) return;
            AddLine(m, HistoryLine);
            AddLine(m, HoursLine);
            m.Provenance.Add(new ProvenanceInfo { Layer = ProvenanceLayer, Provider = ProviderDaymet, Product = "Daymet V4 R1 daily, 1 km",
                                                  Items = new List<string> { $"label years {r.Info.FirstLabelYear}-{r.Info.LastLabelYear}" } });
            m.Provenance.Add(new ProvenanceInfo { Layer = ProvenanceLayer, Provider = ProviderPower, Product = "Hourly point data (MERRA-2, CERES)" });
            if (r.Info.Sources.Any(s => s.Name == ProviderSnotel))
            {
                AddLine(m, SnowCheckLine);
                m.Provenance.Add(new ProvenanceInfo { Layer = ProvenanceLayer, Provider = ProviderSnotel, Product = "Snow water and precipitation (cross-check only)",
                                                      Items = r.Info.Stations.Where(s => s.Triplet.Length > 0).Select(s => s.Triplet).ToList() });
            }
        }

        static void AddLine(PackageManifest m, string line)
        {
            if (!m.Attribution.Contains(line)) m.Attribution.Add(line);
        }

        /// <summary>
        /// The build's steps on a download's progress bar: fetching is the first 70 % of the stage (it waits on servers),
        /// calibrating the rest (about 20 s of one core).
        /// </summary>
        internal const double FetchShare = 0.7;

        /// <summary>An <see cref="IProgress{T}"/> that reports on the caller's thread (no captured context: the build runs off the game's threads).</summary>
        sealed class SyncProgress<T> : IProgress<T>
        {
            readonly Action<T> _report;
            public SyncProgress(Action<T> report) { _report = report; }
            public void Report(T value) => _report(value);
        }
    }
}
