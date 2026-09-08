/**
 * Phase 3 demand and arrivals.
 *
 * This module is deliberately separate from the roster and event engine.  It
 * produces a bounded, serialisable forecast first, then a reproducible
 * realisation of that forecast.  The engine can therefore use the forecast
 * for UI/planning while using the realisation as the authoritative input for
 * guest creation.  No function reads wall-clock time or consumes a shared
 * random stream.
 */

import type {
  DemandPlan,
  DemandWaveKind,
  SimulatedSecond,
} from './contracts.ts';
import type {
  ArrivalDayType,
  GuestArrivalCurveConfigV1,
} from './config.ts';
import {
  DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG,
  DEFAULT_DEMAND_ELASTICITY,
  isGuestArrivalCurveConfig,
} from './config.ts';
import {
  canonicalArrivalCdf,
  constantElasticityDemandMultiplier,
} from './probability.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import { keyedRandomFloat, type RandomSeed } from './random.ts';

export const PHASE3_DEMAND_VERSION = 1 as const;
export const DEMAND_TUNING_VERSION = 1 as const;

/** One operating window must remain cheap to project and easy to inspect. */
export const MAX_DEMAND_BUCKETS = 10_000;
export const MAX_BASE_POTENTIAL_GUESTS = 10_000_000;

export const DEFAULT_DEMAND_REPUTATION_REFERENCE = 0.60;
export const DEFAULT_DEMAND_REPUTATION_SENSITIVITY = 1.80;
export const DEFAULT_DEMAND_VALUE_REFERENCE = 0.50;
export const DEFAULT_DEMAND_VALUE_SENSITIVITY = 1.10;
export const DEFAULT_PARTY_MEAN_SIZE = 2.2;
export const DEFAULT_HEAVY_GROUP_RATE = 0.06;

export type DemandDayType = ArrivalDayType | 'holiday';

export interface DemandTuningV1 {
  readonly version: typeof DEMAND_TUNING_VERSION;
  readonly reputationReference: number;
  readonly reputationSensitivity: number;
  readonly valueReference: number;
  readonly valueSensitivity: number;
  readonly demandElasticity: number;
  readonly partyMeanSize: number;
  readonly heavyGroupRate: number;
}

export interface DemandTuningOverrides {
  readonly reputationReference?: number;
  readonly reputationSensitivity?: number;
  readonly valueReference?: number;
  readonly valueSensitivity?: number;
  readonly demandElasticity?: number;
  readonly partyMeanSize?: number;
  readonly heavyGroupRate?: number;
}

export const DEFAULT_DEMAND_TUNING: DemandTuningV1 = Object.freeze({
  version: DEMAND_TUNING_VERSION,
  reputationReference: DEFAULT_DEMAND_REPUTATION_REFERENCE,
  reputationSensitivity: DEFAULT_DEMAND_REPUTATION_SENSITIVITY,
  valueReference: DEFAULT_DEMAND_VALUE_REFERENCE,
  valueSensitivity: DEFAULT_DEMAND_VALUE_SENSITIVITY,
  demandElasticity: DEFAULT_DEMAND_ELASTICITY,
  partyMeanSize: DEFAULT_PARTY_MEAN_SIZE,
  heavyGroupRate: DEFAULT_HEAVY_GROUP_RATE,
});

/**
 * Inputs to one deterministic operating-window demand projection.
 *
 * `basePotentialGuests` is the expected market demand for this window at the
 * reference reputation/value/price.  It is intentionally not an occupancy
 * count.  `availableCapacityGuests` and `maxGuests` are admission limits.
 */
