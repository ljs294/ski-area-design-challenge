import {
  createIndexedGraph,
  createRouteQueries,
  createSeededRandom,
  findIndexedRoute,
} from './benchmarkKernels.ts';
import {
  checksumGuestPublication,
  packGuestPublication,
  type GuestPublicationRow,
} from './phase7Publication.ts';
import { IntrusiveFifoQueue } from './phase7Queue.ts';
import { DeterministicRouteCache } from './phase7RouteCache.ts';
import {
  selectGuestPublicationRows,
  type GuestViewport,
} from './phase7Policy.ts';

export const PHASE7_STANDARD_POPULATIONS = [1_000, 10_000, 25_000, 50_000] as const;

export interface Phase7KernelResult {
  readonly name: string;
  readonly population: number;
  readonly checksum: number;
  readonly operations: number;
  readonly bytes: number;
  readonly cacheHits?: number;
  readonly cacheMisses?: number;
  readonly cacheEvictions?: number;
}

function positiveInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 1) throw new RangeError(`${name} must be a positive safe integer`);
  return value;
}

function mix(hash: number, value: number): number {
  return Math.imul((hash ^ (value >>> 0)) >>> 0, 16_777_619) >>> 0;
}

export function createPhase7PublicationRows(population: number, seed = 1): GuestPublicationRow[] {
  positiveInteger(population, 'population');
  const random = createSeededRandom(seed);
  const rows = new Array<GuestPublicationRow>(population);
  for (let guestId = 0; guestId < population; guestId += 1) {
    rows[guestId] = {
      guestId,
      x: Math.fround(Math.round((random() * 2_000 - 1_000) * 1_000) / 1_000),
      y: Math.fround(Math.round((random() * 2_000 - 1_000) * 1_000) / 1_000),
      elevation: Math.fround(Math.round((random() * 2_000 - 500) * 1_000) / 1_000),
      statusCode: Math.floor(random() * 8),
      satisfaction: Math.floor(random() * 256),
      flags: Math.floor(random() * 8),
    };
  }
  return rows;
}

export function runPublicationSlabKernel(rows: readonly GuestPublicationRow[]): Phase7KernelResult {
  const slab = packGuestPublication(rows, {
    tick: 1_024,
    sequence: 2,
    environmentRevision: 3,
    topologyRevision: 4,
  });
  return {
    name: 'compact-publication-slab',
    population: rows.length,
    checksum: checksumGuestPublication(slab),
    operations: rows.length,
    bytes: slab.byteLength,
  };
}

export function runIntrusiveQueueKernel(population: number): Phase7KernelResult {
  positiveInteger(population, 'population');
  const queue = new IntrusiveFifoQueue(population);
  for (let node = 0; node < population; node += 1) queue.enqueue(node);
  let checksum = 2_166_136_261;
  let processed = 0;
  queue.drain((node) => {
    checksum = mix(checksum, node);
    processed += 1;
  });
  if (processed !== population || queue.size !== 0) throw new Error('intrusive queue failed conservation check');
  return {
    name: 'intrusive-fifo-queue',
    population,
    checksum,
    operations: population * 2,
    bytes: population * (Int32Array.BYTES_PER_ELEMENT * 2 + Uint8Array.BYTES_PER_ELEMENT),
  };
}

export function runRouteCacheKernel(population: number, seed = 1): Phase7KernelResult {
  positiveInteger(population, 'population');
  const nodeCount = Math.max(32, Math.min(512, Math.ceil(Math.sqrt(population) * 2)));
  const graph = createIndexedGraph(nodeCount, seed);
  const queries = createRouteQueries(population, nodeCount, seed + 1);
  const capacity = Math.max(8, Math.min(256, Math.ceil(Math.sqrt(population))));
  const cache = new DeterministicRouteCache(capacity, 1);
  let checksum = 2_166_136_261;
  for (const query of queries) {
    const route = cache.getOrCompute(query.start, query.goal, 1, () => findIndexedRoute(graph, query.start, query.goal));
    checksum = mix(checksum, query.start);
    checksum = mix(checksum, query.goal);
    checksum = mix(checksum, route.length);
    for (const node of route) checksum = mix(checksum, node);
  }
  const stats = cache.getStats();
  if (stats.size > capacity) throw new Error('route cache exceeded configured capacity');
  return {
    name: 'bounded-deterministic-route-cache',
    population,
    checksum,
    operations: population,
    bytes: capacity * (Float64Array.BYTES_PER_ELEMENT * 3 + Int32Array.BYTES_PER_ELEMENT * 2 + Uint8Array.BYTES_PER_ELEMENT),
    cacheHits: stats.hits,
    cacheMisses: stats.misses,
    cacheEvictions: stats.evictions,
  };
}

export function runViewportSelectionKernel(rows: readonly GuestPublicationRow[], tick = 1_024): Phase7KernelResult {
  const viewport: GuestViewport = { minX: -500, minY: -500, maxX: 500, maxY: 500 };
  const selection = selectGuestPublicationRows(rows, viewport, Math.max(1, Math.ceil(Math.sqrt(rows.length) * 32)), tick);
  let checksum = 2_166_136_261;
  for (const index of selection.selectedIndices) checksum = mix(checksum, rows[index].guestId);
  return {
    name: 'viewport-cull-and-deterministic-sample',
    population: rows.length,
    checksum,
    operations: rows.length,
    bytes: selection.selectedIndices.byteLength,
  };
}

export function runPhase7ScalingKernels(population: number, seed = 1): Phase7KernelResult[] {
  positiveInteger(population, 'population');
  const rows = createPhase7PublicationRows(population, seed);
  return [
    runPublicationSlabKernel(rows),
    runViewportSelectionKernel(rows),
    runIntrusiveQueueKernel(population),
    runRouteCacheKernel(population, seed + 2),
  ];
}

/** Stable fixture checksums used by the headless benchmark harness. */
export function checksumPhase7Fixture(population: number, seed = 1): number {
  let checksum = 2_166_136_261;
  for (const result of runPhase7ScalingKernels(population, seed)) {
    checksum = mix(checksum, result.checksum);
    checksum = mix(checksum, result.operations);
  }
  return checksum;
}
