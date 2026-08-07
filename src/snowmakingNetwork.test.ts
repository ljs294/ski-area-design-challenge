import { describe, expect, it } from 'vitest';
import {
  allocateSnowmakingNode,
  attachNodesToSnowmakingPipe,
  buildSnowmakingPipe,
  detachSnowmakingNode,
  hydrateSnowmakingNetwork,
  pruneAffectedJunctions,
  populateSnowmakingHydrantRun,
  snowmakingNodeLabel,
  snowmakingPipeSpans,
  snowmakingHydrantRunLayout,
  snowmakingPipeIntervalPoints,
  snowmakingPipePointAtStation,
  snowmakingPipeStationAt,
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

describe('snowmaking hydrant runs', () => {
  it('stations clicks and resolves forward and reverse endpoint-inclusive layouts', () => {
    const route = pipe();
    const start = snowmakingPipePointAtStation(route, route.lengthM * 0.2)!;
    const end = snowmakingPipePointAtStation(route, route.lengthM * 0.8)!;
    const projected = snowmakingPipeStationAt(route, start.point)!;
    expect(projected.stationM).toBeCloseTo(start.stationM, 4);

    const forward = snowmakingHydrantRunLayout(route, start, end, { mode: 'count', count: 4 });
    expect(typeof forward).not.toBe('string');
    if (typeof forward === 'string') return;
    expect(forward.positions).toHaveLength(4);
    expect(forward.positions[0].stationM).toBeCloseTo(start.stationM);
    expect(forward.positions.at(-1)?.stationM).toBeCloseTo(end.stationM);
    expect(forward.actualSpacingM).toBeCloseTo(forward.lengthM / 3);

    const reverse = snowmakingHydrantRunLayout(route, end, start, { mode: 'count', count: 3 });
    expect(typeof reverse).not.toBe('string');
    if (typeof reverse === 'string') return;
    expect(reverse.positions.map((position) => position.stationM)).toEqual([
      expect.closeTo(end.stationM), expect.closeTo((start.stationM + end.stationM) / 2),
      expect.closeTo(start.stationM),
    ]);
    expect(snowmakingPipeIntervalPoints(route, end, start)[0]).toEqual(end.point);
  });

  it('treats requested spacing as a maximum and caps oversized batches', () => {
    const route = pipe();
    const start = snowmakingPipePointAtStation(route, 0)!;
    const end = snowmakingPipePointAtStation(route, route.lengthM)!;
    const layout = snowmakingHydrantRunLayout(route, start, end,
      { mode: 'spacing', spacingM: route.lengthM / 2.4 });
    expect(typeof layout).not.toBe('string');
    if (typeof layout === 'string') return;
    expect(layout.positions).toHaveLength(4);
    expect(layout.actualSpacingM).toBeLessThanOrEqual(route.lengthM / 2.4);
    expect(snowmakingHydrantRunLayout(route, start, end,
      { mode: 'spacing', spacingM: 0.01 })).toContain('500');
  });

  it('uses horizontal stationing when elevation is incomplete', () => {
    const route = pipe();
    route.vertices[1].elevM = null;
    const start = snowmakingPipePointAtStation(route, 0)!;
    const end = snowmakingPipePointAtStation(route,
      snowmakingPipeStats(route.vertices).lengthM)!;
    expect(start.elevM).toBeNull();
    expect(end.elevM).toBeNull();
  });

  it('inserts all node references in station order without splitting the route', () => {
    const route = pipe('route');
    const updated = attachNodesToSnowmakingPipe(route, [
      { stationM: route.lengthM * 0.75, nodeId: 'h2' },
      { stationM: route.lengthM * 0.25, nodeId: 'h1' },
    ]);
    expect(updated.id).toBe(route.id);
    expect(updated.vertices.filter((vertex) => vertex.nodeId).map((vertex) => vertex.nodeId))
      .toEqual(['h1', 'h2']);
    expect(updated.lengthM).toBeCloseTo(route.lengthM, 3);
  });

  it('skips occupied positions and allocates labels only for committed hydrants', () => {
    const route = pipe('route');
    const start = snowmakingPipePointAtStation(route, 0)!;
    const end = snowmakingPipePointAtStation(route, route.lengthM)!;
    const layout = snowmakingHydrantRunLayout(route, start, end, { mode: 'count', count: 3 });
    expect(typeof layout).not.toBe('string');
    if (typeof layout === 'string') return;
    const occupied = node('pump-at-start', 'pump', 1);
    const initial: SnowmakingNetworkState = { nodes: [occupied], pipes: [route],
      nextNumbers: { hydrant: 1, junction: 1, pump: 2 } };
    let id = 0;
    const populated = populateSnowmakingHydrantRun(initial, route.id, layout,
      () => `hydrant-${++id}`, () => '2026-01-01T00:00:00.000Z');
    expect(typeof populated).not.toBe('string');
    if (typeof populated === 'string') return;
    expect(populated.skipped).toBe(1);
    expect(populated.nodes.map((created) => created.labelNumber)).toEqual([1, 2]);
    expect(populated.state.nextNumbers.hydrant).toBe(3);
    expect(populated.state.pipes).toHaveLength(1);
    expect(populated.state.pipes[0].vertices.filter((vertex) => vertex.nodeId)
      .map((vertex) => vertex.nodeId)).toEqual(['hydrant-1', 'hydrant-2']);
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
