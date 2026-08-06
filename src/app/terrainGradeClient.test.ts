import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import type { TerrainGradeRequest, TerrainGradeResponse } from './terrainGradeProtocol';
import { TerrainGradeAdapter } from './terrainGradeClient';
import type { TerrainGradeIdentity, TerrainGradeSuccess } from './terrainGradeClient';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<TerrainGradeRequest, TerrainGradeResponse> {
  posted: TerrainGradeRequest[];
  transferred: Transferable[][];
  terminate: Mock<() => void>;
  deliver(response: TerrainGradeResponse): void;
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
      postMessage(message: TerrainGradeRequest, transfer: Transferable[] = []) {
        if (worker.failPost) throw new DOMException('could not clone', 'DataCloneError');
        worker.posted.push(message);
        worker.transferred.push(transfer);
      },
      deliver(response: TerrainGradeResponse) {
        worker.onmessage?.({ data: response } as MessageEvent<TerrainGradeResponse>);
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

const IDENTITY: TerrainGradeIdentity = {
  baseElevationChecksum: 'elevation-a',
  trailGeometryKey: 'grade-0000000a',
};

function gradeRequest(id: number, identity: TerrainGradeIdentity = IDENTITY): TerrainGradeRequest {
  return {
    id,
    kind: 'trail',
    heights: Float32Array.from([1, 2, 3, 4]),
    gridSize: 2,
    bounds: { west: 0, east: 1, south: 0, north: 1 },
    parts: [],
    brushWidthM: 12,
    ...identity,
  };
}

function graded(id: number, identity: TerrainGradeIdentity = IDENTITY): TerrainGradeSuccess {
  return { id, ok: true, cutM3: 100, fillM3: 40, ...identity } as TerrainGradeSuccess;
}

/** Handlers whose preview token and live identity default to matching. */
function handlers(overrides: Partial<{ current: number[]; live: TerrainGradeIdentity }> = {}) {
  return {
    isCurrent: (id: number) => (overrides.current ?? [1]).includes(id),
    live: () => overrides.live ?? IDENTITY,
    onResult: vi.fn(),
    onSuperseded: vi.fn(),
    onError: vi.fn(),
    onCrash: vi.fn(),
  };
}

describe('TerrainGradeAdapter', () => {
  it('posts the request as given and transfers its height grid', () => {
    const { created, factory } = fakeWorkers();
    const request = gradeRequest(1);
    const bound = handlers();
    new TerrainGradeAdapter(factory).run(request, bound);

    expect(created[0].posted[0]).toBe(request);
    expect(created[0].transferred[0]).toEqual([request.heights.buffer]);
    created[0].deliver(graded(1));
    expect(bound.onResult).toHaveBeenCalledOnce();
    expect(bound.onSuperseded).not.toHaveBeenCalled();
  });

  it('ignores a grade whose preview token was superseded', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers({ current: [2] });
    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    created[0].deliver(graded(1));
    created[0].deliver({ id: 1, ok: false, error: 'too steep' });
    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onSuperseded).not.toHaveBeenCalled();
    expect(bound.onError).not.toHaveBeenCalled();
  });

  it('accepts only the response identity of the request actually posted', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers({ current: [1, 2] });
    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    // Token 2 may be current elsewhere, but this worker was asked for token 1.
    created[0].deliver(graded(2));
    expect(bound.onResult).not.toHaveBeenCalled();
    created[0].deliver(graded(1));
    expect(bound.onResult).toHaveBeenCalledOnce();
  });

  it('rejects a malformed response from the live worker and terminates it', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers();
    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    created[0].deliver({ id: 1, ok: true } as TerrainGradeResponse);

    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onError).toHaveBeenCalledWith(
      'Terrain grading returned an invalid response. Try again.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('refuses a grade computed against a different package or footprint', () => {
    const changedTerrain = fakeWorkers();
    const afterTerrain = handlers({ live: { ...IDENTITY, baseElevationChecksum: 'elevation-b' } });
    new TerrainGradeAdapter(changedTerrain.factory).run(gradeRequest(1), afterTerrain);
    changedTerrain.created[0].deliver(graded(1));
    expect(afterTerrain.onSuperseded).toHaveBeenCalledOnce();
    expect(afterTerrain.onResult).not.toHaveBeenCalled();

    const changedFootprint = fakeWorkers();
    const afterRedraw = handlers({ live: { ...IDENTITY, trailGeometryKey: 'grade-0000000b' } });
    new TerrainGradeAdapter(changedFootprint.factory).run(gradeRequest(1), afterRedraw);
    changedFootprint.created[0].deliver(graded(1));
    expect(afterRedraw.onSuperseded).toHaveBeenCalledOnce();
    expect(afterRedraw.onResult).not.toHaveBeenCalled();

    // Leaving review is the same refusal: nothing is in review to apply it to.
    const leftReview = fakeWorkers();
    const afterCancel = handlers({ live: { ...IDENTITY, trailGeometryKey: '' } });
    new TerrainGradeAdapter(leftReview.factory).run(gradeRequest(1), afterCancel);
    leftReview.created[0].deliver(graded(1));
    expect(afterCancel.onSuperseded).toHaveBeenCalledOnce();
  });

  it('reports a refused grade as its own message rather than a supersession', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers();
    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    created[0].deliver({ id: 1, ok: false, error: 'The route is too steep to bench.' });
    expect(bound.onError).toHaveBeenCalledWith('The route is too steep to bench.');
    expect(bound.onSuperseded).not.toHaveBeenCalled();
    expect(bound.onResult).not.toHaveBeenCalled();
  });

  it('aborts a grade still running, and reuses the worker once it is idle', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TerrainGradeAdapter(factory);
    const first = handlers({ current: [1, 2, 3] });

    adapter.run(gradeRequest(1), first);
    adapter.run(gradeRequest(2), first);
    expect(created).toHaveLength(2);
    expect(created[0].terminate).toHaveBeenCalledOnce();

    // A finished grade leaves an idle engine; toggling the preview reuses it.
    created[1].deliver(graded(2));
    expect(first.onResult).toHaveBeenCalledOnce();
    adapter.run(gradeRequest(3), first);
    expect(created).toHaveLength(2);
    expect(created[1].terminate).not.toHaveBeenCalled();
  });

  it('terminates a crashed worker, reports it once, and grades again on a fresh one', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TerrainGradeAdapter(factory);
    const bound = handlers({ current: [1, 2] });

    adapter.run(gradeRequest(1), bound);
    created[0].crash();
    expect(bound.onCrash).toHaveBeenCalledOnce();
    expect(created[0].terminate).toHaveBeenCalledOnce();

    adapter.run(gradeRequest(2), bound);
    expect(created).toHaveLength(2);
    created[1].deliver(graded(2));
    expect(bound.onResult).toHaveBeenCalledOnce();
  });

  it('reports and terminates when the worker refuses the request message', () => {
    const created: FakeWorker[] = [];
    const factory = (): FakeWorker => {
      const worker = fakeWorkers().factory();
      worker.failPost = true;
      created.push(worker);
      return worker;
    };
    const bound = handlers();

    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    expect(bound.onError).toHaveBeenCalledWith('Terrain grading could not start. Try again.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
    expect(bound.onCrash).not.toHaveBeenCalled();
  });

  it('stays silent about a crash the preview has already moved past', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers({ current: [] });
    new TerrainGradeAdapter(factory).run(gradeRequest(1), bound);

    created[0].crash();
    expect(bound.onCrash).not.toHaveBeenCalled();
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('stopping and disposing both abandon the grade and leave the adapter usable', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TerrainGradeAdapter(factory);
    const bound = handlers({ current: [1, 2, 3] });

    adapter.run(gradeRequest(1), bound);
    adapter.stop();
    expect(created[0].terminate).toHaveBeenCalledOnce();
    created[0].deliver(graded(1));
    expect(bound.onResult).not.toHaveBeenCalled();

    adapter.run(gradeRequest(2), bound);
    adapter.dispose();
    expect(created[1].terminate).toHaveBeenCalledOnce();
    created[1].deliver(graded(2));
    expect(bound.onResult).not.toHaveBeenCalled();

    // Teardown is abandonment, not retirement: a StrictMode remount still works.
    adapter.run(gradeRequest(3), bound);
    created[2].deliver(graded(3));
    expect(bound.onResult).toHaveBeenCalledOnce();
  });
});
