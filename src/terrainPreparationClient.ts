import { deriveCoverDisplayGeometry, type DerivedCoverDisplay } from './coverDisplay';
import { deriveFourClassCover } from './fourClassCover';
import type { SiteCoverGrid, TerrainCoverGrid } from './types/cover';
import type { LatLonBounds } from './types/geo';
import type { VectorFeatureSet } from './types/vectorFeatures';
import type { NaipAcquisition } from './usgsTerrainCover';
import type {
  TerrainPreparationWorkerRequest,
  TerrainPreparationWorkerResponse,
} from './terrainPreparation.worker';

export interface TerrainPreparationInput {
  bounds: LatLonBounds;
  original: SiteCoverGrid;
  heights: ArrayLike<number>;
  elevationWidth: number;
  elevationHeight: number;
  naip: NaipAcquisition | null;
  vectors?: VectorFeatureSet;
}

export interface TerrainPreparationResult {
  cover: TerrainCoverGrid;
  display: DerivedCoverDisplay;
}

let nextJobId = 1;

export async function prepareTerrainCover(
  input: TerrainPreparationInput,
  options: { signal?: AbortSignal; terrainRevision?: number; profileRevision?: number;
    onProgress?(phase: 'classifying' | 'vectorizing'): void } = {},
): Promise<TerrainPreparationResult> {
  if (typeof Worker === 'undefined') {
    options.onProgress?.('classifying');
    const cover = deriveFourClassCover({
      bounds: input.bounds,
      original: input.original,
      elevation: { heights: input.heights, width: input.elevationWidth, height: input.elevationHeight },
      naip: input.naip,
      vectors: input.vectors,
      targetCellM: 2,
    });
    options.onProgress?.('vectorizing');
    return { cover, display: deriveCoverDisplayGeometry(cover) };
  }

  const worker = new Worker(new URL('./terrainPreparation.worker.ts', import.meta.url), { type: 'module' });
  const jobId = nextJobId++;
  const heights = Float32Array.from(input.heights);
  const originalData = input.original.data instanceof Uint8Array
    ? input.original.data.slice()
    : Uint8Array.from(input.original.data);
  const cloneBand = (band: Uint8Array) => band.slice();
  const naip = input.naip ? {
    ...input.naip,
    red: cloneBand(input.naip.red), green: cloneBand(input.naip.green),
    blue: cloneBand(input.naip.blue), nir: cloneBand(input.naip.nir),
  } : null;
  const request: TerrainPreparationWorkerRequest = {
    type: 'prepare-cover', jobId,
    terrainRevision: options.terrainRevision ?? 0,
    profileRevision: options.profileRevision ?? 0,
    bounds: input.bounds,
    original: { ...input.original, data: originalData },
    heights,
    elevationWidth: input.elevationWidth,
    elevationHeight: input.elevationHeight,
    naip,
    vectors: input.vectors,
  };
  const transfers: Transferable[] = [heights.buffer, originalData.buffer];
  if (naip) transfers.push(naip.red.buffer, naip.green.buffer, naip.blue.buffer, naip.nir.buffer);

  return new Promise<TerrainPreparationResult>((resolve, reject) => {
    const abort = () => {
      worker.terminate();
      reject(new DOMException('Resort preparation cancelled', 'AbortError'));
    };
    if (options.signal?.aborted) return abort();
    options.signal?.addEventListener('abort', abort, { once: true });
    worker.onerror = (event) => {
      options.signal?.removeEventListener('abort', abort);
      worker.terminate();
      reject(new Error(event.message || 'Terrain preparation worker failed'));
    };
    worker.onmessage = (event: MessageEvent<TerrainPreparationWorkerResponse>) => {
      const response = event.data;
      if (response.jobId !== jobId
        || response.terrainRevision !== request.terrainRevision
        || response.profileRevision !== request.profileRevision) return;
      if (response.type === 'progress') {
        options.onProgress?.(response.phase);
        return;
      }
      options.signal?.removeEventListener('abort', abort);
      worker.terminate();
      if (response.type === 'error') reject(new Error(response.error));
      else resolve({ cover: response.cover, display: { geometry: response.geometry, stats: response.stats } });
    };
    worker.postMessage(request, transfers);
  });
}
