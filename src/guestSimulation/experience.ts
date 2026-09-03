/**
 * Phase 2 guest experience kernels.
 *
 * This module deliberately contains no engine, UI, or persistence imports. It
 * is a collection of small, named formulas that can be evaluated by a worker,
 * a replay, or a UI preview. Every stochastic decision is keyed by world,
 * entity, domain, and ordinal; there is no mutable/random global state here.
 */

import type { RandomSeed } from './random.ts';
import { keyedRandomFloat } from './random.ts';

export const PHASE_2_EXPERIENCE_VERSION = 1 as const;
export type Phase2ExperienceVersion = typeof PHASE_2_EXPERIENCE_VERSION;

export const SATISFACTION_FORMULA_VERSION = 1 as const;
export const EXPECTATION_GAP_FORMULA_VERSION = 1 as const;
export const CROWDING_FORMULA_VERSION = 1 as const;
export const SUITABLE_TERRAIN_FORMULA_VERSION = 1 as const;
export const EARLY_DEPARTURE_FORMULA_VERSION = 1 as const;
export const THOUGHT_AGGREGATION_FORMULA_VERSION = 1 as const;

export const PHASE_2_FORMULAS = Object.freeze({
  satisfaction: Object.freeze({ name: 'weighted-satisfaction-channels', version: SATISFACTION_FORMULA_VERSION }),
  expectationGap: Object.freeze({ name: 'normalized-expectation-gap', version: EXPECTATION_GAP_FORMULA_VERSION }),
  crowding: Object.freeze({ name: 'load-and-queue-crowding', version: CROWDING_FORMULA_VERSION }),
  suitableTerrain: Object.freeze({ name: 'ability-fit-suitable-terrain', version: SUITABLE_TERRAIN_FORMULA_VERSION }),
  earlyDeparture: Object.freeze({ name: 'keyed-early-departure-hazard', version: EARLY_DEPARTURE_FORMULA_VERSION }),
  thoughtAggregation: Object.freeze({ name: 'exact-reason-coded-thought-counts', version: THOUGHT_AGGREGATION_FORMULA_VERSION }),
});

function finite(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new RangeError(`${label} must be finite`);
}

function nonNegative(value: number, label: string): void {
  finite(value, label);
  if (value < 0) throw new RangeError(`${label} must be non-negative`);
}

function positive(value: number, label: string): void {
  finite(value, label);
  if (value <= 0) throw new RangeError(`${label} must be positive`);
}

function unit(value: number, label: string): number {
  finite(value, label);
  return Math.min(1, Math.max(0, value));
}

function clamp(value: number, minimum: number, maximum: number): number {
  finite(value, 'value');
  if (maximum < minimum) throw new RangeError('maximum must be >= minimum');
  return Math.min(maximum, Math.max(minimum, value));
}

function freezeArray<T>(values: readonly T[]): readonly T[] {
  return Object.freeze([...values]);
}

function freezeObject<T extends object>(value: T): T {
  return Object.freeze(value);
}

export type ExpectationDirection = 'higher-is-better' | 'lower-is-better';

export interface ExpectationGapRequest {
  readonly expected: number;
  readonly actual: number;
  /** Positive signed gaps always mean that the result beat expectation. */
  readonly direction?: ExpectationDirection;
  /** A positive stabilizer in the denominator, useful for zero expectations. */
  readonly scale?: number;
}

export interface ExpectationGapResult {
  readonly formula: typeof PHASE_2_FORMULAS.expectationGap.name;
  readonly version: typeof EXPECTATION_GAP_FORMULA_VERSION;
  readonly expected: number;
  readonly actual: number;
  readonly direction: ExpectationDirection;
  /** Signed normalized gap: positive exceeded expectation, negative missed it. */
  readonly signedGap: number;
  /** A non-negative normalized disappointment amount. */
  readonly shortfall: number;
  /** A non-negative normalized amount by which the result exceeded expectation. */
  readonly surplus: number;
  readonly rawGap: number;
  readonly denominator: number;
}

/**
 * Normalize an expectation gap without allowing zero/negative baselines to
 * produce NaN. For a lower-is-better measure, a lower actual value is a
 * positive result. The denominator is explicit in the result for auditability.
 */
