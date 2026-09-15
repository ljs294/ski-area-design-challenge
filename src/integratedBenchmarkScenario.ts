import type { DualAmenity } from './types/dualClock';

export const INTEGRATED_BENCHMARK_SCENARIO_GLOBAL = '__MOUNTAIN_PLANNER_INTEGRATED_BENCHMARK__';
export const INTEGRATED_BENCHMARK_SCENARIO_QUERY = 'integrated-benchmark-scenario';
export const INTEGRATED_BENCHMARK_TELEMETRY_QUERY = 'integrated-benchmark-telemetry';

/**
 * Session-only workload authority installed by the integrated benchmark runner.
 * It is deliberately absent from GameSave so schema-17 and ordinary gameplay
 * retain their existing persistence semantics.
 */
export interface IntegratedBenchmarkScenario {
  readonly version: 1;
  readonly tier: 'diagnostic' | 'qualification';
  readonly workload: 'empty' | 'detailed' | 'aggregate';
  readonly fixtureId: string;
  readonly runId: string;
  readonly seed: string;
  readonly replayId: string;
  readonly saveKey: string;
  readonly terrainKey: string;
  readonly checkpointTarget: number;
  readonly requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64;
  readonly dailyDemand: number;
  readonly dailyDemandByWeekday: readonly [number, number, number, number, number, number, number];
  readonly amenities: readonly DualAmenity[];
}

function finiteNonnegative(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0;
}

export function validateIntegratedBenchmarkScenario(value: unknown): IntegratedBenchmarkScenario {
  if (!value || typeof value !== 'object') throw new Error('Integrated benchmark scenario is missing.');
  const scenario = value as Partial<IntegratedBenchmarkScenario>;
  if (scenario.version !== 1 || !['diagnostic', 'qualification'].includes(scenario.tier ?? '')
    || !['empty', 'detailed', 'aggregate'].includes(scenario.workload ?? '')
    || !scenario.fixtureId || !scenario.runId || !scenario.seed || !scenario.replayId || !scenario.saveKey || !scenario.terrainKey
    || !Number.isSafeInteger(scenario.checkpointTarget) || scenario.checkpointTarget! < 0
    || ![1, 2, 4, 8, 16, 64].includes(scenario.requestedSpeed ?? 0)
    || !Number.isSafeInteger(scenario.dailyDemand) || scenario.dailyDemand! < 0
    || !Array.isArray(scenario.dailyDemandByWeekday) || scenario.dailyDemandByWeekday.length !== 7
    || scenario.dailyDemandByWeekday.some(value => !Number.isSafeInteger(value) || value < 0)
    || !Array.isArray(scenario.amenities) || scenario.amenities.length < 3) {
    throw new Error('Integrated benchmark scenario contract is invalid.');
  }
  if (scenario.tier === 'qualification') {
    const empty = scenario.workload === 'empty';
    if (empty ? scenario.checkpointTarget !== 0 || scenario.dailyDemand !== 0
      || scenario.dailyDemandByWeekday.some(value => value !== 0) || scenario.requestedSpeed !== 1
      : ![1000, 3000].includes(scenario.checkpointTarget!) || scenario.dailyDemand !== 10_000
        || scenario.dailyDemandByWeekday.some(value => value !== 10_000)) {
      throw new Error('Integrated qualification workload does not match the frozen population contract.');
    }
    if ((scenario.workload === 'detailed' && ![1, 2, 4].includes(scenario.requestedSpeed!))
      || (scenario.workload === 'aggregate' && ![8, 16, 64].includes(scenario.requestedSpeed!))) {
      throw new Error('Integrated qualification speed does not match its presentation workload.');
    }
  }
  const destinations = new Set<string>();
  for (const amenity of scenario.amenities) {
    if (!amenity || typeof amenity.id !== 'string' || !amenity.id || typeof amenity.nodeId !== 'string' || !amenity.nodeId
      || typeof amenity.label !== 'string' || !amenity.label || !finiteNonnegative(amenity.priceCents)
      || !finiteNonnegative(amenity.capacityPerHour) || !finiteNonnegative(amenity.inventory)
      || !finiteNonnegative(amenity.opens) || !finiteNonnegative(amenity.closes) || amenity.opens >= amenity.closes
      || !finiteNonnegative(amenity.accessSeconds) || !finiteNonnegative(amenity.serviceSeconds)
      || !['hunger', 'thirst'].includes(amenity.need) || !amenity.relief || Object.values(amenity.relief).some(value => !finiteNonnegative(value))) {
      throw new Error('Integrated benchmark amenity contract is invalid.');
    }
    destinations.add(amenity.nodeId);
  }
  if (destinations.size < 3) throw new Error('Integrated benchmark requires three distinct amenity destinations.');
  return scenario as IntegratedBenchmarkScenario;
}

export function installedIntegratedBenchmarkScenario(
  identity: { saveKey?: string | null; terrainKey?: string | null },
): IntegratedBenchmarkScenario | null {
  const candidate = (globalThis as typeof globalThis & Record<string, unknown>)[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL];
  if (candidate === undefined) return null;
  const scenario = validateIntegratedBenchmarkScenario(candidate);
  return scenario.saveKey === identity.saveKey && scenario.terrainKey === identity.terrainKey ? scenario : null;
}

/** Explicit packaged-Electron bootstrap; a normal launch has neither query. */
export function installIntegratedBenchmarkBootstrap(search: string): void {
  const query = new URLSearchParams(search);
  const encoded = query.get(INTEGRATED_BENCHMARK_SCENARIO_QUERY);
  if (!encoded) return;
  try {
    const base64 = encoded.replaceAll('-', '+').replaceAll('_', '/').padEnd(Math.ceil(encoded.length / 4) * 4, '=');
    const binary = atob(base64);
    const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
    const scenario = validateIntegratedBenchmarkScenario(JSON.parse(new TextDecoder().decode(bytes)));
    const target = globalThis as typeof globalThis & Record<string, unknown>;
    target[INTEGRATED_BENCHMARK_SCENARIO_GLOBAL] = scenario;
    if (query.get(INTEGRATED_BENCHMARK_TELEMETRY_QUERY) === '1') {
      target.__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__ = { enabled: true, maxEntries: 20_000 };
    }
  } catch (error) {
    throw new Error(`Invalid integrated benchmark bootstrap: ${error instanceof Error ? error.message : 'decode failed'}`);
  }
}
