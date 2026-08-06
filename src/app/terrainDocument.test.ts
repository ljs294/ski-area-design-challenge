import { describe, expect, it, vi } from 'vitest';
import { PreviewOwnership, TerrainDocument, type TerrainDocumentPorts,
  type TerrainPublication } from './terrainDocument';
import type { TerrainRecord } from '../types/terrain';

function record(key: string): TerrainRecord {
  return {
    schemaVersion: 5,
    key,
    mountainName: 'Test Mountain',
    latitude: 46.928,
    longitude: -121.474,
    areaSizeMeters: 4000,
    sampleGridSize: 2,
    sampleHeights: [0, 1, 2, 3],
    climate: { monthly: [] },
    sourceType: 'live',
    createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
  };
}

function ports() {
  const publications: TerrainPublication[] = [];
  const calls: string[] = [];
  const spies: TerrainDocumentPorts = {
    cacheDisplayAssets: vi.fn(() => { calls.push('cache'); }),
    activateProtocols: vi.fn(() => { calls.push('protocols'); }),
    publishState: vi.fn((publication: TerrainPublication) => {
      calls.push('state');
      publications.push(publication);
    }),
    refreshSources: vi.fn(() => { calls.push('sources'); }),
    publishPersisted: vi.fn(() => { calls.push('persisted'); }),
    publishConstruction: vi.fn(() => { calls.push('construction'); }),
  };
  return { spies, calls, publications };
}

describe('TerrainDocument revisions', () => {
  it('starts with no record at revision zero', () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);

    expect(document.snapshot()).toEqual({ record: null, revision: 0 });
    expect(spies.publishState).not.toHaveBeenCalled();
  });

  it('increments the revision exactly once per successful commit', () => {
    const { spies, publications } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));

    const first = document.commit({ expectedRevision: 1, record: record('graded'), kind: 'elevation' });
    const second = document.commit({ expectedRevision: 2, record: record('cleared'), kind: 'cover' });

    expect(first).toEqual({ ok: true, revision: 2 });
    expect(second).toEqual({ ok: true, revision: 3 });
    expect(document.snapshot().revision).toBe(3);
    expect(document.snapshot().record?.key).toBe('cleared');
    expect(publications.map((publication) => publication.edit)).toEqual([null, 'elevation', 'cover']);
  });

  it('publishes caches, protocols, React state, and map sources in one coherent order', () => {
    const { spies, calls } = ports();
    const document = new TerrainDocument(spies);

    document.replace(record('base'));

    expect(calls).toEqual(['cache', 'protocols', 'state', 'sources']);
  });

  it('lands a load or package replacement clean rather than as an edit', () => {
    const { spies, publications } = ports();
    const document = new TerrainDocument(spies);

    document.replace(record('prepared'));

    expect(publications).toHaveLength(1);
    expect(publications[0].edit).toBeNull();
    expect(publications[0].revision).toBe(1);
  });

  it('takes ownership of the record shell and exposes a frozen snapshot', () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    const input = record('owned');

    const snapshot = document.replace(input);
    input.key = 'changed-by-caller';

    expect(snapshot.record).not.toBe(input);
    expect(Object.isFrozen(snapshot.record)).toBe(true);
    expect(document.snapshot().record?.key).toBe('owned');
    expect(() => {
      (snapshot.record as TerrainRecord).key = 'changed-through-snapshot';
    }).toThrow();
  });

  it('rejects a stale commit without touching any published state', () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    document.commit({ expectedRevision: 1, record: record('graded'), kind: 'elevation' });
    const before = document.snapshot();
    vi.clearAllMocks();

    const result = document.commit({ expectedRevision: 1, record: record('stale'), kind: 'cover' });

    expect(result).toEqual({ ok: false, reason: 'stale' });
    expect(document.snapshot()).toBe(before);
    expect(document.snapshot().record?.key).toBe('graded');
    expect(spies.cacheDisplayAssets).not.toHaveBeenCalled();
    expect(spies.activateProtocols).not.toHaveBeenCalled();
    expect(spies.publishState).not.toHaveBeenCalled();
    expect(spies.refreshSources).not.toHaveBeenCalled();
  });

  it('clears the dirty flag only when nothing landed while the write was in flight', () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    const { revision } = document.snapshot();

    document.commit({ expectedRevision: revision, record: record('graded'), kind: 'elevation' });

    expect(document.markPersisted(revision)).toBe(false);
    expect(spies.publishPersisted).not.toHaveBeenCalled();
    expect(document.markPersisted(document.snapshot().revision)).toBe(true);
    expect(spies.publishPersisted).toHaveBeenCalledTimes(1);
  });
});

