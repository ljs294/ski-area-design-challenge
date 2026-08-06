import { describe, expect, it, vi } from 'vitest';
import { commitDocuments } from './committedDocumentTransaction';
import { TerrainDocument, type TerrainDocumentPorts } from './terrainDocument';
import { TopologyDocument, type TopologyState } from './topologyDocument';
import type { TerrainRecord } from '../types/terrain';
import type { SavedNode } from '../types/topology';

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

function emptyTopology(): TopologyState {
  return { trails: [], nodes: [], paths: [], junctions: [] };
}

function node(id: string): SavedNode {
  return {
    id,
    name: id,
    point: [-121.5, 46.93],
    elevM: 250,
    createdAt: '2026-01-01T00:00:00.000Z',
  };
}

function documents() {
  let topology!: TopologyDocument;
  const observations: string[] = [];
  const terrainObserver = vi.fn(() => {
    if (topology) observations.push(
      `terrain:${topology.snapshot().nodes.map((entry) => entry.id).join(',')}`,
    );
  });
  const ports: TerrainDocumentPorts = {
    cacheDisplayAssets: vi.fn(),
    activateProtocols: vi.fn(),
    publishState: terrainObserver,
    refreshSources: vi.fn(),
    publishPersisted: vi.fn(),
    publishConstruction: vi.fn(),
  };
  const terrain = new TerrainDocument(ports);
  terrain.replace(record('base'));
  terrainObserver.mockClear();
  const topologyObserver = vi.fn(() => {
    observations.push(`topology:${terrain.snapshot().record?.key ?? 'none'}`);
  });
  topology = new TopologyDocument(emptyTopology(), topologyObserver);
  return { terrain, topology, terrainObserver, topologyObserver, observations };
}

describe('committed terrain/topology transaction', () => {
  it('moves both authoritative snapshots before either observer runs', () => {
    const { terrain, topology, terrainObserver, topologyObserver, observations } = documents();
    const edit = topology.begin();
    edit.addNode(node('new-node'));

    const result = commitDocuments({
      terrain,
      topology: edit,
      terrainCommit: { expectedRevision: 1, record: record('graded'), kind: 'elevation' },
    });

    expect(result).toEqual({
      ok: true,
      terrainRevision: 2,
      topologyRevision: 1,
      topologyChanged: true,
    });
    expect(terrainObserver).toHaveBeenCalledTimes(1);
    expect(topologyObserver).toHaveBeenCalledTimes(1);
    expect(observations).toEqual(['terrain:new-node', 'topology:graded']);
  });

  it('rejects stale terrain without landing topology', () => {
    const { terrain, topology } = documents();
    terrain.commit({ expectedRevision: 1, record: record('winner'), kind: 'cover' });
    const before = topology.snapshot();
    const edit = topology.begin();
    edit.addNode(node('new-node'));

    const result = commitDocuments({
      terrain,
      topology: edit,
      terrainCommit: { expectedRevision: 1, record: record('stale-grade'), kind: 'elevation' },
    });

    expect(result).toEqual({ ok: false, reason: 'terrain-stale' });
    expect(topology.snapshot()).toBe(before);
    expect(edit.commit()).toEqual({ ok: false, reason: 'settled' });
  });

  it('rejects stale topology without landing terrain', () => {
    const { terrain, topology } = documents();
    const stale = topology.begin();
    stale.addNode(node('new-node'));
    const winner = topology.begin();
    winner.addNode(node('winner'));
    winner.commit();
    const before = terrain.snapshot();

    const result = commitDocuments({
      terrain,
      topology: stale,
      terrainCommit: { expectedRevision: before.revision, record: record('graded'), kind: 'elevation' },
    });

    expect(result).toEqual({ ok: false, reason: 'topology-stale' });
    expect(terrain.snapshot()).toBe(before);
    expect(terrain.snapshot().record?.key).toBe('base');
  });

  it('commits topology alone when no terrain edit is required', () => {
    const { terrain, topology } = documents();
    const edit = topology.begin();
    edit.addNode(node('new-node'));

    const result = commitDocuments({ terrain, topology: edit });

    expect(result).toEqual({
      ok: true,
      terrainRevision: 1,
      topologyRevision: 1,
      topologyChanged: true,
    });
    expect(terrain.snapshot().record?.key).toBe('base');
  });

  it('rejects an already settled topology transaction without moving terrain', () => {
    const { terrain, topology } = documents();
    const edit = topology.begin();
    edit.abort();

    const result = commitDocuments({
      terrain,
      topology: edit,
      terrainCommit: { expectedRevision: 1, record: record('graded'), kind: 'elevation' },
    });

    expect(result).toEqual({ ok: false, reason: 'topology-settled' });
    expect(terrain.snapshot().record?.key).toBe('base');
  });
});