export function calculateExpectationGap(request: ExpectationGapRequest): ExpectationGapResult {
  finite(request.expected, 'expected');
  finite(request.actual, 'actual');
  const direction = request.direction ?? 'higher-is-better';
  if (direction !== 'higher-is-better' && direction !== 'lower-is-better') {
    throw new RangeError('expectation direction is invalid');
  }
  const scale = request.scale ?? 1;
  positive(scale, 'expectation scale');
  const unboundedRawGap = direction === 'higher-is-better'
    ? request.actual - request.expected
    : request.expected - request.actual;
  // Subtracting opposite finite extremes can overflow. Keep the diagnostic
  // fields finite and cap the normalized result to a meaningful one-unit
  // disappointment/surplus; no experience signal needs an infinite gap.
  const rawGap = Number.isFinite(unboundedRawGap)
    ? unboundedRawGap
    : Math.sign(unboundedRawGap) * Number.MAX_VALUE;
  const denominator = Math.max(1, Math.min(Number.MAX_VALUE, Math.abs(request.expected) + scale));
  const signedGap = clamp(rawGap / denominator, -1, 1);
  return freezeObject({
    formula: PHASE_2_FORMULAS.expectationGap.name,
    version: EXPECTATION_GAP_FORMULA_VERSION,
    expected: request.expected,
    actual: request.actual,
    direction,
    signedGap,
    shortfall: Math.max(0, -signedGap),
    surplus: Math.max(0, signedGap),
    rawGap,
    denominator,
  });
}

/** Friendly positional spelling for callers with scalar observations. */
export function expectationGap(
  expected: number,
  actual: number,
  direction: ExpectationDirection = 'higher-is-better',
  scale = 1,
): ExpectationGapResult {
  return calculateExpectationGap({ expected, actual, direction, scale });
}

export type CrowdingLevel = 'none' | 'light' | 'moderate' | 'severe';

export interface CrowdingEffectRequest {
  readonly occupancy: number;
  readonly capacity: number;
  readonly queueWaitSeconds?: number;
  readonly expectedQueueWaitSeconds?: number;
  /** 0 is least sensitive and 1 is most sensitive to crowding. */
  readonly sensitivity?: number;
}

export interface CrowdingEffectResult {
  readonly formula: typeof PHASE_2_FORMULAS.crowding.name;
  readonly version: typeof CROWDING_FORMULA_VERSION;
  readonly occupancy: number;
  readonly capacity: number;
  readonly loadRatio: number;
  readonly queuePressure: number;
  readonly effectiveLoad: number;
  /** [0, 1], where 0 is no penalty and 1 is the maximum penalty. */
  readonly penalty: number;
  /** [0, 1], the complement of penalty. */
  readonly score: number;
  /** A signed effect suitable for adding to a satisfaction ledger. */
  readonly effect: number;
  readonly sensitivity: number;
  readonly level: CrowdingLevel;
}

/**
 * Crowding combines facility load with excess queue pressure. The threshold
 * of 0.70 represents a normally comfortable load; above it the penalty rises
 * monotonically and reaches its cap at a load ratio of one.
 */
