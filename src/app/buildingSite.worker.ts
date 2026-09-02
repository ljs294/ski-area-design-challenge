/// <reference lib="webworker" />

import { analyzeBuildingSite } from '../buildingSiteAnalysis';
import type { BuildingSiteRequest, BuildingSiteResponse } from './buildingSiteProtocol';

const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;

scope.onmessage = (event: MessageEvent<BuildingSiteRequest>) => {
  const request = event.data;
  if (request.type && request.type !== 'analyze-building-site') return;
  const checksum = request.elevationChecksum ?? request.baseElevationChecksum ?? '';
  const outcome = analyzeBuildingSite({
    center: request.center,
    bearingDeg: request.bearingDeg,
    dimensions: request.dimensions,
    foundationMode: request.foundationMode,
    foundation: request.foundation,
    heights: request.heights,
    gridSize: request.gridSize,
    bounds: request.bounds,
    baseElevationChecksum: checksum,
    terrainRevision: request.terrainRevision,
    buildingGeometryKey: request.geometryKey,
    contourGridSize: request.contourGridSize,
    contourIntervalM: request.contourIntervalM,
  });
  const response: BuildingSiteResponse = outcome.ok
    ? { id: request.id, ok: true, geometryKey: request.geometryKey,
      terrainRevision: request.terrainRevision, elevationChecksum: checksum,
      result: outcome.result }
    : { id: request.id, ok: false, error: outcome.error,
      geometryKey: request.geometryKey, terrainRevision: request.terrainRevision,
      elevationChecksum: checksum };
  if (!response.ok) {
    scope.postMessage(response);
    return;
  }
  const transfers: Transferable[] = [response.result.patchIndices.buffer,
    response.result.patchHeights.buffer];
  // Contours are intentionally number arrays in the pure earthwork contract;
  // transfer only the large typed arrays, keeping fake-worker tests simple.
  scope.postMessage(response, transfers);
};

export {};

