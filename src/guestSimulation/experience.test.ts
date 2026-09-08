import { describe, expect, it } from 'vitest';
import {
  EARLY_DEPARTURE_REASON_CODES,
  EXPERIENCE_THOUGHT_REASON_CODES,
  PHASE_2_FORMULAS,
  SATISFACTION_CHANNELS,
  aggregateThoughtsByReason,
  calculateCrowdingEffect,
  calculateExpectationGap,
  calculateSatisfactionChannels,
  calculateSuitableTerrainOutcome,
  evaluateEarlyDeparture,
} from './experience';

const satisfactionInput = (overrides: Partial<Parameters<typeof calculateSatisfactionChannels>[0]> = {}) => ({
  terrainFit: 0.8,
  queueWaitSeconds: 300,
  expectedQueueWaitSeconds: 300,
  crowding: 0.15,
  comfort: 0.8,
  conditions: 0.8,
  value: 0.8,
  variety: 0.7,
  safety: 0.95,
  ...overrides,
});

describe('Phase 2 guest experience formulas', () => {
  it('publishes stable names and versions for every formula', () => {
    expect(PHASE_2_FORMULAS.satisfaction).toEqual({ name: 'weighted-satisfaction-channels', version: 1 });
    expect(PHASE_2_FORMULAS.expectationGap).toEqual({ name: 'normalized-expectation-gap', version: 1 });
    expect(PHASE_2_FORMULAS.crowding).toEqual({ name: 'load-and-queue-crowding', version: 1 });
    expect(PHASE_2_FORMULAS.suitableTerrain).toEqual({ name: 'ability-fit-suitable-terrain', version: 1 });
    expect(PHASE_2_FORMULAS.earlyDeparture).toEqual({ name: 'keyed-early-departure-hazard', version: 1 });
    expect(PHASE_2_FORMULAS.thoughtAggregation).toEqual({ name: 'exact-reason-coded-thought-counts', version: 1 });
  });

  it('makes expectation shortfall and surplus directional and bounded', () => {
    const missed = calculateExpectationGap({ expected: 100, actual: 60, direction: 'higher-is-better' });
    const exceeded = calculateExpectationGap({ expected: 100, actual: 140, direction: 'higher-is-better' });
    const shorterQueue = calculateExpectationGap({ expected: 300, actual: 100, direction: 'lower-is-better', scale: 60 });
    expect(missed.signedGap).toBeLessThan(0);
    expect(missed.shortfall).toBeGreaterThan(0);
    expect(missed.surplus).toBe(0);
    expect(exceeded.signedGap).toBeGreaterThan(0);
    expect(exceeded.shortfall).toBe(0);
    expect(exceeded.surplus).toBeGreaterThan(0);
    expect(shorterQueue.signedGap).toBeGreaterThan(0);
    expect(shorterQueue.shortfall).toBe(0);
    expect(shorterQueue.signedGap).toBeLessThanOrEqual(1);
    expect(calculateExpectationGap({ expected: 0, actual: 0 }).signedGap).toBe(0);
    expect(() => calculateExpectationGap({ expected: Number.NaN, actual: 1 })).toThrow(RangeError);
    expect(() => calculateExpectationGap({ expected: 1, actual: 1, scale: 0 })).toThrow(RangeError);
  });

  it('keeps expectation disappointment monotonic for adversarial values', () => {
    const gaps = [0, 1, 10, 100, 1_000].map((actual) => calculateExpectationGap({ expected: 10, actual }));
    expect(gaps.map((gap) => gap.shortfall)).toEqual([0.9090909090909091, 0.8181818181818182, 0, 0, 0]);
    expect(gaps.every((gap) => Number.isFinite(gap.signedGap) && gap.shortfall >= 0 && gap.surplus >= 0)).toBe(true);
    const extreme = calculateExpectationGap({ expected: 1e308, actual: -1e308 });
    expect(Number.isFinite(extreme.rawGap)).toBe(true);
    expect(extreme.signedGap).toBe(-1);
  });

  it('turns occupancy and excess queue pressure into monotonic crowding penalties', () => {
    const light = calculateCrowdingEffect({ occupancy: 50, capacity: 100, queueWaitSeconds: 300, expectedQueueWaitSeconds: 300 });
    const busy = calculateCrowdingEffect({ occupancy: 80, capacity: 100, queueWaitSeconds: 300, expectedQueueWaitSeconds: 300 });
    const overloaded = calculateCrowdingEffect({ occupancy: 100, capacity: 100, queueWaitSeconds: 900, expectedQueueWaitSeconds: 300 });
    const queueOnly = calculateCrowdingEffect({ occupancy: 50, capacity: 100, queueWaitSeconds: 1_200, expectedQueueWaitSeconds: 60 });
    expect(light.penalty).toBe(0);
    expect(busy.penalty).toBeGreaterThan(light.penalty);
    expect(overloaded.penalty).toBeGreaterThan(busy.penalty);
    expect(queueOnly.penalty).toBeGreaterThan(light.penalty);
    expect(overloaded.score + overloaded.penalty).toBe(1);
    expect(overloaded.effect).toBe(-overloaded.penalty);
    expect(overloaded.level).toBe('severe');
    expect(calculateCrowdingEffect({ occupancy: 1e9, capacity: 1, sensitivity: 1 }).penalty).toBe(1);
    expect(() => calculateCrowdingEffect({ occupancy: 1, capacity: 0 })).toThrow(RangeError);
    expect(() => calculateCrowdingEffect({ occupancy: Number.POSITIVE_INFINITY, capacity: 1 })).toThrow(RangeError);
  });

  it('keeps suitable terrain positive, over-ability terrain negative, and closed terrain negative', () => {
    const matched = calculateSuitableTerrainOutcome({ ability: 0.35, terrainDifficulty: 0.35 });
    const easy = calculateSuitableTerrainOutcome({ ability: 0.35, terrainDifficulty: 0.02 });
    const hard = calculateSuitableTerrainOutcome({ ability: 0.35, terrainDifficulty: 0.90 });
    const closed = calculateSuitableTerrainOutcome({ ability: 0.35, terrainDifficulty: 0.35, open: false });
    expect(matched.outcomeDirection).toBe('positive');
    expect(matched.suitable).toBe(true);
    expect(hard.outcomeDirection).toBe('negative');
    expect(hard.reasonCode).toBe('too-difficult');
    expect(closed.outcomeDirection).toBe('negative');
    expect(closed.reasonCode).toBe('closed');
    expect(easy.reasonCode).toBe('too-easy');
    expect(calculateSuitableTerrainOutcome({ ability: 0.2, terrainDifficulty: 0.2, hardcoreTerrainPreference: 0 }).suitability)
      .toBeGreaterThan(calculateSuitableTerrainOutcome({ ability: 0.2, terrainDifficulty: 0.8, hardcoreTerrainPreference: 0 }).suitability);
    expect(() => calculateSuitableTerrainOutcome({ ability: Number.NaN, terrainDifficulty: 0.5 })).toThrow(RangeError);
  });

  it('reconciles weighted satisfaction channels into a bounded score', () => {
    const result = calculateSatisfactionChannels(satisfactionInput());
    expect(result.version).toBe(1);
    expect(result.formula).toBe(PHASE_2_FORMULAS.satisfaction.name);
    expect(result.channels.map((channel) => channel.channel)).toEqual(SATISFACTION_CHANNELS);
    expect(result.overall).toBeGreaterThan(0);
    expect(result.overall).toBeLessThanOrEqual(1);
    expect(result.channels.reduce((sum, channel) => sum + channel.weightedContribution, 0)).toBe(result.overall);
    const worseWait = calculateSatisfactionChannels(satisfactionInput({ queueWaitSeconds: 1_800 }));
    const worseCrowding = calculateSatisfactionChannels(satisfactionInput({ crowding: 0.9 }));
    const betterTerrain = calculateSatisfactionChannels(satisfactionInput({ terrainFit: 1 }));
    expect(worseWait.overall).toBeLessThan(result.overall);
    expect(worseCrowding.overall).toBeLessThan(result.overall);
    expect(betterTerrain.overall).toBeGreaterThan(result.overall);
    expect(() => calculateSatisfactionChannels(satisfactionInput({ comfort: Number.NaN }))).toThrow(RangeError);
    expect(() => calculateSatisfactionChannels(satisfactionInput({ weights: { terrain: -1 } }))).not.toThrow();
  });

  it('uses deterministic keyed randomness and stable reason ranking for departure', () => {
    const calm = evaluateEarlyDeparture({ worldSeed: 'world', entityId: 'guest-1', satisfaction: 1 });
    const unhappy = evaluateEarlyDeparture({ worldSeed: 'world', entityId: 'guest-1', satisfaction: 0.2,
      crowding: 0.9, conditions: 0.1, safety: 0.2, terrain: calculateSuitableTerrainOutcome({ ability: 0.3, terrainDifficulty: 0.95 }),
      injured: true, decisionOrdinal: 2 });
    const repeated = evaluateEarlyDeparture({ worldSeed: 'world', entityId: 'guest-1', satisfaction: 0.2,
      crowding: 0.9, conditions: 0.1, safety: 0.2, terrain: calculateSuitableTerrainOutcome({ ability: 0.3, terrainDifficulty: 0.95 }),
      injured: true, decisionOrdinal: 2 });
    expect(calm.probability).toBe(0);
    expect(calm.departedEarly).toBe(false);
    expect(unhappy.probability).toBeGreaterThan(calm.probability);
    expect(unhappy).toEqual(repeated);
    expect(unhappy.primaryReasonCode).toBe('injury');
    expect(unhappy.reasonCodes).toContain('low-satisfaction');
    expect(unhappy.reasonCodes).toContain('safety-concern');
    expect(unhappy.reasonScores[0]!.reasonCode).toBe(unhappy.primaryReasonCode);
    expect(unhappy.draw).toBeGreaterThan(0);
    expect(unhappy.draw).toBeLessThan(1);
  });

  it('does not classify a planned exit as an early departure', () => {
    const decision = evaluateEarlyDeparture({ worldSeed: 'world', entityId: 'guest-1', satisfaction: 0,
      currentTick: 100, plannedDepartureTick: 100, injured: true });
    expect(decision.eligible).toBe(false);
    expect(decision.probability).toBe(0);
    expect(decision.departedEarly).toBe(false);
    expect(decision.reasonCodes).toEqual([]);
  });

  it('aggregates reason-coded thoughts with exact reason and sentiment reconciliation', () => {
    const observations = [
      { reasonCode: 'low-satisfaction' as const, sentiment: 'negative' as const },
      { reasonCode: 'low-satisfaction' as const, sentiment: 'negative' as const, count: 2 },
      { reasonCode: 'riding' as const, sentiment: 'positive' as const, count: 4 },
      { reasonCode: 'waiting' as const, sentiment: 'neutral' as const, count: 3 },
    ];
    const aggregate = aggregateThoughtsByReason(observations);
    expect(aggregate.totalEvents).toBe(10);
    expect(aggregate.positiveEvents).toBe(4);
    expect(aggregate.neutralEvents).toBe(3);
    expect(aggregate.negativeEvents).toBe(3);
    expect(aggregate.byReason.find((reason) => reason.reasonCode === 'low-satisfaction')?.sentiment).toBe('negative');
    expect(aggregate.byReason.find((reason) => reason.reasonCode === 'riding')?.sentiment).toBe('positive');
    expect(aggregate.byReason.reduce((sum, reason) => sum + reason.count, 0)).toBe(aggregate.totalEvents);
    expect(aggregate.byReason.every((reason) => reason.count === reason.positiveCount + reason.neutralCount + reason.negativeCount)).toBe(true);
    expect(aggregate.reconciled).toBe(true);
    expect(aggregateThoughtsByReason([...observations].reverse())).toEqual(aggregate);
    expect(aggregateThoughtsByReason([]).reconciled).toBe(true);
    expect(() => aggregateThoughtsByReason([{ reasonCode: 'not-a-code' as never, sentiment: 'negative' }])).toThrow(RangeError);
    expect(() => aggregateThoughtsByReason([{ reasonCode: 'waiting', sentiment: 'neutral', count: 1.2 }])).toThrow(RangeError);
    expect(EXPERIENCE_THOUGHT_REASON_CODES).toContain(EARLY_DEPARTURE_REASON_CODES[0]);
  });
});
