/// <reference lib="webworker" />
import { gradeTerrainForTrail } from './terrainGradeEngine';
import type { TerrainGradeRequest, TerrainGradeResponse } from './terrainGradeProtocol';

const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;
scope.onmessage = (event: MessageEvent<TerrainGradeRequest>) => {
  const request = event.data;
  try {
    const result = gradeTerrainForTrail(request);
    const response: TerrainGradeResponse = {
      id: request.id,
      ok: true,
      ...result,
      trailGeometryKey: request.trailGeometryKey,
    };
    scope.postMessage(response, [
      result.patchIndices.buffer, result.patchHeights.buffer,
      result.contourSegments.buffer, result.editedContourSegments.buffer,
    ]);
  } catch (error) {
    const response: TerrainGradeResponse = {
      id: request.id, ok: false,
      error: error instanceof Error ? error.message : 'Unable to grade terrain.',
    };
    scope.postMessage(response);
  }
};
