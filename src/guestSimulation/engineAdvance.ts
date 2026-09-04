import type { SimulatedSecond } from './contracts.ts';
import type { EventCalendar } from './eventCalendar.ts';

export interface BudgetedAdvanceResult {
  readonly tick: SimulatedSecond;
  readonly eventsProcessed: number;
  readonly cpuMs: number;
  readonly budgetExceeded: boolean;
  readonly reachedTarget: boolean;
}

function nowMs(): number {
  return typeof performance === 'undefined' ? Date.now() : performance.now();
}

/** Drain atomically by timestamp while respecting a best-effort CPU budget. */
export function advanceCalendarToBudget<T>(input: {
  calendar: EventCalendar<T>;
  fromTick: SimulatedSecond;
  toTick: SimulatedSecond;
  maxCpuMs: number;
  handle(event: T, tick: SimulatedSecond): void;
  finish(tick: SimulatedSecond): void;
}): BudgetedAdvanceResult {
  const start = nowMs();
  let current = input.fromTick;
  let eventsProcessed = 0;
  let budgetExceeded = false;
  let reachedTarget = false;
  for (;;) {
    const next = input.calendar.peek();
    if (!next || next.tick > input.toTick) {
      input.calendar.advanceTo(input.toTick);
      current = input.toTick;
      reachedTarget = true;
      break;
    }
    const timestamp = next.tick;
    while (input.calendar.peek()?.tick === timestamp) {
      const event = input.calendar.pop();
      if (!event) break;
      current = event.tick;
      input.handle(event.payload, event.tick);
      eventsProcessed += 1;
    }
    if (timestamp >= input.toTick) {
      current = input.toTick;
      reachedTarget = true;
      break;
    }
    if (nowMs() - start >= input.maxCpuMs) {
      budgetExceeded = true;
      break;
    }
  }
  input.finish(current);
  return Object.freeze({ tick: current, eventsProcessed,
    cpuMs: Math.max(0, nowMs() - start), budgetExceeded, reachedTarget });
}
