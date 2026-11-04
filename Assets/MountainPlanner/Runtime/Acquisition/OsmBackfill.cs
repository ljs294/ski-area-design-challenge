#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition
{
    /// <summary>
    /// Get water and roads (task P3-16, BB6): adds OpenStreetMap water, developed land and roads to an area downloaded
    /// without them (OpenStreetMap was down or skipped, or the area is older than they are), then moves the area to its new
    /// id with its saves (<see cref="AreaRekey"/>). Only the missing layers are written; the rest of the package stays
    /// byte for byte. The terrain cache isn't rebuilt here: opening the area does that, so there's one writer.
    /// </summary>
    public static class OsmBackfill
    {
        public const string Stage = AcquisitionPipeline.StageOsm;

        /// <summary>
        /// Runs it; returns the area's id afterwards (unchanged when it needed nothing). Throws
        /// <see cref="AreaRefusedException"/> when the area can't move now, <see cref="OsmUnavailableException"/> when
        /// OpenStreetMap is still unavailable, and the network's own errors when the connection is down; the area is then
        /// left as it was.
        /// </summary>
        public static Task<string> RunAsync(string dataRoot, string packageFolder, string cacheFolder, IProgress<AcquisitionProgress>? progress,
                                            CancellationToken ct)
        {
            var meter = new TransferMeter();
            return RunAsync(dataRoot, packageFolder, new OsmFeatures(new DiskCache(cacheFolder), meter), meter, progress, ct);
        }

        /// <summary>Tests pass their own <paramref name="osm"/> (a recorded response) and <paramref name="step"/> (a crash at each point).</summary>
        internal static async Task<string> RunAsync(string dataRoot, string packageFolder, OsmFeatures osm, TransferMeter meter,
                                                    IProgress<AcquisitionProgress>? progress, CancellationToken ct, Action<RekeyStep>? step = null)
        {
            var manifest = ResortPackage.ReadManifest(packageFolder);   // a newer game's package throws FormatTooNewException
            string oldId = manifest.PackageId;
            if (!WaterAndRoads.Needed(manifest)) return oldId;
            string? refusal = AreaRekey.Refusal(dataRoot, packageFolder, oldId);
            if (refusal != null) throw new AreaRefusedException(refusal);

            var site = SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0);
            OsmLayers.Result result;
            using (var tracker = new ProgressTracker(progress, meter))
            {
                tracker.DefineStage(Stage, 1);
                tracker.BeginStage(Stage);
                result = await OsmLayers.FetchAsync(osm, site, tracker, meter, ct).ConfigureAwait(false);
                tracker.Finish();
            }
            if (result.Core == null || result.Ring == null) throw new OsmUnavailableException(result.Failure ?? "OpenStreetMap is unavailable.");
            ct.ThrowIfCancellationRequested();

            // Leftovers of a run cut short are unlisted, so nothing reads them: they go before the new ones are written.
            var unlisted = OsmLayers.LayerIds.Where(id => manifest.Layers.All(l => l.Id != id)).Select(FileOf).ToList();
            foreach (string file in unlisted) File.Delete(Path.Combine(packageFolder, file));
            OsmLayers.Add(manifest, packageFolder, site, result, onlyMissing: true);
            step?.Invoke(RekeyStep.LayersWritten);

            string newId = ResortPackage.ComputeId(manifest);
            if (AreaRekey.Collides(dataRoot, packageFolder, newId))
            {
                foreach (string file in unlisted) File.Delete(Path.Combine(packageFolder, file));
                throw new AreaRefusedException("Another area in your library is this same area with water and roads already. Open that one instead.");
            }
            return AreaRekey.Move(dataRoot, packageFolder, manifest, oldId, step);
        }

        static string FileOf(string layerId) => layerId == RoadsFile.LayerId ? RoadsFile.FileName : layerId + ".grid";
    }
}
