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
});
