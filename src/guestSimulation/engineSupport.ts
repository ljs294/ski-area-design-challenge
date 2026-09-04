/** Pure network, roster, and routing support for the Phase 1A engine. */

import {
  GUEST_SIMULATION_CONTRACT_VERSION,
  type DemandPlan,
  type Guest,
  type GuestExperience,
  type GuestId,
  type GuestPortal,
  type GuestPreferences,
  type GuestSimulationEnvironmentSnapshot,
  type GuestSimulationSnapshot,
  type Party,
  type SimulatedSecond,
} from './contracts.ts';
import type { GuestSimulationConfig } from './config.ts';
import type { GuestEventPhase } from './eventPhases.ts';
import { keyedRandomFloat, keyedRandomInt, type RandomSeed } from './random.ts';
import type { PartyMemberProfile, PartyPlan, PartyRendezvousPlan } from './party.ts';
import { scoreConditionAwareRoute, type ConditionSnapshot } from './conditions.ts';
import type { EarlyDepartureDecision, ThoughtAggregation } from './experience.ts';
import type { InjuryIncident } from './injury.ts';
import type { Phase4SafetySnapshot } from './phase4Safety.ts';
import type { Phase3RuntimeInput, Phase3SimulationSnapshot } from './phase3Runtime.ts';

export type GuestNetworkNodeKind = 'portal' | 'lift-base' | 'lift-top' | 'junction';

export interface GuestNetworkNode { readonly id: string; readonly kind: GuestNetworkNodeKind; }
export type GuestNetworkEdgeKind = 'connector' | 'lift' | 'descent';

/** A directed edge. Reverse travel is represented by a second edge. */
export interface GuestNetworkEdge {
  readonly id: string;
  readonly fromNodeId: string;
  readonly toNodeId: string;
  readonly kind: GuestNetworkEdgeKind;
  readonly travelSeconds: SimulatedSecond;
  readonly liftId?: string;
  readonly capacitySeats?: number;
  readonly targetRating?: number;
  readonly closed?: boolean;
  readonly openFromTick?: SimulatedSecond;
  readonly openUntilTick?: SimulatedSecond;
}

export interface GuestLift {
  readonly id: string;
  readonly baseNodeId: string;
  readonly topNodeId: string;
  readonly edgeId: string;
  readonly capacitySeats: number;
  readonly dispatchIntervalSeconds?: SimulatedSecond;
  readonly capacityPph?: number;
  readonly rideSeconds: SimulatedSecond;
  readonly openFromTick?: SimulatedSecond;
  readonly openUntilTick?: SimulatedSecond;
}

export interface GuestPortalConnection { readonly portalId: string; readonly nodeId: string; }

export interface GuestSimulationNetwork {
  readonly nodes: readonly GuestNetworkNode[];
  readonly edges: readonly GuestNetworkEdge[];
  readonly lifts: readonly GuestLift[];
  readonly portals: readonly GuestPortal[];
  readonly portalConnections: readonly GuestPortalConnection[];
}

export interface GuestPortalPlacementInput {
  readonly portal?: GuestPortal;
  readonly portalId?: string;
  readonly label?: string;
  readonly capacityGuestsPerTick?: number;
  readonly openFromTick?: SimulatedSecond;
  readonly openUntilTick?: SimulatedSecond;
  readonly nodeId: string;
}

export interface GuestNetworkInput {
  readonly nodes: readonly GuestNetworkNode[];
  readonly edges: readonly GuestNetworkEdge[];
  readonly lifts: readonly GuestLift[];
  readonly portals: readonly GuestPortal[];
  readonly portalConnections: readonly GuestPortalConnection[];
}

export interface GuestItinerary {
  readonly id: string;
  readonly guestId: GuestId;
  readonly liftId: string;
  readonly connectorEdgeIds: readonly string[];
  readonly liftEdgeId: string;
  readonly descentEdgeId: string;
  readonly travelToLiftSeconds: SimulatedSecond;
  readonly rideSeconds: SimulatedSecond;
  readonly descentSeconds: SimulatedSecond;
  readonly targetRating: number;
  readonly weight: number;
  readonly decisionOrdinal: number;
  readonly partyId?: string;
  readonly routeMinimumAbility?: number;
  readonly ownership?: 'leader-owned-shared-plan';
}

