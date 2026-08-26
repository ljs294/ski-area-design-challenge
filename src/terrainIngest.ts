// Orchestrates supported live resort preparation into a verified, persisted
// TerrainRecord.
import type { CoverDisplayMetadata, CoverGrid, SiteCoverGrid } from './types/cover';
import type { AreaSizeMeters, LocalImageryMetadata, TerrainPackageProgress, TerrainRecord } from './types/terrain';
import type { VectorFeatureSet } from './types/vectorFeatures';
import { fetchElevationBuffer, fetchElevationGrid, type SurroundGrid } from './elevation';
import type { LatLonBounds } from './types/geo';
import { bicubicUpscale } from './bicubicUpscale';
import { generateProceduralClimate } from './climate';
import { deleteTerrain, loadTerrain, saveTerrain } from './terrainStorageClient';
import { fetchVectorFeatures, type MapContextProviderError } from './vectorFeatures';
import { contourMetadataOf, coverDisplayMetadataOf, coverGeometryMetadataOf, coverMetadataOf, imageryMetadataOf, manifestOf, originalCoverMetadataOf, validateTerrainPackage } from './terrainPackage';
import { traceContours } from './marchingSquares';
import { deriveCoverBoundarySegments } from './coverAnalysis';
import { deriveCoverDisplayGeometry, type DerivedCoverDisplay } from './coverDisplay';
import { isFourClassGrid } from './fourClassCover';
import { fetchNaipAcquisition } from './usgsTerrainCover';
import { prepareTerrainCover } from './terrainPreparationClient';

const CONTOUR_GRID_SIZE = 512;

function slugify(name: string): string {
  const slug = name
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return slug || 'mountain';
}

function makeKey(mountainName: string, latitude: number, longitude: number): string {
  return `${slugify(mountainName)}-${latitude.toFixed(4)}_${longitude.toFixed(4)}`;
}

async function finalizeAndSave(
  mountainName: string,
  latitude: number,
  longitude: number,
  areaSizeMeters: AreaSizeMeters,
  bounds: LatLonBounds,
  sampleHeights: number[],
  sourceType: TerrainRecord['sourceType'],
  vectorFeatures?: VectorFeatureSet,
  coverGrid?: CoverGrid,
  preparedCoverDisplay?: DerivedCoverDisplay,
  originalCoverGrid?: SiteCoverGrid,
  localImagery?: Uint8Array,
  localImageryMetadata?: LocalImageryMetadata,
  surround?: SurroundGrid
): Promise<TerrainRecord> {
  const sampleGridSize = Math.round(Math.sqrt(sampleHeights.length));
  const contourGridSize = Math.min(CONTOUR_GRID_SIZE, sampleGridSize);
  const contourIntervalM = 6.096; // 20 ft minor contours, matching the master-plan reference density.
  let contourSegments: number[] | undefined;
  let coverBoundarySegments: number[] | undefined;
  let coverDisplayGeometry: number[] | Float32Array | undefined;
  let coverDisplayMetadata: CoverDisplayMetadata | undefined;
  if (coverGrid) {
    const contourHeights = sampleGridSize === contourGridSize
      ? sampleHeights
      : bicubicUpscale(sampleHeights, sampleGridSize, contourGridSize);
    const traced = traceContours(contourHeights, contourGridSize, 1, contourIntervalM);
    contourSegments = traced.flatMap((s) => [s.x1, s.y1, s.x2, s.y2, s.level]);
    coverBoundarySegments = deriveCoverBoundarySegments(coverGrid);
    const display = preparedCoverDisplay ?? deriveCoverDisplayGeometry(coverGrid);
    coverDisplayGeometry = display.geometry;
    coverDisplayMetadata = coverDisplayMetadataOf(display.geometry, display.stats);
  }

  const sum = sampleHeights.reduce((a, b) => a + b, 0);
  const avgAlt = sum / sampleHeights.length;
  const climate = generateProceduralClimate(latitude, avgAlt);

  const now = new Date().toISOString();
  let record: TerrainRecord = {
    schemaVersion: coverGrid ? isFourClassGrid(coverGrid) ? 6 : 5 : 3,
    key: makeKey(mountainName, latitude, longitude),
    mountainName,
    latitude,
    longitude,
    areaSizeMeters,
    bounds,
    sampleGridSize,
    sampleHeights,
    surround,
    climate,
    vectorFeatures,
    coverGrid,
    coverMetadata: coverGrid ? coverMetadataOf(coverGrid) : undefined,
    originalCoverGrid,
    originalCoverMetadata: originalCoverGrid ? originalCoverMetadataOf(originalCoverGrid) : undefined,
    coverBoundarySegments,
    coverGeometryMetadata: coverBoundarySegments ? coverGeometryMetadataOf(coverBoundarySegments) : undefined,
    coverDisplayGeometry,
    coverDisplayMetadata,
    localImagery,
    localImageryMetadata,
    contourSegments,
    contourMetadata: contourSegments ? contourMetadataOf(contourSegments, contourGridSize, contourIntervalM) : undefined,
    sourceType,
    createdAt: now,
    updatedAt: now,
  };

  if (coverGrid) {
    record = { ...record, packageManifest: manifestOf(record) };
    const validation = validateTerrainPackage(record);
    if (!validation.ok) throw new Error(`Invalid resort package: ${validation.errors.join(' ')}`);
  }

  const saveResult = await saveTerrain(record);
  if (!saveResult.ok) {
    throw new Error(`Failed to persist terrain: ${saveResult.error}`);
  }

  if (coverGrid) {
    const persisted = await loadTerrain(record.key);
    if (!persisted) throw new Error('The resort package could not be read after it was written.');
    const validation = validateTerrainPackage(persisted);
    if (!validation.ok) throw new Error(`The saved resort package failed verification: ${validation.errors.join(' ')}`);
    return persisted;
  }

  return record;
}

