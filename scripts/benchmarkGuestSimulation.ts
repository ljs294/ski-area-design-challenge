import * as os from 'node:os';
import { performance } from 'node:perf_hooks';

import {
  STANDARD_POPULATIONS,
  type KernelResult,
  buildCompactPointBuffer,
  buildGeoJsonSnapshot,
  checksumCompactPointBuffer,
  checksumGeoJsonSnapshot,
  chunkedGuestEventsByteLength,
  createChunkedGuestEvents,
  createGuestEventRecords,
  createIndexedGraph,
  createPointWorkload,
  createRouteQueries,
  createScheduledGuests,
  runChunkedSoAKernel,
  runCompactPointBufferKernel,
  runDirectMappedRouteCacheKernel,
  runGeoJsonSnapshotKernel,
  runLruRouteCacheKernel,
  runObjectRecordKernel,
  runStableBinaryHeapKernel,
  runTimingWheelKernel,
} from '../src/guestSimulation/benchmarkKernels.ts';

const DEFAULT_POPULATION = 1_000;
const DEFAULT_ITERATIONS = 5;
const DEFAULT_SEED = 0x51_1a_2026;

interface CliOptions {
  readonly populations: readonly number[];
  readonly iterations: number;
  readonly seed: number;
}

interface BenchmarkCase {
  readonly name: string;
  readonly run: () => KernelResult;
}

interface StorageMeasurement {
  readonly name: string;
  readonly measurementKind: 'exact-array-buffer' | 'serialized-utf8' | 'numeric-payload-lower-bound';
  readonly retainedBytes: number;
  readonly bytesPerGuest: number;
}

interface BenchmarkWorkload {
  readonly cases: readonly BenchmarkCase[];
  readonly storageMeasurements: readonly StorageMeasurement[];
}

interface TimedCase {
  readonly name: string;
  readonly warmup: number;
  readonly samples: number;
  readonly medianMs: number;
  readonly p95Ms: number;
  readonly p99Ms: number;
  readonly throughputOperationsPerSecond: number;
  readonly memoryDeltaBytes: number;
  readonly checksum: number;
  readonly operations: number;
  readonly measurement: true;
}

function parsePositiveInteger(value: string, option: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < 1) {
    throw new Error(`${option} must be a positive safe integer; received ${value}`);
  }
  return parsed;
}

function parseSeed(value: string): number {
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed)) {
    throw new Error(`--seed must be a safe integer; received ${value}`);
  }
  return parsed;
}

function parseCli(argv: readonly string[]): CliOptions {
  let explicitPopulation: number | undefined;
  let preset: string | undefined;
  let iterations = DEFAULT_ITERATIONS;
  let seed = DEFAULT_SEED;

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === '--help' || argument === '-h') {
      console.log(
        [
          'Phase 0 guest-simulation benchmark (measurements only; not release promises)',
          '',
          'Options:',
          '  --population N   one fixture size (default: 1000)',
          '  --iterations N   timed samples per case (default: 5)',
          '  --seed N         deterministic fixture seed',
          '  --preset standard  run 1k, 10k, 25k, and 50k fixtures',
        ].join('\n'),
      );
      process.exit(0);
    }
    const next = (): string => {
      const value = argv[index + 1];
      if (value === undefined) throw new Error(`${argument} requires a value`);
      index += 1;
      return value;
    };
    if (argument === '--population') {
      explicitPopulation = parsePositiveInteger(next(), '--population');
    } else if (argument.startsWith('--population=')) {
      explicitPopulation = parsePositiveInteger(argument.slice('--population='.length), '--population');
    } else if (argument === '--iterations') {
      iterations = parsePositiveInteger(next(), '--iterations');
    } else if (argument.startsWith('--iterations=')) {
      iterations = parsePositiveInteger(argument.slice('--iterations='.length), '--iterations');
    } else if (argument === '--seed') {
      seed = parseSeed(next());
    } else if (argument.startsWith('--seed=')) {
      seed = parseSeed(argument.slice('--seed='.length));
    } else if (argument === '--preset') {
      preset = next().toLowerCase();
    } else if (argument.startsWith('--preset=')) {
      preset = argument.slice('--preset='.length).toLowerCase();
    } else {
      throw new Error(`unknown option ${argument}`);
    }
  }

  if (explicitPopulation !== undefined) return { populations: [explicitPopulation], iterations, seed };
  if (preset === undefined || preset === 'default' || preset === 'quick') {
    return { populations: [DEFAULT_POPULATION], iterations, seed };
  }
  if (preset === 'standard' || preset === 'all') {
    return { populations: [...STANDARD_POPULATIONS], iterations, seed };
  }
  throw new Error(`unknown preset ${preset}; use standard`);
}

