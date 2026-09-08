import {
  ABILITY_GAP_MU_INTERCEPT,
  DEFAULT_ABILITY_GAP_COEFFICIENTS,
  DEFAULT_DEMAND_ELASTICITY,
  DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG,
  DEFAULT_SOFTMAX_TEMPERATURE,
  ARRIVAL_WEIGHT_SCALE,
  type ArrivalDayType,
  type GuestArrivalCurveConfigV1,
} from './config.ts';
import type { AbilityGapCoefficientsV1 } from './config.ts';

export const PROBABILITY_CURVE_VERSION = 1 as const;
export const PROBABILITY_GOLDEN_FIXTURE_VERSION = 1 as const;

function finite(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new RangeError(`${label} must be finite`);
}

function positive(value: number, label: string): void {
  finite(value, label);
  if (value <= 0) throw new RangeError(`${label} must be positive`);
}

/** Clamp a finite value to an inclusive finite interval. */
export function clamp(value: number, minimum: number, maximum: number): number {
  finite(value, 'value');
  finite(minimum, 'minimum');
  finite(maximum, 'maximum');
  if (maximum < minimum) throw new RangeError('maximum must be >= minimum');
  return Math.min(maximum, Math.max(minimum, value));
}

/** Numerically stable logistic sigmoid, including very large magnitudes. */
export function sigmoid(value: number): number {
  finite(value, 'sigmoid input');
  if (value >= 0) {
    const inverse = Math.exp(-value);
    return 1 / (1 + inverse);
  }
  const positive = Math.exp(value);
  return positive / (1 + positive);
}

export interface AbilityGapParameters {
  readonly mu: number;
  readonly sigma: number;
}

export const DEFAULT_ABILITY_GAP_CONFIG: AbilityGapCoefficientsV1 = DEFAULT_ABILITY_GAP_COEFFICIENTS;

export function abilityGapParameters(risk: number, coefficients: AbilityGapCoefficientsV1 = DEFAULT_ABILITY_GAP_COEFFICIENTS): AbilityGapParameters {
  const boundedRisk = clamp(risk, 0, 1);
  const mu = coefficients.muIntercept + coefficients.muRiskSlope * boundedRisk;
  const sigma = coefficients.sigmaIntercept + coefficients.sigmaRiskSlope * boundedRisk;
  positive(sigma, 'ability-gap sigma');
  return { mu, sigma };
}

/**
 * Provisional ability-gap kernel. D-A is supplied as `delta`; risk is
 * normalized to [0, 1]. The score is exp(-0.5 * ((delta - mu) / sigma)^2).
 */
export function abilityGapKernel(delta: number, risk: number, coefficients: AbilityGapCoefficientsV1 = DEFAULT_ABILITY_GAP_COEFFICIENTS): number {
  finite(delta, 'ability-gap delta');
  const { mu, sigma } = abilityGapParameters(risk, coefficients);
  const standardized = (delta - mu) / sigma;
  return Math.exp(-0.5 * standardized * standardized);
}

/** Calculate the same kernel from desired (D) and actual (A) ability values. */
export function abilityGapKernelFromAbilities(desiredAbility: number, actualAbility: number, risk: number,
  coefficients: AbilityGapCoefficientsV1 = DEFAULT_ABILITY_GAP_COEFFICIENTS): number {
  finite(desiredAbility, 'desired ability');
  finite(actualAbility, 'actual ability');
  return abilityGapKernel(desiredAbility - actualAbility, risk, coefficients);
}

export const abilityGapScore = abilityGapKernel;
export const abilityGapProbability = abilityGapKernelFromAbilities;

function validateLogits(logits: readonly number[]): void {
  for (const logit of logits) {
    if (Number.isNaN(logit)) throw new RangeError('softmax logits cannot contain NaN');
    if (logit !== Number.POSITIVE_INFINITY && logit !== Number.NEGATIVE_INFINITY) finite(logit, 'softmax logit');
  }
}

