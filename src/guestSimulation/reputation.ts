/**
 * Phase 3 resort-reputation domain.
 *
 * Reputation is intentionally an additive, event-sourced ledger.  A guest
 * experience or safety outcome produces one immutable signal.  Applying the
 * same event id again is a no-op, so worker retries and replay hydration do
 * not double-count it.  The score is derived from the baseline plus the
 * accumulated delta (rather than clamping after every event), which also
 * makes the result independent of arrival order.
 *
 * This module has no engine, UI, persistence, React, or browser dependencies.
 * It can therefore be evaluated in the simulation worker, a replay, or a
 * balance/calibration tool.
 */

import {
  DEFAULT_SATISFACTION_WEIGHTS,
  EXPERIENCE_THOUGHT_REASON_CODES,
  SATISFACTION_CHANNELS,
  type ExperienceThoughtReasonCode,
  type SatisfactionChannelCode,
  type ThoughtSentiment,
} from './experience.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';

export const PHASE_3_REPUTATION_DOMAIN_VERSION = 1 as const;
export const PHASE_3_REPUTATION_FORMULA_VERSION = 1 as const;

export type Phase3ReputationDomainVersion = typeof PHASE_3_REPUTATION_DOMAIN_VERSION;
export type Phase3ReputationFormulaVersion = typeof PHASE_3_REPUTATION_FORMULA_VERSION;

/**
 * Formula constants are public so balance tools and replay diagnostics can
 * identify the rule that produced a ledger. Values are basis points, where
 * 10,000 is a perfect score and the default starting score is 5,000.
 */
export const PHASE_3_REPUTATION_FORMULAS = Object.freeze({
  version: PHASE_3_REPUTATION_FORMULA_VERSION,
  baselineScoreBps: 5_000,
  minimumScoreBps: 0,
  maximumScoreBps: 10_000,
  /** Upper bound on unique events retained in one simulation ledger. */
  defaultMaximumSignals: 100_000,
  experienceMagnitudeBps: 32,
  safetyIncidentMagnitudeBps: 70,
  safetyMetricsMagnitudeBps: 30,
  safetyReferenceRate: 0.95,
  safetyResponseReferenceSeconds: 900,
  weights: Object.freeze({
    experience: Object.freeze({
      terrain: DEFAULT_SATISFACTION_WEIGHTS.terrain,
      wait: DEFAULT_SATISFACTION_WEIGHTS.wait,
      crowding: DEFAULT_SATISFACTION_WEIGHTS.crowding,
      comfort: DEFAULT_SATISFACTION_WEIGHTS.comfort,
      conditions: DEFAULT_SATISFACTION_WEIGHTS.conditions,
      value: DEFAULT_SATISFACTION_WEIGHTS.value,
      variety: DEFAULT_SATISFACTION_WEIGHTS.variety,
      safety: DEFAULT_SATISFACTION_WEIGHTS.safety,
    }),
    safetySeverity: Object.freeze({ minor: 0.35, moderate: 0.65, major: 1 }),
    safetyOutcome: Object.freeze({ resolved: 1, failed: 1.5, unreachable: 1.5, cancelled: 1.1 }),
  }),
} as const);

export type ReputationSafetyReasonCode = 'safety-incident' | 'safety-response' | 'safety-outcome' | 'safety-rate';
export type ReputationReasonCode = SatisfactionChannelCode | ExperienceThoughtReasonCode | ReputationSafetyReasonCode;

export const REPUTATION_REASON_CODES: readonly ReputationReasonCode[] = Object.freeze([
  ...SATISFACTION_CHANNELS,
  ...EXPERIENCE_THOUGHT_REASON_CODES,
  'safety-incident', 'safety-response', 'safety-outcome', 'safety-rate',
]);

const CHANNEL_REASON_CODES: readonly SatisfactionChannelCode[] = Object.freeze([
  ...SATISFACTION_CHANNELS,
]);

