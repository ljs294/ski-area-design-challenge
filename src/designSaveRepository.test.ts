import { describe, expect, it } from 'vitest';
import { DesignSaveRepository, type DesignStorageAdapter } from './designSaveRepository';
import { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf, manifestOf } from './terrainPackage';
import { DESIGN_SAVE_FORMAT, DESIGN_SAVE_SCHEMA_VERSION,
  type DesignSaveDocument, type DesignSaveDraft, type DesignSaveSummary,
  type DesignTerrainGeneration } from './types/designSave';
import type { SiteCoverGrid } from './types/cover';
import type { TerrainRecord } from './types/terrain';

function terrain(): TerrainRecord {
  const coverGrid: SiteCoverGrid = { bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
    width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200', vintage: '2021' };
  const coverBoundarySegments = [0, 0, 1, 0, 10];
  const contourSegments = [0, 0, 1, 1, 1500];
  let record: TerrainRecord = { schemaVersion: 4, key: 'mountain', mountainName: 'Mountain',
    latitude: 46.905, longitude: -121.495, areaSizeMeters: 2000, bounds: coverGrid.bounds,
    sampleGridSize: 2, sampleHeights: new Float32Array([1000, 1010, 1020, 1030]),
    coverGrid, coverMetadata: coverMetadataOf(coverGrid), coverBoundarySegments,
    coverGeometryMetadata: coverGeometryMetadataOf(coverBoundarySegments), contourSegments,
    contourMetadata: contourMetadataOf(contourSegments, 2, 6.096), climate: { monthly: [] },
    sourceType: 'live', createdAt: '2026-01-01', updatedAt: '2026-01-01' };
  record = { ...record, packageManifest: manifestOf(record) };
  return record;
}

function draft(revision = 0): DesignSaveDraft {
  return { format: DESIGN_SAVE_FORMAT, schemaVersion: DESIGN_SAVE_SCHEMA_VERSION,
    key: 'design', name: `Design ${revision}`, createdAt: '2026-01-01', updatedAt: `2026-01-0${revision + 1}`,
    site: null, camera: { center: [-121.495, 46.905], zoom: 14, bearing: 0, pitch: 45, is3D: true },
    revisions: { design: revision, terrain: 1, topology: revision, lifts: revision },
    terrainRecord: terrain(), lifts: [], trails: [], nodes: [], paths: [], junctions: [] };
}

class MemoryStorage implements DesignStorageAdapter {
  readonly generations = new Map<string, DesignTerrainGeneration>();
  readonly manifests = new Map<string, DesignSaveDocument>();
  readonly heads = new Map<string, string>();
  readonly summaries = new Map<string, DesignSaveSummary>();
  readonly publishedNames: string[] = [];
  failPublish = false;
  failSummary = false;
  beforePublish: ((manifest: DesignSaveDocument) => Promise<void>) | null = null;

  async readCurrentManifest(key: string) {
    const id = this.heads.get(key);
    return id ? structuredClone(this.manifests.get(id) ?? null) : null;
  }
  async readTerrainGeneration(id: string) { return structuredClone(this.generations.get(id) ?? null); }
  async writeTerrainGeneration(generation: DesignTerrainGeneration) {
    if (this.generations.has(generation.generationId)) throw new Error('duplicate generation');
    this.generations.set(generation.generationId, structuredClone(generation));
  }
  async publishManifestAndHead(manifest: DesignSaveDocument) {
    await this.beforePublish?.(manifest);
    if (this.failPublish) throw new Error('interrupted before head publication');
    this.publishedNames.push(manifest.name);
    this.manifests.set(manifest.manifestId, structuredClone(manifest));
    this.heads.set(manifest.key, manifest.manifestId);
  }
  async writeSummary(summary: DesignSaveSummary) {
    if (this.failSummary) throw new Error('index unavailable');
    this.summaries.set(summary.key, structuredClone(summary));
  }
  async listSummaries() {
    const result = new Map(this.summaries);
    for (const [key, id] of this.heads) {
      const save = this.manifests.get(id);
      if (save) result.set(key, { key, name: save.name,
        createdAt: save.createdAt, updatedAt: save.updatedAt,
        revisions: save.revisions, terrain: save.terrain });
    }
    return [...result.values()];
  }
}

describe('DesignSaveRepository', () => {
  it('round-trips an empty design in the isolated fork format', async () => {
    const storage = new MemoryStorage();
    const repository = new DesignSaveRepository(storage, { createId: () => 'one', now: () => '2026-01-01' });

    const saved = await repository.save(draft());
    const loaded = await repository.load('design');

    expect(saved).toMatchObject({ ok: true, receipt: { revisions: { design: 0 }, warnings: [] } });
    expect(loaded).toMatchObject({ ok: true, bundle: { save: {
      format: DESIGN_SAVE_FORMAT, schemaVersion: 1, lifts: [], trails: [], nodes: [], paths: [], junctions: [],
    }, terrain: { key: 'mountain' } } });
  });

  it('round-trips stable relationships and reuses an unchanged terrain generation', async () => {
    let id = 0;
    const storage = new MemoryStorage();
    const repository = new DesignSaveRepository(storage, { createId: () => `id-${++id}`, now: () => '2026-01-01' });
    const built = draft(1);
    built.nodes = [{ id: 'node-a', name: 'A', point: [-121.5, 46.9], elevM: 1000, createdAt: '2026-01-01' }];
    built.paths = [{ id: 'path-a', name: 'Connector', points: [[-121.5, 46.9], [-121.49, 46.91]],
      pointElevM: [1000, 1010], widthM: 4,
      from: { kind: 'node', nodeId: 'node-a', point: [-121.5, 46.9] },
      to: { kind: 'node', nodeId: 'node-b', point: [-121.49, 46.91] },
      lengthM: 100, status: 'complete', createdAt: '2026-01-01' }];
    built.nodes.push({ id: 'node-b', name: 'B', point: [-121.49, 46.91], elevM: 1010, createdAt: '2026-01-01' });

    const first = await repository.save(built);
    const second = await repository.save({ ...built, name: 'Renamed', updatedAt: '2026-01-03',
      revisions: { ...built.revisions, design: 2 } });
    const loaded = await repository.load('design');

    expect(first.ok && second.ok && first.receipt.terrainGenerationId === second.receipt.terrainGenerationId).toBe(true);
    expect(storage.generations.size).toBe(1);
    expect(loaded).toMatchObject({ ok: true, bundle: { save: { name: 'Renamed',
      paths: [{ from: { nodeId: 'node-a' }, to: { nodeId: 'node-b' } }] } } });
  });

  it('keeps the previous complete pair after interruption between terrain and head publication', async () => {
    let id = 0;
    const storage = new MemoryStorage();
    const repository = new DesignSaveRepository(storage, { createId: () => `id-${++id}`, now: () => '2026-01-01' });
    expect((await repository.save(draft())).ok).toBe(true);
    storage.failPublish = true;
    const next = draft(2);
    next.revisions.terrain = 2;
    next.terrainRecord.updatedAt = '2026-02-01';

    expect(await repository.save(next)).toEqual({ ok: false, error: 'interrupted before head publication' });
    expect(await repository.load('design')).toMatchObject({ ok: true, bundle: { save: { name: 'Design 0' } } });
    expect(storage.generations.size).toBe(2);
  });

  it('reports secondary index failure without reversing the authoritative save', async () => {
    const storage = new MemoryStorage();
    let id = 0;
    const repository = new DesignSaveRepository(storage, { createId: () => `id-${++id}`, now: () => '2026-01-01' });

    expect((await repository.save(draft())).ok).toBe(true);
    storage.failSummary = true;
    const next = draft(1);

    const result = await repository.save(next);

    expect(result).toMatchObject({ ok: true, receipt: { warnings: [expect.stringContaining('index')] } });
    expect(await repository.load('design')).toMatchObject({ ok: true, bundle: { save: { name: 'Design 1' } } });
    expect(await repository.list()).toMatchObject([{ name: 'Design 1' }]);
  });

  it('serializes saves per key and captures the requested revision before asynchronous work', async () => {
    const storage = new MemoryStorage();
    let releaseFirst!: () => void;
    const firstGate = new Promise<void>((resolve) => { releaseFirst = resolve; });
    let publication = 0;
    storage.beforePublish = async () => { if (publication++ === 0) await firstGate; };
    let id = 0;
    const repository = new DesignSaveRepository(storage, { createId: () => `id-${++id}`, now: () => '2026-01-01' });
    const first = repository.save(draft());
    const secondDraft = draft(1);
    const second = repository.save(secondDraft);
    secondDraft.name = 'Changed after save started';

    releaseFirst();
    expect((await first).ok).toBe(true);
    expect((await second).ok).toBe(true);
    expect(storage.publishedNames).toEqual(['Design 0', 'Design 1']);
    expect(await repository.load('design')).toMatchObject({ ok: true, bundle: { save: { name: 'Design 1' } } });
  });
});
