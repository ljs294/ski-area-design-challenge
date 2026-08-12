/// <reference lib="webworker" />

import { analyzeSnowmakingSystems } from '../snowmakingHydraulics';
import type { SnowmakingAnalysisRequest, SnowmakingAnalysisResponse } from './snowmakingAnalysisProtocol';

const worker = self as DedicatedWorkerGlobalScope;

worker.onmessage = (event: MessageEvent<SnowmakingAnalysisRequest>) => {
  const request = event.data;
  try {
    const response: SnowmakingAnalysisResponse = {
      id: request.id,
      ok: true,
      result: analyzeSnowmakingSystems(request.input),
    };
    worker.postMessage(response);
  } catch (error) {
    const response: SnowmakingAnalysisResponse = {
      id: request.id,
      ok: false,
      error: error instanceof Error ? error.message : 'The snowmaking hydraulic worker failed.',
    };
    worker.postMessage(response);
  }
};

export {};
