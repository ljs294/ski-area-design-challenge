/// <reference lib="webworker" />
import { stepNaturalSnow } from '../snowSimulation';
import type { SnowStepRequest, SnowStepResponse } from './snowStepProtocol';

const scope: DedicatedWorkerGlobalScope = self as unknown as DedicatedWorkerGlobalScope;
scope.onmessage = (event: MessageEvent<SnowStepRequest>) => {
  const request = event.data;
  try {
    const result = stepNaturalSnow(request.grid, request.terrain, request.hours);
    const response: SnowStepResponse = { id: request.id, terrainBinding: request.terrainBinding,
      ok: true, ...result };
    scope.postMessage(response, [result.grid.depthM.buffer, result.grid.surface.buffer]);
  } catch (error) {
    const response: SnowStepResponse = { id: request.id, terrainBinding: request.terrainBinding, ok: false,
      error: error instanceof Error ? error.message : 'Snow simulation failed.' };
    scope.postMessage(response);
  }
};