export function calculateCrowdingEffect(request: CrowdingEffectRequest): CrowdingEffectResult {
  nonNegative(request.occupancy, 'occupancy');
  positive(request.capacity, 'capacity');
  const queueWaitSeconds = request.queueWaitSeconds ?? 0;
  const expectedQueueWaitSeconds = request.expectedQueueWaitSeconds ?? 0;
  nonNegative(queueWaitSeconds, 'queue wait seconds');
  nonNegative(expectedQueueWaitSeconds, 'expected queue wait seconds');
  const sensitivity = unit(request.sensitivity ?? 0.5, 'crowding sensitivity');
  // Cap the diagnostic ratio before arithmetic so tiny positive capacities
  // and otherwise finite occupancy values cannot create Infinity downstream.
  const loadRatio = Math.min(2, request.occupancy / request.capacity);
  const queuePressure = calculateExpectationGap({
    expected: expectedQueueWaitSeconds,
    actual: queueWaitSeconds,
    direction: 'lower-is-better',
    scale: 60,
  }).shortfall;
  const effectiveLoad = clamp(loadRatio + 0.35 * queuePressure, 0, 2);
  const threshold = 0.70;
  const linearPenalty = clamp((effectiveLoad - threshold) / (1 - threshold), 0, 1);
  // Less sensitive guests experience a softer onset; sensitivity=1 remains
  // exactly linear. This preserves monotonicity for both inputs.
  const exponent = 1 + (1 - sensitivity) * 1.5;
  const penalty = Math.pow(linearPenalty, exponent);
  const level: CrowdingLevel = effectiveLoad <= threshold
    ? 'none'
    : effectiveLoad <= 0.90
      ? 'light'
      : effectiveLoad <= 1.20 ? 'moderate' : 'severe';
  return freezeObject({
    formula: PHASE_2_FORMULAS.crowding.name,
    version: CROWDING_FORMULA_VERSION,
    occupancy: request.occupancy,
    capacity: request.capacity,
    loadRatio,
    queuePressure,
    effectiveLoad,
    penalty,
    score: 1 - penalty,
    effect: -penalty,
    sensitivity,
    level,
  });
}

export const crowdingEffect = calculateCrowdingEffect;
export const crowdingPenalty = (request: CrowdingEffectRequest): number => calculateCrowdingEffect(request).penalty;

export type SuitableTerrainDirection = 'positive' | 'neutral' | 'negative';
export type SuitableTerrainReasonCode = 'well-matched' | 'too-easy' | 'too-difficult' | 'closed';

export interface SuitableTerrainRequest {
  readonly ability: number;
  readonly terrainDifficulty: number;
  readonly hardcoreTerrainPreference?: number;
  readonly riskTolerance?: number;
  readonly open?: boolean;
}

export interface SuitableTerrainOutcome {
  readonly formula: typeof PHASE_2_FORMULAS.suitableTerrain.name;
  readonly version: typeof SUITABLE_TERRAIN_FORMULA_VERSION;
  readonly ability: number;
  readonly terrainDifficulty: number;
  readonly targetDifficulty: number;
  readonly abilityGap: number;
  readonly fitScore: number;
  readonly safetyPenalty: number;
  readonly suitability: number;
  readonly suitable: boolean;
  readonly outcomeDirection: SuitableTerrainDirection;
  readonly reasonCode: SuitableTerrainReasonCode;
}

/**
 * Terrain is suitable when it is close to the guest's desired challenge and
 * does not exceed their ability. Hardcore preference shifts the desired
 * challenge upward, while risk tolerance controls how much over-ability risk
 * is tolerated. A closed resource is always negative.
 */
export function calculateSuitableTerrainOutcome(request: SuitableTerrainRequest): SuitableTerrainOutcome {
  const ability = unit(request.ability, 'ability');
  const terrainDifficulty = unit(request.terrainDifficulty, 'terrain difficulty');
  const hardcore = unit(request.hardcoreTerrainPreference ?? 0, 'hardcore terrain preference');
  const riskTolerance = unit(request.riskTolerance ?? 0.5, 'risk tolerance');
  const open = request.open ?? true;
  const targetDifficulty = clamp(ability + hardcore * (1 - ability) * 0.35, 0, 1);
  const tolerance = 0.18 + 0.12 * riskTolerance;
  const fitScore = clamp(1 - Math.abs(terrainDifficulty - targetDifficulty) / tolerance, 0, 1);
  const abilityGap = terrainDifficulty - ability;
  const safetyPenalty = abilityGap <= 0
    ? 0
    : clamp(abilityGap / Math.max(0.05, 1 - ability), 0, 1) * (1.10 - 0.35 * riskTolerance);
  const suitability = open ? clamp(fitScore - safetyPenalty * 0.55, 0, 1) : 0;
  const suitable = open && suitability >= 0.50 && safetyPenalty <= 0.25;
  const outcomeDirection: SuitableTerrainDirection = !open || suitability < 0.35
    ? 'negative'
    : suitability >= 0.67 ? 'positive' : 'neutral';
  const reasonCode: SuitableTerrainReasonCode = !open
    ? 'closed'
    : safetyPenalty > 0.25 || terrainDifficulty > ability + tolerance ? 'too-difficult'
      : terrainDifficulty < targetDifficulty - tolerance ? 'too-easy' : 'well-matched';
  return freezeObject({
    formula: PHASE_2_FORMULAS.suitableTerrain.name,
    version: SUITABLE_TERRAIN_FORMULA_VERSION,
    ability,
    terrainDifficulty,
    targetDifficulty,
    abilityGap,
    fitScore,
    safetyPenalty,
    suitability,
    suitable,
    outcomeDirection,
    reasonCode,
  });
}

