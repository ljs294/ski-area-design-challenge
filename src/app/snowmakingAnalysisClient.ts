import type { SnowmakingAnalysisInput, SnowmakingAnalysisResult } from '../snowmakingHydraulics';
import type { SnowmakingAnalysisRequest, SnowmakingAnalysisResponse } from './snowmakingAnalysisProtocol';
import { WorkerSession, type WorkerFactory, type WorkerLike } from './workerAdapter';

const CRASHED = 'The snowmaking hydraulic worker failed.';
const POST_FAILED = 'The snowmaking hydraulic worker could not accept the analysis.';
const INVALID_RESPONSE = 'The snowmaking hydraulic worker returned an invalid response.';

function snowmakingAnalysisWorker(): WorkerLike<SnowmakingAnalysisRequest, SnowmakingAnalysisResponse> {
  return new Worker(new URL('./snowmakingAnalysis.worker.ts', import.meta.url), { type: 'module' });
}

export interface SnowmakingAnalysisHandlers {
  onResult(result: SnowmakingAnalysisResult): void;
  onError(message: string): void;
}

export class SnowmakingAnalysisAdapter {
  private readonly session: WorkerSession<SnowmakingAnalysisRequest, SnowmakingAnalysisResponse>;
  private requestId = 0;

  constructor(factory: WorkerFactory<SnowmakingAnalysisRequest, SnowmakingAnalysisResponse> =
    snowmakingAnalysisWorker) {
    this.session = new WorkerSession(factory);
  }

  run(input: SnowmakingAnalysisInput, handlers: SnowmakingAnalysisHandlers): void {
    this.cancel();
    const id = this.requestId;
    this.session.connect({
      onResponse: (response) => {
        if (!isSnowmakingAnalysisResponse(response)) {
          this.session.stop();
          if (id === this.requestId) handlers.onError(INVALID_RESPONSE);
          return;
        }
        if (response.id !== this.requestId) return;
        this.session.stop();
        if (response.ok) handlers.onResult(response.result);
        else handlers.onError(response.error);
      },
      onCrash: () => { if (id === this.requestId) handlers.onError(CRASHED); },
    });
    if (!this.session.post({ id, input }) && id === this.requestId) handlers.onError(POST_FAILED);
  }

  cancel(): void {
    this.requestId++;
    this.session.stop();
  }

  dispose(): void { this.cancel(); }
}

function isSnowmakingAnalysisResponse(value: unknown): value is SnowmakingAnalysisResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  return response.ok ? !!response.result && typeof response.result === 'object'
    : typeof response.error === 'string';
}
