import { describe, expect, it } from 'vitest';
import {
  allocateSnowmakingNode,
  buildSnowmakingPipe,
  detachSnowmakingNode,
  hydrateSnowmakingNetwork,
  pruneAffectedJunctions,
  snowmakingNodeLabel,
  snowmakingPipeSpans,
  snowmakingPipeStats,
  type SnowmakingNetworkState,
} from './snowmakingNetwork';
import type { SavedSnowmakingNode, SavedSnowmakingPipe } from './types/snowmaking';

const A: [number, number] = [-121.5, 46.93];
const B: [number, number] = [-121.5, 46.931];
const C: [number, number] = [-121.5, 46.932];

function node(id: string, kind: SavedSnowmakingNode['kind'], labelNumber?: number): SavedSnowmakingNode {
  return { id, name: `${kind} name`, kind, labelNumber, point: A, elevM: 100,
    createdAt: '2026-01-01T00:00:00.000Z' };
}

function pipe(id = 'pipe', nodeId: string | null = null): SavedSnowmakingPipe {
  return buildSnowmakingPipe({ id, name: 'Pipe 1', diameterIn: 8, points: [A, B, C],
    nodeIds: [null, nodeId, null], createdAt: '2026-01-01T00:00:00.000Z' },
  (point) => 100 + (point[1] - A[1]) * 100000);
}

describe('snowmaking pipe model', () => {
  it('densifies routes and derives terrain-following length and max-minus-min vertical', () => {
    const result = pipe();
    expect(result.vertices.length).toBeGreaterThan(3);
    expect(result.vertices[0].point).toEqual(A);
    expect(result.vertices.at(-1)?.point).toEqual(C);
    expect(result.verticalM).toBeCloseTo(200, 4);
    expect(result.lengthM).toBeGreaterThan(result.verticalM!);
    expect(snowmakingPipeStats(result.vertices)).toEqual({
      lengthM: result.lengthM, verticalM: result.verticalM,
    });
  });

  it('falls back to horizontal length when any elevation is unresolved', () => {
    const result = pipe();
    result.vertices[1].elevM = null;
    const stats = snowmakingPipeStats(result.vertices);
    expect(stats.verticalM).toBeNull();
    expect(stats.lengthM).toBeGreaterThan(200);
  });

  it('derives solver spans without splitting the editable route', () => {
    const result = pipe('route', 'J1');
    const spans = snowmakingPipeSpans(result);
    expect(spans).toHaveLength(2);
    expect(spans[0].at(-1)?.nodeId).toBe('J1');
    expect(spans[1][0].nodeId).toBe('J1');
  });
});

describe('snowmaking numbering and hydration', () => {
  it('repairs duplicate labels and continues above the persisted high-water mark', () => {
    const hydrated = hydrateSnowmakingNetwork([
      node('h1', 'hydrant', 1), node('h2', 'hydrant', 1), node('p1', 'pump'),
    ], [], { hydrant: 7, junction: 1, pump: 4 });
    expect(hydrated.nodes.map(snowmakingNodeLabel)).toEqual(['1', '7', 'P4']);
    expect(hydrated.nextNumbers).toEqual({ hydrant: 8, junction: 1, pump: 5 });
  });

  it('never returns a committed label number after deletion', () => {
    const initial: SnowmakingNetworkState = { nodes: [], pipes: [],
      nextNumbers: { hydrant: 1, junction: 1, pump: 1 } };
    const first = allocateSnowmakingNode(initial, { id: 'h1', kind: 'hydrant', point: A,
      elevM: 100, createdAt: '2026-01-01T00:00:00.000Z' });
    const afterDelete = { ...first.state, nodes: [] };
    const second = allocateSnowmakingNode(afterDelete, { id: 'h2', kind: 'hydrant', point: B,
      elevM: 110, createdAt: '2026-01-01T00:00:00.000Z' });
    expect(first.node.labelNumber).toBe(1);
    expect(second.node.labelNumber).toBe(2);
  });

  it('drops malformed pipes, clears orphan refs, and keeps schema-less pipes empty', () => {
    const junction = node('junction', 'junction', 1);
    const valid = pipe('valid', 'junction');
    const hydrated = hydrateSnowmakingNetwork([junction], [valid, { ...valid, id: 'junction' }], null);
    expect(hydrated.pipes).toHaveLength(1);
    expect(hydrateSnowmakingNetwork([], [], null).pipes).toEqual([]);
  });
});

describe('snowmaking detach and cleanup', () => {
  it('detaches a device without moving pipe geometry', () => {
    const before = pipe('route', 'pump');
    const points = before.vertices.map((vertex) => vertex.point);
    const after = detachSnowmakingNode([before], 'pump')[0];
    expect(after.vertices.map((vertex) => vertex.point)).toEqual(points);
    expect(after.vertices.some((vertex) => vertex.nodeId === 'pump')).toBe(false);
  });

  it('prunes an affected junction once it no longer joins two pipe routes', () => {
    const junction = node('junction', 'junction', 1);
    const state: SnowmakingNetworkState = {
      nodes: [junction], pipes: [pipe('one', 'junction')],
      nextNumbers: { hydrant: 1, junction: 2, pump: 1 },
    };
    const cleaned = pruneAffectedJunctions(state, new Set(['junction']));
    expect(cleaned.nodes).toEqual([]);
    expect(cleaned.pipes[0].vertices.every((vertex) => vertex.nodeId == null)).toBe(true);
  });
});
