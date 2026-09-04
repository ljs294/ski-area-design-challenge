/**
 * Headless Phase 7 scaling benchmark.  This intentionally exercises only
 * deterministic kernels; it does not start Electron, a browser, or a worker.
 * Run with: node --experimental-strip-types scripts/benchmarkPhase7Scaling.ts --preset standard
 */
import * as os from 'node:os';
import { performance } from 'node:perf_hooks';

import {
  PHASE7_STANDARD_POPULATIONS,
  createPhase7PublicationRows,
  runIntrusiveQueueKernel,
  runPublicationSlabKernel,
  runRouteCacheKernel,
  runViewportSelectionKernel,
  type Phase7KernelResult,
} from '../src/guestSimulation/phase7Benchmark.ts';

const DEFAULT_POPULATION = 1_000;
const DEFAULT_ITERATIONS = 3;
const DEFAULT_SEED = 0x51_1a_2026;

interface Options {
  readonly populations: readonly number[];
  readonly iterations: number;
  readonly seed: number;
}

interface TimedMeasurement {
  readonly name: string;
  readonly population: number;
  readonly warmup: number;
  readonly samples: number;
  readonly medianMs: number;
  readonly p95Ms: number;
  readonly throughputOperationsPerSecond: number;
  readonly memoryDeltaBytes: number;
  readonly checksum: number;
  readonly operations: number;
  readonly bytes: number;
  readonly cacheHits?: number;
  readonly cacheMisses?: number;
  readonly cacheEvictions?: number;
}

function positiveInteger(value: string, name: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1) throw new Error(`${name} must be a positive safe integer`);
  return parsed;
}

function parseSeed(value: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) throw new Error('--seed must be a safe integer');
  return parsed;
}

function parseOptions(argv: readonly string[]): Options {
  let population: number | undefined;
  let preset: string | undefined;
  let iterations = DEFAULT_ITERATIONS;
  let seed = DEFAULT_SEED;
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    const next = (): string => {
      const value = argv[index + 1];
      if (value === undefined) throw new Error(`${argument} requires a value`);
      index += 1;
      return value;
    };
    if (argument === '--help' || argument === '-h') {
      console.log('Options: --population N | --preset standard | --iterations N | --seed N');
      process.exit(0);
    } else if (argument === '--population') population = positiveInteger(next(), '--population');
    else if (argument.startsWith('--population=')) population = positiveInteger(argument.slice(13), '--population');
    else if (argument === '--preset') preset = next().toLowerCase();
    else if (argument.startsWith('--preset=')) preset = argument.slice(9).toLowerCase();
    else if (argument === '--iterations') iterations = positiveInteger(next(), '--iterations');
    else if (argument.startsWith('--iterations=')) iterations = positiveInteger(argument.slice(13), '--iterations');
    else if (argument === '--seed') seed = parseSeed(next());
    else if (argument.startsWith('--seed=')) seed = parseSeed(argument.slice(7));
    else throw new Error(`unknown option ${argument}`);
  }
  if (population !== undefined) return { populations: [population], iterations, seed };
  if (preset === undefined || preset === 'default' || preset === 'quick') {
    return { populations: [DEFAULT_POPULATION], iterations, seed };
  }
  if (preset === 'standard' || preset === 'all') return { populations: [...PHASE7_STANDARD_POPULATIONS], iterations, seed };
  throw new Error(`unknown preset ${preset}`);
}

function percentile(values: readonly number[], fraction: number): number {
  const sorted = [...values].sort((left, right) => left - right);
  const position = (sorted.length - 1) * fraction;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  return lower === upper ? sorted[lower] : sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function median(values: readonly number[]): number {
  return percentile(values, 0.5);
}

function round(value: number): number {
  return Number(value.toFixed(4));
}

function collectGc(): void {
  const candidate = (globalThis as typeof globalThis & { gc?: () => void }).gc;
  if (typeof candidate === 'function') candidate();
}

function timeCase(
  name: string,
  population: number,
  run: () => Phase7KernelResult,
  iterations: number,
): TimedMeasurement {
  const warmup = 1;
  const expected = run();
  for (let index = 0; index < warmup; index += 1) {
    if (run().checksum !== expected.checksum) throw new Error(`${name} changed checksum during warmup`);
  }
  const durations: number[] = [];
  const memoryDeltas: number[] = [];
  let latest = expected;
  for (let index = 0; index < iterations; index += 1) {
    collectGc();
    const before = process.memoryUsage().heapUsed;
    const started = performance.now();
    latest = run();
    durations.push(performance.now() - started);
    memoryDeltas.push(process.memoryUsage().heapUsed - before);
    if (latest.checksum !== expected.checksum) throw new Error(`${name} is not deterministic across samples`);
  }
  const medianMs = median(durations);
  return {
    name,
    population,
    warmup,
    samples: iterations,
    medianMs: round(medianMs),
    p95Ms: round(percentile(durations, 0.95)),
    throughputOperationsPerSecond: medianMs > 0 ? round((latest.operations * 1_000) / medianMs) : 0,
    memoryDeltaBytes: Math.round(median(memoryDeltas)),
    checksum: latest.checksum,
    operations: latest.operations,
    bytes: latest.bytes,
    ...(latest.cacheHits === undefined ? {} : { cacheHits: latest.cacheHits }),
    ...(latest.cacheMisses === undefined ? {} : { cacheMisses: latest.cacheMisses }),
    ...(latest.cacheEvictions === undefined ? {} : { cacheEvictions: latest.cacheEvictions }),
  };
}

function benchmarkPopulation(population: number, options: Options): object {
  const rows = createPhase7PublicationRows(population, options.seed + population);
  const cases: Array<{ readonly name: string; readonly run: () => Phase7KernelResult }> = [
    { name: 'compact-publication-slab', run: () => runPublicationSlabKernel(rows) },
    { name: 'viewport-cull-and-deterministic-sample', run: () => runViewportSelectionKernel(rows) },
    { name: 'intrusive-fifo-queue', run: () => runIntrusiveQueueKernel(population) },
    { name: 'bounded-deterministic-route-cache', run: () => runRouteCacheKernel(population, options.seed + population + 1) },
  ];
  return {
    population,
    seed: options.seed + population,
    measurements: cases.map((entry) => timeCase(entry.name, population, entry.run, options.iterations)),
  };
}

function main(): void {
  const options = parseOptions(process.argv.slice(2));
  const cpu = os.cpus()[0];
  console.log(JSON.stringify({
    measurementNotice: 'Phase 7 headless measurements only; timings are machine/configuration dependent.',
    metadata: {
      runtime: 'node',
      nodeVersion: process.version,
      platform: process.platform,
      architecture: process.arch,
      cpuModel: cpu?.model ?? 'unknown',
      cpuCount: os.cpus().length,
    },
    options,
    fixtureContract: {
      populations: [1_000, 10_000, 25_000, 50_000],
      authoritativeShardingChanged: false,
      sharedArrayBuffer: false,
      wasm: false,
    },
    fixtures: options.populations.map((population) => benchmarkPopulation(population, options)),
  }, null, 2));
}

main();

