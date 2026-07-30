import { describe, expect, it, vi } from 'vitest';
import type { CoverGrid } from '../types';
import type { CoverEditRequest, CoverEditResponse } from './coverEditProtocol';
import { runCoverEditWorker } from './coverEditClient';

function request(): CoverEditRequest {
  return {
    grid: {
      bounds: { west: 0, east: 1, south: 0, north: 1 },
      width: 2, height: 2, cellSizeM: 1,
      data: new Uint8Array([1, 1, 1, 1]),
      complete: true, nodataCount: 0,
      source: 'esa-worldcover-2021-v200', vintage: '2021',
    } as CoverGrid,
    polygons: [],
    deriveDisplay: false,
  };
}

function fakeWorker(response?: CoverEditResponse) {
  const worker = {
    onmessage: null as ((event: MessageEvent<CoverEditResponse>) => void) | null,
    onerror: null as (() => void) | null,
    transfer: [] as Transferable[],
    terminate: vi.fn(),
    postMessage(_message: CoverEditRequest, transfer: Transferable[]) {
      worker.transfer = transfer;
      if (response) queueMicrotask(() =>
        worker.onmessage?.({ data: response } as MessageEvent<CoverEditResponse>));
    },
  };
  return worker;
}

describe('runCoverEditWorker', () => {
  it('transfers the grid buffer and resolves a successful response', async () => {
    const response: CoverEditResponse = {
      ok: true, changed: 1, gridData: new Uint8Array([3, 1, 1, 1]),
    };
    const worker = fakeWorker(response);
    const input = request();
    await expect(runCoverEditWorker(input, { workerFactory: () => worker }))
      .resolves.toMatchObject({ changed: 1 });
    expect(worker.transfer).toEqual([(input.grid.data as Uint8Array).buffer]);
    expect(worker.terminate).toHaveBeenCalledOnce();
  });

  it('rejects worker errors and timeouts and always terminates', async () => {
    const failed = fakeWorker({ ok: false, error: 'bad cover' });
    await expect(runCoverEditWorker(request(), { workerFactory: () => failed }))
      .rejects.toThrow('bad cover');
    expect(failed.terminate).toHaveBeenCalledOnce();

    const timedOut = fakeWorker();
    await expect(runCoverEditWorker(request(), {
      workerFactory: () => timedOut,
      timeoutMs: 1,
    })).rejects.toThrow('timed out');
    expect(timedOut.terminate).toHaveBeenCalledOnce();
  });
});
