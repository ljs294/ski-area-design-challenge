/**
 * Phase 3 economy/reputation day boundary.
 *
 * The simulation opens a day with one frozen reputation profile. Guest
 * outcomes are collected during the day, but never feed back into decisions
 * made on that same day. `closePhase3Economy` is the only operation that
 * derives the next-day profile. Closing is idempotent by close id and the
 * guest/day key makes it impossible to count two visits for one guest.
 *
 * The profile has two intentionally different time constants: fast hype is
 * responsive to the latest day, while slow legacy changes gradually. Both
 * are tracked by guest segment and useful resort dimensions so demand can
 * later choose the appropriate view without inventing a second reputation
 * system. All arithmetic is integer basis points where practical.
 */

import type { Guest, SimulatedSecond } from './contracts.ts';
import type { TicketFinanceSnapshot } from './ticketFinance.ts';
import {
  REPUTATION_REASON_CODES,
  evaluateReputationSignal,
  type ReputationReasonCode,
  type ReputationSignal,
  type ReputationSignalRecord,
} from './reputation.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import { keyedRandomFloat, type RandomSeed } from './random.ts';

export const PHASE_3_ECONOMY_DOMAIN_VERSION = 1 as const;
export const PHASE_3_ECONOMY_FORMULA_VERSION = 1 as const;

export type Phase3EconomyDomainVersion = typeof PHASE_3_ECONOMY_DOMAIN_VERSION;
export type Phase3EconomyFormulaVersion = typeof PHASE_3_ECONOMY_FORMULA_VERSION;
export type ReputationSegment = Guest['preferences']['economicSegment'];
export type ReputationDimension = 'overall' | 'terrain' | 'comfort' | 'value' | 'safety' | 'service';

export const REPUTATION_SEGMENTS: readonly ReputationSegment[] = Object.freeze([
  'budget', 'standard', 'premium', 'luxury',
]);
export const REPUTATION_DIMENSIONS: readonly ReputationDimension[] = Object.freeze([
  'overall', 'terrain', 'comfort', 'value', 'safety', 'service',
]);

export const PHASE_3_ECONOMY_FORMULAS = Object.freeze({
  version: PHASE_3_ECONOMY_FORMULA_VERSION,
  baselineScoreBps: 5_000,
  minimumScoreBps: 0,
  maximumScoreBps: 10_000,
  /** Fast layer moves 65% of one day’s average outcome delta. */
  hypeLearningRate: 0.65,
  /** Legacy layer moves only 12% of one day’s average outcome delta. */
  legacyLearningRate: 0.12,
  /** Demand-facing blend favors durable legacy over volatile hype. */
  demandHypeWeight: 0.35,
  demandLegacyWeight: 0.65,
  /** A visit’s direct dimension observation is worth at most 100 bps. */
  visitDimensionMagnitudeBps: 100,
  defaultMaximumVisitOutcomes: 100_000,
  maximumSignalsPerVisit: 32,
  syntheticPeerBaseBps: 5_000,
  syntheticPeerSpreadBps: 1_000,
} as const);

export type ReputationMatrix = Readonly<Record<ReputationDimension, Readonly<Record<ReputationSegment | 'all', number>>>>;
export type ReputationMatrixInput = Partial<Record<ReputationDimension, Partial<Record<ReputationSegment | 'all', number>>>>;

export interface ReputationProfile {
  readonly version: Phase3EconomyDomainVersion;
  readonly formulaVersion: Phase3EconomyFormulaVersion;
  readonly hype: ReputationMatrix;
  readonly legacy: ReputationMatrix;
  readonly checksum: string;
}

export interface ReputationProfileInput {
  readonly hype?: ReputationMatrixInput;
  readonly legacy?: ReputationMatrixInput;
  readonly baselineScoreBps?: number;
}

export interface SyntheticPeerBaseline {
  readonly version: Phase3EconomyDomainVersion;
  readonly source: 'synthetic';
  /** This marker is contractual: it must never feed demand or next-day state. */
  readonly displayOnly: true;
  readonly worldSeed: string;
  readonly dimensions: Readonly<Record<ReputationDimension, number>>;
  readonly segments: Readonly<Record<ReputationSegment, number>>;
  readonly checksum: string;
}

export interface VisitOutcomeInput {
  readonly dayId: string;
  readonly guestId: string;
  readonly segment: ReputationSegment;
  readonly tick?: SimulatedSecond;
  /** Overall visit satisfaction. It is always the fallback for dimensions. */
  readonly satisfaction: number;
  /** Optional per-dimension scores; unspecified dimensions use satisfaction. */
  readonly dimensionScores?: Partial<Record<ReputationDimension, number>>;
  /** Experience and safety overlays observed during this visit. */
  readonly signals?: readonly ReputationSignal[];
}