export const suitableTerrainOutcome = calculateSuitableTerrainOutcome;
export const suitableTerrainOutcomeDirection = (request: SuitableTerrainRequest): SuitableTerrainDirection =>
  calculateSuitableTerrainOutcome(request).outcomeDirection;

export type SatisfactionChannelCode =
  | 'terrain'
  | 'wait'
  | 'crowding'
  | 'comfort'
  | 'conditions'
  | 'value'
  | 'variety'
  | 'safety';

export const SATISFACTION_CHANNELS: readonly SatisfactionChannelCode[] = Object.freeze([
  'terrain', 'wait', 'crowding', 'comfort', 'conditions', 'value', 'variety', 'safety',
]);

export const DEFAULT_SATISFACTION_WEIGHTS: Readonly<Record<SatisfactionChannelCode, number>> = Object.freeze({
  terrain: 0.20,
  wait: 0.16,
  crowding: 0.14,
  comfort: 0.12,
  conditions: 0.12,
  value: 0.10,
  variety: 0.08,
  safety: 0.08,
});

export interface SatisfactionChannelsRequest {
  readonly terrainFit: number;
  readonly queueWaitSeconds: number;
  readonly expectedQueueWaitSeconds: number;
  /** A number is interpreted as a [0, 1] penalty; a result is read by penalty. */
  readonly crowding: number | CrowdingEffectResult;
  readonly comfort: number;
  readonly conditions: number;
  readonly value: number;
  readonly variety: number;
  readonly safety: number;
  readonly weights?: Partial<Record<SatisfactionChannelCode, number>>;
}

export interface SatisfactionChannelScore {
  readonly channel: SatisfactionChannelCode;
  readonly score: number;
  readonly weight: number;
  readonly weightedContribution: number;
  readonly expectationGap?: ExpectationGapResult;
}

export interface SatisfactionChannelsResult {
  readonly formula: typeof PHASE_2_FORMULAS.satisfaction.name;
  readonly version: typeof SATISFACTION_FORMULA_VERSION;
  readonly channels: readonly SatisfactionChannelScore[];
  readonly overall: number;
  readonly weights: Readonly<Record<SatisfactionChannelCode, number>>;
}

function crowdingPenaltyFromInput(value: number | CrowdingEffectResult): number {
  if (typeof value === 'number') return unit(value, 'crowding penalty');
  if (!value || value.formula !== PHASE_2_FORMULAS.crowding.name || value.version !== CROWDING_FORMULA_VERSION) {
    throw new RangeError('crowding result has an unsupported formula version');
  }
  return unit(value.penalty, 'crowding penalty');
}

function satisfactionWeights(overrides: Partial<Record<SatisfactionChannelCode, number>> | undefined): Readonly<Record<SatisfactionChannelCode, number>> {
  const candidate = { ...DEFAULT_SATISFACTION_WEIGHTS, ...overrides } as Record<SatisfactionChannelCode, number>;
  let total = 0;
  for (const channel of SATISFACTION_CHANNELS) {
    candidate[channel] = unit(candidate[channel]!, `weight for ${channel}`);
    total += candidate[channel]!;
  }
  if (!(total > 0)) throw new RangeError('satisfaction weights must have a positive sum');
  const normalized = {} as Record<SatisfactionChannelCode, number>;
  for (const channel of SATISFACTION_CHANNELS) normalized[channel] = candidate[channel]! / total;
  return freezeObject(normalized);
}

