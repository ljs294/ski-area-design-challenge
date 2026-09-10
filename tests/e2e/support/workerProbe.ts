import type { Page } from '@playwright/test';

export interface WorkerProbeEntry {
  url: string;
  terminationCount: number;
  /** Bounded, decoded status counts from real dual-clock movement frames. */
  movementStatuses?: Record<string, number>[];
  /** Bounded control/publication identities for correlating a movement frame with worker state. */
  publications?: { id: number; type: string; generation: number; operationGeneration: number; committedRevision: number;
    macroSecond?: number; microSecond?: number; at?: string;
    representatives?: { id?: string; status?: string; nodeId?: string; edgeId?: string | null; started?: number; due?: number; runs?: number; nextPlan?: string }[];
    flow?: unknown }[];
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
        const entry: WorkerProbeEntry = { url: String(argumentsList[0]), terminationCount: 0, movementStatuses: [], publications: [] };
        entries.push(entry);
        worker.addEventListener('message', (event: MessageEvent<unknown>) => {
          if (!entry.url.includes('dualClock.worker')) return;
          const data = event.data as { id?: number; type?: string; generation?: number; operationGeneration?: number; committedRevision?: number;
            publication?: { clock?: { macroSecond?: number; microSecond?: number; at?: string };
              guests?: { id?: string; status?: string; nodeId?: string; edgeId?: string | null; started?: number; due?: number; runs?: number; nextPlan?: string }[];
              flow?: unknown };
            movement?: { count?: number; capacity?: number; buffer?: ArrayBuffer } };
          if (typeof data.id === 'number' && typeof data.type === 'string' && typeof data.generation === 'number'
            && typeof data.operationGeneration === 'number' && typeof data.committedRevision === 'number') {
            entry.publications!.push({ id: data.id, type: data.type, generation: data.generation,
              operationGeneration: data.operationGeneration, committedRevision: data.committedRevision,
              macroSecond: data.publication?.clock?.macroSecond, microSecond: data.publication?.clock?.microSecond,
              at: data.publication?.clock?.at,
              representatives: data.publication?.guests?.map(({ id, status, nodeId, edgeId, started, due, runs, nextPlan }) =>
                ({ id, status, nodeId, edgeId, started, due, runs, nextPlan })), flow: data.publication?.flow });
            if (entry.publications!.length > 240) entry.publications!.shift();
          }
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
          entry.movementStatuses!.push(counts);
          if (entry.movementStatuses!.length > 240) entry.movementStatuses!.shift();
        });
        if (failPostFor && entry.url.includes(failPostFor)) {
          Object.defineProperty(worker, 'postMessage', {
            configurable: true,
            value: () => { throw new DOMException(`Injected ${failPostFor} post failure`); },
          });
        }
        const terminate = worker.terminate.bind(worker);
        worker.terminate = () => {
          entry.terminationCount += 1;
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
    return entries.filter((entry) => entry.url.includes(workerName));
  }, name);
