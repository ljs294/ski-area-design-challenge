import { DEFAULT_GUEST_SIMULATION_CONFIG } from './config.ts';
import type {
  DemandPlan,
  DemandWave,
  Guest,
  GuestPortal,
  GuestSimulationEnvironmentSnapshot,
  GuestSimulationSnapshot,
  GuestState,
  Incident,
  Party,
  PartyState,
  RunConditionSnapshot,
  SimulatedSecond,
  ThoughtEvent,
} from './contracts.ts';
import { GUEST_SIMULATION_CONTRACT_VERSION, GUEST_SIMULATION_PROTOCOL_VERSION, asSimulatedSecond, isSimulatedSecond } from './contracts.ts';

export const GUEST_FIXTURE_COUNTS = [1_000, 10_000, 25_000, 50_000] as const;
export type GuestFixtureSize = (typeof GUEST_FIXTURE_COUNTS)[number];

export type GuestFixtureScenario =
  | 'weekend-waves'
  | 'heavy-groups'
  | 'closure-storms'
  | 'deteriorating-conditions'
  | 'peak-save-load-metadata';

export const GUEST_FIXTURE_SCENARIOS: readonly GuestFixtureScenario[] = Object.freeze([
  'weekend-waves',
  'heavy-groups',
  'closure-storms',
  'deteriorating-conditions',
  'peak-save-load-metadata',
]);

export interface GuestFixtureFeatureFlags {
  readonly weekendWaves: true;
  readonly heavyGroups: true;
  readonly closureStorms: true;
  readonly deterioratingConditions: true;
}

export interface PeakSaveLoadScenarioMetadata {
  readonly scenario: 'peak-save-load-metadata';
  /** Phase 0 exercises the snapshot contract; the binary round-trip lands in Phase 1C. */
  readonly materialization: 'metadata-only';
  readonly saveTick: SimulatedSecond;
  readonly loadTick: SimulatedSecond;
  readonly snapshotTick: SimulatedSecond;
  readonly expectedGuestCount: number;
  readonly expectedPartyCount: number;
  /** A planning bound, not a claim about the eventual save format. */
  readonly maxSnapshotBytes: number;
}

/**
 * A descriptor contains only counts and deterministic scenario metadata.  It
 * is safe to keep all four descriptors in a test or benchmark registry.
 */
export interface GuestSimulationFixtureDescriptor {
  readonly id: string;
  readonly name: string;
  readonly seed: string;
  readonly guestCount: GuestFixtureSize;
  readonly partyCount: number;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  readonly features: GuestFixtureFeatureFlags;
  readonly scenarios: readonly GuestFixtureScenario[];
  readonly peakSaveLoad: PeakSaveLoadScenarioMetadata;
}

export interface GuestSimulationFixture {
  readonly descriptor: GuestSimulationFixtureDescriptor;
  readonly demandPlan: DemandPlan;
  readonly environment: GuestSimulationEnvironmentSnapshot;
  /** Materialized only when read; 50k fixtures do not allocate this eagerly. */
  readonly guests: readonly Guest[];
  /** Materialized only when read; 50k fixtures do not allocate this eagerly. */
  readonly parties: readonly Party[];
  readonly createSnapshot: (tick?: SimulatedSecond) => GuestSimulationSnapshot;
}

interface PartySpec {
  readonly ordinal: number;
  readonly guestCount: number;
  readonly heavyGroup: boolean;
  readonly arrivalTick: SimulatedSecond;
  readonly plannedDepartureTick: SimulatedSecond;
  readonly kind: Party['kind'];
  readonly waveIndex: number;
}

const HOUR = 60 * 60;
const DAY = 24 * HOUR;
const WEEK = 7 * DAY;
const FIXTURE_SEED_PREFIX = 'guest-simulation-fixture-v1';
const PORTAL_COUNT = 4;
const PARTY_SIZE_PATTERN: readonly number[] = [1, 2, 2, 3, 4, 2, 1, 3, 2, 1, 3, 2];