export interface DemandScenarioInputV1 {
  readonly version?: typeof PHASE3_DEMAND_VERSION;
  readonly seed: RandomSeed;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  readonly bucketSeconds: number;
  readonly dayType: DemandDayType;
  readonly basePotentialGuests: number;
  readonly ticketPriceCents: number;
  readonly referencePriceCents: number;
  readonly reputation: number;
  readonly resortValue: number;
  /** Product/conditions multiplier; 1 is reference quality, 0 is closed. */
  readonly operatingFraction?: number;
  readonly conditionFactor?: number;
  readonly availableCapacityGuests: number;
  readonly maxGuests: number;
  readonly maxParties: number;
  readonly tuning?: DemandTuningOverrides;
  readonly arrivalCurve?: GuestArrivalCurveConfigV1;
}

export interface DemandScenarioV1 {
  readonly version: typeof PHASE3_DEMAND_VERSION;
  readonly seed: string;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  readonly bucketSeconds: number;
  readonly dayType: DemandDayType;
  readonly basePotentialGuests: number;
  readonly ticketPriceCents: number;
  readonly referencePriceCents: number;
  readonly reputation: number;
  readonly resortValue: number;
  readonly operatingFraction: number;
  readonly conditionFactor: number;
  readonly availableCapacityGuests: number;
  readonly maxGuests: number;
  readonly maxParties: number;
  readonly tuning: DemandTuningV1;
  readonly arrivalCurve: GuestArrivalCurveConfigV1;
}

export interface DemandMultipliersV1 {
  readonly reputation: number;
  readonly value: number;
  readonly price: number;
  readonly operating: number;
  readonly conditions: number;
  readonly combined: number;
}

export interface DemandForecastBucketV1 {
  readonly index: number;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  /** Normalised arrival-curve mass assigned to this bucket. */
  readonly shapeWeight: number;
  readonly marketExpectedGuests: number;
  readonly admittedExpectedGuests: number;
}

export interface DemandForecastV1 {
  readonly version: typeof PHASE3_DEMAND_VERSION;
  readonly scenario: DemandScenarioV1;
  readonly multipliers: DemandMultipliersV1;
  /** Demand before the maxGuests and capacity gates. */
  readonly uncappedExpectedGuests: number;
  /** Demand after the maxGuests gate and before resort capacity. */
  readonly marketExpectedGuests: number;
  /** Expected guests admitted after capacity and operating gates. */
  readonly admittedExpectedGuests: number;
  readonly guestLimitFactor: number;
  readonly capacityAdmissionFactor: number;
  readonly buckets: readonly DemandForecastBucketV1[];
  readonly checksum: string;
}

export interface RealizedArrivalBucketV1 {
  readonly index: number;
  readonly startTick: SimulatedSecond;
  readonly endTick: SimulatedSecond;
  readonly marketExpectedGuests: number;
  readonly admittedExpectedGuests: number;
  readonly guestCount: number;
  readonly partyCount: number;
  readonly heavyGroupCount: number;
}

export interface DemandRealizationV1 {
  readonly version: typeof PHASE3_DEMAND_VERSION;
  readonly forecastChecksum: string;
  readonly seed: string;
  readonly guestCount: number;
  readonly partyCount: number;
  readonly heavyGroupCount: number;
  readonly buckets: readonly RealizedArrivalBucketV1[];
  readonly checksum: string;
}

function finite(value: number, label: string): void {
  if (!Number.isFinite(value)) throw new RangeError(`${label} must be finite`);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isSafeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value);
}

function positive(value: number, label: string): void {
  finite(value, label);
  if (value <= 0) throw new RangeError(`${label} must be positive`);
}

function nonNegative(value: number, label: string): void {
  finite(value, label);
  if (value < 0) throw new RangeError(`${label} must be non-negative`);
}

function safeInteger(value: number, label: string): void {
  if (!Number.isSafeInteger(value)) throw new RangeError(`${label} must be a safe integer`);
}

function positiveInteger(value: number, label: string): void {
  safeInteger(value, label);
  if (value <= 0) throw new RangeError(`${label} must be a positive integer`);
}

function unit(value: number, label: string): void {
  finite(value, label);
  if (value < 0 || value > 1) throw new RangeError(`${label} must be within [0, 1]`);
}

