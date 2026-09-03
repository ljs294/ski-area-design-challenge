/**
 * Phase 0 guest-simulation benchmark kernels.
 *
 * These kernels deliberately have no browser, MapLibre, or Electron
 * dependencies.  They are small, deterministic workloads used to compare
 * representation choices.  The numbers produced by the benchmark harness
 * are measurements for a machine/configuration, not release promises.
 */

export const STANDARD_POPULATIONS = [1_000, 10_000, 25_000, 50_000] as const;

export interface KernelResult {
  readonly checksum: number;
  readonly operations: number;
}

export interface GuestEventRecord {
  readonly guestId: number;
  readonly groupId: number;
  readonly routeId: number;
  readonly arrivalTick: number;
  readonly partySize: number;
  readonly priority: number;
}

export interface GuestEventChunk {
  readonly length: number;
  readonly guestId: Uint32Array;
  readonly groupId: Uint32Array;
  readonly routeId: Uint32Array;
  readonly arrivalTick: Uint32Array;
  readonly partySize: Uint16Array;
  readonly priority: Uint8Array;
}

export interface ChunkedGuestEvents {
  readonly length: number;
  readonly chunkSize: number;
  readonly chunks: readonly GuestEventChunk[];
}

export interface ScheduledGuest {
  readonly dueTick: number;
  readonly sequence: number;
  readonly guestId: number;
  readonly routeId: number;
}

export interface IndexedGraph {
  readonly nodeCount: number;
  /** CSR-style offsets. Neighbors for node n are [offsets[n], offsets[n + 1]). */
  readonly offsets: Int32Array;
  readonly neighbors: Int32Array;
}

export interface RouteQuery {
  readonly start: number;
  readonly goal: number;
}

export interface RouteCacheResult extends KernelResult {
  readonly hits: number;
  readonly misses: number;
  readonly capacity: number;
}

export interface TerrainPoint {
  readonly id: number;
  readonly x: number;
  readonly y: number;
  readonly elevation: number;
}

export interface GeoJsonPointFeature {
  readonly type: 'Feature';
  readonly id: number;
  readonly geometry: {
    readonly type: 'Point';
    readonly coordinates: readonly [number, number];
  };
  readonly properties: {
    readonly elevation: number;
  };
}

export interface GeoJsonSnapshot {
  readonly type: 'FeatureCollection';
  readonly features: readonly GeoJsonPointFeature[];
}

export interface CompactPointBuffer {
  readonly ids: Uint32Array;
  /** Packed x, y, elevation triples consumed directly by the custom layer. */
  readonly positions: Float32Array;
  readonly bytesPerGuest: 16;
}

function positiveInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 1) {
    throw new RangeError(`${name} must be a positive safe integer`);
  }
  return value;
}

