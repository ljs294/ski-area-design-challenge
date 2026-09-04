import { describe, expect, it } from 'vitest';
import { SIMULATION_EVENT_PRIORITY, SimulationEventQueue } from './eventQueue';

describe('SimulationEventQueue', () => {
  it('orders same-second work by phase priority and then stable sequence', () => {
    const queue = new SimulationEventQueue<string>();
    queue.enqueue({ dueSecond: 10, priority: SIMULATION_EVENT_PRIORITY.metrics, payload: 'metrics-a' });
    queue.enqueue({ dueSecond: 10, priority: SIMULATION_EVENT_PRIORITY.weatherSnow, payload: 'weather' });
    queue.enqueue({ dueSecond: 10, priority: SIMULATION_EVENT_PRIORITY.metrics, payload: 'metrics-b' });
    queue.enqueue({ dueSecond: 9, priority: SIMULATION_EVENT_PRIORITY.infrastructure, payload: 'infrastructure' });
    const seen: string[] = [];
    const result = queue.advanceTo(10, (event) => seen.push(event.payload));
    expect(seen).toEqual(['infrastructure', 'weather', 'metrics-a', 'metrics-b']);
    expect(result.currentSecond).toBe(10);
    expect(result.processedTimestamps).toBe(2);
    expect(result.backlogSeconds).toBe(0);
  });

  it('is invariant to advance chunking and drains callback-scheduled same-timestamp work', () => {
    const makeQueue = () => {
      const queue = new SimulationEventQueue<string>();
      queue.enqueue({ dueSecond: 2, priority: SIMULATION_EVENT_PRIORITY.arrivals, payload: 'arrival' });
      queue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.guestDecisionsPurchases, payload: 'decision' });
      queue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.weatherSnow, payload: 'weather' });
      return queue;
    };
    const whole: string[] = [];
    const chunked: string[] = [];
    const wholeQueue = makeQueue();
    wholeQueue.advanceTo(5, (event) => {
      whole.push(event.payload);
      if (event.payload === 'weather') wholeQueue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.metrics, payload: 'metrics' });
    });
    const chunkedQueue = makeQueue();
    chunkedQueue.advanceTo(2, (event) => chunked.push(event.payload));
    chunkedQueue.advanceTo(5, (event) => {
      chunked.push(event.payload);
      if (event.payload === 'weather') chunkedQueue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.metrics, payload: 'metrics' });
    });
    expect(chunked).toEqual(whole);
    expect(chunkedQueue.currentSecond).toBe(5);
    expect(chunkedQueue.isEmpty).toBe(true);
  });

  it('yields only between timestamps when CPU or event budgets are reached', () => {
    const queue = new SimulationEventQueue<string>();
    queue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.weatherSnow, payload: 'weather-a' });
    queue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.infrastructure, payload: 'infrastructure' });
    queue.enqueue({ dueSecond: 5, priority: SIMULATION_EVENT_PRIORITY.metrics, payload: 'metrics' });
    queue.enqueue({ dueSecond: 6, priority: SIMULATION_EVENT_PRIORITY.arrivals, payload: 'arrival' });
    let fakeNow = 0;
    const seen: string[] = [];
    const result = queue.advanceTo(20, (event) => { seen.push(event.payload); fakeNow += 2; },
      { maxCpuMs: 1, maxEvents: 1, now: () => fakeNow });
    expect(seen).toEqual(['weather-a', 'infrastructure', 'metrics']);
    expect(result.stopped).toBe(true);
    expect(result.currentSecond).toBe(5);
    expect(result.nextDueSecond).toBe(6);
    expect(result.backlogSeconds).toBe(15);
  });

  it('rejects fractional or backward times and invalid priorities', () => {
    const queue = new SimulationEventQueue();
    expect(() => queue.enqueue({ dueSecond: 0.5, priority: 0, payload: null })).toThrow();
    expect(() => queue.enqueue({ dueSecond: 0, priority: 6 as 0, payload: null })).toThrow();
    expect(() => queue.advanceTo(1.5)).toThrow();
    queue.advanceTo(2);
    expect(() => queue.advanceTo(1)).toThrow();
  });
});
