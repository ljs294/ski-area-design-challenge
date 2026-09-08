import { describe, expect, it } from 'vitest';
import { buildSkiNetwork } from '../network';
import type { SavedLift, SavedTrail } from '../types';
import { createDailyGuestRoster, createGuestSimulationEngine } from '../guestSimulation/engine';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import { guestNetworkFromSkiNetwork, guestRenderPoints } from './guestNetworkAdapter';
import { placeGuestPortal } from './guestPortalPlacement';

const base: [number, number] = [-121.495, 46.904];
const top: [number, number] = [-121.495, 46.906];
const lift: SavedLift = { id: 'guest-lift', name: 'Guest Express', liftTypeId: 'fixed-grip-quad',
  points: [base, top], endpointElevM: [1_000, 1_100], lengthM: 222, verticalM: 100,
  status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' };
const trail: SavedTrail = { id: 'guest-run', name: 'Guest Run', parts: [{ polygon: [[
  [-121.4952, 46.906], [-121.4948, 46.906], [-121.4948, 46.904],
  [-121.4952, 46.904], [-121.4952, 46.906],
]], centerline: [top, base], centerlineElevM: [1_100, 1_000] }], brushWidthM: 30,
  areaM2: 6_000, lengthM: 222, verticalM: 100, avgSlopeDeg: 24, maxSlopeDeg: 26,
  difficulty: 'blue', status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' };

describe('guest map projection', () => {
  it('projects active lift and trail traversal progress instead of fixed landmarks', () => {
    const skiNetwork = buildSkiNetwork([trail], [lift]);
    const portal = placeGuestPortal(skiNetwork, base).portal!;
    const liftEdge = skiNetwork.edges.find((edge) => edge.kind === 'lift')!;
    const descentEdge = skiNetwork.edges.find((edge) => edge.kind === 'trail')!;
    const itinerary = { id: 'route', guestId: 'moving', liftId: lift.id, connectorEdgeIds: [],
      liftEdgeId: liftEdge.id, descentEdgeId: descentEdge.id, travelToLiftSeconds: 0,
      rideSeconds: 100, descentSeconds: 100, targetRating: 0.45, weight: 1, decisionOrdinal: 0 };
    const snapshot = (status: 'lift-ride' | 'skiing', tick: number) => ({ tick,
      guests: [{ id: 'moving', status, currentResourceId: status === 'skiing' ? descentEdge.id : lift.id }],
      itineraries: [itinerary], pendingEvents: [{ tick: 100, guestId: 'moving',
        payload: status === 'skiing' ? { kind: 'descent-complete', guestId: 'moving', traversalId: 'one' }
          : { kind: 'ride-complete', guestId: 'moving' } }], safety: { guestIncidents: [] },
    }) as unknown as GuestSimulationEngineSnapshot;

    const quarterLift = guestRenderPoints(snapshot('lift-ride', 25), skiNetwork, portal)[0]!;
    const threeQuarterLift = guestRenderPoints(snapshot('lift-ride', 75), skiNetwork, portal)[0]!;
    const quarterRun = guestRenderPoints(snapshot('skiing', 25), skiNetwork, portal)[0]!;
    const threeQuarterRun = guestRenderPoints(snapshot('skiing', 75), skiNetwork, portal)[0]!;
    expect(threeQuarterLift.lat).toBeGreaterThan(quarterLift.lat);
    expect(threeQuarterRun.lat).toBeLessThan(quarterRun.lat);
    expect(quarterLift.lat).toBeGreaterThan(base[1]);
    expect(threeQuarterLift.lat).toBeLessThan(top[1]);
  });

  it('publishes an individual point after the first connected guest arrives', () => {
    const skiNetwork = buildSkiNetwork([trail], [lift]);
    const portal = placeGuestPortal(skiNetwork, base).portal!;
    const network = guestNetworkFromSkiNetwork(skiNetwork, portal);
    const roster = createDailyGuestRoster({ seed: 'visible-guests', guestCount: 24,
      portals: network.portals, startTick: 0, endTick: 43_200 });
    const firstArrival = Math.min(...roster.guests.map((guest) => guest.arrivalTick));
    const snapshot = createGuestSimulationEngine({ network, roster }).advanceTo(firstArrival + 1);
    const points = guestRenderPoints(snapshot, skiNetwork, portal);
    expect(snapshot.metrics.active).toBeGreaterThan(0);
    expect(points).toHaveLength(snapshot.metrics.active);
    expect(points[0]).toEqual(expect.objectContaining({ id: expect.any(String),
      lng: expect.any(Number), lat: expect.any(Number), status: expect.any(String) }));
  });

  it('admits and renders guests during a later absolute operating day', () => {
    const skiNetwork = buildSkiNetwork([trail], [lift]);
    const placed = placeGuestPortal(skiNetwork, base).portal!;
    const startTick = 10_627_200;
    const endTick = startTick + 43_200;
    const network = guestNetworkFromSkiNetwork(skiNetwork, placed, { startTick, endTick });
    const roster = createDailyGuestRoster({ seed: 'visible-winter-guests', guestCount: 24,
      portals: network.portals, startTick, endTick });
    const firstArrival = Math.min(...roster.guests.map((guest) => guest.arrivalTick));
    const snapshot = createGuestSimulationEngine({ network, roster }).advanceTo(firstArrival + 600);
    const points = guestRenderPoints(snapshot, skiNetwork, placed);

    expect(network.portals[0]).toMatchObject({ openFromTick: startTick, openUntilTick: endTick });
    expect(snapshot.metrics.arrived).toBeGreaterThan(0);
    expect(snapshot.metrics.active).toBeGreaterThan(0);
    expect(snapshot.metrics.departed).toBeLessThan(snapshot.metrics.arrived);
    expect(points).toHaveLength(snapshot.metrics.active);
    expect(points.some((point) => point.status !== 'arriving' && point.status !== 'choosing')).toBe(true);
  });
});