function clamp(value: number, minimum: number, maximum: number): number {
  return Math.min(maximum, Math.max(minimum, value));
}

function seedString(seed: RandomSeed): string {
  if (typeof seed === 'string') {
    if (seed.length === 0) throw new RangeError('demand seed must be non-empty');
    return seed;
  }
  if (typeof seed === 'number') {
    if (!Number.isSafeInteger(seed)) throw new RangeError('numeric demand seed must be a safe integer');
    return String(seed);
  }
  return `${seed}n`;
}

function normalizedDayType(dayType: DemandDayType): ArrivalDayType {
  return dayType === 'weekday' ? 'weekday' : 'weekend';
}

function freezeArray<T>(values: readonly T[]): readonly T[] {
  return Object.freeze([...values]);
}

function freezeScenario(scenario: DemandScenarioV1): DemandScenarioV1 {
  return Object.freeze({ ...scenario,
    tuning: Object.freeze({ ...scenario.tuning }),
    arrivalCurve: Object.freeze({ ...scenario.arrivalCurve,
      weekday: Object.freeze({ ...scenario.arrivalCurve.weekday }),
      weekend: Object.freeze({ ...scenario.arrivalCurve.weekend }) }) });
}

function validateTuning(tuning: DemandTuningV1): void {
  if (tuning.version !== DEMAND_TUNING_VERSION) throw new RangeError('unsupported demand tuning version');
  unit(tuning.reputationReference, 'reputation reference');
  nonNegative(tuning.reputationSensitivity, 'reputation sensitivity');
  unit(tuning.valueReference, 'value reference');
  nonNegative(tuning.valueSensitivity, 'value sensitivity');
  finite(tuning.demandElasticity, 'demand elasticity');
  if (tuning.demandElasticity >= 0) throw new RangeError('demand elasticity must be negative');
  positive(tuning.partyMeanSize, 'party mean size');
  if (tuning.partyMeanSize > 100) throw new RangeError('party mean size is unreasonably large');
  unit(tuning.heavyGroupRate, 'heavy group rate');
}

export function isDemandScenario(value: unknown): value is DemandScenarioV1 {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const scenario = value as Partial<DemandScenarioV1>;
  try {
    const { version, seed, startTick, endTick, bucketSeconds, dayType, basePotentialGuests,
      ticketPriceCents, referencePriceCents, reputation, resortValue, operatingFraction,
      conditionFactor, availableCapacityGuests, maxGuests, maxParties, tuning, arrivalCurve } = scenario;
    if (version !== PHASE3_DEMAND_VERSION || typeof seed !== 'string' || seed.length === 0
      || !isSafeInteger(startTick) || startTick < 0 || !isSafeInteger(endTick)
      || endTick <= startTick || !isSafeInteger(bucketSeconds)
      || bucketSeconds <= 0 || endTick - startTick > bucketSeconds * MAX_DEMAND_BUCKETS
      || (dayType !== 'weekday' && dayType !== 'weekend' && dayType !== 'holiday')
      || !isFiniteNumber(basePotentialGuests) || basePotentialGuests < 0 || basePotentialGuests > MAX_BASE_POTENTIAL_GUESTS
      || !isFiniteNumber(ticketPriceCents) || ticketPriceCents <= 0
      || !isFiniteNumber(referencePriceCents) || referencePriceCents <= 0
      || !isFiniteNumber(reputation) || reputation < 0 || reputation > 1
      || !isFiniteNumber(resortValue) || resortValue < 0 || resortValue > 1
      || !isFiniteNumber(operatingFraction) || operatingFraction < 0 || operatingFraction > 1
      || !isFiniteNumber(conditionFactor) || conditionFactor < 0 || conditionFactor > 1
      || !isSafeInteger(availableCapacityGuests) || availableCapacityGuests < 0
      || !isSafeInteger(maxGuests) || maxGuests <= 0
      || !isSafeInteger(maxParties) || maxParties <= 0
      || !tuning || !arrivalCurve || !isGuestArrivalCurveConfig(arrivalCurve)) return false;
    validateTuning(tuning);
    return true;
  } catch {
    return false;
  }
}

