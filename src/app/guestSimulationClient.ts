import { WorkerSession, type WorkerFactory } from './workerAdapter';
import type {
  GuestSimulationCompactAdvanceRequest,
  GuestSimulationEnvironmentUpdateRequest,
  GuestSimulationTopologyUpdateRequest,
  GuestSimulationWorkerRequest,
  GuestSimulationWorkerResponse,
} from './guestSimulationWorkerProtocol';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import type { GuestState } from '../guestSimulation/contracts';

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

  initialize(request: Omit<Extract<GuestSimulationWorkerRequest, { type: 'initialize' }>, 'requestId' | 'sequence'>): Promise<GuestSimulationEngineSnapshot> {
    return this.send({ ...request, type: 'initialize' }).then((response) => {
      if (!('snapshot' in response) || !response.snapshot) throw new Error('Guest simulation worker returned the wrong initialize response.');
      return response.snapshot;
    });
  }

  restore(bytes: Uint8Array, expectedTopologyRevision: number): Promise<GuestSimulationEngineSnapshot> {
    return this.send({ type: 'restore', bytes, expectedTopologyRevision }).then((response) => {
      if (!('snapshot' in response) || !response.snapshot) throw new Error('Guest simulation worker returned the wrong restore response.');
      return response.snapshot;
    });
  }

  advance(toTick: number, expectedEnvironmentRevision: number, expectedTopologyRevision: number,
    conditionSnapshot?: import('../guestSimulation/conditions').ConditionSnapshot): Promise<GuestSimulationEngineSnapshot>;
  advance(request: Omit<GuestSimulationCompactAdvanceRequest, 'requestId' | 'sequence' | 'type'>): Promise<Extract<GuestSimulationWorkerResponse, { type: 'advanced'; committedSecond: number }>>;
  advance(toTickOrRequest: number | Omit<GuestSimulationCompactAdvanceRequest, 'requestId' | 'sequence' | 'type'>,
    expectedEnvironmentRevision?: number, expectedTopologyRevision?: number,
    conditionSnapshot?: import('../guestSimulation/conditions').ConditionSnapshot) {
    if (typeof toTickOrRequest !== 'number') {
      return this.send({ ...toTickOrRequest, type: 'advance' }).then((response) => {
        if (response.type !== 'advanced' || !('renderFrame' in response)) {
          throw new Error('Guest simulation worker returned the wrong compact advance response.');
        }
        return response;
      });
    }
    return this.send({ type: 'advance', toTick: toTickOrRequest,
      expectedEnvironmentRevision: expectedEnvironmentRevision!, expectedTopologyRevision: expectedTopologyRevision!,
      ...(conditionSnapshot ? { conditionSnapshot } : {}) }).then((response) => {
      if (!('snapshot' in response) || !response.snapshot) throw new Error('Guest simulation worker returned the wrong advance response.');
      return response.snapshot;
    });
  }

  /** Named alias for coordinators that want to make the protocol mode explicit. */
  advanceCompact(request: Omit<GuestSimulationCompactAdvanceRequest, 'requestId' | 'sequence' | 'type'>) {
    return this.advance(request);
  }

  updateTopology(request: Omit<GuestSimulationTopologyUpdateRequest, 'requestId' | 'sequence' | 'type'>) {
    return this.send({ ...request, type: 'topology-update' }).then((response) => {
      if (response.type !== 'topology-updated') {
        throw new Error('Guest simulation worker returned the wrong topology update response.');
      }
      return response;
    });
  }

  updateEnvironment(request: Omit<GuestSimulationEnvironmentUpdateRequest, 'requestId' | 'sequence' | 'type'>): Promise<Extract<GuestSimulationWorkerResponse, { type: 'environment-updated' }>> {
    return this.send({ ...request, type: 'updateEnvironment' }).then((response) => {
      if (response.type !== 'environment-updated') {
        throw new Error('Guest simulation worker returned the wrong environment update response.');
      }
      return response;
    });
  }

  snapshot(): Promise<GuestSimulationEngineSnapshot> {
    return this.send({ type: 'snapshot' }).then((response) => {
      if (!('snapshot' in response) || !response.snapshot) throw new Error('Guest simulation worker returned the wrong snapshot response.');
      return response.snapshot;
    });
  }

  inspectGuest(guestId: string): Promise<GuestState | undefined> {
    return this.send({ type: 'inspectGuest', guestId }).then((response) => {
      if (response.type !== 'guest') throw new Error('Guest simulation worker returned the wrong guest inspection response.');
      return response.guest ?? undefined;
    });
  }

  checkpoint() { return this.send({ type: 'checkpoint' }).then((response) => {
    if (response.type !== 'checkpoint') throw new Error('Guest simulation worker returned the wrong checkpoint response.');
    return { snapshot: response.snapshot, bytes: response.bytes, committedSecond: response.committedSecond };
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