export interface VisitOutcomeRecord {
  readonly version: Phase3EconomyDomainVersion;
  readonly formulaVersion: Phase3EconomyFormulaVersion;
  readonly id: string;
  readonly dayId: string;
  readonly guestId: string;
  readonly segment: ReputationSegment;
  readonly tick: SimulatedSecond | null;
  readonly satisfaction: number;
  readonly dimensionScores: Readonly<Record<ReputationDimension, number>>;
  readonly signals: readonly ReputationSignalRecord[];
  /** Signed overall additive delta in basis points. */
  readonly deltaBps: number;
  readonly deltaByDimensionBps: Readonly<Record<ReputationDimension, number>>;
  readonly checksum: string;
}

export interface EconomyMetrics {
  readonly visitCount: number;
  readonly positiveVisitCount: number;
  readonly neutralVisitCount: number;
  readonly negativeVisitCount: number;
  readonly signalCount: number;
  readonly netVisitDeltaBps: number;
  readonly averageSatisfaction: number | null;
  readonly ticketCount: number;
  readonly ticketRevenueCents: number;
  readonly ticketRevenueBySegment: Readonly<Record<ReputationSegment, number>>;
}

export interface DayCloseRecord {
  readonly closeId: string;
  readonly closedTick: SimulatedSecond;
  readonly nextDayReputation: ReputationProfile;
  readonly ticketFinanceChecksum: string | null;
  readonly checksum: string;
}

export interface Phase3EconomySnapshot {
  readonly version: Phase3EconomyDomainVersion;
  readonly formulaVersion: Phase3EconomyFormulaVersion;
  readonly dayId: string;
  /** Never changes during this day, even after closing. */
  readonly openingReputation: ReputationProfile;
  /** Synthetic comparison data is display-only and has no simulation effect. */
  readonly syntheticPeerBaseline: SyntheticPeerBaseline;
  readonly ticketFinance: TicketFinanceSnapshot | null;
  readonly maximumVisitOutcomes: number;
  readonly visitOutcomes: readonly VisitOutcomeRecord[];
  readonly metrics: EconomyMetrics;
  readonly closed: boolean;
  readonly closing: DayCloseRecord | null;
  readonly checksum: string;
}

export interface CreatePhase3EconomyInput {
  readonly dayId: string;
  readonly openingReputation?: ReputationProfile | ReputationProfileInput;
  readonly syntheticPeerBaseline?: SyntheticPeerBaseline;
  readonly ticketFinance?: TicketFinanceSnapshot;
  readonly maximumVisitOutcomes?: number;
}

export interface ClosePhase3EconomyInput {
  readonly closeId: string;
  readonly closedTick: SimulatedSecond;
  readonly ticketFinance?: TicketFinanceSnapshot;
}

export type EconomyValidationCode =
  | 'invalid-input'
  | 'invalid-snapshot'
  | 'unsupported-version'
  | 'capacity-exceeded'
  | 'conflict'
  | 'checksum-mismatch';

export class Phase3EconomyValidationError extends RangeError {
  readonly code: EconomyValidationCode;

  constructor(code: EconomyValidationCode, message: string) {
    super(message);
    this.name = 'Phase3EconomyValidationError';
    this.code = code;
  }
}

function freezeObject<T extends object>(value: T): Readonly<T> { return Object.freeze(value); }
function freezeArray<T>(value: readonly T[]): readonly T[] { return Object.freeze([...value]); }
function clamp(value: number, minimum: number, maximum: number): number { return Math.min(maximum, Math.max(minimum, value)); }

function finite(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw new Phase3EconomyValidationError('invalid-input', `${label} must be finite`);
}

function unit(value: unknown, label: string): asserts value is number {
  finite(value, label);
  if (value < 0 || value > 1) throw new Phase3EconomyValidationError('invalid-input', `${label} must be in [0, 1]`);
}

function integer(value: unknown, label: string): asserts value is number {
  finite(value, label);
  if (!Number.isSafeInteger(value)) throw new Phase3EconomyValidationError('invalid-input', `${label} must be a safe integer`);
}

function nonNegativeInteger(value: unknown, label: string): asserts value is number {
  integer(value, label);
  if (value < 0) throw new Phase3EconomyValidationError('invalid-input', `${label} must be non-negative`);
}

function positiveInteger(value: unknown, label: string): asserts value is number {
  nonNegativeInteger(value, label);
  if (value <= 0) throw new Phase3EconomyValidationError('invalid-input', `${label} must be positive`);
}

