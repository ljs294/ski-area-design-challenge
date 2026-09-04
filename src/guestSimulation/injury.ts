/**
 * Phase 4 traversal-scoped injury hazard.
 *
 * This is intentionally a small, dependency-neutral domain seam.  It does
 * not mutate a guest, schedule into the engine, or inspect a map.  The engine
 * can call `evaluateTraversalInjury` once when a guest enters a run and, when
 * `scheduledIncident` is non-null, enqueue that one incident in its normal
 * incident/event phase.  There are no per-frame rolls in this module.
 *
 * Calibration status is deliberately explicit: the coefficients below are
 * gameplay placeholders, not observed injury rates.  A later calibration
 * changes the formula version and should provide a migration/golden-fixture
 * plan before it is shipped.
 */

import { eventCalendarChecksum } from './eventCalendar.ts';
import type { SimulatedSecond } from './contracts.ts';
import { keyedRandomFloat, type RandomSeed } from './random.ts';

export const PHASE_4_INJURY_DOMAIN_VERSION = 1 as const;
export const PHASE_4_INJURY_FORMULA_VERSION = 1 as const;
export const PHASE_4_INJURY_CALIBRATION_STATUS = 'uncalibrated' as const;

export type Phase4InjuryDomainVersion = typeof PHASE_4_INJURY_DOMAIN_VERSION;
export type Phase4InjuryFormulaVersion = typeof PHASE_4_INJURY_FORMULA_VERSION;
export type InjuryCalibrationStatus = typeof PHASE_4_INJURY_CALIBRATION_STATUS;

/**
 * Formula constants are part of the public contract so a replay, fixture, or
 * balance tool can identify exactly which gameplay rule produced a result.
 * The rate is per reference traversal, rather than per frame.
 */
export const PHASE_4_INJURY_FORMULAS = Object.freeze({
  version: PHASE_4_INJURY_FORMULA_VERSION,
  calibration: PHASE_4_INJURY_CALIBRATION_STATUS,
  referenceDurationSeconds: 300,
  minimumDurationMultiplier: 0.25,
  maximumDurationMultiplier: 4,
  hazardRatePerReferenceTraversal: 0.08,
  maximumProbability: 0.35,
  weights: Object.freeze({
    abilityDeficit: 0.34,
    effectiveDifficulty: 0.18,
    traffic: 0.14,
    coverage: 0.14,
    grooming: 0.10,
    exposure: 0.10,
  }),
} as const);

export type InjuryReasonCode = keyof typeof PHASE_4_INJURY_FORMULAS.weights;

export type InjurySeverity = 'minor' | 'moderate' | 'major';
export type InjuryOutcomeSeverity = InjurySeverity | 'none';

/**
 * All factors are hazard-direction values in [0, 1].  Coverage and grooming
 * are deliberately inverted here: `coverage = 1 - input.coverage` and
 * `grooming = 1 - input.grooming`, so larger values always mean more risk.
 */
export interface InjuryRiskFactors {
  readonly abilityDeficit: number;
  readonly effectiveDifficulty: number;
  readonly traffic: number;
  readonly coverage: number;
  readonly grooming: number;
  readonly exposure: number;
}

/** Weighted reason contributions; the six fields sum to `hazardScore`. */
export type InjuryReasonVector = InjuryRiskFactors;

export interface InjuryTraversalInput {
  readonly worldSeed: RandomSeed;
  /** Stable individual identity, not a party alias. */
  readonly guestId: string;
  /** Stable run/trail identity used for human-facing diagnostics. */
  readonly runId: string;
  /** Unique traversal identity (normally guest + run + traversal ordinal). */
  readonly traversalId: string;
  readonly entryTick: SimulatedSecond;
  /** Positive whole seconds. The interval is [entryTick, entryTick + duration). */
  readonly durationSeconds: SimulatedSecond;
  /** Stable ordinal for this guest's injury-entry decision domain. */
  readonly decisionOrdinal: number;
  /** Actual guest skiing ability in [0, 1]. */
  readonly ability: number;
  /** Condition-adjusted run difficulty in [0, 1]. */
  readonly effectiveDifficulty: number;
  /** Run traffic as an occupancy/capacity ratio; zero is empty and >1 is allowed. */
  readonly traffic: number;
  /** Fraction of the run with skiable coverage in [0, 1]. */
  readonly coverage: number;
  /** Grooming quality in [0, 1]. */
  readonly grooming: number;
  /** Route/visibility/weather exposure in [0, 1]. */
  readonly exposure: number;
}

