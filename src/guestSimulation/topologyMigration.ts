import type { GuestId, GuestSimulationEnvironmentSnapshot, SimulatedSecond } from './contracts.ts';
import { createConditionSnapshot, type ConditionSnapshot } from './conditions.ts';
import type { EventCalendar } from './eventCalendar.ts';
import { GUEST_EVENT_PHASE, scheduleGuestEvent } from './eventPhases.ts';
import type { ExperienceThoughtReasonCode } from './experience.ts';
import type { MutableGuest, MutableLiftLedger } from './engineState.ts';
import {
  createGuestSimulationNetwork,
  edgeById,
  edgeOpenAt,
  freezeArray,
  tick,
  type GuestItinerary,
  type GuestSimulationEventPayload,
  type GuestSimulationNetwork,
} from './engineSupport.ts';

export interface GuestTopologyMigrationResult {
  readonly topologyRevision: number;
  readonly preservedGuestIds: readonly GuestId[];
  readonly reroutedGuestIds: readonly GuestId[];
  readonly waitingGuestIds: readonly GuestId[];
}

interface DeleteMap { delete(key: string): boolean }

export interface TopologyMigrationInput {
  readonly networkInput: GuestSimulationNetwork;
  readonly topologyRevision: number;
  readonly tickValue: SimulatedSecond;
  readonly environmentValue: GuestSimulationEnvironmentSnapshot;
  readonly conditionSnapshotValue: ConditionSnapshot;
  readonly conditionHistory: ConditionSnapshot[];
  readonly guestsById: Map<GuestId, MutableGuest>;
  readonly itinerariesByGuest: Map<GuestId, GuestItinerary>;
  readonly partyItinerariesByParty: Map<string, GuestItinerary>;
  readonly partyPlansByParty: DeleteMap;
  readonly rendezvousPlansByParty: DeleteMap;
  readonly liftsById: Map<string, MutableLiftLedger>;
  readonly calendar: EventCalendar<GuestSimulationEventPayload>;
  readonly rosterEndTick: SimulatedSecond;
  readonly dispatchEnd: (liftId: string) => SimulatedSecond;
  readonly activateTopology: (network: GuestSimulationNetwork,
    environment: GuestSimulationEnvironmentSnapshot, conditions: ConditionSnapshot) => void;
  readonly appendThought: (guest: MutableGuest, reason: ExperienceThoughtReasonCode) => void;
  readonly handleDecision: (guestId: GuestId) => void;
}

export interface TopologyMigrationState {
  readonly network: GuestSimulationNetwork;
  readonly environment: GuestSimulationEnvironmentSnapshot;
  readonly conditionSnapshot: ConditionSnapshot;
  readonly migration: GuestTopologyMigrationResult;
}

function itineraryUsable(
  network: GuestSimulationNetwork,
  environment: GuestSimulationEnvironmentSnapshot,
  at: SimulatedSecond,
  itinerary: GuestItinerary,
): boolean {
  const edges = edgeById(network);
  const lift = network.lifts.find((candidate) => candidate.id === itinerary.liftId);
  const liftEdge = edges.get(itinerary.liftEdgeId);
  const descentEdge = edges.get(itinerary.descentEdgeId);
  if (!lift || !liftEdge || !descentEdge || lift.edgeId !== liftEdge.id
    || liftEdge.liftId !== lift.id || liftEdge.fromNodeId !== lift.baseNodeId
    || liftEdge.toNodeId !== lift.topNodeId) return false;
  if (!edgeOpenAt(network, liftEdge, environment, at)
    || !edgeOpenAt(network, descentEdge, environment, at)) return false;
  return itinerary.connectorEdgeIds.every((edgeId) => {
    const edge = edges.get(edgeId);
    return edge?.kind === 'connector' && edgeOpenAt(network, edge, environment, at);
  });
}

function releaseGuestRoute(input: TopologyMigrationInput, guest: MutableGuest): void {
  if (!input.itinerariesByGuest.has(guest.id)) return;
  for (const event of input.calendar.stateProjection().events) {
    if (event.key === guest.id) input.calendar.generationFor(event.entityId, event.key);
  }
  for (const ledger of input.liftsById.values()) {
    const queueIndex = ledger.queue.indexOf(guest.id);
    if (queueIndex >= 0) ledger.queue.splice(queueIndex, 1);
    if (ledger.inTransit.delete(guest.id)) ledger.completedRides += 1;
    const partyIndex = ledger.partyOrder.indexOf(guest.partyId);
    if (partyIndex >= 0 && !ledger.queue.some((id) => input.guestsById.get(id)?.partyId === guest.partyId)) {
      ledger.partyOrder.splice(partyIndex, 1);
      if (ledger.partyCursor >= ledger.partyOrder.length) ledger.partyCursor = 0;
    }
  }
  input.itinerariesByGuest.delete(guest.id);
  input.partyItinerariesByParty.delete(guest.partyId);
  input.partyPlansByParty.delete(guest.partyId);
  input.rendezvousPlansByParty.delete(guest.partyId);
  guest.currentResourceId = null;
  const departureTick = Math.max(input.tickValue,
    guest.plannedDepartureTick ?? input.rosterEndTick);
  scheduleGuestEvent(input.calendar, { dueTick: departureTick,
    phase: GUEST_EVENT_PHASE.thresholdsDeparturesRouteFailures,
    ownerId: guest.id, guestId: guest.id,
    payload: { kind: 'depart', guestId: guest.id } });
}

