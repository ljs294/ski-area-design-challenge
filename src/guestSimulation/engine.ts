/**
 * Phase 1A guest simulation orchestration. Pure network, roster, and routing
 * support lives in engineSupport.ts; this class owns explicit-time state.
 */

import {
  GUEST_SIMULATION_CONTRACT_VERSION,
  GUEST_SIMULATION_PROTOCOL_VERSION,
  type GuestId,
  type GuestSimulationEnvironmentSnapshot,
  type GuestState,
  type PartyState,
  type SimulatedSecond,
  type ThoughtEvent,
} from './contracts.ts';
import { DEFAULT_GUEST_SIMULATION_CONFIG, type GuestSimulationConfig } from './config.ts';
import { eventCalendarChecksum, EventCalendar, type EventCalendarStateProjection } from './eventCalendar.ts';
import { GUEST_EVENT_PHASE, scheduleGuestEvent, type GuestEventPhase } from './eventPhases.ts';
import { createPartyPlan, createPartyRendezvous } from './party.ts';
import {
  DEFAULT_DAY_END,
  DEFAULT_GUEST_COUNT,
  bounded,
  chooseWeightedGuestItinerary,
  compareId,
  createDailyGuestRoster,
  createDefaultGuestSimulationNetwork,
  createGuestSimulationNetwork,
  freezeArray,
  guestAbilityTargets,
  liftDispatchInterval,
  memberProfile,
  type GuestSimulationEngineOptions,
  type GuestSimulationEngineSnapshot,
  type GuestSimulationEventPayload,
  type GuestSimulationNetwork,
  type GuestSimulationPendingEvent,
  type GuestItinerary,
  type GuestLift,
  type GuestAbilityTargets,
  type GuestSimulationMetrics,
  type DailyGuestRoster,
  edgeById,
  activeIncident,
  edgeOpenAt,
  portalOpenAt,
  tick,
} from './engineSupport.ts';
import type { PartyPlan, PartyRendezvousPlan } from './party.ts';

export {
  chooseWeightedGuestItinerary,
  createDailyGuestRoster,
  createDefaultGuestSimulationNetwork,
  createGuestSimulationNetwork,
  guestAbilityTargets,
  placeGuestPortal,
} from './engineSupport.ts';
export type {
  GuestNetworkNodeKind,
  GuestNetworkEdgeKind,
  GuestNetworkEdge,
  GuestNetworkNode,
  GuestLift,
  GuestPortalConnection,
  GuestSimulationNetwork,
  GuestPortalPlacementInput,
  GuestNetworkInput,
  GuestItinerary,
  GuestAbilityTargets,
  DailyRosterOptions,
  DailyGuestRoster,
  LiftSeatLedger,
  GuestSimulationMetrics,
  GuestSimulationEngineSnapshot,
  GuestSimulationPendingEvent,
  GuestSimulationLiftQueue,
  GuestSimulationEngineOptions,
  GuestSimulationEventPayload,
} from './engineSupport.ts';

type EnginePayload = GuestSimulationEventPayload;

interface MutableGuest extends GuestState {
  status: GuestState['status'];
  currentPortalId: string | null;
  currentResourceId: string | null;
  satisfaction: number;
  pendingDeparture: boolean;
  decisionOrdinal: number;
}

interface MutableParty extends PartyState {
  status: PartyState['status'];
}

interface MutableLiftLedger {
  readonly lift: GuestLift;
  readonly queue: GuestId[];
  readonly partyOrder: string[];
  partyCursor: number;
  readonly inTransit: Set<GuestId>;
  dispatches: number;
  completedRides: number;
}

function environmentAt(base: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond): GuestSimulationEnvironmentSnapshot {
  return Object.freeze({ ...base, tick: tickValue, conditions: Object.freeze({ ...base.conditions, tick: tickValue }),
    operating: base.operating });
}

function immutableGuest(guest: MutableGuest): GuestState {
  const { pendingDeparture: _pendingDeparture, decisionOrdinal: _decisionOrdinal, ...state } = guest;
  return Object.freeze({ ...state });
}

