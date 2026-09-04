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
import { assertConditionSnapshot, createConditionSnapshot,
  type ConditionSnapshot } from './conditions.ts';
import { aggregateThoughtsByReason, calculateCrowdingEffect, calculateSatisfactionChannels,
  calculateSuitableTerrainOutcome, evaluateEarlyDeparture, type EarlyDepartureDecision,
  type ExperienceThoughtReasonCode } from './experience.ts';
import { createPhase4SafetyRuntime, type Phase4SafetySnapshot } from './phase4Safety.ts';
import type { InjuryIncident } from './injury.ts';
import { Phase3Runtime } from './phase3Runtime.ts';
import { RemainingPhasesRuntime } from './remainingPhasesRuntime.ts';

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
  queueJoinedTick: SimulatedSecond | null;
  lastQueueWaitSeconds: number;
  traversalOrdinal: number;
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
  const { pendingDeparture: _pendingDeparture, decisionOrdinal: _decisionOrdinal,
    queueJoinedTick: _queueJoinedTick, lastQueueWaitSeconds: _lastQueueWaitSeconds,
    traversalOrdinal: _traversalOrdinal, ...state } = guest;
  return Object.freeze({ ...state });
}

function defaultThoughtReason(kind: ThoughtEvent['kind'], sentiment: ThoughtEvent['sentiment']): ExperienceThoughtReasonCode {
  if (sentiment === 'positive') return 'positive-experience';
  if (kind === 'arrived') return 'arrival';
  if (kind === 'concerned') return 'terrain-mismatch';
  return kind;
}

