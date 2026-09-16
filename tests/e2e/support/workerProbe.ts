import type { Page } from '@playwright/test';

export interface WorkerProbePublication {
  id: number; type: string; generation: number; operationGeneration: number; committedRevision: number;
  macroSecond?: number; microSecond?: number; at?: string; requestType?: string; requestedTargetMs?: number; busy?: boolean;
  movementCount?: number; selectedId: string | null;
  representatives?: { id?: string; status?: string; nodeId?: string; edgeId?: string | null; started?: number; due?: number; runs?: number; nextPlan?: string }[];
  flow?: unknown;
}

export interface WorkerProbeEntry {
  url: string;
  terminationCount: number;
  messageCount?: number;
  /** Bounded, decoded status counts from real dual-clock movement frames. */
  movementStatuses?: Record<string, number>[];
  /** Bounded control/publication identities for correlating a movement frame with worker state. */
  publications?: WorkerProbePublication[];
  publicationSize?: number;
  publicationWriteIndex?: number;
  movementStatusSize?: number;
  movementStatusWriteIndex?: number;
  outstandingRequestCount?: number;
  droppedRequestCount?: number;
}

export class BoundedProbeRequestLedger<T> {
  readonly #values = new Map<number, T>();
  #dropped = 0;
  readonly capacity: number;
  constructor(capacity: number) {
    this.capacity = capacity;
    if (!Number.isInteger(capacity) || capacity < 1) throw new Error('Probe request capacity must be a positive integer');
  }
  set(id: number, value: T): void {
    if (!this.#values.has(id) && this.#values.size === this.capacity) {
      const oldest = this.#values.keys().next().value as number | undefined;
      if (oldest !== undefined) this.#values.delete(oldest);
      this.#dropped += 1;
    }
    this.#values.set(id, value);
  }
  complete(id: number): T | undefined {
    const value = this.#values.get(id);
    this.#values.delete(id);
    return value;
  }
  peek(id: number): T | undefined { return this.#values.get(id); }
  clear(): void { this.#values.clear(); }
  get size(): number { return this.#values.size; }
  get dropped(): number { return this.#dropped; }
}

export function orderedProbeRing<T>(values: readonly T[], size: number, writeIndex: number): T[] {
  if (size < values.length) return values.slice(0, size);
  return [...values.slice(writeIndex), ...values.slice(0, writeIndex)];
}

export function classifyDualClockProbePublications(
  publications: readonly WorkerProbePublication[],
  accepted?: { generation: number; operationGeneration: number },
): { measured: WorkerProbePublication[]; acknowledgements: WorkerProbePublication[]; stale: WorkerProbePublication[] } {
  const stale: WorkerProbePublication[] = [], measured: WorkerProbePublication[] = [], acknowledgements: WorkerProbePublication[] = [];
  for (const publication of publications) {
    if (accepted && (publication.generation !== accepted.generation
      || publication.operationGeneration !== accepted.operationGeneration)) { stale.push(publication); continue; }
    const flow = publication.flow as { active?: number } | undefined;
    if (Number.isFinite(publication.macroSecond) && Number.isFinite(publication.microSecond)
      && Number.isFinite(flow?.active) && Number.isFinite(publication.movementCount)) measured.push(publication);
    else acknowledgements.push(publication);
  }
  return { measured, acknowledgements, stale };
}

/**
 * Observe real browser Worker construction and termination without changing
 * the application protocol. The script is installed before every navigation,
 * so Vite's hashed worker URLs and replacement workers are both visible.
 */
export async function installWorkerProbe(
  page: Page,
  options: { failPostFor?: string } = {},
): Promise<void> {
  await page.addInitScript(({ failPostFor }) => {
    const NativeWorker = window.Worker;
    const entries: WorkerProbeEntry[] = [];
    const ProbedWorker = new Proxy(NativeWorker, {
      construct(target, argumentsList) {
        const worker = Reflect.construct(target, argumentsList) as Worker;
        const entry: WorkerProbeEntry = { url: String(argumentsList[0]), terminationCount: 0, messageCount: 0, movementStatuses: [], publications: [],
          publicationSize: 0, publicationWriteIndex: 0, movementStatusSize: 0, movementStatusWriteIndex: 0,
          outstandingRequestCount: 0, droppedRequestCount: 0 };
        const requests = new Map<number, { type?: string; targetMs?: number }>();
        const requestCapacity = 4_096;
        entries.push(entry);
        worker.addEventListener('message', (event: MessageEvent<unknown>) => {
          entry.messageCount = (entry.messageCount ?? 0) + 1;
          if (!entry.url.includes('dualClock.worker')) return;
          const data = event.data as { id?: number; type?: string; generation?: number; operationGeneration?: number; committedRevision?: number; busy?: boolean;
            publication?: { clock?: { macroSecond?: number; microSecond?: number; at?: string };
              guests?: { id?: string; status?: string; nodeId?: string; edgeId?: string | null; started?: number; due?: number; runs?: number; nextPlan?: string }[];
              selected?: { id?: string } | null;
              flow?: unknown };
            movement?: { count?: number; capacity?: number; buffer?: ArrayBuffer } };
          if (typeof data.id === 'number' && typeof data.type === 'string' && typeof data.generation === 'number'
            && typeof data.operationGeneration === 'number' && typeof data.committedRevision === 'number') {
            const request = requests.get(data.id);
            const publication = { id: data.id, type: data.type, generation: data.generation,
              operationGeneration: data.operationGeneration, committedRevision: data.committedRevision,
              macroSecond: data.publication?.clock?.macroSecond, microSecond: data.publication?.clock?.microSecond,
              at: data.publication?.clock?.at, requestType: request?.type,
              requestedTargetMs: request?.targetMs, busy: data.busy,
              movementCount: data.publication ? data.movement?.count ?? 0 : undefined,
              selectedId: data.publication?.selected?.id ?? null,
              representatives: data.publication?.guests?.map(({ id, status, nodeId, edgeId, started, due, runs, nextPlan }) =>
                ({ id, status, nodeId, edgeId, started, due, runs, nextPlan })), flow: data.publication?.flow };
            const index = entry.publicationWriteIndex!;
            if (entry.publications!.length < 2_048) entry.publications!.push(publication);
            else entry.publications![index] = publication;
            entry.publicationSize = Math.min(2_048, entry.publicationSize! + 1);
            entry.publicationWriteIndex = (index + 1) % 2_048;
            // A busy response is an intermediate publication for the same
            // request. Every other response is terminal, including weather
            // acknowledgements and errors, so its correlation can be retired.
          }
          if (typeof data.id === 'number' && data.busy !== true) requests.delete(data.id);
          entry.outstandingRequestCount = requests.size;
          const frame = data.movement;
          if (!frame?.buffer || !Number.isInteger(frame.count) || !Number.isInteger(frame.capacity)
            || frame.count! > frame.capacity! || frame.buffer.byteLength !== frame.capacity! * 33) return;
          const statuses = new Uint8Array(frame.buffer, frame.capacity! * 32, frame.capacity!);
          // Keep the original wire order intact; trail-queue was appended by
          // the dual-clock movement transport for schema-17 guests.
          const names = ['walking', 'lift-queue', 'lift-ride', 'skiing', 'resting', 'departed', 'trail-queue'];
          const counts: Record<string, number> = {};
          for (let index = 0; index < frame.count!; index++) {
            const name = names[statuses[index]!] ?? 'unknown'; counts[name] = (counts[name] ?? 0) + 1;
          }
          const statusIndex = entry.movementStatusWriteIndex!;
          if (entry.movementStatuses!.length < 2_048) entry.movementStatuses!.push(counts);
          else entry.movementStatuses![statusIndex] = counts;
          entry.movementStatusSize = Math.min(2_048, entry.movementStatusSize! + 1);
          entry.movementStatusWriteIndex = (statusIndex + 1) % 2_048;
        });
        const nativePostMessage = worker.postMessage.bind(worker) as (...args: unknown[]) => void;
        Object.defineProperty(worker, 'postMessage', { configurable: true, value: (...args: unknown[]) => {
          const request = args[0] as { requestId?: number; type?: string; targetMs?: number } | undefined;
          if (typeof request?.requestId === 'number') {
            if (!requests.has(request.requestId) && requests.size === requestCapacity) {
              const oldest = requests.keys().next().value;
              if (oldest !== undefined) requests.delete(oldest);
              entry.droppedRequestCount = (entry.droppedRequestCount ?? 0) + 1;
            }
            requests.set(request.requestId,
              { type: request.type, targetMs: Number.isFinite(request.targetMs) ? request.targetMs : undefined });
            entry.outstandingRequestCount = requests.size;
          }
          if (failPostFor && entry.url.includes(failPostFor)) {
            throw new DOMException(`Injected ${failPostFor} post failure`);
          }
          nativePostMessage(...args);
        } });
        const terminate = worker.terminate.bind(worker);
        worker.terminate = () => {
          entry.terminationCount += 1;
          requests.clear();
          entry.outstandingRequestCount = 0;
          terminate();
        };
        return worker;
      },
    });
    Object.defineProperty(window, 'Worker', {
      configurable: true,
      writable: true,
      value: ProbedWorker,
    });
    (window as unknown as { appWorkerProbe: WorkerProbeEntry[] }).appWorkerProbe = entries;
  }, options);
}

export const workerEntries = (page: Page, name: string): Promise<WorkerProbeEntry[]> =>
  page.evaluate((workerName) => {
    const entries = (window as unknown as { appWorkerProbe: WorkerProbeEntry[] }).appWorkerProbe;
    const ordered = <T,>(values: T[], size = values.length, writeIndex = 0): T[] => size < values.length
      ? values.slice(0, size) : [...values.slice(writeIndex), ...values.slice(0, writeIndex)];
    return entries.filter((entry) => entry.url.includes(workerName)).map(entry => ({ ...entry,
      publications: ordered(entry.publications ?? [], entry.publicationSize, entry.publicationWriteIndex),
      movementStatuses: ordered(entry.movementStatuses ?? [], entry.movementStatusSize, entry.movementStatusWriteIndex),
    }));
  }, name);
