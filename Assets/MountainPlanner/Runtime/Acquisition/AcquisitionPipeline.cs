#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Acquisition.Providers;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Domain.Terrain;
using MountainPlanner.Persistence;

namespace MountainPlanner.Acquisition
{
    /// <summary>What to download: a named site square (0.3 §6.1).</summary>
    public sealed class SiteRequest
    {
        public string Name = "";
        public GeoPoint Centre;
        public double SizeKm = 2;
    }

    /// <summary>
    /// Downloads a site and writes its resort package (0.3 §6 pipeline; task 04a covers terrain).
    /// Resumable: every remote read goes through <see cref="DiskCache"/>, so a killed run continues
    /// where it stopped. Idempotent: the same inputs give an identical package id.
    /// </summary>
    public sealed class AcquisitionPipeline
    {
        public const string CoreLayer = "heights-core";
        public const string RingLayer = "heights-ring";

        public const string StageCore = "Terrain";
        public const string StageRing = "Terrain surroundings";
        public const string StageBuild = "Building";

        readonly DiskCache _cache;

        public AcquisitionPipeline(string cacheFolder)
        {
            _cache = new DiskCache(cacheFolder);
        }

        public async Task<PackageManifest> RunAsync(SiteRequest request, string packageFolder, IProgress<AcquisitionProgress>? progress, CancellationToken ct)
        {
            var site = SiteSquare.Create(request.Centre, request.SizeKm);
            var meter = new TransferMeter();
            using (var tracker = new ProgressTracker(progress, meter))
            {
                var s1m = new S1mTiles(_cache, meter);
                var assembler = new HeightAssembler(s1m, new Dep3Service(_cache, meter), meter);

                // Plan first, so the overall bar and time remaining are honest from the start.
                tracker.DefineStage(StageCore, 1);
                tracker.DefineStage(StageRing, 1);
                tracker.DefineStage(StageBuild, 1);
                tracker.BeginStage(StageCore);
                tracker.BeginStep("checking S1M coverage", 1, 1, () => 0.5);
                var corePlan = await assembler.PlanAsync(site.CoreGrid, 0, ct).ConfigureAwait(false);
                var ringPlan = await assembler.PlanAsync(site.RingGrid, 1, ct).ConfigureAwait(false);
                tracker.SetStageWeight(StageCore, corePlan.ExpectedBytes);
                tracker.SetStageWeight(StageRing, ringPlan.ExpectedBytes);
                // Building takes roughly as long as downloading a few MB; weight it by cells.
                tracker.SetStageWeight(StageBuild, (site.CoreGrid.CellCount + site.RingGrid.CellCount) * 0.5);

                var core = await assembler.ExecuteAsync(corePlan, tracker, classify: true, ct).ConfigureAwait(false);
                tracker.BeginStage(StageRing);
                var ring = await assembler.ExecuteAsync(ringPlan, tracker, classify: false, ct).ConfigureAwait(false);

                tracker.BeginStage(StageBuild);
                var manifest = Build(request, site, corePlan, core, ringPlan, ring, packageFolder, tracker);
                tracker.Finish();
                return manifest;
            }
        }

        static PackageManifest Build(SiteRequest request, SiteSquare site, HeightPlan corePlan, HeightResult core,
                                     HeightPlan ringPlan, HeightResult ring, string folder, ProgressTracker tracker)
        {
            Directory.CreateDirectory(folder);
            var geo = Albers6350.Inverse(site.Centre);
            var scale = site.Scale;
            var manifest = new PackageManifest
            {
                Site = new SiteInfo
                {
                    Name = request.Name, Latitude = Math.Round(geo.Latitude, 6), Longitude = Math.Round(geo.Longitude, 6),
                    CentreX = site.Centre.X, CentreY = site.Centre.Y, SizeMetres = site.SizeMetres, RingMetres = (int)SiteSquare.RingMetres,
                },
                Crs = new CrsInfo
                {
                    ScaleParallel = Math.Round(scale.Parallel, 9), ScaleMeridian = Math.Round(scale.Meridian, 9),
                    GridConvergenceDegrees = Math.Round(Albers6350.GridConvergence(geo.Longitude), 6),
                },
                CreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                Tool = "MountainPlanner.Acquisition 0.1",
            };

            tracker.BeginStep("compressing heights", 1, 2, () => 0.3);
            var cg = site.CoreGrid;
            ResortPackage.AddLayer(folder, manifest, CoreLayer, new GridHeader(GridValueType.Float32, cg.Columns, cg.Rows, cg.West, cg.North, cg.CellSize), core.Heights);
            tracker.BeginStep("compressing heights", 2, 2, () => 0.3);
            var rg = site.RingGrid;
            ResortPackage.AddLayer(folder, manifest, RingLayer, new GridHeader(GridValueType.Float32, rg.Columns, rg.Rows, rg.West, rg.North, rg.CellSize), ring.Heights);

            long total = core.SourceCells.Values.Sum();
            manifest.Quality = new QualityInfo
            {
                Score = TerrainQuality.Score(core.SourceCells),
                OneLiner = TerrainQuality.OneLiner(core.SourceCells),
                SourceShares = core.SourceCells.OrderBy(kv => kv.Key)
                    .ToDictionary(kv => TerrainQuality.Label(kv.Key), kv => Math.Round((double)kv.Value / total, 4)),
            };

            var tiles = corePlan.Tiles.Union(ringPlan.Tiles).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (tiles.Count > 0)
                manifest.Provenance.Add(new ProvenanceInfo
                {
                    Layer = CoreLayer + ", " + RingLayer, Provider = "USGS 3DEP", Product = "Standard 1 m DEM (S1M), 2 m overview for the ring",
                    Items = tiles,
                });
            var fallback = core.FallbackSources.Union(ring.FallbackSources).ToList();
            if (core.FallbackSectors + ring.FallbackSectors > 0)
                manifest.Provenance.Add(new ProvenanceInfo
                {
                    Layer = CoreLayer + ", " + RingLayer, Provider = "USGS 3DEP",
                    Product = $"3DEPElevation dynamic service, {core.FallbackSectors + ring.FallbackSectors} sectors, 50 m blend",
                    Items = fallback,
                });
            manifest.Attribution.Add("Elevation: U.S. Geological Survey, 3D Elevation Program (public domain).");

            tracker.BeginStep("writing manifest", 1, 1, () => 0.9);
            ResortPackage.WriteManifest(folder, manifest);
            return manifest;
        }
    }
}
