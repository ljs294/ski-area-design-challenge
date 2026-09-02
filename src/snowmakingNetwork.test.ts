import { describe, expect, it } from 'vitest';
import {
  allocateSnowmakingNode,
  attachInlinePumpToSnowmakingPipe,
  attachNodesToSnowmakingPipe,
  buildSnowmakingPipe,
  closestSnowmakingPipeLocation,
  detachSnowmakingNode,
  hydrateSnowmakingNetwork,
  normalizeSnowmakingPipeSegments,
  pruneAffectedJunctions,
  populateSnowmakingHydrantRun,
  snowmakingNodeLabel,
  snowmakingPipeSpans,
  snowmakingPipeSegments,
  snowmakingHydrantRunLayout,
  snowmakingPipeIntervalPoints,
  snowmakingPipePointAtStation,
  snowmakingPipeStationAt,
  snowmakingPipeStats,
  type SnowmakingNetworkState,
} from './snowmakingNetwork';
import { createOwnedSnowmakingPump, removeBuildingOwnedPump,
  renameOwnedSnowmakingPump, snowmakingNodeInspectorCapabilities } from './snowmakingOwnedPumps';
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

  it('keeps stable segment identity and pump-facing roles across topology edits', () => {
    const route = pipe('route', 'pump');
    route.segments = normalizeSnowmakingPipeSegments(route).map((segment, index) => ({
      ...segment,
      ...(index === 0 ? { endPumpPort: 'suction' as const }
        : { startPumpPort: 'discharge' as const }),
    }));
    let id = 0;
    const split = attachNodesToSnowmakingPipe(route,
      [{ stationM: route.lengthM * 0.25, nodeId: 'hydrant' }], () => `new-segment-${++id}`);
    expect(split.segments).toHaveLength(3);
    expect(split.segments?.map((segment) => segment.id)).not.toContain(route.segments[0].id);
    expect(split.segments?.at(-1)?.id).toBe(route.segments.at(-1)?.id);
    expect(split.segments?.[1].endPumpPort).toBe('suction');
    expect(split.segments?.[2].startPumpPort).toBe('discharge');
    expect(snowmakingPipeSegments(split).map((segment) => segment.vertices.length)
      .every((length) => length >= 2)).toBe(true);
  });

  it('installs an inline pump with exactly two oppositely configured arms', () => {
    const route = pipe('main');
    const location = closestSnowmakingPipeLocation(route, B)!;
    const installed = attachInlinePumpToSnowmakingPipe(route, location, 'pump-1',
      'route-start', (() => { let id = 0; return () => `split-${++id}`; })());
    expect(installed).not.toBeNull();
    const arms = snowmakingPipeSegments(installed!).filter((segment) =>
      segment.fromNodeId === 'pump-1' || segment.toNodeId === 'pump-1');
    expect(arms).toHaveLength(2);
    expect(arms.find((segment) => segment.toNodeId === 'pump-1')?.endPumpPort).toBe('suction');
    expect(arms.find((segment) => segment.fromNodeId === 'pump-1')?.startPumpPort).toBe('discharge');
    expect(arms.map((segment) => segment.id)).not.toContain(route.segments?.[0].id);
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
    const initial: SnowmakingNetworkState = { nodes: [occupied], pipes: [route], guns: [],
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
    const initial: SnowmakingNetworkState = { nodes: [], pipes: [], guns: [],
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

  it('materializes schema-12 segments for legacy pipes and repairs malformed metadata', () => {
    const legacy = pipe('legacy');
    const { segments: _segments, ...withoutSegments } = legacy;
    const hydrated = hydrateSnowmakingNetwork([], [withoutSegments], null);
    expect(hydrated.pipes[0].segments).toEqual([{ id: 'legacy:segment:0',
      startVertexIndex: 0, endVertexIndex: legacy.vertices.length - 1,
      startPumpPort: null, endPumpPort: null }]);
    const malformed = { ...legacy, segments: [{ id: 'duplicate', startVertexIndex: 1,
      endVertexIndex: 2, startPumpPort: 'suction', endPumpPort: 'discharge' }] };
    expect(hydrateSnowmakingNetwork([], [malformed], null).pipes[0].segments)
      .toEqual(hydrated.pipes[0].segments);
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
      nodes: [junction], pipes: [pipe('one', 'junction')], guns: [],
      nextNumbers: { hydrant: 1, junction: 2, pump: 1 },
    };
    const cleaned = pruneAffectedJunctions(state, new Set(['junction']));
    expect(cleaned.nodes).toEqual([]);
    expect(cleaned.pipes[0].vertices.every((vertex) => vertex.nodeId == null)).toBe(true);
  });

  it('creates a numbered fixed-rating building pump and removes it with pipe ends detached', () => {
    const state: SnowmakingNetworkState = { nodes: [], pipes: [pipe('route')], guns: [],
      nextNumbers: { hydrant: 1, junction: 1, pump: 1 } };
    const created = createOwnedSnowmakingPump(state, {
      id: 'house-pump', ownerBuildingId: 'house', name: 'Alpine Pump House', point: A,
      elevM: 100, createdAt: '2026-01-01T00:00:00.000Z',
    });
    expect(created?.node).toMatchObject({ kind: 'pump', labelNumber: 1,
      ownerBuildingId: 'house', pumpRating: { horsepowerHp: 1000, efficiency: 0.85 } });
    expect(created?.state.nextNumbers.pump).toBe(2);
    if (!created) return;
    const connected = { ...created.state, pipes: [buildSnowmakingPipe({
      id: 'connected', name: 'Connected', diameterIn: 8,
      points: [A, B], nodeIds: ['house-pump', null], createdAt: '2026-01-01T00:00:00.000Z',
    }, () => 100)] };
    const renamed = renameOwnedSnowmakingPump(connected, 'house', 'Renamed House');
    expect(renamed?.nodes[0].name).toBe('Renamed House');
    expect(snowmakingNodeInspectorCapabilities(renamed?.nodes[0])).toEqual({
      canRename: false, canRemove: false, ownerBuildingId: 'house',
    });
    const removed = removeBuildingOwnedPump(renamed!, 'house');
    expect(removed?.state.nodes).toEqual([]);
    expect(removed?.connectedPipeIds).toEqual(['connected']);
    expect(removed?.state.pipes[0].vertices[0].nodeId).toBeNull();
  });

  it('does not let building removal helpers affect a manual pump', () => {
    const manual = node('manual', 'pump', 1);
    const state: SnowmakingNetworkState = { nodes: [manual], pipes: [], guns: [],
      nextNumbers: { hydrant: 1, junction: 1, pump: 2 } };
    expect(removeBuildingOwnedPump(state, 'missing')).toBeNull();
    expect(snowmakingNodeInspectorCapabilities(manual)).toEqual({
      canRename: true, canRemove: true, ownerBuildingId: null,
    });
  });
});
