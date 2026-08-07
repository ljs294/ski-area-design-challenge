import { saveTerrainMapContext } from './terrainStorageClient';
import type { TerrainMapContextSaveRequest } from './ipcContract';
import type { TerrainRecord } from './types/terrain';
import type { VectorFeatureSet } from './types/vectorFeatures';
import { fetchVectorFeatures } from './vectorFeatures';

export interface TerrainMapContextPorts {
  fetch(bounds: NonNullable<TerrainRecord['bounds']>, signal?: AbortSignal): Promise<VectorFeatureSet>;
  save(request: TerrainMapContextSaveRequest): Promise<{ ok: boolean; error?: string }>;
  now(): string;
}

export type TerrainMapContextRepairResult =
  | { ok: true; vectorFeatures: VectorFeatureSet; updatedAt: string }
  | { ok: false; error: string };

const DEFAULT_PORTS: TerrainMapContextPorts = {
  fetch: fetchVectorFeatures,
  save: saveTerrainMapContext,
  now: () => new Date().toISOString(),
};

/** Add only missing OSM context to an otherwise committed terrain package.
 * Persistence happens before publication so the live document never points at
 * context that failed to reach disk. */
export async function repairTerrainMapContext(
  record: TerrainRecord,
  ports: TerrainMapContextPorts = DEFAULT_PORTS,
  signal?: AbortSignal,
): Promise<TerrainMapContextRepairResult> {
  if (!record.bounds) return { ok: false, error: 'Terrain bounds are unavailable.' };
  try {
    const vectorFeatures = await ports.fetch(record.bounds, signal);
    signal?.throwIfAborted();
    const updatedAt = ports.now();
    const saved = await ports.save({ key: record.key, vectorFeatures, updatedAt });
    if (!saved.ok) return { ok: false, error: saved.error ?? 'Unable to save map context.' };
    return { ok: true, vectorFeatures, updatedAt };
  } catch (error) {
    return { ok: false,
      error: error instanceof Error ? error.message : 'Unable to download map context.' };
  }
}