/** Evaluate independently inspectable satisfaction channels and reconcile them into [0, 1]. */
export function calculateSatisfactionChannels(request: SatisfactionChannelsRequest): SatisfactionChannelsResult {
  const terrainFit = unit(request.terrainFit, 'terrain fit');
  const comfort = unit(request.comfort, 'comfort');
  const conditions = unit(request.conditions, 'conditions');
  const value = unit(request.value, 'value');
  const variety = unit(request.variety, 'variety');
  const safety = unit(request.safety, 'safety');
  nonNegative(request.queueWaitSeconds, 'queue wait seconds');
  nonNegative(request.expectedQueueWaitSeconds, 'expected queue wait seconds');
  const crowdingPenalty = crowdingPenaltyFromInput(request.crowding);
  const waitGap = calculateExpectationGap({
    expected: request.expectedQueueWaitSeconds,
    actual: request.queueWaitSeconds,
    direction: 'lower-is-better',
    scale: 60,
  });
  const scores: Readonly<Record<SatisfactionChannelCode, number>> = {
    terrain: terrainFit,
    wait: clamp(1 - waitGap.shortfall, 0, 1),
    crowding: 1 - crowdingPenalty,
    comfort,
    conditions,
    value,
    variety,
    safety,
  };
  const weights = satisfactionWeights(request.weights);
  const channels = SATISFACTION_CHANNELS.map((channel) => freezeObject({
    channel,
    score: scores[channel],
    weight: weights[channel],
    weightedContribution: scores[channel] * weights[channel],
    ...(channel === 'wait' ? { expectationGap: waitGap } : {}),
  }));
  const overall = clamp(channels.reduce((sum, channel) => sum + channel.weightedContribution, 0), 0, 1);
  return freezeObject({
    formula: PHASE_2_FORMULAS.satisfaction.name,
    version: SATISFACTION_FORMULA_VERSION,
    channels: freezeArray(channels),
    overall,
    weights,
  });
}

export const satisfactionChannels = calculateSatisfactionChannels;
export const satisfactionScore = (request: SatisfactionChannelsRequest): number => calculateSatisfactionChannels(request).overall;

export type EarlyDepartureReasonCode =
  | 'low-satisfaction'
  | 'expectation-shortfall'
  | 'crowding'
  | 'long-wait'
  | 'poor-conditions'
  | 'terrain-mismatch'
  | 'safety-concern'
  | 'value-concern'
  | 'injury';

export const EARLY_DEPARTURE_REASON_CODES: readonly EarlyDepartureReasonCode[] = Object.freeze([
  'low-satisfaction', 'expectation-shortfall', 'crowding', 'long-wait',
  'poor-conditions', 'terrain-mismatch', 'safety-concern', 'value-concern', 'injury',
]);

export interface EarlyDepartureRequest {
  readonly worldSeed: RandomSeed;
  readonly entityId: string;
  readonly decisionOrdinal?: number;
  readonly currentTick?: number;
  readonly plannedDepartureTick?: number | null;
  readonly satisfaction: number;
  readonly channels?: SatisfactionChannelsResult;
  readonly expectationGaps?: readonly ExpectationGapResult[];
  readonly crowding?: CrowdingEffectResult | number;
  readonly terrain?: SuitableTerrainOutcome;
  readonly conditions?: number;
  readonly safety?: number;
  readonly value?: number;
  readonly queueWaitSeconds?: number;
  readonly expectedQueueWaitSeconds?: number;
  readonly injured?: boolean;
}

export interface EarlyDepartureReasonScore {
  readonly reasonCode: EarlyDepartureReasonCode;
  readonly contribution: number;
}

export interface EarlyDepartureDecision {
  readonly formula: typeof PHASE_2_FORMULAS.earlyDeparture.name;
  readonly version: typeof EARLY_DEPARTURE_FORMULA_VERSION;
  readonly probability: number;
  readonly draw: number;
  readonly departedEarly: boolean;
  readonly eligible: boolean;
  readonly reasonCodes: readonly EarlyDepartureReasonCode[];
  readonly primaryReasonCode: EarlyDepartureReasonCode | null;
  readonly reasonScores: readonly EarlyDepartureReasonScore[];
}

