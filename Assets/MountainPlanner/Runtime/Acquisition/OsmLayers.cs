#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Roads;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition
{
    /// <summary>
    /// OpenStreetMap water, developed land and roads for a package (task P3-16): fetched and rasterized, then added to
    /// the manifest. A download uses it, and so can getting water and roads later for an area that was downloaded
    /// without them; adding twice never leaves both the "unavailable" entry and the real one.
    /// </summary>
    internal static class OsmLayers
    {
        /// <summary>The OSM layers a package has once water and roads are in it.</summary>
        public static readonly string[] LayerIds = { AcquisitionPipeline.OsmCoreLayer, AcquisitionPipeline.OsmRingLayer, RoadsFile.LayerId };

        public const string Provider = "OpenStreetMap contributors";
        public const string Attribution = "Water and roads: © OpenStreetMap contributors (ODbL).";
        /// <summary>The provenance layer recorded when water and roads couldn't be had.</summary>
        public const string NoneLayer = "(none)";
        /// <summary>The provenance reason when the player chose Skip water and roads (task P3-16, BB5).</summary>
        public const string SkippedReason = "Skipped by the player while OpenStreetMap was holding the download up.";

        internal sealed class Result
        {
            public byte[]? Core, Ring;
            public int Shapes;
            public List<Road>? Roads;
            public string? Failure;
        }

        /// <summary>
        /// OpenStreetMap water and developed land. Optional: if Overpass is down, the package is built without it and
        /// WorldCover's water and built-up classes stand in (recorded in provenance). <paramref name="tracker"/> may be null.
        /// Cancelling <paramref name="skipOptional"/> (the player's Skip water and roads) leaves the layer out the same way,
        /// without touching the network if it was already cancelled.
        /// </summary>
        public static async Task<Result> FetchAsync(OsmFeatures osm, SiteSquare site, ProgressTracker? tracker, TransferMeter meter, CancellationToken ct,
                                                    CancellationToken skipOptional = default)
        {
            if (skipOptional.IsCancellationRequested) return Skipped(tracker);
            long start = meter.Bytes;
            double rasterized = 0;
            tracker?.BeginStep("downloading map features", 1, 3, () => Math.Min(0.95, (meter.Bytes - start) / 4e6), Servers.OpenStreetMap);
            byte[] response;
            try
            {
                using (var either = CancellationTokenSource.CreateLinkedTokenSource(ct, skipOptional))
                    response = await osm.DownloadAsync(site.Ring, either.Token, host => tracker?.SetServer(Servers.NameOf(host))).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (skipOptional.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return Skipped(tracker);
            }
            // Overpass being down, busy or slow is a missing optional layer (task P3-16); the connection going (or offline
            // mode) is not: OsmFeatures lets that through, and the download waits for it rather than build a package
            // without water and roads (task P2-06).
            catch (OsmUnavailableException ex) when (!ct.IsCancellationRequested)
            {
                tracker?.BeginStep("map features unavailable · skipped, satellite land cover used instead", 3, 3, () => 1);
                return new Result { Failure = ex.Message };
            }
            var shapes = OsmFeatures.Parse(response);
            tracker?.BeginStep($"drawing {shapes.Count:N0} map features, core at 1 m", 2, 3, () => rasterized);
            var (cw, cd) = OsmFeatures.Rasterize(shapes, site.CoreGrid);
            rasterized = 1;
            tracker?.BeginStep($"drawing {shapes.Count:N0} map features, surroundings at 2 m", 3, 3, () => 0.5);
            var (rw, rd) = OsmFeatures.Rasterize(shapes, site.RingGrid);
            return new Result { Core = OsmFeatures.Interleave(cw, cd), Ring = OsmFeatures.Interleave(rw, rd), Shapes = shapes.Count, Roads = OsmFeatures.ParseRoads(response) };
        }

        static Result Skipped(ProgressTracker? tracker)
        {
            tracker?.BeginStep("map features skipped · satellite land cover used instead", 3, 3, () => 1);
            return new Result { Failure = SkippedReason };
        }

        /// <summary>
        /// Adds <paramref name="osm"/> to the manifest: the layers, roads, provenance and attribution, or the
        /// "unavailable" provenance entry. Any earlier OpenStreetMap provenance entry is replaced, and the attribution is
        /// added once. The caller writes the manifest.
        /// </summary>
        public static void Add(PackageManifest manifest, string folder, SiteSquare site, Result osm)
        {
            manifest.Provenance.RemoveAll(p => p.Provider == Provider);
            if (osm.Core == null || osm.Ring == null)
            {
                manifest.Provenance.Add(new ProvenanceInfo
                {
                    Layer = NoneLayer, Provider = Provider, Product = "Unavailable during this download; WorldCover water and built-up used instead",
                    Items = new List<string> { osm.Failure ?? "" },
                });
                return;
            }
            const string units = "OpenStreetMap coverage 0-255, bands: water, developed";
            var cg = site.CoreGrid;
            ResortPackage.AddLayer(folder, manifest, AcquisitionPipeline.OsmCoreLayer, new GridHeader(GridValueType.UInt8, cg.Columns * 2, cg.Rows, cg.West, cg.North, cg.CellSize), osm.Core, 2, units);
            var rg = site.RingGrid;
            ResortPackage.AddLayer(folder, manifest, AcquisitionPipeline.OsmRingLayer, new GridHeader(GridValueType.UInt8, rg.Columns * 2, rg.Rows, rg.West, rg.North, rg.CellSize), osm.Ring, 2, units);
            manifest.Provenance.Add(new ProvenanceInfo
            {
                Layer = AcquisitionPipeline.OsmCoreLayer + ", " + AcquisitionPipeline.OsmRingLayer, Provider = Provider,
                Product = "Water, roads, buildings and built-up land use via the Overpass API",
                Items = new List<string> { $"{osm.Shapes} features" },
            });
            if (osm.Roads != null) RoadsFile.Add(folder, manifest, osm.Roads);   // the roads themselves (task 12d)
            if (!manifest.Attribution.Contains(Attribution)) manifest.Attribution.Add(Attribution);
        }
    }
}