/** Stable softmax. +Infinity logits share mass uniformly; all -Infinity logits do too. */
export function stableSoftmax(logits: readonly number[], temperature = DEFAULT_SOFTMAX_TEMPERATURE): number[] {
  positive(temperature, 'softmax temperature');
  validateLogits(logits);
  if (logits.length === 0) return [];
  const positiveInfinity = logits.reduce<number[]>((indices, value, index) => {
    if (value === Number.POSITIVE_INFINITY) indices.push(index);
    return indices;
  }, []);
  if (positiveInfinity.length > 0) {
    const result = Array<number>(logits.length).fill(0);
    const share = 1 / positiveInfinity.length;
    for (const index of positiveInfinity) result[index] = share;
    return result;
  }
  let maximum = Number.NEGATIVE_INFINITY;
  for (const logit of logits) if (logit > maximum) maximum = logit;
  if (maximum === Number.NEGATIVE_INFINITY) return Array<number>(logits.length).fill(1 / logits.length);
  const weights = logits.map((logit) => logit === Number.NEGATIVE_INFINITY ? 0 : Math.exp((logit - maximum) / temperature));
  const total = weights.reduce((sum, weight) => sum + weight, 0);
  if (!Number.isFinite(total) || total <= 0) return Array<number>(logits.length).fill(1 / logits.length);
  const probabilities = weights.map((weight) => weight / total);
  // Apply the rounding residual to the largest bucket. Correcting the final
  // bucket can make a legitimate near-zero probability negative.
  const sum = probabilities.reduce((running, probability) => running + probability, 0);
  let largestIndex = 0;
  for (let index = 1; index < probabilities.length; index += 1) {
    if (probabilities[index] > probabilities[largestIndex]) largestIndex = index;
  }
  probabilities[largestIndex] += 1 - sum;
  return probabilities;
}

function validateUniform(uniform: number): void {
  finite(uniform, 'uniform draw');
  if (uniform < 0 || uniform >= 1) throw new RangeError('uniform draw must be in [0, 1)');
}

function sampleIndex(probabilities: readonly number[], uniform: number): number {
  validateUniform(uniform);
  if (probabilities.length === 0) throw new RangeError('cannot sample an empty distribution');
  let total = 0;
  for (const probability of probabilities) {
    finite(probability, 'probability');
    if (probability < 0) throw new RangeError('probabilities cannot be negative');
    total += probability;
  }
  if (!Number.isFinite(total) || total <= 0) throw new RangeError('probabilities must have a positive finite sum');
  const target = uniform * total;
  let cumulative = 0;
  for (let index = 0; index < probabilities.length; index += 1) {
    cumulative += probabilities[index];
    if (target < cumulative || index === probabilities.length - 1) return index;
  }
  return probabilities.length - 1;
}

/** Deterministically sample a probability vector using one injected U[0,1) draw. */
export function sampleCumulative(probabilities: readonly number[], uniform: number): number;
export function sampleCumulative<T>(items: readonly T[], probabilities: readonly number[], uniform: number): T;
export function sampleCumulative<T>(first: readonly number[] | readonly T[], second: number | readonly number[], third?: number): number | T {
  if (third === undefined) return sampleIndex(first as readonly number[], second as number);
  const items = first as readonly T[];
  const probabilities = second as readonly number[];
  if (items.length !== probabilities.length) throw new RangeError('items and probabilities must have equal length');
  return items[sampleIndex(probabilities, third)]!;
}

export function cumulativeDistribution(probabilities: readonly number[]): number[] {
  if (probabilities.length === 0) return [];
  let total = 0;
  for (const probability of probabilities) {
    finite(probability, 'probability');
    if (probability < 0) throw new RangeError('probabilities cannot be negative');
    total += probability;
  }
  if (!Number.isFinite(total) || total <= 0) throw new RangeError('probabilities must have a positive finite sum');
  const result: number[] = [];
  let cumulative = 0;
  for (const probability of probabilities) {
    cumulative += probability / total;
    result.push(cumulative);
  }
  result[result.length - 1] = 1;
  return result;
}

export function sampleSoftmaxIndex(logits: readonly number[], uniform: number, temperature = DEFAULT_SOFTMAX_TEMPERATURE): number {
  return sampleIndex(stableSoftmax(logits, temperature), uniform);
}

export function sampleSoftmax<T>(items: readonly T[], logits: readonly number[], uniform: number,
  temperature = DEFAULT_SOFTMAX_TEMPERATURE): T {
  if (items.length !== logits.length) throw new RangeError('items and logits must have equal length');
  return items[sampleSoftmaxIndex(logits, uniform, temperature)]!;
}