export function createDemandScenario(input: DemandScenarioInputV1): DemandScenarioV1 {
  if (input.version !== undefined && input.version !== PHASE3_DEMAND_VERSION) throw new RangeError('unsupported Phase 3 demand version');
  safeInteger(input.startTick, 'demand startTick');
  if (input.startTick < 0) throw new RangeError('demand startTick must be non-negative');
  safeInteger(input.endTick, 'demand endTick');
  if (input.endTick <= input.startTick) throw new RangeError('demand endTick must be after startTick');
  positiveInteger(input.bucketSeconds, 'demand bucketSeconds');
  const duration = input.endTick - input.startTick;
  if (duration % input.bucketSeconds !== 0) throw new RangeError('demand window must divide evenly into buckets');
  if (duration / input.bucketSeconds > MAX_DEMAND_BUCKETS) throw new RangeError(`demand window exceeds ${MAX_DEMAND_BUCKETS} buckets`);
  if (input.dayType !== 'weekday' && input.dayType !== 'weekend' && input.dayType !== 'holiday') throw new RangeError('invalid demand day type');
  nonNegative(input.basePotentialGuests, 'base potential guests');
  if (input.basePotentialGuests > MAX_BASE_POTENTIAL_GUESTS) throw new RangeError('base potential guests exceed bounded demand limit');
  positive(input.ticketPriceCents, 'ticket price');
  positive(input.referencePriceCents, 'reference price');
  unit(input.reputation, 'reputation');
  unit(input.resortValue, 'resort value');
  const operatingFraction = input.operatingFraction ?? 1;
  const conditionFactor = input.conditionFactor ?? 1;
  unit(operatingFraction, 'operating fraction');
  unit(conditionFactor, 'condition factor');
  safeInteger(input.availableCapacityGuests, 'available capacity guests');
  if (input.availableCapacityGuests < 0) throw new RangeError('available capacity guests must be non-negative');
  positiveInteger(input.maxGuests, 'max guests');
  positiveInteger(input.maxParties, 'max parties');
  const arrivalCurve = input.arrivalCurve ?? DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG;
  if (!isGuestArrivalCurveConfig(arrivalCurve)) throw new RangeError('invalid demand arrival curve');
  const tuning: DemandTuningV1 = { ...DEFAULT_DEMAND_TUNING, ...input.tuning, version: DEMAND_TUNING_VERSION };
  validateTuning(tuning);
  const scenario: DemandScenarioV1 = {
    version: PHASE3_DEMAND_VERSION,
    seed: seedString(input.seed),
    startTick: input.startTick,
    endTick: input.endTick,
    bucketSeconds: input.bucketSeconds,
    dayType: input.dayType,
    basePotentialGuests: input.basePotentialGuests,
    ticketPriceCents: input.ticketPriceCents,
    referencePriceCents: input.referencePriceCents,
    reputation: input.reputation,
    resortValue: input.resortValue,
    operatingFraction,
    conditionFactor,
    availableCapacityGuests: input.availableCapacityGuests,
    maxGuests: input.maxGuests,
    maxParties: input.maxParties,
    tuning,
    arrivalCurve,
  };
  return freezeScenario(scenario);
}

export function assertDemandScenario(value: unknown): asserts value is DemandScenarioV1 {
  if (!isDemandScenario(value)) throw new RangeError('invalid Phase 3 demand scenario');
}

/** exp(k * (reputation - reference)); bounded to prevent an input spike. */
export function reputationDemandMultiplier(reputation: number, tuning: DemandTuningV1 = DEFAULT_DEMAND_TUNING): number {
  unit(reputation, 'reputation');
  validateTuning(tuning);
  return clamp(Math.exp(tuning.reputationSensitivity * (reputation - tuning.reputationReference)), 0.05, 20);
}