export interface ReputationExperienceSignal {
  readonly version?: Phase3ReputationDomainVersion;
  readonly kind: 'experience';
  /** Stable event identity. It must not be a frame/tick counter alone. */
  readonly eventId: string;
  readonly guestId: string;
  readonly tick: number;
  /** Optional scalar satisfaction when a complete channel result is absent. */
  readonly satisfaction?: number;
  /** Used when the producer only has a reason-coded thought. */
  readonly sentiment?: ThoughtSentiment;
  /** Phase 2 reason code for a scalar/thought signal. */
  readonly reasonCode?: ExperienceThoughtReasonCode;
  /**
   * Optional channel scores. When present, only supplied channels are
   * normalized and the reason vector retains their individual contributions.
   */
  readonly channels?: Partial<Record<SatisfactionChannelCode, number>>;
  /** Optional [0,1] confidence/exposure scale; defaults to 1. */
  readonly weight?: number;
}

export type ReputationSafetySeverity = 'minor' | 'moderate' | 'major';
export type ReputationSafetyOutcome = 'resolved' | 'failed' | 'unreachable' | 'cancelled';

export interface ReputationSafetySignal {
  readonly version?: Phase3ReputationDomainVersion;
  readonly kind: 'safety';
  readonly eventId: string;
  readonly guestId: string;
  readonly tick: number;
  readonly severity: ReputationSafetySeverity;
  readonly outcome: ReputationSafetyOutcome;
  /** Optional response duration. Longer response produces an extra penalty. */
  readonly responseSeconds?: number;
}

/**
 * A metric signal is useful at a resort boundary (for example, once per
 * operating day). Callers must give each observation a stable event id and
 * must not feed cumulative snapshots under a new id every frame.
 */
export interface ReputationSafetyMetricsSignal {
  readonly version?: Phase3ReputationDomainVersion;
  readonly kind: 'safety-metrics';
  readonly eventId: string;
  readonly guestId: string;
  readonly tick: number;
  readonly observedTraversals: number;
  readonly incidentCount: number;
  readonly failedIncidents?: number;
  readonly safetyRate?: number;
}

export type ReputationSignal = ReputationExperienceSignal | ReputationSafetySignal | ReputationSafetyMetricsSignal;

export interface ReputationReasonVector {
  readonly [reasonCode: string]: number;
}

export interface ReputationSignalRecord {
  readonly version: Phase3ReputationDomainVersion;
  readonly formulaVersion: Phase3ReputationFormulaVersion;
  readonly eventId: string;
  readonly guestId: string;
  readonly tick: number;
  readonly kind: ReputationSignal['kind'];
  /** Signed additive delta in basis points. */
  readonly deltaBps: number;
  /** Signed per-reason additive contributions; the sum equals deltaBps. */
  readonly reasonVectorBps: ReputationReasonVector;
  readonly checksum: string;
}

export interface ReputationReasonAggregate {
  readonly reasonCode: ReputationReasonCode;
  readonly eventCount: number;
  readonly deltaBps: number;
  readonly positiveDeltaBps: number;
  readonly negativeDeltaBps: number;
}

export interface ReputationMetrics {
  readonly appliedSignalCount: number;
  readonly positiveSignalCount: number;
  readonly neutralSignalCount: number;
  readonly negativeSignalCount: number;
  readonly netDeltaBps: number;
  readonly firstSignalTick: number | null;
  readonly lastSignalTick: number | null;
  readonly byReason: readonly ReputationReasonAggregate[];
}

export interface ReputationLedger {
  readonly version: Phase3ReputationDomainVersion;
  readonly formulaVersion: Phase3ReputationFormulaVersion;
  readonly baselineScoreBps: number;
  readonly minimumScoreBps: number;
  readonly maximumScoreBps: number;
  readonly maximumSignals: number;
  /** Derived score, clamped only after the full additive sum. */
  readonly scoreBps: number;
  readonly appliedEventIds: readonly string[];
  readonly signals: readonly ReputationSignalRecord[];
  readonly metrics: ReputationMetrics;
  readonly checksum: string;
}