function checksumProjection(snapshot: Omit<GuestSimulationEngineSnapshot, 'checksum'>): string {
  return eventCalendarChecksum({ tick: snapshot.tick, guests: snapshot.guests.map((guest) => ({ id: guest.id, status: guest.status,
    currentResourceId: guest.currentResourceId, satisfaction: guest.satisfaction })),
  parties: snapshot.parties.map((party) => ({ id: party.id, status: party.status })),
  itineraries: snapshot.itineraries.map((itinerary) => ({ id: itinerary.id, guestId: itinerary.guestId })),
  metrics: snapshot.metrics,
  pendingEvents: snapshot.pendingEvents,
  liftQueues: snapshot.liftQueues,
  decisionOrdinals: snapshot.decisionOrdinals,
  partyPlans: snapshot.partyPlans,
  rendezvousPlans: snapshot.rendezvousPlans,
  });
}

/** Pure, explicit-time simulation controller for the Phase 1A slice. */
export class GuestSimulationEngine {
  readonly network: GuestSimulationNetwork;
  readonly roster: DailyGuestRoster;
  readonly config: GuestSimulationConfig;
  readonly runId: string;
  private readonly calendar: EventCalendar<EnginePayload>;
  private readonly guestsById = new Map<GuestId, MutableGuest>();
  private readonly partiesById = new Map<string, MutableParty>();
  private readonly abilityTargetsByGuest = new Map<GuestId, GuestAbilityTargets>();
  private readonly itinerariesByGuest = new Map<GuestId, GuestItinerary>();
  private readonly partyItinerariesByParty = new Map<string, GuestItinerary>();
  private readonly partyPlansByParty = new Map<string, PartyPlan>();
  private readonly rendezvousPlansByParty = new Map<string, PartyRendezvousPlan>();
  private readonly liftsById = new Map<string, MutableLiftLedger>();
  private readonly portalEntriesByTick = new Map<string, number>();
  private readonly thoughtEventsInternal: ThoughtEvent[] = [];
  private sequence = 0;
  private tickValue: SimulatedSecond;
  private environmentValue: GuestSimulationEnvironmentSnapshot;

