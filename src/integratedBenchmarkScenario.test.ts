import { describe, expect, it } from 'vitest';
import {
  INTEGRATED_BENCHMARK_SCENARIO_GLOBAL,
  installIntegratedBenchmarkBootstrap,
  installedIntegratedBenchmarkScenario,
  validateIntegratedBenchmarkScenario,
  type IntegratedBenchmarkScenario,
} from './integratedBenchmarkScenario';

function scenario(): IntegratedBenchmarkScenario {
  return {
    version: 1,
    tier: 'qualification',
    workload: 'detailed',
    fixtureId: 'jackson-nh-white-mountains-v1',
    runId: 'trial-1',
    seed: 'jackson-seed-v1',
    replayId: 'detailed-camera-v1',
    saveKey: 'jackson-1000',
    terrainKey: 'jackson-terrain',
    checkpointTarget: 1000,
    requestedSpeed: 1,
    dailyDemand: 10000,
    dailyDemandByWeekday: [10000, 10000, 10000, 10000, 10000, 10000, 10000],
    amenities: [0, 1, 2].map(index => ({
      id: `amenity-${index}`, nodeId: `node-${index}`, label: `Amenity ${index}`,
      priceCents: 1000, capacityPerHour: 100, inventory: 1000,
      opens: 8, closes: 16, accessSeconds: 30, serviceSeconds: 60,
      need: index === 1 ? 'thirst' : 'hunger', relief: { hunger: 0.5 },
    })),
  };
}

describe('integrated benchmark transient scenario', () => {
  it('binds the scenario to both save and terrain identity', () => {
    const value = scenario();
    (globalThis as typeof globalThis & Record<string, unknown>)[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL] = value;
    expect(installedIntegratedBenchmarkScenario({ saveKey: value.saveKey, terrainKey: value.terrainKey })).toEqual(value);
    expect(installedIntegratedBenchmarkScenario({ saveKey: 'another-save', terrainKey: value.terrainKey })).toBeNull();
    expect(installedIntegratedBenchmarkScenario({ saveKey: value.saveKey, terrainKey: 'another-terrain' })).toBeNull();
    delete (globalThis as typeof globalThis & Record<string, unknown>)[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL];
  });

  it('requires three distinct amenity destinations and the frozen demand', () => {
    const value = scenario();
    expect(() => validateIntegratedBenchmarkScenario({ ...value, dailyDemand: 900 })).toThrow(/qualification workload/);
    expect(() => validateIntegratedBenchmarkScenario({ ...value,
      amenities: value.amenities.map(amenity => ({ ...amenity, nodeId: 'same-node' })) })).toThrow(/distinct/);
  });
  it('accepts an explicit zero-demand qualification reference workload', () => {
    const value = scenario();
    expect(validateIntegratedBenchmarkScenario({ ...value, workload: 'empty', checkpointTarget: 0,
      dailyDemand: 0, dailyDemandByWeekday: [0, 0, 0, 0, 0, 0, 0] })).toMatchObject({ workload: 'empty' });
  });
  it('installs an explicit packaged bootstrap without changing the save contract', () => {
    const value = scenario();
    const encoded = Buffer.from(JSON.stringify(value)).toString('base64url');
    installIntegratedBenchmarkBootstrap(`?integrated-benchmark-scenario=${encoded}&integrated-benchmark-telemetry=1`);
    const globals = globalThis as typeof globalThis & Record<string, unknown>;
    expect(globals[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL]).toEqual(value);
    expect(globals.__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__).toEqual({ enabled: true, maxEntries: 20_000 });
    delete globals[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL];
    delete globals.__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__;
  });
});
