import { describe, expect, it } from 'vitest';
import type { WorkerLike } from './workerAdapter';
import type { GuestSimulationWorkerRequest, GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';
import { GuestSimulationClient } from './guestSimulationClient';

class FakeWorker implements WorkerLike<GuestSimulationWorkerRequest, GuestSimulationWorkerResponse> {
  onmessage: ((event: MessageEvent<GuestSimulationWorkerResponse>) => void) | null = null;
  onerror: ((event: ErrorEvent) => void) | null = null;
  posted: GuestSimulationWorkerRequest[] = [];
  terminated = false;
  postMessage(message: GuestSimulationWorkerRequest): void { this.posted.push(message); }
  terminate(): void { this.terminated = true; }
  deliver(response: GuestSimulationWorkerResponse): void { this.onmessage?.({ data: response } as MessageEvent<GuestSimulationWorkerResponse>); }
}

describe('GuestSimulationClient', () => {
  it('uses one long-lived worker and terminates it exactly on disposal', async () => {
    const worker = new FakeWorker();
    const client = new GuestSimulationClient(() => worker);
    const pending = client.snapshot();
    expect(worker.posted[0]).toMatchObject({ type: 'snapshot', sequence: 0 });
    worker.deliver({ type: 'error', requestId: worker.posted[0]!.requestId, sequence: 0,
      code: 'not-initialized', message: 'not ready' });
    await expect(pending).rejects.toThrow('not-initialized');
    client.dispose();
    client.dispose();
    expect(worker.terminated).toBe(true);
    await expect(client.snapshot()).rejects.toThrow('disposed');
  });

  it('routes compact advance requests and keeps rich snapshots out of the response', async () => {
    const worker = new FakeWorker();
    const client = new GuestSimulationClient(() => worker);
    const pending = client.advance({ targetSecond: 120, maxCpuMs: 8,
      topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
    expect(worker.posted[0]).toMatchObject({ type: 'advance', targetSecond: 120, maxCpuMs: 8, sequence: 0 });
    const ids = new Uint32Array([1, 2]);
    const edgeIndices = new Int32Array([0, 1]);
    const progress = new Float32Array([0.25, 0.75]);
    const statusFlags = new Uint32Array([8, 32]);
    worker.deliver({ type: 'advanced', requestId: worker.posted[0]!.requestId, sequence: 0,
      committedSecond: 60, backlogSeconds: 60,
      renderFrame: { ids, guestIds: ids, edgeIndices, progress, statusFlags, bytesPerGuest: 16,
        byteLength: ids.byteLength + edgeIndices.byteLength + progress.byteLength + statusFlags.byteLength },
      performance: { cpuMs: 1, workerCpuMs: 1, workerP95Ms: 1, budgetMs: 8, eventsProcessed: 0, budgetExceeded: false },
      topologyRevision: 3, operationsRevision: 0, weatherRevision: 0 });
    const response = await pending;
    expect(response).not.toHaveProperty('snapshot');
    expect(response.renderFrame.bytesPerGuest).toBe(16);
    client.dispose();
  });

  it('exposes on-demand guest inspection independently of compact advances', async () => {
    const worker = new FakeWorker();
    const client = new GuestSimulationClient(() => worker);
    const pending = client.inspectGuest('guest-000001');
    expect(worker.posted[0]).toMatchObject({ type: 'inspectGuest', guestId: 'guest-000001' });
    worker.deliver({ type: 'guest', requestId: worker.posted[0]!.requestId, sequence: 0,
      guestId: 'guest-000001', committedSecond: 12.5, guest: null });
    await expect(pending).resolves.toBeUndefined();
    client.dispose();
  });

  it('routes future-effective environment updates through the worker session', async () => {
    const worker = new FakeWorker();
    const client = new GuestSimulationClient(() => worker);
    const pending = client.updateEnvironment({ effectiveSecond: 300, topologyRevision: 3,
      operationsRevision: 1, weatherRevision: 2 });
    expect(worker.posted[0]).toMatchObject({ type: 'updateEnvironment', effectiveSecond: 300,
      operationsRevision: 1, weatherRevision: 2, sequence: 0 });
    worker.deliver({ type: 'environment-updated', requestId: worker.posted[0]!.requestId, sequence: 0,
      effectiveSecond: 300, committedSecond: 120, topologyRevision: 3, operationsRevision: 1, weatherRevision: 2 });
    await expect(pending).resolves.toMatchObject({ type: 'environment-updated', effectiveSecond: 300 });
    client.dispose();
  });
});
