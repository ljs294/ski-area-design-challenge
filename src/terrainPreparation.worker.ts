/// <reference lib="webworker" />
import { deriveCoverDisplayGeometry } from './coverDisplay';
import { deriveFourClassCover } from './fourClassCover';
import type { SiteCoverGrid } from './types/cover';
import type { LatLonBounds } from './types/geo';
import type { VectorFeatureSet } from './types/vectorFeatures';
import type { NaipAcquisition } from './usgsTerrainCover';

export interface TerrainPreparationWorkerRequest {
  type: 'prepare-cover';
  jobId: number;
  terrainRevision: number;
  profileRevision: number;
  bounds: LatLonBounds;
  original: SiteCoverGrid;
  heights: Float32Array;
  elevationWidth: number;
  elevationHeight: number;
  naip: NaipAcquisition | null;
  vectors?: VectorFeatureSet;
}

export type TerrainPreparationWorkerResponse =
  | { type: 'progress'; jobId: number; terrainRevision: number; profileRevision: number;
      phase: 'classifying' | 'vectorizing' }
  | { type: 'result'; jobId: number; terrainRevision: number; profileRevision: number;
      cover: ReturnType<typeof deriveFourClassCover>; geometry: Float32Array;
      stats: ReturnType<typeof deriveCoverDisplayGeometry>['stats'] }
  | { type: 'error'; jobId: number; terrainRevision: number; profileRevision: number; error: string };

const scope = self as DedicatedWorkerGlobalScope;
scope.onmessage = (event: MessageEvent<TerrainPreparationWorkerRequest>) => {
  const request = event.data;
  if (request.type !== 'prepare-cover') return;
  const base = {
    jobId: request.jobId,
    terrainRevision: request.terrainRevision,
    profileRevision: request.profileRevision,
  };
  try {
    const classifying: TerrainPreparationWorkerResponse = {
      type: 'progress', ...base, phase: 'classifying',
    };
    scope.postMessage(classifying);
    const cover = deriveFourClassCover({
      bounds: request.bounds,
      original: request.original,
      elevation: { heights: request.heights, width: request.elevationWidth, height: request.elevationHeight },
      naip: request.naip,
      vectors: request.vectors,
      targetCellM: 2,
    });
    const vectorizing: TerrainPreparationWorkerResponse = {
      type: 'progress', ...base, phase: 'vectorizing',
    };
    scope.postMessage(vectorizing);
    const display = deriveCoverDisplayGeometry(cover);
    const geometry = Float32Array.from(display.geometry);
    const coverData = cover.data instanceof Uint8Array ? cover.data : Uint8Array.from(cover.data);
    cover.data = coverData;
    const result: TerrainPreparationWorkerResponse = {
      type: 'result', ...base, cover, geometry, stats: display.stats,
    };
    scope.postMessage(result, [coverData.buffer, geometry.buffer]);
  } catch (error) {
    const failure: TerrainPreparationWorkerResponse = { type: 'error', ...base,
      error: error instanceof Error ? error.message : 'Terrain preparation failed' };
    scope.postMessage(failure);
  }
};