export interface InjuryIncident {
  readonly version: Phase4InjuryDomainVersion;
  readonly formulaVersion: Phase4InjuryFormulaVersion;
  readonly calibration: InjuryCalibrationStatus;
  readonly id: string;
  readonly guestId: string;
  readonly runId: string;
  readonly traversalId: string;
  readonly entryTick: SimulatedSecond;
  readonly incidentTick: SimulatedSecond;
  /** Position along the traversal, where 0 is run entry and 1 is run exit. */
  readonly positionFraction: number;
  readonly severity: InjurySeverity;
  readonly primaryReasonCode: InjuryReasonCode;
  readonly reasonVector: InjuryReasonVector;
}

export interface InjuryTraversalResult {
  readonly version: Phase4InjuryDomainVersion;
  readonly formulaVersion: Phase4InjuryFormulaVersion;
  readonly calibration: InjuryCalibrationStatus;
  readonly guestId: string;
  readonly runId: string;
  readonly traversalId: string;
  readonly entryTick: SimulatedSecond;
  readonly durationSeconds: SimulatedSecond;
  /** Hazard-direction normalized factors before weighting. */
  readonly factors: InjuryRiskFactors;
  /** Weighted contributions, suitable for thought/diagnostic explanations. */
  readonly reasonVector: InjuryReasonVector;
  readonly hazardScore: number;
  readonly probability: number;
  /** The only stochastic draw made by this evaluation, at run entry. */
  readonly entryDraw: number;
  readonly randomDrawCount: 1;
  readonly severity: InjuryOutcomeSeverity;
  /** At most one incident can be scheduled for a traversal. */
  readonly scheduledIncident: InjuryIncident | null;
  readonly checksum: string;
}

export type InjuryValidationCode =
  | 'invalid-input'
  | 'invalid-result'
  | 'unsupported-version'
  | 'checksum-mismatch';

export class InjuryValidationError extends RangeError {
  readonly code: InjuryValidationCode;

  constructor(code: InjuryValidationCode, message: string) {
    super(message);
    this.name = 'InjuryValidationError';
    this.code = code;
  }
}

const REASON_CODES = Object.freeze([
  'abilityDeficit',
  'effectiveDifficulty',
  'traffic',
  'coverage',
  'grooming',
  'exposure',
] as const);

function freezeObject<T extends object>(value: T): Readonly<T> {
  return Object.freeze(value);
}

function freezeArray<T>(values: readonly T[]): readonly T[] {
  return Object.freeze([...values]);
}

function assertRecord(value: unknown, label: string): asserts value is Record<string, unknown> {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    throw new InjuryValidationError('invalid-input', `${label} must be an object`);
  }
}

function assertText(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new InjuryValidationError('invalid-input', `${label} must be a non-empty string`);
  }
}

function assertFinite(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    throw new InjuryValidationError('invalid-input', `${label} must be finite`);
  }
}

function assertUnit(value: unknown, label: string): asserts value is number {
  assertFinite(value, label);
  if (value < 0 || value > 1) {
    throw new InjuryValidationError('invalid-input', `${label} must be in [0, 1]`);
  }
}

function assertNonNegative(value: unknown, label: string): asserts value is number {
  assertFinite(value, label);
  if (value < 0) throw new InjuryValidationError('invalid-input', `${label} must be non-negative`);
}

function assertTick(value: unknown, label: string): asserts value is SimulatedSecond {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) {
    throw new InjuryValidationError('invalid-input', `${label} must be a non-negative whole second`);
  }
}

function assertOrdinal(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isSafeInteger(value) || value < 0) {
    throw new InjuryValidationError('invalid-input', `${label} must be a non-negative safe integer`);
  }
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
}

function normalizeTraffic(traffic: number): number {
  // This preserves monotonicity for an uncapped occupancy/capacity ratio and
  // keeps the weighted factor in [0, 1].
  return traffic / (1 + traffic);
}