export interface GuestAbilityTargets {
  readonly ability: number;
  readonly targetRating: number;
  readonly minimumTargetRating: number;
  readonly maximumTargetRating: number;
}

export interface DailyRosterOptions {
  readonly seed: RandomSeed;
  readonly guestCount: number;
  readonly portals: readonly GuestPortal[];
  readonly startTick?: SimulatedSecond;
  readonly endTick?: SimulatedSecond;
  /** Optional Phase 3 authority for the realised within-day arrival shape. */
  readonly demandPlan?: DemandPlan;
}

export interface DailyGuestRoster {
  readonly seed: string;
  readonly guests: readonly Guest[];
  readonly parties: readonly Party[];
  readonly demandPlan: DemandPlan;
}

export interface LiftSeatLedger {
  readonly liftId: string;
  readonly capacitySeats: number;
  readonly dispatches: number;
  readonly completedRides: number;
  readonly ridersInTransit: number;
  readonly queuedGuests: number;
}

export interface GuestSimulationMetrics {
  readonly population: number;
  readonly scheduled: number;
  readonly arrived: number;
  readonly active: number;
  readonly departed: number;
  readonly liftSeats: readonly LiftSeatLedger[];
  readonly liftSeatsConserved: boolean;
}

export interface GuestSimulationEngineSnapshot extends GuestSimulationSnapshot {
  readonly network: GuestSimulationNetwork;
  readonly itineraries: readonly GuestItinerary[];
  readonly abilityTargets: readonly { guestId: GuestId; targets: GuestAbilityTargets }[];
  readonly metrics: GuestSimulationMetrics;
  readonly pendingEvents: readonly GuestSimulationPendingEvent[];
  readonly liftQueues: readonly GuestSimulationLiftQueue[];
  readonly decisionOrdinals: readonly { guestId: GuestId; ordinal: number }[];
  readonly partyPlans: readonly PartyPlan[];
  readonly rendezvousPlans: readonly PartyRendezvousPlan[];
  readonly conditionSnapshot: ConditionSnapshot;
  readonly conditionHistory: readonly ConditionSnapshot[];
  readonly thoughtAggregation: ThoughtAggregation;
  readonly earlyDepartures: readonly { guestId: GuestId; tick: SimulatedSecond; decision: EarlyDepartureDecision }[];
  readonly safety: Phase4SafetySnapshot;
  readonly phase3: Phase3SimulationSnapshot;
}

export interface GuestSimulationPendingEvent {
  readonly tick: SimulatedSecond;
  readonly phase?: GuestEventPhase;
  readonly ownerId: string;
  readonly guestId: string;
  readonly payload: GuestSimulationEventPayload;
  readonly generation: number;
  readonly insertionSequence: number;
}

export interface GuestSimulationLiftQueue {
  readonly liftId: string;
  readonly queuedGuestIds: readonly GuestId[];
  readonly ridersInTransit: readonly GuestId[];
}

export interface GuestSimulationEngineOptions {
  readonly network: GuestSimulationNetwork;
  readonly roster: DailyGuestRoster;
  readonly runId?: string;
  readonly config?: GuestSimulationConfig;
  readonly environment?: GuestSimulationEnvironmentSnapshot;
  readonly conditionSnapshot?: ConditionSnapshot;
  readonly phase3?: Phase3RuntimeInput;
}

export type GuestSimulationEventPayload =
  | { readonly kind: 'arrival'; readonly guestId: GuestId }
  | { readonly kind: 'portal-retry'; readonly guestId: GuestId }
  | { readonly kind: 'reach-lift'; readonly guestId: GuestId }
  | { readonly kind: 'lift-dispatch'; readonly liftId: string }
  | { readonly kind: 'ride-complete'; readonly guestId: GuestId }
  | { readonly kind: 'descent-complete'; readonly guestId: GuestId; readonly traversalId: string }
  | { readonly kind: 'injury'; readonly guestId: GuestId; readonly incident: InjuryIncident }
  | { readonly kind: 'decide'; readonly guestId: GuestId }
  | { readonly kind: 'depart'; readonly guestId: GuestId };

