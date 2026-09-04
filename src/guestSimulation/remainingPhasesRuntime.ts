import type { GuestState, SimulatedSecond } from './contracts.ts';
import type { DailyGuestRoster, GuestSimulationNetwork } from './engineSupport.ts';
import { createFacility, type FacilityContract } from './facilities.ts';
import { createPhase5Amenities, type AmenityRequestRecord, type Phase5AmenitiesRuntime, type Phase5AmenitiesSnapshot } from './phase5Amenities.ts';
import {
  compileAccessGraph,
  createPhase6AccessSnapshot,
  routeAccessGraph,
  simulateVehicleAccess,
  type AccessGraph,
  type AccessGraphInput,
  type Phase6AccessSnapshot,
  type VehicleTrip,
} from './access.ts';
import { createLodgingSchedule, type LodgingScheduleSnapshot } from './lodging.ts';
import { createRepeatVisitorMemorySnapshot, recordRepeatVisitorVisits, type RepeatVisitorMemorySnapshot } from './repeatMemory.ts';
import { checksumGuestPublication, GUEST_PUBLICATION_BYTES_PER_GUEST, packGuestPublication } from './phase7Publication.ts';
import { DEFAULT_GUEST_PUBLICATION_POLICY, PHASE7_DEGRADATION_ORDER } from './phase7Policy.ts';

export interface RemainingPhasesInput {
  readonly facilities?: readonly FacilityContract[];
  readonly access?: AccessGraphInput;
  readonly compiledAccess?: AccessGraph;
  readonly repeatVisitors?: RepeatVisitorMemorySnapshot;
}

export interface Phase7RuntimeSnapshot {
  readonly bytesPerGuest: number;
  readonly publicationBytes: number;
  readonly publicationChecksum: number;
  readonly maxPublishedGuests: number;
  readonly degradationOrder: readonly string[];
}

export interface RemainingPhasesSnapshot {
  readonly initialFacilities: readonly FacilityContract[];
  readonly initialRepeatVisitors: RepeatVisitorMemorySnapshot;
  readonly amenities: Phase5AmenitiesSnapshot;
  readonly access: Phase6AccessSnapshot;
  readonly lodging: LodgingScheduleSnapshot;
  readonly repeatVisitors: RepeatVisitorMemorySnapshot;
  readonly scaling: Phase7RuntimeSnapshot;
}

function defaultFacilities(network: GuestSimulationNetwork, startTick: number, endTick: number): readonly FacilityContract[] {
  const entrance = network.portalConnections[0]?.nodeId ?? network.nodes[0]!.id;
  return [
    createFacility({ id: 'base-cafe', label: 'Base Café', kind: 'cafe', operating: true,
      quality: 0.62, comfort: 0.68, schedule: { openFromTick: startTick, openUntilTick: endTick },
      entrances: [{ id: 'base-cafe-entrance', nodeId: entrance, accessSeconds: 90 }], services: [
        { id: 'hot-meal', label: 'Hot meal', kind: 'meal', priceCents: 1_800, serviceSeconds: 120,
          capacity: 24, queueCapacity: 180, quality: 0.7, comfort: 0.72,
          restores: { hunger: 0.8, thirst: 0.25, warmth: 0.35, fatigue: 0.12 } },
        { id: 'drink', label: 'Drink', kind: 'drink', priceCents: 450, serviceSeconds: 30,
          capacity: 12, queueCapacity: 120, quality: 0.58, comfort: 0.6, restores: { thirst: 0.8 } },
      ] }),
    createFacility({ id: 'base-services', label: 'Base Services', kind: 'restroom', operating: true,
      quality: 0.55, comfort: 0.58, schedule: { openFromTick: startTick, openUntilTick: endTick },
      entrances: [{ id: 'base-services-entrance', nodeId: entrance, accessSeconds: 60 }], services: [
        { id: 'restroom', label: 'Restroom', kind: 'restroom', priceCents: 0, serviceSeconds: 45,
          capacity: 20, queueCapacity: 160, quality: 0.55, comfort: 0.58, restores: { restroom: 0.95 } },
        { id: 'warm-up', label: 'Warm up', kind: 'warmth', priceCents: 0, serviceSeconds: 180,
          capacity: 40, queueCapacity: 200, quality: 0.6, comfort: 0.7, restores: { warmth: 0.8, fatigue: 0.2 } },
      ] }),
  ];
}