function rebuildConditions(input: TopologyMigrationInput, network: GuestSimulationNetwork): ConditionSnapshot {
  const prior = new Map(input.conditionSnapshotValue.edges.map((edge) => [edge.edgeId, edge]));
  return createConditionSnapshot({ revision: input.conditionSnapshotValue.revision + 1, tick: input.tickValue,
    edges: network.edges.map((edge) => {
      const previous = prior.get(edge.id);
      return { edgeId: edge.id, baseDifficulty: previous?.baseDifficulty ?? edge.targetRating ?? 0.25,
        grooming: previous?.grooming.quality ?? (edge.kind === 'descent' ? 0.5 : 1),
        snowQuality: previous?.snowQuality.quality ?? 0.75, coverage: previous?.coverage.fraction ?? 1,
        occupancy: { guests: previous?.occupancy.guests ?? 0,
          capacity: previous?.occupancy.capacity ?? (edge.kind === 'lift' ? edge.capacitySeats ?? 1 : 100) } };
    }) });
}

function reconcileLifts(input: TopologyMigrationInput, network: GuestSimulationNetwork): void {
  const next = new Map(network.lifts.map((lift) => [lift.id, lift]));
  for (const [liftId, ledger] of [...input.liftsById]) {
    const replacement = next.get(liftId);
    if (!replacement) {
      input.calendar.generationFor(liftId, '');
      input.liftsById.delete(liftId);
    } else {
      ledger.lift = replacement;
      next.delete(liftId);
    }
  }
  for (const lift of next.values()) {
    input.liftsById.set(lift.id, { lift, queue: [], partyOrder: [], partyCursor: 0,
      inTransit: new Set(), dispatches: 0, completedRides: 0 });
    const first = Math.max(input.tickValue, lift.openFromTick ?? input.tickValue);
    if (first <= input.dispatchEnd(lift.id)) {
      scheduleGuestEvent(input.calendar, { dueTick: first, phase: GUEST_EVENT_PHASE.capacityDispatch,
        ownerId: lift.id, guestId: '', payload: { kind: 'lift-dispatch', liftId: lift.id } });
    }
  }
}

export function replaceGuestTopology(input: TopologyMigrationInput): TopologyMigrationState {
  tick(input.topologyRevision, 'topologyRevision');
  if (input.topologyRevision === input.environmentValue.topologyRevision) {
    const empty = freezeArray<GuestId>([]);
    return { network: input.networkInput, environment: input.environmentValue,
      conditionSnapshot: input.conditionSnapshotValue,
      migration: { topologyRevision: input.topologyRevision, preservedGuestIds: empty,
        reroutedGuestIds: empty, waitingGuestIds: empty } };
  }
  const network = createGuestSimulationNetwork(input.networkInput);
  const environment = Object.freeze({ ...input.environmentValue, topologyRevision: input.topologyRevision,
    tick: input.tickValue, portals: network.portals,
    conditions: Object.freeze({ ...input.environmentValue.conditions, tick: input.tickValue }) });
  const conditions = rebuildConditions(input, network);
  const priorItineraries = new Map(input.itinerariesByGuest);
  const affectedParties = new Set<string>();
  for (const [guestId, itinerary] of priorItineraries) {
    if (!itineraryUsable(network, environment, input.tickValue, itinerary)) {
      affectedParties.add(input.guestsById.get(guestId)?.partyId ?? '');
    }
  }
  affectedParties.delete('');
  reconcileLifts(input, network);
  input.activateTopology(network, environment, conditions);
  const preserved: GuestId[] = [], rerouted: GuestId[] = [], waiting: GuestId[] = [];
  for (const guest of [...input.guestsById.values()].sort((a, b) => a.id.localeCompare(b.id))) {
    if (guest.status === 'departed') continue;
    const old = priorItineraries.get(guest.id);
    const mustRetry = (old !== undefined && affectedParties.has(guest.partyId))
      || (old === undefined && guest.status === 'waiting-for-route');
    if (mustRetry) {
      if (old) releaseGuestRoute(input, guest);
      guest.status = 'waiting-for-route';
      guest.routeStateReason = 'topology-changed-reroute-pending';
      input.appendThought(guest, 'waiting');
      input.handleDecision(guest.id);
      (guest.status === 'waiting-for-route' ? waiting : rerouted).push(guest.id);
    } else if (old && itineraryUsable(network, environment, input.tickValue, old)) {
      preserved.push(guest.id);
    }
  }
  input.conditionHistory.push(conditions);
  return Object.freeze({ network, environment, conditionSnapshot: conditions,
    migration: Object.freeze({ topologyRevision: input.topologyRevision,
      preservedGuestIds: freezeArray(preserved), reroutedGuestIds: freezeArray(rerouted),
      waitingGuestIds: freezeArray(waiting) }) });
}
