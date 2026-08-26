export interface DisplayImageryRequest {
  bytes: Uint8Array;
  mimeType: string;
  width: number;
  height: number;
  maxSide: number;
}

interface WorkerResponse { ok: boolean; bytes?: ArrayBuffer; error?: string }

export function displayImageryDimensions(
  width: number,
  height: number,
  maxSide: number,
): { width: number; height: number } {
  const longest = Math.max(width, height);
  if (longest <= maxSide) return { width, height };
  const scale = maxSide / longest;
  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  };
}

export function prepareDisplayImagery(
  request: DisplayImageryRequest,
  signal?: AbortSignal,
): Promise<Uint8Array> {
  if (Math.max(request.width, request.height) <= request.maxSide) {
    return Promise.resolve(request.bytes);
  }
  return new Promise<Uint8Array>((resolve, reject) => {
    const worker = new Worker(new URL('./displayImagery.worker.ts', import.meta.url), { type: 'module' });
    let settled = false;
    const finish = (result: Uint8Array | Error) => {
      if (settled) return;
      settled = true;
      signal?.removeEventListener('abort', abort);
      worker.terminate();
      if (result instanceof Error) reject(result);
      else resolve(result);
    };
    const abort = () => finish(new DOMException('Display imagery generation was cancelled.', 'AbortError'));
    signal?.addEventListener('abort', abort, { once: true });
    worker.onerror = () => finish(new Error('Display imagery worker stopped unexpectedly.'));
    worker.onmessage = (event: MessageEvent<WorkerResponse>) => {
      const response = event.data;
      finish(response.ok && response.bytes
        ? new Uint8Array(response.bytes)
        : new Error(response.error ?? 'Display imagery generation failed.'));
    };
    worker.postMessage(request, [request.bytes.buffer]);
  });
}
