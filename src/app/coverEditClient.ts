import type { CoverEditPayload, CoverEditRequest, CoverEditResponse } from './coverEditProtocol';
import { WorkerSession } from './workerAdapter';
import type { WorkerFactory, WorkerLike } from './workerAdapter';

const DEFAULT_TIMEOUT_MS = 60_000;
const CRASHED = 'Ground-cover worker stopped unexpectedly.';
const ABANDONED = 'The ground-cover edit was abandoned.';
const POST_FAILED = 'Ground-cover processing could not start.';
const INVALID_RESPONSE = 'Ground-cover processing returned an invalid response.';

export type CoverEditSuccess = Extract<CoverEditResponse, { ok: true }>;

export interface CoverEditClientOptions {
  timeoutMs?: number;
  workerFactory?: WorkerFactory<CoverEditRequest, CoverEditResponse>;
}

function coverEditWorker(): WorkerLike<CoverEditRequest, CoverEditResponse> {
  return new Worker(new URL('./coverEdit.worker.ts', import.meta.url), { type: 'module' });
}

/**
 * One ground-cover edit at a time. The terrain document already serializes
 * them, so the adapter holds a single session and treats an overlapping edit as
 * a caller mistake to be reported rather than a second worker to run.
 *
 * Every ending — success, worker error, timeout, teardown — settles the promise
 * once and terminates the worker. Cover editing is best effort, so the caller
 * turning that rejection into a retained failure is the whole contract; what
 * this owes it is that the worker is never left grinding on a stamp nobody is
 * waiting for.
 */
export class CoverEditAdapter {
  private readonly session: WorkerSession<CoverEditRequest, CoverEditResponse>;
  private readonly timeoutMs: number;
  private abandon: ((reason: Error) => void) | null = null;
  private requestId = 0;

  constructor(options: CoverEditClientOptions = {}) {
    this.session = new WorkerSession(options.workerFactory ?? coverEditWorker);
    this.timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  }

  run(payload: CoverEditPayload): Promise<CoverEditSuccess> {
    this.abandon?.(new Error(ABANDONED));
    const request: CoverEditRequest = { ...payload, id: ++this.requestId };
    return new Promise<CoverEditSuccess>((resolve, reject) => {
      let settled = false;
      const finish = (outcome: CoverEditSuccess | Error) => {
        if (settled) return;
        settled = true;
        this.abandon = null;
        globalThis.clearTimeout(timeout);
        this.session.stop();
        if (outcome instanceof Error) reject(outcome);
        else resolve(outcome);
      };
      const timeout = globalThis.setTimeout(
        () => finish(new Error(
          `Ground-cover processing timed out after ${this.timeoutMs / 1000} seconds.`)),
        this.timeoutMs,
      );
      this.abandon = finish;
      this.session.connect({
        onResponse: (response) => {
          if (!isCoverEditResponse(response)) {
            finish(new Error(INVALID_RESPONSE));
            return;
          }
          if (response.id !== request.id) return;
          finish(response.ok ? response : new Error(response.error));
        },
        onCrash: () => finish(new Error(CRASHED)),
      });
      const data = request.grid.data as Uint8Array;
      if (!this.session.post(request, [data.buffer])) finish(new Error(POST_FAILED));
    });
  }

  /**
   * Terminate the edit in flight and reject whoever was awaiting it. The
   * adapter stays usable so a StrictMode remount does not retire it.
   */
  dispose(): void {
    this.session.stop();
    this.abandon?.(new Error(ABANDONED));
  }
}

function isCoverEditResponse(value: unknown): value is CoverEditResponse {
  if (!value || typeof value !== 'object') return false;
  const response = value as Record<string, unknown>;
  if (!Number.isSafeInteger(response.id) || typeof response.ok !== 'boolean') return false;
  if (!response.ok) return typeof response.error === 'string';
  return Number.isFinite(response.changed) && response.gridData instanceof Uint8Array &&
    !!response.coverMetadata && typeof response.coverMetadata === 'object';
}
