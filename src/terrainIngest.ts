// Orchestrates turning either a live map selection or a curated preset into
// a fully-built TerrainDB: fetch/generate the raw sample grid, upscale it
// for display, attach a climate profile, persist it, and return it.
import type { AreaSizeMeters, SiteCoverGrid, TerrainDB, TerrainPackageProgress, TerrainRecord, VectorFeatureSet } from './types';
import { fetchElevationGrid, sampleGridSizeFor, type LatLonBounds, type ElevationProgress } from './elevation';
import { bicubicUpscale } from './bicubicUpscale';
import { generateProceduralClimate } from './climate';
import { generateProceduralHeights, NA_MOUNTAIN_PRESETS } from './mountainPresets';
import { deleteTerrain, loadTerrain, saveTerrain } from './terrainStorageClient';
import { boundsForSquareMeters } from './geo';
import { fetchVectorFeatures, hydrateVectorFeatures } from './vectorFeatures';
import { MAP_SIZE } from './renderer';
import { sampleSiteCoverGrid } from './app/worldcoverProtocol';
import { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf, manifestOf, validateTerrainPackage } from './terrainPackage';
import { traceContours } from './marchingSquares';
import { deriveCoverBoundarySegments } from './coverAnalysis';

export const DISPLAY_GRID_SIZE = 512;

/**
 * Fetch a curated preset's bundled real elevation data, if present.
 * Produced offline via `npm run download-preset -- <id>`
 * (scripts/downloadPresetTerrain.ts) as a raw Float32 binary file under
 * public/presetTerrain/ — plain static passthrough (Vite copies public/
 * verbatim, no transform), not a JSON import. Serving/parsing the
 * equivalent data as JSON was found to outright crash Vite's dev server
 * once these grids grew past tens of megabytes; a `public/` static fetch
 * sidesteps that entirely and is ~4.5x smaller on the wire besides (4
 * bytes/point raw binary vs ~18 bytes/point as JSON text).
 * Returns null if this preset has no bundled file (caller falls back to
 * procedural terrain).
 */
async function loadBundledPresetHeights(presetId: string): Promise<number[] | null> {
  let response: Response;
  try {
    response = await fetch(`/presetTerrain/${presetId}.heights.bin`);
  } catch {
    return null;
  }
  if (!response.ok) return null;

  const buf = await response.arrayBuffer();
  return Array.from(new Float32Array(buf));
}

/**
 * Fetch a curated preset's bundled real vector features (roads, water,
 * peaks, land cover), if present. Produced offline via the same
 * download-preset script as the heightmap. Presets never hit Overpass at
 * runtime — only live map-picker ingests do — so a missing bundle just
 * means that preset renders without overlays, not an error.
 */
async function loadBundledPresetVectors(presetId: string): Promise<VectorFeatureSet | null> {
  let response: Response;
  try {
    response = await fetch(`/presetTerrain/${presetId}.vectors.json`);
  } catch {
    return null;
  }
  if (!response.ok) return null;

  try {
    return (await response.json()) as VectorFeatureSet;
  } catch {
    return null;
  }
}

/**
 * The elevation raster's TRUE extent, written alongside a preset's bundled
 * heights (see scripts/downloadPresetTerrain.ts). Older bundles predate the
 * sidecar and return null, so the caller falls back to the computed
 * square-in-meters bounds — those bundles are misregistered until re-downloaded.
 */
async function loadBundledPresetBounds(presetId: string): Promise<LatLonBounds | null> {
  let response: Response;
  try {
    response = await fetch(`/presetTerrain/${presetId}.meta.json`);
  } catch {
    return null;
  }
  if (!response.ok) return null;
  try {
    const meta = (await response.json()) as { bounds?: LatLonBounds };
    return meta.bounds ?? null;
  } catch {
    return null;
  }
}

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

/**
 * Rebuild a TerrainDB (upscaled display grid + derived fields) from a
 * persisted TerrainRecord, without re-saving it. Used both internally by
 * finalizeAndSave and by the Content Manager's "Load" action.
 */