export interface ResortPreparationSite {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
}

export interface ResortPreparationServices {
  sampleSiteCoverGrid(
    bounds: LatLonBounds,
    targetCellM: number,
    signal?: AbortSignal
  ): Promise<SiteCoverGrid>;
}

export interface ResortPreparationOptions {
  onProgress?: (progress: TerrainPackageProgress) => void;
  signal?: AbortSignal;
  onMapContextFailure?(error: MapContextProviderError): Promise<MapContextDecision>;
}

export type MapContextDecision = 'retry' | 'continue' | 'cancel';

/**
 * Prepare the mandatory local elevation + WorldCover package for gameplay.
 * WorldCover/USGS are contacted only here; the returned terrainKey is what the
 * game subsequently loads through local protocols.
 */
export async function prepareResortPackage(
  site: ResortPreparationSite,
  mountainName: string,
  services: ResortPreparationServices,
  options: ResortPreparationOptions = {}
): Promise<TerrainRecord> {
  const { onProgress, signal } = options;
  const [[west, south], [east, north]] = site.bounds;
  const requestedBounds = { west, south, east, north };
  const areaSizeMeters = Math.round(Math.max(site.widthKm, site.heightKm) * 1000);
  const report = (phase: TerrainPackageProgress['phase'], message: string, completed: number) =>
    onProgress?.({ phase, message, completed, total: 9 });
  const abort = () => {
    if (signal?.aborted) throw new DOMException('Resort preparation cancelled', 'AbortError');
  };

  report('elevation', 'Downloading and validating elevation', 0);
  const elevation = await fetchElevationGrid(
    requestedBounds,
    areaSizeMeters,
    (p) => report('elevation', p.phase === 'fetching' ? 'Downloading elevation' : 'Decoding elevation', 0),
    signal
  );
  abort();

  // The elevation service may return a wider/taller extent than requested (see
  // ElevationGrid.bounds). Adopt that true extent for EVERY layer — ground
  // cover, contours, vectors — so they all register against the same footprint
  // and the satellite imagery.
  const bounds = elevation.bounds;
  const sampleHeights = elevation.heights;
  const center = {
    latitude: (bounds.south + bounds.north) / 2,
    longitude: (bounds.west + bounds.east) / 2,
  };

  // Coarse offline surround ring — fetched in the background alongside the
  // cover/imagery downloads so it costs no extra serial time. Best-effort:
  // resolves null on any failure and the resort just renders without a buffer.
  const surroundPromise = fetchElevationBuffer(bounds, signal);

  report('ground-cover', 'Downloading ESA WorldCover recovery data', 1);
  const originalCoverGrid = await services.sampleSiteCoverGrid(bounds, 10, signal);
  if (!originalCoverGrid.complete) throw new Error(`Ground-cover package is incomplete (${originalCoverGrid.nodataCount} missing cells).`);
  abort();

  report('imagery', 'Downloading public-domain NAIP imagery and map context', 2);
  const [naip, firstContext] = await Promise.all([
    fetchNaipAcquisition(bounds, undefined, signal),
    fetchVectorFeatures(bounds, signal).then(
      (value) => ({ ok: true as const, value }),
      (error: MapContextProviderError) => ({ ok: false as const, error }),
    ),
  ]);
  let vectorFeatures: VectorFeatureSet | undefined;
  let contextResult = firstContext;
  while (!contextResult.ok) {
    abort();
    if (!options.onMapContextFailure) throw contextResult.error;
    const decision = await options.onMapContextFailure(contextResult.error);
    abort();
    if (decision === 'cancel') {
      throw new DOMException('Resort preparation cancelled', 'AbortError');
    }
    if (decision === 'continue') break;
    contextResult = await fetchVectorFeatures(bounds, signal).then(
      (value) => ({ ok: true as const, value }),
      (error: MapContextProviderError) => ({ ok: false as const, error }),
    );
  }
  if (contextResult.ok) vectorFeatures = contextResult.value;
  abort();

  const preparedCover = await prepareTerrainCover({
    bounds,
    original: originalCoverGrid,
    heights: sampleHeights,
    elevationWidth: elevation.width,
    elevationHeight: elevation.height,
    naip,
    vectors: vectorFeatures,
  }, {
    signal,
    onProgress: (phase) => report(
      phase === 'classifying' ? 'decoding' : 'vectorizing-cover',
      phase === 'classifying'
        ? 'Classifying forest, alpine, grassland, and water'
        : 'Drawing detailed tree-cover polygons',
      phase === 'classifying' ? 3 : 4,
    ),
  });
  abort();
  const coverGrid = preparedCover.cover;
  const coverDisplay = preparedCover.display;

  report('deriving', 'Preparing local contours', 5);
  abort();

  const localImagery = naip?.jpeg;
  const localImageryMetadata = localImagery && naip ? imageryMetadataOf(localImagery, {
    bounds: naip.bounds,
    width: naip.width,
    height: naip.height,
    mimeType: 'image/jpeg',
    acquisitionYear: naip.acquisitionYear,
    sceneIds: naip.sceneIds,
    attribution: 'USDA/USGS NAIP orthoimagery · public domain',
  }) : undefined;

  const surround = (await surroundPromise) ?? undefined;
  abort();

  report('saving', 'Saving local resort package', 6);
  const terrain = await finalizeAndSave(
    mountainName,
    center.latitude,
    center.longitude,
    areaSizeMeters,
    bounds,
    sampleHeights,
    'live',
    vectorFeatures,
    coverGrid,
    coverDisplay,
    originalCoverGrid,
    localImagery,
    localImageryMetadata,
    surround
  );
  if (signal?.aborted) {
    await deleteTerrain(terrain.key);
    abort();
  }
  report('verifying', 'Verifying local resort package', 7);
  const validation = validateTerrainPackage(terrain);
  if (!validation.ok) {
    await deleteTerrain(terrain.key);
    throw new Error(validation.errors.join(' '));
  }
  report('verifying', 'Finalizing four-class terrain cover', 8);
  report('verifying', 'Resort package ready', 9);
  return terrain;
}
