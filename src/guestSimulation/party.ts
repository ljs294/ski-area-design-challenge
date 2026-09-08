/**
 * Dependency-neutral party planning, boarding, and member accounting.
 *
 * A party is a coordination boundary, not a simulation entity that replaces
 * its members.  Every operation in this module therefore carries member IDs
 * through the queue, chair, trail, exit, and save/load seams.  The only
 * shared decision is the leader-owned plan; capacity, risk, presentation,
 * and random draws remain per member.
 */

import type { Party, SimulatedSecond } from './contracts.ts';
import { asSimulatedSecond } from './contracts.ts';
import { keyedRandomFloat } from './random.ts';

export const PARTY_FOUNDATION_VERSION = 1 as const;
export type PartyFoundationVersion = typeof PARTY_FOUNDATION_VERSION;

export type PartyMemberLocation = 'queue' | 'lift' | 'trail' | 'exit';

/** The smallest profile needed by the party layer. `Guest` is structurally compatible. */
export interface PartyMemberProfile {
  readonly id: string;
  readonly partyId: string;
  readonly ordinal?: number;
  readonly ability?: number;
  readonly patience?: number;
  readonly riskTolerance?: number;
  readonly preferences?: {
    readonly ability?: number;
    readonly patience?: number;
    readonly riskTolerance?: number;
  };
}

export interface PartyRouteOption {
  readonly id: string;
  readonly liftId?: string | null;
  readonly trailId?: string | null;
  /** Ability required by this route. `difficulty` is accepted as a friendly alias. */
  readonly minimumAbility?: number;
  readonly difficulty?: number;
  /** Optional leader-only preference score. It never includes party size. */
  readonly leaderAppeal?: number;
  readonly durationTicks?: number;
}

export interface PartyPlanRequest {
  readonly party: Party;
  readonly members: readonly PartyMemberProfile[];
  readonly routes: readonly PartyRouteOption[];
  readonly worldSeed: string | number | bigint;
  readonly tick?: SimulatedSecond;
  /** Keep a reachable route eligible even when it exceeds the weakest member's ability. */
  readonly allowUnsafeSelection?: boolean;
}

export interface PartyPlan {
  readonly version: PartyFoundationVersion;
  readonly partyId: string;
  readonly leaderId: string;
  readonly memberIds: readonly string[];
  readonly selectedRouteId: string | null;
  readonly selectedLiftId: string | null;
  readonly selectedTrailId: string | null;
  readonly weakestMemberId: string;
  readonly weakestAbility: number;
  readonly routeMinimumAbility: number | null;
  readonly weakestMemberSafe: boolean;
  readonly canProceed: boolean;
  readonly createdTick: SimulatedSecond;
  readonly revision: number;
  readonly ownership: 'leader-owned-shared-plan';
}

export interface PartyMemberSafety {
  readonly memberId: string;
  readonly ability: number;
  readonly safe: boolean;
}

export interface PartyPlanSafety {
  readonly safe: boolean;
  readonly weakestMemberId: string;
  readonly weakestAbility: number;
  readonly routeMinimumAbility: number | null;
  readonly members: readonly PartyMemberSafety[];
}

export interface PartyRendezvousRequest {
  readonly party: Party;
  readonly memberIds?: readonly string[];
  readonly locationId: string;
  readonly targetTick: SimulatedSecond;
  readonly leaderId?: string;
}

export interface PartyRendezvousPlan {
  readonly version: PartyFoundationVersion;
  readonly id: string;
  readonly partyId: string;
  readonly leaderId: string;
  readonly locationId: string;
  readonly targetTick: SimulatedSecond;
  readonly memberIds: readonly string[];
  readonly ownership: 'leader-owned-shared-plan';
}

export interface PartyChair {
  readonly id: string;
  readonly capacity: number;
  readonly departureTick?: SimulatedSecond;
}

export interface ChairBoardingAssignment {
  readonly partyId: string;
  readonly chairId: string;
  readonly memberIds: readonly string[];
  readonly departureTick?: SimulatedSecond;
  readonly queueRound: number;
}

export interface ConsecutiveChairBoardingRequest {
  readonly partyId: string;
  readonly memberIds: readonly string[];
  readonly chairs: readonly PartyChair[];
  readonly startChairIndex?: number;
  readonly queueRound?: number;
}