function validateTick(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError(`${label} must be a non-negative safe integer`);
}

function scoreAtMost(value: number, threshold: number): number {
  return clamp((threshold - unit(value, 'score')) / threshold, 0, 1);
}

function channelScore(channels: SatisfactionChannelsResult | undefined, channel: SatisfactionChannelCode): number | undefined {
  return channels?.channels.find((candidate) => candidate.channel === channel)?.score;
}

/**
 * Evaluate an unplanned departure hazard. Reasons are ranked by their exact
 * contributions, while the final yes/no decision uses one keyed draw. With no
 * adverse signal the probability is exactly zero, making ordinary guests
 * stable under unrelated random draws.
 */
export function evaluateEarlyDeparture(request: EarlyDepartureRequest): EarlyDepartureDecision {
  if (typeof request.entityId !== 'string' || request.entityId.length === 0) throw new RangeError('entityId must be non-empty');
  const ordinal = request.decisionOrdinal ?? 0;
  if (!Number.isSafeInteger(ordinal) || ordinal < 0) throw new RangeError('decisionOrdinal must be a non-negative safe integer');
  const satisfaction = unit(request.satisfaction, 'satisfaction');
  if (request.currentTick !== undefined) validateTick(request.currentTick, 'current tick');
  if (request.plannedDepartureTick !== undefined && request.plannedDepartureTick !== null) {
    validateTick(request.plannedDepartureTick, 'planned departure tick');
  }
  if (request.currentTick !== undefined && request.plannedDepartureTick !== undefined && request.plannedDepartureTick !== null
    && request.currentTick >= request.plannedDepartureTick) {
    const draw = keyedRandomFloat(request.worldSeed, request.entityId, 'early-departure', ordinal);
    return freezeObject({ formula: PHASE_2_FORMULAS.earlyDeparture.name, version: EARLY_DEPARTURE_FORMULA_VERSION,
      probability: 0, draw, departedEarly: false, eligible: false, reasonCodes: freezeArray([]), primaryReasonCode: null,
      reasonScores: freezeArray([]) });
  }
  if (request.channels && (request.channels.formula !== PHASE_2_FORMULAS.satisfaction.name
    || request.channels.version !== SATISFACTION_FORMULA_VERSION)) {
    throw new RangeError('satisfaction channels have an unsupported formula version');
  }
  if (request.terrain && (request.terrain.formula !== PHASE_2_FORMULAS.suitableTerrain.name
    || request.terrain.version !== SUITABLE_TERRAIN_FORMULA_VERSION)) {
    throw new RangeError('terrain outcome has an unsupported formula version');
  }
  const scores = new Map<EarlyDepartureReasonCode, number>();
  const lowSatisfaction = scoreAtMost(satisfaction, 0.60);
  if (lowSatisfaction > 0) scores.set('low-satisfaction', lowSatisfaction);
  const crowdingInput = request.crowding;
  if (crowdingInput && typeof crowdingInput !== 'number'
    && (crowdingInput.formula !== PHASE_2_FORMULAS.crowding.name || crowdingInput.version !== CROWDING_FORMULA_VERSION)) {
    throw new RangeError('crowding result has an unsupported formula version');
  }
  const crowding = crowdingInput === undefined
    ? 0
    : typeof crowdingInput === 'number' ? unit(crowdingInput, 'crowding penalty') : unit(crowdingInput.penalty, 'crowding penalty');
  if (crowding > 0.05) scores.set('crowding', crowding);
  const waitScore = channelScore(request.channels, 'wait');
  const queueWait = request.queueWaitSeconds ?? 0;
  const expectedWait = request.expectedQueueWaitSeconds ?? 0;
  nonNegative(queueWait, 'queue wait seconds');
  nonNegative(expectedWait, 'expected queue wait seconds');
  const waitShortfall = waitScore === undefined
    ? calculateExpectationGap({ expected: expectedWait, actual: queueWait, direction: 'lower-is-better', scale: 60 }).shortfall
    : 1 - waitScore;
  if (waitShortfall > 0.05) scores.set('long-wait', clamp(waitShortfall, 0, 1));
  if ((request.expectationGaps ?? []).some((gap) => {
    if (gap.formula !== PHASE_2_FORMULAS.expectationGap.name || gap.version !== EXPECTATION_GAP_FORMULA_VERSION) {
      throw new RangeError('expectation gap has an unsupported formula version');
    }
    return gap.shortfall > 0.05;
  })) scores.set('expectation-shortfall', clamp(Math.max(...(request.expectationGaps ?? []).map((gap) => gap.shortfall)), 0, 1));
  const conditions = channelScore(request.channels, 'conditions') ?? (request.conditions ?? 1);
  if (scoreAtMost(conditions, 0.55) > 0.05) scores.set('poor-conditions', scoreAtMost(conditions, 0.55));
  const terrain = request.terrain;
  const terrainScore = terrain?.suitability ?? channelScore(request.channels, 'terrain') ?? 1;
  if (scoreAtMost(terrainScore, 0.45) > 0.05) scores.set('terrain-mismatch', scoreAtMost(terrainScore, 0.45));
  const safety = channelScore(request.channels, 'safety') ?? (request.safety ?? 1);
  if (scoreAtMost(safety, 0.60) > 0.05) scores.set('safety-concern', scoreAtMost(safety, 0.60));
  const value = channelScore(request.channels, 'value') ?? (request.value ?? 1);
  if (scoreAtMost(value, 0.50) > 0.05) scores.set('value-concern', scoreAtMost(value, 0.50));
  if (request.injured === true) scores.set('injury', 1);
  const reasonScores = EARLY_DEPARTURE_REASON_CODES
    .filter((reasonCode) => scores.has(reasonCode))
    .map((reasonCode) => freezeObject({ reasonCode, contribution: scores.get(reasonCode)! }))
    .sort((left, right) => right.contribution - left.contribution || left.reasonCode.localeCompare(right.reasonCode));
  const hazard = reasonScores.reduce((sum, reason) => sum + reason.contribution, 0);
  const probability = clamp(1 - Math.exp(-0.46 * hazard), 0, 0.98);
  const draw = keyedRandomFloat(request.worldSeed, request.entityId, 'early-departure', ordinal);
  const reasonCodes = reasonScores.map((reason) => reason.reasonCode);
  return freezeObject({ formula: PHASE_2_FORMULAS.earlyDeparture.name, version: EARLY_DEPARTURE_FORMULA_VERSION,
    probability, draw, departedEarly: draw < probability, eligible: true, reasonCodes: freezeArray(reasonCodes),
    primaryReasonCode: reasonCodes[0] ?? null, reasonScores: freezeArray(reasonScores) });
}

