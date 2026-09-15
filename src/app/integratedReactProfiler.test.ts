import { afterEach, describe, expect, it } from 'vitest';
import { recordIntegratedReactProfile } from './integratedReactProfiler';

const key = '__MOUNTAIN_PLANNER_INTEGRATED_REACT_PROFILE__';

describe('integrated React profiling ring', () => {
  afterEach(() => { delete (globalThis as typeof globalThis & Record<string, unknown>)[key]; });

  it('retains a bounded ring with enough metadata for chronological collection', () => {
    for (let index = 0; index < 20_002; index++) {
      recordIntegratedReactProfile('App', 'update', index, index + 1, index + 2, index + 3);
    }
    const ring = (globalThis as typeof globalThis & Record<string, unknown>)[key] as {
      entries: { actualDuration: number }[]; maxEntries: number; size: number; writeIndex: number; dropped: number };
    expect(ring).toMatchObject({ maxEntries: 20_000, size: 20_000, writeIndex: 2, dropped: 2 });
    expect([...ring.entries.slice(ring.writeIndex), ...ring.entries.slice(0, ring.writeIndex)].map(entry => entry.actualDuration))
      .toEqual(Array.from({ length: 20_000 }, (_, index) => index + 2));
  });
});
