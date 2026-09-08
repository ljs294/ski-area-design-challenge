/**
 * Deterministic bounded route cache.
 *
 * Entries use an open-addressed table and an intrusive LRU list.  Hash-table
 * probes and eviction order are deterministic for a given query stream, while
 * the explicit capacity prevents topology churn from growing retained memory.
 * A topology revision is part of every key; changing it invalidates old paths
 * before a lookup can observe them.
 */

export interface RouteCacheStats {
  readonly capacity: number;
  readonly size: number;
  readonly hits: number;
  readonly misses: number;
  readonly evictions: number;
  readonly revisionResets: number;
  readonly topologyRevision: number | undefined;
}

export interface RouteCacheEntry {
  readonly start: number;
  readonly goal: number;
  readonly topologyRevision: number;
  readonly route: Int32Array;
}

function positiveCapacity(value: number): number {
  if (!Number.isSafeInteger(value) || value < 1 || value > 1_000_000) {
    throw new RangeError('route cache capacity must be an integer in [1, 1000000]');
  }
  return value;
}

function endpoint(value: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < 0 || value > 0x7fff_ffff) {
    throw new RangeError(`${name} must fit a non-negative signed 32-bit node index`);
  }
  return value;
}

function revision(value: number): number {
  if (!Number.isSafeInteger(value) || value < 0) throw new RangeError('topologyRevision must be non-negative');
  return value;
}

function tableSizeFor(capacity: number): number {
  let size = 1;
  while (size < capacity * 2) size *= 2;
  return size;
}

function mixKey(start: number, goal: number, topologyRevision: number): number {
  let hash = 2_166_136_261;
  hash = Math.imul((hash ^ start) >>> 0, 16_777_619) >>> 0;
  hash = Math.imul((hash ^ goal) >>> 0, 16_777_619) >>> 0;
  return Math.imul((hash ^ (topologyRevision >>> 0)) >>> 0, 16_777_619) >>> 0;
}

export class DeterministicRouteCache {
  public readonly capacity: number;
  private readonly table: Int32Array;
  private readonly starts: Float64Array;
  private readonly goals: Float64Array;
  private readonly revisions: Float64Array;
  private readonly routes: Array<Int32Array | undefined>;
  private readonly previous: Int32Array;
  private readonly next: Int32Array;
  private readonly used: Uint8Array;
  private readonly tableMask: number;
  private entryCount = 0;
  private nextUnused = 0;
  private leastRecentlyUsed = -1;
  private mostRecentlyUsed = -1;
  private currentRevision: number | undefined;
  private hitCount = 0;
  private missCount = 0;
  private evictionCount = 0;
  private revisionResetCount = 0;

  public constructor(capacity: number, topologyRevision?: number) {
    positiveCapacity(capacity);
    this.capacity = capacity;
    const tableSize = tableSizeFor(capacity);
    this.table = new Int32Array(tableSize);
    this.tableMask = tableSize - 1;
    this.starts = new Float64Array(capacity);
    this.goals = new Float64Array(capacity);
    this.revisions = new Float64Array(capacity);
    this.routes = new Array<Int32Array | undefined>(capacity);
    this.previous = new Int32Array(capacity);
    this.next = new Int32Array(capacity);
    this.used = new Uint8Array(capacity);
    this.previous.fill(-1);
    this.next.fill(-1);
    if (topologyRevision !== undefined) this.currentRevision = revision(topologyRevision);
  }

  public get size(): number {
    return this.entryCount;
  }

  public getStats(): RouteCacheStats {
    return {
      capacity: this.capacity,
      size: this.entryCount,
      hits: this.hitCount,
      misses: this.missCount,
      evictions: this.evictionCount,
      revisionResets: this.revisionResetCount,
      topologyRevision: this.currentRevision,
    };
  }

  private prepareRevision(topologyRevision: number): number {
    const checked = revision(topologyRevision);
    if (this.currentRevision === undefined) {
      this.currentRevision = checked;
    } else if (checked !== this.currentRevision) {
      this.clearEntries();
      this.currentRevision = checked;
      this.revisionResetCount += 1;
    }
    return checked;
  }

  private slotFor(start: number, goal: number, topologyRevision: number): number {
    let slot = mixKey(start, goal, topologyRevision) & this.tableMask;
    while (true) {
      const marker = this.table[slot];
      if (marker === 0) return -slot - 1;
      const entry = marker - 1;
      if (this.starts[entry] === start && this.goals[entry] === goal && this.revisions[entry] === topologyRevision) {
        return slot;
      }
      slot = (slot + 1) & this.tableMask;
    }
  }

  private insertTable(entry: number): void {
    let slot = mixKey(this.starts[entry], this.goals[entry], this.revisions[entry]) & this.tableMask;
    while (this.table[slot] !== 0) slot = (slot + 1) & this.tableMask;
    this.table[slot] = entry + 1;
  }

