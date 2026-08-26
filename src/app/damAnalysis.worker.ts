/// <reference lib="webworker" />
import { analyzeDam } from '../damAnalysis';
import type { TerrainRecord } from '../types';
import type { DamAnalysisRequest, DamAnalysisResponse } from './damAnalysisProtocol';
import { earthworkTerrainPatch } from '../earthwork';

self.onmessage = (event: MessageEvent<DamAnalysisRequest>) => {
  const request = event.data;
  const terrain = { bounds: request.bounds, sampleGridSize: request.gridSize,
    sampleHeights: request.heights,
    contourMetadata: { gridSize: request.contourGridSize, intervalM: request.contourIntervalM },
    packageManifest: { elevationChecksum: request.baseElevationChecksum },
  } as unknown as TerrainRecord;
  const outcome = analyzeDam(terrain, request.points, request.crestElevationM, request.streams);
  const response: DamAnalysisResponse = outcome.ok
    ? { id: request.id, ok: true, result: outcome.result,
      grade: earthworkTerrainPatch(terrain, outcome.result.patchIndices, outcome.result.patchHeights) }
    : { id: request.id, ok: false, error: outcome.error };
  self.postMessage(response, response.ok
    ? [response.grade.patchIndices.buffer, response.grade.patchHeights.buffer]
    : []);
};

export {};