function fallbackAccess(network: GuestSimulationNetwork): AccessGraphInput {
  return { nodes: [{ id: 'access-origin', kind: 'edge-of-map' }, { id: 'access-resort', kind: 'road' }],
    edges: [{ id: 'access-in', fromNodeId: 'access-origin', toNodeId: 'access-resort', travelSeconds: 0, capacityVehicles: 500 },
      { id: 'access-out', fromNodeId: 'access-resort', toNodeId: 'access-origin', travelSeconds: 0, capacityVehicles: 500 }],
    edgeOfMapNodes: [{ id: 'access-origin' }], parkingAreas: [{ id: 'base-parking', capacityVehicles: 50_000,
      roadNodeId: 'access-resort' }], portals: network.portals.map((portal) => ({ portal, roadNodeId: 'access-resort' })) };
}

function accessTrips(roster: DailyGuestRoster, graph: AccessGraph): readonly VehicleTrip[] {
  const origin = graph.nodes.find((node) => node.kind === 'edge-of-map')!.id;
  const parking = graph.parkingAreas[0]?.id;
  const portalTravelSeconds = new Map(graph.portalConnections.map((connection) => [connection.portalId,
    routeAccessGraph(graph, origin, connection.portalNodeId)?.travelSeconds ?? 0]));
  const portalByParty = new Map(roster.guests.map((guest) => [guest.partyId, guest.portalId]));
  return roster.parties.map((party) => {
    const destinationPortalId = portalByParty.get(party.id) ?? graph.portals[0]!.id;
    return { id: `vehicle:${party.id}`, edgeOfMapNodeId: origin, destinationPortalId,
      ...(parking ? { destinationFacilityId: parking } : {}),
      departureTick: Math.max(roster.demandPlan.startTick,
        party.arrivalTick - (portalTravelSeconds.get(destinationPortalId) ?? 0)),
      occupants: party.guestIds.map((guestId) => ({ guestId, visitorKey: guestId })) };
  });
}

/** Owns Phase 5–7 sidecars without leaking map/save concerns into the engine. */
export class RemainingPhasesRuntime {
  private readonly amenities: Phase5AmenitiesRuntime;
  private readonly initialFacilities: readonly FacilityContract[];
  private readonly initialRepeatVisitors: RepeatVisitorMemorySnapshot;
  private readonly accessGraph: AccessGraph;
  private readonly trips: readonly VehicleTrip[];
  private readonly lodging: LodgingScheduleSnapshot;
  private readonly lodgingGuestIds: ReadonlySet<string>;
  private accessCache: Phase6AccessSnapshot | null = null;
  private readonly accessArrivalTicks = new Map<string, SimulatedSecond>();
  private requestOrdinal = 0;