function nonNegativeInteger(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${name} must be a non-negative safe integer`);
  }
  return value;
}

/** A tiny seeded generator is enough for stable fixture construction. */
export function createSeededRandom(seed: number): () => number {
  let state = (seed >>> 0) || 0x9e37_79b9;
  return () => {
    state ^= state << 13;
    state ^= state >>> 17;
    state ^= state << 5;
    state >>>= 0;
    return state / 0x1_0000_0000;
  };
}

function mixHash(hash: number, value: number): number {
  return Math.imul((hash ^ (value >>> 0)) >>> 0, 16_777_619) >>> 0;
}

function hashNumber(hash: number, value: number): number {
  // Fixtures use bounded coordinates/integers. Quantizing here makes the
  // comparison explicit and avoids representation-specific stringification.
  return mixHash(hash, Math.round(value * 1_000_000));
}

function eventValue(
  guestId: number,
  groupId: number,
  routeId: number,
  arrivalTick: number,
  partySize: number,
  priority: number,
  step: number,
): number {
  const tick = arrivalTick + step * 3;
  const shiftedGuest = guestId + step;
  return (
    Math.imul(shiftedGuest, 31) ^
    Math.imul(groupId + step * 7, 17) ^
    Math.imul(routeId + step * 11, 13) ^
    Math.imul(tick, 5) ^
    Math.imul(partySize, 3) ^
    priority
  ) >>> 0;
}

export function createGuestEventRecords(
  population: number,
  seed = 1,
): GuestEventRecord[] {
  positiveInteger(population, 'population');
  const random = createSeededRandom(seed);
  const records = new Array<GuestEventRecord>(population);
  for (let guestId = 0; guestId < population; guestId += 1) {
    records[guestId] = {
      guestId,
      groupId: Math.floor(random() * Math.max(1, population / 8)),
      routeId: Math.floor(random() * 128),
      arrivalTick: Math.floor(random() * 4_096),
      partySize: 1 + Math.floor(random() * 6),
      priority: Math.floor(random() * 8),
    };
  }
  return records;
}

export function createChunkedGuestEvents(
  records: readonly GuestEventRecord[],
  chunkSize = 4_096,
): ChunkedGuestEvents {
  positiveInteger(chunkSize, 'chunkSize');
  const chunks: GuestEventChunk[] = [];
  for (let offset = 0; offset < records.length; offset += chunkSize) {
    const length = Math.min(chunkSize, records.length - offset);
    const chunk: GuestEventChunk = {
      length,
      guestId: new Uint32Array(length),
      groupId: new Uint32Array(length),
      routeId: new Uint32Array(length),
      arrivalTick: new Uint32Array(length),
      partySize: new Uint16Array(length),
      priority: new Uint8Array(length),
    };
    for (let index = 0; index < length; index += 1) {
      const record = records[offset + index];
      chunk.guestId[index] = record.guestId;
      chunk.groupId[index] = record.groupId;
      chunk.routeId[index] = record.routeId;
      chunk.arrivalTick[index] = record.arrivalTick;
      chunk.partySize[index] = record.partySize;
      chunk.priority[index] = record.priority;
    }
    chunks.push(chunk);
  }
  return { length: records.length, chunkSize, chunks };
}

export function chunkedGuestEventsByteLength(events: ChunkedGuestEvents): number {
  return events.chunks.reduce((total, chunk) => total + chunk.guestId.byteLength
    + chunk.groupId.byteLength + chunk.routeId.byteLength + chunk.arrivalTick.byteLength
    + chunk.partySize.byteLength + chunk.priority.byteLength, 0);
}

export function runObjectRecordKernel(
  records: readonly GuestEventRecord[],
  iterations = 1,
): KernelResult {
  positiveInteger(iterations, 'iterations');
  let checksum = 2_166_136_261;
  for (let step = 0; step < iterations; step += 1) {
    for (const record of records) {
      checksum = mixHash(
        checksum,
        eventValue(
          record.guestId,
          record.groupId,
          record.routeId,
          record.arrivalTick,
          record.partySize,
          record.priority,
          step,
        ),
      );
    }
  }
  return { checksum, operations: records.length * iterations };
}

export function runChunkedSoAKernel(
  events: ChunkedGuestEvents,
  iterations = 1,
): KernelResult {
  positiveInteger(iterations, 'iterations');
  let logicalLength = 0;
  for (const chunk of events.chunks) {
    if (!Number.isSafeInteger(chunk.length) || chunk.length <= 0) throw new RangeError('guest event chunk length must be a positive integer');
    if (chunk.length > events.chunkSize) throw new RangeError('guest event chunk exceeds configured chunk size');
    logicalLength += chunk.length;
  }
  if (logicalLength !== events.length) throw new RangeError('guest event aggregate length is inconsistent');
  let checksum = 2_166_136_261;
  for (let step = 0; step < iterations; step += 1) {
    for (const chunk of events.chunks) {
      if (chunk.length !== chunk.guestId.length || chunk.groupId.length !== chunk.length
        || chunk.routeId.length !== chunk.length || chunk.arrivalTick.length !== chunk.length
        || chunk.partySize.length !== chunk.length || chunk.priority.length !== chunk.length) {
        throw new RangeError('all guest event chunk columns must match its logical length');
      }
      for (let index = 0; index < chunk.length; index += 1) {
        checksum = mixHash(
          checksum,
          eventValue(
            chunk.guestId[index],
            chunk.groupId[index],
            chunk.routeId[index],
            chunk.arrivalTick[index],
            chunk.partySize[index],
            chunk.priority[index],
            step,
          ),
        );
      }
    }
  }
  return { checksum, operations: events.length * iterations };
}

function compareScheduled(left: ScheduledGuest, right: ScheduledGuest): number {
  return left.dueTick - right.dueTick || left.sequence - right.sequence;
}

export class StableBinaryHeap {
  private readonly items: ScheduledGuest[] = [];

  get size(): number {
    return this.items.length;
  }

  push(item: ScheduledGuest): void {
    let index = this.items.push(item) - 1;
    while (index > 0) {
      const parent = Math.floor((index - 1) / 2);
      if (compareScheduled(this.items[parent], this.items[index]) <= 0) break;
      [this.items[parent], this.items[index]] = [this.items[index], this.items[parent]];
      index = parent;
    }
  }

  pop(): ScheduledGuest | undefined {
    if (this.items.length === 0) return undefined;
    const first = this.items[0];
    const last = this.items.pop();
    if (this.items.length > 0 && last !== undefined) {
      this.items[0] = last;
      let index = 0;
      while (true) {
        const left = index * 2 + 1;
        const right = left + 1;
        let smallest = index;
        if (
          left < this.items.length &&
          compareScheduled(this.items[left], this.items[smallest]) < 0
        ) {
          smallest = left;
        }
        if (
          right < this.items.length &&
          compareScheduled(this.items[right], this.items[smallest]) < 0
        ) {
          smallest = right;
        }
        if (smallest === index) break;
        [this.items[index], this.items[smallest]] = [
          this.items[smallest],
          this.items[index],
        ];
        index = smallest;
      }
    }
    return first;
  }
}

/** A deliberately simple wheel candidate with sorted same-tick buckets. */
export class SimpleTimingWheel {
  private readonly buckets: ScheduledGuest[][];
  private readonly bucketCount: number;
  private maxDueTick = 0;
  private pending = 0;

  constructor(bucketCount = 256) {
    positiveInteger(bucketCount, 'bucketCount');
    this.bucketCount = bucketCount;
    this.buckets = Array.from({ length: bucketCount }, () => []);
  }

  schedule(item: ScheduledGuest): void {
    nonNegativeInteger(item.dueTick, 'dueTick');
    this.buckets[item.dueTick % this.bucketCount].push(item);
    this.maxDueTick = Math.max(this.maxDueTick, item.dueTick);
    this.pending += 1;
  }

  get size(): number {
    return this.pending;
  }

  drain(visit: (item: ScheduledGuest) => void): number {
    let drained = 0;
    for (let tick = 0; tick <= this.maxDueTick; tick += 1) {
      const bucketIndex = tick % this.bucketCount;
      const bucket = this.buckets[bucketIndex];
      if (bucket.length === 0) continue;
      const dueNow: ScheduledGuest[] = [];
      const future: ScheduledGuest[] = [];
      for (const item of bucket) {
        (item.dueTick === tick ? dueNow : future).push(item);
      }
      dueNow.sort((left, right) => left.sequence - right.sequence);
      for (const item of dueNow) {
        visit(item);
        drained += 1;
        this.pending -= 1;
      }
      this.buckets[bucketIndex] = future;
    }
    if (this.pending !== 0) {
      throw new Error('timing wheel failed to drain all scheduled guests');
    }
    return drained;
  }
}

export function createScheduledGuests(
  population: number,
  seed = 1,
  horizon = 4_096,
): ScheduledGuest[] {
  positiveInteger(population, 'population');
  positiveInteger(horizon, 'horizon');
  const random = createSeededRandom(seed);
  const events = new Array<ScheduledGuest>(population);
  for (let sequence = 0; sequence < population; sequence += 1) {
    events[sequence] = {
      dueTick: Math.floor(random() * horizon),
      sequence,
      guestId: sequence,
      routeId: Math.floor(random() * 128),
    };
  }
  return events;
}

function mixScheduled(hash: number, item: ScheduledGuest): number {
  hash = mixHash(hash, item.guestId);
  hash = mixHash(hash, item.routeId);
  hash = mixHash(hash, item.dueTick);
  return mixHash(hash, item.sequence);
}

export function runStableBinaryHeapKernel(
  events: readonly ScheduledGuest[],
): KernelResult {
  const heap = new StableBinaryHeap();
  for (const event of events) heap.push(event);
  let checksum = 2_166_136_261;
  let processed = 0;
  while (heap.size > 0) {
    const event = heap.pop();
    if (event === undefined) throw new Error('heap unexpectedly empty');
    checksum = mixScheduled(checksum, event);
    processed += 1;
  }
  return { checksum, operations: events.length * 2 };
}

export function runTimingWheelKernel(
  events: readonly ScheduledGuest[],
  bucketCount = 256,
): KernelResult {
  const wheel = new SimpleTimingWheel(bucketCount);
  for (const event of events) wheel.schedule(event);
  let checksum = 2_166_136_261;
  const processed = wheel.drain((event) => {
    checksum = mixScheduled(checksum, event);
  });
  if (processed !== events.length) throw new Error('wheel operation count mismatch');
  return { checksum, operations: events.length * 2 };
}

export function createIndexedGraph(nodeCount: number, seed = 1): IndexedGraph {
  positiveInteger(nodeCount, 'nodeCount');
  const degree = 4;
  const offsets = new Int32Array(nodeCount + 1);
  const neighbors = new Int32Array(nodeCount * degree);
  const random = createSeededRandom(seed);
  for (let node = 0; node < nodeCount; node += 1) {
    offsets[node] = node * degree;
    const base = node * degree;
    neighbors[base] = (node + 1) % nodeCount;
    neighbors[base + 1] = (node + nodeCount - 1) % nodeCount;
    neighbors[base + 2] = Math.floor(random() * nodeCount);
    neighbors[base + 3] = Math.floor(random() * nodeCount);
  }
  offsets[nodeCount] = neighbors.length;
  return { nodeCount, offsets, neighbors };
}

export function createRouteQueries(
  queryCount: number,
  nodeCount: number,
  seed = 1,
): RouteQuery[] {
  positiveInteger(queryCount, 'queryCount');
  positiveInteger(nodeCount, 'nodeCount');
  const random = createSeededRandom(seed);
  const poolSize = Math.min(512, Math.max(16, Math.ceil(Math.sqrt(queryCount))));
  const pool = new Array<RouteQuery>(poolSize);
  for (let index = 0; index < poolSize; index += 1) {
    const start = Math.floor(random() * nodeCount);
    const distance = 1 + Math.floor(random() * Math.max(1, nodeCount - 1));
    pool[index] = { start, goal: (start + distance) % nodeCount };
  }
  const queries = new Array<RouteQuery>(queryCount);
  for (let index = 0; index < queryCount; index += 1) {
    if (random() < 0.82) {
      queries[index] = pool[Math.floor(random() * pool.length)];
    } else {
      const start = Math.floor(random() * nodeCount);
      queries[index] = {
        start,
        goal: (start + 1 + Math.floor(random() * Math.max(1, nodeCount - 1))) % nodeCount,
      };
    }
  }
  return queries;
}

export function findIndexedRoute(
  graph: IndexedGraph,
  start: number,
  goal: number,
): Int32Array {
  if (
    !Number.isInteger(start) ||
    !Number.isInteger(goal) ||
    start < 0 ||
    goal < 0 ||
    start >= graph.nodeCount ||
    goal >= graph.nodeCount
  ) {
    throw new RangeError('route endpoints must be valid graph node indexes');
  }
  if (start === goal) return Int32Array.of(start);
  const visited = new Uint8Array(graph.nodeCount);
  const parent = new Int32Array(graph.nodeCount);
  parent.fill(-1);
  const queue = new Int32Array(graph.nodeCount);
  let head = 0;
  let tail = 0;
  queue[tail++] = start;
  visited[start] = 1;
  while (head < tail) {
    const node = queue[head++];
    for (let edge = graph.offsets[node]; edge < graph.offsets[node + 1]; edge += 1) {
      const next = graph.neighbors[edge];
      if (visited[next] !== 0) continue;
      visited[next] = 1;
      parent[next] = node;
      if (next === goal) {
        const reversed: number[] = [goal];
        let cursor = goal;
        while (cursor !== start) {
          cursor = parent[cursor];
          reversed.push(cursor);
        }
        reversed.reverse();
        return Int32Array.from(reversed);
      }
      queue[tail++] = next;
    }
  }
  return new Int32Array(0);
}

function routeKey(query: RouteQuery, nodeCount: number): number {
  return query.start * nodeCount + query.goal;
}

function mixRoute(hash: number, query: RouteQuery, route: Int32Array): number {
  hash = mixHash(hash, query.start);
  hash = mixHash(hash, query.goal);
  hash = mixHash(hash, route.length);
  for (const node of route) hash = mixHash(hash, node);
  return hash;
}

function cacheCapacity(value: number): number {
  return positiveInteger(value, 'capacity');
}

function runRouteCache(
  graph: IndexedGraph,
  queries: readonly RouteQuery[],
  capacity: number,
  lookup: (key: number) => Int32Array | undefined,
  store: (key: number, route: Int32Array) => void,
): RouteCacheResult {
  const boundedCapacity = cacheCapacity(capacity);
  let checksum = 2_166_136_261;
  let hits = 0;
  let misses = 0;
  for (const query of queries) {
    const key = routeKey(query, graph.nodeCount);
    let route = lookup(key);
    if (route === undefined) {
      misses += 1;
      route = findIndexedRoute(graph, query.start, query.goal);
      store(key, route);
    } else {
      hits += 1;
    }
    checksum = mixRoute(checksum, query, route);
  }
  return {
    checksum,
    operations: queries.length,
    hits,
    misses,
    capacity: boundedCapacity,
  };
}

export function runLruRouteCacheKernel(
  graph: IndexedGraph,
  queries: readonly RouteQuery[],
  capacity: number,
): RouteCacheResult {
  const boundedCapacity = cacheCapacity(capacity);
  const cache = new Map<number, Int32Array>();
  return runRouteCache(
    graph,
    queries,
    boundedCapacity,
    (key) => {
      const route = cache.get(key);
      if (route !== undefined) {
        cache.delete(key);
        cache.set(key, route);
      }
      return route;
    },
    (key, route) => {
      cache.delete(key);
      cache.set(key, route);
      while (cache.size > boundedCapacity) {
        const oldest = cache.keys().next().value as number | undefined;
        if (oldest === undefined) break;
        cache.delete(oldest);
      }
    },
  );
}

export function runDirectMappedRouteCacheKernel(
  graph: IndexedGraph,
  queries: readonly RouteQuery[],
  capacity: number,
): RouteCacheResult {
  const boundedCapacity = cacheCapacity(capacity);
  const keys = new Int32Array(boundedCapacity);
  keys.fill(-1);
  const routes: Array<Int32Array | undefined> = new Array(boundedCapacity);
  return runRouteCache(
    graph,
    queries,
    boundedCapacity,
    (key) => {
      const slot = key % boundedCapacity;
      return keys[slot] === key ? routes[slot] : undefined;
    },
    (key, route) => {
      const slot = key % boundedCapacity;
      keys[slot] = key;
      routes[slot] = route;
    },
  );
}

export function createPointWorkload(count: number, seed = 1): TerrainPoint[] {
  positiveInteger(count, 'count');
  const random = createSeededRandom(seed);
  const points = new Array<TerrainPoint>(count);
  for (let id = 0; id < count; id += 1) {
    points[id] = {
      id,
      x: Math.fround(Math.round((random() * 2_000 - 1_000) * 1_000) / 1_000),
      y: Math.fround(Math.round((random() * 2_000 - 1_000) * 1_000) / 1_000),
      elevation: Math.fround(Math.round((random() * 2_000 - 500) * 1_000) / 1_000),
    };
  }
  return points;
}

export function buildGeoJsonSnapshot(points: readonly TerrainPoint[]): GeoJsonSnapshot {
  return {
    type: 'FeatureCollection',
    features: points.map((point) => ({
      type: 'Feature' as const,
      id: point.id,
      geometry: {
        type: 'Point' as const,
        coordinates: [Math.fround(point.x), Math.fround(point.y)] as [number, number],
      },
      properties: { elevation: Math.fround(point.elevation) },
    })),
  };
}

export function buildCompactPointBuffer(points: readonly TerrainPoint[]): CompactPointBuffer {
  const ids = new Uint32Array(points.length);
  const positions = new Float32Array(points.length * 3);
  for (let index = 0; index < points.length; index += 1) {
    const point = points[index];
    ids[index] = point.id;
    positions[index * 3] = point.x;
    positions[index * 3 + 1] = point.y;
    positions[index * 3 + 2] = point.elevation;
  }
  return { ids, positions, bytesPerGuest: 16 };
}

export function checksumGeoJsonSnapshot(snapshot: GeoJsonSnapshot): number {
  let checksum = 2_166_136_261;
  for (const feature of snapshot.features) {
    checksum = mixHash(checksum, feature.id);
    checksum = hashNumber(checksum, feature.geometry.coordinates[0]);
    checksum = hashNumber(checksum, feature.geometry.coordinates[1]);
    checksum = hashNumber(checksum, feature.properties.elevation);
  }
  return checksum;
}

export function checksumCompactPointBuffer(buffer: CompactPointBuffer): number {
  if (buffer.positions.length !== buffer.ids.length * 3) {
    throw new RangeError('compact point buffer columns have inconsistent lengths');
  }
  let checksum = 2_166_136_261;
  for (let index = 0; index < buffer.ids.length; index += 1) {
    checksum = mixHash(checksum, buffer.ids[index]);
    checksum = hashNumber(checksum, buffer.positions[index * 3]);
    checksum = hashNumber(checksum, buffer.positions[index * 3 + 1]);
    checksum = hashNumber(checksum, buffer.positions[index * 3 + 2]);
  }
  return checksum;
}

export function runGeoJsonSnapshotKernel(points: readonly TerrainPoint[]): KernelResult {
  const snapshot = buildGeoJsonSnapshot(points);
  return { checksum: checksumGeoJsonSnapshot(snapshot), operations: points.length };
}

export function runCompactPointBufferKernel(points: readonly TerrainPoint[]): KernelResult {
  const buffer = buildCompactPointBuffer(points);
  return { checksum: checksumCompactPointBuffer(buffer), operations: points.length };
}