const WAVE_STARTS: readonly Readonly<{ kind: DemandWave['kind']; startTick: SimulatedSecond; endTick: SimulatedSecond }>[] = Object.freeze([
  { kind: 'weekday', startTick: 0, endTick: 2 * HOUR },
  { kind: 'weekday', startTick: DAY, endTick: DAY + 2 * HOUR },
  { kind: 'weekend', startTick: 2 * DAY, endTick: 2 * DAY + 3 * HOUR },
  { kind: 'weekend', startTick: 2 * DAY + 4 * HOUR, endTick: 2 * DAY + 7 * HOUR },
  { kind: 'weekend', startTick: 3 * DAY, endTick: 3 * DAY + 3 * HOUR },
  { kind: 'weekend', startTick: 3 * DAY + 4 * HOUR, endTick: 3 * DAY + 7 * HOUR },
  { kind: 'weekday', startTick: 4 * DAY, endTick: 4 * DAY + 2 * HOUR },
  { kind: 'weekday', startTick: 5 * DAY, endTick: 5 * DAY + 2 * HOUR },
  { kind: 'weekday', startTick: 6 * DAY, endTick: 6 * DAY + 2 * HOUR },
]);

function freezeArray<T>(items: readonly T[]): readonly T[] {
  return Object.freeze([...items]);
}

function isFixtureSize(value: number): value is GuestFixtureSize {
  return (GUEST_FIXTURE_COUNTS as readonly number[]).includes(value);
}

export function isGuestFixtureSize(value: unknown): value is GuestFixtureSize {
  return typeof value === 'number' && isFixtureSize(value);
}

function assertFixtureSize(value: number): asserts value is GuestFixtureSize {
  if (!isFixtureSize(value)) {
    throw new RangeError(`Guest fixture size must be exactly one of ${GUEST_FIXTURE_COUNTS.join(', ')}`);
  }
}

function partySizeFor(ordinal: number): { guestCount: number; heavyGroup: boolean } {
  // Every nineteenth party is intentionally large enough to exercise group
  // behavior, while the regular pattern keeps small fixtures realistic.
  if (ordinal % 19 === 0) return { guestCount: 8 + (ordinal % 5), heavyGroup: true };
  return { guestCount: PARTY_SIZE_PATTERN[ordinal % PARTY_SIZE_PATTERN.length], heavyGroup: false };
}

function partyKindFor(ordinal: number, heavyGroup: boolean): Party['kind'] {
  if (heavyGroup) return ordinal % 2 === 0 ? 'club' : 'school';
  return (['individual', 'family', 'friends', 'family'] as const)[ordinal % 4];
}

function waveIndexFor(ordinal: number): number {
  // Eleven of every twenty parties are routed through the weekend waves.
  const weekend = ordinal % 20 < 11;
  if (weekend) return 2 + (ordinal % 4);
  return ordinal % 3 === 0 ? 0 : 1 + (ordinal % 2) * 5;
}

function partySpecsFor(descriptor: GuestSimulationFixtureDescriptor): readonly PartySpec[] {
  const specs: PartySpec[] = [];
  let assigned = 0;
  let ordinal = 0;
  while (assigned < descriptor.guestCount) {
    const requested = partySizeFor(ordinal);
    const guestCount = Math.min(requested.guestCount, descriptor.guestCount - assigned);
    const waveIndex = waveIndexFor(ordinal);
    const wave = WAVE_STARTS[waveIndex];
    const arrivalTick = wave.startTick + ((ordinal * 37) % Math.max(1, wave.endTick - wave.startTick));
    const plannedDepartureTick = Math.min(descriptor.endTick - 1, arrivalTick + 4 * HOUR + (ordinal % 5) * HOUR);
    specs.push({ ordinal, guestCount, heavyGroup: requested.heavyGroup && guestCount >= 6,
      arrivalTick: asSimulatedSecond(arrivalTick, 'fixture arrival tick'),
      plannedDepartureTick: asSimulatedSecond(plannedDepartureTick, 'fixture departure tick'),
      kind: partyKindFor(ordinal, requested.heavyGroup && guestCount >= 6), waveIndex });
    assigned += guestCount;
    ordinal += 1;
  }
  return freezeArray(specs);
}