function validateTraversalInput(input: InjuryTraversalInput): void {
  assertRecord(input, 'injury traversal input');
  assertText(input.guestId, 'guestId');
  assertText(input.runId, 'runId');
  assertText(input.traversalId, 'traversalId');
  assertTick(input.entryTick, 'entryTick');
  assertTick(input.durationSeconds, 'durationSeconds');
  if (input.durationSeconds <= 0) {
    throw new InjuryValidationError('invalid-input', 'durationSeconds must be positive');
  }
  assertOrdinal(input.decisionOrdinal, 'decisionOrdinal');
  assertUnit(input.ability, 'ability');
  assertUnit(input.effectiveDifficulty, 'effectiveDifficulty');
  assertNonNegative(input.traffic, 'traffic');
  assertUnit(input.coverage, 'coverage');
  assertUnit(input.grooming, 'grooming');
  assertUnit(input.exposure, 'exposure');
  if (input.entryTick > Number.MAX_SAFE_INTEGER - input.durationSeconds) {
    throw new InjuryValidationError('invalid-input', 'traversal interval exceeds safe tick range');
  }
}

function factorsFor(input: InjuryTraversalInput): InjuryRiskFactors {
  return freezeObject({
    abilityDeficit: clamp(input.effectiveDifficulty - input.ability, 0, 1),
    effectiveDifficulty: input.effectiveDifficulty,
    traffic: normalizeTraffic(input.traffic),
    coverage: 1 - input.coverage,
    grooming: 1 - input.grooming,
    exposure: input.exposure,
  });
}

function reasonVectorFor(factors: InjuryRiskFactors): InjuryReasonVector {
  const weights = PHASE_4_INJURY_FORMULAS.weights;
  return freezeObject({
    abilityDeficit: factors.abilityDeficit * weights.abilityDeficit,
    effectiveDifficulty: factors.effectiveDifficulty * weights.effectiveDifficulty,
    traffic: factors.traffic * weights.traffic,
    coverage: factors.coverage * weights.coverage,
    grooming: factors.grooming * weights.grooming,
    exposure: factors.exposure * weights.exposure,
  });
}

function vectorSum(vector: InjuryReasonVector): number {
  return REASON_CODES.reduce((sum, code) => sum + vector[code], 0);
}

function durationMultiplier(durationSeconds: number): number {
  return clamp(durationSeconds / PHASE_4_INJURY_FORMULAS.referenceDurationSeconds,
    PHASE_4_INJURY_FORMULAS.minimumDurationMultiplier,
    PHASE_4_INJURY_FORMULAS.maximumDurationMultiplier);
}

function injuryProbability(hazardScore: number, durationSeconds: number): number {
  const rate = PHASE_4_INJURY_FORMULAS.hazardRatePerReferenceTraversal
    * hazardScore * durationMultiplier(durationSeconds);
  return clamp(1 - Math.exp(-rate), 0, PHASE_4_INJURY_FORMULAS.maximumProbability);
}

function primaryReasonCodeFor(reasonVector: InjuryReasonVector): InjuryReasonCode {
  let primary: InjuryReasonCode = REASON_CODES[0];
  for (const code of REASON_CODES.slice(1)) {
    // Stable source order deliberately resolves equal contributions.
    if (reasonVector[code] > reasonVector[primary]) primary = code;
  }
  return primary;
}

function severityFor(hazardScore: number): InjurySeverity {
  if (hazardScore < 0.25) return 'minor';
  if (hazardScore < 0.55) return 'moderate';
  return 'major';
}

function incidentFor(input: InjuryTraversalInput, probability: number, entryDraw: number,
  reasonVector: InjuryReasonVector, hazardScore: number): InjuryIncident | null {
  if (!(entryDraw < probability)) return null;
  // Conditional on the event, this remains uniform in [0, 1), while reusing
  // the one entry draw. No second keyed draw is permitted for position.
  const positionFraction = clamp(entryDraw / probability, 0, 0.9999999999999999);
  const incidentOffset = Math.min(input.durationSeconds - 1, Math.floor(positionFraction * input.durationSeconds));
  return freezeObject({
    version: PHASE_4_INJURY_DOMAIN_VERSION,
    formulaVersion: PHASE_4_INJURY_FORMULA_VERSION,
    calibration: PHASE_4_INJURY_CALIBRATION_STATUS,
    id: `phase4-injury:${input.guestId}:${input.runId}:${input.traversalId}`,
    guestId: input.guestId,
    runId: input.runId,
    traversalId: input.traversalId,
    entryTick: input.entryTick,
    incidentTick: input.entryTick + incidentOffset,
    positionFraction,
    severity: severityFor(hazardScore),
    primaryReasonCode: primaryReasonCodeFor(reasonVector),
    reasonVector,
  });
}

