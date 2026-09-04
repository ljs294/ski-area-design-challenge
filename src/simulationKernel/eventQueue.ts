/**
 * Dependency-neutral event calendar for the continuous simulation kernel.
 *
 * The comparator is intentionally small and total: time first, then the
 * phase priority, then the monotonic insertion sequence. No wall-clock or UI
 * type crosses this boundary.
 */

export const SIMULATION_EVENT_PRIORITY = Object.freeze({
  weatherSnow: 0,
  infrastructure: 1,
  movementCapacity: 2,
  arrivals: 3,
  guestDecisionsPurchases: 4,
  metrics: 5,
} as const);

/** Readable aliases for integrations that use title-cased phase names. */
export const EVENT_PRIORITY = Object.freeze({
  weatherSnow: SIMULATION_EVENT_PRIORITY.weatherSnow,
  infrastructure: SIMULATION_EVENT_PRIORITY.infrastructure,
  movementCapacity: SIMULATION_EVENT_PRIORITY.movementCapacity,
  arrivals: SIMULATION_EVENT_PRIORITY.arrivals,
  guestDecisionsPurchases: SIMULATION_EVENT_PRIORITY.guestDecisionsPurchases,
  metrics: SIMULATION_EVENT_PRIORITY.metrics,
} as const);

export type SimulationEventPriority = 0 | 1 | 2 | 3 | 4 | 5;

export interface SimulationEvent<T> {
  readonly dueSecond: number;
  readonly priority: SimulationEventPriority;
  readonly sequence: number;
  readonly payload: T;
}

export interface SimulationEventInput<T> {
  readonly dueSecond: number;
  readonly priority: SimulationEventPriority;
  readonly payload: T;
  /** Optional only for deterministic checkpoint restoration. */
  readonly sequence?: number;
}

export interface SimulationEventAdvanceOptions<T> {
  /** Maximum callback time before yielding before the next timestamp. */
  readonly maxCpuMs?: number;
  /** A deterministic event bound; it is also observed only between timestamps. */
  readonly maxEvents?: number;
  readonly onEvent?: (event: SimulationEvent<T>) => void;
  /** Injectable clock for deterministic budget tests. */
  readonly now?: () => number;
}

export interface SimulationEventAdvanceResult {
  readonly currentSecond: number;
  readonly targetSecond: number;
  readonly backlogSeconds: number;
  readonly processedEvents: number;
  readonly processedTimestamps: number;
  readonly stopped: boolean;
  readonly nextDueSecond: number | null;
}

export type SimulationEventCallback<T> = (event: SimulationEvent<T>) => void;

function assertIntegerSecond(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value < 0) {
    throw new RangeError(`${label} must be a non-negative safe integer simulated second`);
  }
}

function assertPriority(value: number): asserts value is SimulationEventPriority {
  if (!Number.isSafeInteger(value) || value < 0 || value > 5) {
    throw new RangeError('event priority must be one of the six simulation phases');
  }
}

function defaultNow(): number {
  return typeof performance === 'undefined' ? 0 : performance.now();
}

function compareEvents<T>(left: SimulationEvent<T>, right: SimulationEvent<T>): number {
  if (left.dueSecond !== right.dueSecond) return left.dueSecond < right.dueSecond ? -1 : 1;
  if (left.priority !== right.priority) return left.priority < right.priority ? -1 : 1;
  return left.sequence < right.sequence ? -1 : left.sequence > right.sequence ? 1 : 0;
}

/** A binary min-heap with deterministic event identity and bounded advances. */
export class SimulationEventQueue<T> {
  private readonly heap: SimulationEvent<T>[] = [];
  private nextSequence = 0;
  private current = 0;
  private activeDueSecond: number | null = null;
  private activePriority: SimulationEventPriority | null = null;

  constructor(startSecond = 0) {
    assertIntegerSecond(startSecond, 'startSecond');
    this.current = startSecond;
  }

  get currentSecond(): number { return this.current; }
  get size(): number { return this.heap.length; }
  get isEmpty(): boolean { return this.heap.length === 0; }

  peek(): SimulationEvent<T> | undefined { return this.heap[0]; }

  /** Schedule an event with a stable monotonic sequence. */
  enqueue(input: SimulationEventInput<T>): SimulationEvent<T> {
    assertIntegerSecond(input.dueSecond, 'dueSecond');
    assertPriority(input.priority);
    if (input.dueSecond < this.current) throw new RangeError('cannot schedule an event before currentSecond');
    if (this.activeDueSecond === input.dueSecond && this.activePriority !== null && input.priority < this.activePriority) {
      throw new RangeError('same-timestamp events cannot be scheduled in an earlier priority phase');
    }
    const sequence = input.sequence ?? this.nextSequence;
    if (!Number.isSafeInteger(sequence) || sequence < 0) throw new RangeError('event sequence must be a non-negative safe integer');
    if (sequence < this.nextSequence) throw new RangeError('event sequence must be monotonic');
    if (sequence === Number.MAX_SAFE_INTEGER) throw new RangeError('event sequence overflowed');
    this.nextSequence = sequence + 1;
    const event = Object.freeze({ dueSecond: input.dueSecond, priority: input.priority, sequence, payload: input.payload });
    this.heap.push(event);
    this.siftUp(this.heap.length - 1);
    return event;
  }

