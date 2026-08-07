import type {
  TrailPaintRequest, TrailPaintRequestPayload, TrailPaintResponse,
} from './trailPaintProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const POST_FAILED = 'Trail painting could not send work to its engine.';
const INVALID_RESPONSE = 'Trail painting returned an invalid response.';

export type TrailPaintSuccess = Extract<TrailPaintResponse, { ok: true }>;
export type TrailPaintPreview = Extract<TrailPaintSuccess, { type: 'preview' }>;
export type TrailPaintAnalysis = Extract<TrailPaintSuccess, { type: 'analysis' }>;

/** What a painting engine needs before it can accept a stroke. */
export interface TrailPaintInit {
  origin: [number, number];
  brushWidthM: number;
}

export interface TrailPaintHandlers {
  /** An engine is initialized and holding an empty canvas. */
  onReady(): void;
  onPreview(response: TrailPaintPreview): void;
  onAnalysis(response: TrailPaintAnalysis): void;
  onFailure(error: string): void;
  /** The engine died and a replacement is initializing, also empty. */
  onRestart(): void;
  /** The engine died a second time. No replacement will be started. */
  onLost(): void;
}

function trailPaintWorker(): WorkerLike<TrailPaintRequest, TrailPaintResponse> {
  return new Worker(new URL('./trailPaint.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * The painting engine, which unlike the other workers holds state: the canvas
 * accumulates every stroke, so a request is only meaningful against the engine
 * that has seen the ones before it.
 *
 * Two consequences shape this adapter. Requests are numbered so an out-of-order
 * preview cannot repaint the canvas backwards, and the numbering restarts its
 * watermark whenever a new engine starts, because a fresh canvas has answered
 * nothing yet. And a crash is recoverable rather than fatal — one replacement
 * engine is started, and the owner is told to replay what it painted. A second
 * crash is not retried, since an engine that dies twice on the same strokes
 * will die on the replay too.
 */
export class TrailPaintAdapter {
  private readonly session: WorkerSession<TrailPaintRequest, TrailPaintResponse>;
  private handlers: TrailPaintHandlers | null = null;
  private init: TrailPaintInit | null = null;
  private requestId = 0;
  private applied = 0;
  private recoveries = 0;
  private readonly pending = new Map<number, TrailPaintRequest['type']>();

  constructor(factory: WorkerFactory<TrailPaintRequest, TrailPaintResponse> = trailPaintWorker) {
    this.session = new WorkerSession(factory);
  }

  /** Discard any engine and initialize a new one on an empty canvas. */
  start(init: TrailPaintInit, handlers: TrailPaintHandlers): void {
    this.session.stop();
    this.init = init;
    this.handlers = handlers;
    this.launch();
  }

  /**
   * Forget earlier crashes, so the next one is allowed its one restart. Opening
   * the painter is a new attempt; changing the brush mid-session is not.
   */
  allowRestart(): void {
    this.recoveries = 0;
  }

  /** Number and send a request. Zero when no engine is running. */
  post(payload: TrailPaintRequestPayload, transfer: Transferable[] = []): number {
    if (!this.session.running) return 0;
    this.requestId += 1;
    const message = { ...payload, id: this.requestId } as TrailPaintRequest;
    if (!this.session.post(message, transfer)) {
      this.handlers?.onFailure(POST_FAILED);
      return 0;
    }
    this.pending.set(message.id, message.type);
    return message.id;
  }

  /** Discard the engine and everything painted on it. */
  stop(): void {
    this.session.stop();
    this.pending.clear();
  }

  /**
   * Teardown is the same discard as cancelling the tool. The adapter stays
   * usable afterwards so a StrictMode remount does not retire it.
   */
  dispose(): void {
    this.stop();
  }

  private launch(): void {
    // A new canvas has answered nothing, so nothing it sends is out of order.
    this.applied = 0;
    this.pending.clear();
    this.session.connect({
      onResponse: (response) => this.receive(response),
      onCrash: () => this.recover(),
    });
    if (this.init) this.post({ type: 'init', ...this.init });
  }

  private receive(response: TrailPaintResponse): void {
    const id = responseId(response);
    const requestType = id === null ? undefined : this.pending.get(id);
    if (id === null || !requestType) return;
    if (!isTrailPaintResponse(response) || !responseMatchesRequest(response, requestType)) {
      this.stop();
      this.handlers?.onFailure(INVALID_RESPONSE);
      return;
    }
    this.pending.delete(response.id);
    if (response.id < this.applied) return;
    this.applied = response.id;
    for (const pendingId of this.pending.keys()) {
      if (pendingId < this.applied) this.pending.delete(pendingId);
    }
    const handlers = this.handlers;
    if (!handlers) return;
    if (!response.ok) {
      handlers.onFailure(response.error);
      return;
    }
    if (response.type === 'ready') handlers.onReady();
    else if (response.type === 'preview') handlers.onPreview(response);
    else handlers.onAnalysis(response);
  }

  private recover(): void {
    this.pending.clear();
    const handlers = this.handlers;
    if (this.recoveries > 0) {
      handlers?.onLost();
      return;
    }
    this.recoveries += 1;
    handlers?.onRestart();
    this.launch();
  }
}

function responseId(value: unknown): number | null {
  if (!value || typeof value !== 'object') return null;
  const id = (value as { id?: unknown }).id;
  return Number.isSafeInteger(id) ? id as number : null;
}

function isTrailPaintResponse(value: unknown): value is TrailPaintResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  if (response.type === 'ready') return true;
  if (response.type === 'preview') {
    return Array.isArray(response.polygons) && Number.isFinite(response.areaM2) &&
      typeof response.canUndo === 'boolean';
  }
  return response.type === 'analysis' && Array.isArray(response.parts) &&
    Number.isFinite(response.areaM2);
}

function responseMatchesRequest(
  response: TrailPaintResponse,
  requestType: TrailPaintRequest['type'],
): boolean {
  if (!response.ok) return true;
  if (requestType === 'init') return response.type === 'ready';
  if (requestType === 'finish') return response.type === 'analysis';
  return response.type === 'preview';
}
