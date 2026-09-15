export interface BenchmarkCorrelation {
  readonly requestId: number;
  readonly generation: number;
  readonly operationGeneration: number;
  readonly committedRevision: number;
  readonly publicationSequence?: number;
}

export interface BenchmarkTelemetryEntry extends Partial<BenchmarkCorrelation> {
  readonly stage: string;
  readonly at: number;
  readonly durationMs?: number;
  readonly bytes?: number;
  readonly count?: number;
  readonly detail?: string;
  readonly terrainGeneration?: number;
  readonly terrainKey?: string;
  readonly realm?: 'main' | 'worker';
}

export interface BenchmarkTelemetrySnapshot {
  readonly version: 1;
  readonly entries: readonly BenchmarkTelemetryEntry[];
  readonly dropped: number;
  readonly truncated: boolean;
  readonly maxEntries: number;
}

interface BenchmarkTelemetryState {
  version: 1;
  entries: (BenchmarkTelemetryEntry | undefined)[];
  maxEntries: number;
  size: number;
  writeIndex: number;
  dropped: number;
}

const CONFIG_KEY = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__';
const STATE_KEY = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY_STATE__';
const correlations = new WeakMap<object, BenchmarkCorrelation>();

function config(): { enabled?: boolean; maxEntries?: number } | null {
  return (globalThis as typeof globalThis & Record<string, unknown>)[CONFIG_KEY] as { enabled?: boolean; maxEntries?: number } | null;
}

function configuredMaximum(): number {
  return Math.max(128, Math.min(20_000, config()?.maxEntries ?? 8_192));
}

function state(): BenchmarkTelemetryState {
  const target = globalThis as typeof globalThis & Record<string, unknown>;
  const maximum = configuredMaximum();
  let current = target[STATE_KEY] as BenchmarkTelemetryState | undefined;
  if (!current || current.maxEntries !== maximum) {
    current = { version: 1, entries: new Array(maximum), maxEntries: maximum, size: 0, writeIndex: 0, dropped: 0 };
    target[STATE_KEY] = current;
  }
  return current;
}

export function benchmarkTelemetryEnabled(): boolean { return config()?.enabled === true; }

/** Bind one accepted publication or derived render object to its immutable identity. */
export function bindBenchmarkCorrelation(value: object, correlation: BenchmarkCorrelation): void {
  if (benchmarkTelemetryEnabled()) correlations.set(value, correlation);
}

export function benchmarkCorrelationFor(value: object | null | undefined): BenchmarkCorrelation | null {
  return value && benchmarkTelemetryEnabled() ? correlations.get(value) ?? null : null;
}

export function markBenchmarkTelemetry(
  stage: string,
  fields: Partial<Omit<BenchmarkTelemetryEntry, 'stage' | 'at'>> = {},
): void {
  if (!benchmarkTelemetryEnabled()) return;
  const current = state();
  current.entries[current.writeIndex] = { stage, at: performance.now(), realm: 'main', ...fields };
  current.writeIndex = (current.writeIndex + 1) % current.maxEntries;
  if (current.size < current.maxEntries) current.size++;
  else current.dropped++;
}

export function benchmarkTelemetrySnapshot(): BenchmarkTelemetrySnapshot | null {
  if (!benchmarkTelemetryEnabled()) return null;
  const current = state();
  const first = current.size === current.maxEntries ? current.writeIndex : 0;
  const entries: BenchmarkTelemetryEntry[] = [];
  for (let index = 0; index < current.size; index++) {
    const entry = current.entries[(first + index) % current.maxEntries];
    if (entry) entries.push({ ...entry });
  }
  return { version: 1, entries, dropped: current.dropped, truncated: current.dropped > 0,
    maxEntries: current.maxEntries };
}
