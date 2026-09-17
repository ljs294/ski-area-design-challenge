import { describe, expect, it, vi } from 'vitest';
import type { SavedLift } from '../../types/lifts';
import type { TerrainRecord } from '../../types/terrain';
import { DesignSession, type DesignSessionPorts } from './designSession';

function terrain(key = 'terrain'): TerrainRecord {
  return { schemaVersion: 6, key, mountainName: 'Test', latitude: 46, longitude: -121,
    areaSizeMeters: 4000, sampleGridSize: 2, sampleHeights: [0, 1, 2, 3],
    climate: { monthly: [] }, sourceType: 'live', createdAt: '2026-01-01', updatedAt: '2026-01-01' };
}

function lift(id = 'lift'): SavedLift {
  return { id, identifier: 'A', name: id, liftTypeId: 'fixed-grip-quad',
    points: [[-121.01, 46], [-121, 46.01]], endpointElevM: [100, 200],
    lengthM: 1000, verticalM: 100, status: 'complete', createdAt: '2026-01-01' };
}

function ports(overrides: Partial<DesignSessionPorts> = {}): DesignSessionPorts {
  return { terrain: { cacheDisplayAssets: vi.fn(), activateProtocols: vi.fn(),
    publishState: vi.fn(), refreshSources: vi.fn(), publishPersisted: vi.fn(),
    publishConstruction: vi.fn() }, ...overrides };
}

function session(sessionPorts = ports()) {
  return new DesignSession({ identity: { id: 'design-1', terrainKey: 'terrain' },
    terrain: terrain(), topology: { trails: [], nodes: [], paths: [], junctions: [] },
    lifts: [], ports: sessionPorts });
}

describe('DesignSession', () => {
  it('owns committed models and exposes a renderer-independent persistence snapshot', () => {
    const source = lift();
    const design = new DesignSession({ identity: { id: 'stable', terrainKey: null },
      terrain: terrain(), topology: { trails: [], nodes: [], paths: [], junctions: [] },
      lifts: [source], ports: ports() });
    source.name = 'caller mutation';

    const snapshot = design.persistenceSnapshot();

    expect(snapshot.identity).toEqual({ id: 'stable', terrainKey: 'terrain' });
    expect(snapshot.terrain?.key).toBe('terrain');
    expect(snapshot.lifts[0]?.name).toBe('lift');
    expect(Object.isFrozen(snapshot.lifts)).toBe(true);
    expect(design.snapshot().capabilities).toMatchObject({ simulation: false, weather: false });
    expect(design.snapshot().network.edges.some((edge) => edge.kind === 'lift')).toBe(true);
    expect(Object.keys(design.read)).toEqual(['snapshot', 'persistenceSnapshot', 'publicationFailures']);
    expect(Object.keys(design.commands)).toEqual([
      'beginTopology', 'commitDocuments', 'commitLifts', 'addLift', 'patchLift', 'removeLift',
    ]);
  });

  it('revision-checks lift commands and rebuilds the derived network', () => {
    const publishLifts = vi.fn();
    const design = session(ports({ publishLifts }));

    expect(design.addLift(lift('first'))).toEqual({ ok: true, revision: 1, changed: true });
    expect(design.commitLifts(0, [lift('stale')])).toEqual({ ok: false, reason: 'stale' });
    expect(design.snapshot().lifts.map((entry) => entry.id)).toEqual(['first']);
    expect(design.snapshot().network.edges.some((edge) => edge.id === 'l:first')).toBe(true);
    expect(publishLifts).toHaveBeenCalledTimes(1);
  });

  it('keeps accepted topology authoritative when projection and failure reporting throw', () => {
    const design = session(ports({
      publishTopology: () => { throw new Error('renderer unavailable'); },
      publishFailure: () => { throw new Error('status unavailable'); },
    }));
    const edit = design.topology.begin();
    edit.addNode({ id: 'node', name: 'Node', point: [-121, 46], elevM: 100, createdAt: '2026-01-01' });

    expect(() => edit.commit()).not.toThrow();
    expect(design.snapshot().topology.nodes.map((node) => node.id)).toEqual(['node']);
    expect(design.publicationFailures()).toMatchObject([
      { source: 'session', target: 'publishTopology', message: 'renderer unavailable' },
    ]);
  });

  it('captures one coherent terrain/topology pair after an atomic construction commit', () => {
    const design = session();
    const edit = design.topology.begin();
    edit.addNode({ id: 'node', name: 'Node', point: [-121, 46], elevM: 100, createdAt: '2026-01-01' });

    const result = design.commitDocuments(edit, {
      expectedRevision: design.terrain.revision, record: terrain('graded'), kind: 'elevation',
    });
    const snapshot = design.persistenceSnapshot();

    expect(result.ok).toBe(true);
    expect(snapshot.terrain?.key).toBe('graded');
    expect(snapshot.nodes.map((node) => node.id)).toEqual(['node']);
    expect(snapshot.terrainRevision).toBe(2);
    expect(snapshot.topologyRevision).toBe(1);
  });
});