function text(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.trim().length === 0) throw new Phase3EconomyValidationError('invalid-input', `${label} must be a non-empty string`);
}

function enumValue<T extends string>(value: unknown, values: readonly T[], label: string): asserts value is T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw new Phase3EconomyValidationError('invalid-input', `${label} is invalid`);
}

function safeBps(value: number, label: string): void {
  integer(value, label);
  if (value < PHASE_3_ECONOMY_FORMULAS.minimumScoreBps || value > PHASE_3_ECONOMY_FORMULAS.maximumScoreBps) {
    throw new Phase3EconomyValidationError('invalid-input', `${label} must be within score bounds`);
  }
}

function roundBps(value: number): number {
  const rounded = Math.round(value);
  if (!Number.isSafeInteger(rounded)) throw new Phase3EconomyValidationError('invalid-input', 'delta exceeds safe integer range');
  return rounded;
}

function worldSeedText(seed: RandomSeed): string {
  if (typeof seed === 'string') { text(seed, 'worldSeed'); return seed; }
  if (typeof seed === 'number') { integer(seed, 'worldSeed'); return String(seed); }
  return `${seed}n`;
}

function profileProjection(profile: Omit<ReputationProfile, 'checksum'>): unknown {
  return { version: profile.version, formulaVersion: profile.formulaVersion, hype: profile.hype, legacy: profile.legacy };
}

export function reputationProfileChecksum(profile: Omit<ReputationProfile, 'checksum'> | ReputationProfile): string {
  const { checksum: _checksum, ...base } = profile as ReputationProfile;
  return eventCalendarChecksum(profileProjection(base));
}

function scoreSource(source: ReputationMatrixInput | undefined, dimension: ReputationDimension, segment: ReputationSegment | 'all', baseline: number): number {
  const value = source?.[dimension]?.[segment];
  if (value === undefined) return baseline;
  finite(value, `${dimension}/${segment} score`);
  safeBps(value, `${dimension}/${segment} score`);
  return value;
}

function createMatrix(source: ReputationMatrixInput | undefined, baseline: number): ReputationMatrix {
  const matrix = {} as Record<ReputationDimension, Readonly<Record<ReputationSegment | 'all', number>>>;
  for (const dimension of REPUTATION_DIMENSIONS) {
    const row = {} as Record<ReputationSegment | 'all', number>;
    for (const segment of ['all', ...REPUTATION_SEGMENTS] as const) row[segment] = scoreSource(source, dimension, segment, baseline);
    matrix[dimension] = freezeObject(row);
  }
  return freezeObject(matrix);
}

function makeProfile(hype: ReputationMatrix, legacy: ReputationMatrix): ReputationProfile {
  const base = { version: PHASE_3_ECONOMY_DOMAIN_VERSION, formulaVersion: PHASE_3_ECONOMY_FORMULA_VERSION,
    hype, legacy } satisfies Omit<ReputationProfile, 'checksum'>;
  return freezeObject({ ...base, checksum: reputationProfileChecksum(base) });
}

/** Create a profile with a neutral baseline unless a prior profile is supplied. */
export function createReputationProfile(input: ReputationProfileInput = {}): ReputationProfile {
  const baseline = input.baselineScoreBps ?? PHASE_3_ECONOMY_FORMULAS.baselineScoreBps;
  safeBps(baseline, 'baselineScoreBps');
  return makeProfile(createMatrix(input.hype, baseline), createMatrix(input.legacy, baseline));
}

function assertProfile(profile: ReputationProfile): void {
  if (profile.version !== PHASE_3_ECONOMY_DOMAIN_VERSION || profile.formulaVersion !== PHASE_3_ECONOMY_FORMULA_VERSION) {
    throw new Phase3EconomyValidationError('unsupported-version', 'unsupported reputation profile version');
  }
  for (const layer of [profile.hype, profile.legacy]) {
    for (const dimension of REPUTATION_DIMENSIONS) {
      for (const segment of ['all', ...REPUTATION_SEGMENTS] as const) safeBps(layer[dimension][segment], `${dimension}/${segment} profile score`);
    }
  }
  if (profile.checksum !== reputationProfileChecksum(profile)) throw new Phase3EconomyValidationError('checksum-mismatch', 'reputation profile checksum mismatch');
}

export function isReputationProfile(value: unknown): value is ReputationProfile {
  try { assertProfile(value as ReputationProfile); return true; } catch { return false; }
}

