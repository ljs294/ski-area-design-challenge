import { describe, expect, it } from 'vitest';
import {
  chooseWeightedGuestItinerary,
  createDailyGuestRoster,
  createDefaultGuestSimulationNetwork,
  createGuestSimulationEngine,
  createGuestSimulationNetwork,
  guestAbilityTargets,
  placeGuestPortal,
  type GuestNetworkEdge,
  type GuestNetworkNode,
  type GuestLift,
} from './engine';
import { GUEST_SIMULATION_CONTRACT_VERSION, type GuestPortal } from './contracts';

const portal = (id: string): GuestPortal => ({
  version: GUEST_SIMULATION_CONTRACT_VERSION, id, kind: 'guest-entrance', type: 'guest-entrance',
  semantics: 'guest-entrance', direction: 'inbound', accepts: 'guests', label: id,
  capacityGuestsPerTick: 8, openFromTick: 0, openUntilTick: 100_000,
});

function smallNetwork(portals: readonly GuestPortal[] = [portal('entrance')]) {
  const nodes: GuestNetworkNode[] = [
    { id: 'portal-node', kind: 'portal' }, { id: 'base', kind: 'lift-base' }, { id: 'top', kind: 'lift-top' },
  ];
  const edges: GuestNetworkEdge[] = [
    { id: 'walk', fromNodeId: 'portal-node', toNodeId: 'base', kind: 'connector', travelSeconds: 1 },
    { id: 'chair', fromNodeId: 'base', toNodeId: 'top', kind: 'lift', travelSeconds: 1, liftId: 'chair-1', capacitySeats: 2 },
    { id: 'run', fromNodeId: 'top', toNodeId: 'base', kind: 'descent', travelSeconds: 3, targetRating: 0.65 },
  ];
  const lifts: GuestLift[] = [{ id: 'chair-1', baseNodeId: 'base', topNodeId: 'top', edgeId: 'chair', capacitySeats: 2, rideSeconds: 2 }];
  return createGuestSimulationNetwork({ nodes, edges, lifts, portals,
    portalConnections: portals.map((entry) => ({ portalId: entry.id, nodeId: 'portal-node' })) });
}