export function hydrateTerrainRecord(record: TerrainRecord): TerrainDB {
  const displayHeights = bicubicUpscale(record.sampleHeights, record.sampleGridSize, DISPLAY_GRID_SIZE);
  // schemaVersion-2 records predate `bounds` — recompute it the same way it
  // would have been derived, so old saves still load (without vector
  // features, which they never had either).
  const bounds = record.bounds ?? boundsForSquareMeters(record.latitude, record.longitude, record.areaSizeMeters);
  const hydratedFeatures = hydrateVectorFeatures(record.vectorFeatures, bounds, MAP_SIZE);

  return {
    ...record,
    bounds,
    hydratedFeatures,
    displayGridSize: DISPLAY_GRID_SIZE,
    displayHeights,
    widthMeters: record.areaSizeMeters,
    heightMeters: record.areaSizeMeters,
  };
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
  coverGrid?: SiteCoverGrid
): Promise<TerrainDB> {
  const sampleGridSize = Math.round(Math.sqrt(sampleHeights.length));
  const contourGridSize = Math.min(DISPLAY_GRID_SIZE, sampleGridSize);
  const contourIntervalM = 6.096; // 20 ft minor contours, matching the master-plan reference density.
  let contourSegments: number[] | undefined;
  let coverBoundarySegments: number[] | undefined;
  if (coverGrid) {
    const contourHeights = sampleGridSize === contourGridSize
      ? sampleHeights
      : bicubicUpscale(sampleHeights, sampleGridSize, contourGridSize);
    const traced = traceContours(contourHeights, contourGridSize, 1, contourIntervalM);
    contourSegments = traced.flatMap((s) => [s.x1, s.y1, s.x2, s.y2, s.level]);
    coverBoundarySegments = deriveCoverBoundarySegments(coverGrid);
  }

  const sum = sampleHeights.reduce((a, b) => a + b, 0);
  const avgAlt = sum / sampleHeights.length;
  const climate = generateProceduralClimate(latitude, avgAlt);

  const now = new Date().toISOString();
  let record: TerrainRecord = {
    schemaVersion: coverGrid ? 4 : 3,
    key: makeKey(mountainName, latitude, longitude),
    mountainName,
    latitude,
    longitude,
    areaSizeMeters,
    bounds,
    sampleGridSize,
    sampleHeights,
    climate,
    vectorFeatures,
    coverGrid,
    coverMetadata: coverGrid ? coverMetadataOf(coverGrid) : undefined,
    coverBoundarySegments,
    coverGeometryMetadata: coverBoundarySegments ? coverGeometryMetadataOf(coverBoundarySegments) : undefined,
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
    return hydrateTerrainRecord(persisted);
  }

  return hydrateTerrainRecord(record);
}

export interface ResortPreparationSite {
  bounds: [[number, number], [number, number]];
  widthKm: number;
  heightKm: number;
}

/**
 * Prepare the mandatory local elevation + WorldCover package for gameplay.
 * WorldCover/USGS are contacted only here; the returned terrainKey is what the
 * game subsequently loads through local protocols.
 */
export async function prepareResortPackage(
  site: ResortPreparationSite,
  mountainName: string,
  onProgress?: (progress: TerrainPackageProgress) => void,
  signal?: AbortSignal
): Promise<TerrainDB> {
  const [[west, south], [east, north]] = site.bounds;
  const requestedBounds = { west, south, east, north };
  const areaSizeMeters = Math.round(Math.max(site.widthKm, site.heightKm) * 1000);
  const report = (phase: TerrainPackageProgress['phase'], message: string, completed: number) =>
    onProgress?.({ phase, message, completed, total: 6 });
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

  report('ground-cover', 'Downloading ESA WorldCover 2021', 1);
  const coverGrid = await sampleSiteCoverGrid(bounds, 10, signal);
  if (!coverGrid.complete) throw new Error(`Ground-cover package is incomplete (${coverGrid.nodataCount} missing cells).`);
  abort();

  report('decoding', 'Validating source-faithful land-cover classes', 2);
  abort();

  report('deriving', 'Preparing exact canopy boundaries and local contours', 3);
  const vectorFeatures = await fetchVectorFeatures(bounds).catch(() => undefined);
  abort();

  report('saving', 'Saving local resort package', 4);
  const terrain = await finalizeAndSave(
    mountainName,
    center.latitude,
    center.longitude,
    areaSizeMeters,
    bounds,
    sampleHeights,
    'live',
    vectorFeatures,
    coverGrid
  );
  if (signal?.aborted) {
    await deleteTerrain(terrain.key);
    abort();
  }
  report('verifying', 'Verifying local resort package', 5);
  const validation = validateTerrainPackage(terrain);
  if (!validation.ok) {
    await deleteTerrain(terrain.key);
    throw new Error(validation.errors.join(' '));
  }
  report('verifying', 'Resort package ready', 6);
  return terrain;
}

