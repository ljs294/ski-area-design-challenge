import { describe, expect, it } from 'vitest';
import { DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG } from './config';
import {
  ARRIVAL_UINT24_MAX_DRAW,
  ARRIVAL_UINT24_SCALE,
  CANONICAL_ARRIVAL_GOLDEN_FIXTURES,
  PROBABILITY_GOLDEN_FIXTURES,
  abilityGapKernel,
  allocateDailyGuestTotal,
  buildArrivalBinWeightTable,
  canonicalArrivalCdf,
  clamp,
  constantElasticityDemandMultiplier,
  cumulativeDistribution,
  sampleCumulative,
  sampleArrivalBin,
  sampleSoftmax,
  sigmoid,
  shiftedLognormalArrivalPdf,
  stableSoftmax,
  validateArrivalBinWeightTable,
  validateDemandElasticity,
} from './probability';

describe('guest simulation probability foundation', () => {
  it('clamps and evaluates sigmoid without overflow', () => {
    expect(clamp(-2, 0, 1)).toBe(0);
    expect(clamp(0.4, 0, 1)).toBe(0.4);
    expect(clamp(2, 0, 1)).toBe(1);
    expect(sigmoid(-1_000_000)).toBeGreaterThanOrEqual(0);
    expect(sigmoid(-1_000_000)).toBeLessThan( Number.EPSILON);
    expect(sigmoid(1_000_000)).toBe(1);
    expect(() => clamp(0, 2, 1)).toThrow(RangeError);
    for (const vector of PROBABILITY_GOLDEN_FIXTURES.sigmoid) {
      expect(sigmoid(vector.input)).toBeCloseTo(vector.expected, 14);
    }
  });

  it('matches versioned ability-gap golden points and stays bounded', () => {
    for (const vector of PROBABILITY_GOLDEN_FIXTURES.abilityGap) {
      expect(abilityGapKernel(vector.delta, vector.risk)).toBeCloseTo(vector.expected, 12);
    }
    for (const risk of [0, 0.25, 0.5, 0.75, 1]) {
      expect(abilityGapKernel(0, risk)).toBeGreaterThanOrEqual(0);
      expect(abilityGapKernel(0, risk)).toBeLessThanOrEqual(1);
    }
    expect(abilityGapKernel(1e308, 1)).toBe(0);
  });

  it('normalizes softmax stably and samples deterministically from injected draws', () => {
    for (const vector of PROBABILITY_GOLDEN_FIXTURES.softmax) {
      expect(stableSoftmax(vector.logits, vector.temperature)).toHaveLength(vector.expected.length);
      stableSoftmax(vector.logits, vector.temperature).forEach((value, index) => {
        expect(value).toBeCloseTo(vector.expected[index]!, 14);
      });
    }
    const stable = stableSoftmax([1e308, 1e308 - 1, -1e308]);
    expect(stable.every((value) => Number.isFinite(value) && value >= 0)).toBe(true);
    expect(stable.reduce((sum, value) => sum + value, 0)).toBeCloseTo(1, 14);
    expect(stableSoftmax([Number.POSITIVE_INFINITY, Number.POSITIVE_INFINITY, 0])).toEqual([0.5, 0.5, 0]);
    const adversarial = stableSoftmax([-95.62992899114687, -667.792202957477,
      -94.46378547320933, -362.4665653631771]);
    expect(adversarial.every((value) => value >= 0)).toBe(true);
    expect(() => sampleSoftmax([0, 1, 2, 3], adversarial, 0.99)).not.toThrow();
    expect(cumulativeDistribution([1, 2, 1]).at(-1)).toBe(1);
    expect(sampleCumulative([0.2, 0.3, 0.5], 0)).toBe(0);
    expect(sampleCumulative([0.2, 0.3, 0.5], 0.2)).toBe(1);
    expect(sampleCumulative(['a', 'b', 'c'], [0.2, 0.3, 0.5], 0.99)).toBe('c');
    expect(sampleSoftmax(['left', 'right'], [0, 1], 0.99)).toBe('right');
  });

  it('keeps constant-elasticity demand decreasing and negative-elasticity constrained', () => {
    for (const vector of PROBABILITY_GOLDEN_FIXTURES.demand) {
      expect(constantElasticityDemandMultiplier(vector.price, vector.referencePrice, vector.elasticity))
        .toBeCloseTo(vector.expected, 12);
    }
    expect(constantElasticityDemandMultiplier(50, 100)).toBeGreaterThan(constantElasticityDemandMultiplier(100, 100));
    expect(constantElasticityDemandMultiplier(100, 100)).toBeGreaterThan(constantElasticityDemandMultiplier(200, 100));
    expect(constantElasticityDemandMultiplier(Number.MAX_VALUE, 1)).toBeGreaterThanOrEqual(0);
    expect(() => validateDemandElasticity(0)).toThrow(RangeError);
    expect(() => validateDemandElasticity(0.1)).toThrow(RangeError);
  });

  it('builds canonical weekday/weekend uint24 curves from the documented shifted density', () => {
    const weekday = buildArrivalBinWeightTable('weekday');
    const weekend = buildArrivalBinWeightTable('weekend');
    expect(weekday.version).toBe(1);
    expect(weekday.horizonSeconds).toBe(12 * 60 * 60);
    expect(weekday.binSeconds).toBe(10 * 60);
    expect(weekday.shiftSeconds).toBe(15 * 60);
    expect(weekday.weights).toHaveLength(72);
    expect(Array.from(weekday.weights).reduce((sum, weight) => sum + weight, 0)).toBe(ARRIVAL_UINT24_SCALE);
    expect(Array.from(weekend.weights).reduce((sum, weight) => sum + weight, 0)).toBe(ARRIVAL_UINT24_SCALE);
    expect(weekday.cdfThresholds.every((threshold, index) => index === 0 || threshold >= weekday.cdfThresholds[index - 1]!)).toBe(true);
    expect(weekday.cdfThresholds.at(-1)).toBe(ARRIVAL_UINT24_SCALE);
    expect(shiftedLognormalArrivalPdf(0, 'weekday')).toBeGreaterThan(0);
    expect(canonicalArrivalCdf(3_600, 'weekend')).toBeGreaterThan(canonicalArrivalCdf(3_600, 'weekday'));
    expect(weekend.medianSeconds).toBeLessThan(weekday.medianSeconds);
    expect(weekend.sigma).toBeLessThan(weekday.sigma);
    for (const vector of CANONICAL_ARRIVAL_GOLDEN_FIXTURES) {
      expect(canonicalArrivalCdf(vector.seconds, vector.dayType)).toBeCloseTo(vector.expectedCdf, 12);
    }
    expect(() => buildArrivalBinWeightTable('weekday', {
      ...DEFAULT_GUEST_ARRIVAL_CURVE_CONFIG, shiftSeconds: 0,
    })).toThrow(RangeError);
  });

  it('samples canonical tables at uint24 endpoints and conserves daily allocations exactly', () => {
    const table = buildArrivalBinWeightTable('weekday');
    expect(ARRIVAL_UINT24_MAX_DRAW).toBe(ARRIVAL_UINT24_SCALE - 1);
    expect(sampleArrivalBin(table, 0)).toBe(0);
    expect(sampleArrivalBin(table, table.cdfThresholds[0]! - 1)).toBe(0);
    expect(sampleArrivalBin(table, table.cdfThresholds[0]!)).toBe(1);
    expect(sampleArrivalBin(table, ARRIVAL_UINT24_MAX_DRAW)).toBe(table.binCount - 1);
    expect(() => sampleArrivalBin(table, ARRIVAL_UINT24_SCALE)).toThrow(RangeError);
    const forgedThresholds = { ...table, cdfThresholds: table.cdfThresholds.slice() };
    forgedThresholds.cdfThresholds[1] += 1;
    expect(() => validateArrivalBinWeightTable(forgedThresholds)).toThrow(RangeError);
    const forgedWeights = { ...table, weights: table.weights.slice() };
    forgedWeights.weights[0] += 1;
    expect(() => allocateDailyGuestTotal(100, forgedWeights)).toThrow(RangeError);
    for (const total of [0, 1, 123, 12_345, 50_000]) {
      const first = allocateDailyGuestTotal(total, table);
      const second = allocateDailyGuestTotal(total, table);
      expect(first).toEqual(second);
      expect(first).toHaveLength(table.binCount);
      expect(first.reduce((sum, count) => sum + count, 0)).toBe(total);
      expect(first.every((count) => Number.isSafeInteger(count) && count >= 0)).toBe(true);
    }
  });
});