/** Create deterministic synthetic comparison values for the UI only. */
export function createSyntheticPeerBaseline(worldSeed: RandomSeed = 'phase3-synthetic-peers'): SyntheticPeerBaseline {
  const seed = worldSeedText(worldSeed);
  const dimensions = {} as Record<ReputationDimension, number>;
  for (const [index, dimension] of REPUTATION_DIMENSIONS.entries()) {
    dimensions[dimension] = clamp(PHASE_3_ECONOMY_FORMULAS.syntheticPeerBaseBps
      + Math.round((keyedRandomFloat(seed, dimension, 'synthetic-peer-dimension', index) - 0.5) * PHASE_3_ECONOMY_FORMULAS.syntheticPeerSpreadBps), 0, 10_000);
  }
  const segments = {} as Record<ReputationSegment, number>;
  for (const [index, segment] of REPUTATION_SEGMENTS.entries()) {
    segments[segment] = clamp(PHASE_3_ECONOMY_FORMULAS.syntheticPeerBaseBps
      + Math.round((keyedRandomFloat(seed, segment, 'synthetic-peer-segment', index) - 0.5) * PHASE_3_ECONOMY_FORMULAS.syntheticPeerSpreadBps), 0, 10_000);
  }
  const base = { version: PHASE_3_ECONOMY_DOMAIN_VERSION, source: 'synthetic' as const, displayOnly: true as const,
    worldSeed: seed, dimensions: freezeObject(dimensions), segments: freezeObject(segments) };
  return freezeObject({ ...base, checksum: eventCalendarChecksum(base) });
}

function assertPeerBaseline(peer: SyntheticPeerBaseline): void {
  if (peer.version !== PHASE_3_ECONOMY_DOMAIN_VERSION || peer.source !== 'synthetic' || peer.displayOnly !== true) {
    throw new Phase3EconomyValidationError('invalid-snapshot', 'synthetic peer baseline marker is invalid');
  }
  text(peer.worldSeed, 'peer worldSeed');
  for (const dimension of REPUTATION_DIMENSIONS) safeBps(peer.dimensions[dimension], `peer ${dimension}`);
  for (const segment of REPUTATION_SEGMENTS) safeBps(peer.segments[segment], `peer ${segment}`);
  const { checksum: _checksum, ...base } = peer;
  if (peer.checksum !== eventCalendarChecksum(base)) throw new Phase3EconomyValidationError('checksum-mismatch', 'synthetic peer checksum mismatch');
}

/** Demand-facing score. It intentionally reads only the opening profile. */
export function blendedReputationScore(profile: ReputationProfile, segment: ReputationSegment | 'all' = 'all', dimension: ReputationDimension = 'overall'): number {
  assertProfile(profile);
  enumValue(segment, ['all', ...REPUTATION_SEGMENTS] as const, 'reputation segment');
  enumValue(dimension, REPUTATION_DIMENSIONS, 'reputation dimension');
  return (profile.hype[dimension][segment] * PHASE_3_ECONOMY_FORMULAS.demandHypeWeight
    + profile.legacy[dimension][segment] * PHASE_3_ECONOMY_FORMULAS.demandLegacyWeight) / 10_000;
}

function dimensionForReason(reasonCode: ReputationReasonCode): ReputationDimension | null {
  if (reasonCode === 'terrain' || reasonCode === 'terrain-mismatch') return 'terrain';
  if (reasonCode === 'comfort') return 'comfort';
  if (reasonCode === 'value' || reasonCode === 'value-concern') return 'value';
  if (reasonCode === 'safety' || reasonCode === 'safety-concern' || reasonCode === 'injury'
    || reasonCode === 'safety-incident' || reasonCode === 'safety-response' || reasonCode === 'safety-outcome'
    || reasonCode === 'safety-rate') return 'safety';
  if (reasonCode === 'wait' || reasonCode === 'crowding' || reasonCode === 'conditions'
    || reasonCode === 'queueing' || reasonCode === 'waiting' || reasonCode === 'long-wait'
    || reasonCode === 'poor-conditions' || reasonCode === 'expectation-shortfall') return 'service';
  return null;
}

function validateVisitInput(input: VisitOutcomeInput): void {
  text(input.dayId, 'dayId');
  text(input.guestId, 'guestId');
  enumValue(input.segment, REPUTATION_SEGMENTS, 'guest segment');
  if (input.tick !== undefined) nonNegativeInteger(input.tick, 'visit tick');
  unit(input.satisfaction, 'satisfaction');
  if (input.dimensionScores) {
    for (const [dimension, score] of Object.entries(input.dimensionScores)) {
      enumValue(dimension, REPUTATION_DIMENSIONS, 'dimension score key');
      unit(score, `dimension ${dimension}`);
    }
  }
  if (input.signals && input.signals.length > PHASE_3_ECONOMY_FORMULAS.maximumSignalsPerVisit) {
    throw new Phase3EconomyValidationError('capacity-exceeded', 'visit signal capacity exceeded');
  }
}