/** exp(k * (value - reference)); value includes the resort's terrain/frills fit. */
export function valueDemandMultiplier(value: number, tuning: DemandTuningV1 = DEFAULT_DEMAND_TUNING): number {
  unit(value, 'resort value');
  validateTuning(tuning);
  return clamp(Math.exp(tuning.valueSensitivity * (value - tuning.valueReference)), 0.05, 20);
}

export function demandMultipliers(scenario: DemandScenarioV1): DemandMultipliersV1 {
  assertDemandScenario(scenario);
  const reputation = reputationDemandMultiplier(scenario.reputation, scenario.tuning);
  const value = valueDemandMultiplier(scenario.resortValue, scenario.tuning);
  const price = constantElasticityDemandMultiplier(scenario.ticketPriceCents, scenario.referencePriceCents, scenario.tuning.demandElasticity);
  const operating = scenario.operatingFraction;
  const conditions = scenario.conditionFactor;
  const combined = reputation * value * price * operating * conditions;
  return Object.freeze({ reputation, value, price, operating, conditions, combined });
}

function forecastProjection(forecast: Omit<DemandForecastV1, 'checksum'>): unknown {
  return {
    version: forecast.version,
    scenario: forecast.scenario,
    multipliers: forecast.multipliers,
    uncappedExpectedGuests: forecast.uncappedExpectedGuests,
    marketExpectedGuests: forecast.marketExpectedGuests,
    admittedExpectedGuests: forecast.admittedExpectedGuests,
    guestLimitFactor: forecast.guestLimitFactor,
    capacityAdmissionFactor: forecast.capacityAdmissionFactor,
    buckets: forecast.buckets,
  };
}

export function demandForecastChecksum(forecast: Omit<DemandForecastV1, 'checksum'> | DemandForecastV1): string {
  return eventCalendarChecksum(forecastProjection(forecast as Omit<DemandForecastV1, 'checksum'>));
}

export function buildDemandForecast(scenario: DemandScenarioV1): DemandForecastV1 {
  assertDemandScenario(scenario);
  const multipliers = demandMultipliers(scenario);
  const uncappedExpectedGuests = scenario.basePotentialGuests * multipliers.combined;
  if (!Number.isFinite(uncappedExpectedGuests)) throw new RangeError('demand forecast overflowed');
  const guestLimitFactor = uncappedExpectedGuests > 0 ? Math.min(1, scenario.maxGuests / uncappedExpectedGuests) : 1;
  const marketExpectedGuests = uncappedExpectedGuests * guestLimitFactor;
  const capacityAdmissionFactor = marketExpectedGuests > 0
    ? Math.min(1, scenario.availableCapacityGuests / marketExpectedGuests) : 1;
  const admittedExpectedGuests = marketExpectedGuests * capacityAdmissionFactor;
  const dayType = normalizedDayType(scenario.dayType);
  const bucketCount = (scenario.endTick - scenario.startTick) / scenario.bucketSeconds;
  const rawWeights: number[] = [];
  let rawTotal = 0;
  for (let index = 0; index < bucketCount; index += 1) {
    const fromSeconds = index * scenario.bucketSeconds;
    const toSeconds = fromSeconds + scenario.bucketSeconds;
    const weight = Math.max(0, canonicalArrivalCdf(toSeconds, dayType, scenario.arrivalCurve)
      - canonicalArrivalCdf(fromSeconds, dayType, scenario.arrivalCurve));
    rawWeights.push(weight);
    rawTotal += weight;
  }
  if (!(rawTotal > 0) || !Number.isFinite(rawTotal)) throw new RangeError('demand arrival curve has no mass in the window');
  const buckets = freezeArray(rawWeights.map((rawWeight, index) => {
    const shapeWeight = rawWeight / rawTotal;
    const startTick = scenario.startTick + index * scenario.bucketSeconds;
    const endTick = startTick + scenario.bucketSeconds;
    return Object.freeze({ index, startTick, endTick, shapeWeight,
      marketExpectedGuests: marketExpectedGuests * shapeWeight,
      admittedExpectedGuests: admittedExpectedGuests * shapeWeight });
  }));
  const forecast: Omit<DemandForecastV1, 'checksum'> = {
    version: PHASE3_DEMAND_VERSION, scenario, multipliers, uncappedExpectedGuests,
    marketExpectedGuests, admittedExpectedGuests, guestLimitFactor, capacityAdmissionFactor, buckets,
  };
  return Object.freeze({ ...forecast, checksum: demandForecastChecksum(forecast) });
}