export interface ReputationLedgerOptions {
  readonly baselineScoreBps?: number;
  readonly minimumScoreBps?: number;
  readonly maximumScoreBps?: number;
  readonly maximumSignals?: number;
}

export interface ReputationApplyResult {
  readonly ledger: ReputationLedger;
  readonly applied: boolean;
  readonly duplicate: boolean;
  readonly signal: ReputationSignalRecord | null;
}

export type ReputationValidationCode =
  | 'invalid-input'
  | 'invalid-ledger'
  | 'unsupported-version'
  | 'capacity-exceeded'
  | 'checksum-mismatch';

export class ReputationValidationError extends RangeError {
  readonly code: ReputationValidationCode;

  constructor(code: ReputationValidationCode, message: string) {
    super(message);
    this.name = 'ReputationValidationError';
    this.code = code;
  }
}

function freezeObject<T extends object>(value: T): Readonly<T> { return Object.freeze(value); }
function freezeArray<T>(values: readonly T[]): readonly T[] { return Object.freeze([...values]); }

function finite(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new ReputationValidationError('invalid-input', `${label} must be finite`);
  }
}

function integer(value: unknown, label: string): asserts value is number {
  finite(value, label);
  if (!Number.isSafeInteger(value)) throw new ReputationValidationError('invalid-input', `${label} must be a safe integer`);
}

function nonNegativeInteger(value: unknown, label: string): asserts value is number {
  integer(value, label);
  if (value < 0) throw new ReputationValidationError('invalid-input', `${label} must be non-negative`);
}

function unit(value: unknown, label: string): asserts value is number {
  finite(value, label);
  if (value < 0 || value > 1) throw new ReputationValidationError('invalid-input', `${label} must be in [0, 1]`);
}

function text(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new ReputationValidationError('invalid-input', `${label} must be a non-empty string`);
  }
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
}

function roundBps(value: number): number {
  if (!Number.isFinite(value)) throw new ReputationValidationError('invalid-input', 'reputation delta must be finite');
  const rounded = Math.round(value);
  if (!Number.isSafeInteger(rounded)) throw new ReputationValidationError('invalid-input', 'reputation delta exceeds safe integer range');
  return rounded;
}

function reasonVectorZero(): Record<ReputationReasonCode, number> {
  return Object.fromEntries(REPUTATION_REASON_CODES.map((reasonCode) => [reasonCode, 0])) as Record<ReputationReasonCode, number>;
}

function validateReasonCode(value: unknown): asserts value is ReputationReasonCode {
  if (typeof value !== 'string' || !(REPUTATION_REASON_CODES as readonly string[]).includes(value)) {
    throw new ReputationValidationError('invalid-input', `unknown reputation reason code ${String(value)}`);
  }
}

function validateSignalBase(signal: ReputationSignal): void {
  text(signal.eventId, 'eventId');
  text(signal.guestId, 'guestId');
  nonNegativeInteger(signal.tick, 'tick');
  if (signal.version !== undefined && signal.version !== PHASE_3_REPUTATION_DOMAIN_VERSION) {
    throw new ReputationValidationError('unsupported-version', 'unsupported reputation signal version');
  }
}

function normalizedChannelWeights(channels: Partial<Record<SatisfactionChannelCode, number>>): Readonly<Record<SatisfactionChannelCode, number>> {
  const supplied = CHANNEL_REASON_CODES.filter((channel) => channels[channel] !== undefined);
  if (supplied.length === 0) throw new ReputationValidationError('invalid-input', 'channels must contain at least one score');
  let total = 0;
  const candidate = {} as Record<SatisfactionChannelCode, number>;
  for (const channel of supplied) {
    unit(channels[channel], `channel ${channel}`);
    const weight = PHASE_3_REPUTATION_FORMULAS.weights.experience[channel];
    candidate[channel] = weight;
    total += weight;
  }
  const result = {} as Record<SatisfactionChannelCode, number>;
  for (const channel of supplied) result[channel] = candidate[channel]! / total;
  return result;
}