  /** Remove one table marker and repair the following open-addressed cluster. */
  private removeTableEntry(entry: number): void {
    const slot = this.slotFor(this.starts[entry], this.goals[entry], this.revisions[entry]);
    if (slot < 0) throw new Error('route cache table lost an entry');
    this.table[slot] = 0;
    let scan = (slot + 1) & this.tableMask;
    while (this.table[scan] !== 0) {
      const moved = this.table[scan] - 1;
      this.table[scan] = 0;
      this.insertTable(moved);
      scan = (scan + 1) & this.tableMask;
    }
  }

  private unlink(entry: number): void {
    const predecessor = this.previous[entry];
    const successor = this.next[entry];
    if (predecessor < 0) this.leastRecentlyUsed = successor;
    else this.next[predecessor] = successor;
    if (successor < 0) this.mostRecentlyUsed = predecessor;
    else this.previous[successor] = predecessor;
    this.previous[entry] = -1;
    this.next[entry] = -1;
  }

  private linkAsMostRecent(entry: number): void {
    this.previous[entry] = this.mostRecentlyUsed;
    this.next[entry] = -1;
    if (this.mostRecentlyUsed < 0) this.leastRecentlyUsed = entry;
    else this.next[this.mostRecentlyUsed] = entry;
    this.mostRecentlyUsed = entry;
  }

  private touch(entry: number): void {
    if (entry === this.mostRecentlyUsed) return;
    this.unlink(entry);
    this.linkAsMostRecent(entry);
  }

  private clearEntries(): void {
    this.table.fill(0);
    this.previous.fill(-1);
    this.next.fill(-1);
    this.used.fill(0);
    this.routes.fill(undefined);
    this.entryCount = 0;
    this.nextUnused = 0;
    this.leastRecentlyUsed = -1;
    this.mostRecentlyUsed = -1;
  }

  /** Clear paths while preserving cumulative hit/miss telemetry. */
  public clear(): void {
    this.clearEntries();
  }

  public get(start: number, goal: number, topologyRevision: number): Int32Array | undefined {
    const checkedStart = endpoint(start, 'start');
    const checkedGoal = endpoint(goal, 'goal');
    const checkedRevision = this.prepareRevision(topologyRevision);
    const slot = this.slotFor(checkedStart, checkedGoal, checkedRevision);
    if (slot < 0) {
      this.missCount += 1;
      return undefined;
    }
    const entry = this.table[slot] - 1;
    const route = this.routes[entry];
    if (route === undefined) throw new Error('route cache entry has no route');
    this.touch(entry);
    this.hitCount += 1;
    return route.slice();
  }

  public set(start: number, goal: number, topologyRevision: number, route: Int32Array): void {
    const checkedStart = endpoint(start, 'start');
    const checkedGoal = endpoint(goal, 'goal');
    const checkedRevision = this.prepareRevision(topologyRevision);
    for (const node of route) endpoint(node, 'route node');
    const existingSlot = this.slotFor(checkedStart, checkedGoal, checkedRevision);
    if (existingSlot >= 0) {
      const entry = this.table[existingSlot] - 1;
      this.routes[entry] = route.slice();
      this.touch(entry);
      return;
    }

    let entry: number;
    if (this.entryCount === this.capacity) {
      entry = this.leastRecentlyUsed;
      if (entry < 0) throw new Error('route cache is full but has no LRU entry');
      this.removeTableEntry(entry);
      this.unlink(entry);
      this.evictionCount += 1;
    } else {
      entry = this.nextUnused;
      this.nextUnused += 1;
      this.entryCount += 1;
      this.used[entry] = 1;
    }
    this.starts[entry] = checkedStart;
    this.goals[entry] = checkedGoal;
    this.revisions[entry] = checkedRevision;
    this.routes[entry] = route.slice();
    this.insertTable(entry);
    this.linkAsMostRecent(entry);
  }

  /** Lookup, compute, and retain one path; query telemetry remains exact. */
  public getOrCompute(
    start: number,
    goal: number,
    topologyRevision: number,
    compute: () => Int32Array,
  ): Int32Array {
    const cached = this.get(start, goal, topologyRevision);
    if (cached !== undefined) return cached;
    const route = compute();
    this.set(start, goal, topologyRevision, route);
    return route.slice();
  }

  /** Snapshot LRU order for diagnostics without exposing mutable cache paths. */
  public entries(): RouteCacheEntry[] {
    const entries: RouteCacheEntry[] = [];
    let entry = this.leastRecentlyUsed;
    while (entry >= 0) {
      const route = this.routes[entry];
      if (route === undefined || this.used[entry] === 0) throw new Error('route cache LRU links are inconsistent');
      entries.push({
        start: this.starts[entry],
        goal: this.goals[entry],
        topologyRevision: this.revisions[entry],
        route: route.slice(),
      });
      entry = this.next[entry];
      if (entries.length > this.capacity) throw new Error('route cache LRU contains a cycle');
    }
    return entries;
  }
}