export const DEFAULT_DAY_END = 12 * 60 * 60;
export const DEFAULT_GUEST_COUNT = 24;
const DEFAULT_TARGET_SPREAD = 0.22;
const DEFAULT_TRAVEL_SECONDS = 30;
const DEFAULT_RATING = 0.5;

export function freezeArray<T>(values: readonly T[]): readonly T[] { return Object.freeze([...values]); }
export function stringSeed(seed: RandomSeed): string { return typeof seed === 'bigint' ? `${seed}n` : String(seed); }
export function positiveInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${label} must be a positive integer`);
}
export function tick(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}
export function bounded(value: number, minimum = 0, maximum = 1): number { return Math.max(minimum, Math.min(maximum, value)); }
export function liftDispatchInterval(lift: GuestLift): SimulatedSecond {
  if (lift.dispatchIntervalSeconds !== undefined) return lift.dispatchIntervalSeconds;
  if (lift.capacityPph !== undefined) return Math.max(1, Math.round((lift.capacitySeats * 60 * 60) / lift.capacityPph));
  return 1;
}
export function compareId(left: { id: string }, right: { id: string }): number {
  return left.id < right.id ? -1 : left.id > right.id ? 1 : 0;
}

function uniqueById(items: readonly { id: string }[], label: string): void {
  const seen = new Set<string>();
  for (const item of items) {
    if (!item.id) throw new RangeError(`${label} ids must be non-empty`);
    if (seen.has(item.id)) throw new RangeError(`${label} ids must be unique`);
    seen.add(item.id);
  }
}

export function validatePortal(portal: GuestPortal): void {
  if (portal.version !== GUEST_SIMULATION_CONTRACT_VERSION || portal.kind !== 'guest-entrance'
    || portal.type !== 'guest-entrance' || portal.semantics !== 'guest-entrance'
    || portal.direction !== 'inbound' || portal.accepts !== 'guests') throw new RangeError(`Invalid guest portal ${portal.id}`);
  positiveInteger(portal.capacityGuestsPerTick, 'portal capacity');
  tick(portal.openFromTick, 'portal openFromTick');
  tick(portal.openUntilTick, 'portal openUntilTick');
  if (portal.openUntilTick <= portal.openFromTick) throw new RangeError('portal operating interval must be half-open and non-empty');
}

function validateNetwork(input: GuestNetworkInput): GuestSimulationNetwork {
  uniqueById(input.nodes, 'network node'); uniqueById(input.edges, 'network edge');
  uniqueById(input.lifts, 'lift'); uniqueById(input.portals, 'portal');
  const nodes = new Map(input.nodes.map((node) => [node.id, node]));
  const edges = new Map(input.edges.map((edge) => [edge.id, edge]));
  const lifts = new Map(input.lifts.map((lift) => [lift.id, lift]));
  const portals = new Map(input.portals.map((portal) => [portal.id, portal]));
  for (const portal of input.portals) validatePortal(portal);
  if (input.portalConnections.length !== input.portals.length) throw new RangeError('each portal must have one network connection');
  const connected = new Set<string>();
  for (const connection of input.portalConnections) {
    if (!portals.has(connection.portalId)) throw new RangeError(`portal connection references unknown portal ${connection.portalId}`);
    if (!nodes.has(connection.nodeId)) throw new RangeError(`portal connection references unknown node ${connection.nodeId}`);
    if (connected.has(connection.portalId)) throw new RangeError(`portal ${connection.portalId} has multiple network connections`);
    connected.add(connection.portalId);
  }
  for (const edge of input.edges) {
    if (!nodes.has(edge.fromNodeId) || !nodes.has(edge.toNodeId)) throw new RangeError(`edge ${edge.id} references an unknown node`);
    tick(edge.travelSeconds, `edge ${edge.id} travelSeconds`);
    if (edge.kind === 'lift') {
      if (!edge.liftId || !lifts.has(edge.liftId)) throw new RangeError(`lift edge ${edge.id} must reference a lift`);
      if (edge.capacitySeats === undefined || edge.capacitySeats <= 0 || !Number.isSafeInteger(edge.capacitySeats)) throw new RangeError(`lift edge ${edge.id} must have positive integer capacitySeats`);
    }
    if (edge.kind === 'descent' && edge.targetRating !== undefined && (edge.targetRating < 0 || edge.targetRating > 1)) throw new RangeError(`descent edge ${edge.id} targetRating must be in [0, 1]`);
  }
  for (const lift of input.lifts) {
    if (!nodes.has(lift.baseNodeId) || !nodes.has(lift.topNodeId)) throw new RangeError(`lift ${lift.id} references an unknown node`);
    const edge = edges.get(lift.edgeId);
    if (!edge || edge.kind !== 'lift' || edge.liftId !== lift.id) throw new RangeError(`lift ${lift.id} edge does not match its lift`);
    if (edge.fromNodeId !== lift.baseNodeId || edge.toNodeId !== lift.topNodeId) throw new RangeError(`lift ${lift.id} edge endpoints do not match`);
    positiveInteger(lift.capacitySeats, `lift ${lift.id} capacitySeats`);
    if (lift.dispatchIntervalSeconds !== undefined) positiveInteger(lift.dispatchIntervalSeconds, `lift ${lift.id} dispatchIntervalSeconds`);
    if (lift.capacityPph !== undefined && (!Number.isFinite(lift.capacityPph) || lift.capacityPph <= 0)) throw new RangeError(`lift ${lift.id} capacityPph must be positive when supplied`);
    tick(lift.rideSeconds, `lift ${lift.id} rideSeconds`);
  }
  return Object.freeze({ nodes: freezeArray([...input.nodes].sort(compareId)), edges: freezeArray([...input.edges].sort(compareId)),
    lifts: freezeArray([...input.lifts].sort(compareId)), portals: freezeArray([...input.portals].sort(compareId)),
    portalConnections: freezeArray([...input.portalConnections].sort((a, b) => a.portalId.localeCompare(b.portalId))) });
}

export function createGuestSimulationNetwork(input: GuestNetworkInput): GuestSimulationNetwork { return validateNetwork(input); }

export function placeGuestPortal(network: GuestSimulationNetwork, input: GuestPortalPlacementInput): GuestSimulationNetwork {
  const node = network.nodes.find((candidate) => candidate.id === input.nodeId);
  if (!node) throw new RangeError(`cannot place portal on unknown node ${input.nodeId}`);
  const portal = input.portal ?? Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION,
    id: input.portalId ?? `guest-entrance-${network.portals.length + 1}`, kind: 'guest-entrance' as const,
    type: 'guest-entrance' as const, semantics: 'guest-entrance' as const, direction: 'inbound' as const,
    accepts: 'guests' as const, label: input.label ?? `Guest Entrance ${network.portals.length + 1}`,
    capacityGuestsPerTick: input.capacityGuestsPerTick ?? 2, openFromTick: input.openFromTick ?? 0,
    openUntilTick: input.openUntilTick ?? DEFAULT_DAY_END });
  if (network.portals.some((candidate) => candidate.id === portal.id)) throw new RangeError(`portal ${portal.id} already exists`);
  return createGuestSimulationNetwork({ ...network, portals: [...network.portals, portal],
    portalConnections: [...network.portalConnections, { portalId: portal.id, nodeId: node.id }] });
}

export const addGuestPortal = placeGuestPortal;
export const createGuestPortalPlacement = placeGuestPortal;

function portalNodeId(network: GuestSimulationNetwork, portalId: string): string | undefined {
  return network.portalConnections.find((connection) => connection.portalId === portalId)?.nodeId;
}

export function guestAbilityTargets(guest: Guest, seed: RandomSeed, ordinal = 0): GuestAbilityTargets {
  const ability = bounded(guest.preferences.ability);
  const spread = Math.min(DEFAULT_TARGET_SPREAD, Math.max(0.05, 0.10 + guest.preferences.varietySeeking * 0.20));
  const minimumTargetRating = bounded(ability - spread);
  const maximumTargetRating = bounded(ability + spread);
  const targetRating = minimumTargetRating + (maximumTargetRating - minimumTargetRating) * keyedRandomFloat(seed, guest.id, 'target-rating', ordinal);
  return Object.freeze({ ability, targetRating, minimumTargetRating, maximumTargetRating });
}
export const targetRatingForGuest = guestAbilityTargets;

function preferenceFor(seed: RandomSeed, ordinal: number): GuestPreferences {
  const draw = (tag: string): number => keyedRandomFloat(seed, `guest-${ordinal + 1}`, tag);
  const ability = draw('ability');
  const band: GuestExperience = ability < 0.25 ? 'beginner' : ability < 0.55 ? 'intermediate' : ability < 0.82 ? 'advanced' : 'expert';
  const ageBands = ['child', 'teen', 'adult', 'senior'] as const;
  const segments = ['budget', 'standard', 'premium', 'luxury'] as const;
  const economicIndex = Math.floor(draw('economic-segment') * segments.length) % segments.length;
  const ageIndex = Math.floor(draw('age-band') * ageBands.length) % ageBands.length;
  const tripCashCents = 5_000 + keyedRandomInt(seed, `guest-${ordinal + 1}`, 'trip-cash', 0, 0, 12) * 2_500;
  return Object.freeze({ experience: band, abilityBand: band, ability, ageBand: ageBands[ageIndex], wantsLessons: draw('lessons') < 0.14,
    budgetCents: tripCashCents, economicSegment: segments[economicIndex], tripCashCents, riskTolerance: draw('risk'),
    comfortDemand: draw('comfort'), hardcoreTerrainPreference: draw('hardcore-terrain'), priceSensitivity: draw('price-sensitivity'),
    frugality: draw('frugality'), patience: draw('patience'), varietySeeking: draw('variety') });
}

export function createDailyGuestRoster(options: DailyRosterOptions): DailyGuestRoster {
  if (!Number.isSafeInteger(options.guestCount) || options.guestCount < 0) {
    throw new RangeError('guestCount must be a non-negative safe integer');
  }
  if (options.portals.length === 0) throw new RangeError('daily roster requires at least one guest portal');
  for (const portal of options.portals) validatePortal(portal);
  const seed = stringSeed(options.seed), startTick = options.startTick ?? 0, endTick = options.endTick ?? DEFAULT_DAY_END;
  tick(startTick, 'roster startTick'); tick(endTick, 'roster endTick');
  if (endTick <= startTick) throw new RangeError('roster endTick must be after startTick');
  const suppliedPlan = options.demandPlan;
  if (suppliedPlan && (suppliedPlan.seed !== seed || suppliedPlan.guestCount !== options.guestCount
    || suppliedPlan.startTick !== startTick || suppliedPlan.endTick !== endTick)) {
    throw new RangeError('demandPlan must match the roster seed, guest count, and horizon');
  }
  const guests: Guest[] = [], parties: Party[] = [];
  const waveCount = suppliedPlan?.waves.length ?? Math.min(3, endTick - startTick);
  const waveLength = Math.max(1, Math.ceil((endTick - startTick) / Math.max(1, waveCount)));
  const rosterWaves = suppliedPlan?.waves ?? Array.from({ length: waveCount }, (_, index) => {
    const waveStart = startTick + index * waveLength;
    const baseGuests = Math.floor(options.guestCount / waveCount);
    const allocatedGuests = baseGuests + (index < options.guestCount % waveCount ? 1 : 0);
    return { id: `wave-${String(index + 1).padStart(2, '0')}`, kind: index === 1 ? 'weekend' as const : 'weekday' as const,
      startTick: waveStart, endTick: index === waveCount - 1 ? endTick : Math.min(endTick, waveStart + waveLength),
      guestCount: allocatedGuests, partyCount: 0 };
  });
  let guestOrdinal = 0, partyOrdinal = 0;
  let waveIndex = 0, guestsPlacedInWave = 0;
  while (guestOrdinal < options.guestCount) {
    while (waveIndex < rosterWaves.length - 1 && guestsPlacedInWave >= rosterWaves[waveIndex]!.guestCount) {
      waveIndex += 1; guestsPlacedInWave = 0;
    }
    const wave = rosterWaves[waveIndex]!;
    const remainingInWave = Math.max(1, wave.guestCount - guestsPlacedInWave);
    const maximumParty = Math.min(4, options.guestCount - guestOrdinal, remainingInWave);
    const partySize = keyedRandomInt(seed, `party-${partyOrdinal + 1}`, 'party-size', 0, 1, maximumParty);
    const waveStart = wave.startTick, waveEnd = wave.endTick;
    const arrivalTick = keyedRandomInt(seed, `party-${partyOrdinal + 1}`, 'arrival', 0, waveStart, Math.max(waveStart, waveEnd - 1));
    const departureTick = Math.min(endTick, arrivalTick + 4 * 60 * 60 + (partyOrdinal % 3) * 60 * 60);
    const partyId = `party-${String(partyOrdinal + 1).padStart(5, '0')}`;
    const guestIds = Array.from({ length: partySize }, (_, member) => `guest-${String(guestOrdinal + member + 1).padStart(6, '0')}`);
    const partyPortalId = options.portals[partyOrdinal % options.portals.length]!.id;
    const kind: Party['kind'] = partySize === 1 ? 'individual' : partySize === 2 ? 'friends' : 'family';
    parties.push(Object.freeze({ id: partyId, guestIds: freezeArray(guestIds), size: partySize, kind, heavyGroup: partySize >= 4,
      arrivalTick, plannedDepartureTick: departureTick, futurePartyId: null }));
    for (let member = 0; member < partySize; member += 1) {
      const id = guestIds[member]!;
      guests.push(Object.freeze({ id, partyId, ordinal: guestOrdinal + member,
        arrivalTick: Math.min(waveEnd - 1, arrivalTick + Math.min(member, 2)), plannedDepartureTick: departureTick,
        portalId: partyPortalId, preferences: preferenceFor(seed, guestOrdinal + member), futurePartyId: null }));
    }
    guestOrdinal += partySize; partyOrdinal += 1; guestsPlacedInWave += partySize;
  }
  const waves = freezeArray(rosterWaves.map((wave) => {
    const waveStart = wave.startTick, waveEnd = wave.endTick;
    const selected = parties.filter((party) => party.arrivalTick >= waveStart && party.arrivalTick < waveEnd);
    return Object.freeze({ id: wave.id, kind: wave.kind,
      startTick: waveStart, endTick: waveEnd, guestCount: selected.reduce((sum, party) => sum + party.size, 0), partyCount: selected.length });
  }));
  const demandPlan: DemandPlan = Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, seed, guestCount: guests.length,
    partyCount: parties.length, startTick, endTick, waves, heavyGroupCount: parties.filter((party) => party.heavyGroup).length });
  return Object.freeze({ seed, guests: freezeArray(guests), parties: freezeArray(parties), demandPlan });
}
export const createDailyRoster = createDailyGuestRoster;
export const createSeededDailyRoster = createDailyGuestRoster;

export function edgeById(network: GuestSimulationNetwork): Map<string, GuestNetworkEdge> { return new Map(network.edges.map((edge) => [edge.id, edge])); }
export function activeIncident(environment: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond, resourceId: string | null, kind: string): boolean {
  return environment.incidents.some((incident) => incident.startTick <= tickValue && (incident.endTick === null || tickValue < incident.endTick)
    && incident.kind === kind && incident.affectedResourceId === resourceId);
}
export function edgeOpenAt(network: GuestSimulationNetwork, edge: GuestNetworkEdge, environment: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond): boolean {
  if (edge.closed || (edge.openFromTick !== undefined && tickValue < edge.openFromTick) || (edge.openUntilTick !== undefined && tickValue >= edge.openUntilTick)) return false;
  if (edge.kind === 'lift') {
    const lift = network.lifts.find((candidate) => candidate.id === edge.liftId);
    if (!lift || (lift.openFromTick !== undefined && tickValue < lift.openFromTick) || (lift.openUntilTick !== undefined && tickValue >= lift.openUntilTick)) return false;
    return !activeIncident(environment, tickValue, lift.id, 'lift-closure');
  }
  if (edge.kind === 'descent') return !activeIncident(environment, tickValue, edge.id, 'trail-closure');
  return true;
}
export function portalOpenAt(portal: GuestPortal, environment: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond): boolean {
  if (tickValue < portal.openFromTick || tickValue >= portal.openUntilTick) return false;
  return !environment.incidents.some((incident) => incident.kind === 'portal-closure' && incident.affectedPortalId === portal.id
    && incident.startTick <= tickValue && (incident.endTick === null || tickValue < incident.endTick));
}
function findConnectorPath(network: GuestSimulationNetwork, fromNodeId: string, toNodeId: string, environment: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond): GuestNetworkEdge[] | null {
  if (fromNodeId === toNodeId) return [];
  const edges = network.edges.filter((edge) => edge.kind === 'connector' && edgeOpenAt(network, edge, environment, tickValue));
  const byFrom = new Map<string, GuestNetworkEdge[]>();
  for (const edge of edges) byFrom.set(edge.fromNodeId, [...(byFrom.get(edge.fromNodeId) ?? []), edge]);
  for (const candidates of byFrom.values()) candidates.sort(compareId);
  const queue = [fromNodeId], previous = new Map<string, { nodeId: string; edge: GuestNetworkEdge }>(), visited = new Set([fromNodeId]);
  while (queue.length > 0) {
    const nodeId = queue.shift()!;
    for (const edge of byFrom.get(nodeId) ?? []) {
      if (visited.has(edge.toNodeId)) continue;
      visited.add(edge.toNodeId); previous.set(edge.toNodeId, { nodeId, edge });
      if (edge.toNodeId === toNodeId) {
        const result: GuestNetworkEdge[] = []; let cursor = toNodeId;
        while (cursor !== fromNodeId) { const step = previous.get(cursor); if (!step) return null; result.push(step.edge); cursor = step.nodeId; }
        result.reverse(); return result;
      }
      queue.push(edge.toNodeId);
    }
  }
  return null;
}
function ratingForEdge(edge: GuestNetworkEdge): number { return bounded(edge.targetRating ?? DEFAULT_RATING); }

export function chooseWeightedGuestItinerary(network: GuestSimulationNetwork, guest: Guest, seed: RandomSeed,
  environment: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond, decisionOrdinal = 0,
  queueLengths: ReadonlyMap<string, number> = new Map(), minimumAbility?: number,
  conditions?: ConditionSnapshot): GuestItinerary | null {
  const portal = network.portals.find((candidate) => candidate.id === guest.portalId), startNodeId = portal ? portalNodeId(network, portal.id) : undefined;
  if (!portal || !startNodeId || !portalOpenAt(portal, environment, tickValue) || !environment.operating) return null;
  const edges = edgeById(network), candidates: { itinerary: GuestItinerary; score: number }[] = [];
  for (const lift of network.lifts) {
    const liftEdge = edges.get(lift.edgeId);
    if (!liftEdge || !edgeOpenAt(network, liftEdge, environment, tickValue)) continue;
    const connectorPath = findConnectorPath(network, startNodeId, lift.baseNodeId, environment, tickValue);
    if (!connectorPath || connectorPath.some((edge) => !edgeOpenAt(network, edge, environment, tickValue))) continue;
    const descents = network.edges.filter((edge) => edge.kind === 'descent' && edge.fromNodeId === lift.topNodeId
      && edgeOpenAt(network, edge, environment, tickValue));
    for (const descent of descents) {
      const target = guestAbilityTargets(guest, seed, decisionOrdinal).targetRating;
      const compatibility = 1 - Math.abs(target - ratingForEdge(descent));
      const waitPenalty = (queueLengths.get(lift.id) ?? 0) / Math.max(1, lift.capacitySeats);
      const conditionScore = conditions?.edges.some((edge) => edge.edgeId === descent.id)
        ? scoreConditionAwareRoute(conditions, [descent.id], { ability: minimumAbility ?? guest.preferences.ability,
          targetDifficulty: target, comfortDemand: guest.preferences.comfortDemand,
          hardcoreTerrainPreference: guest.preferences.hardcoreTerrainPreference,
          crowdingSensitivity: 1 - guest.preferences.patience }) : null;
      if (conditionScore && !conditionScore.canProceed) continue;
      const score = compatibility * 3 + guest.preferences.varietySeeking * 0.25
        + (conditionScore?.score ?? 0.5) * 2 - waitPenalty * (1 + guest.preferences.patience);
      const weight = Math.max(0.000001, Math.exp(score));
      candidates.push({ score, itinerary: Object.freeze({ id: `itinerary-${guest.id}-${decisionOrdinal + 1}-${lift.id}-${descent.id}`,
        guestId: guest.id, liftId: lift.id, connectorEdgeIds: freezeArray(connectorPath.map((edge) => edge.id)), liftEdgeId: lift.edgeId,
        descentEdgeId: descent.id, travelToLiftSeconds: connectorPath.reduce((sum, edge) => sum + edge.travelSeconds, 0),
        rideSeconds: lift.rideSeconds, descentSeconds: descent.travelSeconds, targetRating: target, weight, decisionOrdinal,
        routeMinimumAbility: ratingForEdge(descent) }) });
    }
  }
  if (candidates.length === 0) return null;
  const total = candidates.reduce((sum, candidate) => sum + candidate.itinerary.weight, 0);
  let cursor = keyedRandomFloat(seed, guest.id, 'itinerary-choice', decisionOrdinal) * total;
  for (const candidate of candidates.sort((left, right) => left.itinerary.id.localeCompare(right.itinerary.id))) {
    cursor -= candidate.itinerary.weight;
    if (cursor < 0) return candidate.itinerary;
  }
  return candidates[candidates.length - 1]!.itinerary;
}
export const chooseGuestItinerary = chooseWeightedGuestItinerary;

export function memberProfile(guest: Guest): PartyMemberProfile {
  return { id: guest.id, partyId: guest.partyId, ordinal: guest.ordinal, ability: guest.preferences.ability,
    patience: guest.preferences.patience, riskTolerance: guest.preferences.riskTolerance };
}

export function environmentAt(base: GuestSimulationEnvironmentSnapshot, tickValue: SimulatedSecond): GuestSimulationEnvironmentSnapshot {
  return Object.freeze({ ...base, tick: tickValue, conditions: Object.freeze({ ...base.conditions, tick: tickValue }), operating: base.operating });
}

export function createDefaultGuestSimulationNetwork(portals: readonly GuestPortal[]): GuestSimulationNetwork {
  const nodes: GuestNetworkNode[] = [], edges: GuestNetworkEdge[] = [], connections: GuestPortalConnection[] = [];
  for (let index = 0; index < portals.length; index += 1) {
    const portal = portals[index]!, portalNodeId = `portal-node-${index + 1}`;
    nodes.push({ id: portalNodeId, kind: 'portal' }); connections.push({ portalId: portal.id, nodeId: portalNodeId });
  }
  nodes.push({ id: 'base-1', kind: 'lift-base' }, { id: 'top-1', kind: 'lift-top' }, { id: 'base-2', kind: 'lift-base' }, { id: 'top-2', kind: 'lift-top' });
  for (let index = 0; index < portals.length; index += 1) edges.push({ id: `connector-${index + 1}`, fromNodeId: `portal-node-${index + 1}`,
    toNodeId: index % 2 === 0 ? 'base-1' : 'base-2', kind: 'connector', travelSeconds: DEFAULT_TRAVEL_SECONDS });
  edges.push({ id: 'lift-edge-1', fromNodeId: 'base-1', toNodeId: 'top-1', kind: 'lift', travelSeconds: 1, liftId: 'lift-1', capacitySeats: 4 },
    { id: 'descent-edge-1', fromNodeId: 'top-1', toNodeId: 'base-1', kind: 'descent', travelSeconds: 120, targetRating: 0.45 },
    { id: 'lift-edge-2', fromNodeId: 'base-2', toNodeId: 'top-2', kind: 'lift', travelSeconds: 1, liftId: 'lift-2', capacitySeats: 6 },
    { id: 'descent-edge-2', fromNodeId: 'top-2', toNodeId: 'base-2', kind: 'descent', travelSeconds: 180, targetRating: 0.72 });
  const lifts: GuestLift[] = [
    { id: 'lift-1', baseNodeId: 'base-1', topNodeId: 'top-1', edgeId: 'lift-edge-1', capacitySeats: 4, rideSeconds: 180 },
    { id: 'lift-2', baseNodeId: 'base-2', topNodeId: 'top-2', edgeId: 'lift-edge-2', capacitySeats: 6, rideSeconds: 240 },
  ];
  return createGuestSimulationNetwork({ nodes, edges, lifts, portals, portalConnections: connections });
}