function experienceSignalRecord(signal: ReputationExperienceSignal): ReputationSignalRecord {
  validateSignalBase(signal);
  if (signal.kind !== 'experience') throw new ReputationValidationError('invalid-input', 'signal kind is not experience');
  if (signal.satisfaction === undefined && signal.sentiment === undefined && signal.channels === undefined) {
    throw new ReputationValidationError('invalid-input', 'experience signal needs satisfaction, sentiment, or channels');
  }
  if (signal.satisfaction !== undefined) unit(signal.satisfaction, 'satisfaction');
  if (signal.sentiment !== undefined && signal.sentiment !== 'positive'
    && signal.sentiment !== 'neutral' && signal.sentiment !== 'negative') {
    throw new ReputationValidationError('invalid-input', 'invalid experience sentiment');
  }
  if (signal.reasonCode !== undefined) validateReasonCode(signal.reasonCode);
  if (signal.weight !== undefined) unit(signal.weight, 'experience weight');
  const scale = signal.weight ?? 1;
  const vector = reasonVectorZero();
  let deltaBps = 0;
  if (signal.channels !== undefined) {
    const weights = normalizedChannelWeights(signal.channels);
    const channelDeltas: Array<{ code: SatisfactionChannelCode; value: number }> = [];
    for (const channel of CHANNEL_REASON_CODES) {
      const score = signal.channels[channel];
      if (score === undefined) continue;
      const quality = (score - 0.5) * 2;
      channelDeltas.push({ code: channel, value: roundBps(PHASE_3_REPUTATION_FORMULAS.experienceMagnitudeBps * quality * weights[channel]! * scale) });
    }
    for (const contribution of channelDeltas) {
      vector[contribution.code] += contribution.value;
      deltaBps += contribution.value;
    }
  } else {
    const quality = signal.satisfaction !== undefined
      ? (signal.satisfaction - 0.5) * 2
      : signal.sentiment === 'positive' ? 1 : signal.sentiment === 'negative' ? -1 : 0;
    const reasonCode = signal.reasonCode ?? (quality < -0.33 ? 'low-satisfaction' : 'positive-experience');
    vector[reasonCode] = roundBps(PHASE_3_REPUTATION_FORMULAS.experienceMagnitudeBps * quality * scale);
    deltaBps = vector[reasonCode];
  }
  return makeSignalRecord(signal, deltaBps, vector);
}

function safetySignalRecord(signal: ReputationSafetySignal): ReputationSignalRecord {
  validateSignalBase(signal);
  if (signal.kind !== 'safety') throw new ReputationValidationError('invalid-input', 'signal kind is not safety');
  if (!(signal.severity in PHASE_3_REPUTATION_FORMULAS.weights.safetySeverity)) {
    throw new ReputationValidationError('invalid-input', 'invalid safety severity');
  }
  if (!(signal.outcome in PHASE_3_REPUTATION_FORMULAS.weights.safetyOutcome)) {
    throw new ReputationValidationError('invalid-input', 'invalid safety outcome');
  }
  if (signal.responseSeconds !== undefined) nonNegativeInteger(signal.responseSeconds, 'responseSeconds');
  const severityWeight = PHASE_3_REPUTATION_FORMULAS.weights.safetySeverity[signal.severity];
  const outcomeWeight = PHASE_3_REPUTATION_FORMULAS.weights.safetyOutcome[signal.outcome];
  const responseRatio = signal.responseSeconds === undefined ? 0
    : clamp(signal.responseSeconds / PHASE_3_REPUTATION_FORMULAS.safetyResponseReferenceSeconds, 0, 1);
  const incident = -roundBps(PHASE_3_REPUTATION_FORMULAS.safetyIncidentMagnitudeBps * severityWeight * outcomeWeight);
  const response = -roundBps(PHASE_3_REPUTATION_FORMULAS.safetyIncidentMagnitudeBps * 0.25 * responseRatio);
  const outcome = -roundBps(PHASE_3_REPUTATION_FORMULAS.safetyIncidentMagnitudeBps * 0.15 * Math.max(0, outcomeWeight - 1));
  const vector = reasonVectorZero();
  vector['safety-incident'] = incident;
  vector['safety-response'] = response;
  vector['safety-outcome'] = outcome;
  return makeSignalRecord(signal, incident + response + outcome, vector);
}