/**
 * Download real elevation data for a map selection and build a TerrainDB.
 * Vector features (roads/water/peaks/land cover) are fetched from Overpass
 * in parallel with elevation — and are allowed to fail independently, since
 * Overpass is a shared, best-effort community service and a flaky map-
 * feature fetch shouldn't block getting a playable terrain.
 */
export async function ingestLiveArea(
  bounds: LatLonBounds,
  _center: { latitude: number; longitude: number },
  areaSizeMeters: AreaSizeMeters,
  mountainName: string,
  onProgress?: (progress: ElevationProgress) => void
): Promise<TerrainDB> {
  // Elevation first: it may return a wider/taller extent than requested (see
  // ElevationGrid.bounds), and every other layer must be pinned to that true
  // extent — so vectors are fetched against it rather than the request.
  const elevation = await fetchElevationGrid(bounds, areaSizeMeters, onProgress);
  const trueBounds = elevation.bounds;
  const center = {
    latitude: (trueBounds.south + trueBounds.north) / 2,
    longitude: (trueBounds.west + trueBounds.east) / 2,
  };
  const vectorFeatures = await fetchVectorFeatures(trueBounds).catch((e) => {
    console.error('Failed to fetch map features (roads/water/peaks/land cover):', e);
    return undefined;
  });
  return finalizeAndSave(mountainName, center.latitude, center.longitude, areaSizeMeters, trueBounds, elevation.heights, 'live', vectorFeatures);
}

/**
 * Build a TerrainDB for a curated preset mountain. Uses real bundled
 * elevation data and vector features when available
 * (public/presetTerrain/<id>.heights.bin / .vectors.json), otherwise falls
 * back to procedurally generated placeholder elevation with no overlays —
 * both go through the same finalize/upscale pipeline as live downloads.
 * Presets never call Overpass live; see downloadPresetTerrain.ts.
 */
export async function ingestPreset(presetId: string): Promise<TerrainDB> {
  const preset = NA_MOUNTAIN_PRESETS.find((p) => p.id === presetId);
  if (!preset) throw new Error(`Unknown preset: ${presetId}`);

  const areaSizeMeters = preset.areaSizeMeters ?? 4000;
  const [bundledHeights, bundledVectors, bundledBounds] = await Promise.all([
    loadBundledPresetHeights(presetId),
    loadBundledPresetVectors(presetId),
    loadBundledPresetBounds(presetId),
  ]);
  // Prefer the raster's true extent captured at download time; fall back to the
  // computed square for procedural terrain and legacy bundles.
  const bounds = bundledBounds ?? boundsForSquareMeters(preset.latitude, preset.longitude, areaSizeMeters);

  if (bundledHeights) {
    return finalizeAndSave(
      preset.name,
      preset.latitude,
      preset.longitude,
      areaSizeMeters,
      bounds,
      bundledHeights,
      'preset-real',
      bundledVectors ?? undefined
    );
  }

  const sampleHeights = generateProceduralHeights(
    preset.minAltitude,
    preset.maxAltitude,
    sampleGridSizeFor(areaSizeMeters)
  );
  return finalizeAndSave(
    preset.name,
    preset.latitude,
    preset.longitude,
    areaSizeMeters,
    bounds,
    sampleHeights,
    'preset',
    bundledVectors ?? undefined
  );
}
