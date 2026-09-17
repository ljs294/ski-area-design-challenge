import { afterEach, describe, expect, it, vi } from 'vitest';
import fs from 'fs';
import os from 'os';
import path from 'path';
import { FileDesignStorage } from './designFileStorage';
import type { DesignSaveDocument, DesignTerrainGeneration } from '../src/types/designSave';

const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

function root(): string {
  const value = fs.mkdtempSync(path.join(os.tmpdir(), 'mountain-planner-design-'));
  roots.push(value);
  return value;
}

function generation(): DesignTerrainGeneration {
  return { format: 'mountain-planner-three-terrain-generation', schemaVersion: 1,
    generationId: 'terrain-generation', logicalKey: 'terrain', sourceRevision: 1, createdAt: '2026-01-01',
    record: { schemaVersion: 4, key: 'terrain', mountainName: 'Test', latitude: 46, longitude: -121,
      areaSizeMeters: 1000, sampleGridSize: 2,
      sampleHeights: new Float32Array([1, 2, 3, 4]), localImagery: new Uint8Array([4, 5, 6]),
      climate: { monthly: [] }, sourceType: 'live', createdAt: '2026-01-01', updatedAt: '2026-01-01' } };
}

function manifest(id: string, name: string): DesignSaveDocument {
  return { format: 'mountain-planner-three-design', schemaVersion: 1, manifestId: id,
    key: 'save', name, createdAt: '2026-01-01', updatedAt: '2026-01-01', site: null,
    camera: { center: [-121, 46], zoom: 12, bearing: 0, pitch: 0, is3D: false },
    terrain: { logicalKey: 'terrain', generationId: 'terrain-generation', sourceRevision: 1,
      updatedAt: '2026-01-01' },
    revisions: { design: id === 'first' ? 1 : 2, terrain: 1, topology: 0, lifts: 0 },
    lifts: [], trails: [], nodes: [], paths: [], junctions: [] };
}

describe('FileDesignStorage', () => {
  it('round-trips typed terrain assets and reconstructs a missing or stale index', async () => {
    const storage = new FileDesignStorage(root());
    await storage.writeTerrainGeneration(generation());
    await storage.publishManifestAndHead(manifest('first', 'First'));
    await storage.writeSummary({ key: 'save', name: 'First', createdAt: '2026-01-01',
      updatedAt: '2026-01-01', revisions: manifest('first', 'First').revisions,
      terrain: manifest('first', 'First').terrain });
    await storage.publishManifestAndHead(manifest('second', 'Second'));

    const loaded = await storage.readTerrainGeneration('terrain-generation');

    expect(loaded?.record.sampleHeights).toBeInstanceOf(Float32Array);
    expect(loaded?.record.localImagery).toBeInstanceOf(Uint8Array);
    expect(await storage.listSummaries()).toMatchObject([{ key: 'save', name: 'Second' }]);
  });

  it('falls back to the previous complete head when the current head is unreadable', async () => {
    const directory = root();
    const storage = new FileDesignStorage(directory);
    await storage.writeTerrainGeneration(generation());
    await storage.publishManifestAndHead(manifest('first', 'First'));
    await storage.publishManifestAndHead(manifest('second', 'Second'));
    const head = fs.readdirSync(path.join(directory, 'heads')).find((entry) => entry.endsWith('.head.json'));
    expect(head).toBeDefined();
    fs.writeFileSync(path.join(directory, 'heads', head!), '{interrupted', 'utf8');

    expect(await storage.readCurrentManifest('save')).toMatchObject({ manifestId: 'first', name: 'First' });
  });

  it('restores the prior head when atomic replacement is interrupted', async () => {
    const directory = root();
    const storage = new FileDesignStorage(directory);
    await storage.writeTerrainGeneration(generation());
    await storage.publishManifestAndHead(manifest('first', 'First'));
    const rename = fs.renameSync.bind(fs);
    let injected = false;
    const spy = vi.spyOn(fs, 'renameSync').mockImplementation((from, to) => {
      if (!injected && String(from).endsWith('.tmp') && String(to).endsWith('.head.json')) {
        injected = true;
        throw new Error('injected head replacement interruption');
      }
      return rename(from, to);
    });

    await expect(storage.publishManifestAndHead(manifest('second', 'Second')))
      .rejects.toThrow('injected head replacement interruption');
    spy.mockRestore();

    expect(injected).toBe(true);
    expect(await storage.readCurrentManifest('save')).toMatchObject({ manifestId: 'first', name: 'First' });
  });
});