function partyCountForGuestCount(guestCount: GuestFixtureSize): number {
  let assigned = 0;
  let parties = 0;
  while (assigned < guestCount) {
    assigned += Math.min(partySizeFor(parties).guestCount, guestCount - assigned);
    parties += 1;
  }
  return parties;
}

function heavyGroupCountFor(guestCount: GuestFixtureSize): number {
  let assigned = 0;
  let parties = 0;
  let heavy = 0;
  while (assigned < guestCount) {
    const size = partySizeFor(parties);
    const actual = Math.min(size.guestCount, guestCount - assigned);
    if (size.heavyGroup && actual >= 6) heavy += 1;
    assigned += actual;
    parties += 1;
  }
  return heavy;
}

function peakSaveLoadFor(guestCount: GuestFixtureSize, partyCount: number, endTick: SimulatedSecond): PeakSaveLoadScenarioMetadata {
  const saveTick = asSimulatedSecond(Math.floor(endTick * 0.82), 'fixture save tick');
  return Object.freeze({ scenario: 'peak-save-load-metadata', materialization: 'metadata-only', saveTick, loadTick: saveTick + 1_800,
    snapshotTick: saveTick, expectedGuestCount: guestCount, expectedPartyCount: partyCount,
    maxSnapshotBytes: 600_000 + guestCount * 640 + partyCount * 240 });
}

const GUEST_FIXTURE_FEATURES: GuestFixtureFeatureFlags = Object.freeze({
  weekendWaves: true,
  heavyGroups: true,
  closureStorms: true,
  deterioratingConditions: true,
});

function makeDescriptor(guestCount: GuestFixtureSize): GuestSimulationFixtureDescriptor {
  const partyCount = partyCountForGuestCount(guestCount);
  const endTick = WEEK;
  return Object.freeze({ id: `guest-fixture-${guestCount}`, name: `${guestCount.toLocaleString('en-US')} guest simulation`,
    seed: `${FIXTURE_SEED_PREFIX}-${guestCount}`, guestCount, partyCount, startTick: 0, endTick,
    features: GUEST_FIXTURE_FEATURES,
    scenarios: GUEST_FIXTURE_SCENARIOS, peakSaveLoad: peakSaveLoadFor(guestCount, partyCount, endTick) });
}

export const GUEST_SIMULATION_FIXTURE_DESCRIPTORS: readonly GuestSimulationFixtureDescriptor[] = Object.freeze(
  GUEST_FIXTURE_COUNTS.map((guestCount) => makeDescriptor(guestCount)),
);

export function getGuestSimulationFixtureDescriptor(size: GuestFixtureSize): GuestSimulationFixtureDescriptor {
  assertFixtureSize(size);
  return GUEST_SIMULATION_FIXTURE_DESCRIPTORS.find((descriptor) => descriptor.guestCount === size)!;
}

export const describeGuestSimulationFixture = getGuestSimulationFixtureDescriptor;

export function createGuestSimulationFixtureDescriptors(): readonly GuestSimulationFixtureDescriptor[] {
  return GUEST_SIMULATION_FIXTURE_DESCRIPTORS;
}

function guestId(ordinal: number): string {
  return `guest-${String(ordinal + 1).padStart(6, '0')}`;
}

function partyId(ordinal: number): string {
  return `party-${String(ordinal + 1).padStart(5, '0')}`;
}

function portalId(index: number): string {
  return `guest-entrance-${index + 1}`;
}

