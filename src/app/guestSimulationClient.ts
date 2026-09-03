import { WorkerSession, type WorkerFactory } from './workerAdapter';
import type { GuestSimulationWorkerRequest, GuestSimulationWorkerResponse } from './guestSimulationWorkerProtocol';

function browserWorker(): Worker {
  return new Worker(new URL('./guestSimulation.worker.ts', import.meta.url), { type: 'module' });
}

type SuccessResponse = Exclude<GuestSimulationWorkerResponse, { type: 'error' }>;
type UnsequencedRequest = GuestSimulationWorkerRequest extends infer Request
  ? Request extends GuestSimulationWorkerRequest ? Omit<Request, 'requestId' | 'sequence'> : never
  : never;

export class GuestSimulationClient {
  private readonly session: WorkerSession<GuestSimulationWorkerRequest, GuestSimulationWorkerResponse>;
  private readonly pending = new Map<string, { resolve(value: SuccessResponse): void; reject(error: Error): void }>();
  private sequence = 0;
  private disposed = false;

  constructor(factory: WorkerFactory<GuestSimulationWorkerRequest, GuestSimulationWorkerResponse> = browserWorker) {
    this.session = new WorkerSession(factory);
  }

  initialize(request: Omit<Extract<GuestSimulationWorkerRequest, { type: 'initialize' }>, 'requestId' | 'sequence'>) {
    return this.send({ ...request, type: 'initialize' }).then((response) => response.snapshot);
  }

  restore(bytes: Uint8Array, expectedTopologyRevision: number) {
    return this.send({ type: 'restore', bytes, expectedTopologyRevision }).then((response) => response.snapshot);
  }

  advance(toTick: number, expectedEnvironmentRevision: number, expectedTopologyRevision: number,
    conditionSnapshot?: import('../guestSimulation/conditions').ConditionSnapshot) {
    return this.send({ type: 'advance', toTick, expectedEnvironmentRevision, expectedTopologyRevision,
      ...(conditionSnapshot ? { conditionSnapshot } : {}) }).then((response) => response.snapshot);
  }

  snapshot() { return this.send({ type: 'snapshot' }).then((response) => response.snapshot); }
  checkpoint() { return this.send({ type: 'checkpoint' }).then((response) => {
    if (response.type !== 'checkpoint') throw new Error('Guest simulation worker returned the wrong checkpoint response.');
    return { snapshot: response.snapshot, bytes: response.bytes };
  }); }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.session.stop();
    for (const pending of this.pending.values()) pending.reject(new Error('Guest simulation client was disposed.'));
    this.pending.clear();
  }

  private send(request: UnsequencedRequest): Promise<SuccessResponse> {
    if (this.disposed) return Promise.reject(new Error('Guest simulation client was disposed.'));
    const sequence = this.sequence++;
    const requestId = `guest-request-${sequence}`;
    return new Promise((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject });
      this.session.connect({
        onResponse: (response) => this.receive(response),
        onCrash: () => this.failAll(new Error('Guest simulation worker stopped unexpectedly.')),
      });
      if (!this.session.post({ ...request, requestId, sequence } as GuestSimulationWorkerRequest)) {
        this.pending.delete(requestId);
        reject(new Error('Guest simulation worker could not accept the request.'));
      }
    });
  }

  private receive(response: GuestSimulationWorkerResponse): void {
    const pending = this.pending.get(response.requestId);
    if (!pending) return;
    this.pending.delete(response.requestId);
    if (response.type === 'error') pending.reject(new Error(`${response.code}: ${response.message}`));
    else pending.resolve(response);
  }

  private failAll(error: Error): void {
    for (const pending of this.pending.values()) pending.reject(error);
    this.pending.clear();
  }
}
