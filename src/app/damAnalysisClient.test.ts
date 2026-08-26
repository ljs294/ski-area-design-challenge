import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import type { DamAnalysisResult } from '../damAnalysis';
import type { DamAnalysisRequest, DamAnalysisResponse } from './damAnalysisProtocol';
import { DamAnalysisAdapter } from './damAnalysisClient';
import type { WorkerLike } from './workerAdapter';
import type { EarthworkTerrainPatch } from '../earthwork';

interface FakeWorker extends WorkerLike<DamAnalysisRequest, DamAnalysisResponse> {
  posted: DamAnalysisRequest[];
  transferred: Transferable[][];
  terminate: Mock<() => void>;
  deliver(response: DamAnalysisResponse): void;
  crash(): void;
  failPost: boolean;
}

function fakeWorkers() {
  const created: FakeWorker[] = [];
  const factory = (): FakeWorker => {
    const worker: FakeWorker = {
      onmessage: null,
      onerror: null,
      posted: [],
      transferred: [],
      terminate: vi.fn(() => {}),
      failPost: false,
      postMessage(message: DamAnalysisRequest, transfer: Transferable[] = []) {
        if (worker.failPost) throw new DOMException('could not clone', 'DataCloneError');
        worker.posted.push(message);
        worker.transferred.push(transfer);
      },
      deliver(response: DamAnalysisResponse) {
        worker.onmessage?.({ data: response } as MessageEvent<DamAnalysisResponse>);
      },
      crash() {
        worker.onerror?.({} as ErrorEvent);
      },
    };
    created.push(worker);
    return worker;
  };
  return { created, factory };
}

function alignment(crestElevationM = 1000): Omit<DamAnalysisRequest, 'id'> {
  return {
    heights: Float32Array.from([1, 2, 3, 4]),
    gridSize: 2,
    bounds: { west: 0, east: 1, south: 0, north: 1 },
    points: [[0.25, 0.25], [0.75, 0.75]],
    crestElevationM,
    streams: [],
    contourGridSize: 2,
    contourIntervalM: 6.096,
    baseElevationChecksum: 'checksum',
  };
}

const analysis = { areaM2: 1234 } as DamAnalysisResult;
const grade = { patchIndices: new Uint32Array(), patchHeights: new Float32Array(),
  contourSegments: [], editedContourSegments: [], contourGridSize: 2,
  contourIntervalM: 6.096, baseElevationChecksum: 'checksum',
  disturbancePolygons: [] } satisfies EarthworkTerrainPatch;

function handlers() {
  return { onResult: vi.fn(), onError: vi.fn() };
}

describe('DamAnalysisAdapter', () => {
  it('numbers each alignment and transfers only its height grid', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const request = alignment();
    const bound = handlers();

    adapter.run(request, bound);
    expect(created[0].posted[0].id).toBe(1);
    expect(created[0].posted[0].crestElevationM).toBe(1000);
    expect(created[0].transferred[0]).toEqual([request.heights.buffer]);

    created[0].deliver({ id: 1, ok: true, result: analysis, grade });
    expect(bound.onResult).toHaveBeenCalledOnce();
    expect(bound.onResult).toHaveBeenCalledWith(analysis, grade);
    // A settled analysis owns nothing: the worker is gone before review opens.
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('reports a refused alignment as its own message, not a crash', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const bound = handlers();

    adapter.run(alignment(), bound);
    created[0].deliver({ id: 1, ok: false, error: 'No stream crosses this alignment.' });
    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onError).toHaveBeenCalledWith('No stream crosses this alignment.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('aborts the running analysis when a second alignment supersedes it', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const first = handlers();
    const second = handlers();

    adapter.run(alignment(1000), first);
    adapter.run(alignment(1200), second);
    expect(created).toHaveLength(2);
    expect(created[0].terminate).toHaveBeenCalledOnce();
    expect(created[1].posted[0].id).toBe(2);

    // The abandoned worker answering late must not reopen the old review.
    created[0].deliver({ id: 1, ok: true, result: analysis, grade });
    created[0].crash();
    expect(first.onResult).not.toHaveBeenCalled();
    expect(first.onError).not.toHaveBeenCalled();

    created[1].deliver({ id: 2, ok: true, result: analysis, grade });
    expect(second.onResult).toHaveBeenCalledOnce();
  });

  it('drops a response whose request number is no longer the live one', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const bound = handlers();

    adapter.run(alignment(), bound);
    created[0].deliver({ id: 0, ok: true, result: analysis, grade });
    created[0].deliver({ id: 7, ok: false, error: 'from a request nobody made' });
    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onError).not.toHaveBeenCalled();

    created[0].deliver({ id: 1, ok: true, result: analysis, grade });
    expect(bound.onResult).toHaveBeenCalledOnce();
  });

  it('rejects a malformed response from the live worker and terminates it', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const bound = handlers();

    adapter.run(alignment(), bound);
    created[0].deliver({ id: 1, ok: true } as DamAnalysisResponse);

    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onError).toHaveBeenCalledWith(
      'The pond analysis worker returned an invalid response. Try again.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('reports a crashed worker once, and only for the live request', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const bound = handlers();

    adapter.run(alignment(), bound);
    created[0].crash();
    expect(bound.onError).toHaveBeenCalledOnce();
    expect(bound.onError).toHaveBeenCalledWith(
      'The pond analysis worker failed. Try another alignment.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('reports and terminates when the worker refuses the request message', () => {
    const created: FakeWorker[] = [];
    const factory = (): FakeWorker => {
      const worker = fakeWorkers().factory();
      worker.failPost = true;
      created.push(worker);
      return worker;
    };
    const adapter = new DamAnalysisAdapter(factory);
    const bound = handlers();

    adapter.run(alignment(), bound);

    expect(bound.onError).toHaveBeenCalledWith(
      'The pond analysis worker could not accept the alignment. Try again.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('cancelling and disposing both abandon the analysis and leave the adapter usable', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new DamAnalysisAdapter(factory);
    const cancelled = handlers();

    adapter.run(alignment(), cancelled);
    adapter.cancel();
    expect(created[0].terminate).toHaveBeenCalledOnce();
    created[0].deliver({ id: 1, ok: true, result: analysis, grade });
    created[0].crash();
    expect(cancelled.onResult).not.toHaveBeenCalled();
    expect(cancelled.onError).not.toHaveBeenCalled();

    const disposed = handlers();
    adapter.run(alignment(), disposed);
    adapter.dispose();
    expect(created[1].terminate).toHaveBeenCalledOnce();
    created[1].deliver({ id: 3, ok: true, result: analysis, grade });
    expect(disposed.onResult).not.toHaveBeenCalled();

    // Teardown is abandonment, not retirement: a StrictMode remount still works.
    const remounted = handlers();
    adapter.run(alignment(), remounted);
    created[2].deliver({ id: created[2].posted[0].id, ok: true, result: analysis, grade });
    expect(remounted.onResult).toHaveBeenCalledOnce();
  });
});