function visitOutcomeRecord(input: VisitOutcomeInput): VisitOutcomeRecord {
  validateVisitInput(input);
  const signalRecords: ReputationSignalRecord[] = [];
  const signalIds = new Set<string>();
  for (const signal of input.signals ?? []) {
    if (signal.guestId !== input.guestId) throw new Phase3EconomyValidationError('invalid-input', 'visit signal guest does not match outcome guest');
    if (signalIds.has(signal.eventId)) throw new Phase3EconomyValidationError('invalid-input', 'visit contains duplicate signal event id');
    signalIds.add(signal.eventId);
    signalRecords.push(evaluateReputationSignal(signal));
  }
  signalRecords.sort((left, right) => left.eventId.localeCompare(right.eventId));
  const dimensionScores = {} as Record<ReputationDimension, number>;
  for (const dimension of REPUTATION_DIMENSIONS) {
    const supplied = input.dimensionScores?.[dimension];
    dimensionScores[dimension] = supplied ?? input.satisfaction;
  }
  const deltas = {} as Record<ReputationDimension, number>;
  for (const dimension of REPUTATION_DIMENSIONS) {
    deltas[dimension] = roundBps((dimensionScores[dimension]! - 0.5) * 2 * PHASE_3_ECONOMY_FORMULAS.visitDimensionMagnitudeBps);
  }
  let deltaBps = deltas.overall!;
  for (const signal of signalRecords) {
    deltaBps += signal.deltaBps;
    let mapped = false;
    for (const reasonCode of REPUTATION_REASON_CODES) {
      const contribution = signal.reasonVectorBps[reasonCode] ?? 0;
      if (contribution === 0) continue;
      const dimension = dimensionForReason(reasonCode);
      if (dimension) { deltas[dimension]! += contribution; mapped = true; }
    }
    if (!mapped) deltas.overall! += signal.deltaBps;
  }
  const base = { version: PHASE_3_ECONOMY_DOMAIN_VERSION, formulaVersion: PHASE_3_ECONOMY_FORMULA_VERSION,
    id: `visit:${input.dayId}:${input.guestId}`, dayId: input.dayId, guestId: input.guestId, segment: input.segment,
    tick: input.tick ?? null, satisfaction: input.satisfaction, dimensionScores: freezeObject(dimensionScores),
    signals: freezeArray(signalRecords), deltaBps, deltaByDimensionBps: freezeObject(deltas) } satisfies Omit<VisitOutcomeRecord, 'checksum'>;
  return freezeObject({ ...base, checksum: eventCalendarChecksum(base) });
}

function ticketMetrics(ticketFinance: TicketFinanceSnapshot | null): Pick<EconomyMetrics, 'ticketCount' | 'ticketRevenueCents' | 'ticketRevenueBySegment'> {
  const revenue = {} as Record<ReputationSegment, number>;
  for (const segment of REPUTATION_SEGMENTS) revenue[segment] = 0;
  if (!ticketFinance) return { ticketCount: 0, ticketRevenueCents: 0, ticketRevenueBySegment: freezeObject(revenue) };
  for (const transaction of ticketFinance.transactions) {
    if ((REPUTATION_SEGMENTS as readonly string[]).includes(transaction.segment)) revenue[transaction.segment] += transaction.amountCents;
  }
  return { ticketCount: ticketFinance.recognizedCount, ticketRevenueCents: ticketFinance.ticketRevenueCents, ticketRevenueBySegment: freezeObject(revenue) };
}

function createMetrics(outcomes: readonly VisitOutcomeRecord[], ticketFinance: TicketFinanceSnapshot | null): EconomyMetrics {
  let positiveVisitCount = 0;
  let neutralVisitCount = 0;
  let negativeVisitCount = 0;
  let signalCount = 0;
  let netVisitDeltaBps = 0;
  let satisfactionTotal = 0;
  for (const outcome of [...outcomes].sort((left, right) => left.guestId.localeCompare(right.guestId))) {
    if (outcome.deltaBps > 0) positiveVisitCount += 1;
    else if (outcome.deltaBps < 0) negativeVisitCount += 1;
    else neutralVisitCount += 1;
    signalCount += outcome.signals.length;
    netVisitDeltaBps += outcome.deltaBps;
    if (!Number.isSafeInteger(netVisitDeltaBps)) throw new Phase3EconomyValidationError('invalid-snapshot', 'visit delta exceeds safe integer range');
    satisfactionTotal += outcome.satisfaction;
  }
  const tickets = ticketMetrics(ticketFinance);
  return freezeObject({ visitCount: outcomes.length, positiveVisitCount, neutralVisitCount, negativeVisitCount,
    signalCount, netVisitDeltaBps, averageSatisfaction: outcomes.length > 0 ? satisfactionTotal / outcomes.length : null,
    ...tickets });
}