function resultProjection(result: Omit<InjuryTraversalResult, 'checksum'>): unknown {
  // The checksum is intentionally excluded even when a caller passes the
  // complete persisted result through the public checksum helper.
  const { checksum: _checksum, ...projection } = result as InjuryTraversalResult;
  return projection;
}

/** Compute the deterministic checksum for a Phase 4 traversal outcome. */
export function injuryTraversalChecksum(
  result: Pick<InjuryTraversalResult, Exclude<keyof InjuryTraversalResult, 'checksum'>>,
): string {
  return eventCalendarChecksum(resultProjection(result as Omit<InjuryTraversalResult, 'checksum'>));
}

/**
 * Evaluate one run entry. This function performs exactly one keyed random
 * draw. Re-running the same input is therefore replay-safe and frame-rate
 * independent.
 */
export function evaluateTraversalInjury(input: InjuryTraversalInput): InjuryTraversalResult {
  validateTraversalInput(input);
  const factors = factorsFor(input);
  const reasonVector = reasonVectorFor(factors);
  const hazardScore = vectorSum(reasonVector);
  const probability = injuryProbability(hazardScore, input.durationSeconds);
  const entryDraw = keyedRandomFloat(input.worldSeed, input.guestId, 'phase4-injury-run-entry', input.decisionOrdinal);
  const scheduledIncident = incidentFor(input, probability, entryDraw, reasonVector, hazardScore);
  const severity: InjuryOutcomeSeverity = scheduledIncident?.severity ?? 'none';
  const base = freezeObject({
    version: PHASE_4_INJURY_DOMAIN_VERSION,
    formulaVersion: PHASE_4_INJURY_FORMULA_VERSION,
    calibration: PHASE_4_INJURY_CALIBRATION_STATUS,
    guestId: input.guestId,
    runId: input.runId,
    traversalId: input.traversalId,
    entryTick: input.entryTick,
    durationSeconds: input.durationSeconds,
    factors,
    reasonVector,
    hazardScore,
    probability,
    entryDraw,
    randomDrawCount: 1 as const,
    severity,
    scheduledIncident,
  });
  return freezeObject({ ...base, checksum: injuryTraversalChecksum(base) });
}

export const evaluateInjuryAtRunEntry = evaluateTraversalInjury;
export const scheduleTraversalInjury = evaluateTraversalInjury;

function assertReasonVector(value: unknown, label: string, code: InjuryValidationCode): asserts value is InjuryReasonVector {
  assertRecord(value, label);
  for (const reason of REASON_CODES) {
    const factor = value[reason];
    if (typeof factor !== 'number' || !Number.isFinite(factor) || factor < 0 || factor > PHASE_4_INJURY_FORMULAS.weights[reason]) {
      throw new InjuryValidationError(code, `${label}.${reason} is outside its weighted bound`);
    }
  }
}

