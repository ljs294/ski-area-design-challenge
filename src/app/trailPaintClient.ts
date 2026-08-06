import type {
  TrailPaintRequest, TrailPaintRequestPayload, TrailPaintResponse,
} from './trailPaintProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const POST_FAILED = 'Trail painting could not send work to its engine.';

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
    return message.id;
  }

  /** Discard the engine and everything painted on it. */
  stop(): void {
    this.session.stop();
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
    this.session.connect({
      onResponse: (response) => this.receive(response),
      onCrash: () => this.recover(),
    });
    if (this.init) this.post({ type: 'init', ...this.init });
  }

  private receive(response: TrailPaintResponse): void {
    if (response.id < this.applied) return;
    this.applied = response.id;
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