function preferencesFor(ordinal: number): Guest['preferences'] {
  const ageBands = ['child', 'teen', 'adult', 'senior'] as const;
  const ability = (ordinal * 37 % 101) / 100;
  const abilityBand = ability < 0.25 ? 'beginner' : ability < 0.55 ? 'intermediate' : ability < 0.82 ? 'advanced' : 'expert';
  const tripCashCents = 7_500 + (ordinal % 12) * 2_500;
  const economicSegments = ['budget', 'standard', 'premium', 'luxury'] as const;
  return { experience: abilityBand, abilityBand, ability, ageBand: ageBands[(ordinal * 3) % ageBands.length],
    wantsLessons: ordinal % 7 === 0, budgetCents: tripCashCents, economicSegment: economicSegments[ordinal % economicSegments.length],
    tripCashCents, riskTolerance: (ordinal * 17 % 101) / 100, comfortDemand: (ordinal * 29 % 101) / 100,
    hardcoreTerrainPreference: (ordinal * 43 % 101) / 100, priceSensitivity: (ordinal * 59 % 101) / 100,
    frugality: (ordinal * 71 % 101) / 100, patience: (ordinal * 83 % 101) / 100,
    varietySeeking: (ordinal * 97 % 101) / 100 };
}

/** Materializes only the flat guest records, without simulation state. */
export function generateGuests(descriptor: GuestSimulationFixtureDescriptor): readonly Guest[] {
  const guests: Guest[] = [];
  for (const spec of partySpecsFor(descriptor)) {
    for (let member = 0; member < spec.guestCount; member += 1) {
      const ordinal = guests.length;
      const arrivalTick = asSimulatedSecond(Math.min(descriptor.endTick - 1, spec.arrivalTick + Math.min(member, 3)), 'fixture guest arrival tick');
      guests.push({ id: guestId(ordinal), partyId: partyId(spec.ordinal), ordinal, arrivalTick,
        plannedDepartureTick: spec.plannedDepartureTick, portalId: portalId(spec.ordinal % PORTAL_COUNT),
        preferences: preferencesFor(ordinal), futurePartyId: null });
    }
  }
  return freezeArray(guests);
}

/** Materializes flat party records with stable guest ID references. */
export function generateParties(descriptor: GuestSimulationFixtureDescriptor): readonly Party[] {
  let guestOrdinal = 0;
  const parties: Party[] = [];
  for (const spec of partySpecsFor(descriptor)) {
    const guestIds = Array.from({ length: spec.guestCount }, (_, member) => guestId(guestOrdinal + member));
    parties.push({ id: partyId(spec.ordinal), guestIds: freezeArray(guestIds), size: spec.guestCount,
      kind: spec.kind, heavyGroup: spec.heavyGroup, arrivalTick: spec.arrivalTick,
      plannedDepartureTick: spec.plannedDepartureTick, futurePartyId: null });
    guestOrdinal += spec.guestCount;
  }
  return freezeArray(parties);
}

function createWaves(specs: readonly PartySpec[]): readonly DemandWave[] {
  return freezeArray(WAVE_STARTS.map((wave, index) => {
    const selected = specs.filter((spec) => spec.waveIndex === index);
    return Object.freeze({ id: `wave-${String(index + 1).padStart(2, '0')}`, kind: wave.kind,
      startTick: wave.startTick, endTick: wave.endTick,
      guestCount: selected.reduce((sum, spec) => sum + spec.guestCount, 0), partyCount: selected.length });
  }));
}

export function createDemandPlan(descriptor: GuestSimulationFixtureDescriptor): DemandPlan {
  const specs = partySpecsFor(descriptor);
  return Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, seed: descriptor.seed,
    guestCount: descriptor.guestCount, partyCount: descriptor.partyCount, startTick: descriptor.startTick,
    endTick: descriptor.endTick, waves: createWaves(specs), heavyGroupCount: heavyGroupCountFor(descriptor.guestCount) });
}

