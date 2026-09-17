import { describe, expect, it, vi } from 'vitest';
import type { TerrainPublication } from '../../src/app/terrainDocument';
import type { TerrainRecord } from '../../src/types/terrain';
import { TerrainPresentationCoordinator } from './terrainPresentation';

const record = { sampleGridSize: 3 } as TerrainRecord;
function publication(revision: number, samples?: number[], edit: 'elevation' | 'cover' = 'elevation'):
TerrainPublication {
  return { record, revision, edit, ...(samples ? { changedSampleIndices: samples } : {}) };
}
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => { resolve = done; });
  return { promise, resolve };
}

describe('TerrainPresentationCoordinator', () => {
  it('unions accepted edits when newer preparation supersedes an older job', async () => {
    const first = deferred<string>(), prepared: Array<readonly number[] | undefined> = [];
    const scheduled: Array<() => void> = [], activated: string[] = [];
    let calls = 0;
    const coordinator = new TerrainPresentationCoordinator(publication(1), {
      prepare: (_publication, samples) => {
        prepared.push(samples);
        return calls++ === 0 ? first.promise : 'latest';
      },
      activate: (value) => activated.push(value.stage),
      schedule: (task) => scheduled.push(task),
    });

    coordinator.enqueue(publication(2, [1, 2])); scheduled.shift()?.();
    coordinator.enqueue(publication(3, [7]));
    first.resolve('stale'); await Promise.resolve(); await Promise.resolve();
    scheduled.shift()?.(); await Promise.resolve(); await Promise.resolve();
    coordinator.activateAtFrameBoundary();

    expect(prepared).toEqual([[1, 2], [1, 2, 7]]);
    expect(activated).toEqual(['latest']);
    expect(coordinator.status).toMatchObject({ state: 'ready', presentedRevision: 3 });
  });

  it('retains a pending grade when a following cover edit requires a full refresh', async () => {
    const scheduled: Array<() => void> = [], prepared: Array<readonly number[] | undefined> = [];
    const coordinator = new TerrainPresentationCoordinator(publication(1), {
      prepare: (_publication, samples) => { prepared.push(samples); return 'stage'; },
      activate: () => {}, schedule: (task) => scheduled.push(task),
    });
    coordinator.enqueue(publication(2, [2]));
    coordinator.enqueue(publication(3, undefined, 'cover'));
    scheduled.shift()?.(); await Promise.resolve(); await Promise.resolve();
    coordinator.activateAtFrameBoundary();
    expect(prepared).toEqual([undefined]);
    expect(coordinator.status.presentedRevision).toBe(3);
  });

  it('does not advance presentation revision when frame activation fails', async () => {
    const scheduled: Array<() => void> = [], activate = vi.fn(() => { throw new Error('GPU lost'); });
    const coordinator = new TerrainPresentationCoordinator(publication(1), {
      prepare: () => 'stage', activate, schedule: (task) => scheduled.push(task),
    });
    coordinator.enqueue(publication(2, [4])); scheduled.shift()?.();
    await Promise.resolve(); await Promise.resolve();

    expect(coordinator.activateAtFrameBoundary()).toBe(false);
    expect(coordinator.status).toMatchObject({ state: 'failed', committedRevision: 2,
      presentedRevision: 1, message: 'GPU lost' });
  });
});