export const forecastDemand = buildDemandForecast;

function validateForecast(forecast: DemandForecastV1): void {
  if (forecast.version !== PHASE3_DEMAND_VERSION || !isDemandScenario(forecast.scenario)
    || !forecast.multipliers || !isFiniteNumber(forecast.multipliers.reputation)
    || !isFiniteNumber(forecast.multipliers.value) || !isFiniteNumber(forecast.multipliers.price)
    || !isFiniteNumber(forecast.multipliers.operating) || !isFiniteNumber(forecast.multipliers.conditions)
    || !isFiniteNumber(forecast.multipliers.combined)) throw new RangeError('invalid demand forecast envelope');
  if (!Number.isFinite(forecast.uncappedExpectedGuests) || forecast.uncappedExpectedGuests < 0
    || !Number.isFinite(forecast.marketExpectedGuests) || forecast.marketExpectedGuests < 0
    || !Number.isFinite(forecast.admittedExpectedGuests) || forecast.admittedExpectedGuests < 0
    || !Number.isFinite(forecast.guestLimitFactor) || forecast.guestLimitFactor < 0 || forecast.guestLimitFactor > 1
    || !Number.isFinite(forecast.capacityAdmissionFactor) || forecast.capacityAdmissionFactor < 0 || forecast.capacityAdmissionFactor > 1
    || !Array.isArray(forecast.buckets) || forecast.buckets.length === 0 || forecast.buckets.length > MAX_DEMAND_BUCKETS
    || typeof forecast.checksum !== 'string' || forecast.checksum.length === 0) throw new RangeError('invalid demand forecast values');
  let shapeTotal = 0;
  let marketTotal = 0;
  let admittedTotal = 0;
  for (let index = 0; index < forecast.buckets.length; index += 1) {
    const bucket = forecast.buckets[index]!;
    if (bucket.index !== index || bucket.startTick !== forecast.scenario.startTick + index * forecast.scenario.bucketSeconds
      || bucket.endTick !== bucket.startTick + forecast.scenario.bucketSeconds || !Number.isFinite(bucket.shapeWeight)
      || bucket.shapeWeight < 0 || !Number.isFinite(bucket.marketExpectedGuests) || bucket.marketExpectedGuests < 0
      || !Number.isFinite(bucket.admittedExpectedGuests) || bucket.admittedExpectedGuests < 0) throw new RangeError('invalid demand forecast bucket');
    shapeTotal += bucket.shapeWeight;
    marketTotal += bucket.marketExpectedGuests;
    admittedTotal += bucket.admittedExpectedGuests;
  }
  if (Math.abs(shapeTotal - 1) > 1e-9 || Math.abs(marketTotal - forecast.marketExpectedGuests) > 1e-7
    || Math.abs(admittedTotal - forecast.admittedExpectedGuests) > 1e-7
    || demandForecastChecksum(forecast) !== forecast.checksum) throw new RangeError('demand forecast checksum or mass mismatch');
}

export function isDemandForecast(value: unknown): value is DemandForecastV1 {
  try {
    validateForecast(value as DemandForecastV1);
    return true;
  } catch {
    return false;
  }
}