export function createGuestPortals(endTick = WEEK): readonly GuestPortal[] {
  if (!isSimulatedSecond(endTick) || endTick <= 0) throw new RangeError('Portal horizon must be a positive integer tick');
  return freezeArray(Array.from({ length: PORTAL_COUNT }, (_, index) => Object.freeze({
    version: GUEST_SIMULATION_CONTRACT_VERSION, id: portalId(index), kind: 'guest-entrance' as const,
    type: 'guest-entrance' as const, semantics: 'guest-entrance' as const, direction: 'inbound' as const,
    accepts: 'guests' as const, label: `Guest Entrance ${index + 1}`, capacityGuestsPerTick: 2 + (index % 2),
    openFromTick: 0, openUntilTick: endTick,
  })));
}

export function createRunConditionSnapshot(descriptor: GuestSimulationFixtureDescriptor, tick = 0): RunConditionSnapshot {
  if (!isSimulatedSecond(tick) || tick > descriptor.endTick) throw new RangeError('Condition tick is outside fixture bounds');
  const progress = tick / descriptor.endTick;
  const status: RunConditionSnapshot['status'] = progress >= 0.84 ? 'severe' : progress >= 0.62 ? 'poor' : progress >= 0.35 ? 'fair' : 'good';
  return Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, tick,
    status, trend: progress === 0 ? 'stable' : 'deteriorating', temperatureC: -2 - 9 * progress,
    windKph: 12 + 32 * progress, visibilityKm: Math.max(1, 18 - 14 * progress),
    precipitationMm: progress < 0.25 ? 0 : 0.4 + 2.2 * progress, snowfallCm: progress < 0.4 ? 0 : 0.2 + 2.8 * progress,
    terrainOpenFraction: Math.max(0.2, 1 - 0.7 * progress), liftOpenFraction: Math.max(0.1, 1 - 0.8 * progress),
    trailOpenFraction: Math.max(0.15, 1 - 0.65 * progress) });
}

export function createClosureStormIncidents(descriptor: GuestSimulationFixtureDescriptor): readonly Incident[] {
  const stormStart = asSimulatedSecond(Math.floor(descriptor.endTick * 0.58), 'closure storm start tick');
  const stormEnd = asSimulatedSecond(Math.floor(descriptor.endTick * 0.78), 'closure storm end tick');
  return freezeArray([
    { version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'incident-closure-storm-01', kind: 'closure-storm' as const,
      severity: 'major' as const, startTick: stormStart, endTick: stormEnd, affectedPortalId: null,
      affectedResourceId: null, message: 'Closure storm reduces operating capacity across the mountain.' },
    { version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'incident-portal-closure-01', kind: 'portal-closure' as const,
      severity: 'major' as const, startTick: stormStart + 900, endTick: stormEnd, affectedPortalId: portalId(1),
      affectedResourceId: null, message: 'Guest Entrance 2 is closed during the closure storm.' },
    { version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'incident-lift-closure-01', kind: 'lift-closure' as const,
      severity: 'major' as const, startTick: stormStart + 1_800, endTick: stormEnd - 900, affectedPortalId: null,
      affectedResourceId: 'lift-03', message: 'Lift 3 is closed during the closure storm.' },
    { version: GUEST_SIMULATION_CONTRACT_VERSION, id: 'incident-trail-closure-01', kind: 'trail-closure' as const,
      severity: 'minor' as const, startTick: stormStart + 2_700, endTick: stormEnd, affectedPortalId: null,
      affectedResourceId: 'trail-07', message: 'Trail 7 is closed while conditions deteriorate.' },
  ]);
}

export function createEnvironmentSnapshot(descriptor: GuestSimulationFixtureDescriptor, tick = 0): GuestSimulationEnvironmentSnapshot {
  const conditions = createRunConditionSnapshot(descriptor, tick);
  const incidents = createClosureStormIncidents(descriptor);
  return Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, tick, environmentRevision: 1, topologyRevision: 1,
    operating: tick < descriptor.endTick,
    conditions, portals: createGuestPortals(descriptor.endTick), incidents });
}

