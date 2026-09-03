/**
 * Deterministic event calendar for the guest simulation.
 *
 * Ticks are integer simulated seconds.  The heap comparator is a complete
 * order: tick, priority, stable entity id, stable event key, then insertion
 * sequence.  Cancellation only advances a generation counter; stale heap
 * entries are discarded when they reach the root (lazy cancellation).
 */

import type { SimulatedSecond } from './contracts.ts';

export type EventIdentityPart = string | number | bigint;

export interface EventGenerationToken {
  readonly entityId: string;
  readonly key: string;
  readonly generation: number;
}

export interface ScheduleEventInput<T = unknown> {
  readonly tick: SimulatedSecond;
  readonly priority?: number;
  /** Compatibility spelling for callers that name this field explicitly. */
  readonly eventPriority?: number;
  readonly entityId?: EventIdentityPart;
  readonly stableEntity?: EventIdentityPart;
  readonly key?: EventIdentityPart;
  readonly stableKey?: EventIdentityPart;
  readonly payload: T;
  /** Optional scheduling metadata; never participates in generic ordering. */
  readonly phase?: string;
  readonly generation?: EventGenerationToken;
  readonly generationToken?: EventGenerationToken;
}

export interface ScheduledCalendarEvent<T = unknown> {
  readonly tick: SimulatedSecond;
  readonly priority: number;
  readonly eventPriority: number;
  readonly entityId: string;
  readonly key: string;
  readonly payload: T;
  readonly phase?: string;
  readonly generation: number;
  readonly generationToken: EventGenerationToken;
  readonly token: EventGenerationToken;
  readonly insertionSequence: number;
}

export interface CalendarProjectionEvent<T = unknown> {
  readonly tick: SimulatedSecond;
  readonly priority: number;
  readonly entityId: string;
  readonly key: string;
  readonly payload: T;
  readonly phase?: string;
  readonly generation: number;
  readonly insertionSequence: number;
}

export interface EventCalendarStateProjection<T = unknown> {
  readonly currentTick: SimulatedSecond;
  readonly nextInsertionSequence: number;
  readonly generations: readonly EventGenerationToken[];
  readonly events: readonly CalendarProjectionEvent<T>[];
}

export interface EventCalendarOptions {
  readonly startTick?: SimulatedSecond;
}

const FNV_OFFSET = 0x811c_9dc5;
const FNV_PRIME = 0x0100_0193;

function stablePart(value: EventIdentityPart | undefined, label: string, optional = false): string {
  if (value === undefined) {
    if (optional) return '';
    throw new TypeError(`${label} is required`);
  }
  if (typeof value === 'number' && (!Number.isFinite(value) || !Number.isInteger(value))) {
    throw new RangeError(`${label} must be a finite integer when supplied as a number`);
  }
  return typeof value === 'bigint' ? `${value}n` : String(value);
}