function safetyMetricsSignalRecord(signal: ReputationSafetyMetricsSignal): ReputationSignalRecord {
  validateSignalBase(signal);
  if (signal.kind !== 'safety-metrics') throw new ReputationValidationError('invalid-input', 'signal kind is not safety-metrics');
  nonNegativeInteger(signal.observedTraversals, 'observedTraversals');
  nonNegativeInteger(signal.incidentCount, 'incidentCount');
  nonNegativeInteger(signal.failedIncidents ?? 0, 'failedIncidents');
  if (signal.incidentCount > signal.observedTraversals) throw new ReputationValidationError('invalid-input', 'incidentCount exceeds observedTraversals');
  if ((signal.failedIncidents ?? 0) > signal.incidentCount) throw new ReputationValidationError('invalid-input', 'failedIncidents exceeds incidentCount');
  if (signal.safetyRate !== undefined) unit(signal.safetyRate, 'safetyRate');
  if (signal.observedTraversals === 0) return makeSignalRecord(signal, 0, reasonVectorZero());
  const calculatedRate = 1 - signal.incidentCount / signal.observedTraversals;
  const safetyRate = signal.safetyRate ?? calculatedRate;
  const rateQuality = clamp((safetyRate - PHASE_3_REPUTATION_FORMULAS.safetyReferenceRate) / (1 - PHASE_3_REPUTATION_FORMULAS.safetyReferenceRate), -1, 1);
  const failurePenalty = 1 + 0.25 * ((signal.failedIncidents ?? 0) / Math.max(1, signal.incidentCount));
  const delta = roundBps(PHASE_3_REPUTATION_FORMULAS.safetyMetricsMagnitudeBps * rateQuality * failurePenalty);
  const vector = reasonVectorZero();
  vector['safety-rate'] = delta;
  return makeSignalRecord(signal, delta, vector);
}

function makeSignalRecord(signal: ReputationSignal, deltaBps: number, reasonVectorBps: ReputationReasonVector): ReputationSignalRecord {
  const vector = reasonVectorZero();
  for (const reasonCode of REPUTATION_REASON_CODES) vector[reasonCode] = reasonVectorBps[reasonCode] ?? 0;
  const sum = REPUTATION_REASON_CODES.reduce((total, reasonCode) => total + vector[reasonCode]!, 0);
  if (sum !== deltaBps) throw new ReputationValidationError('invalid-input', 'reason vector does not reconcile to delta');
  const base = { version: PHASE_3_REPUTATION_DOMAIN_VERSION, formulaVersion: PHASE_3_REPUTATION_FORMULA_VERSION,
    eventId: signal.eventId, guestId: signal.guestId, tick: signal.tick, kind: signal.kind,
    deltaBps, reasonVectorBps: freezeObject(vector) } satisfies Omit<ReputationSignalRecord, 'checksum'>;
  return freezeObject({ ...base, checksum: eventCalendarChecksum(base) });
}

/** Evaluate one signal without mutating a ledger. */
export function evaluateReputationSignal(signal: ReputationSignal): ReputationSignalRecord {
  if (signal.kind === 'experience') return experienceSignalRecord(signal);
  if (signal.kind === 'safety') return safetySignalRecord(signal);
  return safetyMetricsSignalRecord(signal);
}

export function reputationSignalChecksum(record: ReputationSignalRecord): string {
  const { checksum: _checksum, ...base } = record;
  return eventCalendarChecksum(base);
}

function ledgerProjection(ledger: Omit<ReputationLedger, 'checksum'>): unknown {
  return {
    version: ledger.version, formulaVersion: ledger.formulaVersion, baselineScoreBps: ledger.baselineScoreBps,
    minimumScoreBps: ledger.minimumScoreBps, maximumScoreBps: ledger.maximumScoreBps, maximumSignals: ledger.maximumSignals,
    scoreBps: ledger.scoreBps, appliedEventIds: ledger.appliedEventIds, signals: ledger.signals, metrics: ledger.metrics,
  };
}

