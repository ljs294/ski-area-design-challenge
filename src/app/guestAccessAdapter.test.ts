import { describe, expect, it } from 'vitest';
import { compileAccessGraph, routeAccessGraph } from '../guestSimulation/access';
import { guestAccessFromRoads } from './guestAccessAdapter';

const portal = { version: 1 as const, id: 'portal', kind: 'guest-entrance' as const, type: 'guest-entrance' as const,
  semantics: 'guest-entrance' as const, direction: 'inbound' as const, accepts: 'guests' as const, label: 'Entrance',
  capacityGuestsPerTick: 20, openFromTick: 0, openUntilTick: 10_000, nodeId: 'ski-node', lngLat: [2, 0] as const };

describe('guest road access adapter', () => {
  it('routes a saved road from its remote edge through parking to the GuestPortal', () => {
    const input = guestAccessFromRoads([{ id: 'road', name: 'Access', roadType: 'two-lane', widthM: 8,
      points: [[0, 0], [1, 0], [2, 0]], lengthM: 800, createdAt: '2026-01-01' }], portal)!;
    const graph = compileAccessGraph(input);
    const origin = graph.nodes.find((node) => node.kind === 'edge-of-map')!;
    const destination = graph.portalConnections[0]!.portalNodeId;
    expect(routeAccessGraph(graph, origin.id, destination)?.travelSeconds).toBeGreaterThan(0);
    expect(graph.parkingAreas).toHaveLength(1);
  });

  it('lets the worker fall back when no road exists', () => {
    expect(guestAccessFromRoads([], portal)).toBeUndefined();
  });
});
