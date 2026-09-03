import { describe, expect, it } from 'vitest';
import { EventCalendar } from './eventCalendar';

function seededCalendar(): EventCalendar<string> {
  const calendar = new EventCalendar<string>();
  calendar.schedule({ tick: 8, priority: 3, entityId: 'guest-b', key: 'leave', payload: 'leave-b' });
  calendar.schedule({ tick: 3, priority: 5, entityId: 'guest-z', key: 'arrive', payload: 'arrive-z' });
  calendar.schedule({ tick: 3, priority: 1, entityId: 'guest-z', key: 'arrive', payload: 'arrive-z-early' });
  calendar.schedule({ tick: 3, priority: 1, entityId: 'guest-a', key: 'arrive', payload: 'arrive-a' });
  calendar.schedule({ tick: 3, priority: 1, entityId: 'guest-a', key: 'board', payload: 'board-a' });
  calendar.schedule({ tick: 3, priority: 1, entityId: 'guest-a', key: 'board', payload: 'board-a-second' });
  return calendar;
}

describe('guest simulation event calendar', () => {
  it('uses the complete tick, priority, entity/key, sequence order', () => {
    const calendar = seededCalendar();
    expect(calendar.checksum()).toBe('fnv1a32-795981ce');
    expect(calendar.advanceTo(3).map((event) => event.payload)).toEqual([
      'arrive-a', 'board-a', 'board-a-second', 'arrive-z-early', 'arrive-z',
    ]);
    expect(calendar.currentTick).toBe(3);
    expect(calendar.peek()?.payload).toBe('leave-b');
  });

  it('cancels generations lazily and rejects stale replacement tokens', () => {
    const calendar = new EventCalendar<string>();
    const oldGeneration = calendar.generationFor('guest-1', 'route');
    calendar.schedule({ tick: 10, entityId: 'guest-1', key: 'route', payload: 'old', generation: oldGeneration });
    const replacement = calendar.generationFor('guest-1', 'route');
    calendar.schedule({ tick: 10, entityId: 'guest-1', key: 'route', payload: 'new', generation: replacement });
    expect(calendar.size).toBe(1);
    expect(calendar.advanceTo(10).map((event) => event.payload)).toEqual(['new']);
    expect(() => calendar.schedule({ tick: 11, entityId: 'guest-1', key: 'route', payload: 'stale', generation: oldGeneration }))
      .toThrow(/stale/i);

    const finalGeneration = calendar.generationFor('guest-1', 'route');
    calendar.schedule({ tick: 20, entityId: 'guest-1', key: 'route', payload: 'cancelled', generation: finalGeneration });
    calendar.cancelGeneration(finalGeneration);
    expect(calendar.isEmpty).toBe(true);
    expect(calendar.advanceTo(20)).toEqual([]);
  });

  it('reports live size even when a cancelled entry is not the heap root', () => {
    const calendar = new EventCalendar<string>();
    const root = calendar.generationFor('guest-root', 'visit');
    const child = calendar.generationFor('guest-child', 'visit');
    calendar.schedule({ tick: 1, entityId: 'guest-root', key: 'visit', payload: 'root', generation: root });
    calendar.schedule({ tick: 20, entityId: 'guest-child', key: 'visit', payload: 'child', generation: child });
    calendar.cancelGeneration(child);
    expect(calendar.size).toBe(1);
    expect(calendar.pop()?.payload).toBe('root');
    expect(calendar.pop()).toBeUndefined();
  });

  it('does not expose mutable heap keys or generation tokens', () => {
    const calendar = new EventCalendar<string>();
    const event = calendar.schedule({ tick: 1, entityId: 'guest', key: 'visit', payload: 'visit' });
    expect(Object.isFrozen(event)).toBe(true);
    expect(Object.isFrozen(event.generationToken)).toBe(true);
    expect(() => { (event as { tick: number }).tick = 9; }).toThrow(TypeError);
    expect(calendar.advanceTo(2).map((entry) => entry.payload)).toEqual(['visit']);
  });

  it('has a deterministic projection and checksum independent of payload key insertion order', () => {
    const left = new EventCalendar<object>();
    const right = new EventCalendar<object>();
    left.schedule({ tick: 2, priority: 1, entityId: 'guest', key: 'visit', payload: { b: 2, a: 1 } });
    right.schedule({ tick: 2, priority: 1, entityId: 'guest', key: 'visit', payload: { a: 1, b: 2 } });
    expect(left.stateProjection()).toEqual(right.stateProjection());
    expect(left.checksum()).toBe(right.checksum());
  });

  it('preserves delimiter characters in generation identities', () => {
    const calendar = new EventCalendar<string>();
    const token = calendar.generationFor('guest|east:1', 'route|next:2');
    calendar.schedule({ tick: 4, entityId: 'guest|east:1', key: 'route|next:2', payload: 'visit', generation: token });
    expect(calendar.stateProjection().generations).toEqual([token]);
    expect(calendar.advanceTo(4).map((event) => event.payload)).toEqual(['visit']);
  });

  it('produces the same state and event sequence for large and one-second advances', () => {
    const large = seededCalendar();
    const sliced = seededCalendar();
    const largeEvents = large.advanceTo(12).map((event) => event.payload);
    const slicedEvents: string[] = [];
    for (let tick = 1; tick <= 12; tick += 1) {
      slicedEvents.push(...sliced.advanceTo(tick).map((event) => event.payload));
    }
    expect(slicedEvents).toEqual(largeEvents);
    expect(sliced.stateProjection()).toEqual(large.stateProjection());
    expect(sliced.checksum()).toBe(large.checksum());
  });

  it('dispatches events scheduled by handlers without changing target-tick semantics', () => {
    const large = new EventCalendar<string>();
    const sliced = new EventCalendar<string>();
    for (const calendar of [large, sliced]) {
      calendar.schedule({ tick: 2, priority: 1, entityId: 'guest', key: 'start', payload: 'start' });
    }
    const dispatch = (calendar: EventCalendar<string>, event: { payload: string }) => {
      if (event.payload === 'start') calendar.schedule({ tick: 5, priority: 1, entityId: 'guest', key: 'finish', payload: 'finish' });
    };
    const largeEvents = large.advanceTo(8, (event) => dispatch(large, event)).map((event) => event.payload);
    const slicedEvents: string[] = [];
    for (let tick = 1; tick <= 8; tick += 1) {
      slicedEvents.push(...sliced.advanceTo(tick, (event) => dispatch(sliced, event)).map((event) => event.payload));
    }
    expect(slicedEvents).toEqual(largeEvents);
    expect(sliced.stateProjection()).toEqual(large.stateProjection());
  });
});