  constructor(network: GuestSimulationNetwork, roster: DailyGuestRoster, input: RemainingPhasesInput = {}) {
    const facilities = input.facilities ?? defaultFacilities(network, roster.demandPlan.startTick, roster.demandPlan.endTick);
    this.initialFacilities = Object.freeze([...facilities]);
    this.initialRepeatVisitors = input.repeatVisitors ?? createRepeatVisitorMemorySnapshot();
    this.amenities = createPhase5Amenities({ facilities, startTick: roster.demandPlan.startTick,
      guests: roster.guests.map((guest) => ({ guest, walletCents: guest.preferences.tripCashCents })) });
    this.accessGraph = input.compiledAccess ?? compileAccessGraph(input.access ?? fallbackAccess(network));
    this.trips = accessTrips(roster, this.accessGraph);
    const plannedAccess = simulateVehicleAccess({ graph: this.accessGraph, trips: this.trips,
      tick: roster.demandPlan.endTick });
    for (const handoff of plannedAccess.handoffs) this.accessArrivalTicks.set(handoff.guestId, handoff.tick);
    const economicSegmentByGuest = new Map(roster.guests.map((guest) => [guest.id, guest.preferences.economicSegment]));
    const lodgingParties = roster.parties.filter((party) => party.guestIds.some((id) => {
      const segment = economicSegmentByGuest.get(id);
      return segment === 'premium' || segment === 'luxury';
    }));
    this.lodgingGuestIds = new Set(lodgingParties.flatMap((party) => party.guestIds));
    const day = Math.floor(roster.demandPlan.startTick / 86_400);
    this.lodging = createLodgingSchedule({ properties: [{ id: 'external-lodging',
      capacityGuests: Math.max(1, this.lodgingGuestIds.size), checkInFromTick: 54_000, checkInUntilTick: 79_200,
      checkOutFromTick: 25_200, checkOutUntilTick: 39_600, nightlyRateCents: 24_000 }],
    stays: lodgingParties.map((party) => ({ id: `stay:${party.id}`, visitorKey: party.id,
      guestIds: party.guestIds, lodgingId: 'external-lodging', arrivalDay: day, departureDay: day + 2 })) });
  }

  advanceTo(tick: SimulatedSecond): void { this.amenities.advanceClockTo(tick); this.accessCache = null; }

  arrivalTickFor(guestId: string): SimulatedSecond | null { return this.accessArrivalTicks.get(guestId) ?? null; }

  considerAmenity(guestId: string, tick: SimulatedSecond): AmenityRequestRecord | null {
    this.advanceTo(tick);
    return this.amenities.chooseAndRequest(guestId, `amenity:${guestId}:${this.requestOrdinal++}`)?.request ?? null;
  }

  amenityProgress(requestId: string, tick: SimulatedSecond): AmenityRequestRecord | undefined {
    this.advanceTo(tick);
    return this.amenities.requestRecord(requestId);
  }

  amenitySatisfaction(guestId: string): number | undefined { return this.amenities.guest(guestId)?.satisfaction; }

  snapshot(tick: SimulatedSecond, guests: readonly GuestState[], environmentRevision = 0,
    topologyRevision = 0): RemainingPhasesSnapshot {
    if (!this.accessCache || this.accessCache.ledger.tick !== tick) {
      this.accessCache = createPhase6AccessSnapshot(this.accessGraph,
        simulateVehicleAccess({ graph: this.accessGraph, trips: this.trips, tick }));
    }
    const publication = packGuestPublication(guests.map((guest) => ({ guestId: guest.ordinal, x: 0, y: 0,
      elevation: 0, statusCode: guest.status === 'departed' ? 2 : guest.status === 'scheduled' ? 0 : 1,
      satisfaction: Math.round(Math.max(0, Math.min(1, guest.satisfaction)) * 255) })),
    { tick, sequence: tick, environmentRevision, topologyRevision });
    const visitDay = Math.floor(tick / 86_400);
    const repeatVisitors = recordRepeatVisitorVisits(this.initialRepeatVisitors, guests
      .filter((guest) => guest.status === 'departed').map((guest) => ({ visitId: `visit:${guest.id}:${visitDay}`,
        visitorKey: guest.id, day: visitDay, portalId: guest.portalId,
        ...(this.lodgingGuestIds.has(guest.id) ? { lodgingId: 'external-lodging' } : {}), satisfaction: guest.satisfaction })));
    return Object.freeze({ initialFacilities: this.initialFacilities, initialRepeatVisitors: this.initialRepeatVisitors,
      amenities: this.amenities.snapshot(), access: this.accessCache,
      lodging: this.lodging, repeatVisitors,
      scaling: Object.freeze({ bytesPerGuest: GUEST_PUBLICATION_BYTES_PER_GUEST,
        publicationBytes: publication.byteLength, publicationChecksum: checksumGuestPublication(publication),
        maxPublishedGuests: DEFAULT_GUEST_PUBLICATION_POLICY.maxPublishedGuests,
        degradationOrder: Object.freeze([...PHASE7_DEGRADATION_ORDER]) }) });
  }
}
