import type { GuestSimulationContractVersion } from './contracts.ts';
import { GUEST_SIMULATION_CONTRACT_VERSION } from './contracts.ts';

export const GUEST_SIMULATION_CONFIG_VERSION = 1 as const;
export const GUEST_PROBABILITY_CONFIG_VERSION = 1 as const;

/**
 * Provisional game-tuning coefficients. They are intentionally named and
 * versioned so a later balance pass can change them without changing the
 * simulation's contract or implying a real-world behavioral claim.
 */
export const ABILITY_GAP_MU_INTERCEPT = -0.35;
export const ABILITY_GAP_MU_RISK_SLOPE = 0.80;
export const ABILITY_GAP_SIGMA_INTERCEPT = 0.50;
export const ABILITY_GAP_SIGMA_RISK_SLOPE = 0.45;
export const DEFAULT_SOFTMAX_TEMPERATURE = 0.10;
export const DEFAULT_DEMAND_ELASTICITY = -1.10;
export const GUEST_ARRIVAL_CURVE_CONFIG_VERSION = 1 as const;
export const ARRIVAL_BIN_SECONDS = 10 * 60;
export const ARRIVAL_SHIFT_SECONDS = 15 * 60;
export const ARRIVAL_HORIZON_SECONDS = 12 * 60 * 60;
export const ARRIVAL_WEEKDAY_MEDIAN_SECONDS = 80 * 60;
export const ARRIVAL_WEEKEND_MEDIAN_SECONDS = 55 * 60;
export const ARRIVAL_WEEKDAY_SIGMA = 0.60;
export const ARRIVAL_WEEKEND_SIGMA = 0.36;
export const ARRIVAL_WEEKDAY_BACKGROUND_MIX = 0.20;
export const ARRIVAL_WEEKEND_BACKGROUND_MIX = 0.08;
export const ARRIVAL_WEIGHT_SCALE = 2 ** 24;

export type ArrivalDayType = 'weekday' | 'weekend';

export interface GuestArrivalCurveProfileV1 {
  readonly medianSeconds: number;
  readonly sigma: number;
  readonly backgroundMix: number;
}

export interface GuestArrivalCurveConfigV1 {
  readonly version: typeof GUEST_ARRIVAL_CURVE_CONFIG_VERSION;
  readonly horizonSeconds: number;
  readonly binSeconds: number;
  readonly shiftSeconds: number;
  readonly weekday: GuestArrivalCurveProfileV1;
  readonly weekend: GuestArrivalCurveProfileV1;
}

export type GuestArrivalCurveConfigOverrides = Partial<Omit<GuestArrivalCurveConfigV1, 'version' | 'weekday' | 'weekend'>> & {
  readonly weekday?: Partial<GuestArrivalCurveProfileV1>;
  readonly weekend?: Partial<GuestArrivalCurveProfileV1>;
};

export interface AbilityGapCoefficientsV1 {
  readonly muIntercept: number;
  readonly muRiskSlope: number;
  readonly sigmaIntercept: number;
  readonly sigmaRiskSlope: number;
}

export interface GuestProbabilityConfigV1 {
  readonly version: typeof GUEST_PROBABILITY_CONFIG_VERSION;
  readonly abilityGap: AbilityGapCoefficientsV1;
  readonly softmaxTemperature: number;
  readonly demandReferencePrice: number;
  readonly demandElasticity: number;
  readonly arrivalCurve: GuestArrivalCurveConfigV1;
}

export type GuestProbabilityConfigOverrides = Partial<Omit<GuestProbabilityConfigV1, 'version' | 'abilityGap' | 'arrivalCurve'>> & {
  readonly abilityGap?: Partial<AbilityGapCoefficientsV1>;
  readonly arrivalCurve?: GuestArrivalCurveConfigOverrides;
};

export const DEFAULT_ABILITY_GAP_COEFFICIENTS: AbilityGapCoefficientsV1 = Object.freeze({
  muIntercept: ABILITY_GAP_MU_INTERCEPT,
  muRiskSlope: ABILITY_GAP_MU_RISK_SLOPE,
  sigmaIntercept: ABILITY_GAP_SIGMA_INTERCEPT,
  sigmaRiskSlope: ABILITY_GAP_SIGMA_RISK_SLOPE,
});

export const DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG: GuestArrivalCurveConfigV1 = Object.freeze({
  version: GUEST_ARRIVAL_CURVE_CONFIG_VERSION,
  horizonSeconds: ARRIVAL_HORIZON_SECONDS,
  binSeconds: ARRIVAL_BIN_SECONDS,
  shiftSeconds: ARRIVAL_SHIFT_SECONDS,
  weekday: Object.freeze({ medianSeconds: ARRIVAL_WEEKDAY_MEDIAN_SECONDS, sigma: ARRIVAL_WEEKDAY_SIGMA, backgroundMix: ARRIVAL_WEEKDAY_BACKGROUND_MIX }),
  weekend: Object.freeze({ medianSeconds: ARRIVAL_WEEKEND_MEDIAN_SECONDS, sigma: ARRIVAL_WEEKEND_SIGMA, backgroundMix: ARRIVAL_WEEKEND_BACKGROUND_MIX }),
});