export interface PartyQueueEntry {
  readonly partyId: string;
  readonly memberIds: readonly string[];
  readonly boardedMemberIds?: readonly string[];
  readonly enqueuedTick: SimulatedSecond;
  readonly queueOrder: number;
}

export interface QueueBoardingRequest {
  readonly queue: readonly PartyQueueEntry[];
  readonly chairs: readonly PartyChair[];
  /** Number of chairs a party may reserve in one fair round. Defaults to one. */
  readonly maxChairsPerRound?: number;
}

export interface QueueBoardingResult {
  readonly assignments: readonly ChairBoardingAssignment[];
  readonly queue: readonly PartyQueueEntry[];
  /** Number of round-robin turns until the final assignment. */
  readonly rounds: number;
  readonly maxPartyWaitRounds: number;
}

export interface PartyMemberIdentity {
  /** These IDs are stable one-to-one identities, never party-level aliases. */
  readonly renderedDotId: string;
  readonly seatIdentity: string;
  readonly occupancyIdentity: string;
  readonly thoughtIdentity: string;
  readonly injuryRollIdentity: string;
  readonly persistenceIdentity: string;
}

export interface PartyMemberThought {
  readonly id: string;
  readonly memberId: string;
  readonly partyId: string;
  readonly tick: SimulatedSecond;
  readonly text: string;
}

export interface PartyMemberOutcome {
  readonly memberId: string;
  readonly partyId: string;
  readonly identity: PartyMemberIdentity;
  readonly cohesionRoll: number;
  readonly followsPlan: boolean;
  readonly injuryRoll: number;
  readonly injuryProbability: number;
  readonly injured: boolean;
  readonly thought: PartyMemberThought;
}

export interface PartyOutcomeRequest {
  readonly party: Party;
  readonly members: readonly PartyMemberProfile[];
  readonly plan: PartyPlan;
  readonly worldSeed: string | number | bigint;
  readonly tick: SimulatedSecond;
  readonly decisionOrdinal?: number;
}

export interface PartyMemberAccountingRecord {
  readonly memberId: string;
  readonly partyId: string;
  readonly location: PartyMemberLocation;
  readonly identity: PartyMemberIdentity;
  readonly queuedTick: SimulatedSecond;
  readonly chairId: string | null;
  readonly seatIndex: number | null;
  readonly liftRideId: string | null;
  readonly trailId: string | null;
  readonly exitedTick: SimulatedSecond | null;
}

export interface PartyAccountingState {
  readonly version: PartyFoundationVersion;
  readonly members: readonly PartyMemberAccountingRecord[];
}

export interface PartyConservation {
  readonly total: number;
  readonly byLocation: Readonly<Record<PartyMemberLocation, number>>;
  readonly conserved: boolean;
}

function freezeArray<T>(values: readonly T[]): readonly T[] {
  return Object.freeze([...values]);
}

function freezeObject<T extends object>(value: T): T {
  return Object.freeze(value);
}

