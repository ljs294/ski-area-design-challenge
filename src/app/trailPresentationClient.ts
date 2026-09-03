import { TRAIL_PRESENTATION_VERSION, type TrailPresentationInput,
  type TrailPresentationResult } from '../types/trailPresentation';
import type { TrailPresentationRequest, TrailPresentationResponse } from './trailPresentationProtocol';
import { WorkerSession, type WorkerFactory, type WorkerLike } from './workerAdapter';

const FAILED = 'The ski-run presentation could not be prepared.';
const INVALID = 'The ski-run presentation returned invalid geometry.';

export interface TrailPresentationHandlers {
  onResult(result: TrailPresentationResult): void;
  onError(message: string): void;
}

function presentationWorker(): WorkerLike<TrailPresentationRequest, TrailPresentationResponse> {
  return new Worker(new URL('./trailPresentation.worker.ts', import.meta.url), { type: 'module' });
}

export class TrailPresentationAdapter {
  private readonly session: WorkerSession<TrailPresentationRequest, TrailPresentationResponse>;
  private nextId = 0;
  private activeId = 0;

  constructor(factory: WorkerFactory<TrailPresentationRequest, TrailPresentationResponse> = presentationWorker) {
    this.session = new WorkerSession(factory);
  }

  compile(input: TrailPresentationInput, handlers: TrailPresentationHandlers): number {
    const id = ++this.nextId;
    this.activeId = id;
    this.session.stop();
    this.dispatch({ id, type: 'compile', input }, handlers, false);
    return id;
  }

  private dispatch(
    request: TrailPresentationRequest,
    handlers: TrailPresentationHandlers,
    retried: boolean,
  ): void {
    this.session.connect({
      onResponse: (response) => {
        if (request.id !== this.activeId || response.id !== request.id) return;
        if (!isResponse(response)) {
          this.session.stop();
          handlers.onError(INVALID);
          return;
        }
        if (!response.ok) {
          handlers.onError(response.error);
          return;
        }
        handlers.onResult(response.result);
      },
      onCrash: () => {
        if (request.id !== this.activeId) return;
        if (!retried) this.dispatch(request, handlers, true);
        else handlers.onError(FAILED);
      },
    });
    if (!this.session.post(request)) {
      if (request.id !== this.activeId) return;
      if (!retried) this.dispatch(request, handlers, true);
      else handlers.onError(FAILED);
    }
  }

  cancel(): void {
    this.activeId = ++this.nextId;
    this.session.stop();
  }

  dispose(): void {
    this.cancel();
  }
}

function isResponse(value: unknown): value is TrailPresentationResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  const result = response.result as Record<string, unknown> | undefined;
  return result?.version === TRAIL_PRESENTATION_VERSION && Array.isArray(result.surface) &&
    Array.isArray(result.routes) && Array.isArray(result.labels) && Array.isArray(result.junctions);
}