/** Strong runtime validation for a persisted/replayed result. */
export function assertInjuryTraversalResult(value: unknown): asserts value is InjuryTraversalResult {
  assertRecord(value, 'injury traversal result');
  if (value.version !== PHASE_4_INJURY_DOMAIN_VERSION || value.formulaVersion !== PHASE_4_INJURY_FORMULA_VERSION
    || value.calibration !== PHASE_4_INJURY_CALIBRATION_STATUS) {
    throw new InjuryValidationError('unsupported-version', 'unsupported Phase 4 injury result version');
  }
  assertText(value.guestId, 'result guestId');
  assertText(value.runId, 'result runId');
  assertText(value.traversalId, 'result traversalId');
  assertTick(value.entryTick, 'result entryTick');
  assertTick(value.durationSeconds, 'result durationSeconds');
  if (value.durationSeconds <= 0) throw new InjuryValidationError('invalid-result', 'result durationSeconds must be positive');
  // Validate the structured body without using input validation (the original
  // ability/coverage inputs intentionally are not persisted in the result).
  const result = value as unknown as InjuryTraversalResult;
  for (const reason of REASON_CODES) {
    const factor = result.factors[reason];
    if (typeof factor !== 'number' || !Number.isFinite(factor) || factor < 0 || factor > 1) {
      throw new InjuryValidationError('invalid-result', `result factors.${reason} must be in [0, 1]`);
    }
  }
  assertReasonVector(result.reasonVector, 'result reasonVector', 'invalid-result');
  assertUnit(result.hazardScore, 'result hazardScore');
  assertUnit(result.probability, 'result probability');
  if (typeof result.entryDraw !== 'number' || result.entryDraw < 0 || result.entryDraw >= 1) {
    throw new InjuryValidationError('invalid-result', 'result entryDraw must be in [0, 1)');
  }
  if (result.randomDrawCount !== 1) throw new InjuryValidationError('invalid-result', 'result must contain exactly one random draw');
  if (!['none', 'minor', 'moderate', 'major'].includes(result.severity)) {
    throw new InjuryValidationError('invalid-result', 'result severity is invalid');
  }
  const expectedHazard = vectorSum(result.reasonVector);
  if (result.hazardScore !== expectedHazard) throw new InjuryValidationError('invalid-result', 'result hazard score does not match reason vector');
  const expectedProbability = injuryProbability(result.hazardScore, result.durationSeconds);
  if (result.probability !== expectedProbability) throw new InjuryValidationError('invalid-result', 'result probability does not match formula');
  const expectedIncident = result.entryDraw < result.probability;
  if ((result.scheduledIncident !== null) !== expectedIncident) throw new InjuryValidationError('invalid-result', 'result incident does not match entry draw');
  if (result.scheduledIncident === null) {
    if (result.severity !== 'none') throw new InjuryValidationError('invalid-result', 'non-event result cannot have injury severity');
  } else {
    const incident = result.scheduledIncident;
    if (incident.version !== PHASE_4_INJURY_DOMAIN_VERSION || incident.formulaVersion !== PHASE_4_INJURY_FORMULA_VERSION
      || incident.calibration !== PHASE_4_INJURY_CALIBRATION_STATUS || incident.guestId !== result.guestId
      || incident.runId !== result.runId || incident.traversalId !== result.traversalId || incident.entryTick !== result.entryTick) {
      throw new InjuryValidationError('invalid-result', 'scheduled incident identity does not match traversal');
    }
    if (!Number.isSafeInteger(incident.incidentTick) || incident.incidentTick < result.entryTick
      || incident.incidentTick >= result.entryTick + result.durationSeconds) {
      throw new InjuryValidationError('invalid-result', 'scheduled incident tick is outside traversal interval');
    }
    assertUnit(incident.positionFraction, 'incident positionFraction');
    if (!['minor', 'moderate', 'major'].includes(incident.severity)) throw new InjuryValidationError('invalid-result', 'incident severity is invalid');
    if (!REASON_CODES.includes(incident.primaryReasonCode)) throw new InjuryValidationError('invalid-result', 'incident reason code is invalid');
    assertReasonVector(incident.reasonVector, 'incident reasonVector', 'invalid-result');
    if (incident.severity !== result.severity
      || REASON_CODES.some((reason) => incident.reasonVector[reason] !== result.reasonVector[reason])) {
      throw new InjuryValidationError('invalid-result', 'scheduled incident details do not match result');
    }
  }
  if (typeof result.checksum !== 'string' || result.checksum.length === 0) {
    throw new InjuryValidationError('invalid-result', 'result checksum is missing');
  }
  if (result.checksum !== injuryTraversalChecksum(result)) throw new InjuryValidationError('checksum-mismatch', 'injury result checksum does not match payload');
}

export function isInjuryTraversalResult(value: unknown): value is InjuryTraversalResult {
  try {
    assertInjuryTraversalResult(value);
    return true;
  } catch {
    return false;
  }
}

/** Return a zero-or-one incident list for adapters that consume event arrays. */
export function traversalIncidents(result: InjuryTraversalResult): readonly InjuryIncident[] {
  assertInjuryTraversalResult(result);
  return result.scheduledIncident === null ? freezeArray([]) : freezeArray([result.scheduledIncident]);
}