function validateTick(value: number, label = 'tick'): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${label} must be a non-negative safe integer simulated second`);
  }
}

function validatePriority(value: number): void {
  if (!Number.isSafeInteger(value)) throw new RangeError('event priority must be a safe integer');
}

function identityKey(entityId: string, key: string): string {
  return `${entityId.length}:${entityId}|${key.length}:${key}`;
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function compareEvents<T>(left: ScheduledCalendarEvent<T>, right: ScheduledCalendarEvent<T>): number {
  if (left.tick !== right.tick) return left.tick < right.tick ? -1 : 1;
  if (left.priority !== right.priority) return left.priority < right.priority ? -1 : 1;
  const entityOrder = compareText(left.entityId, right.entityId);
  if (entityOrder !== 0) return entityOrder;
  const keyOrder = compareText(left.key, right.key);
  if (keyOrder !== 0) return keyOrder;
  if (left.insertionSequence !== right.insertionSequence) {
    return left.insertionSequence < right.insertionSequence ? -1 : 1;
  }
  return 0;
}

function fnv1a32(text: string): number {
  let hash = FNV_OFFSET;
  for (let index = 0; index < text.length; index += 1) {
    const codeUnit = text.charCodeAt(index);
    hash ^= codeUnit & 0xff;
    hash = Math.imul(hash, FNV_PRIME) >>> 0;
    hash ^= codeUnit >>> 8;
    hash = Math.imul(hash, FNV_PRIME) >>> 0;
  }
  return hash >>> 0;
}

function stableSerialize(value: unknown, stack: Set<object> = new Set()): string {
  if (value === null) return 'null';
  if (value === undefined) return 'undefined';
  if (typeof value === 'string') return `string:${JSON.stringify(value)}`;
  if (typeof value === 'boolean') return value ? 'boolean:true' : 'boolean:false';
  if (typeof value === 'number') {
    if (Number.isNaN(value)) return 'number:NaN';
    if (value === Infinity) return 'number:+Infinity';
    if (value === -Infinity) return 'number:-Infinity';
    if (Object.is(value, -0)) return 'number:-0';
    return `number:${value}`;
  }
  if (typeof value === 'bigint') return `bigint:${value}n`;
  if (typeof value === 'function') return 'function';
  if (typeof value === 'symbol') return `symbol:${String(value)}`;
  if (typeof value !== 'object') return `${typeof value}:${String(value)}`;
  if (stack.has(value)) throw new TypeError('calendar payload cannot contain a cycle');
  stack.add(value);
  let serialized: string;
  if (Array.isArray(value)) {
    serialized = `[${value.map((entry) => stableSerialize(entry, stack)).join(',')}]`;
  } else {
    const record = value as Record<string, unknown>;
    serialized = `{${Object.keys(record).sort().map((key) =>
      `${JSON.stringify(key)}:${stableSerialize(record[key], stack)}`).join(',')}}`;
  }
  stack.delete(value);
  return serialized;
}

function makeToken(entityId: string, key: string, generation: number): EventGenerationToken {
  return Object.freeze({ entityId, key, generation });
}

/** Compute a deterministic checksum for a calendar projection. */
export function eventCalendarChecksum(projection: unknown): string {
  return `fnv1a32-${fnv1a32(stableSerialize(projection)).toString(16).padStart(8, '0')}`;
}

export class EventCalendar<T = unknown> {
  private readonly heap: ScheduledCalendarEvent<T>[] = [];
  private readonly generationsByIdentity = new Map<string, number>();
  private current = 0;
  private nextSequence = 0;

  constructor(startTickOrOptions: SimulatedSecond | EventCalendarOptions = 0) {
    const startTick = typeof startTickOrOptions === 'number'
      ? startTickOrOptions
      : startTickOrOptions.startTick ?? 0;
    validateTick(startTick, 'startTick');
    this.current = startTick;
  }

  get currentTick(): SimulatedSecond {
    return this.current;
  }

  /** Number of live entries; stale entries remain physically lazy in the heap. */
  get size(): number {
    let live = 0;
    for (const event of this.heap) if (this.isEventLive(event)) live += 1;
    return live;
  }

  get isEmpty(): boolean {
    return this.size === 0;
  }

  /** Begin a new generation, invalidating older events for this entity/key. */
  generationFor(entity: EventIdentityPart, key: EventIdentityPart = ''): EventGenerationToken {
    const entityId = stablePart(entity, 'entityId');
    const eventKey = stablePart(key, 'key');
    const identity = identityKey(entityId, eventKey);
    const generation = (this.generationsByIdentity.get(identity) ?? 0) + 1;
    if (!Number.isSafeInteger(generation)) throw new RangeError('event generation overflowed');
    this.generationsByIdentity.set(identity, generation);
    return makeToken(entityId, eventKey, generation);
  }

  /** Alias for generationFor at call sites that use token terminology. */
  createGeneration(entity: EventIdentityPart, key: EventIdentityPart = ''): EventGenerationToken {
    return this.generationFor(entity, key);
  }

  nextGeneration(entity: EventIdentityPart, key: EventIdentityPart = ''): EventGenerationToken {
    return this.generationFor(entity, key);
  }

  getGeneration(entity: EventIdentityPart, key: EventIdentityPart = ''): number {
    return this.generationsByIdentity.get(identityKey(
      stablePart(entity, 'entityId'), stablePart(key, 'key'),
    )) ?? 0;
  }

  isGenerationCurrent(token: EventGenerationToken): boolean {
    return this.getGeneration(token.entityId, token.key) === token.generation;
  }

  /** Invalidate a generation without scanning or rebuilding the heap. */
  cancelGeneration(token: EventGenerationToken): void {
    this.validateToken(token);
    const identity = identityKey(token.entityId, token.key);
    const current = this.generationsByIdentity.get(identity) ?? 0;
    if (token.generation >= current) {
      if (token.generation === Number.MAX_SAFE_INTEGER) throw new RangeError('event generation overflowed');
      this.generationsByIdentity.set(identity, token.generation + 1);
    }
  }

  cancel(eventOrToken: EventGenerationToken | ScheduledCalendarEvent<T>): void {
    this.cancelGeneration('generationToken' in eventOrToken ? eventOrToken.generationToken : eventOrToken);
  }

  schedule(input: ScheduleEventInput<T>): ScheduledCalendarEvent<T> {
    validateTick(input.tick);
    if (input.tick < this.current) throw new RangeError('cannot schedule an event before currentTick');
    const entityId = stablePart(input.entityId ?? input.stableEntity, 'entityId');
    const key = stablePart(input.key ?? input.stableKey, 'key', true);
    const priority = input.priority ?? input.eventPriority ?? 0;
    if (input.priority !== undefined && input.eventPriority !== undefined && input.priority !== input.eventPriority) {
      throw new RangeError('priority and eventPriority disagree');
    }
    validatePriority(priority);
    const identity = identityKey(entityId, key);
    const suppliedToken = input.generationToken ?? input.generation;
    let token: EventGenerationToken;
    if (suppliedToken !== undefined) {
      this.validateToken(suppliedToken);
      if (suppliedToken.entityId !== entityId || suppliedToken.key !== key) {
        throw new RangeError('event generation token does not match entityId/key');
      }
      const currentGeneration = this.generationsByIdentity.get(identity);
      if (currentGeneration === undefined) this.generationsByIdentity.set(identity, suppliedToken.generation);
      else if (currentGeneration !== suppliedToken.generation) throw new RangeError('event generation token is stale');
      token = makeToken(suppliedToken.entityId, suppliedToken.key, suppliedToken.generation);
    } else {
      token = makeToken(entityId, key, this.generationsByIdentity.get(identity) ?? 0);
      if (!this.generationsByIdentity.has(identity)) this.generationsByIdentity.set(identity, 0);
    }
    if (this.nextSequence === Number.MAX_SAFE_INTEGER) throw new RangeError('event insertion sequence overflowed');
    const baseEvent = {
      tick: input.tick,
      priority,
      eventPriority: priority,
      entityId,
      key,
      payload: input.payload,
      generation: token.generation,
      generationToken: token,
      token,
      insertionSequence: this.nextSequence,
    };
    const event: ScheduledCalendarEvent<T> = Object.freeze(input.phase === undefined
      ? baseEvent
      : { ...baseEvent, phase: input.phase });
    this.nextSequence += 1;
    this.heap.push(event);
    this.siftUp(this.heap.length - 1);
    return event;
  }

  peek(): ScheduledCalendarEvent<T> | undefined {
    this.discardStaleRoots();
    return this.heap[0];
  }

  pop(): ScheduledCalendarEvent<T> | undefined {
    this.discardStaleRoots();
    if (this.heap.length === 0) return undefined;
    const first = this.heap[0];
    const last = this.heap.pop();
    if (last !== undefined && this.heap.length > 0) {
      this.heap[0] = last;
      this.siftDown(0);
    }
    return first;
  }

  /**
   * Dispatch every event through targetTick, in exactly the same order whether
   * the caller advances by one second or by a larger interval.
   */
  advanceTo(
    targetTick: SimulatedSecond,
    onEvent?: (event: ScheduledCalendarEvent<T>) => void,
  ): ScheduledCalendarEvent<T>[] {
    validateTick(targetTick, 'targetTick');
    if (targetTick < this.current) throw new RangeError('targetTick cannot move backwards');
    const dispatched: ScheduledCalendarEvent<T>[] = [];
    for (;;) {
      const next = this.peek();
      if (next === undefined || next.tick > targetTick) break;
      const event = this.pop();
      if (event === undefined) break;
      this.current = event.tick;
      dispatched.push(event);
      onEvent?.(event);
    }
    this.current = targetTick;
    return dispatched;
  }

  advanceBy(seconds: SimulatedSecond, onEvent?: (event: ScheduledCalendarEvent<T>) => void): ScheduledCalendarEvent<T>[] {
    validateTick(seconds, 'seconds');
    return this.advanceTo(this.current + seconds, onEvent);
  }

  drainUntil(targetTick: SimulatedSecond, onEvent?: (event: ScheduledCalendarEvent<T>) => void): ScheduledCalendarEvent<T>[] {
    return this.advanceTo(targetTick, onEvent);
  }

  stateProjection(): EventCalendarStateProjection<T> {
    const events = this.heap.filter((event) => this.isEventLive(event)).slice();
    events.sort(compareEvents);
    const projectedEvents = events.map((event) => {
      const { tick, priority, entityId, key, payload, generation, insertionSequence, phase } = event;
      const base = { tick, priority, entityId, key, payload, generation, insertionSequence };
      return phase === undefined ? base : { ...base, phase };
    });
    const generations = [...this.generationsByIdentity.entries()]
      .map(([identity, generation]) => {
        // Decode the length-delimited identity rather than splitting on the
        // separator: entity ids and keys are allowed to contain ':' or '|'.
        const entityLengthEnd = identity.indexOf(':');
        const entityLength = Number(identity.slice(0, entityLengthEnd));
        const entityStart = entityLengthEnd + 1;
        const entityId = identity.slice(entityStart, entityStart + entityLength);
        const keyLengthEnd = identity.indexOf(':', entityStart + entityLength + 1);
        const keyStart = keyLengthEnd + 1;
        const key = identity.slice(keyStart);
        return makeToken(entityId, key, generation);
      })
      .sort((left, right) => compareText(identityKey(left.entityId, left.key), identityKey(right.entityId, right.key)));
    return {
      currentTick: this.current,
      nextInsertionSequence: this.nextSequence,
      generations,
      events: projectedEvents,
    };
  }

  checksum(): string {
    return eventCalendarChecksum(this.stateProjection());
  }

  stateChecksum(): string {
    return this.checksum();
  }

  clear(): void {
    this.heap.length = 0;
    this.generationsByIdentity.clear();
    this.nextSequence = 0;
  }

  private validateToken(token: EventGenerationToken): void {
    if (typeof token?.entityId !== 'string' || typeof token.key !== 'string') {
      throw new TypeError('event generation token must contain string entityId and key');
    }
    if (!Number.isSafeInteger(token.generation) || token.generation < 0) {
      throw new RangeError('event generation must be a non-negative safe integer');
    }
  }

  private isEventLive(event: ScheduledCalendarEvent<T>): boolean {
    return (this.generationsByIdentity.get(identityKey(event.entityId, event.key)) ?? 0) === event.generation;
  }

  private discardStaleRoots(): void {
    while (this.heap.length > 0 && !this.isEventLive(this.heap[0])) this.popRaw();
  }

  private popRaw(): void {
    const last = this.heap.pop();
    if (last !== undefined && this.heap.length > 0) {
      this.heap[0] = last;
      this.siftDown(0);
    }
  }

  private siftUp(index: number): void {
    let child = index;
    while (child > 0) {
      const parent = (child - 1) >> 1;
      if (compareEvents(this.heap[parent], this.heap[child]) <= 0) break;
      [this.heap[parent], this.heap[child]] = [this.heap[child], this.heap[parent]];
      child = parent;
    }
  }

  private siftDown(index: number): void {
    let parent = index;
    for (;;) {
      const left = parent * 2 + 1;
      if (left >= this.heap.length) return;
      const right = left + 1;
      let smallest = left;
      if (right < this.heap.length && compareEvents(this.heap[right], this.heap[left]) < 0) smallest = right;
      if (compareEvents(this.heap[parent], this.heap[smallest]) <= 0) return;
      [this.heap[parent], this.heap[smallest]] = [this.heap[smallest], this.heap[parent]];
      parent = smallest;
    }
  }
}

export const SimEventCalendar = EventCalendar;
export const DeterministicEventCalendar = EventCalendar;