export const DEFAULT_GUEST_PROBABILITY_CONFIG: GuestProbabilityConfigV1 = Object.freeze({
  version: GUEST_PROBABILITY_CONFIG_VERSION,
  abilityGap: DEFAULT_ABILITY_GAP_COEFFICIENTS,
  softmaxTemperature: DEFAULT_SOFTMAX_TEMPERATURE,
  demandReferencePrice: 100,
  demandElasticity: DEFAULT_DEMAND_ELASTICITY,
  arrivalCurve: DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG,
});

export interface GuestSimulationConfigV1 {
  readonly version: GuestSimulationContractVersion;
  readonly configVersion: typeof GUEST_SIMULATION_CONFIG_VERSION;
  /** Both fields are retained so callers can say either “tick” or “second”. */
  readonly tickSeconds: number;
  readonly simulatedSecondsPerTick: number;
  readonly maxGuests: number;
  readonly maxParties: number;
  readonly maxThoughtEventsPerSnapshot: number;
  readonly maxIncidentsPerSnapshot: number;
  readonly operatingDaySeconds: number;
  readonly horizonSeconds: number;
  readonly probability: GuestProbabilityConfigV1;
}

export type GuestSimulationConfig = GuestSimulationConfigV1;
export type GuestSimulationConfigOverrides = Partial<Omit<GuestSimulationConfigV1, 'version' | 'configVersion' | 'probability'>> & {
  readonly probability?: GuestProbabilityConfigOverrides;
};

/** The initial contract intentionally advances at one simulated second per tick. */
export const DEFAULT_GUEST_SIMULATION_CONFIG: GuestSimulationConfigV1 = Object.freeze({
  version: GUEST_SIMULATION_CONTRACT_VERSION,
  configVersion: GUEST_SIMULATION_CONFIG_VERSION,
  tickSeconds: 1,
  simulatedSecondsPerTick: 1,
  maxGuests: 50_000,
  maxParties: 20_000,
  maxThoughtEventsPerSnapshot: 2_000,
  maxIncidentsPerSnapshot: 256,
  operatingDaySeconds: 12 * 60 * 60,
  horizonSeconds: 7 * 24 * 60 * 60,
  probability: DEFAULT_GUEST_PROBABILITY_CONFIG,
});

function isPositiveInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function assertBoundedInteger(value: unknown, label: string, maximum: number): void {
  if (!isPositiveInteger(value) || value > maximum) {
    throw new RangeError(`${label} must be a positive integer no greater than ${maximum}`);
  }
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

export function isGuestProbabilityConfig(value: unknown): value is GuestProbabilityConfigV1 {
  if (typeof value !== 'object' || value === null) return false;
  const config = value as Partial<GuestProbabilityConfigV1>;
  const gap = config.abilityGap;
  return config.version === GUEST_PROBABILITY_CONFIG_VERSION
    && typeof gap === 'object' && gap !== null
    && isFiniteNumber(gap.muIntercept)
    && isFiniteNumber(gap.muRiskSlope)
    && isFiniteNumber(gap.sigmaIntercept) && gap.sigmaIntercept > 0
    && isFiniteNumber(gap.sigmaRiskSlope) && gap.sigmaRiskSlope >= 0
    && isFiniteNumber(config.softmaxTemperature) && config.softmaxTemperature > 0
    && isFiniteNumber(config.demandReferencePrice) && config.demandReferencePrice > 0
    && isFiniteNumber(config.demandElasticity) && config.demandElasticity < 0
    && isGuestArrivalCurveConfig(config.arrivalCurve);
}

function isArrivalProfile(value: unknown): value is GuestArrivalCurveProfileV1 {
  if (typeof value !== 'object' || value === null) return false;
  const profile = value as Partial<GuestArrivalCurveProfileV1>;
  return isPositiveInteger(profile.medianSeconds)
    && isFiniteNumber(profile.sigma) && profile.sigma > 0
    && isFiniteNumber(profile.backgroundMix) && profile.backgroundMix >= 0 && profile.backgroundMix <= 1;
}

export function isGuestArrivalCurveConfig(value: unknown): value is GuestArrivalCurveConfigV1 {
  if (typeof value !== 'object' || value === null) return false;
  const config = value as Partial<GuestArrivalCurveConfigV1>;
  return config.version === GUEST_ARRIVAL_CURVE_CONFIG_VERSION
    && isPositiveInteger(config.horizonSeconds)
    && isPositiveInteger(config.binSeconds)
    && config.horizonSeconds % config.binSeconds === 0
    && isPositiveInteger(config.shiftSeconds)
    && config.shiftSeconds < config.horizonSeconds
    && isArrivalProfile(config.weekday)
    && isArrivalProfile(config.weekend);
}

export function assertGuestArrivalCurveConfig(value: unknown): asserts value is GuestArrivalCurveConfigV1 {
  if (!isGuestArrivalCurveConfig(value)) throw new RangeError('Invalid guest arrival curve config');
}

export function createGuestArrivalCurveConfig(overrides: GuestArrivalCurveConfigOverrides = {}): GuestArrivalCurveConfigV1 {
  const config: GuestArrivalCurveConfigV1 = {
    ...DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG,
    ...overrides,
    version: GUEST_ARRIVAL_CURVE_CONFIG_VERSION,
    weekday: { ...DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG.weekday, ...overrides.weekday },
    weekend: { ...DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG.weekend, ...overrides.weekend },
  };
  assertGuestArrivalCurveConfig(config);
  return Object.freeze({ ...config, weekday: Object.freeze({ ...config.weekday }), weekend: Object.freeze({ ...config.weekend }) });
}

export function assertGuestProbabilityConfig(value: unknown): asserts value is GuestProbabilityConfigV1 {
  if (!isGuestProbabilityConfig(value)) throw new RangeError('Invalid guest probability config');
}

export function createGuestProbabilityConfig(overrides: GuestProbabilityConfigOverrides = {}): GuestProbabilityConfigV1 {
  const abilityGap = { ...DEFAULT_ABILITY_GAP_COEFFICIENTS, ...overrides.abilityGap };
  const config: GuestProbabilityConfigV1 = {
    ...DEFAULT_GUEST_PROBABILITY_CONFIG,
    ...overrides,
    version: GUEST_PROBABILITY_CONFIG_VERSION,
    abilityGap,
    arrivalCurve: createGuestArrivalCurveConfig(overrides.arrivalCurve),
  };
  assertGuestProbabilityConfig(config);
  return Object.freeze({ ...config, abilityGap: Object.freeze({ ...abilityGap }) });
}

/**
 * Checks a config without changing it.  This is deliberately stricter than a
 * structural TypeScript cast because configs can cross a worker boundary.
 */
export function isGuestSimulationConfig(value: unknown): value is GuestSimulationConfigV1 {
  if (typeof value !== 'object' || value === null) return false;
  const config = value as Partial<GuestSimulationConfigV1>;
  return config.version === GUEST_SIMULATION_CONTRACT_VERSION
    && config.configVersion === GUEST_SIMULATION_CONFIG_VERSION
    && isPositiveInteger(config.tickSeconds)
    && isPositiveInteger(config.simulatedSecondsPerTick)
    && config.tickSeconds === config.simulatedSecondsPerTick
    && isPositiveInteger(config.maxGuests)
    && config.maxGuests <= 50_000
    && isPositiveInteger(config.maxParties)
    && config.maxParties <= 50_000
    && isPositiveInteger(config.maxThoughtEventsPerSnapshot)
    && config.maxThoughtEventsPerSnapshot <= 1_000_000
    && isPositiveInteger(config.maxIncidentsPerSnapshot)
    && config.maxIncidentsPerSnapshot <= 100_000
    && isPositiveInteger(config.operatingDaySeconds)
    && config.operatingDaySeconds <= 7 * 24 * 60 * 60
    && isPositiveInteger(config.horizonSeconds)
    && config.horizonSeconds <= 366 * 24 * 60 * 60
    && isGuestProbabilityConfig(config.probability)
    && isGuestArrivalCurveConfig(config.probability.arrivalCurve);
}

export function assertGuestSimulationConfig(value: unknown): asserts value is GuestSimulationConfigV1 {
  if (!isGuestSimulationConfig(value)) {
    throw new RangeError('Invalid guest simulation config');
  }
}

/**
 * Creates an immutable v1 config.  Version fields are not overrideable: a
 * future config must add a new version and an explicit migration.
 */
export function createGuestSimulationConfig(overrides: GuestSimulationConfigOverrides = {}): GuestSimulationConfigV1 {
  const tickSeconds = overrides.tickSeconds ?? overrides.simulatedSecondsPerTick ?? DEFAULT_GUEST_SIMULATION_CONFIG.tickSeconds;
  const simulatedSecondsPerTick = overrides.simulatedSecondsPerTick ?? tickSeconds;
  const config: GuestSimulationConfigV1 = {
    ...DEFAULT_GUEST_SIMULATION_CONFIG,
    ...overrides,
    version: GUEST_SIMULATION_CONTRACT_VERSION,
    configVersion: GUEST_SIMULATION_CONFIG_VERSION,
    tickSeconds,
    simulatedSecondsPerTick,
    probability: createGuestProbabilityConfig(overrides.probability),
  };
  assertGuestSimulationConfig(config);
  return Object.freeze(config);
}

/** Alias useful to protocol adapters that call config construction “normalization”. */
export const normalizeGuestSimulationConfig = createGuestSimulationConfig;

export function validateGuestSimulationConfig(value: unknown): GuestSimulationConfigV1 {
  assertGuestSimulationConfig(value);
  return value;
}

export function assertGuestSimulationConfigBounds(config: GuestSimulationConfigV1): void {
  assertGuestSimulationConfig(config);
  assertBoundedInteger(config.maxGuests, 'maxGuests', 50_000);
  assertBoundedInteger(config.maxParties, 'maxParties', 50_000);
}
