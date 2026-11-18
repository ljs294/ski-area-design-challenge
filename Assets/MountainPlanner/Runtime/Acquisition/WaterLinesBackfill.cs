#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition
{
    /// <summary>
    /// Stream lines and water names for an older area (task 4.14, WS-08, LK-10): an area whose water and roads came before
    /// 4.14 has the rasterised water but no water.json, so its dams fall back to the streams body and its lakes have no
    /// names. This fetches OpenStreetMap once more and writes water.json, then the manifest (the commit point). Like Get
    /// weather, the area keeps its id (the Water entry isn't part of it), so no save moves and the area may stay open.
    /// </summary>
    public static class WaterLinesBackfill
    {
        /// <summary>Whether the area lacks water.json but has its OpenStreetMap water (Get water and roads adds both).</summary>
        public static bool Needed(PackageManifest m) => m.Water == null && !WaterAndRoads.Needed(m);

        /// <summary>
        /// Runs it; returns false when the area needed nothing. Throws <see cref="OsmUnavailableException"/> when Overpass
        /// gives nothing, and the network's own errors when the connection is down; the area is then left as it was.
        /// </summary>
        public static Task<bool> RunAsync(string packageFolder, string cacheFolder, CancellationToken ct)
        {
            var meter = new TransferMeter();
            return RunAsync(packageFolder, new OsmFeatures(new DiskCache(cacheFolder), meter), meter, ct);
        }

        /// <summary>Tests pass their own <paramref name="osm"/> (a recorded response).</summary>
        internal static async Task<bool> RunAsync(string packageFolder, OsmFeatures osm, TransferMeter meter, CancellationToken ct)
        {
            var manifest = ResortPackage.ReadManifest(packageFolder);   // a newer game's package throws FormatTooNewException
            if (!Needed(manifest)) return false;
            var site = SiteSquare.Create(new AlbersPoint(manifest.Site.CentreX, manifest.Site.CentreY), manifest.Site.SizeMetres / 1000.0);
            var result = await OsmLayers.FetchAsync(osm, site, null, meter, ct).ConfigureAwait(false);
            if (result.Streams == null) throw new OsmUnavailableException(result.Failure ?? "OpenStreetMap is unavailable.");
            ct.ThrowIfCancellationRequested();

            // Re-read: the player may have renamed the area meanwhile; every field but Water stays as it is on disk now.
            manifest = ResortPackage.ReadManifest(packageFolder);
            if (!Needed(manifest)) return false;
            string id = manifest.PackageId;
            WaterFile.Add(packageFolder, manifest, result.Streams, result.WaterNames ?? new List<Domain.Water.WaterName>());
            ResortPackage.WriteManifest(packageFolder, manifest);
            if (manifest.PackageId != id) throw new InvalidDataException("Adding the water lines moved the area's id.");   // never: Water isn't in the id
            return true;
        }
    }
}
