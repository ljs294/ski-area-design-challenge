/// <reference lib="webworker" />
import { analyzeDam } from '../damAnalysis';
import type { TerrainRecord } from '../types';
import type { DamAnalysisRequest, DamAnalysisResponse } from './damAnalysisProtocol';

self.onmessage = (event: MessageEvent<DamAnalysisRequest>) => {
  const request = event.data;
  const terrain = { bounds: request.bounds, sampleGridSize: request.gridSize,
    sampleHeights: request.heights } as unknown as TerrainRecord;
  const outcome = analyzeDam(terrain, request.points, request.crestElevationM, request.streams);
  const response: DamAnalysisResponse = outcome.ok
    ? { id: request.id, ok: true, result: outcome.result }
    : { id: request.id, ok: false, error: outcome.error };
  self.postMessage(response);
};

export {};