function snapshotProjection(snapshot: Omit<Phase3EconomySnapshot, 'checksum'>): unknown {
  return { version: snapshot.version, formulaVersion: snapshot.formulaVersion, dayId: snapshot.dayId,
    openingReputation: snapshot.openingReputation, syntheticPeerBaseline: snapshot.syntheticPeerBaseline,
    ticketFinance: snapshot.ticketFinance, maximumVisitOutcomes: snapshot.maximumVisitOutcomes,
    visitOutcomes: snapshot.visitOutcomes, metrics: snapshot.metrics, closed: snapshot.closed, closing: snapshot.closing };
}

export function phase3EconomySnapshotChecksum(snapshot: Omit<Phase3EconomySnapshot, 'checksum'> | Phase3EconomySnapshot): string {
  const { checksum: _checksum, ...base } = snapshot as Phase3EconomySnapshot;
  return eventCalendarChecksum(snapshotProjection(base));
}

function makeSnapshot(input: Omit<Phase3EconomySnapshot, 'checksum'>): Phase3EconomySnapshot {
  const base = { ...input, visitOutcomes: freezeArray([...input.visitOutcomes].sort((left, right) => left.guestId.localeCompare(right.guestId))) };
  return freezeObject({ ...base, checksum: phase3EconomySnapshotChecksum(base) });
}

function profileFromInput(input: ReputationProfile | ReputationProfileInput | undefined): ReputationProfile {
  if (!input) return createReputationProfile();
  if ('checksum' in input) { assertProfile(input); return input; }
  return createReputationProfile(input);
}

/** Create the immutable opening state for one operating day. */
export function createPhase3Economy(input: CreatePhase3EconomyInput): Phase3EconomySnapshot {
  text(input.dayId, 'dayId');
  const maximumVisitOutcomes = input.maximumVisitOutcomes ?? PHASE_3_ECONOMY_FORMULAS.defaultMaximumVisitOutcomes;
  positiveInteger(maximumVisitOutcomes, 'maximumVisitOutcomes');
  const openingReputation = profileFromInput(input.openingReputation);
  const syntheticPeerBaseline = input.syntheticPeerBaseline ?? createSyntheticPeerBaseline(`peers:${input.dayId}`);
  assertPeerBaseline(syntheticPeerBaseline);
  if (input.ticketFinance && input.ticketFinance.dayId !== input.dayId) {
    throw new Phase3EconomyValidationError('invalid-input', 'ticket finance day does not match economy day');
  }
  const ticketFinance = input.ticketFinance ?? null;
  const outcomes: readonly VisitOutcomeRecord[] = [];
  const base = { version: PHASE_3_ECONOMY_DOMAIN_VERSION, formulaVersion: PHASE_3_ECONOMY_FORMULA_VERSION,
    dayId: input.dayId, openingReputation, syntheticPeerBaseline, ticketFinance, maximumVisitOutcomes,
    visitOutcomes: outcomes, metrics: createMetrics(outcomes, ticketFinance), closed: false, closing: null } satisfies Omit<Phase3EconomySnapshot, 'checksum'>;
  return makeSnapshot(base);
}

function assertOutcome(outcome: VisitOutcomeRecord, dayId: string): void {
  if (outcome.version !== PHASE_3_ECONOMY_DOMAIN_VERSION || outcome.formulaVersion !== PHASE_3_ECONOMY_FORMULA_VERSION) throw new Phase3EconomyValidationError('unsupported-version', 'unsupported visit outcome version');
  text(outcome.id, 'outcome id');
  if (outcome.dayId !== dayId || outcome.id !== `visit:${outcome.dayId}:${outcome.guestId}`) throw new Phase3EconomyValidationError('invalid-snapshot', 'visit outcome identity does not reconcile');
  text(outcome.guestId, 'outcome guestId');
  enumValue(outcome.segment, REPUTATION_SEGMENTS, 'outcome segment');
  if (outcome.tick !== null) nonNegativeInteger(outcome.tick, 'outcome tick');
  unit(outcome.satisfaction, 'outcome satisfaction');
  for (const dimension of REPUTATION_DIMENSIONS) { unit(outcome.dimensionScores[dimension], `outcome ${dimension} score`); integer(outcome.deltaByDimensionBps[dimension], `outcome ${dimension} delta`); }
  integer(outcome.deltaBps, 'outcome delta');
  for (const signal of outcome.signals) {
    if (signal.guestId !== outcome.guestId) throw new Phase3EconomyValidationError('invalid-snapshot', 'outcome signal guest mismatch');
  }
  if (outcome.checksum !== eventCalendarChecksum((({ checksum: _checksum, ...base }) => base)(outcome))) throw new Phase3EconomyValidationError('checksum-mismatch', 'visit outcome checksum mismatch');
}

