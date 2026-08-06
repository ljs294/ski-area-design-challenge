import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { WorkerSession } from './workerAdapter';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<string, string> {
  posted: string[];
  transferred: Transferable[][];
  terminate: Mock<() => void>;
  deliver(response: string): void;
  crash(): void;
  failPost: boolean;
}

/** A factory plus the workers it has been asked for, in construction order. */
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
      postMessage(message: string, transfer: Transferable[] = []) {
        if (worker.failPost) throw new DOMException('could not clone', 'DataCloneError');
        worker.posted.push(message);
        worker.transferred.push(transfer);
      },
      deliver(response: string) {
        worker.onmessage?.({ data: response } as MessageEvent<string>);
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

function handlers() {
  return { onResponse: vi.fn(), onCrash: vi.fn() };
}

describe('WorkerSession', () => {
  it('starts one worker on demand and reuses it while it runs', () => {
    const { created, factory } = fakeWorkers();
    const session = new WorkerSession(factory);
    expect(session.running).toBe(false);
    expect(session.post('too early')).toBe(false);
    expect(created).toHaveLength(0);

    const first = handlers();
    session.connect(first);
    expect(session.running).toBe(true);
    expect(session.post('work', ['buffer' as unknown as Transferable])).toBe(true);

    // Rebinding for a second request must not orphan a running worker.
    const second = handlers();
    session.connect(second);
    expect(created).toHaveLength(1);
    created[0].deliver('done');
    expect(first.onResponse).not.toHaveBeenCalled();
    expect(second.onResponse).toHaveBeenCalledOnce();
    expect(second.onResponse).toHaveBeenCalledWith('done');
    expect(created[0].posted).toEqual(['work']);
    expect(created[0].transferred).toEqual([['buffer']]);
  });

  it('stops the running worker and ignores whatever it still delivers', () => {
    const { created, factory } = fakeWorkers();
    const session = new WorkerSession(factory);
    const bound = handlers();
    session.connect(bound);

    session.stop();
    expect(created[0].terminate).toHaveBeenCalledOnce();
    expect(session.running).toBe(false);
    expect(session.post('after stop')).toBe(false);

    // A real worker cannot answer after terminate(); a retired stub can, and
    // the answer must not reach the caller that moved on.
    created[0].onmessage?.({ data: 'late' } as MessageEvent<string>);
    created[0].onerror?.({} as ErrorEvent);
    expect(bound.onResponse).not.toHaveBeenCalled();
    expect(bound.onCrash).not.toHaveBeenCalled();

    session.stop();
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('stays usable after a stop, so a remount is not a retirement', () => {
    const { created, factory } = fakeWorkers();
    const session = new WorkerSession(factory);
    session.connect(handlers());
    session.stop();

    const restarted = handlers();
    session.connect(restarted);
    expect(created).toHaveLength(2);
    created[1].deliver('fresh');
    expect(restarted.onResponse).toHaveBeenCalledOnce();
    expect(restarted.onResponse).toHaveBeenCalledWith('fresh');
  });

  it('terminates a crashed worker before reporting it, and never reuses it', () => {
    const { created, factory } = fakeWorkers();
    const session = new WorkerSession(factory);
    const bound = {
      onResponse: vi.fn(),
      onCrash: vi.fn(() => {
        // The session must have retired the worker before the owner is told,
        // so an owner that restarts from here gets a fresh engine.
        expect(created[0].terminate).toHaveBeenCalledOnce();
        expect(session.running).toBe(false);
      }),
    };
    session.connect(bound);
    created[0].crash();
    expect(bound.onCrash).toHaveBeenCalledOnce();

    created[0].crash();
    expect(bound.onCrash).toHaveBeenCalledOnce();

    session.connect(bound);
    expect(created).toHaveLength(2);
  });

  it('retires a worker when postMessage rejects the request and remains reusable', () => {
    const { created, factory } = fakeWorkers();
    const session = new WorkerSession(factory);
    session.connect(handlers());
    created[0].failPost = true;

    let accepted: boolean | undefined;
    expect(() => { accepted = session.post('uncloneable'); }).not.toThrow();
    expect(accepted).toBe(false);
    expect(session.running).toBe(false);
    expect(created[0].terminate).toHaveBeenCalledOnce();

    session.connect(handlers());
    expect(created).toHaveLength(2);
    expect(session.post('fresh')).toBe(true);
    expect(created[1].posted).toEqual(['fresh']);
  });
});