export function reputationLedgerChecksum(ledger: Pick<ReputationLedger, 'version' | 'formulaVersion' | 'baselineScoreBps' | 'minimumScoreBps' | 'maximumScoreBps' | 'maximumSignals' | 'scoreBps' | 'appliedEventIds' | 'signals' | 'metrics'>): string {
  return eventCalendarChecksum(ledgerProjection(ledger as Omit<ReputationLedger, 'checksum'>));
}

function createMetrics(signals: readonly ReputationSignalRecord[]): ReputationMetrics {
  const byReason = new Map<ReputationReasonCode, { eventCount: number; deltaBps: number }>();
  let netDeltaBps = 0;
  let positiveSignalCount = 0;
  let neutralSignalCount = 0;
  let negativeSignalCount = 0;
  let firstSignalTick: number | null = null;
  let lastSignalTick: number | null = null;
  for (const signal of signals) {
    netDeltaBps += signal.deltaBps;
    if (!Number.isSafeInteger(netDeltaBps)) throw new ReputationValidationError('invalid-ledger', 'reputation delta exceeds safe integer range');
    firstSignalTick = firstSignalTick === null ? signal.tick : Math.min(firstSignalTick, signal.tick);
    lastSignalTick = lastSignalTick === null ? signal.tick : Math.max(lastSignalTick, signal.tick);
    if (signal.deltaBps > 0) positiveSignalCount += 1;
    else if (signal.deltaBps < 0) negativeSignalCount += 1;
    else neutralSignalCount += 1;
    for (const reasonCode of REPUTATION_REASON_CODES) {
      const contribution = signal.reasonVectorBps[reasonCode] ?? 0;
      if (contribution === 0) continue;
      const aggregate = byReason.get(reasonCode) ?? { eventCount: 0, deltaBps: 0 };
      aggregate.eventCount += 1;
      aggregate.deltaBps += contribution;
      byReason.set(reasonCode, aggregate);
    }
  }
  const reasonAggregates = REPUTATION_REASON_CODES
    .filter((reasonCode) => byReason.has(reasonCode))
    .map((reasonCode) => {
      const aggregate = byReason.get(reasonCode)!;
      return freezeObject({ reasonCode, eventCount: aggregate.eventCount, deltaBps: aggregate.deltaBps,
        positiveDeltaBps: Math.max(0, aggregate.deltaBps), negativeDeltaBps: Math.min(0, aggregate.deltaBps) });
    });
  return freezeObject({ appliedSignalCount: signals.length, positiveSignalCount, neutralSignalCount, negativeSignalCount,
    netDeltaBps, firstSignalTick, lastSignalTick, byReason: freezeArray(reasonAggregates) });
}

function makeLedger(options: Required<ReputationLedgerOptions>, signals: readonly ReputationSignalRecord[]): ReputationLedger {
  const orderedSignals = [...signals].sort((left, right) => left.eventId.localeCompare(right.eventId));
  const appliedEventIds = orderedSignals.map((signal) => signal.eventId);
  const metrics = createMetrics(orderedSignals);
  const scoreBps = clamp(options.baselineScoreBps + metrics.netDeltaBps, options.minimumScoreBps, options.maximumScoreBps);
  const base = { version: PHASE_3_REPUTATION_DOMAIN_VERSION, formulaVersion: PHASE_3_REPUTATION_FORMULA_VERSION,
    baselineScoreBps: options.baselineScoreBps, minimumScoreBps: options.minimumScoreBps, maximumScoreBps: options.maximumScoreBps,
    maximumSignals: options.maximumSignals, scoreBps, appliedEventIds: freezeArray(appliedEventIds),
    signals: freezeArray(orderedSignals), metrics } satisfies Omit<ReputationLedger, 'checksum'>;
  return freezeObject({ ...base, checksum: reputationLedgerChecksum(base) });
}