export const earlyDepartureDecision = evaluateEarlyDeparture;
export const earlyDepartureProbability = (request: EarlyDepartureRequest): number => evaluateEarlyDeparture(request).probability;

export type ExperienceThoughtReasonCode = EarlyDepartureReasonCode
  | 'arrival' | 'queueing' | 'riding' | 'skiing' | 'waiting' | 'leaving' | 'positive-experience';
export type ThoughtSentiment = 'positive' | 'neutral' | 'negative';

export const EXPERIENCE_THOUGHT_REASON_CODES: readonly ExperienceThoughtReasonCode[] = Object.freeze([
  ...EARLY_DEPARTURE_REASON_CODES, 'arrival', 'queueing', 'riding', 'skiing', 'waiting', 'leaving', 'positive-experience',
]);

export interface ExperienceThoughtObservation {
  readonly reasonCode: ExperienceThoughtReasonCode;
  readonly sentiment: ThoughtSentiment;
  /** Defaults to one; integer counts make reconciliation exact across batches. */
  readonly count?: number;
}

export interface ThoughtReasonAggregate {
  readonly reasonCode: ExperienceThoughtReasonCode;
  readonly count: number;
  readonly positiveCount: number;
  readonly neutralCount: number;
  readonly negativeCount: number;
  /** Dominant sentiment for compact UI rows; ties conservatively prefer negative. */
  readonly sentiment: ThoughtSentiment;
}