describe('TerrainDocument construction ownership', () => {
  it('acquires ownership synchronously and rejects a same-tick double confirmation', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    let ownedAtEntry: string | null = null;

    const first = document.runConstruction('road', async () => {
      ownedAtEntry = document.constructionActivity;
      await Promise.resolve();
      return 'built';
    });
    const second = document.runConstruction('road', () => Promise.resolve('built twice'));

    expect(ownedAtEntry).toBe('road');
    expect(await second).toEqual({ ok: false, reason: 'busy' });
    expect(await first).toEqual({ ok: true, value: 'built' });
    expect(document.constructionActivity).toBeNull();
    expect(spies.publishConstruction).toHaveBeenNthCalledWith(1, 'road');
    expect(spies.publishConstruction).toHaveBeenNthCalledWith(2, null);
  });

  it('releases ownership after a rejected operation', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);

    await expect(document.runConstruction('dam', () => Promise.reject(new Error('no grade'))))
      .rejects.toThrow('no grade');

    expect(document.constructionActivity).toBeNull();
    expect(spies.publishConstruction).toHaveBeenLastCalledWith(null);
  });
});

describe('TerrainDocument cover edits', () => {
  it('serializes cover edits rather than running them concurrently', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    const order: string[] = [];
    let releaseFirst!: () => void;
    const firstStarted = new Promise<void>((resolve) => {
      void document.runCoverEdit(async () => {
        order.push('first:start');
        resolve();
        await new Promise<void>((done) => { releaseFirst = done; });
        order.push('first:end');
      });
    });

    const second = document.runCoverEdit(async () => {
      order.push('second:start');
      await Promise.resolve();
    });

    await firstStarted;
    expect(order).toEqual(['first:start']);
    releaseFirst();
    await second;

    expect(order).toEqual(['first:start', 'first:end', 'second:start']);
  });

  it('hands each cover edit the snapshot current when it starts, not when it queued', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    let releaseFirst!: () => void;
    const firstStarted = new Promise<void>((resolve) => {
      void document.runCoverEdit(async () => {
        resolve();
        await new Promise<void>((done) => { releaseFirst = done; });
      });
    });
    let observed: string | undefined;
    const second = document.runCoverEdit(async (snapshot) => {
      observed = snapshot.record?.key;
      await Promise.resolve();
    });

    await firstStarted;
    document.commit({ expectedRevision: 1, record: record('graded'), kind: 'elevation' });
    releaseFirst();
    await second;

    expect(observed).toBe('graded');
  });

  it('rejects an older cover result rather than publishing it over a newer revision', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    const captured = document.snapshot();

    document.commit({ expectedRevision: captured.revision, record: record('graded'), kind: 'elevation' });
    const late = document.commit({
      expectedRevision: captured.revision,
      record: record('cover-from-stale-base'),
      kind: 'cover',
    });

    expect(late).toEqual({ ok: false, reason: 'stale' });
    expect(document.snapshot().record?.key).toBe('graded');
  });

  it('skips cover work still queued when the session is disposed', async () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    document.replace(record('base'));
    let releaseFirst!: () => void;
    const firstStarted = new Promise<void>((resolve) => {
      void document.runCoverEdit(async () => {
        resolve();
        await new Promise<void>((done) => { releaseFirst = done; });
      });
    });
    const ran = vi.fn(() => Promise.resolve());
    const second = document.runCoverEdit(ran);

    await firstStarted;
    document.dispose();
    releaseFirst();
    await second;

    expect(ran).not.toHaveBeenCalled();
  });
});

describe('PreviewOwnership', () => {
  it('supersedes the outstanding claim on every new claim', () => {
    const preview = new PreviewOwnership();

    const first = preview.claim();
    const second = preview.claim();

    expect(preview.isCurrent(first)).toBe(false);
    expect(preview.isCurrent(second)).toBe(true);
    expect(preview.current).toBe(second);
  });

  it('invalidates the outstanding claim when a tool cancels', () => {
    const preview = new PreviewOwnership();
    const token = preview.claim();

    preview.invalidate();

    expect(preview.isCurrent(token)).toBe(false);
  });

  it('invalidates outstanding preview work on document disposal', () => {
    const { spies } = ports();
    const document = new TerrainDocument(spies);
    const token = document.preview.claim();

    document.dispose();

    expect(document.preview.isCurrent(token)).toBe(false);
  });
});