describe('Phase 1A guest simulation engine', () => {
  it('uses a realized demand plan as roster authority and permits a zero-demand day', () => {
    const network = smallNetwork();
    const empty = createDailyGuestRoster({ seed: 'quiet-day', guestCount: 0, portals: network.portals,
      startTick: 0, endTick: 100, demandPlan: { version: 1, seed: 'quiet-day', guestCount: 0,
        partyCount: 0, startTick: 0, endTick: 100, heavyGroupCount: 0,
        waves: [{ id: 'empty', kind: 'weekday', startTick: 0, endTick: 100, guestCount: 0, partyCount: 0 }] } });
    expect(empty.guests).toHaveLength(0);
    expect(createGuestSimulationEngine({ network, roster: empty }).snapshot().metrics.population).toBe(0);

    const demandPlan = { version: 1 as const, seed: 'demand-shaped', guestCount: 6, partyCount: 4,
      startTick: 0, endTick: 100, heavyGroupCount: 0,
      waves: [{ id: 'early', kind: 'weekday' as const, startTick: 0, endTick: 50, guestCount: 2, partyCount: 1 },
        { id: 'late', kind: 'weekday' as const, startTick: 50, endTick: 100, guestCount: 4, partyCount: 3 }] };
    const roster = createDailyGuestRoster({ seed: demandPlan.seed, guestCount: 6, portals: network.portals,
      startTick: 0, endTick: 100, demandPlan });
    expect(roster.guests.filter((guest) => guest.arrivalTick < 50)).toHaveLength(2);
    expect(roster.guests.filter((guest) => guest.arrivalTick >= 50)).toHaveLength(4);
    expect(roster.demandPlan.waves.map((wave) => wave.guestCount)).toEqual([2, 4]);
  });

  it('places only inbound portals on existing network nodes', () => {
    const network = smallNetwork();
    const placed = placeGuestPortal(network, { portalId: 'new-entrance', nodeId: 'portal-node', capacityGuestsPerTick: 3 });
    expect(placed.portals.map((entry) => entry.id)).toEqual(['entrance', 'new-entrance']);
    expect(placed.portalConnections.find((entry) => entry.portalId === 'new-entrance')?.nodeId).toBe('portal-node');
    expect(() => placeGuestPortal(network, { portalId: 'bad', nodeId: 'missing' })).toThrow(/unknown node/i);
  });

  it('creates stable individual ability and bounded target ratings from a seed', () => {
    const network = smallNetwork();
    const roster = createDailyGuestRoster({ seed: 'ability-seed', guestCount: 4, portals: network.portals, endTick: 100 });
    const guest = roster.guests[0]!;
    const first = guestAbilityTargets(guest, roster.seed);
    const second = guestAbilityTargets(guest, roster.seed);
    expect(first).toEqual(second);
    expect(first.minimumTargetRating).toBeLessThanOrEqual(first.ability);
    expect(first.ability).toBeLessThanOrEqual(first.maximumTargetRating);
    expect(first.targetRating).toBeGreaterThanOrEqual(first.minimumTargetRating);
    expect(first.targetRating).toBeLessThanOrEqual(first.maximumTargetRating);
  });

  it('does not choose a closed lift or descent edge', () => {
    const network = createGuestSimulationNetwork({
      ...smallNetwork(),
      edges: smallNetwork().edges.map((edge) => edge.id === 'run' ? { ...edge, closed: true } : edge),
    });
    const roster = createDailyGuestRoster({ seed: 'route-seed', guestCount: 1, portals: network.portals, endTick: 100 });
    const guest = roster.guests[0]!;
    const environment = {
      version: GUEST_SIMULATION_CONTRACT_VERSION, tick: 0, environmentRevision: 1, topologyRevision: 1, operating: true,
      conditions: { version: GUEST_SIMULATION_CONTRACT_VERSION, tick: 0, status: 'good' as const, trend: 'stable' as const,
        temperatureC: -2, windKph: 0, visibilityKm: 10, precipitationMm: 0, snowfallCm: 0,
        terrainOpenFraction: 1, liftOpenFraction: 1, trailOpenFraction: 1 }, portals: network.portals, incidents: [],
    };
    expect(chooseWeightedGuestItinerary(network, guest, roster.seed, environment, 0)).toBeNull();
  });

  it('conserves lift seats and produces deterministic large/small advances', () => {
    const network = smallNetwork();
    const roster = createDailyGuestRoster({ seed: 'advance-seed', guestCount: 8, portals: network.portals, endTick: 120 });
    const large = createGuestSimulationEngine({ network, roster, runId: 'same-run' });
    const sliced = createGuestSimulationEngine({ network, roster, runId: 'same-run' });
    const largeSnapshot = large.advanceTo(120);
    for (let second = 1; second <= 120; second += 1) sliced.advanceTo(second);
    const slicedSnapshot = sliced.snapshot();
    expect(slicedSnapshot.checksum).toBe(largeSnapshot.checksum);
    expect(slicedSnapshot.guests).toEqual(largeSnapshot.guests);
    expect(largeSnapshot.metrics.population).toBe(8);
    expect(largeSnapshot.metrics.liftSeatsConserved).toBe(true);
    expect(largeSnapshot.metrics.liftSeats[0]!.dispatches).toBe(
      largeSnapshot.metrics.liftSeats[0]!.completedRides + largeSnapshot.metrics.liftSeats[0]!.ridersInTransit,
    );
    expect(largeSnapshot.thoughtEvents.some((event) => event.kind === 'arrived')).toBe(true);
    expect(largeSnapshot.thoughtEvents.some((event) => event.kind === 'riding')).toBe(true);
  });

  it('runs through planned departure and clean exit after active rides settle', () => {
    const network = createDefaultGuestSimulationNetwork([portal('entrance')]);
    const roster = createDailyGuestRoster({ seed: 'departure-seed', guestCount: 2, portals: network.portals, endTick: 500 });
    const engine = createGuestSimulationEngine({ network, roster });
    const snapshot = engine.advanceTo(1_000);
    expect(snapshot.guests.every((guest) => guest.status === 'departed')).toBe(true);
    expect(snapshot.thoughtEvents.some((event) => event.kind === 'leaving')).toBe(true);
    expect(snapshot.phase3.economy.visitOutcomes).toHaveLength(2);
    expect(snapshot.phase3.economy.closed).toBe(true);
    expect(snapshot.phase3.economy.closing?.nextDayReputation.checksum).toBeTruthy();
    expect(snapshot.phase3.reconciled).toBe(true);
  });

  it('keeps a party on one leader-owned route constrained by its weakest member', () => {
    const network = createDefaultGuestSimulationNetwork([portal('entrance')]);
    const roster = createDailyGuestRoster({ seed: 'party', guestCount: 8, portals: network.portals, endTick: 500 });
    const engine = createGuestSimulationEngine({ network, roster });
    const snapshot = engine.advanceTo(420);
    const party = roster.parties.find((candidate) => candidate.size > 1 && candidate.arrivalTick < 420);
    expect(party).toBeDefined();
    const plan = snapshot.partyPlans.find((candidate) => candidate.partyId === party!.id);
    expect(plan?.ownership).toBe('leader-owned-shared-plan');
    expect(plan?.weakestMemberSafe).toBe(true);
    const members = party!.guestIds.map((guestId) => snapshot.itineraries.find((itinerary) => itinerary.guestId === guestId));
    const activeMembers = members.filter((itinerary): itinerary is NonNullable<typeof itinerary> => itinerary !== undefined);
    expect(activeMembers.length).toBeGreaterThan(1);
    expect(new Set(activeMembers.map((itinerary) => itinerary.liftId)).size).toBe(1);
    expect(new Set(activeMembers.map((itinerary) => itinerary.descentEdgeId)).size).toBe(1);
    expect(snapshot.rendezvousPlans.some((rendezvous) => rendezvous.partyId === party!.id)).toBe(true);
  });

  it('dispatches physical chairs at the configured interval and boards parties consecutively without starvation', () => {
    const base = smallNetwork();
    const network = createGuestSimulationNetwork({
      ...base,
      lifts: base.lifts.map((lift) => ({ ...lift, dispatchIntervalSeconds: 10, capacitySeats: 1 })),
      edges: base.edges.map((edge) => edge.kind === 'lift' ? { ...edge, capacitySeats: 1 } : edge),
    });
    const roster = createDailyGuestRoster({ seed: 'interval', guestCount: 12, portals: network.portals, endTick: 100 });
    const engine = createGuestSimulationEngine({ network, roster });
    const initialDispatches = engine.snapshot().pendingEvents.filter((event) => event.payload.kind === 'lift-dispatch');
    expect(initialDispatches).toHaveLength(1);
    expect(initialDispatches[0]!.tick).toBe(0);
    const snapshot = engine.advanceTo(25);
    const dispatches = snapshot.metrics.liftSeats[0]!;
    expect(dispatches.dispatches).toBeLessThanOrEqual(3);
    expect(dispatches.dispatches).toBe(dispatches.completedRides + dispatches.ridersInTransit);
    const dispatchTicks = snapshot.pendingEvents.filter((event) => event.payload.kind === 'lift-dispatch').map((event) => event.tick);
    expect(dispatchTicks).toContain(30);
  });
});