function assertMetrics(metrics: EconomyMetrics, outcomes: readonly VisitOutcomeRecord[], ticketFinance: TicketFinanceSnapshot | null): void {
  const expected = createMetrics(outcomes, ticketFinance);
  if (JSON.stringify(metrics) !== JSON.stringify(expected)) throw new Phase3EconomyValidationError('invalid-snapshot', 'economy metrics do not reconcile');
}

/** Strong snapshot validation, including nested profiles and outcome checksums. */
export function assertPhase3EconomySnapshot(value: unknown): asserts value is Phase3EconomySnapshot {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new Phase3EconomyValidationError('invalid-snapshot', 'economy snapshot must be an object');
  const snapshot = value as Phase3EconomySnapshot;
  if (snapshot.version !== PHASE_3_ECONOMY_DOMAIN_VERSION || snapshot.formulaVersion !== PHASE_3_ECONOMY_FORMULA_VERSION) throw new Phase3EconomyValidationError('unsupported-version', 'unsupported economy snapshot version');
  text(snapshot.dayId, 'snapshot dayId');
  assertProfile(snapshot.openingReputation);
  assertPeerBaseline(snapshot.syntheticPeerBaseline);
  positiveInteger(snapshot.maximumVisitOutcomes, 'maximumVisitOutcomes');
  if (!Array.isArray(snapshot.visitOutcomes) || snapshot.visitOutcomes.length > snapshot.maximumVisitOutcomes) throw new Phase3EconomyValidationError('capacity-exceeded', 'snapshot visit capacity exceeded');
  const guestIds = new Set<string>();
  const signalIds = new Set<string>();
  for (const outcome of snapshot.visitOutcomes) {
    assertOutcome(outcome, snapshot.dayId);
    if (guestIds.has(outcome.guestId)) throw new Phase3EconomyValidationError('invalid-snapshot', 'more than one outcome exists for guest/day');
    guestIds.add(outcome.guestId);
    for (const signal of outcome.signals) {
      if (signalIds.has(signal.eventId)) throw new Phase3EconomyValidationError('invalid-snapshot', 'signal event was applied to multiple outcomes');
      signalIds.add(signal.eventId);
    }
  }
  if (snapshot.closed !== (snapshot.closing !== null)) throw new Phase3EconomyValidationError('invalid-snapshot', 'closed marker does not reconcile');
  if (snapshot.ticketFinance && snapshot.ticketFinance.dayId !== snapshot.dayId) throw new Phase3EconomyValidationError('invalid-snapshot', 'ticket finance day mismatch');
  assertMetrics(snapshot.metrics, snapshot.visitOutcomes, snapshot.ticketFinance);
  if (snapshot.closing) {
    text(snapshot.closing.closeId, 'closeId');
    nonNegativeInteger(snapshot.closing.closedTick, 'closedTick');
    assertProfile(snapshot.closing.nextDayReputation);
    const { checksum: _checksum, ...base } = snapshot.closing;
    if (snapshot.closing.checksum !== eventCalendarChecksum(base)) throw new Phase3EconomyValidationError('checksum-mismatch', 'close checksum mismatch');
  }
  if (snapshot.checksum !== phase3EconomySnapshotChecksum(snapshot)) throw new Phase3EconomyValidationError('checksum-mismatch', 'economy snapshot checksum mismatch');
}

export function isPhase3EconomySnapshot(value: unknown): value is Phase3EconomySnapshot {
  try { assertPhase3EconomySnapshot(value); return true; } catch { return false; }
}

/** Add exactly one guest/day outcome without changing opening reputation. */
export function recordVisitOutcome(snapshot: Phase3EconomySnapshot, input: VisitOutcomeInput): Phase3EconomySnapshot {
  return recordVisitOutcomes(snapshot, [input]);
}