  constructor(options: GuestSimulationEngineOptions) {
    this.network = createGuestSimulationNetwork(options.network);
    this.roster = options.roster;
    this.config = options.config ?? DEFAULT_GUEST_SIMULATION_CONFIG;
    this.runId = options.runId ?? `guest-run-${this.roster.seed}`;
    if (this.roster.guests.length > this.config.maxGuests || this.roster.parties.length > this.config.maxParties) {
      throw new RangeError('roster exceeds configured simulation bounds');
    }
    this.environmentValue = options.environment ?? Object.freeze({
      version: GUEST_SIMULATION_CONTRACT_VERSION,
      tick: this.roster.demandPlan.startTick,
      environmentRevision: 1,
      topologyRevision: 1,
      operating: true,
      conditions: Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, tick: this.roster.demandPlan.startTick,
        status: 'good' as const, trend: 'stable' as const, temperatureC: -2, windKph: 10, visibilityKm: 20,
        precipitationMm: 0, snowfallCm: 0, terrainOpenFraction: 1, liftOpenFraction: 1, trailOpenFraction: 1 }),
      portals: this.network.portals, incidents: freezeArray([]),
    });
    this.tickValue = this.environmentValue.tick;
    tick(this.tickValue, 'environment tick');
    this.calendar = new EventCalendar<EnginePayload>(this.tickValue);
    for (const guest of this.roster.guests) {
      const mutable: MutableGuest = { ...guest, status: 'scheduled', currentPortalId: guest.portalId,
        currentResourceId: null, satisfaction: 1, pendingDeparture: false, decisionOrdinal: 0 };
      this.guestsById.set(guest.id, mutable);
      this.abilityTargetsByGuest.set(guest.id, guestAbilityTargets(guest, this.roster.seed));
      scheduleGuestEvent(this.calendar, { dueTick: guest.arrivalTick, phase: GUEST_EVENT_PHASE.bookingsArrivals,
        ownerId: guest.id, guestId: guest.id, payload: { kind: 'arrival', guestId: guest.id } });
      const departureTick = guest.plannedDepartureTick ?? this.roster.demandPlan.endTick;
      scheduleGuestEvent(this.calendar, { dueTick: Math.max(this.tickValue, departureTick), phase: GUEST_EVENT_PHASE.thresholdsDeparturesRouteFailures,
        ownerId: guest.id, guestId: guest.id, payload: { kind: 'depart', guestId: guest.id } });
    }
    for (const party of this.roster.parties) {
      this.partiesById.set(party.id, { ...party, status: 'arriving' });
    }
    for (const lift of this.network.lifts) {
      const ledger: MutableLiftLedger = { lift, queue: [], partyOrder: [], partyCursor: 0, inTransit: new Set(), dispatches: 0, completedRides: 0 };
      this.liftsById.set(lift.id, ledger);
      const dispatchEnd = this.dispatchEnd(lift);
      const firstDispatchTick = Math.max(this.tickValue, lift.openFromTick ?? this.tickValue);
      if (firstDispatchTick <= dispatchEnd) {
        scheduleGuestEvent(this.calendar, { dueTick: firstDispatchTick, phase: GUEST_EVENT_PHASE.capacityDispatch,
          ownerId: lift.id, guestId: '', payload: { kind: 'lift-dispatch', liftId: lift.id } });
      }
    }
  }

  get currentTick(): SimulatedSecond { return this.tickValue; }

  get tick(): SimulatedSecond { return this.tickValue; }

  /** Advance through scheduled second-resolution events. */
  advanceTo(toTick: SimulatedSecond): GuestSimulationEngineSnapshot {
    tick(toTick, 'toTick');
    if (toTick < this.tickValue) throw new RangeError('toTick cannot move backwards');
    this.calendar.advanceTo(toTick, (event) => {
      this.tickValue = event.tick;
      this.handle(event.payload);
    });
    this.tickValue = toTick;
    return this.snapshot();
  }

  advanceBy(seconds: SimulatedSecond): GuestSimulationEngineSnapshot {
    tick(seconds, 'seconds');
    return this.advanceTo(this.tickValue + seconds);
  }

  advance(toTick: SimulatedSecond): GuestSimulationEngineSnapshot { return this.advanceTo(toTick); }

  getGuest(guestId: GuestId): GuestState | undefined {
    const guest = this.guestsById.get(guestId);
    return guest ? immutableGuest(guest) : undefined;
  }

  getItinerary(guestId: GuestId): GuestItinerary | undefined { return this.itinerariesByGuest.get(guestId); }

  getThoughtEvents(): readonly ThoughtEvent[] { return freezeArray(this.thoughtEventsInternal); }

  snapshot(): GuestSimulationEngineSnapshot {
    const guests = freezeArray([...this.guestsById.values()].sort(compareId).map(immutableGuest));
    const parties = freezeArray([...this.partiesById.values()].sort(compareId).map((party) => Object.freeze({ ...party })));
    const environment = environmentAt(this.environmentValue, this.tickValue);
    const metrics = this.metricsFor(guests);
    const base = {
      version: GUEST_SIMULATION_CONTRACT_VERSION,
      protocolVersion: GUEST_SIMULATION_PROTOCOL_VERSION,
      configVersion: this.config.configVersion,
      runId: this.runId,
      tick: this.tickValue,
      sequence: this.sequence,
      environmentRevision: environment.environmentRevision,
      topologyRevision: environment.topologyRevision,
      guests, parties, demandPlan: this.roster.demandPlan, environment,
      incidents: environment.incidents,
      thoughtEvents: freezeArray(this.thoughtEventsInternal.slice(-this.config.maxThoughtEventsPerSnapshot)),
      futureParty: null,
      network: this.network,
      itineraries: freezeArray([...this.itinerariesByGuest.values()].sort((left, right) => left.guestId.localeCompare(right.guestId))),
      abilityTargets: freezeArray([...this.abilityTargetsByGuest.entries()].sort(([left], [right]) => left.localeCompare(right))
        .map(([guestId, targets]) => Object.freeze({ guestId, targets }))),
      metrics,
      pendingEvents: this.pendingEvents(),
      liftQueues: freezeArray([...this.liftsById.values()].sort((left, right) => left.lift.id.localeCompare(right.lift.id)).map((ledger) => Object.freeze({
        liftId: ledger.lift.id, queuedGuestIds: freezeArray(ledger.queue), ridersInTransit: freezeArray([...ledger.inTransit].sort()),
      }))),
      decisionOrdinals: freezeArray([...this.guestsById.values()].sort(compareId).map((guest) => Object.freeze({ guestId: guest.id, ordinal: guest.decisionOrdinal }))),
      partyPlans: freezeArray([...this.partyPlansByParty.values()].sort((left, right) => left.partyId.localeCompare(right.partyId))),
      rendezvousPlans: freezeArray([...this.rendezvousPlansByParty.values()].sort((left, right) => left.partyId.localeCompare(right.partyId))),
    } satisfies Omit<GuestSimulationEngineSnapshot, 'checksum'>;
    const checksum = checksumProjection(base);
    return Object.freeze({ ...base, checksum });
  }

  getMetrics(): GuestSimulationMetrics { return this.snapshot().metrics; }

  private metricsFor(guests: readonly GuestState[]): GuestSimulationMetrics {
    const scheduled = guests.filter((guest) => guest.status === 'scheduled' || guest.status === 'arriving').length;
    const departed = guests.filter((guest) => guest.status === 'departed').length;
    const active = guests.length - scheduled - departed;
    const arrived = guests.length - scheduled;
    const liftSeats = freezeArray([...this.liftsById.values()].sort((a, b) => a.lift.id.localeCompare(b.lift.id)).map((ledger) => Object.freeze({
      liftId: ledger.lift.id, capacitySeats: ledger.lift.capacitySeats, dispatches: ledger.dispatches,
      completedRides: ledger.completedRides, ridersInTransit: ledger.inTransit.size, queuedGuests: ledger.queue.length,
    })));
    return Object.freeze({ population: guests.length, scheduled, arrived, active, departed, liftSeats,
      liftSeatsConserved: liftSeats.every((seat) => seat.dispatches === seat.completedRides + seat.ridersInTransit) });
  }

  private handle(payload: EnginePayload): void {
    switch (payload.kind) {
      case 'arrival': this.handleArrival(payload.guestId); break;
      case 'portal-retry': this.handleArrival(payload.guestId); break;
      case 'reach-lift': this.handleReachLift(payload.guestId); break;
      case 'lift-dispatch': this.handleLiftDispatch(payload.liftId); break;
      case 'ride-complete': this.handleRideComplete(payload.guestId); break;
      case 'descent-complete': this.handleDescentComplete(payload.guestId); break;
      case 'decide': this.handleDecision(payload.guestId); break;
      case 'depart': this.handleDeparture(payload.guestId); break;
    }
  }

  private appendThought(guest: MutableGuest, kind: ThoughtEvent['kind'], sentiment: ThoughtEvent['sentiment'], text: string): void {
    if (this.thoughtEventsInternal.length >= this.config.maxThoughtEventsPerSnapshot) return;
    this.sequence += 1;
    this.thoughtEventsInternal.push(Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION,
      id: `thought-${String(this.sequence).padStart(8, '0')}`, tick: this.tickValue,
      guestId: guest.id, partyId: guest.partyId, kind, sentiment, text }));
  }

  private handleArrival(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status === 'departed') return;
    const portal = this.network.portals.find((candidate) => candidate.id === guest.portalId);
    if (!portal || !portalOpenAt(portal, this.environmentValue, this.tickValue)) {
      guest.status = 'arriving';
      this.appendThought(guest, 'waiting', 'neutral', 'Waiting for an open guest entrance.');
      this.scheduleRetry(guest);
      return;
    }
    const entryKey = `${portal.id}:${this.tickValue}`;
    const used = this.portalEntriesByTick.get(entryKey) ?? 0;
    if (used >= portal.capacityGuestsPerTick) {
      guest.status = 'arriving';
      this.scheduleRetry(guest);
      return;
    }
    this.portalEntriesByTick.set(entryKey, used + 1);
    guest.status = 'choosing';
    guest.currentPortalId = portal.id;
    this.partiesById.get(guest.partyId)!.status = 'active';
    this.appendThought(guest, 'arrived', 'positive', 'Arrived at the resort.');
    this.scheduleDecision(guest, this.tickValue);
  }

  private scheduleRetry(guest: MutableGuest): void {
    const nextTick = this.tickValue + 1;
    if (nextTick >= (guest.plannedDepartureTick ?? this.roster.demandPlan.endTick)) return;
    scheduleGuestEvent(this.calendar, { dueTick: nextTick, phase: GUEST_EVENT_PHASE.bookingsArrivals,
      ownerId: guest.id, guestId: guest.id, payload: { kind: 'portal-retry', guestId: guest.id } });
  }

  private scheduleDecision(guest: MutableGuest, dueTick: SimulatedSecond): void {
    scheduleGuestEvent(this.calendar, { dueTick, phase: GUEST_EVENT_PHASE.decisionsEnqueue,
      ownerId: guest.id, guestId: guest.id, payload: { kind: 'decide', guestId: guest.id } });
  }

  /**
   * Select once for the party leader, constrained by the weakest member, then
   * hand the same lift/trail itinerary to every member.  Members retain their
   * own target rating and event identity, but never split onto an unsafe
   * route.  A follower waits briefly if the leader has not reached a decision
   * yet, which keeps arrival waves cohesive without deadlocking a malformed
   * roster.
   */
  private sharedItineraryFor(guest: MutableGuest, queueLengths: ReadonlyMap<string, number>): GuestItinerary | null {
    const party = this.partiesById.get(guest.partyId);
    if (!party) return null;
    const shared = this.partyItinerariesByParty.get(party.id);
    const edges = edgeById(this.network);
    if (shared) {
      const liftEdge = edges.get(shared.liftEdgeId);
      const descentEdge = edges.get(shared.descentEdgeId);
      if (liftEdge && descentEdge && edgeOpenAt(this.network, liftEdge, this.environmentValue, this.tickValue)
        && edgeOpenAt(this.network, descentEdge, this.environmentValue, this.tickValue)) {
        return Object.freeze({ ...shared, guestId: guest.id,
          targetRating: this.abilityTargetsByGuest.get(guest.id)?.targetRating ?? shared.targetRating,
          decisionOrdinal: guest.decisionOrdinal });
      }
      this.partyItinerariesByParty.delete(party.id);
      this.partyPlansByParty.delete(party.id);
      this.rendezvousPlansByParty.delete(party.id);
    }
    const leaderId = party.guestIds[0];
    if (leaderId !== guest.id) return null;
    const members = party.guestIds.map((memberId) => this.guestsById.get(memberId)).filter((member): member is MutableGuest => member !== undefined);
    if (members.length !== party.guestIds.length) return null;
    const weakestAbility = Math.min(...members.map((member) => member.preferences.ability));
    const selected = chooseWeightedGuestItinerary(this.network, guest, this.roster.seed, this.environmentValue,
      this.tickValue, guest.decisionOrdinal, queueLengths, weakestAbility);
    if (!selected) return null;
    const routeMinimumAbility = selected.routeMinimumAbility ?? selected.targetRating;
    const plan = createPartyPlan({ party, members: members.map(memberProfile), worldSeed: this.roster.seed, tick: this.tickValue,
      routes: [{ id: selected.id, liftId: selected.liftId, trailId: selected.descentEdgeId,
        minimumAbility: routeMinimumAbility, leaderAppeal: selected.weight }] });
    if (!plan.canProceed || plan.selectedRouteId !== selected.id) return null;
    const sharedItinerary = Object.freeze({ ...selected, guestId: leaderId, partyId: party.id,
      routeMinimumAbility, ownership: 'leader-owned-shared-plan' as const });
    this.partyItinerariesByParty.set(party.id, sharedItinerary);
    this.partyPlansByParty.set(party.id, plan);
    const rendezvous = createPartyRendezvous({ party, locationId: `lift:${selected.liftId}:top`,
      targetTick: this.tickValue + selected.travelToLiftSeconds + selected.rideSeconds });
    this.rendezvousPlansByParty.set(party.id, rendezvous);
    return Object.freeze({ ...sharedItinerary, guestId: guest.id, targetRating: this.abilityTargetsByGuest.get(guest.id)?.targetRating ?? selected.targetRating,
      decisionOrdinal: guest.decisionOrdinal });
  }

  private handleDecision(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status === 'departed' || guest.pendingDeparture) return;
    if (this.tickValue >= (guest.plannedDepartureTick ?? this.roster.demandPlan.endTick)) {
      this.handleDeparture(guest.id);
      return;
    }
    const queueLengths = new Map([...this.liftsById.entries()].map(([id, ledger]) => [id, ledger.queue.length]));
    const itinerary = this.sharedItineraryFor(guest, queueLengths);
    if (!itinerary) {
      guest.status = 'choosing';
      guest.satisfaction = bounded(guest.satisfaction - 0.001);
      this.appendThought(guest, 'concerned', 'negative', 'No open route is available right now.');
      this.scheduleDecision(guest, this.tickValue + 1);
      return;
    }
    guest.decisionOrdinal += 1;
    this.itinerariesByGuest.set(guest.id, itinerary);
    guest.status = 'travelling-to-lift';
    guest.currentResourceId = itinerary.liftId;
    scheduleGuestEvent(this.calendar, { dueTick: this.tickValue + itinerary.travelToLiftSeconds,
      phase: GUEST_EVENT_PHASE.dueTravelServiceCompletions, ownerId: itinerary.liftId, guestId: guest.id,
      payload: { kind: 'reach-lift', guestId: guest.id } });
  }

  private handleReachLift(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    const itinerary = this.itinerariesByGuest.get(guestId);
    if (!guest || !itinerary || guest.status !== 'travelling-to-lift') return;
    const lift = this.liftsById.get(itinerary.liftId);
    const edges = edgeById(this.network);
    const liftEdge = edges.get(itinerary.liftEdgeId);
    const descentEdge = edges.get(itinerary.descentEdgeId);
    if (!lift || !liftEdge || !descentEdge || !edgeOpenAt(this.network, liftEdge, this.environmentValue, this.tickValue)
      || !edgeOpenAt(this.network, descentEdge, this.environmentValue, this.tickValue)) {
      this.itinerariesByGuest.delete(guest.id);
      this.partyItinerariesByParty.delete(guest.partyId);
      this.partyPlansByParty.delete(guest.partyId);
      this.rendezvousPlansByParty.delete(guest.partyId);
      guest.status = 'choosing';
      guest.currentResourceId = null;
      this.appendThought(guest, 'concerned', 'negative', 'The selected route closed before I reached the lift.');
      this.scheduleDecision(guest, this.tickValue + 1);
      return;
    }
    guest.status = 'lift-queue';
    if (!lift.partyOrder.includes(guest.partyId)) lift.partyOrder.push(guest.partyId);
    lift.queue.push(guest.id);
    this.appendThought(guest, 'queueing', 'neutral', 'Joined the lift queue.');
  }

  private handleLiftDispatch(liftId: string): void {
    const ledger = this.liftsById.get(liftId);
    if (!ledger) return;
    if (this.liftOpen(ledger.lift)) {
      // One physical chair is dispatched per event. Members of one party are
      // kept together in consecutive chairs, while the cursor rotates across
      // parties so an oversized group cannot starve later arrivals.
      let selectedPartyIndex = -1;
      let selectedMembers: GuestId[] = [];
      for (let offset = 0; offset < ledger.partyOrder.length; offset += 1) {
        const index = (ledger.partyCursor + offset) % ledger.partyOrder.length;
        const partyId = ledger.partyOrder[index]!;
        const members = ledger.queue.filter((guestId) => {
          const member = this.guestsById.get(guestId);
          return member?.partyId === partyId && member.status === 'lift-queue' && !member.pendingDeparture;
        });
        if (members.length > 0) {
          selectedPartyIndex = index;
          selectedMembers = members.slice(0, ledger.lift.capacitySeats);
          break;
        }
      }
      if (selectedPartyIndex >= 0) {
        const selected = new Set(selectedMembers);
        for (let index = ledger.queue.length - 1; index >= 0; index -= 1) {
          if (selected.has(ledger.queue[index]!)) ledger.queue.splice(index, 1);
        }
        for (const guestId of selectedMembers) {
          const guest = this.guestsById.get(guestId);
          if (!guest) continue;
          guest.status = 'lift-ride';
          guest.currentResourceId = ledger.lift.id;
          ledger.inTransit.add(guest.id);
          ledger.dispatches += 1;
          this.appendThought(guest, 'riding', 'positive', 'Riding the lift uphill.');
          const itinerary = this.itinerariesByGuest.get(guest.id);
          if (!itinerary) continue;
          scheduleGuestEvent(this.calendar, { dueTick: this.tickValue + ledger.lift.rideSeconds,
            phase: GUEST_EVENT_PHASE.dueTravelServiceCompletions, ownerId: ledger.lift.id, guestId: guest.id,
            payload: { kind: 'ride-complete', guestId: guest.id } });
        }
        ledger.partyCursor = ledger.partyOrder.length === 0 ? 0 : (selectedPartyIndex + 1) % ledger.partyOrder.length;
      }
    }
    const nextTick = this.tickValue + liftDispatchInterval(ledger.lift);
    if (nextTick <= this.dispatchEnd(ledger.lift)) {
      scheduleGuestEvent(this.calendar, { dueTick: nextTick, phase: GUEST_EVENT_PHASE.capacityDispatch,
        ownerId: ledger.lift.id, guestId: '', payload: { kind: 'lift-dispatch', liftId: ledger.lift.id } });
    }
  }

  private dispatchEnd(lift: GuestLift): SimulatedSecond {
    return Math.max(this.roster.demandPlan.endTick, lift.openUntilTick ?? this.roster.demandPlan.endTick);
  }

  private pendingEvents(): readonly GuestSimulationPendingEvent[] {
    const projection: EventCalendarStateProjection<EnginePayload> = this.calendar.stateProjection();
    return freezeArray(projection.events.map((event) => Object.freeze({
      tick: event.tick, phase: event.phase as GuestEventPhase | undefined, ownerId: event.entityId,
      guestId: event.key, payload: Object.freeze({ ...event.payload }), generation: event.generation,
      insertionSequence: event.insertionSequence,
    })));
  }

  private liftOpen(lift: GuestLift): boolean {
    if (lift.openFromTick !== undefined && this.tickValue < lift.openFromTick) return false;
    if (lift.openUntilTick !== undefined && this.tickValue >= lift.openUntilTick) return false;
    return !activeIncident(this.environmentValue, this.tickValue, lift.id, 'lift-closure');
  }

  private handleRideComplete(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    const itinerary = this.itinerariesByGuest.get(guestId);
    const ledger = itinerary ? this.liftsById.get(itinerary.liftId) : undefined;
    if (!guest || !itinerary || !ledger || guest.status !== 'lift-ride') return;
    ledger.inTransit.delete(guest.id);
    ledger.completedRides += 1;
    guest.status = 'skiing';
    guest.currentResourceId = itinerary.descentEdgeId;
    this.appendThought(guest, 'skiing', 'positive', 'The descent looks good.');
    scheduleGuestEvent(this.calendar, { dueTick: this.tickValue + itinerary.descentSeconds,
      phase: GUEST_EVENT_PHASE.dueTravelServiceCompletions, ownerId: itinerary.descentEdgeId, guestId: guest.id,
      payload: { kind: 'descent-complete', guestId: guest.id } });
  }

  private handleDescentComplete(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status !== 'skiing') return;
    guest.status = 'appraising';
    guest.currentResourceId = null;
    const itinerary = this.itinerariesByGuest.get(guest.id);
    if (itinerary) {
      const ratingGap = Math.abs(itinerary.targetRating - 0.5);
      guest.satisfaction = bounded(guest.satisfaction + 0.01 - ratingGap * 0.005);
    }
    if (guest.pendingDeparture || this.tickValue >= (guest.plannedDepartureTick ?? this.roster.demandPlan.endTick)) {
      this.markDeparted(guest);
      return;
    }
    this.appendThought(guest, 'waiting', 'neutral', 'Taking a short break before the next run.');
    this.scheduleDecision(guest, this.tickValue + 1);
  }

  private handleDeparture(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status === 'departed') return;
    guest.pendingDeparture = true;
    if (guest.status === 'lift-ride') return;
    const ledger = guest.currentResourceId ? this.liftsById.get(guest.currentResourceId) : undefined;
    if (ledger) {
      const index = ledger.queue.indexOf(guest.id);
      if (index >= 0) ledger.queue.splice(index, 1);
    }
    this.markDeparted(guest);
  }

  private markDeparted(guest: MutableGuest): void {
    if (guest.status === 'departed') return;
    if (guest.status === 'lift-ride') {
      guest.pendingDeparture = true;
      return;
    }
    guest.status = 'departed';
    guest.currentPortalId = null;
    guest.currentResourceId = null;
    this.itinerariesByGuest.delete(guest.id);
    this.appendThought(guest, 'leaving', 'neutral', 'Leaving the resort cleanly.');
    const party = this.partiesById.get(guest.partyId);
    if (party && party.guestIds.every((id) => this.guestsById.get(id)?.status === 'departed')) party.status = 'departed';
  }
}

