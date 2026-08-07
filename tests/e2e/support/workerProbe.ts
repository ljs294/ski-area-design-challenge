import type { Page } from '@playwright/test';

export interface WorkerProbeEntry {
  url: string;
  terminationCount: number;
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
        const entry = { url: String(argumentsList[0]), terminationCount: 0 };
        entries.push(entry);
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