function createSnapshot(descriptor: GuestSimulationFixtureDescriptor, demandPlan: DemandPlan, tick: SimulatedSecond): GuestSimulationSnapshot {
  if (!isSimulatedSecond(tick) || tick > descriptor.endTick) throw new RangeError('Snapshot tick is outside fixture bounds');
  const guests: GuestState[] = generateGuests(descriptor).map((guest) => ({ ...guest,
    status: guest.arrivalTick > tick ? 'scheduled' : guest.plannedDepartureTick !== null && guest.plannedDepartureTick <= tick ? 'departed' : 'choosing',
    currentPortalId: guest.arrivalTick > tick ? guest.portalId : null, currentResourceId: null,
    satisfaction: guest.arrivalTick > tick ? 1 : Math.max(0, 1 - (tick - guest.arrivalTick) / descriptor.endTick) }));
  const parties: PartyState[] = generateParties(descriptor).map((party) => ({ ...party,
    status: party.arrivalTick > tick ? 'arriving' : party.plannedDepartureTick !== null && party.plannedDepartureTick <= tick ? 'departed' : 'active' }));
  const thoughtEvents = guests.slice(0, Math.min(24, guests.length)).map<ThoughtEvent>((guest, index) => ({
    version: GUEST_SIMULATION_CONTRACT_VERSION, id: `thought-${String(index + 1).padStart(4, '0')}`,
    tick: guest.arrivalTick <= tick ? guest.arrivalTick : tick, guestId: guest.id, partyId: guest.partyId,
    kind: guest.arrivalTick > tick ? 'waiting' : 'arrived', sentiment: guest.arrivalTick > tick ? 'neutral' : 'positive',
    text: guest.arrivalTick > tick ? 'Waiting for the planned arrival wave.' : 'Arrived at the resort.',
  })).sort((left, right) => left.tick - right.tick || left.guestId.localeCompare(right.guestId)
    || left.id.localeCompare(right.id));
  const environment = createEnvironmentSnapshot(descriptor, tick);
  return Object.freeze({ version: GUEST_SIMULATION_CONTRACT_VERSION, protocolVersion: GUEST_SIMULATION_PROTOCOL_VERSION,
    configVersion: DEFAULT_GUEST_SIMULATION_CONFIG.configVersion, runId: descriptor.id, tick, sequence: 0,
    environmentRevision: 1, topologyRevision: 1,
    checksum: `fixture-v1:${descriptor.seed}:${tick}:${descriptor.guestCount}:${descriptor.partyCount}`,
    guests: freezeArray(guests),
    parties: freezeArray(parties), demandPlan, environment, incidents: environment.incidents,
    thoughtEvents: freezeArray(thoughtEvents), futureParty: null });
}

export function createGuestSimulationFixture(size: GuestFixtureSize): GuestSimulationFixture {
  const descriptor = getGuestSimulationFixtureDescriptor(size);
  const demandPlan = createDemandPlan(descriptor);
  const environment = createEnvironmentSnapshot(descriptor, 0);
  let guestsCache: readonly Guest[] | undefined;
  let partiesCache: readonly Party[] | undefined;
  return {
    descriptor, demandPlan, environment,
    get guests() { return guestsCache ??= generateGuests(descriptor); },
    get parties() { return partiesCache ??= generateParties(descriptor); },
    createSnapshot: (tick = descriptor.peakSaveLoad.snapshotTick) => createSnapshot(descriptor, demandPlan, tick),
  };
}

export function createGuestSimulationFixtures(): readonly GuestSimulationFixtureDescriptor[] {
  // Return descriptors by default so callers can enumerate all scales without
  // paying for 86k+ rich records.  Use createGuestSimulationFixture(size) to
  // opt into lazy materialized records for one scale.
  return GUEST_SIMULATION_FIXTURE_DESCRIPTORS;
}

/** Compatibility aliases for callers that prefer “fixture” over “simulation”. */
export const createFixtureDescriptor = getGuestSimulationFixtureDescriptor;
export const createFixture = createGuestSimulationFixture;
