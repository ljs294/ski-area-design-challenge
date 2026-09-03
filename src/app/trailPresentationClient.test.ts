import { describe, expect, it, vi, type Mock } from 'vitest';
import { TRAIL_PRESENTATION_VERSION, type TrailPresentationResult } from '../types/trailPresentation';
import type { TrailPresentationRequest, TrailPresentationResponse } from './trailPresentationProtocol';
import { TrailPresentationAdapter } from './trailPresentationClient';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<TrailPresentationRequest, TrailPresentationResponse> {
  posted: TrailPresentationRequest[];
  terminate: Mock<() => void>;
  deliver(response: TrailPresentationResponse): void;
  crash(): void;
}

function fakeWorkers() {
  const created: FakeWorker[] = [];
  const factory = (): FakeWorker => {
    const worker: FakeWorker = {
      onmessage: null, onerror: null, posted: [], terminate: vi.fn(),
      postMessage(message) { worker.posted.push(message); },
      deliver(response) { worker.onmessage?.({ data: response } as MessageEvent<TrailPresentationResponse>); },
      crash() { worker.onerror?.({} as ErrorEvent); },
    };
    created.push(worker);
    return worker;
  };
  return { created, factory };
}

const EMPTY_RESULT: TrailPresentationResult = {
  version: TRAIL_PRESENTATION_VERSION, surface: [], routes: [], labels: [], junctions: [],
};
const INPUT = { trails: [], junctions: [] };
const handlers = () => ({ onResult: vi.fn(), onError: vi.fn() });

describe('TrailPresentationAdapter', () => {
  it('supersedes stale work and delivers only the active revision', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPresentationAdapter(factory);
    const first = handlers(), second = handlers();
    adapter.compile(INPUT, first);
    adapter.compile(INPUT, second);

    expect(created[0].terminate).toHaveBeenCalledOnce();
    created[0].deliver({ id: 1, ok: true, result: EMPTY_RESULT });
    created[1].deliver({ id: 2, ok: true, result: EMPTY_RESULT });
    expect(first.onResult).not.toHaveBeenCalled();
    expect(second.onResult).toHaveBeenCalledWith(EMPTY_RESULT);
  });

  it('retries one crash and reports a persistent worker failure', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPresentationAdapter(factory);
    const bound = handlers();
    adapter.compile(INPUT, bound);

    created[0].crash();
    expect(created[1].posted[0]).toEqual(created[0].posted[0]);
    expect(bound.onError).not.toHaveBeenCalled();
    created[1].crash();
    expect(bound.onError).toHaveBeenCalledWith(
      'The ski-run presentation could not be prepared.');
  });

  it('rejects an invalid result without replacing the coherent presentation', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new TrailPresentationAdapter(factory);
    const bound = handlers();
    adapter.compile(INPUT, bound);
    created[0].deliver({ id: 1, ok: true,
      result: { ...EMPTY_RESULT, version: 99 } as never });
    expect(bound.onResult).not.toHaveBeenCalled();
    expect(bound.onError).toHaveBeenCalledWith(
      'The ski-run presentation returned invalid geometry.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });
});