export function createGuestSimulationEngine(options: GuestSimulationEngineOptions): GuestSimulationEngine {
  return new GuestSimulationEngine(options);
}

export const createSimulationEngine = createGuestSimulationEngine;
export const createGuestSimulation = createGuestSimulationEngine;

/** Functional faÃƒÂ§ade for adapters that keep the engine instance in their own state. */
export function advanceGuestSimulation(engine: GuestSimulationEngine, toTick: SimulatedSecond): GuestSimulationEngineSnapshot {
  return engine.advanceTo(toTick);
}

export function snapshotGuestSimulation(engine: GuestSimulationEngine): GuestSimulationEngineSnapshot {
  return engine.snapshot();
}

/** Small default network useful for unit tests and a first UI adapter. */

export function createDefaultGuestSimulation(options: Omit<import('./engineSupport.ts').DailyRosterOptions, 'portals'> & { readonly portals?: readonly import('./contracts.ts').GuestPortal[] } = {
  seed: 'phase-1a', guestCount: DEFAULT_GUEST_COUNT, portals: [],
}): GuestSimulationEngine {
  const portals = options.portals && options.portals.length > 0 ? options.portals : [{
    version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'guest-entrance-1', kind: 'guest-entrance' as const,
    type: 'guest-entrance' as const, semantics: 'guest-entrance' as const, direction: 'inbound' as const,
    accepts: 'guests' as const, label: 'Guest Entrance 1', capacityGuestsPerTick: 4, openFromTick: 0, openUntilTick: DEFAULT_DAY_END,
  }];
  const network = createDefaultGuestSimulationNetwork(portals);
  const roster = createDailyGuestRoster({ ...options, guestCount: options.guestCount ?? DEFAULT_GUEST_COUNT, portals });
  return createGuestSimulationEngine({ network, roster });
}
