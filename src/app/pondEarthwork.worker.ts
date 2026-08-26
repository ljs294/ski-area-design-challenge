/// <reference lib="webworker" />
import { designPondEarthwork, pondTerrainPatch } from '../pondEarthwork';
import type { TerrainRecord } from '../types/terrain';
import type { PondEarthworkRequest, PondEarthworkResponse } from './pondEarthworkProtocol';

const scope = self as DedicatedWorkerGlobalScope;
scope.onmessage = (event: MessageEvent<PondEarthworkRequest>) => {
  const request = event.data;
  const base = { jobId: request.jobId, terrainRevision: request.terrainRevision,
    profileRevision: request.profileRevision };
  try {
    const terrain = {
      bounds: request.bounds,
      sampleGridSize: request.gridSize,
      sampleHeights: request.heights,
      contourMetadata: { gridSize: request.contourGridSize, intervalM: request.contourIntervalM },
      packageManifest: { elevationChecksum: request.baseElevationChecksum },
    } as unknown as TerrainRecord;
    const design = designPondEarthwork(terrain, request.boundary, {
      topElevationM: request.topElevationM,
      excavationDepthM: request.excavationDepthM,
      poolAreaM2: request.poolAreaM2,
    });
    if (!design) throw new Error('The pond could not be graded into this terrain.');
    const grade = pondTerrainPatch(terrain, design);
    const response: PondEarthworkResponse = { type: 'result', ...base, design, grade };
    scope.postMessage(response, [grade.patchIndices.buffer, grade.patchHeights.buffer]);
  } catch (error) {
    const response: PondEarthworkResponse = { type: 'error', ...base,
      error: error instanceof Error ? error.message : 'Unable to grade the pond.' };
    scope.postMessage(response);
  }
};