export const softmax = stableSoftmax;
export const sampleFromCumulative = sampleCumulative;

export function validateDemandElasticity(elasticity: number): number {
  finite(elasticity, 'demand elasticity');
  if (elasticity >= 0) throw new RangeError('demand elasticity must be negative');
  return elasticity;
}

export function logConstantElasticityDemandMultiplier(price: number, referencePrice: number,
  elasticity = DEFAULT_DEMAND_ELASTICITY): number {
  positive(price, 'price');
  positive(referencePrice, 'reference price');
  validateDemandElasticity(elasticity);
  return elasticity * Math.log(price / referencePrice);
}

/** Constant-elasticity demand: exp(elasticity * ln(price / referencePrice)). */
export function constantElasticityDemandMultiplier(price: number, referencePrice: number,
  elasticity = DEFAULT_DEMAND_ELASTICITY): number {
  const logMultiplier = logConstantElasticityDemandMultiplier(price, referencePrice, elasticity);
  // Keep an extreme but valid input finite; ordinary values remain exact exp(log).
  if (logMultiplier >= Math.log(Number.MAX_VALUE)) return Number.MAX_VALUE;
  if (logMultiplier <= Math.log(Number.MIN_VALUE)) return 0;
  return Math.exp(logMultiplier);
}

export const demandMultiplier = constantElasticityDemandMultiplier;

/** Standard normal CDF approximation with bounded tails and no exp overflow. */
export function standardNormalCdf(value: number): number {
  finite(value, 'normal CDF input');
  if (value <= -8) return 0;
  if (value >= 8) return 1;
  const absolute = Math.abs(value);
  const t = 1 / (1 + 0.2316419 * absolute);
  const polynomial = t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
  const density = 0.3989422804014327 * Math.exp(-0.5 * absolute * absolute);
  const upperTail = density * polynomial;
  return value >= 0 ? 1 - upperTail : upperTail;
}

export const ARRIVAL_UINT24_SCALE = ARRIVAL_WEIGHT_SCALE;
export const ARRIVAL_UINT24_MAX_DRAW = ARRIVAL_UINT24_SCALE - 1;

export interface ArrivalBinWeightTableV1 {
  readonly version: typeof PROBABILITY_CURVE_VERSION;
  readonly dayType: ArrivalDayType;
  readonly horizonSeconds: number;
  readonly binSeconds: number;
  readonly shiftSeconds: number;
  readonly medianSeconds: number;
  readonly mu: number;
  readonly sigma: number;
  readonly backgroundMix: number;
  readonly weights: Uint32Array;
  /** Monotone half-open cumulative thresholds; final value is 2^24. */
  readonly cdfThresholds: Uint32Array;
  readonly totalWeight: number;
  readonly binCount: number;
  /** Detects stale, truncated, or mutated persisted tuning tables. */
  readonly checksum: string;
}

export interface CanonicalArrivalGoldenVectorV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly dayType: ArrivalDayType;
  readonly seconds: number;
  readonly expectedCdf: number;
}

function validateCanonicalArrivalConfig(config: GuestArrivalCurveConfigV1): void {
  if (config.version !== 1) throw new RangeError('Unsupported canonical arrival curve version');
  if (!Number.isSafeInteger(config.horizonSeconds) || config.horizonSeconds <= 0) throw new RangeError('arrival horizon must be a positive integer');
  if (!Number.isSafeInteger(config.binSeconds) || config.binSeconds <= 0) throw new RangeError('arrival bin size must be a positive integer');
  if (config.horizonSeconds % config.binSeconds !== 0) throw new RangeError('arrival horizon must be divisible by bin size');
  if (!Number.isSafeInteger(config.shiftSeconds) || config.shiftSeconds <= 0) throw new RangeError('arrival shift must be a positive integer');
  if (config.shiftSeconds >= config.horizonSeconds) throw new RangeError('arrival shift must be less than horizon');
  const profiles: readonly (readonly [ArrivalDayType, GuestArrivalCurveConfigV1['weekday']])[] = [
    ['weekday', config.weekday], ['weekend', config.weekend],
  ];
  for (const [name, profile] of profiles) {
    if (!Number.isSafeInteger(profile.medianSeconds) || profile.medianSeconds <= 0) throw new RangeError(`${name} arrival median must be a positive integer`);
    positive(profile.sigma, `${name} arrival sigma`);
    if (!Number.isFinite(profile.backgroundMix) || profile.backgroundMix < 0 || profile.backgroundMix > 1) throw new RangeError(`${name} background mix must be within [0, 1]`);
  }
}

