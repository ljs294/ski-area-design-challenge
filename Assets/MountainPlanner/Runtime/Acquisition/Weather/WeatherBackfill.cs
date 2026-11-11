#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition.Weather
{
    /// <summary>
    /// Get weather (task 4.05, PR A2): adds the weather to an area downloaded before it existed, or whose download skipped it.
    /// Unlike Get water and roads (<see cref="OsmBackfill"/>) the area keeps its id: the Weather block isn't part of it, so no
    /// save moves and the area may stay open meanwhile. It writes weather.rows, then the manifest (the commit point); no other
    /// file is touched. A run cut short leaves an unlisted weather.rows, which the next run replaces.
    /// </summary>
    public static class WeatherBackfill
    {
        public const string Stage = AcquisitionPipeline.StageWeather;

        /// <summary>
        /// Runs it; returns false when the area needed nothing. Throws the network's own errors when the services can't be
        /// reached (offline mode: <see cref="NetworkDisabledException"/>), and leaves the area as it was then. The caller never
        /// passes the built-in demo (it ships with its weather and is read-only).
        /// </summary>
        public static Task<bool> RunAsync(string packageFolder, string cacheFolder, IProgress<AcquisitionProgress>? progress, CancellationToken ct,
                                          int? buildYear = null)
        {
            var meter = new TransferMeter();
            return RunAsync(packageFolder, WeatherBuild.Source(new DiskCache(cacheFolder), meter), meter, progress, ct, buildYear);
        }

        /// <summary>Tests and <c>-weatherrecording</c> pass their own <paramref name="source"/> (a recorded folder).</summary>
        public static async Task<bool> RunAsync(string packageFolder, IWeatherResponses source, TransferMeter meter, IProgress<AcquisitionProgress>? progress,
                                                CancellationToken ct, int? buildYear = null, Action? beforeCommit = null)
        {
            var manifest = ResortPackage.ReadManifest(packageFolder);   // a newer game's package throws FormatTooNewException
            if (!WeatherBuild.Needed(manifest)) return false;
            var site = WeatherBuild.SiteFor(packageFolder, manifest, buildYear ?? WeatherBuild.BuildYearNow());

            WeatherBuildResult result;
            using (var tracker = new ProgressTracker(progress, meter))
            {
                tracker.DefineStage(Stage, 1);
                tracker.BeginStage(Stage);
                result = await WeatherBuild.BuildAsync(source, site, new WeatherSteps(tracker), ct, AcquisitionPipeline.ToolName).ConfigureAwait(false);
                tracker.Finish();
            }
            ct.ThrowIfCancellationRequested();

            // Re-read: the player may have renamed or changed the area while the weather was fetched (one writer at a time
            // is the caller's guard; this keeps every field but the weather's as it is on disk now).
            manifest = ResortPackage.ReadManifest(packageFolder);
            if (!WeatherBuild.Needed(manifest)) return false;
            string id = manifest.PackageId;
            WeatherBuild.AddTo(packageFolder, manifest, result);
            beforeCommit?.Invoke();
            ResortPackage.WriteManifest(packageFolder, manifest);
            if (manifest.PackageId != id) throw new InvalidDataException("Adding the weather moved the area's id.");   // never: Weather isn't in the id
            return true;
        }

    }
}