  schedule(input: SimulationEventInput<T>): SimulationEvent<T> { return this.enqueue(input); }

  clear(): void {
    this.heap.length = 0;
    this.activeDueSecond = null;
    this.activePriority = null;
  }

  /**
   * Process all events through targetSecond. If the callback budget is
   * reached, yielding happens only before the next timestamp; all events at
   * the timestamp already being processed are drained atomically.
   */
  advanceTo(targetSecond: number, onEvent?: SimulationEventCallback<T>, options?: SimulationEventAdvanceOptions<T>): SimulationEventAdvanceResult;
  advanceTo(targetSecond: number, options?: SimulationEventAdvanceOptions<T>): SimulationEventAdvanceResult;
  advanceTo(targetSecond: number, callbackOrOptions?: SimulationEventCallback<T> | SimulationEventAdvanceOptions<T>, maybeOptions: SimulationEventAdvanceOptions<T> = {}): SimulationEventAdvanceResult {
    assertIntegerSecond(targetSecond, 'targetSecond');
    if (targetSecond < this.current) throw new RangeError('targetSecond cannot move backwards');
    const options: SimulationEventAdvanceOptions<T> = typeof callbackOrOptions === 'function'
      ? { ...maybeOptions, onEvent: callbackOrOptions } : callbackOrOptions ?? {};
    const callback = options.onEvent;
    const maxCpuMs = options.maxCpuMs ?? 8;
    if (!Number.isFinite(maxCpuMs) || maxCpuMs <= 0) throw new RangeError('maxCpuMs must be a positive finite number');
    const maxEvents = options.maxEvents ?? Number.POSITIVE_INFINITY;
    if (maxEvents !== Number.POSITIVE_INFINITY
      && (!Number.isSafeInteger(maxEvents) || maxEvents <= 0)) throw new RangeError('maxEvents must be a positive safe integer');
    const now = options.now ?? defaultNow;
    const startedAt = now();
    let processedEvents = 0;
    let processedTimestamps = 0;
    let timestamp: number | null = null;
    let stopped = false;

    while (this.heap.length > 0) {
      const next = this.heap[0]!;
      if (next.dueSecond > targetSecond) break;
      const startsNewTimestamp = timestamp !== next.dueSecond;
      // Never pop an event when yielding: leaving the heap untouched makes a
      // resumed advance exactly equivalent to a single larger advance.
      if (startsNewTimestamp && (processedEvents >= maxEvents || now() - startedAt >= maxCpuMs)) {
        stopped = true;
        break;
      }
      timestamp = next.dueSecond;
      if (startsNewTimestamp) {
        processedTimestamps += 1;
        this.current = timestamp;
      }
      const event = this.popInternal()!;
      this.activeDueSecond = event.dueSecond;
      this.activePriority = event.priority;
      processedEvents += 1;
      callback?.(event);
      this.activeDueSecond = null;
      this.activePriority = null;
    }
    if (!stopped) this.current = targetSecond;
    const nextDueSecond = this.heap[0]?.dueSecond ?? null;
    return Object.freeze({ currentSecond: this.current, targetSecond,
      backlogSeconds: Math.max(0, targetSecond - this.current), processedEvents,
      processedTimestamps, stopped, nextDueSecond });
  }

  private popInternal(): SimulationEvent<T> | undefined {
    if (this.heap.length === 0) return undefined;
    const first = this.heap[0];
    const last = this.heap.pop()!;
    if (this.heap.length > 0) {
      this.heap[0] = last;
      this.siftDown(0);
    }
    return first;
  }

  private siftUp(index: number): void {
    while (index > 0) {
      const parent = (index - 1) >> 1;
      if (compareEvents(this.heap[parent]!, this.heap[index]!) <= 0) break;
      [this.heap[parent], this.heap[index]] = [this.heap[index]!, this.heap[parent]!];
      index = parent;
    }
  }

  private siftDown(index: number): void {
    for (;;) {
      const left = index * 2 + 1;
      if (left >= this.heap.length) return;
      const right = left + 1;
      let child = left;
      if (right < this.heap.length && compareEvents(this.heap[right]!, this.heap[left]!) < 0) child = right;
      if (compareEvents(this.heap[index]!, this.heap[child]!) <= 0) return;
      [this.heap[index], this.heap[child]] = [this.heap[child]!, this.heap[index]!];
      index = child;
    }
  }
}

export { SimulationEventQueue as EventQueue };