function canonicalArrivalProfile(dayType: ArrivalDayType, config: GuestArrivalCurveConfigV1): GuestArrivalCurveConfigV1['weekday'] {
  return dayType === 'weekday' ? config.weekday : config.weekend;
}

function canonicalArrivalMu(dayType: ArrivalDayType, config: GuestArrivalCurveConfigV1): number {
  const profile = canonicalArrivalProfile(dayType, config);
  // The specified t-median is exp(mu) - d for this shifted density.
  return Math.log(profile.medianSeconds + config.shiftSeconds);
}

/** The documented shifted-lognormal density f(t), before the background mix. */
export function shiftedLognormalArrivalPdf(seconds: number, dayType: ArrivalDayType = 'weekday',
  config: GuestArrivalCurveConfigV1 = DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG): number {
  validateCanonicalArrivalConfig(config);
  finite(seconds, 'arrival seconds');
  const profile = canonicalArrivalProfile(dayType, config);
  if (seconds < 0 || seconds > config.horizonSeconds) return 0;
  const mu = canonicalArrivalMu(dayType, config);
  const shifted = seconds + config.shiftSeconds;
  const z = (Math.log(shifted) - mu) / profile.sigma;
  const coefficient = 1 / (shifted * profile.sigma * Math.sqrt(2 * Math.PI));
  const exponent = -0.5 * z * z;
  return coefficient * Math.exp(exponent);
}

/** CDF of f(t) from the start of the documented [0, H] interval. */
export function shiftedLognormalArrivalIntervalCdf(seconds: number, dayType: ArrivalDayType = 'weekday',
  config: GuestArrivalCurveConfigV1 = DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG): number {
  validateCanonicalArrivalConfig(config);
  finite(seconds, 'arrival seconds');
  if (seconds <= 0) return 0;
  if (seconds > config.horizonSeconds) seconds = config.horizonSeconds;
  const profile = canonicalArrivalProfile(dayType, config);
  const mu = canonicalArrivalMu(dayType, config);
  const lower = (Math.log(config.shiftSeconds) - mu) / profile.sigma;
  const upper = (Math.log(seconds + config.shiftSeconds) - mu) / profile.sigma;
  return clamp(standardNormalCdf(upper) - standardNormalCdf(lower), 0, 1);
}

/**
 * CDF of the canonical mixed curve on [0, H]. The denominator accounts for
 * the small amount of shifted-lognormal mass beyond H because the contract
 * defines bin probabilities as proportional to the mixed terms.
 */
export function canonicalArrivalCdf(seconds: number, dayType: ArrivalDayType = 'weekday',
  config: GuestArrivalCurveConfigV1 = DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG): number {
  validateCanonicalArrivalConfig(config);
  finite(seconds, 'arrival seconds');
  if (seconds <= 0) return 0;
  if (seconds >= config.horizonSeconds) return 1;
  const profile = canonicalArrivalProfile(dayType, config);
  const lognormalMass = shiftedLognormalArrivalIntervalCdf(config.horizonSeconds, dayType, config);
  const partialMass = shiftedLognormalArrivalIntervalCdf(seconds, dayType, config);
  const denominator = (1 - profile.backgroundMix) * lognormalMass + profile.backgroundMix;
  return clamp(((1 - profile.backgroundMix) * partialMass + profile.backgroundMix * seconds / config.horizonSeconds) / denominator, 0, 1);
}

