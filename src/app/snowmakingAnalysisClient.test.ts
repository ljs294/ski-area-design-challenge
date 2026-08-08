import { describe, expect, it, vi, type Mock } from 'vitest';
import type { SnowmakingAnalysisInput, SnowmakingAnalysisResult } from '../snowmakingHydraulics';
import type { SnowmakingAnalysisRequest, SnowmakingAnalysisResponse } from './snowmakingAnalysisProtocol';
import { SnowmakingAnalysisAdapter } from './snowmakingAnalysisClient';
import type { WorkerLike } from './workerAdapter';

interface FakeWorker extends WorkerLike<SnowmakingAnalysisRequest, SnowmakingAnalysisResponse> {
  posted: SnowmakingAnalysisRequest[];
  terminate: Mock<() => void>;
  deliver(response: SnowmakingAnalysisResponse): void;
}

function fakeWorkers() {
  const created: FakeWorker[] = [];
  const factory = (): FakeWorker => {
    const worker: FakeWorker = { onmessage: null, onerror: null, posted: [],
      terminate: vi.fn(() => {}),
      postMessage(message) { worker.posted.push(message); },
      deliver(response) { worker.onmessage?.({ data: response } as MessageEvent<SnowmakingAnalysisResponse>); } };
    created.push(worker); return worker;
  };
  return { created, factory };
}

const input: SnowmakingAnalysisInput = { nodes: [], pipes: [], guns: [], selectedGunIds: [],
  selectedIntakeNodeIds: [], wetBulbF: 28, pumpSettings: {} };
const result: SnowmakingAnalysisResult = { status: 'failed', diagnostics: [], systems: [], sources: [],
  summary: { systemCount: 0, readySystemCount: 0, selectedGunCount: 0, analyzedGunCount: 0,
    readyGunCount: 0, notAnalyzedGunCount: 0, requestedDemandGpm: 0, waterUseGalPerHour: 0,
    minimumGunPressurePsi: null, limitingSourceRuntimeHours: null, overallReady: false } };

describe('SnowmakingAnalysisAdapter', () => {
  it('terminates a superseded worker and ignores its late response', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new SnowmakingAnalysisAdapter(factory);
    const first = { onResult: vi.fn(), onError: vi.fn() };
    const second = { onResult: vi.fn(), onError: vi.fn() };
    adapter.run(input, first); adapter.run(input, second);
    expect(created[0].terminate).toHaveBeenCalledOnce();
    created[0].deliver({ id: created[0].posted[0].id, ok: true, result });
    expect(first.onResult).not.toHaveBeenCalled();
    created[1].deliver({ id: created[1].posted[0].id, ok: true, result });
    expect(second.onResult).toHaveBeenCalledWith(result);
    expect(created[1].terminate).toHaveBeenCalledOnce();
  });

  it('drops stale revisions from the live worker', () => {
    const { created, factory } = fakeWorkers();
    const adapter = new SnowmakingAnalysisAdapter(factory);
    const handlers = { onResult: vi.fn(), onError: vi.fn() };
    adapter.run(input, handlers);
    created[0].deliver({ id: created[0].posted[0].id - 1, ok: true, result });
    expect(handlers.onResult).not.toHaveBeenCalled();
    adapter.cancel();
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });
});