export function validateDemandForecast(value: unknown): asserts value is DemandForecastV1 {
  validateForecast(value as DemandForecastV1);
}

export function assertDemandForecast(value: unknown): asserts value is DemandForecastV1 {
  validateForecast(value as DemandForecastV1);
}

function realizationProjection(realization: Omit<DemandRealizationV1, 'checksum'>): unknown {
  return { version: realization.version, forecastChecksum: realization.forecastChecksum, seed: realization.seed,
    guestCount: realization.guestCount, partyCount: realization.partyCount, heavyGroupCount: realization.heavyGroupCount,
    buckets: realization.buckets };
}

export function demandRealizationChecksum(realization: Omit<DemandRealizationV1, 'checksum'> | DemandRealizationV1): string {
  return eventCalendarChecksum(realizationProjection(realization as Omit<DemandRealizationV1, 'checksum'>));
}

/**
 * Realise each bucket with deterministic stochastic rounding:
 * floor(E) + 1{ keyedDraw(bucket) < E - floor(E) }.
 * A running admission cap makes the realised result obey both configured
 * limits even when several fractional buckets round up.
 */
export function realizeDemand(forecast: DemandForecastV1): DemandRealizationV1 {
  validateForecast(forecast);
  const scenario = forecast.scenario;
  let remainingCapacity = Math.min(scenario.availableCapacityGuests, scenario.maxGuests);
  let remainingParties = scenario.maxParties;
  let guestCount = 0;
  let partyCount = 0;
  let heavyGroupCount = 0;
  const buckets = freezeArray(forecast.buckets.map((bucket) => {
    const expected = Math.min(bucket.admittedExpectedGuests, remainingCapacity);
    const whole = Math.floor(expected);
    const fractional = expected - whole;
    const draw = keyedRandomFloat(scenario.seed, `demand-bucket-${bucket.index}`, 'phase3-arrivals', 0);
    const rounded = whole + (draw < fractional ? 1 : 0);
    const realizedGuests = Math.max(0, Math.min(remainingCapacity, rounded));
    remainingCapacity -= realizedGuests;
    const expectedParties = realizedGuests === 0 ? 0 : Math.ceil(realizedGuests / scenario.tuning.partyMeanSize);
    const realizedParties = Math.min(realizedGuests, remainingParties, expectedParties);
    remainingParties -= realizedParties;
    const heavyExpected = realizedParties * scenario.tuning.heavyGroupRate;
    const heavyWhole = Math.floor(heavyExpected);
    const heavyFraction = heavyExpected - heavyWhole;
    const heavyDraw = keyedRandomFloat(scenario.seed, `demand-bucket-${bucket.index}`, 'phase3-heavy-groups', 0);
    const realizedHeavy = Math.min(realizedParties, heavyWhole + (heavyDraw < heavyFraction ? 1 : 0));
    guestCount += realizedGuests;
    partyCount += realizedParties;
    heavyGroupCount += realizedHeavy;
    return Object.freeze({ index: bucket.index, startTick: bucket.startTick, endTick: bucket.endTick,
      marketExpectedGuests: bucket.marketExpectedGuests, admittedExpectedGuests: bucket.admittedExpectedGuests,
      guestCount: realizedGuests, partyCount: realizedParties, heavyGroupCount: realizedHeavy });
  }));
  const realization: Omit<DemandRealizationV1, 'checksum'> = {
    version: PHASE3_DEMAND_VERSION, forecastChecksum: forecast.checksum, seed: scenario.seed,
    guestCount, partyCount, heavyGroupCount, buckets,
  };
  return Object.freeze({ ...realization, checksum: demandRealizationChecksum(realization) });
}

export const realizeArrivals = realizeDemand;