function canonicalBinProbabilities(dayType: ArrivalDayType, config: GuestArrivalCurveConfigV1): number[] {
  validateCanonicalArrivalConfig(config);
  const profile = canonicalArrivalProfile(dayType, config);
  const binCount = config.horizonSeconds / config.binSeconds;
  const uniform = 1 / binCount;
  const probabilities: number[] = [];
  for (let index = 0; index < binCount; index += 1) {
    const start = index * config.binSeconds;
    const end = start + config.binSeconds;
    const integral = shiftedLognormalArrivalIntervalCdf(end, dayType, config)
      - shiftedLognormalArrivalIntervalCdf(start, dayType, config);
    probabilities.push((1 - profile.backgroundMix) * integral + profile.backgroundMix * uniform);
  }
  const total = probabilities.reduce((sum, value) => sum + value, 0);
  if (!(total > 0) || !Number.isFinite(total)) throw new RangeError('arrival curve has no finite probability mass');
  return probabilities.map((value) => value / total);
}

function largestRemainderAllocation(probabilities: readonly number[], total: number): number[] {
  if (!Number.isSafeInteger(total) || total < 0) throw new RangeError('allocation total must be a non-negative safe integer');
  const allocation = probabilities.map((probability) => Math.floor(probability * total));
  let assigned = allocation.reduce((sum, value) => sum + value, 0);
  const remainder = total - assigned;
  const ranked = probabilities.map((probability, index) => ({ index, fractional: probability * total - allocation[index] }))
    .sort((left, right) => right.fractional - left.fractional || left.index - right.index);
  for (let index = 0; index < remainder; index += 1) allocation[ranked[index % ranked.length]!.index] += 1;
  assigned = allocation.reduce((sum, value) => sum + value, 0);
  if (assigned !== total) throw new RangeError('arrival allocation did not conserve total');
  return allocation;
}

function arrivalTableChecksum(table: Omit<ArrivalBinWeightTableV1, 'checksum' | 'cdfThresholds'>): string {
  let hash = 2_166_136_261;
  const input = [table.version, table.dayType, table.horizonSeconds, table.binSeconds, table.shiftSeconds,
    table.medianSeconds, table.mu, table.sigma, table.backgroundMix, table.totalWeight, table.binCount,
    ...table.weights].join('|');
  for (let index = 0; index < input.length; index += 1) {
    hash ^= input.charCodeAt(index);
    hash = Math.imul(hash, 16_777_619) >>> 0;
  }
  return `arrival-v1-${hash.toString(16).padStart(8, '0')}`;
}

/** Validate every persisted table invariant before sampling or allocation. */
export function validateArrivalBinWeightTable(table: ArrivalBinWeightTableV1): void {
  if (table.version !== PROBABILITY_CURVE_VERSION) throw new RangeError('Unsupported arrival table version');
  if (table.dayType !== 'weekday' && table.dayType !== 'weekend') throw new RangeError('arrival table has invalid day type');
  for (const [label, value] of [['horizon', table.horizonSeconds], ['bin size', table.binSeconds],
    ['shift', table.shiftSeconds], ['median', table.medianSeconds], ['bin count', table.binCount]] as const) {
    if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`arrival table ${label} must be a positive integer`);
  }
  positive(table.sigma, 'arrival table sigma');
  finite(table.mu, 'arrival table mu');
  if (!Number.isFinite(table.backgroundMix) || table.backgroundMix < 0 || table.backgroundMix > 1) {
    throw new RangeError('arrival table background mix must be within [0, 1]');
  }
  if (table.horizonSeconds % table.binSeconds !== 0 || table.binCount !== table.horizonSeconds / table.binSeconds) {
    throw new RangeError('arrival table dimensions are inconsistent');
  }
  if (table.totalWeight !== ARRIVAL_UINT24_SCALE || table.weights.length !== table.binCount
    || table.cdfThresholds.length !== table.binCount) throw new RangeError('arrival table columns are inconsistent');
  let cumulative = 0;
  for (let index = 0; index < table.binCount; index += 1) {
    const weight = table.weights[index]!;
    if (!Number.isSafeInteger(weight) || weight < 0) throw new RangeError('arrival table weights must be unsigned integers');
    cumulative += weight;
    if (table.cdfThresholds[index] !== cumulative) throw new RangeError('arrival table CDF does not match its weights');
  }
  if (cumulative !== ARRIVAL_UINT24_SCALE) throw new RangeError('arrival table weights do not conserve uint24 mass');
  if (table.checksum !== arrivalTableChecksum(table)) throw new RangeError('arrival table checksum mismatch');
}

