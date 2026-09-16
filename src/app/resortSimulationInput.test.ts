import { describe, expect, it } from 'vitest';
import type { IntegratedBenchmarkScenario } from '../integratedBenchmarkScenario';
import { canonicalResortSimulationInput, DAILY_DEMAND_BY_WEEKDAY } from './resortSimulationInput';

const base = { revision: 1, edges: [], trails: [], portal: null, ticketPriceCents: 10_000, amenities: [] };
const scenario: IntegratedBenchmarkScenario = {
  version: 1, tier: 'qualification', workload: 'detailed', fixtureId: 'fixture', runId: 'run', seed: 'seed', replayId: 'replay', saveKey: 'save', terrainKey: 'terrain', checkpointTarget: 1000, requestedSpeed: 1,
  dailyDemand: 10_000, dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000],
  amenities: [0, 1, 2].map(index => ({ id: `a${index}`, nodeId: `n${index}`, label: `A${index}`,
    priceCents: 1, capacityPerHour: 1, inventory: 1, opens: 8, closes: 16, accessSeconds: 1,
    serviceSeconds: 1, need: 'hunger', relief: { hunger: 1 } })),
};

describe('canonical resort simulation input', () => {
  it('uses transient benchmark demand and amenities when explicitly bound', () => {
    const result = canonicalResortSimulationInput(base, scenario);
    expect(result.dailyDemand).toBe(10_000);
    expect(result.dailyDemandByWeekday).toEqual(scenario.dailyDemandByWeekday);
    expect(result.amenities).toEqual(scenario.amenities);
  });

  it('retains ordinary gameplay defaults without a scenario', () => {
    const result = canonicalResortSimulationInput(base);
    expect(result.dailyDemand).toBe(900);
    expect(result.dailyDemandByWeekday).toEqual(DAILY_DEMAND_BY_WEEKDAY);
    expect(result.amenities).toEqual([]);
  });
});
