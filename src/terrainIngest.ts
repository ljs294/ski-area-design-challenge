// Orchestrates turning either a live map selection or a curated preset into
// a fully-built TerrainDB: fetch/generate the raw sample grid, upscale it
// for display, attach a climate profile, persist it, and return it.
import type { AreaSizeMeters, TerrainDB, TerrainRecord } from './types';
import { fetchElevationGrid, SAMPLE_GRID_SIZE, type LatLonBounds, type ElevationProgress } from './elevation';
import { bicubicUpscale } from './bicubicUpscale';
import { generateProceduralClimate } from './climate';
import { generateProceduralHeights, NA_MOUNTAIN_PRESETS } from './mountainPresets';
import { saveTerrain } from './terrainStorageClient';

export const DISPLAY_GRID_SIZE = 512;

// Real (downloaded, not procedural) elevation data bundled at build time for
// specific presets. Produced offline via `npm run download-preset -- <id>`
// (scripts/downloadPresetTerrain.ts) and committed under src/presetTerrain/.
// Presets without a matching file here fall back to procedural placeholder
// terrain in ingestPreset().
interface BundledPresetTerrain {
  sampleGridSize: number;
  sampleHeights: number[];
}

const bundledPresetModules = import.meta.glob<BundledPresetTerrain>('./presetTerrain/*.json', {
  eager: true,
  import: 'default',
});

const bundledPresetTerrain: Record<string, BundledPresetTerrain> = {};
for (const [path, data] of Object.entries(bundledPresetModules)) {
  const id = path.match(/([^/]+)\.json$/)?.[1];
  if (id) bundledPresetTerrain[id] = data;
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

  return {
    ...record,
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
  sampleHeights: number[],
  sourceType: TerrainRecord['sourceType']
): Promise<TerrainDB> {
  const sampleGridSize = Math.round(Math.sqrt(sampleHeights.length));

  const sum = sampleHeights.reduce((a, b) => a + b, 0);
  const avgAlt = sum / sampleHeights.length;
  const climate = generateProceduralClimate(latitude, avgAlt);

  const now = new Date().toISOString();
  const record: TerrainRecord = {
    schemaVersion: 2,
    key: makeKey(mountainName, latitude, longitude),
    mountainName,
    latitude,
    longitude,
    areaSizeMeters,
    sampleGridSize,
    sampleHeights,
    climate,
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
 */
export async function ingestLiveArea(
  bounds: LatLonBounds,
  center: { latitude: number; longitude: number },
  areaSizeMeters: AreaSizeMeters,
  mountainName: string,
  onProgress?: (progress: ElevationProgress) => void
): Promise<TerrainDB> {
  const sampleHeights = await fetchElevationGrid(bounds, onProgress);
  return finalizeAndSave(mountainName, center.latitude, center.longitude, areaSizeMeters, sampleHeights, 'live');
}

/**
 * Build a TerrainDB for a curated preset mountain. Uses real bundled
 * elevation data when available (src/presetTerrain/<id>.json), otherwise
 * falls back to procedurally generated placeholder elevation — both go
 * through the same finalize/upscale pipeline as live downloads.
 */
export async function ingestPreset(presetId: string): Promise<TerrainDB> {
  const preset = NA_MOUNTAIN_PRESETS.find((p) => p.id === presetId);
  if (!preset) throw new Error(`Unknown preset: ${presetId}`);

  const areaSizeMeters = preset.areaSizeMeters ?? 4000;
  const bundled = bundledPresetTerrain[presetId];

  if (bundled) {
    return finalizeAndSave(
      preset.name,
      preset.latitude,
      preset.longitude,
      areaSizeMeters,
      bundled.sampleHeights,
      'preset-real'
    );
  }

  const sampleHeights = generateProceduralHeights(preset.minAltitude, preset.maxAltitude, SAMPLE_GRID_SIZE);
  return finalizeAndSave(preset.name, preset.latitude, preset.longitude, areaSizeMeters, sampleHeights, 'preset');
}
