import type { CoverGrid } from '../../../src/types/cover';
import type { TerrainRecord, TerrainSummary } from '../../../src/types/terrain';
import { listTerrains, loadTerrain } from '../../../src/terrainStorageClient';

async function fetchOptional(url: string): Promise<Response | null> {
  const response = await fetch(url);
  return response.ok ? response : null;
}

export async function listPrototypeTerrains(): Promise<TerrainSummary[]> {
  const local = await fetchOptional('/__p0_terrain/index.json');
  if (local) return await local.json() as TerrainSummary[];
  return listTerrains();
}

export async function loadPrototypeTerrain(key: string): Promise<TerrainRecord | null> {
  const safeKey = encodeURIComponent(key);
  const metadataResponse = await fetchOptional(`/__p0_terrain/${safeKey}.json`);
  if (!metadataResponse) return loadTerrain(key);
  const metadata = await metadataResponse.json() as Omit<TerrainRecord, 'sampleHeights' | 'coverGrid'>;
  const heightsResponse = await fetchOptional(`/__p0_terrain/${safeKey}.heights.bin`);
  if (!heightsResponse) throw new Error('The terrain elevation binary is missing.');
  const sampleHeights = new Float32Array(await heightsResponse.arrayBuffer());
  let coverGrid: CoverGrid | undefined;
  if (metadata.coverMetadata) {
    const coverResponse = await fetchOptional(`/__p0_terrain/${safeKey}.cover.bin`);
    if (coverResponse) coverGrid = { ...metadata.coverMetadata,
      data: new Uint8Array(await coverResponse.arrayBuffer()) } as CoverGrid;
  }
  return { ...metadata, sampleHeights, ...(coverGrid ? { coverGrid } : {}) } as TerrainRecord;
}

