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
using MountainPlanner.Domain.Flora;
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
    /// Downloads a site and writes its resort package (0.3 §6 pipeline; tasks 04a and 04b):
    /// terrain, forest canopy, land cover and tree species. OSM water and developed land come in task 07.
    /// Resumable: every remote read goes through <see cref="DiskCache"/>, so a killed run continues
    /// where it stopped. Idempotent: the same inputs give an identical package id.
    /// </summary>
    public sealed class AcquisitionPipeline
    {
        public const string CoreLayer = "heights-core";
        public const string RingLayer = "heights-ring";

        public const string CanopyLayer = "canopy-core";
        public const string CoverLayer = "cover";
        public const string SpeciesIdsLayer = "species-ids";
        public const string SpeciesWeightsLayer = "species-weights";

        public const string StageCore = "Terrain";
        public const string StageRing = "Terrain surroundings";
        public const string StageForest = "Forest";
        public const string StageCover = "Ground cover";
        public const string StageSpecies = "Tree species";
        public const string StageBuild = "Building";
        public const string StagePrepare = "Preparing terrain";

        public const double CoverCellMetres = 10;
        public const double SpeciesCellMetres = 30;

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
                var covers = new CoverAssembler(new CanopyTiles(_cache, meter), new WorldCoverTiles(_cache, meter), new BigmapService(_cache, meter), meter);
                var coverGrid = GridSpec.Covering(site.Ring, CoverCellMetres);
                var ringBox = site.Ring;
                var speciesGrid = new GridSpec(ringBox.West, ringBox.North, SpeciesCellMetres,
                    (int)Math.Ceiling(ringBox.Width / SpeciesCellMetres), (int)Math.Ceiling(ringBox.Height / SpeciesCellMetres));

                // Plan first, so the overall bar and time remaining are honest from the start.
                foreach (string stage in new[] { StageCore, StageRing, StageForest, StageCover, StageSpecies, StageBuild, StagePrepare }) tracker.DefineStage(stage, 1);
                tracker.BeginStage(StageCore);
                tracker.BeginStep("planning the download", 1, 1, () => 0.5);
                var corePlan = await assembler.PlanAsync(site.CoreGrid, 0, ct).ConfigureAwait(false);
                var ringPlan = await assembler.PlanAsync(site.RingGrid, 1, ct).ConfigureAwait(false);
                var canopyPlan = await covers.PlanCanopyAsync(site.CoreGrid, ct).ConfigureAwait(false);
                var coverPlan = await covers.PlanCoverAsync(coverGrid, ct).ConfigureAwait(false);
                var speciesLayers = await covers.PlanSpeciesAsync(speciesGrid, ct).ConfigureAwait(false);
                tracker.SetStageWeight(StageCore, corePlan.ExpectedBytes);
                tracker.SetStageWeight(StageRing, ringPlan.ExpectedBytes);
                tracker.SetStageWeight(StageForest, canopyPlan.Bytes);
                tracker.SetStageWeight(StageCover, coverPlan.Bytes);
                tracker.SetStageWeight(StageSpecies, CoverAssembler.SpeciesWeight(speciesLayers.Count));
                // Building takes roughly as long as downloading a few MB; weight it by cells.
                tracker.SetStageWeight(StageBuild, (site.CoreGrid.CellCount + site.RingGrid.CellCount) * 0.5);
                // Preparing the terrain cache samples every tile vertex once (task 05).
                var tileGrid = TileGrid.For(site);
                tracker.SetStageWeight(StagePrepare, tileGrid.All().Sum(k => (double)tileGrid.Resolution(k) * tileGrid.Resolution(k)) * 0.4);

                var core = await assembler.ExecuteAsync(corePlan, tracker, classify: true, ct).ConfigureAwait(false);
                tracker.BeginStage(StageRing);
                var ring = await assembler.ExecuteAsync(ringPlan, tracker, classify: false, ct).ConfigureAwait(false);
                tracker.BeginStage(StageForest);
                var (canopy, canopyMissing) = await covers.CanopyAsync(canopyPlan, tracker, ct).ConfigureAwait(false);
                tracker.BeginStage(StageCover);
                byte[] cover = await covers.CoverAsync(coverPlan, tracker, ct).ConfigureAwait(false);
                tracker.BeginStage(StageSpecies);
                var species = await covers.SpeciesAsync(speciesGrid, speciesLayers, tracker, ct).ConfigureAwait(false);

                tracker.BeginStage(StageBuild);
                var manifest = Build(request, site, corePlan, core, ringPlan, ring, packageFolder, tracker);
                AddCover(manifest, packageFolder, site, canopyPlan, canopy, coverPlan, cover, speciesGrid, species);
                tracker.BeginStep("scoring flora", 1, 1, () => 0.5);
                AddFloraScore(manifest, site, canopy, canopyMissing, coverPlan.Grid, cover, speciesGrid, species);
                tracker.BeginStep("writing manifest", 1, 1, () => 0.9);
                ResortPackage.WriteManifest(packageFolder, manifest);

                tracker.BeginStage(StagePrepare);
                TerrainCache.Build(packageFolder, manifest, new TileProgress(tracker), ct);
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

            return manifest;
        }

        /// <summary>Forwards cache-building progress to the tracker as "preparing terrain tile 17 of 121".</summary>
        sealed class TileProgress : IProgress<CacheProgress>
        {
            readonly ProgressTracker _tracker;
            public TileProgress(ProgressTracker tracker) => _tracker = tracker;
            public void Report(CacheProgress p) => _tracker.BeginStep("preparing terrain tile", p.Tile, p.Tiles, () => 0);
        }

        /// <summary>Year of the oldest flora imagery: Meta/WRI canopy uses 2017–2020 imagery.</summary>
        public const int FloraDataYear = 2017;

        static void AddFloraScore(PackageManifest manifest, SiteSquare site, byte[] canopy, long canopyMissing, GridSpec coverGrid, byte[] cover,
                                  GridSpec speciesGrid, CoverAssembler.SpeciesResult species)
        {
            var (agreement, speciesCoverage) = CoverAssembler.MeasureFlora(site.CoreGrid, canopy, coverGrid, cover, speciesGrid, species.Ids);
            double modelled = species.Species.Where(s => TreeLibrary.ModelledSpecies.Contains(s.Spcd)).Sum(s => s.ShareOfBiomass)
                              / Math.Max(1e-9, species.Species.Sum(s => s.ShareOfBiomass));
            var inputs = new FloraInputs(1 - canopyMissing / (double)canopy.Length, speciesCoverage, agreement,
                                         species.Species.Count > 0 ? modelled : 0, FloraDataYear, DateTime.UtcNow.Year);
            manifest.Flora = new FloraQualityInfo
            {
                Score = FloraQuality.Score(inputs),
                OneLiner = FloraQuality.OneLiner(inputs, "canopy 1 m (2017–2020 imagery)", "species from BIGMAP 30 m"),
                Components = FloraQuality.Components(inputs).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
        }

        static void AddCover(PackageManifest manifest, string folder, SiteSquare site, CoverPlan canopyPlan, byte[] canopy,
                             CoverPlan coverPlan, byte[] cover, GridSpec speciesGrid, CoverAssembler.SpeciesResult species)
        {
            var cg = site.CoreGrid;
            ResortPackage.AddLayer(folder, manifest, CanopyLayer, new GridHeader(GridValueType.UInt8, cg.Columns, cg.Rows, cg.West, cg.North, cg.CellSize),
                canopy, 1, "canopy height in 0.25 m steps (0 = open, 255 = 63.75 m or more)");
            var vg = coverPlan.Grid;
            ResortPackage.AddLayer(folder, manifest, CoverLayer, new GridHeader(GridValueType.UInt8, vg.Columns, vg.Rows, vg.West, vg.North, vg.CellSize),
                cover, 1, "ESA WorldCover 2021 class (10 trees, 20 shrubs, 30 grass, 60 bare, 70 snow and ice, 80 water, 50 built-up)");
            var sg = speciesGrid;
            ResortPackage.AddLayer(folder, manifest, SpeciesIdsLayer, new GridHeader(GridValueType.UInt8, sg.Columns * 4, sg.Rows, sg.West, sg.North, sg.CellSize),
                species.Ids, 4, "top four species per cell, as 1-based indices into Species (0 = none)");
            ResortPackage.AddLayer(folder, manifest, SpeciesWeightsLayer, new GridHeader(GridValueType.UInt8, sg.Columns * 4, sg.Rows, sg.West, sg.North, sg.CellSize),
                species.Weights, 4, "each top species' share of those four (0–255)");
            manifest.Species.Clear();
            manifest.Species.AddRange(species.Species);

            manifest.Provenance.Add(new ProvenanceInfo
            {
                Layer = CanopyLayer, Provider = "Meta and World Resources Institute", Product = "High Resolution Canopy Height Maps (v1, 1 m, core only)",
                Items = canopyPlan.Sources.ToList(),
            });
            manifest.Provenance.Add(new ProvenanceInfo { Layer = CoverLayer, Provider = "ESA", Product = "WorldCover 2021 v200 (10 m)", Items = coverPlan.Sources.ToList() });
            manifest.Provenance.Add(new ProvenanceInfo
            {
                Layer = SpeciesIdsLayer + ", " + SpeciesWeightsLayer, Provider = "USDA Forest Service", Product = "FIA BIGMAP 2018 tree species aboveground biomass (30 m)",
                Items = new List<string> { $"{species.LayersCovering} species layers cover the site; {species.Species.Count} present" },
            });
            manifest.Attribution.Add("Canopy height: © Meta and World Resources Institute, High Resolution Canopy Height Maps (CC BY 4.0).");
            manifest.Attribution.Add("Land cover: © ESA WorldCover project 2021, contains modified Copernicus Sentinel data (2021) processed by the ESA WorldCover consortium (CC BY 4.0).");
            manifest.Attribution.Add("Tree species: USDA Forest Service, Forest Inventory and Analysis, BIGMAP 2018 (public domain).");
        }
    }
}