/** Build exact uint32 weights using deterministic largest-remainder rounding. */
export function buildArrivalBinWeightTable(dayType: ArrivalDayType = 'weekday',
  config: GuestArrivalCurveConfigV1 = DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG): ArrivalBinWeightTableV1 {
  validateCanonicalArrivalConfig(config);
  const profile = canonicalArrivalProfile(dayType, config);
  const probabilities = canonicalBinProbabilities(dayType, config);
  const weights = Uint32Array.from(largestRemainderAllocation(probabilities, ARRIVAL_UINT24_SCALE));
  const cdfThresholds = new Uint32Array(weights.length);
  let cumulative = 0;
  for (let index = 0; index < weights.length; index += 1) {
    cumulative += weights[index]!;
    cdfThresholds[index] = cumulative;
  }
  cdfThresholds[cdfThresholds.length - 1] = ARRIVAL_UINT24_SCALE;
  const base = { version: PROBABILITY_CURVE_VERSION, dayType, horizonSeconds: config.horizonSeconds,
    binSeconds: config.binSeconds, shiftSeconds: config.shiftSeconds, medianSeconds: profile.medianSeconds,
    mu: canonicalArrivalMu(dayType, config), sigma: profile.sigma, backgroundMix: profile.backgroundMix,
    weights, totalWeight: ARRIVAL_UINT24_SCALE, binCount: weights.length };
  const table = Object.freeze({ ...base, cdfThresholds, checksum: arrivalTableChecksum(base) });
  validateArrivalBinWeightTable(table);
  return table;
}

export const buildArrivalProbabilityTable = buildArrivalBinWeightTable;
export const buildArrivalWeightTable = buildArrivalBinWeightTable;

/** Versioned canonical arrival points at boundaries and representative medians. */
export const CANONICAL_ARRIVAL_GOLDEN_FIXTURES: readonly CanonicalArrivalGoldenVectorV1[] = Object.freeze([
  { version: 1 as const, dayType: 'weekday' as const, seconds: 0, expectedCdf: 0 },
  { version: 1 as const, dayType: 'weekday' as const, seconds: 4_800, expectedCdf: 0.42184726365593295 },
  { version: 1 as const, dayType: 'weekday' as const, seconds: 43_200, expectedCdf: 1 },
  { version: 1 as const, dayType: 'weekend' as const, seconds: 0, expectedCdf: 0 },
  { version: 1 as const, dayType: 'weekend' as const, seconds: 3_300, expectedCdf: 0.46610649808752563 },
  { version: 1 as const, dayType: 'weekend' as const, seconds: 43_200, expectedCdf: 1 },
]);

export const ARRIVAL_CURVE_GOLDEN_FIXTURES = CANONICAL_ARRIVAL_GOLDEN_FIXTURES;

/** Binary-search a table using an injected keyed 24-bit integer draw. */
export function sampleArrivalBin(table: ArrivalBinWeightTableV1, draw: number): number {
  if (!Number.isSafeInteger(draw) || draw < 0 || draw >= ARRIVAL_UINT24_SCALE) throw new RangeError('arrival draw must be a 24-bit unsigned integer');
  validateArrivalBinWeightTable(table);
  let lower = 0;
  let upper = table.cdfThresholds.length - 1;
  while (lower < upper) {
    const middle = Math.floor((lower + upper) / 2);
    if (draw < table.cdfThresholds[middle]!) upper = middle;
    else lower = middle + 1;
  }
  return lower;
}

export const sampleArrivalBinFromDraw = sampleArrivalBin;
export const sampleArrival = sampleArrivalBin;

/** Allocate a realized daily total exactly across the table's integer bins. */
export function allocateDailyGuestTotal(dailyTotal: number, table: ArrivalBinWeightTableV1): readonly number[];
export function allocateDailyGuestTotal(dailyTotal: number, dayType: ArrivalDayType, config?: GuestArrivalCurveConfigV1): readonly number[];
export function allocateDailyGuestTotal(dailyTotal: number, tableOrDayType: ArrivalBinWeightTableV1 | ArrivalDayType,
  config?: GuestArrivalCurveConfigV1): readonly number[] {
  if (!Number.isSafeInteger(dailyTotal) || dailyTotal < 0) throw new RangeError('daily guest total must be a non-negative safe integer');
  const table = typeof tableOrDayType === 'string' ? buildArrivalBinWeightTable(tableOrDayType, config ?? DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG) : tableOrDayType;
  validateArrivalBinWeightTable(table);
  return Object.freeze(largestRemainderAllocation(Array.from(table.weights, (weight) => weight / ARRIVAL_UINT24_SCALE), dailyTotal));
}