/** Create a bounded empty ledger. */
export function createReputationLedger(options: ReputationLedgerOptions = {}): ReputationLedger {
  const minimumScoreBps = options.minimumScoreBps ?? PHASE_3_REPUTATION_FORMULAS.minimumScoreBps;
  const maximumScoreBps = options.maximumScoreBps ?? PHASE_3_REPUTATION_FORMULAS.maximumScoreBps;
  const baselineScoreBps = options.baselineScoreBps ?? PHASE_3_REPUTATION_FORMULAS.baselineScoreBps;
  const maximumSignals = options.maximumSignals ?? PHASE_3_REPUTATION_FORMULAS.defaultMaximumSignals;
  nonNegativeInteger(minimumScoreBps, 'minimumScoreBps');
  nonNegativeInteger(maximumScoreBps, 'maximumScoreBps');
  nonNegativeInteger(baselineScoreBps, 'baselineScoreBps');
  if (maximumScoreBps <= minimumScoreBps) throw new ReputationValidationError('invalid-input', 'maximumScoreBps must exceed minimumScoreBps');
  if (baselineScoreBps < minimumScoreBps || baselineScoreBps > maximumScoreBps) throw new ReputationValidationError('invalid-input', 'baselineScoreBps must be within score bounds');
  if (maximumSignals <= 0) throw new ReputationValidationError('invalid-input', 'maximumSignals must be positive');
  return makeLedger({ baselineScoreBps, minimumScoreBps, maximumScoreBps, maximumSignals }, []);
}

function assertSignalRecord(record: ReputationSignalRecord): void {
  if (record.version !== PHASE_3_REPUTATION_DOMAIN_VERSION || record.formulaVersion !== PHASE_3_REPUTATION_FORMULA_VERSION) {
    throw new ReputationValidationError('unsupported-version', 'unsupported reputation signal record version');
  }
  text(record.eventId, 'signal eventId');
  text(record.guestId, 'signal guestId');
  nonNegativeInteger(record.tick, 'signal tick');
  integer(record.deltaBps, 'signal deltaBps');
  const sum = REPUTATION_REASON_CODES.reduce((total, reasonCode) => {
    const value = record.reasonVectorBps[reasonCode];
    integer(value, `reason vector ${reasonCode}`);
    return total + value;
  }, 0);
  if (sum !== record.deltaBps) throw new ReputationValidationError('invalid-ledger', 'signal reason vector does not reconcile');
  if (record.checksum !== reputationSignalChecksum(record)) throw new ReputationValidationError('checksum-mismatch', 'signal checksum does not match payload');
}