function checksumProjection(snapshot: Omit<GuestSimulationEngineSnapshot, 'checksum'>): string {
  return eventCalendarChecksum(snapshot);
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
  private readonly thoughtCountsInternal = new Map<string, { reasonCode: ExperienceThoughtReasonCode;
    sentiment: ThoughtEvent['sentiment']; count: number }>();
  private readonly earlyDeparturesInternal: { guestId: GuestId; tick: SimulatedSecond; decision: EarlyDepartureDecision }[] = [];
  private readonly conditionHistoryInternal: ConditionSnapshot[] = [];
  private readonly safetyRuntime;
  private readonly finalizedSafetyIncidents = new Set<string>();
  private readonly phase3Runtime: Phase3Runtime;
  private readonly remainingPhases: RemainingPhasesRuntime;
  private sequence = 0;
  private tickValue: SimulatedSecond;
  private environmentValue: GuestSimulationEnvironmentSnapshot;
  private conditionSnapshotValue: ConditionSnapshot;

  constructor(options: GuestSimulationEngineOptions) {
    this.network = createGuestSimulationNetwork(options.network);
    this.roster = options.roster;
    this.config = options.config ?? DEFAULT_GUEST_SIMULATION_CONFIG;
    this.runId = options.runId ?? `guest-run-${this.roster.seed}`;
    this.phase3Runtime = new Phase3Runtime(this.roster, options.phase3 ?? {
      dayId: `${this.roster.seed}:${this.roster.demandPlan.startTick}`,
      ticketPriceCents: 10_000,
    });
    this.remainingPhases = new RemainingPhasesRuntime(this.network, this.roster, options.phase5to7);
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
    this.safetyRuntime = createPhase4SafetyRuntime(this.network, this.tickValue);
    this.conditionSnapshotValue = options.conditionSnapshot ?? createConditionSnapshot({
      revision: 0, tick: this.tickValue, edges: this.network.edges.map((edge) => ({
        edgeId: edge.id, baseDifficulty: edge.targetRating ?? 0.25,
        grooming: edge.kind === 'descent' ? 0.5 : 1, snowQuality: 0.75, coverage: 1,
        occupancy: { guests: 0, capacity: edge.kind === 'lift' ? edge.capacitySeats ?? 1 : 100 },
      })),
    });
    assertConditionSnapshot(this.conditionSnapshotValue);
    if (this.conditionSnapshotValue.tick !== this.tickValue) throw new RangeError('initial condition snapshot tick must match environment tick');
    this.conditionHistoryInternal.push(this.conditionSnapshotValue);
    tick(this.tickValue, 'environment tick');
    this.calendar = new EventCalendar<EnginePayload>(this.tickValue);
    for (const guest of this.roster.guests) {
      const mutable: MutableGuest = { ...guest, status: 'scheduled', currentPortalId: guest.portalId,
        currentResourceId: null, satisfaction: 1, pendingDeparture: false, decisionOrdinal: 0,
        queueJoinedTick: null, lastQueueWaitSeconds: 0, traversalOrdinal: 0 };
      this.guestsById.set(guest.id, mutable);
      this.abilityTargetsByGuest.set(guest.id, guestAbilityTargets(guest, this.roster.seed));
      const accessArrivalTick = this.remainingPhases.arrivalTickFor(guest.id);
      if (accessArrivalTick !== null) scheduleGuestEvent(this.calendar, { dueTick: accessArrivalTick,
        phase: GUEST_EVENT_PHASE.bookingsArrivals, ownerId: guest.id, guestId: guest.id,
        payload: { kind: 'arrival', guestId: guest.id } });
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
    this.remainingPhases.advanceTo(toTick);
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

  /** Apply a revision at its exact tick before same-tick decisions are dispatched. */
  applyConditionSnapshot(snapshot: ConditionSnapshot): void {
    assertConditionSnapshot(snapshot);
    if (snapshot.tick < this.tickValue) throw new RangeError('condition snapshot cannot move simulation time backwards');
    const networkEdgeIds = new Set(this.network.edges.map((edge) => edge.id));
    if (snapshot.edges.length !== networkEdgeIds.size || snapshot.edges.some((edge) => !networkEdgeIds.has(edge.edgeId))) {
      throw new RangeError('condition snapshot must cover every network edge exactly once');
    }
    if (snapshot.tick === this.tickValue) {
      if (snapshot.checksum === this.conditionSnapshotValue.checksum) return;
      throw new RangeError('condition snapshot cannot replace already-observed same-tick conditions');
    }
    if (snapshot.revision <= this.conditionSnapshotValue.revision) throw new RangeError('condition revision must increase monotonically');
    this.advanceTo(snapshot.tick - 1);
    this.conditionSnapshotValue = snapshot;
    const previous = this.conditionHistoryInternal[this.conditionHistoryInternal.length - 1];
    if (!previous || previous.checksum !== snapshot.checksum) this.conditionHistoryInternal.push(snapshot);
  }

  snapshot(): GuestSimulationEngineSnapshot {
    const safety = this.syncSafetyState();
    const guests = freezeArray([...this.guestsById.values()].sort(compareId).map(immutableGuest));
    const parties = freezeArray([...this.partiesById.values()].sort(compareId).map((party) => Object.freeze({ ...party })));
    const environment = environmentAt(this.environmentValue, this.tickValue);
    const metrics = this.metricsFor(guests);
    const thoughtAggregation = aggregateThoughtsByReason([...this.thoughtCountsInternal.values()]);
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
      conditionSnapshot: this.conditionSnapshotValue,
      conditionHistory: freezeArray(this.conditionHistoryInternal),
      thoughtAggregation,
      earlyDepartures: freezeArray(this.earlyDeparturesInternal),
      safety,
      phase3: this.phase3Runtime.snapshot(this.tickValue, guests),
      phase5to7: this.remainingPhases.snapshot(this.tickValue, guests,
        environment.environmentRevision, environment.topologyRevision),
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
    this.syncSafetyState();
    switch (payload.kind) {
      case 'arrival': this.handleArrival(payload.guestId); break;
      case 'portal-retry': this.handleArrival(payload.guestId); break;
      case 'reach-lift': this.handleReachLift(payload.guestId); break;
      case 'lift-dispatch': this.handleLiftDispatch(payload.liftId); break;
      case 'ride-complete': this.handleRideComplete(payload.guestId); break;
      case 'descent-complete': this.handleDescentComplete(payload.guestId, payload.traversalId); break;
      case 'amenity-progress': this.handleAmenityProgress(payload.guestId, payload.requestId); break;
      case 'injury': this.handleInjury(payload.guestId, payload.incident); break;
      case 'decide': this.handleDecision(payload.guestId); break;
      case 'depart': this.handleDeparture(payload.guestId); break;
    }
  }

  private appendThought(guest: MutableGuest, kind: ThoughtEvent['kind'], sentiment: ThoughtEvent['sentiment'], text: string,
    reasonCode: ExperienceThoughtReasonCode = defaultThoughtReason(kind, sentiment), observedTick = this.tickValue): void {
    const aggregationKey = `${reasonCode}|${sentiment}`;
    const aggregate = this.thoughtCountsInternal.get(aggregationKey);
    if (aggregate) aggregate.count += 1;
    else this.thoughtCountsInternal.set(aggregationKey, { reasonCode, sentiment, count: 1 });
    if (this.thoughtEventsInternal.length >= this.config.maxThoughtEventsPerSnapshot) return;
    this.sequence += 1;
    this.thoughtEventsInternal.push(Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION,
      id: `thought-${String(this.sequence).padStart(8, '0')}`, tick: observedTick,
      guestId: guest.id, partyId: guest.partyId, kind, sentiment, text, reasonCode }));
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
      this.tickValue, guest.decisionOrdinal, queueLengths, weakestAbility, this.conditionSnapshotValue);
    if (!selected) return null;
    const routeMinimumAbility = selected.routeMinimumAbility ?? selected.targetRating;
    const plan = createPartyPlan({ party, members: members.map(memberProfile), worldSeed: this.roster.seed, tick: this.tickValue,
      routes: [{ id: selected.id, liftId: selected.liftId, trailId: selected.descentEdgeId,
        minimumAbility: routeMinimumAbility, leaderAppeal: selected.weight }], allowUnsafeSelection: true });
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
    guest.queueJoinedTick = this.tickValue;
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
          guest.lastQueueWaitSeconds = Math.max(0, this.tickValue - (guest.queueJoinedTick ?? this.tickValue));
          guest.queueJoinedTick = null;
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
    const condition = this.conditionSnapshotValue.edges.find((edge) => edge.edgeId === itinerary.descentEdgeId);
    const traversalOrdinal = guest.traversalOrdinal++;
    const traversalId = `${guest.id}:${itinerary.descentEdgeId}:${traversalOrdinal}`;
    const evaluation = this.safetyRuntime.evaluate({ worldSeed: this.roster.seed, guestId: guest.id,
      runId: itinerary.descentEdgeId, traversalId, entryTick: this.tickValue,
      durationSeconds: itinerary.descentSeconds, decisionOrdinal: traversalOrdinal,
      ability: guest.preferences.ability, effectiveDifficulty: condition?.effectiveDifficulty ?? itinerary.targetRating,
      traffic: condition?.occupancy.crowding.ratio ?? 0, coverage: condition?.coverage.fraction ?? 1,
      grooming: condition?.grooming.quality ?? 0.5,
      exposure: Math.min(1, 0.25 + guest.preferences.riskTolerance * 0.5) });
    const token = this.calendar.generationFor(traversalId, guest.id);
    const incident = evaluation.scheduledIncident;
    scheduleGuestEvent(this.calendar, { dueTick: incident?.incidentTick ?? this.tickValue + itinerary.descentSeconds,
      phase: incident ? GUEST_EVENT_PHASE.cancellationIncidents : GUEST_EVENT_PHASE.dueTravelServiceCompletions,
      ownerId: traversalId, guestId: guest.id, generationToken: token,
      payload: incident ? { kind: 'injury', guestId: guest.id, incident }
        : { kind: 'descent-complete', guestId: guest.id, traversalId } });
  }

  private handleDescentComplete(guestId: GuestId, traversalId: string): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status !== 'skiing') return;
    this.safetyRuntime.markNormal(traversalId);
    this.calendar.generationFor(traversalId, guest.id);
    guest.status = 'appraising';
    guest.currentResourceId = null;
    const itinerary = this.itinerariesByGuest.get(guest.id);
    if (itinerary) {
      const edge = this.conditionSnapshotValue.edges.find((candidate) => candidate.edgeId === itinerary.descentEdgeId);
      const terrain = calculateSuitableTerrainOutcome({ ability: guest.preferences.ability,
        terrainDifficulty: edge?.effectiveDifficulty ?? itinerary.routeMinimumAbility ?? itinerary.targetRating,
        hardcoreTerrainPreference: guest.preferences.hardcoreTerrainPreference,
        riskTolerance: guest.preferences.riskTolerance, open: true });
      const crowding = calculateCrowdingEffect({ occupancy: edge?.occupancy.guests ?? 0,
        capacity: edge?.occupancy.capacity ?? 100, queueWaitSeconds: guest.lastQueueWaitSeconds,
        expectedQueueWaitSeconds: 120, sensitivity: 1 - guest.preferences.patience });
      const conditions = edge ? (edge.snowQuality.quality + edge.coverage.fraction) / 2 : 0.75;
      const satisfaction = calculateSatisfactionChannels({ terrainFit: terrain.suitability,
        queueWaitSeconds: guest.lastQueueWaitSeconds, expectedQueueWaitSeconds: 120, crowding,
        comfort: edge?.comfort ?? 0.65, conditions, value: 0.7,
        variety: guest.preferences.varietySeeking, safety: 1 - terrain.safetyPenalty });
      guest.satisfaction = bounded(guest.satisfaction * 0.7 + satisfaction.overall * 0.3);
      const departure = evaluateEarlyDeparture({ worldSeed: this.roster.seed, entityId: guest.id,
        decisionOrdinal: guest.decisionOrdinal, currentTick: this.tickValue,
        plannedDepartureTick: guest.plannedDepartureTick, satisfaction: guest.satisfaction,
        channels: satisfaction, crowding, terrain, conditions, queueWaitSeconds: guest.lastQueueWaitSeconds,
        expectedQueueWaitSeconds: 120 });
      if (departure.departedEarly) {
        this.earlyDeparturesInternal.push(Object.freeze({ guestId: guest.id, tick: this.tickValue, decision: departure }));
        const reason = departure.primaryReasonCode ?? 'low-satisfaction';
        this.appendThought(guest, 'leaving', 'negative', `Leaving early: ${reason.replace(/-/g, ' ')}.`, reason);
        this.markDeparted(guest, false);
        return;
      }
      const sentiment = satisfaction.overall >= 0.67 ? 'positive' : satisfaction.overall < 0.4 ? 'negative' : 'neutral';
      const reason: ExperienceThoughtReasonCode = terrain.reasonCode === 'well-matched' ? 'positive-experience'
        : terrain.reasonCode === 'too-difficult' || terrain.reasonCode === 'too-easy' ? 'terrain-mismatch'
          : conditions < 0.5 ? 'poor-conditions' : 'waiting';
      this.appendThought(guest, sentiment === 'negative' ? 'concerned' : 'waiting', sentiment,
        terrain.reasonCode === 'well-matched' ? 'That run matched my ability and expectations.'
          : `That terrain felt ${terrain.reasonCode.replace('too-', 'too ')}.`, reason);
    }
    if (guest.pendingDeparture || this.tickValue >= (guest.plannedDepartureTick ?? this.roster.demandPlan.endTick)) {
      this.markDeparted(guest);
      return;
    }
    this.appendThought(guest, 'waiting', 'neutral', 'Taking a short break before the next run.');
    const request = this.remainingPhases.considerAmenity(guest.id, this.tickValue);
    if (request && (request.status === 'queued' || request.status === 'service')) {
      guest.status = request.status === 'queued' ? 'facility-queue' : 'facility-service';
      guest.currentResourceId = request.facilityId;
      scheduleGuestEvent(this.calendar, { dueTick: Math.max(this.tickValue + 1, request.completionTick ?? this.tickValue + 1),
        phase: GUEST_EVENT_PHASE.dueTravelServiceCompletions, ownerId: request.facilityId, guestId: guest.id,
        generationToken: this.calendar.generationFor(request.facilityId, guest.id),
        payload: { kind: 'amenity-progress', guestId: guest.id, requestId: request.requestId } });
      return;
    }
    this.scheduleDecision(guest, this.tickValue + 1);
  }

  private handleAmenityProgress(guestId: GuestId, requestId: string): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status === 'departed') return;
    const request = this.remainingPhases.amenityProgress(requestId, this.tickValue);
    if (request && (request.status === 'queued' || request.status === 'service')) {
      guest.status = request.status === 'queued' ? 'facility-queue' : 'facility-service';
      scheduleGuestEvent(this.calendar, { dueTick: Math.max(this.tickValue + 1, request.completionTick ?? this.tickValue + 1),
        phase: GUEST_EVENT_PHASE.dueTravelServiceCompletions, ownerId: request.facilityId, guestId,
        generationToken: this.calendar.generationFor(request.facilityId, guestId),
        payload: { kind: 'amenity-progress', guestId, requestId } });
      return;
    }
    guest.satisfaction = this.remainingPhases.amenitySatisfaction(guestId) ?? guest.satisfaction;
    guest.status = 'appraising'; guest.currentResourceId = null;
    if (guest.pendingDeparture) this.markDeparted(guest); else this.scheduleDecision(guest, this.tickValue + 1);
  }

  private handleInjury(guestId: GuestId, incident: InjuryIncident): void {
    const guest = this.guestsById.get(guestId);
    const itinerary = this.itinerariesByGuest.get(guestId);
    if (!guest || !itinerary || guest.status !== 'skiing') return;
    this.calendar.generationFor(incident.traversalId, guest.id);
    const edge = edgeById(this.network).get(incident.runId);
    this.safetyRuntime.reportInjury(incident, guest.partyId, edge?.fromNodeId ?? this.network.portalConnections[0]!.nodeId);
    guest.status = 'patrol-response';
    guest.currentResourceId = incident.runId;
    guest.satisfaction = bounded(guest.satisfaction * 0.7 + 0.05 * 0.3);
    this.partyItinerariesByParty.delete(guest.partyId);
    this.partyPlansByParty.delete(guest.partyId);
    this.rendezvousPlansByParty.delete(guest.partyId);
    this.appendThought(guest, 'concerned', 'negative',
      `Injured on the run; ski patrol was notified (${incident.primaryReasonCode}).`, 'injury');
  }

  private syncSafetyState(): Phase4SafetySnapshot {
    const safety = this.safetyRuntime.snapshot(this.tickValue);
    for (const incident of safety.guestIncidents) {
      if (this.finalizedSafetyIncidents.has(incident.id)) continue;
      if (incident.status !== 'resolved' && incident.status !== 'failed'
        && incident.status !== 'unreachable' && incident.status !== 'cancelled') continue;
      this.finalizedSafetyIncidents.add(incident.id);
      const guest = this.guestsById.get(incident.guestId);
      if (!guest || guest.status === 'departed') continue;
      const failed = incident.status !== 'resolved';
      const terminalTick = safety.patrol.dispatches.find((dispatch) => dispatch.incidentId === incident.id)?.completeTick
        ?? incident.createdTick;
      this.appendThought(guest, failed ? 'concerned' : 'leaving', failed ? 'negative' : 'neutral',
        failed ? 'The patrol response failed; leaving the resort.' : 'Ski patrol completed the rescue; leaving to recover.',
        failed ? 'safety-concern' : 'injury', terminalTick);
      this.markDeparted(guest, false);
    }
    return safety;
  }

  private handleDeparture(guestId: GuestId): void {
    const guest = this.guestsById.get(guestId);
    if (!guest || guest.status === 'departed') return;
    guest.pendingDeparture = true;
    if (guest.status === 'lift-ride' || guest.status === 'skiing'
      || guest.status === 'incident' || guest.status === 'patrol-response') return;
    const ledger = guest.currentResourceId ? this.liftsById.get(guest.currentResourceId) : undefined;
    if (ledger) {
      const index = ledger.queue.indexOf(guest.id);
      if (index >= 0) ledger.queue.splice(index, 1);
    }
    this.markDeparted(guest);
  }

  private markDeparted(guest: MutableGuest, appendThought = true): void {
    if (guest.status === 'departed') return;
    if (guest.status === 'lift-ride') {
      guest.pendingDeparture = true;
      return;
    }
    this.phase3Runtime.recordDeparture(immutableGuest(guest), this.tickValue);
    guest.status = 'departed';
    guest.currentPortalId = null;
    guest.currentResourceId = null;
    this.itinerariesByGuest.delete(guest.id);
    if (appendThought) this.appendThought(guest, 'leaving', 'neutral', 'Leaving the resort cleanly.');
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