function median(values: readonly number[]): number {
  const sorted = [...values].sort((left, right) => left - right);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 0
    ? (sorted[middle - 1] + sorted[middle]) / 2
    : sorted[middle];
}

function percentile(values: readonly number[], percentileValue: number): number {
  const sorted = [...values].sort((left, right) => left - right);
  if (sorted.length === 1) return sorted[0];
  const position = (sorted.length - 1) * percentileValue;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  if (lower === upper) return sorted[lower];
  return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

function roundMeasurement(value: number): number {
  return Number(value.toFixed(4));
}

function maybeCollectGarbage(): void {
  const candidate = (globalThis as typeof globalThis & { gc?: () => void }).gc;
  if (typeof candidate === 'function') candidate();
}

function runTimedCase(
  benchmarkCase: BenchmarkCase,
  expectedChecksum: number,
  sampleCount: number,
): TimedCase {
  const warmup = 2;
  for (let index = 0; index < warmup; index += 1) {
    const result = benchmarkCase.run();
    if (result.checksum !== expectedChecksum) {
      throw new Error(`${benchmarkCase.name} changed checksum during warmup`);
    }
  }

  const durations: number[] = [];
  const memoryDeltas: number[] = [];
  let checksum = expectedChecksum;
  let operations = 0;
  for (let index = 0; index < sampleCount; index += 1) {
    maybeCollectGarbage();
    const before = process.memoryUsage().heapUsed;
    const started = performance.now();
    const result = benchmarkCase.run();
    const elapsed = performance.now() - started;
    const after = process.memoryUsage().heapUsed;
    if (result.checksum !== expectedChecksum) {
      throw new Error(`${benchmarkCase.name} is not deterministic across samples`);
    }
    checksum = result.checksum;
    operations = result.operations;
    durations.push(elapsed);
    memoryDeltas.push(after - before);
  }
  const medianMs = median(durations);
  return {
    name: benchmarkCase.name,
    warmup,
    samples: sampleCount,
    medianMs: roundMeasurement(medianMs),
    p95Ms: roundMeasurement(percentile(durations, 0.95)),
    p99Ms: roundMeasurement(percentile(durations, 0.99)),
    throughputOperationsPerSecond:
      medianMs > 0 ? roundMeasurement((operations * 1_000) / medianMs) : 0,
    memoryDeltaBytes: Math.round(median(memoryDeltas)),
    checksum,
    operations,
    measurement: true,
  };
}

function assertEquivalent(label: string, left: KernelResult, right: KernelResult): void {
  if (left.checksum !== right.checksum || left.operations !== right.operations) {
    throw new Error(`${label} kernels disagree (checksum or operation count)`);
  }
}

function makeWorkload(population: number, seed: number): BenchmarkWorkload {
  const records = createGuestEventRecords(population, seed);
  const chunked = createChunkedGuestEvents(records);
  const schedule = createScheduledGuests(population, seed + 1);
  const nodeCount = Math.max(32, Math.min(512, Math.ceil(Math.sqrt(population) * 2)));
  const graph = createIndexedGraph(nodeCount, seed + 2);
  const routeQueries = createRouteQueries(population, nodeCount, seed + 3);
  const routeCapacity = Math.max(8, Math.min(256, Math.ceil(Math.sqrt(population))));
  const points = createPointWorkload(population, seed + 4);
  const geoJson = buildGeoJsonSnapshot(points);
  const compactPoints = buildCompactPointBuffer(points);

  const eventIterations = 4;
  const cases: BenchmarkCase[] = [
    {
      name: 'object-records',
      run: () => runObjectRecordKernel(records, eventIterations),
    },
    {
      name: 'chunked-structure-of-arrays',
      run: () => runChunkedSoAKernel(chunked, eventIterations),
    },
    {
      name: 'stable-binary-heap',
      run: () => runStableBinaryHeapKernel(schedule),
    },
    {
      name: 'simple-timing-wheel',
      run: () => runTimingWheelKernel(schedule),
    },
    {
      name: 'lru-route-cache',
      run: () => runLruRouteCacheKernel(graph, routeQueries, routeCapacity),
    },
    {
      name: 'direct-mapped-route-cache',
      run: () => runDirectMappedRouteCacheKernel(graph, routeQueries, routeCapacity),
    },
    {
      name: 'geojson-snapshot',
      run: () => runGeoJsonSnapshotKernel(points),
    },
    {
      name: 'compact-point-buffer',
      run: () => runCompactPointBufferKernel(points),
    },
  ];

  assertEquivalent('object-records/SoA', cases[0].run(), cases[1].run());
  assertEquivalent('heap/timing-wheel', cases[2].run(), cases[3].run());
  assertEquivalent('LRU/direct-mapped route cache', cases[4].run(), cases[5].run());
  assertEquivalent('GeoJSON/compact point buffer', cases[6].run(), cases[7].run());

  // Exercise the explicit snapshot builders as part of the harness contract;
  // the timed kernels below still include construction and checksum traversal.
  if (checksumGeoJsonSnapshot(geoJson) !== checksumCompactPointBuffer(compactPoints)) {
    throw new Error('snapshot builders disagree');
  }
  const objectNumericLowerBound = records.length * 6 * Float64Array.BYTES_PER_ELEMENT;
  const geoJsonSerializedBytes = Buffer.byteLength(JSON.stringify(geoJson), 'utf8');
  const compactBytes = compactPoints.ids.byteLength + compactPoints.positions.byteLength;
  return {
    cases,
    storageMeasurements: [
      { name: 'object-records', measurementKind: 'numeric-payload-lower-bound',
        retainedBytes: objectNumericLowerBound, bytesPerGuest: objectNumericLowerBound / population },
      { name: 'chunked-structure-of-arrays', measurementKind: 'exact-array-buffer',
        retainedBytes: chunkedGuestEventsByteLength(chunked), bytesPerGuest: chunkedGuestEventsByteLength(chunked) / population },
      { name: 'geojson-snapshot', measurementKind: 'serialized-utf8',
        retainedBytes: geoJsonSerializedBytes, bytesPerGuest: geoJsonSerializedBytes / population },
      { name: 'compact-point-buffer', measurementKind: 'exact-array-buffer',
        retainedBytes: compactBytes, bytesPerGuest: compactBytes / population },
    ],
  };
}

function benchmarkFixture(population: number, options: CliOptions): object {
  const workload = makeWorkload(population, options.seed);
  const expected = new Map<string, number>();
  for (const benchmarkCase of workload.cases) expected.set(benchmarkCase.name, benchmarkCase.run().checksum);
  const measurements = workload.cases.map((benchmarkCase) =>
    runTimedCase(benchmarkCase, expected.get(benchmarkCase.name) ?? 0, options.iterations),
  );
  return {
    population,
    seed: options.seed,
    timedSampleCount: options.iterations,
    timingScope: 'processing-and-checksum; memoryDeltaBytes is noisy process telemetry, not retained representation size',
    storageMeasurements: workload.storageMeasurements,
    measurements,
  };
}

function main(): void {
  const options = parseCli(process.argv.slice(2));
  const cpu = os.cpus()[0];
  const report = {
    measurementNotice:
      'Phase 0 measurements only. Results are machine/configuration dependent and are not release promises.',
    metadata: {
      runtime: 'node',
      nodeVersion: process.version,
      v8Version: process.versions.v8,
      platform: process.platform,
      architecture: process.arch,
      cpuCount: os.cpus().length,
      cpuModel: cpu?.model ?? 'unknown',
      totalMemoryBytes: os.totalmem(),
    },
    options: {
      populations: options.populations,
      iterations: options.iterations,
      seed: options.seed,
    },
    fixtures: options.populations.map((population) => benchmarkFixture(population, options)),
    deferred: [
      {
        name: 'packaged-electron-benchmark',
        measured: false,
        reason:
          'Deferred: package integration belongs to the root integration owner. This harness intentionally runs headless Node kernels only.',
      },
    ],
  };
  console.log(JSON.stringify(report, null, 2));
}

main();
