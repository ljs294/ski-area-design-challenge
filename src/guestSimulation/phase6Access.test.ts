import { describe, expect, it } from 'vitest';
import { GUEST_SIMULATION_CONTRACT_VERSION, type GuestPortal } from './contracts.ts';
import {
  chunkVehicleTrips,
  compileAccessGraph,
  congestionAccessibility,
  createDeterministicVehicleTrips,
  createPhase6AccessSnapshot,
  decodePhase6AccessSnapshot,
  encodePhase6AccessSnapshot,
  handoffVehicleOccupants,
  routeAccessGraph,
  simulateVehicleAccess,
  type AccessGraph,
  type VehicleTrip,
} from './access.ts';
import { createLodgingSchedule, lodgingCheckInOpenAt, lodgingOccupancyAt } from './lodging.ts';
import { createRepeatVisitorMemorySnapshot, decodeRepeatVisitorMemory, encodeRepeatVisitorMemory, recordRepeatVisitorVisits, repeatVisitorMemoryFor } from './repeatMemory.ts';

const portal = (id = 'base'): GuestPortal => ({ version: GUEST_SIMULATION_CONTRACT_VERSION, id, kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: id, capacityGuestsPerTick: 2, openFromTick: 0, openUntilTick: 200 });

function accessGraph(extra: { readonly parking?: boolean; readonly dropOff?: boolean } = {}): AccessGraph {
  return compileAccessGraph({
    nodes: [{ id: 'map-edge', kind: 'edge-of-map' }],
    roads: [{ id: 'road-a', points: [[0, 0], [1, 0]], travelSeconds: 3, capacityVehicles: 4 }],
    edges: [{ id: 'edge-link', fromNodeId: 'map-edge', toNodeId: 'road:road-a:0', travelSeconds: 1 }],
    parkingAreas: extra.parking ? [{ id: 'lot', roadId: 'road-a', capacityVehicles: 1 }] : [],
    dropOffZones: extra.dropOff ? [{ id: 'drop', roadId: 'road-a', capacityVehiclesPerTick: 1 }] : [],
    portals: [{ portal: portal(), roadId: 'road-a', pointIndex: 1 }],
  });
}

function trip(id: string, guestIds: readonly string[], destinationFacilityId?: string): VehicleTrip {
  return { id, edgeOfMapNodeId: 'map-edge', destinationPortalId: 'base', ...(destinationFacilityId ? { destinationFacilityId } : {}), departureTick: 0, occupants: guestIds.map((guestId) => ({ guestId })) };
}