export const allocateGuestTotalAcrossBins = allocateDailyGuestTotal;
export const allocateDailyGuestArrivals = allocateDailyGuestTotal;
export const allocateDailyGuests = allocateDailyGuestTotal;

export function buildArrivalTables(config: GuestArrivalCurveConfigV1 = DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG): Readonly<{
  readonly weekday: ArrivalBinWeightTableV1;
  readonly weekend: ArrivalBinWeightTableV1;
}> {
  return Object.freeze({ weekday: buildArrivalBinWeightTable('weekday', config), weekend: buildArrivalBinWeightTable('weekend', config) });
}

export const buildArrivalCdfThresholdTable = buildArrivalBinWeightTable;
export const sampleArrivalBinFrom24Bit = sampleArrivalBin;

export const buildArrivalCdfTable = buildArrivalBinWeightTable;

export interface AbilityGapGoldenVectorV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly delta: number;
  readonly risk: number;
  readonly expected: number;
}

export interface SigmoidGoldenVectorV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly input: number;
  readonly expected: number;
}

export interface SoftmaxGoldenVectorV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly logits: readonly number[];
  readonly temperature: number;
  readonly expected: readonly number[];
}

export interface DemandGoldenVectorV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly price: number;
  readonly referencePrice: number;
  readonly elasticity: number;
  readonly expected: number;
}

export interface ProbabilityGoldenFixturesV1 {
  readonly version: typeof PROBABILITY_GOLDEN_FIXTURE_VERSION;
  readonly sigmoid: readonly SigmoidGoldenVectorV1[];
  readonly abilityGap: readonly AbilityGapGoldenVectorV1[];
  readonly softmax: readonly SoftmaxGoldenVectorV1[];
  readonly demand: readonly DemandGoldenVectorV1[];
}

/** Versioned golden points; coefficients are provisional game tuning. */
export const PROBABILITY_GOLDEN_FIXTURES: ProbabilityGoldenFixturesV1 = Object.freeze({
  version: PROBABILITY_GOLDEN_FIXTURE_VERSION,
  sigmoid: Object.freeze([
    { version: 1 as const, input: -40, expected: 4.248354255291589e-18 },
    { version: 1 as const, input: 0, expected: 0.5 },
    { version: 1 as const, input: 40, expected: 1 },
  ]),
  abilityGap: Object.freeze([
    { version: 1 as const, delta: ABILITY_GAP_MU_INTERCEPT, risk: 0, expected: 1 },
    { version: 1 as const, delta: 0, risk: 0, expected: 0.7827045382418681 },
    { version: 1 as const, delta: 0.45, risk: 1, expected: 1 },
    { version: 1 as const, delta: 1, risk: 0.5, expected: 0.42379654585632254 },
  ]),
  softmax: Object.freeze([
    { version: 1 as const, logits: Object.freeze([-1, 0, 1]), temperature: DEFAULT_SOFTMAX_TEMPERATURE,
      expected: Object.freeze([2.061060046209062e-9, 0.000045397868608866656, 0.9999546000703311]) },
    { version: 1 as const, logits: Object.freeze([0, 0, 0]), temperature: DEFAULT_SOFTMAX_TEMPERATURE,
      expected: Object.freeze([1 / 3, 1 / 3, 1 / 3]) },
  ]),
  demand: Object.freeze([
    { version: 1 as const, price: 100, referencePrice: 100, elasticity: DEFAULT_DEMAND_ELASTICITY, expected: 1 },
    { version: 1 as const, price: 50, referencePrice: 100, elasticity: DEFAULT_DEMAND_ELASTICITY, expected: 2.1435469250725863 },
    { version: 1 as const, price: 200, referencePrice: 100, elasticity: DEFAULT_DEMAND_ELASTICITY, expected: 0.4665164957684037 },
  ]),
});

export const PROBABILITY_CURVE_GOLDEN_FIXTURES = PROBABILITY_GOLDEN_FIXTURES;
