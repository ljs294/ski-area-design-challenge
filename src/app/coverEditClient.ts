import type { CoverEditRequest, CoverEditResponse } from './coverEditProtocol';

const DEFAULT_TIMEOUT_MS = 60_000;
type CoverEditSuccess = Extract<CoverEditResponse, { ok: true }>;

interface CoverEditWorkerLike {
  onmessage: ((event: MessageEvent<CoverEditResponse>) => void) | null;
  onerror: (() => void) | null;
  postMessage(message: CoverEditRequest, transfer: Transferable[]): void;
  terminate(): void;
}

export interface CoverEditClientOptions {
  timeoutMs?: number;
  workerFactory?: () => CoverEditWorkerLike;
}

export function runCoverEditWorker(
  request: CoverEditRequest,
  options: CoverEditClientOptions = {}
): Promise<CoverEditSuccess> {
  return new Promise((resolve, reject) => {
    const worker = options.workerFactory?.() ??
      new Worker(new URL('./coverEdit.worker.ts', import.meta.url), { type: 'module' });
    let settled = false;
    const finish = (result: CoverEditSuccess | Error) => {
      if (settled) return;
      settled = true;
      globalThis.clearTimeout(timeout);
      worker.terminate();
      if (result instanceof Error) reject(result);
      else resolve(result);
    };
    const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
    const timeout = globalThis.setTimeout(
      () => finish(new Error(`Ground-cover processing timed out after ${timeoutMs / 1000} seconds.`)),
      timeoutMs
    );
    worker.onmessage = (event: MessageEvent<CoverEditResponse>) => {
      const response = event.data;
      if (!response.ok) finish(new Error(response.error));
      else finish(response);
    };
    worker.onerror = () => finish(new Error('Ground-cover worker stopped unexpectedly.'));
    const data = request.grid.data as Uint8Array;
    worker.postMessage(request, [data.buffer]);
  });
}