describe('Phase 6 road access', () => {
  it('compiles roads and finds deterministic routes with congestion monotonicity', () => {
    const graph = accessGraph();
    const free = routeAccessGraph(graph, 'map-edge', 'portal:base');
    const busy = routeAccessGraph(graph, 'map-edge', 'portal:base', { edgeFlowVehicles: { 'road-a:0': 4 } });
    expect(free?.edgeIds).toEqual(['edge-link', 'road-a:0', 'portal:base:in']);
    expect(busy?.travelSeconds).toBeGreaterThan(free?.travelSeconds ?? 0);
    expect(congestionAccessibility(10, 0, 5)).toBeGreaterThan(congestionAccessibility(10, 10, 5));
  });

  it('generates identical demand when trips are consumed in chunks', () => {
    const options = { seed: 'phase6', tripCount: 11, edgeOfMapNodeIds: ['edge-a', 'edge-b'], destinationPortalIds: ['base', 'north'], startTick: 4, endTick: 80, minimumOccupants: 1, maximumOccupants: 3 } as const;
    const full = createDeterministicVehicleTrips(options);
    const chunked = chunkVehicleTrips(options, 4).flatMap((chunk) => chunk.trips);
    expect(chunked).toEqual(full);
  });

  it('keeps future arrivals queued instead of turning them away', () => {
    const ledger = simulateVehicleAccess({ graph: accessGraph(), trips: [trip('v-1', ['g-1'])], tick: 2 });
    expect(ledger.occupants[0]?.status).toBe('queued');
    expect(ledger.conservation.turnedAway).toBe(0);
  });

  it('enforces parking capacity and conserves every occupant', () => {
    const ledger = simulateVehicleAccess({ graph: accessGraph({ parking: true }), trips: [trip('v-1', ['g-1'], 'lot'), trip('v-2', ['g-2'], 'lot')], tick: 100 });
    expect(ledger.conservation.occupants).toBe(2);
    expect(ledger.conservation.parked + ledger.conservation.handedOff + ledger.conservation.queued + ledger.conservation.turnedAway).toBe(2);
    expect(ledger.occupants.filter((occupant) => occupant.status === 'handed-off')).toHaveLength(1);
    expect(ledger.occupants.find((occupant) => occupant.guestId === 'g-2')?.status).toBe('queued');
  });

  it('enforces one drop-off vehicle per tick', () => {
    const ledger = simulateVehicleAccess({ graph: accessGraph({ dropOff: true }), trips: [trip('v-1', ['g-1'], 'drop'), trip('v-2', ['g-2'], 'drop')], tick: 100 });
    expect(ledger.occupants.filter((occupant) => occupant.status === 'handed-off')).toHaveLength(1);
    expect(ledger.occupants.find((occupant) => occupant.guestId === 'g-2')?.status).toBe('queued');
  });

  it('makes portal handoff idempotent and does not duplicate guests', () => {
    const graph = accessGraph();
    const initial = simulateVehicleAccess({ graph, trips: [trip('v-1', ['g-1', 'g-2'])], tick: 100 });
    const once = handoffVehicleOccupants(initial, 'v-1', 'base', 20, graph);
    const twice = handoffVehicleOccupants(once, 'v-1', 'base', 20, graph);
    expect(once).toEqual(twice);
    expect(twice.handoffs.map((entry) => entry.guestId)).toEqual(['g-1', 'g-2']);
    const restored = decodePhase6AccessSnapshot(encodePhase6AccessSnapshot(createPhase6AccessSnapshot(graph, twice)));
    expect(restored.graph.checksum).toBe(graph.checksum);
  });
});

describe('Phase 6 lodging and repeat memory', () => {
  it('records multi-day stays, capacity conflicts, and schedules', () => {
    const schedule = createLodgingSchedule({ properties: [{ id: 'lodge', capacityGuests: 2, checkInFromTick: 50, checkInUntilTick: 100, checkOutFromTick: 10, checkOutUntilTick: 40 }], stays: [
      { id: 'stay-a', visitorKey: 'visitor-a', guestIds: ['a'], lodgingId: 'lodge', arrivalDay: 1, departureDay: 3 },
      { id: 'stay-b', visitorKey: 'visitor-b', guestIds: ['b', 'c'], lodgingId: 'lodge', arrivalDay: 2, departureDay: 3 },
    ] });
    expect(schedule.stays.find((stay) => stay.id === 'stay-a')?.nights).toBe(2);
    expect(schedule.stays.find((stay) => stay.id === 'stay-b')?.status).toBe('waitlisted');
    expect(lodgingOccupancyAt(schedule, 'lodge', 2)).toBe(1);
    expect(lodgingCheckInOpenAt(schedule, 'lodge', 86_400 + 60)).toBe(true);
  });

  it('carries repeat memory in a separate checksummed sidecar', () => {
    const empty = createRepeatVisitorMemorySnapshot();
    const next = recordRepeatVisitorVisits(empty, [{ visitId: 'v1', visitorKey: 'repeat-a', day: 1, portalId: 'base', lodgingId: 'lodge', satisfaction: 0.8 }, { visitId: 'v2', visitorKey: 'repeat-a', day: 3, portalId: 'north', satisfaction: 0.6 }]);
    expect(repeatVisitorMemoryFor(next, 'repeat-a')).toMatchObject({ visitCount: 2, firstVisitDay: 1, lastVisitDay: 3, preferredPortalId: 'north' });
    expect(recordRepeatVisitorVisits(next, [{ visitId: 'v1', visitorKey: 'repeat-a', day: 1,
      portalId: 'base', lodgingId: 'lodge', satisfaction: 0.8 }])).toEqual(next);
    expect(decodeRepeatVisitorMemory(encodeRepeatVisitorMemory(next))).toEqual(next);
    expect(next.checksum).not.toBe(empty.checksum);
  });
});