export interface ThoughtAggregation {
  readonly formula: typeof PHASE_2_FORMULAS.thoughtAggregation.name;
  readonly version: typeof THOUGHT_AGGREGATION_FORMULA_VERSION;
  readonly totalEvents: number;
  readonly positiveEvents: number;
  readonly neutralEvents: number;
  readonly negativeEvents: number;
  readonly byReason: readonly ThoughtReasonAggregate[];
  /** True only when both reason and sentiment partitions reconcile exactly. */
  readonly reconciled: boolean;
}

function validateThoughtCode(value: ExperienceThoughtReasonCode): void {
  if (!(EXPERIENCE_THOUGHT_REASON_CODES as readonly string[]).includes(value)) {
    throw new RangeError(`unknown thought reason code ${String(value)}`);
  }
}

function validateSentiment(value: ThoughtSentiment): void {
  if (value !== 'positive' && value !== 'neutral' && value !== 'negative') throw new RangeError('invalid thought sentiment');
}

function dominantSentiment(counts: Pick<ThoughtReasonAggregate, 'positiveCount' | 'neutralCount' | 'negativeCount'>): ThoughtSentiment {
  if (counts.negativeCount >= counts.neutralCount && counts.negativeCount >= counts.positiveCount) return 'negative';
  if (counts.neutralCount >= counts.positiveCount) return 'neutral';
  return 'positive';
}

/** Aggregate reason-coded observations with integer-only accounting. */
export function aggregateThoughtsByReason(observations: readonly ExperienceThoughtObservation[]): ThoughtAggregation {
  const byReason = new Map<ExperienceThoughtReasonCode, { count: number; positiveCount: number; neutralCount: number; negativeCount: number }>();
  let totalEvents = 0;
  let positiveEvents = 0;
  let neutralEvents = 0;
  let negativeEvents = 0;
  for (const observation of observations) {
    validateThoughtCode(observation.reasonCode);
    validateSentiment(observation.sentiment);
    const count = observation.count ?? 1;
    if (!Number.isSafeInteger(count) || count < 0) throw new RangeError('thought count must be a non-negative safe integer');
    const existing = byReason.get(observation.reasonCode) ?? { count: 0, positiveCount: 0, neutralCount: 0, negativeCount: 0 };
    existing.count += count;
    if (observation.sentiment === 'positive') { existing.positiveCount += count; positiveEvents += count; }
    if (observation.sentiment === 'neutral') { existing.neutralCount += count; neutralEvents += count; }
    if (observation.sentiment === 'negative') { existing.negativeCount += count; negativeEvents += count; }
    if (!Number.isSafeInteger(existing.count) || !Number.isSafeInteger(existing.positiveCount)
      || !Number.isSafeInteger(existing.neutralCount) || !Number.isSafeInteger(existing.negativeCount)
      || !Number.isSafeInteger(totalEvents + count) || !Number.isSafeInteger(positiveEvents)
      || !Number.isSafeInteger(neutralEvents) || !Number.isSafeInteger(negativeEvents)) {
      throw new RangeError('thought counts exceed the safe integer range');
    }
    byReason.set(observation.reasonCode, existing);
    totalEvents += count;
  }
  const aggregates = EXPERIENCE_THOUGHT_REASON_CODES
    .filter((reasonCode) => byReason.has(reasonCode))
    .map((reasonCode) => {
      const counts = byReason.get(reasonCode)!;
      return freezeObject({ reasonCode, ...counts, sentiment: dominantSentiment(counts) });
    });
  const reasonTotal = aggregates.reduce((sum, aggregate) => sum + aggregate.count, 0);
  const sentimentTotal = positiveEvents + neutralEvents + negativeEvents;
  return freezeObject({ formula: PHASE_2_FORMULAS.thoughtAggregation.name, version: THOUGHT_AGGREGATION_FORMULA_VERSION,
    totalEvents, positiveEvents, neutralEvents, negativeEvents, byReason: freezeArray(aggregates),
    reconciled: reasonTotal === totalEvents && sentimentTotal === totalEvents
      && aggregates.every((aggregate) => aggregate.count === aggregate.positiveCount + aggregate.neutralCount + aggregate.negativeCount) });
}

export const aggregateThoughts = aggregateThoughtsByReason;