function assertNonEmptyString(value: string, label: string): void {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be a non-empty string`);
}

function assertNonNegativeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

function clampUnit(value: number | undefined, fallback: number): number {
  const candidate = value ?? fallback;
  if (!Number.isFinite(candidate)) return fallback;
  return Math.max(0, Math.min(1, candidate));
}

function memberAbility(member: PartyMemberProfile): number {
  return clampUnit(member.ability ?? member.preferences?.ability, 0);
}

function memberPatience(member: PartyMemberProfile): number {
  return clampUnit(member.patience ?? member.preferences?.patience, 0.5);
}

function memberRiskTolerance(member: PartyMemberProfile): number {
  return clampUnit(member.riskTolerance ?? member.preferences?.riskTolerance, 0.5);
}

function memberIdsForParty(party: Party, members: readonly PartyMemberProfile[]): readonly string[] {
  assertNonEmptyString(party.id, 'party.id');
  if (!Number.isSafeInteger(party.size) || party.size < 1) throw new RangeError('party.size must be a positive integer');
  if (party.guestIds.length !== party.size) throw new RangeError('party.guestIds must contain exactly party.size members');
  const supplied = new Map<string, PartyMemberProfile>();
  for (const member of members) {
    assertNonEmptyString(member.id, 'member.id');
    assertNonEmptyString(member.partyId, 'member.partyId');
    if (member.partyId !== party.id) throw new RangeError(`Member ${member.id} belongs to ${member.partyId}, not ${party.id}`);
    if (supplied.has(member.id)) throw new RangeError(`Duplicate member ${member.id}`);
    supplied.set(member.id, member);
  }
  const ids = party.guestIds.map((id) => String(id));
  if (new Set(ids).size !== ids.length) throw new RangeError('party.guestIds must be unique');
  if (supplied.size !== ids.length || ids.some((id) => !supplied.has(id))) {
    throw new RangeError('members must contain exactly party.guestIds');
  }
  return freezeArray(ids);
}

function routeMinimumAbility(route: PartyRouteOption): number {
  const value = route.minimumAbility ?? route.difficulty ?? 0;
  return clampUnit(value, 0);
}

/** Return the least-able member, with member order as a deterministic tie-breaker. */
export function getWeakestPartyMember(members: readonly PartyMemberProfile[]): PartyMemberProfile {
  if (members.length === 0) throw new RangeError('A party must contain at least one member');
  let weakest = members[0];
  for (const member of members.slice(1)) {
    if (memberAbility(member) < memberAbility(weakest)) weakest = member;
  }
  return weakest;
}

function routeSortKey(route: PartyRouteOption, leaderId: string, worldSeed: string | number | bigint): number {
  const appeal = route.leaderAppeal ?? (1 - routeMinimumAbility(route));
  const tie = keyedRandomFloat(worldSeed, leaderId, `party-plan-route:${route.id}`, 0);
  // Appeal is the leader's preference; keyed tie-breaking makes equal options replayable.
  return appeal * 2 + tie * 1e-6;
}

/** Create one shared plan owned by the party leader and safe for its weakest member. */
export function createPartyPlan(request: PartyPlanRequest): PartyPlan;
export function createPartyPlan(
  party: Party,
  members: readonly PartyMemberProfile[],
  routes: readonly PartyRouteOption[],
  worldSeed: string | number | bigint,
  tick?: SimulatedSecond,
): PartyPlan;
export function createPartyPlan(
  requestOrParty: PartyPlanRequest | Party,
  positionalMembers?: readonly PartyMemberProfile[],
  positionalRoutes?: readonly PartyRouteOption[],
  positionalSeed?: string | number | bigint,
  positionalTick = 0,
): PartyPlan {
  const request: PartyPlanRequest = 'party' in requestOrParty
    ? requestOrParty
    : { party: requestOrParty, members: positionalMembers ?? [], routes: positionalRoutes ?? [], worldSeed: positionalSeed ?? 'party-foundation', tick: positionalTick };
  const memberIds = memberIdsForParty(request.party, request.members);
  const tick = asSimulatedSecond(request.tick ?? request.party.arrivalTick, 'party plan tick');
  const leaderId = memberIds[0];
  const weakest = getWeakestPartyMember(request.members);
  const weakestAbility = memberAbility(weakest);
  const eligibleRoutes = request.routes.filter((route) => {
    assertNonEmptyString(route.id, 'route.id');
    return request.allowUnsafeSelection || routeMinimumAbility(route) <= weakestAbility;
  });
  const selected = eligibleRoutes.slice().sort((left, right) => {
    const score = routeSortKey(right, leaderId, request.worldSeed) - routeSortKey(left, leaderId, request.worldSeed);
    return score || left.id.localeCompare(right.id);
  })[0] ?? null;
  const routeMinimum = selected === null ? null : routeMinimumAbility(selected);
  return freezeObject({
    version: PARTY_FOUNDATION_VERSION,
    partyId: request.party.id,
    leaderId,
    memberIds,
    selectedRouteId: selected?.id ?? null,
    selectedLiftId: selected?.liftId ?? null,
    selectedTrailId: selected?.trailId ?? null,
    weakestMemberId: weakest.id,
    weakestAbility,
    routeMinimumAbility: routeMinimum,
    weakestMemberSafe: selected !== null && routeMinimum! <= weakestAbility,
    canProceed: selected !== null,
    createdTick: tick,
    revision: 1,
    ownership: 'leader-owned-shared-plan',
  });
}

/** Check every member against the route selected by the shared plan. */
export function evaluatePartyPlanSafety(
  plan: PartyPlan,
  members: readonly PartyMemberProfile[],
): PartyPlanSafety {
  const byId = new Map(members.map((member) => [member.id, member]));
  const memberSafety = plan.memberIds.map((memberId) => {
    const member = byId.get(memberId);
    if (!member) throw new RangeError(`Missing member ${memberId}`);
    const ability = memberAbility(member);
    return freezeObject({ memberId, ability, safe: plan.routeMinimumAbility !== null && ability >= plan.routeMinimumAbility });
  });
  const weakest = memberSafety.reduce((left, right) => right.ability < left.ability ? right : left);
  return freezeObject({
    safe: plan.canProceed && memberSafety.every((member) => member.safe),
    weakestMemberId: weakest.memberId,
    weakestAbility: weakest.ability,
    routeMinimumAbility: plan.routeMinimumAbility,
    members: freezeArray(memberSafety),
  });
}

export const assessWeakestMemberSafety = evaluatePartyPlanSafety;
export const isPartyPlanSafe = (plan: PartyPlan, members: readonly PartyMemberProfile[]): boolean => evaluatePartyPlanSafety(plan, members).safe;

/** Build a rendezvous token with all member IDs intact after a split chair ride. */
export function createPartyRendezvous(request: PartyRendezvousRequest): PartyRendezvousPlan {
  assertNonEmptyString(request.locationId, 'rendezvous locationId');
  const memberIds = freezeArray(request.memberIds ?? request.party.guestIds.map(String));
  if (memberIds.length === 0 || new Set(memberIds).size !== memberIds.length) throw new RangeError('Rendezvous members must be unique and non-empty');
  if (memberIds.some((id) => !request.party.guestIds.includes(id))) throw new RangeError('Rendezvous member is not in the party');
  const leaderId = request.leaderId ?? String(request.party.guestIds[0]);
  if (!memberIds.includes(leaderId)) throw new RangeError('Rendezvous leader must be a rendezvous member');
  const targetTick = asSimulatedSecond(request.targetTick, 'rendezvous target tick');
  return freezeObject({
    version: PARTY_FOUNDATION_VERSION,
    id: `rendezvous:${request.party.id}:${request.locationId}:${targetTick}`,
    partyId: request.party.id,
    leaderId,
    locationId: request.locationId,
    targetTick,
    memberIds,
    ownership: 'leader-owned-shared-plan',
  });
}

export const planPartyRendezvous = createPartyRendezvous;

/** Friendly spelling used by callers that model the leader as a coordinator. */
export const createSharedPartyPlan = createPartyPlan;
export const planParty = createPartyPlan;

function assertChair(chair: PartyChair): void {
  assertNonEmptyString(chair.id, 'chair.id');
  if (!Number.isSafeInteger(chair.capacity) || chair.capacity < 1) throw new RangeError(`Chair ${chair.id} capacity must be positive`);
  if (chair.departureTick !== undefined) assertNonNegativeInteger(chair.departureTick, 'chair departure tick');
}

/** Split members in stable order into consecutive chair assignments. */
export function splitPartyAcrossConsecutiveChairs(request: ConsecutiveChairBoardingRequest): readonly ChairBoardingAssignment[] {
  assertNonEmptyString(request.partyId, 'partyId');
  if (request.memberIds.length === 0) return freezeArray([]);
  if (new Set(request.memberIds).size !== request.memberIds.length) throw new RangeError('Party member IDs must be unique');
  const startChairIndex = request.startChairIndex ?? 0;
  const queueRound = request.queueRound ?? 0;
  assertNonNegativeInteger(startChairIndex, 'start chair index');
  assertNonNegativeInteger(queueRound, 'queue round');
  const chairIds = new Set<string>();
  request.chairs.forEach((chair) => {
    assertChair(chair);
    if (chairIds.has(chair.id)) throw new RangeError(`Duplicate chair ${chair.id}`);
    chairIds.add(chair.id);
  });
  const assignments: ChairBoardingAssignment[] = [];
  let offset = 0;
  for (let chairIndex = startChairIndex; offset < request.memberIds.length; chairIndex += 1) {
    const chair = request.chairs[chairIndex];
    if (!chair) throw new RangeError('Not enough consecutive chairs for party members');
    const memberIds = request.memberIds.slice(offset, offset + chair.capacity);
    assignments.push(freezeObject({ partyId: request.partyId, chairId: chair.id, memberIds: freezeArray(memberIds), departureTick: chair.departureTick, queueRound }));
    offset += memberIds.length;
  }
  return freezeArray(assignments);
}

export const planConsecutiveChairBoarding = splitPartyAcrossConsecutiveChairs;
export const splitAcrossConsecutiveChairs = splitPartyAcrossConsecutiveChairs;

/**
 * Capacity-only variant for queue adapters that do not yet have chair IDs.
 * The returned groups are still ordered and each group is one physical chair;
 * callers can attach IDs before applying them to accounting.
 */
export function splitMembersIntoChairGroups(memberIds: readonly string[], chairCapacity: number): readonly (readonly string[])[] {
  if (!Number.isSafeInteger(chairCapacity) || chairCapacity < 1) throw new RangeError('chairCapacity must be positive');
  if (new Set(memberIds).size !== memberIds.length) throw new RangeError('Party member IDs must be unique');
  const groups: string[][] = [];
  for (let offset = 0; offset < memberIds.length; offset += chairCapacity) groups.push(memberIds.slice(offset, offset + chairCapacity));
  return freezeArray(groups.map((group) => freezeArray(group)));
}

function remainingMemberIds(entry: PartyQueueEntry): readonly string[] {
  const boarded = new Set(entry.boardedMemberIds ?? []);
  if (new Set(entry.memberIds).size !== entry.memberIds.length) throw new RangeError(`Queue entry ${entry.partyId} contains duplicate members`);
  for (const memberId of boarded) if (!entry.memberIds.includes(memberId)) throw new RangeError(`Queue entry ${entry.partyId} boarded an unknown member`);
  return freezeArray(entry.memberIds.filter((memberId) => !boarded.has(memberId)));
}

/**
 * Fair, deterministic chair scheduling. Each party receives at most one
 * consecutive chair per round by default. This prevents an oversized party
 * from monopolising a finite chair list while still giving it every remaining
 * member after enough rounds.
 */
export function schedulePartyQueueBoarding(request: QueueBoardingRequest): QueueBoardingResult {
  const maxChairsPerRound = request.maxChairsPerRound ?? 1;
  if (!Number.isSafeInteger(maxChairsPerRound) || maxChairsPerRound < 1) throw new RangeError('maxChairsPerRound must be positive');
  const entries = request.queue.slice().sort((left, right) => left.queueOrder - right.queueOrder || left.partyId.localeCompare(right.partyId));
  const seenParties = new Set<string>();
  for (const entry of entries) {
    assertNonEmptyString(entry.partyId, 'queue partyId');
    assertNonNegativeInteger(entry.queueOrder, 'queue order');
    asSimulatedSecond(entry.enqueuedTick, 'queue enqueued tick');
    if (seenParties.has(entry.partyId)) throw new RangeError(`Duplicate queue party ${entry.partyId}`);
    seenParties.add(entry.partyId);
  }
  const chairs = request.chairs.slice();
  chairs.forEach(assertChair);
  const chairIds = new Set<string>();
  for (const chair of chairs) {
    if (chairIds.has(chair.id)) throw new RangeError(`Duplicate chair ${chair.id}`);
    chairIds.add(chair.id);
  }
  const remaining = new Map(entries.map((entry) => [entry.partyId, remainingMemberIds(entry)]));
  const boarded = new Map(entries.map((entry) => [entry.partyId, new Set(entry.boardedMemberIds ?? [])]));
  const assignments: ChairBoardingAssignment[] = [];
  let chairIndex = 0;
  let round = 0;
  while (chairIndex < chairs.length && entries.some((entry) => (remaining.get(entry.partyId)?.length ?? 0) > 0)) {
    let assignedThisRound = 0;
    for (const entry of entries) {
      if (chairIndex >= chairs.length || assignedThisRound >= entries.length * maxChairsPerRound) break;
      let partyRemaining = remaining.get(entry.partyId)!;
      let partyChairs = 0;
      while (chairIndex < chairs.length && partyRemaining.length > 0 && partyChairs < maxChairsPerRound) {
        const chair = chairs[chairIndex];
        const memberIds = partyRemaining.slice(0, chair.capacity);
        partyRemaining = freezeArray(partyRemaining.slice(memberIds.length));
        remaining.set(entry.partyId, partyRemaining);
        for (const memberId of memberIds) boarded.get(entry.partyId)!.add(memberId);
        assignments.push(freezeObject({ partyId: entry.partyId, chairId: chair.id, memberIds: freezeArray(memberIds), departureTick: chair.departureTick, queueRound: round }));
        chairIndex += 1;
        partyChairs += 1;
        assignedThisRound += 1;
      }
    }
    if (assignedThisRound === 0) break;
    round += 1;
  }
  const resultQueue = entries.map((entry) => freezeObject({
    ...entry,
    boardedMemberIds: freezeArray([...boarded.get(entry.partyId)!]),
  }));
  const waits = entries.map((entry) => {
    const first = assignments.findIndex((assignment) => assignment.partyId === entry.partyId);
    return first < 0 ? 0 : assignments[first].queueRound;
  });
  return freezeObject({ assignments: freezeArray(assignments), queue: freezeArray(resultQueue), rounds: round, maxPartyWaitRounds: Math.max(0, ...waits) });
}

export const planFairPartyBoarding = schedulePartyQueueBoarding;
export const scheduleBoarding = schedulePartyQueueBoarding;

function identityFor(memberId: string): PartyMemberIdentity {
  assertNonEmptyString(memberId, 'memberId');
  return freezeObject({
    renderedDotId: `guest-dot:${memberId}`,
    seatIdentity: `guest-seat:${memberId}`,
    occupancyIdentity: `guest-occupancy:${memberId}`,
    thoughtIdentity: `guest-thought:${memberId}`,
    injuryRollIdentity: `guest-injury:${memberId}`,
    persistenceIdentity: `guest-persistence:${memberId}`,
  });
}

export function createPartyMemberIdentity(memberId: string): PartyMemberIdentity {
  return identityFor(memberId);
}

export const createMemberIdentity = createPartyMemberIdentity;
export const createMemberPresentationIdentity = createPartyMemberIdentity;

/** Create exactly one deterministic cohesion/injury/thought record per member. */
export function rollPartyMemberOutcomes(request: PartyOutcomeRequest): readonly PartyMemberOutcome[] {
  const memberIds = memberIdsForParty(request.party, request.members);
  const byId = new Map(request.members.map((member) => [member.id, member]));
  const ordinal = request.decisionOrdinal ?? 0;
  assertNonNegativeInteger(ordinal, 'party decision ordinal');
  const tick = asSimulatedSecond(request.tick, 'party outcome tick');
  const safety = request.plan.routeMinimumAbility ?? 0;
  const difficulty = Math.max(0, Math.min(1, safety));
  return freezeArray(memberIds.map((memberId) => {
    const member = byId.get(memberId)!;
    const patience = memberPatience(member);
    const cohesionRoll = keyedRandomFloat(request.worldSeed, memberId, 'party-cohesion', ordinal);
    const followsPlan = memberId === request.plan.leaderId || cohesionRoll < 0.55 + patience * 0.4;
    const injuryRoll = keyedRandomFloat(request.worldSeed, memberId, 'party-injury', ordinal);
    // Risk uses this member's attributes and route difficulty only. Party size
    // is intentionally absent: there is no authoritative group multiplier.
    const injuryProbability = Math.max(0, Math.min(1, 0.005 + (1 - memberAbility(member)) * 0.025 + difficulty * 0.02
      + (1 - memberRiskTolerance(member)) * 0.01));
    const identity = identityFor(memberId);
    return freezeObject({
      memberId,
      partyId: request.party.id,
      identity,
      cohesionRoll,
      followsPlan,
      injuryRoll,
      injuryProbability,
      injured: injuryRoll < injuryProbability,
      thought: freezeObject({ id: identity.thoughtIdentity, memberId, partyId: request.party.id, tick, text: followsPlan ? 'Following the party plan.' : 'Keeping a little space from the party plan.' }),
    });
  }));
}

export const evaluatePartyMemberOutcomes = rollPartyMemberOutcomes;
export const deterministicPartyCohesion = rollPartyMemberOutcomes;
export const rollMemberOutcomes = rollPartyMemberOutcomes;

function blankRecord(member: PartyMemberProfile, queuedTick: SimulatedSecond): PartyMemberAccountingRecord {
  return freezeObject({ memberId: member.id, partyId: member.partyId, location: 'queue', identity: identityFor(member.id), queuedTick,
    chairId: null, seatIndex: null, liftRideId: null, trailId: null, exitedTick: null });
}

/** Create an accounting ledger with one and only one record per member. */
export function createPartyAccounting(
  members: readonly PartyMemberProfile[],
  queuedTick = 0,
): PartyAccountingState {
  const tick = asSimulatedSecond(queuedTick, 'queued tick');
  const seen = new Set<string>();
  const records = members.map((member) => {
    assertNonEmptyString(member.id, 'member.id');
    assertNonEmptyString(member.partyId, 'member.partyId');
    if (seen.has(member.id)) throw new RangeError(`Duplicate member ${member.id}`);
    seen.add(member.id);
    return blankRecord(member, tick);
  });
  return freezeObject({ version: PARTY_FOUNDATION_VERSION, members: freezeArray(records) });
}

function recordMap(state: PartyAccountingState): Map<string, PartyMemberAccountingRecord> {
  if (state.version !== PARTY_FOUNDATION_VERSION) throw new RangeError('Unsupported party accounting version');
  const map = new Map<string, PartyMemberAccountingRecord>();
  for (const record of state.members) {
    if (map.has(record.memberId)) throw new RangeError(`Duplicate accounting member ${record.memberId}`);
    map.set(record.memberId, record);
  }
  return map;
}

function withRecords(state: PartyAccountingState, map: Map<string, PartyMemberAccountingRecord>): PartyAccountingState {
  const records = state.members.map((record) => map.get(record.memberId)!);
  return freezeObject({ version: PARTY_FOUNDATION_VERSION, members: freezeArray(records) });
}

/** Move individually named queued members into lift seats. */
export function boardParty(
  state: PartyAccountingState,
  assignments: readonly ChairBoardingAssignment[],
  boardedTick = 0,
): PartyAccountingState {
  const tick = asSimulatedSecond(boardedTick, 'boarded tick');
  const map = recordMap(state);
  const seenMembers = new Set<string>();
  const seenSeats = new Set<string>();
  for (const assignment of assignments) {
    assertNonEmptyString(assignment.partyId, 'assignment.partyId');
    assertNonEmptyString(assignment.chairId, 'assignment.chairId');
    assignment.memberIds.forEach((memberId, seatIndex) => {
      if (seenMembers.has(memberId)) throw new RangeError(`Member ${memberId} appears in more than one boarding assignment`);
      seenMembers.add(memberId);
      const record = map.get(memberId);
      if (!record) throw new RangeError(`Unknown accounting member ${memberId}`);
      if (record.partyId !== assignment.partyId) throw new RangeError(`Member ${memberId} is not in party ${assignment.partyId}`);
      if (record.location !== 'queue') throw new RangeError(`Member ${memberId} is not queued`);
      const seatKey = `${assignment.chairId}:${seatIndex}`;
      if (seenSeats.has(seatKey)) throw new RangeError(`Seat ${seatKey} is assigned twice`);
      seenSeats.add(seatKey);
      map.set(memberId, freezeObject({ ...record, location: 'lift', chairId: assignment.chairId, seatIndex,
        liftRideId: `lift-ride:${assignment.chairId}:${tick}`, exitedTick: null }));
    });
  }
  return withRecords(state, map);
}

export const applyPartyBoarding = boardParty;
export const applyBoardingAssignments = boardParty;

/** Complete a lift ride for exactly the named members. */
export function movePartyMembersToTrail(
  state: PartyAccountingState,
  memberIds: readonly string[],
  trailId: string,
): PartyAccountingState {
  assertNonEmptyString(trailId, 'trailId');
  const map = recordMap(state);
  const seen = new Set<string>();
  for (const memberId of memberIds) {
    if (seen.has(memberId)) throw new RangeError(`Duplicate trail member ${memberId}`);
    seen.add(memberId);
    const record = map.get(memberId);
    if (!record) throw new RangeError(`Unknown accounting member ${memberId}`);
    if (record.location !== 'lift') throw new RangeError(`Member ${memberId} is not on a lift`);
    map.set(memberId, freezeObject({ ...record, location: 'trail', trailId }));
  }
  return withRecords(state, map);
}

export const completePartyLiftRide = movePartyMembersToTrail;
export const advanceMembersToTrail = movePartyMembersToTrail;

/** Exit exactly the named trail members while retaining all identity records. */
export function exitPartyMembers(
  state: PartyAccountingState,
  memberIds: readonly string[],
  exitedTick = 0,
): PartyAccountingState {
  const tick = asSimulatedSecond(exitedTick, 'exited tick');
  const map = recordMap(state);
  const seen = new Set<string>();
  for (const memberId of memberIds) {
    if (seen.has(memberId)) throw new RangeError(`Duplicate exiting member ${memberId}`);
    seen.add(memberId);
    const record = map.get(memberId);
    if (!record) throw new RangeError(`Unknown accounting member ${memberId}`);
    if (record.location !== 'trail') throw new RangeError(`Member ${memberId} is not on a trail`);
    map.set(memberId, freezeObject({ ...record, location: 'exit', exitedTick: tick }));
  }
  return withRecords(state, map);
}

export const completePartyTrail = exitPartyMembers;
export const exitMembers = exitPartyMembers;

export function partyConservation(state: PartyAccountingState): PartyConservation {
  const counts: Record<PartyMemberLocation, number> = { queue: 0, lift: 0, trail: 0, exit: 0 };
  const seen = new Set<string>();
  for (const record of state.members) {
    if (seen.has(record.memberId)) throw new RangeError(`Duplicate accounting member ${record.memberId}`);
    seen.add(record.memberId);
    counts[record.location] += 1;
  }
  const total = state.members.length;
  return freezeObject({ total, byLocation: freezeObject(counts), conserved: Object.values(counts).reduce((sum, count) => sum + count, 0) === total });
}

export function assertPartyConservation(state: PartyAccountingState, expectedTotal = state.members.length): void {
  if (!Number.isSafeInteger(expectedTotal) || expectedTotal < 0) throw new RangeError('expectedTotal must be non-negative');
  const result = partyConservation(state);
  if (!result.conserved || result.total !== expectedTotal) throw new RangeError(`Party accounting conservation failed: expected ${expectedTotal}, got ${result.total}`);
}

export const countPartyMembers = partyConservation;
export const conservationForParty = partyConservation;

export interface PartyAccountingSave {
  readonly version: PartyFoundationVersion;
  readonly members: readonly PartyMemberAccountingRecord[];
}

/** Stable JSON boundary for save/load tests and a later GameSave adapter. */
export function serializePartyAccounting(state: PartyAccountingState): string {
  assertPartyConservation(state);
  const canonical = state.members.slice().sort((left, right) => left.memberId.localeCompare(right.memberId));
  return JSON.stringify({ version: PARTY_FOUNDATION_VERSION, members: canonical });
}

function parseSave(value: string | PartyAccountingSave): PartyAccountingSave {
  const parsed: unknown = typeof value === 'string' ? JSON.parse(value) : value;
  if (!parsed || typeof parsed !== 'object' || (parsed as PartyAccountingSave).version !== PARTY_FOUNDATION_VERSION || !Array.isArray((parsed as PartyAccountingSave).members)) {
    throw new RangeError('Invalid party accounting save');
  }
  return parsed as PartyAccountingSave;
}

export function deserializePartyAccounting(value: string | PartyAccountingSave): PartyAccountingState {
  const save = parseSave(value);
  const records = save.members.map((record) => {
    assertNonEmptyString(record.memberId, 'saved memberId');
    assertNonEmptyString(record.partyId, 'saved partyId');
    if (!['queue', 'lift', 'trail', 'exit'].includes(record.location)) throw new RangeError(`Invalid location for ${record.memberId}`);
    const identity = identityFor(record.memberId);
    if (JSON.stringify(record.identity) !== JSON.stringify(identity)) throw new RangeError(`Invalid identity for ${record.memberId}`);
    return freezeObject({ ...record, identity });
  });
  const state = freezeObject({ version: PARTY_FOUNDATION_VERSION, members: freezeArray(records) });
  assertPartyConservation(state);
  return state;
}

export const savePartyAccounting = serializePartyAccounting;
export const loadPartyAccounting = deserializePartyAccounting;
export const createPartyLedger = createPartyAccounting;
export const savePartyLedger = serializePartyAccounting;
export const loadPartyLedger = deserializePartyAccounting;
