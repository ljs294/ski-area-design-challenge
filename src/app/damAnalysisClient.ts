import type { DamAnalysisResult } from '../damAnalysis';
import type { EarthworkTerrainPatch } from '../earthwork';
import type { DamAnalysisRequest, DamAnalysisResponse } from './damAnalysisProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const CRASHED = 'The pond analysis worker failed. Try another alignment.';
const POST_FAILED = 'The pond analysis worker could not accept the alignment. Try again.';
const INVALID_RESPONSE = 'The pond analysis worker returned an invalid response. Try again.';

export interface DamAnalysisHandlers {
  onResult(result: DamAnalysisResult, grade: EarthworkTerrainPatch): void;
  onError(message: string): void;
}

function damAnalysisWorker(): WorkerLike<DamAnalysisRequest, DamAnalysisResponse> {
  return new Worker(new URL('./damAnalysis.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * One dam alignment analyzed at a time. Anchoring a second alignment, or
 * cancelling the tool, abandons the first outright: the pond it described is no
 * longer the pond on screen, so its response must never reach the review panel.
 */
export class DamAnalysisAdapter {
  private readonly session: WorkerSession<DamAnalysisRequest, DamAnalysisResponse>;
  private requestId = 0;

  constructor(factory: WorkerFactory<DamAnalysisRequest, DamAnalysisResponse> = damAnalysisWorker) {
    this.session = new WorkerSession(factory);
  }

  /** Analyze one alignment, superseding whatever was already running. */
  run(request: Omit<DamAnalysisRequest, 'id'>, handlers: DamAnalysisHandlers): void {
    this.cancel();
    const id = this.requestId;
    this.session.connect({
      onResponse: (response) => {
        if (!isDamAnalysisResponse(response)) {
          this.session.stop();
          if (id === this.requestId) handlers.onError(INVALID_RESPONSE);
          return;
        }
        if (response.id !== this.requestId) return;
        this.session.stop();
        if (response.ok) handlers.onResult(response.result, response.grade);
        else handlers.onError(response.error);
      },
      onCrash: () => {
        if (id !== this.requestId) return;
        handlers.onError(CRASHED);
      },
    });
    if (!this.session.post({ ...request, id }, [request.heights.buffer]) &&
        id === this.requestId) {
      handlers.onError(POST_FAILED);
    }
  }

  /** Abandon the analysis in flight; nothing it produces can be applied. */
  cancel(): void {
    this.requestId++;
    this.session.stop();
  }

  /**
   * Teardown is the same abandonment as cancelling the tool. The adapter stays
   * usable afterwards so a StrictMode remount does not retire it.
   */
  dispose(): void {
    this.cancel();
  }
}

function isDamAnalysisResponse(value: unknown): value is DamAnalysisResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  return !!response.result && typeof response.result === 'object'
    && !!response.grade && typeof response.grade === 'object';
}
