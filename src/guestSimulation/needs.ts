/** Deterministic comfort needs used by Phase 5 amenity decisions. */

import type { SimulatedSecond } from './contracts.ts';

export const NEEDS_VERSION = 1 as const;

export type NeedType = 'hunger' | 'thirst' | 'warmth' | 'restroom' | 'fatigue';

export const NEED_TYPES: readonly NeedType[] = ['hunger', 'thirst', 'warmth', 'restroom', 'fatigue'];

export interface NeedState {
  readonly hunger: number;
  readonly thirst: number;
  readonly warmth: number;
  readonly restroom: number;
  readonly fatigue: number;
}

export type GuestNeedState = NeedState;
export type GuestNeeds = NeedState;

export type NeedStateInput = Partial<Readonly<Record<NeedType, number>>>;
export type NeedWeights = Partial<Readonly<Record<NeedType, number>>>;

/** Deficit gained per simulated second.  Rates are deliberately small and explicit. */
export interface NeedRates {
  readonly hunger: number;
  readonly thirst: number;
  readonly warmth: number;
  readonly restroom: number;
  readonly fatigue: number;
}

export const DEFAULT_NEED_RATES: NeedRates = Object.freeze({
  hunger: 1 / 3_600,
  thirst: 1 / 2_700,
  warmth: 1 / 4_800,
  restroom: 1 / 5_400,
  fatigue: 1 / 7_200,
});

function unit(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) {
    throw new RangeError(`${label} must be in [0, 1]`);
  }
}

function seconds(value: unknown, label: string): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) throw new RangeError(`${label} must be a non-negative integer`);
}

function freezeState(state: NeedState): NeedState { return Object.freeze({ ...state }); }

function normalizeInput(input: NeedStateInput = {}): NeedState {
  const state = {} as Record<NeedType, number>;
  for (const type of NEED_TYPES) {
    const value = input[type] ?? 0;
    unit(value, `need ${type}`);
    state[type] = value;
  }
  return freezeState(state as NeedState);
}

/** Create a bounded deficit state. Missing needs begin at zero. */
export function createNeedState(input: NeedStateInput = {}): NeedState {
  return normalizeInput(input);
}

export function assertNeedState(value: unknown): asserts value is NeedState {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new RangeError('need state must be an object');
  const state = value as NeedState;
  for (const type of NEED_TYPES) unit(state[type], `need ${type}`);
}

export function isNeedState(value: unknown): value is NeedState {
  try { assertNeedState(value); return true; } catch { return false; }
}

function normalizeRates(rates: NeedRates): NeedRates {
  const result = {} as Record<NeedType, number>;
  for (const type of NEED_TYPES) {
    const value = rates[type];
    if (typeof value !== 'number' || !Number.isFinite(value) || value < 0) throw new RangeError(`need rate ${type} must be finite and non-negative`);
    result[type] = value;
  }
  return Object.freeze(result) as NeedRates;
}

/** Advance deficits by whole seconds, clamping each channel to one. */
export function advanceNeedState(state: NeedState, elapsedSeconds: SimulatedSecond, rates: NeedRates = DEFAULT_NEED_RATES): NeedState {
  assertNeedState(state);
  seconds(elapsedSeconds, 'elapsedSeconds');
  const safeRates = normalizeRates(rates);
  const next = {} as Record<NeedType, number>;
  // Fixed precision prevents different host advance chunk sizes from leaking
  // IEEE-754 accumulation order into authoritative checksums.
  for (const type of NEED_TYPES) next[type] = Math.min(1,
    Math.round((state[type] + elapsedSeconds * safeRates[type]) * 1_000_000_000_000) / 1_000_000_000_000);
  return freezeState(next as NeedState);
}

/** Reduce deficits by bounded relief supplied by a facility service. */
export function relieveNeedState(state: NeedState, relief: NeedStateInput): NeedState {
  assertNeedState(state);
  const next = {} as Record<NeedType, number>;
  for (const type of NEED_TYPES) {
    const amount = relief[type] ?? 0;
    unit(amount, `need relief ${type}`);
    next[type] = Math.max(0, state[type] - amount);
  }
  return freezeState(next as NeedState);
}

/** Weighted urgency used by deterministic service choice. */
export function needUrgency(state: NeedState, weights: NeedWeights = {}): number {
  assertNeedState(state);
  let total = 0;
  let weightTotal = 0;
  for (const type of NEED_TYPES) {
    const weight = weights[type] ?? 1;
    if (typeof weight !== 'number' || !Number.isFinite(weight) || weight < 0) throw new RangeError(`need weight ${type} must be finite and non-negative`);
    total += state[type] * weight;
    weightTotal += weight;
  }
  return weightTotal === 0 ? 0 : total / weightTotal;
}

export function highestNeed(state: NeedState): { readonly type: NeedType; readonly value: number } {
  assertNeedState(state);
  let selected: NeedType = NEED_TYPES[0]!;
  for (const type of NEED_TYPES.slice(1)) {
    if (state[type] > state[selected]) selected = type;
  }
  return Object.freeze({ type: selected, value: state[selected] });
}

/** Convert current deficits into a comfort score where one is fully comfortable. */
export function comfortFromNeeds(state: NeedState): number {
  return 1 - needUrgency(state);
}

export const updateNeeds = advanceNeedState;
export const advanceNeeds = advanceNeedState;
export const applyNeedRelief = relieveNeedState;
export const calculateNeedUrgency = needUrgency;
export const createNeeds = createNeedState;
