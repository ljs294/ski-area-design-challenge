import { describe, expect, it, vi } from 'vitest';
import type { TerrainRecord } from '../../types/terrain';
import { DesignSession } from './designSession';
import { designSaveDraft } from './designSaveSnapshot';

function terrain(): TerrainRecord {
  return { schemaVersion: 4, key: 'terrain', mountainName: 'Test', latitude: 46, longitude: -121,
    areaSizeMeters: 1000, sampleGridSize: 2, sampleHeights: [1, 2, 3, 4], climate: { monthly: [] },
    sourceType: 'live', createdAt: '2026-01-01', updatedAt: '2026-01-01' };
}

describe('designSaveDraft', () => {
  it('captures only committed editor state and revisions', () => {
    const session = new DesignSession({ identity: { id: 'design', terrainKey: null }, terrain: terrain(),
      topology: { trails: [], nodes: [], paths: [], junctions: [] }, lifts: [],
      ports: { terrain: { cacheDisplayAssets: vi.fn(), activateProtocols: vi.fn(), publishState: vi.fn(),
        refreshSources: vi.fn(), publishPersisted: vi.fn(), publishConstruction: vi.fn() } } });

    const draft = designSaveDraft(session.read.persistenceSnapshot(), {
      name: 'Design', createdAt: '2026-01-01', updatedAt: '2026-01-02', site: null,
      camera: { center: [-121, 46], zoom: 12, bearing: 0, pitch: 0, is3D: false },
    });

    expect(draft).toMatchObject({ format: 'mountain-planner-three-design', schemaVersion: 1,
      key: 'design', terrainRecord: { key: 'terrain' }, revisions: { terrain: 1 },
      lifts: [], trails: [], nodes: [], paths: [], junctions: [] });
    expect(draft).not.toHaveProperty('weatherRun');
    expect(draft).not.toHaveProperty('time');
    expect(draft).not.toHaveProperty('simulation');
  });
});
