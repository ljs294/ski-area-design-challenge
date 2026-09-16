import { afterEach, describe, expect, it } from 'vitest';
import { benchmarkCorrelationFor, benchmarkTelemetrySnapshot, bindBenchmarkCorrelation,
  markBenchmarkTelemetry, type BenchmarkCorrelation } from './integratedBenchmarkTelemetry';

const CONFIG = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__';
const STATE = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY_STATE__';
const globals = globalThis as typeof globalThis & Record<string, unknown>;

afterEach(() => { delete globals[CONFIG]; delete globals[STATE]; });

describe('integrated benchmark telemetry', () => {
  it('does not allocate state or retain correlations while disabled', () => {
    const publication = {};
    bindBenchmarkCorrelation(publication, { requestId: 1, generation: 1, operationGeneration: 1, committedRevision: 1 });
    markBenchmarkTelemetry('disabled');
    expect(globals[STATE]).toBeUndefined();
    expect(benchmarkCorrelationFor(publication)).toBeNull();
    expect(benchmarkTelemetrySnapshot()).toBeNull();
  });

  it('retains object-specific identities and reports ordered ring truncation', () => {
    globals[CONFIG] = { enabled: true, maxEntries: 128 };
    const first = {}, second = {};
    const firstCorrelation: BenchmarkCorrelation = {
      requestId: 4, generation: 2, operationGeneration: 3, committedRevision: 10, publicationSequence: 7,
    };
    const secondCorrelation: BenchmarkCorrelation = { ...firstCorrelation, requestId: 5, committedRevision: 11 };
    bindBenchmarkCorrelation(first, firstCorrelation);
    bindBenchmarkCorrelation(second, secondCorrelation);
    expect(benchmarkCorrelationFor(first)).toEqual(firstCorrelation);
    expect(benchmarkCorrelationFor(second)).toEqual(secondCorrelation);

    for (let index = 0; index < 130; index++) markBenchmarkTelemetry(`event-${index}`, { count: index });
    const snapshot = benchmarkTelemetrySnapshot()!;
    expect(snapshot.entries).toHaveLength(128);
    expect(snapshot.entries[0]?.stage).toBe('event-2');
    expect(snapshot.entries[127]?.stage).toBe('event-129');
    expect(snapshot.dropped).toBe(2);
    expect(snapshot.truncated).toBe(true);
  });
});