export function isDemandRealization(value: unknown): value is DemandRealizationV1 {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const realization = value as Partial<DemandRealizationV1>;
  const { version, forecastChecksum, seed, guestCount, partyCount, heavyGroupCount, buckets, checksum } = realization;
  if (version !== PHASE3_DEMAND_VERSION || typeof forecastChecksum !== 'string' || forecastChecksum.length === 0
    || typeof seed !== 'string' || !isSafeInteger(guestCount) || guestCount < 0
    || !isSafeInteger(partyCount) || partyCount < 0
    || !isSafeInteger(heavyGroupCount) || heavyGroupCount < 0
    || !Array.isArray(buckets) || typeof checksum !== 'string') return false;
  try {
    let summedGuests = 0;
    let summedParties = 0;
    let summedHeavyGroups = 0;
    for (let index = 0; index < buckets.length; index += 1) {
      const bucket = buckets[index] as Partial<RealizedArrivalBucketV1>;
      if (bucket.index !== index || !isSafeInteger(bucket.startTick) || !isSafeInteger(bucket.endTick)
        || bucket.endTick <= bucket.startTick || !isFiniteNumber(bucket.marketExpectedGuests) || bucket.marketExpectedGuests < 0
        || !isFiniteNumber(bucket.admittedExpectedGuests) || bucket.admittedExpectedGuests < 0
        || !isSafeInteger(bucket.guestCount) || bucket.guestCount < 0
        || !isSafeInteger(bucket.partyCount) || bucket.partyCount < 0 || bucket.partyCount > bucket.guestCount
        || !isSafeInteger(bucket.heavyGroupCount) || bucket.heavyGroupCount < 0 || bucket.heavyGroupCount > bucket.partyCount) return false;
      summedGuests += bucket.guestCount;
      summedParties += bucket.partyCount;
      summedHeavyGroups += bucket.heavyGroupCount;
    }
    if (summedGuests !== guestCount || summedParties !== partyCount || summedHeavyGroups !== heavyGroupCount) return false;
    const projection = { ...realization, checksum: undefined } as unknown as DemandRealizationV1;
    return demandRealizationChecksum(projection) === checksum;
  } catch {
    return false;
  }
}

export function assertDemandRealization(value: unknown): asserts value is DemandRealizationV1 {
  if (!isDemandRealization(value)) throw new RangeError('invalid demand realization');
}

export const validateDemandRealization = assertDemandRealization;

function waveKind(dayType: DemandDayType): DemandWaveKind {
  return dayType;
}

/** Convert the realised bucket plan to the existing roster-compatible contract. */
export function toDemandPlan(realization: DemandRealizationV1, scenario: DemandScenarioV1): DemandPlan {
  assertDemandScenario(scenario);
  assertDemandRealization(realization);
  if (realization.seed !== scenario.seed) throw new RangeError('demand realization seed does not match scenario');
  const waves = freezeArray(realization.buckets.map((bucket) => Object.freeze({
    id: `phase3-wave-${String(bucket.index + 1).padStart(5, '0')}`,
    kind: waveKind(scenario.dayType), startTick: bucket.startTick, endTick: bucket.endTick,
    guestCount: bucket.guestCount, partyCount: bucket.partyCount,
  })));
  return Object.freeze({ version: 1, seed: scenario.seed, guestCount: realization.guestCount,
    partyCount: realization.partyCount, startTick: scenario.startTick, endTick: scenario.endTick,
    waves, heavyGroupCount: realization.heavyGroupCount });
}

export function planDailyArrivals(input: DemandScenarioInputV1): {
  readonly scenario: DemandScenarioV1;
  readonly forecast: DemandForecastV1;
  readonly realization: DemandRealizationV1;
  readonly demandPlan: DemandPlan;
} {
  const scenario = createDemandScenario(input);
  const forecast = buildDemandForecast(scenario);
  const realization = realizeDemand(forecast);
  return Object.freeze({ scenario, forecast, realization, demandPlan: toDemandPlan(realization, scenario) });
}