/** Strong validation, including nested signal and ledger checksums. */
export function assertReputationLedger(value: unknown): asserts value is ReputationLedger {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) throw new ReputationValidationError('invalid-ledger', 'ledger must be an object');
  const ledger = value as ReputationLedger;
  if (ledger.version !== PHASE_3_REPUTATION_DOMAIN_VERSION || ledger.formulaVersion !== PHASE_3_REPUTATION_FORMULA_VERSION) {
    throw new ReputationValidationError('unsupported-version', 'unsupported reputation ledger version');
  }
  nonNegativeInteger(ledger.baselineScoreBps, 'baselineScoreBps');
  nonNegativeInteger(ledger.minimumScoreBps, 'minimumScoreBps');
  nonNegativeInteger(ledger.maximumScoreBps, 'maximumScoreBps');
  nonNegativeInteger(ledger.maximumSignals, 'maximumSignals');
  nonNegativeInteger(ledger.scoreBps, 'scoreBps');
  if (ledger.maximumScoreBps <= ledger.minimumScoreBps || ledger.baselineScoreBps < ledger.minimumScoreBps || ledger.baselineScoreBps > ledger.maximumScoreBps) {
    throw new ReputationValidationError('invalid-ledger', 'ledger score bounds are invalid');
  }
  if (ledger.scoreBps < ledger.minimumScoreBps || ledger.scoreBps > ledger.maximumScoreBps) {
    throw new ReputationValidationError('invalid-ledger', 'ledger score is outside score bounds');
  }
  if (!Array.isArray(ledger.appliedEventIds) || !Array.isArray(ledger.signals) || ledger.signals.length !== ledger.appliedEventIds.length) {
    throw new ReputationValidationError('invalid-ledger', 'ledger signal arrays do not reconcile');
  }
  if (ledger.signals.length > ledger.maximumSignals) throw new ReputationValidationError('capacity-exceeded', 'ledger exceeds maximum signal capacity');
  for (let index = 0; index < ledger.signals.length; index += 1) {
    const signal = ledger.signals[index]!;
    assertSignalRecord(signal);
    if (ledger.appliedEventIds[index] !== signal.eventId) throw new ReputationValidationError('invalid-ledger', 'event id index does not reconcile');
    if (index > 0 && ledger.appliedEventIds[index - 1]! >= signal.eventId) throw new ReputationValidationError('invalid-ledger', 'event ids must be unique and sorted');
  }
  const metrics = createMetrics(ledger.signals);
  if (JSON.stringify(metrics) !== JSON.stringify(ledger.metrics)) throw new ReputationValidationError('invalid-ledger', 'ledger metrics do not reconcile');
  const expectedScore = clamp(ledger.baselineScoreBps + metrics.netDeltaBps, ledger.minimumScoreBps, ledger.maximumScoreBps);
  if (ledger.scoreBps !== expectedScore) throw new ReputationValidationError('invalid-ledger', 'ledger score does not reconcile');
  if (ledger.checksum !== reputationLedgerChecksum(ledger)) throw new ReputationValidationError('checksum-mismatch', 'ledger checksum does not match payload');
}

export function isReputationLedger(value: unknown): value is ReputationLedger {
  try { assertReputationLedger(value); return true; } catch { return false; }
}

/** Apply one signal idempotently. Duplicate event ids return the same ledger. */
export function applyReputationSignal(ledger: ReputationLedger, signal: ReputationSignal): ReputationApplyResult {
  assertReputationLedger(ledger);
  const record = evaluateReputationSignal(signal);
  const existing = ledger.signals.find((candidate) => candidate.eventId === signal.eventId);
  if (existing) {
    if (existing.checksum !== record.checksum) {
      throw new ReputationValidationError('invalid-input', `event id ${signal.eventId} was reused with a different reputation signal`);
    }
    return freezeObject({ ledger, applied: false, duplicate: true, signal: existing });
  }
  if (ledger.signals.length >= ledger.maximumSignals) throw new ReputationValidationError('capacity-exceeded', 'reputation ledger signal capacity exceeded');
  return freezeObject({ ledger: makeLedger({ baselineScoreBps: ledger.baselineScoreBps, minimumScoreBps: ledger.minimumScoreBps,
    maximumScoreBps: ledger.maximumScoreBps, maximumSignals: ledger.maximumSignals }, [...ledger.signals, record]), applied: true, duplicate: false, signal: record });
}

export function recordExperienceSignal(ledger: ReputationLedger, signal: Omit<ReputationExperienceSignal, 'kind'>): ReputationApplyResult {
  return applyReputationSignal(ledger, { ...signal, kind: 'experience' });
}

export function recordSafetySignal(ledger: ReputationLedger, signal: Omit<ReputationSafetySignal, 'kind'>): ReputationApplyResult {
  return applyReputationSignal(ledger, { ...signal, kind: 'safety' });
}

export function recordSafetyMetricsSignal(ledger: ReputationLedger, signal: Omit<ReputationSafetyMetricsSignal, 'kind'>): ReputationApplyResult {
  return applyReputationSignal(ledger, { ...signal, kind: 'safety-metrics' });
}

export function reputationScore(ledger: ReputationLedger): number {
  assertReputationLedger(ledger);
  return ledger.scoreBps / ledger.maximumScoreBps;
}

export const validateReputationLedger = assertReputationLedger;
