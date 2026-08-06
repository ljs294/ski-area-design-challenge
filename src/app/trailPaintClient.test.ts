import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import type { SavedTrailPart } from '../types';
import type { TrailPaintRequest, TrailPaintResponse } from './trailPaintProtocol';
import { TrailPaintAdapter } from './trailPaintClient';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<TrailPaintRequest, TrailPaintResponse> {
  posted: TrailPaintRequest[];
  transferred: Transferable[][];
  terminate: Mock<() => void>;
  deliver(response: TrailPaintResponse): void;
  crash(): void;
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
      postMessage(message: TrailPaintRequest, transfer: Transferable[] = []) {
        worker.posted.push(message);
        worker.transferred.push(transfer);
      },
      deliver(response: TrailPaintResponse) {
        worker.onmessage?.({ data: response } as MessageEvent<TrailPaintResponse>);
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

const INIT = { origin: [-121.5, 46.9] as [number, number], brushWidthM: 12 };

function handlers() {
  return {
    onReady: vi.fn(),
    onPreview: vi.fn(),
    onAnalysis: vi.fn(),
    onFailure: vi.fn(),
    onRestart: vi.fn(),
    onLost: vi.fn(),
  };
}

function preview(id: number, areaM2: number): TrailPaintResponse {
  return { id, ok: true, type: 'preview', polygons: [], areaM2, canUndo: true };
}

describe('TrailPaintAdapter', () => {
  it('initializes an engine and numbers every request sent to it', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPaintAdapter(factory);
    const bound = handlers();

    adapter.start(INIT, bound);
    expect(created[0].posted[0]).toEqual({ type: 'init', ...INIT, id: 1 });

    const coordinates = new Float64Array([0, 0, 1, 1]);
    expect(adapter.post({ type: 'stroke', mode: 'paint', coordinates }, [coordinates.buffer]))
      .toBe(2);
    expect(adapter.post({ type: 'undo' })).toBe(3);
    expect(created[0].posted.map((request) => request.id)).toEqual([1, 2, 3]);
    expect(created[0].transferred[1]).toEqual([coordinates.buffer]);
  });

  it('refuses to number a request when no engine is running', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPaintAdapter(factory);
    expect(adapter.post({ type: 'undo' })).toBe(0);

    adapter.start(INIT, handlers());
    adapter.stop();
    expect(adapter.post({ type: 'undo' })).toBe(0);
    expect(created[0].posted).toHaveLength(1);
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('routes each answer to its own handler', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers();
    new TrailPaintAdapter(factory).start(INIT, bound);

    created[0].deliver({ id: 1, ok: true, type: 'ready' });
    expect(bound.onReady).toHaveBeenCalledOnce();

    created[0].deliver(preview(2, 400));
    expect(bound.onPreview).toHaveBeenCalledOnce();
    expect(bound.onPreview.mock.calls[0][0]).toMatchObject({ areaM2: 400 });

    const parts: SavedTrailPart[] = [];
    created[0].deliver({ id: 3, ok: true, type: 'analysis', parts, areaM2: 400 });
    expect(bound.onAnalysis).toHaveBeenCalledOnce();

    created[0].deliver({ id: 4, ok: false, error: 'Trail analysis failed.' });
    expect(bound.onFailure).toHaveBeenCalledWith('Trail analysis failed.');
  });

  it('drops an answer that would repaint the canvas backwards', () => {
    const { created, factory } = fakeWorkers();
    const bound = handlers();
    new TrailPaintAdapter(factory).start(INIT, bound);

    created[0].deliver(preview(5, 500));
    created[0].deliver(preview(3, 300));
    created[0].deliver(preview(4, 400));
    expect(bound.onPreview).toHaveBeenCalledOnce();
    expect(bound.onPreview.mock.calls[0][0]).toMatchObject({ areaM2: 500 });

    // The watermark tracks the newest answer, not the newest request.
    created[0].deliver(preview(6, 600));
    expect(bound.onPreview).toHaveBeenCalledTimes(2);
  });

  it('restarts a crashed engine once and asks the owner to repaint it', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPaintAdapter(factory);
    const bound = handlers();
    adapter.start(INIT, bound);
    adapter.post({ type: 'undo' });

    created[0].crash();
    expect(bound.onRestart).toHaveBeenCalledOnce();
    expect(bound.onLost).not.toHaveBeenCalled();
    expect(created[0].terminate).toHaveBeenCalledOnce();
    // The replacement is initialized with the same origin and brush, and its
    // request numbers keep climbing so a stale answer stays identifiable.
    expect(created[1].posted[0]).toEqual({ type: 'init', ...INIT, id: 3 });

    // The empty canvas has answered nothing, so its first answer is applied
    // even though the dead engine had already answered a higher number.
    created[1].deliver({ id: 3, ok: true, type: 'ready' });
    expect(bound.onReady).toHaveBeenCalledOnce();

    created[1].crash();
    expect(bound.onLost).toHaveBeenCalledOnce();
    expect(bound.onRestart).toHaveBeenCalledOnce();
    expect(created).toHaveLength(2);
    expect(adapter.post({ type: 'undo' })).toBe(0);
  });

  it('allows another restart only when the painter is opened again', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPaintAdapter(factory);
    const bound = handlers();

    adapter.start(INIT, bound);
    created[0].crash();
    expect(bound.onRestart).toHaveBeenCalledOnce();

    // Restarting the engine for a new brush width is the same attempt.
    adapter.start({ ...INIT, brushWidthM: 20 }, bound);
    created[2].crash();
    expect(bound.onLost).toHaveBeenCalledOnce();

    adapter.allowRestart();
    adapter.start(INIT, bound);
    created[3].crash();
    expect(bound.onRestart).toHaveBeenCalledTimes(2);
  });

  it('stopping and disposing both discard the engine and leave the adapter usable', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPaintAdapter(factory);
    const bound = handlers();

    adapter.start(INIT, bound);
    adapter.dispose();
    expect(created[0].terminate).toHaveBeenCalledOnce();
    created[0].deliver(preview(2, 200));
    created[0].crash();
    expect(bound.onPreview).not.toHaveBeenCalled();
    expect(bound.onRestart).not.toHaveBeenCalled();

    // Teardown is a discard, not a retirement: a StrictMode remount still works.
    adapter.start(INIT, bound);
    created[1].deliver({ id: created[1].posted[0].id, ok: true, type: 'ready' });
    expect(bound.onReady).toHaveBeenCalledOnce();
  });
});
