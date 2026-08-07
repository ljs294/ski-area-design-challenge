/**
 * One worker at a time, owned by one adapter.
 *
 * Every worker in the app was constructed, re-bound, and terminated inline in
 * `MapView`, which is how the same three questions ended up answered
 * differently in each place: whether a response still belongs to the request
 * the player is waiting on, whether a superseded computation keeps burning a
 * core, and whether leaving the resort actually stops the work.
 *
 * A session answers the first two structurally. Handlers are bound to the
 * worker instance that was running when they were bound, so a retired worker
 * can never deliver into the live tool, and terminating is the ordinary way to
 * supersede work rather than an afterthought. Request identity and validation
 * belong to each protocol, so they stay in the adapters above this.
 */

export interface WorkerLike<Request, Response> {
  onmessage: ((event: MessageEvent<Response>) => void) | null;
  onerror: ((event: ErrorEvent) => void) | null;
  postMessage(message: Request, transfer?: Transferable[]): void;
  terminate(): void;
}

export type WorkerFactory<Request, Response> = () => WorkerLike<Request, Response>;

export interface WorkerHandlers<Response> {
  onResponse(response: Response): void;
  /** The worker raised an uncaught error. It is already terminated. */
  onCrash(): void;
}

export class WorkerSession<Request, Response> {
  private readonly factory: WorkerFactory<Request, Response>;
  private worker: WorkerLike<Request, Response> | null = null;

  constructor(factory: WorkerFactory<Request, Response>) {
    this.factory = factory;
  }

  /** True while a worker exists. It may be idle or computing. */
  get running(): boolean {
    return this.worker !== null;
  }

  /** Bind `handlers` to a running worker, starting one if none is running. */
  connect(handlers: WorkerHandlers<Response>): void {
    const worker = this.worker ?? this.factory();
    this.worker = worker;
    worker.onmessage = (event) => {
      if (this.worker === worker) handlers.onResponse(event.data);
    };
    // A crashed worker is never reused: an engine that threw once has already
    // lost whatever state the next request would have been answered against.
    worker.onerror = () => {
      if (this.worker !== worker) return;
      this.stop();
      handlers.onCrash();
    };
  }

  /**
   * Send a request to the running worker. False when none is running or the
   * browser refuses the message (for example, because a transfer is invalid).
   * A worker whose post failed is retired: callers may safely start a fresh
   * session, but must settle the request that was not accepted.
   */
  post(request: Request, transfer: Transferable[] = []): boolean {
    if (!this.worker) return false;
    try {
      this.worker.postMessage(request, transfer);
      return true;
    } catch {
      this.stop();
      return false;
    }
  }

  /**
   * Terminate the running worker, abandoning whatever it was computing. The
   * session stays usable, so a cancelled tool — or a StrictMode remount — can
   * start another worker without being retired.
   */
  stop(): void {
    const worker = this.worker;
    if (!worker) return;
    this.worker = null;
    worker.onmessage = null;
    worker.onerror = null;
    worker.terminate();
  }
}