/** Batch form used at day close so thousands of outcomes remain O(n log n), not O(n^2). */
export function recordVisitOutcomes(snapshot: Phase3EconomySnapshot,
  inputs: readonly VisitOutcomeInput[]): Phase3EconomySnapshot {
  assertPhase3EconomySnapshot(snapshot);
  if (snapshot.closed) throw new Phase3EconomyValidationError('conflict', 'cannot record an outcome after day close');
  const byId = new Map(snapshot.visitOutcomes.map((outcome) => [outcome.id, outcome]));
  const signalIds = new Set(snapshot.visitOutcomes.flatMap((outcome) => outcome.signals.map((signal) => signal.eventId)));
  let changed = false;
  for (const input of inputs) {
    if (input.dayId !== snapshot.dayId) throw new Phase3EconomyValidationError('conflict', 'visit outcome day does not match snapshot');
    const outcome = visitOutcomeRecord(input);
    const existing = byId.get(outcome.id);
    if (existing) {
      if (existing.checksum !== outcome.checksum) throw new Phase3EconomyValidationError('conflict', `guest/day outcome ${outcome.id} was reused with different data`);
      continue;
    }
    for (const signal of outcome.signals) {
      if (signalIds.has(signal.eventId)) throw new Phase3EconomyValidationError('conflict', `signal ${signal.eventId} was already used by another outcome`);
      signalIds.add(signal.eventId);
    }
    byId.set(outcome.id, outcome); changed = true;
  }
  if (byId.size > snapshot.maximumVisitOutcomes) throw new Phase3EconomyValidationError('capacity-exceeded', 'visit outcome capacity exceeded');
  if (!changed) return snapshot;
  const outcomes = [...byId.values()];
  return makeSnapshot({ ...snapshot, visitOutcomes: outcomes, metrics: createMetrics(outcomes, snapshot.ticketFinance) });
}

function nextProfile(opening: ReputationProfile, outcomes: readonly VisitOutcomeRecord[]): ReputationProfile {
  const layers = { hype: {} as ReputationMatrixInput, legacy: {} as ReputationMatrixInput };
  for (const dimension of REPUTATION_DIMENSIONS) {
    layers.hype[dimension] = {};
    layers.legacy[dimension] = {};
    for (const segment of ['all', ...REPUTATION_SEGMENTS] as const) {
      const scoped = segment === 'all' ? outcomes : outcomes.filter((outcome) => outcome.segment === segment);
      if (scoped.length === 0) {
        layers.hype[dimension]![segment] = opening.hype[dimension][segment];
        layers.legacy[dimension]![segment] = opening.legacy[dimension][segment];
        continue;
      }
      let total = 0;
      for (const outcome of scoped) total += outcome.deltaByDimensionBps[dimension]!;
      const averageDelta = total / scoped.length;
      layers.hype[dimension]![segment] = clamp(opening.hype[dimension][segment]
        + roundBps(PHASE_3_ECONOMY_FORMULAS.hypeLearningRate * averageDelta), 0, 10_000);
      layers.legacy[dimension]![segment] = clamp(opening.legacy[dimension][segment]
        + roundBps(PHASE_3_ECONOMY_FORMULAS.legacyLearningRate * averageDelta), 0, 10_000);
    }
  }
  return createReputationProfile({ hype: layers.hype, legacy: layers.legacy });
}

/** Close exactly once and derive a next-day profile without same-day feedback. */
export function closePhase3Economy(snapshot: Phase3EconomySnapshot, input: ClosePhase3EconomyInput): Phase3EconomySnapshot {
  assertPhase3EconomySnapshot(snapshot);
  text(input.closeId, 'closeId');
  nonNegativeInteger(input.closedTick, 'closedTick');
  if (input.ticketFinance && input.ticketFinance.dayId !== snapshot.dayId) throw new Phase3EconomyValidationError('conflict', 'ticket finance day does not match close day');
  const ticketFinance = input.ticketFinance ?? snapshot.ticketFinance;
  if (snapshot.closed) {
    if (!snapshot.closing || snapshot.closing.closeId !== input.closeId || snapshot.closing.closedTick !== input.closedTick
      || snapshot.closing.ticketFinanceChecksum !== (ticketFinance?.checksum ?? null)) {
      throw new Phase3EconomyValidationError('conflict', 'day was already closed with a different close request');
    }
    return snapshot;
  }
  const nextDayReputation = nextProfile(snapshot.openingReputation, snapshot.visitOutcomes);
  const closeBase = { closeId: input.closeId, closedTick: input.closedTick, nextDayReputation,
    ticketFinanceChecksum: ticketFinance?.checksum ?? null };
  const closing = freezeObject({ ...closeBase, checksum: eventCalendarChecksum(closeBase) });
  return makeSnapshot({ ...snapshot, ticketFinance, metrics: createMetrics(snapshot.visitOutcomes, ticketFinance), closed: true, closing });
}

export const validatePhase3EconomySnapshot = assertPhase3EconomySnapshot;
