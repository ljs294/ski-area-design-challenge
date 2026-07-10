// Orchestrates turning either a live map selection or a curated preset into
// a fully-built TerrainDB: fetch/generate the raw sample grid, upscale it
// for display, attach a climate profile, persist it, and return it.
import type { AreaSizeMeters, TerrainDB, TerrainRecord, VectorFeatureSet } from './types';
import { fetchElevationGrid, sampleGridSizeFor, type LatLonBounds, type ElevationProgress } from './elevation';
import { bicubicUpscale } from './bicubicUpscale';
import { generateProceduralClimate } from './climate';
import { generateProceduralHeights, NA_MOUNTAIN_PRESETS } from './mountainPresets';
import { saveTerrain } from './terrainStorageClient';
import { boundsForSquareMeters } from './geo';
import { fetchVectorFeatures, hydrateVectorFeatures } from './vectorFeatures';
import { MAP_SIZE } from './renderer';

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
  vectorFeatures?: VectorFeatureSet
): Promise<TerrainDB> {
  const sampleGridSize = Math.round(Math.sqrt(sampleHeights.length));

  const sum = sampleHeights.reduce((a, b) => a + b, 0);
  const avgAlt = sum / sampleHeights.length;
  const climate = generateProceduralClimate(latitude, avgAlt);

  const now = new Date().toISOString();
  const record: TerrainRecord = {
    schemaVersion: 3,
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
    sourceType,
    createdAt: now,
    updatedAt: now,
  };

  const saveResult = await saveTerrain(record);
  if (!saveResult.ok) {
    console.error('Failed to persist terrain:', saveResult.error);
  }

  return hydrateTerrainRecord(record);
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
  center: { latitude: number; longitude: number },
  areaSizeMeters: AreaSizeMeters,
  mountainName: string,
  onProgress?: (progress: ElevationProgress) => void
): Promise<TerrainDB> {
  const [sampleHeights, vectorFeatures] = await Promise.all([
    fetchElevationGrid(bounds, areaSizeMeters, onProgress),
    fetchVectorFeatures(bounds).catch((e) => {
      console.error('Failed to fetch map features (roads/water/peaks/land cover):', e);
      return undefined;
    }),
  ]);
  return finalizeAndSave(mountainName, center.latitude, center.longitude, areaSizeMeters, bounds, sampleHeights, 'live', vectorFeatures);
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
  const bounds = boundsForSquareMeters(preset.latitude, preset.longitude, areaSizeMeters);
  const [bundledHeights, bundledVectors] = await Promise.all([
    loadBundledPresetHeights(presetId),
    loadBundledPresetVectors(presetId),
  ]);

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
