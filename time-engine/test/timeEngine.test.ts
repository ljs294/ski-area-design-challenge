import { describe, expect, it } from 'vitest';
import {
  DEFAULT_TIME_CONFIG,
  MINUTES_PER_WEEK,
  advanceClock,
  advanceSummerPeriod,
  advanceToBoundary,
  confirmSeasonTransition,
  createClock,
  createTimeSnapshot,
  restoreTimeSnapshot,
} from '../src/timeEngine';

function startWinter() {
  let clock = createClock();
  for (let i = 0; i < DEFAULT_TIME_CONFIG.summerPeriods; i += 1) {
    clock = advanceSummerPeriod(clock).clock;
  }
  return confirmSeasonTransition(clock).clock;
}

describe('time engine', () => {
  it('starts in the first summer planning period', () => {
    const clock = createClock();
    expect(clock.season).toBe('summer');
    expect(clock.summerPeriod).toBe(1);
    expect(clock.runState).toBe('paused');
  });

  it('moves through summer and begins winter on Monday at 5 AM', () => {
    const winter = startWinter();
    expect(winter.season).toBe('winter');
    expect(winter.winterWeek).toBe(1);
    expect(winter.weekday).toBe(1);
    expect(winter.minuteOfDay).toBe(300);
    expect(winter.runState).toBe('paused');
    expect(winter.calendarDate).toBe('2026-11-02T05:00:00.000Z');
  });

  it('defines a winter week as exactly 10,080 minutes', () => {
    const clock = startWinter();
    const result = advanceClock(clock, MINUTES_PER_WEEK);
    expect(result.simulatedMinutesAdvanced).toBe(MINUTES_PER_WEEK);
    expect(result.clock.winterWeek).toBe(2);
    expect(result.clock.weekday).toBe(clock.weekday);
    expect(result.clock.minuteOfDay).toBe(clock.minuteOfDay);
  });

  it('produces the same state when advancing batched or minute by minute', () => {
    const start = startWinter();
    const batched = advanceClock(start, 3_000).clock;
    let stepped = start;
    for (let i = 0; i < 3_000; i += 1) stepped = advanceClock(stepped, 1).clock;
    expect(stepped).toEqual(batched);
  });

  it('skip week retains weekday and time', () => {
    const start = advanceClock(startWinter(), 2_345).clock;
    const skipped = advanceToBoundary(start, 'next-week').clock;
    expect(skipped.weekday).toBe(start.weekday);
    expect(skipped.minuteOfDay).toBe(start.minuteOfDay);
  });

  it('stops at summer when skipping the winter season', () => {
    const start = advanceClock(startWinter(), 5_000).clock;
    const result = advanceToBoundary(start, 'next-season');
    expect(result.clock.season).toBe('summer');
    expect(result.clock.summerPeriod).toBe(1);
    expect(result.clock.runState).toBe('paused');
    expect(result.events.some((event) => event.type === 'seasonEnded')).toBe(true);
  });

  it('skips to the equivalent point in the following winter year', () => {
    const start = advanceClock(startWinter(), 12_345).clock;
    const nextYear = advanceToBoundary(start, 'next-year').clock;
    expect(nextYear.season).toBe('winter');
    expect(nextYear.resortYear).toBe(start.resortYear + 1);
    expect(nextYear.winterWeek).toBe(start.winterWeek);
    expect(nextYear.weekday).toBe(start.weekday);
    expect(nextYear.minuteOfDay).toBe(start.minuteOfDay);
  });

  it('round trips a snapshot and always restores paused', () => {
    const running = {
      ...advanceClock(startWinter(), 999).clock,
      runState: 'running' as const,
      speed: 8 as const,
    };
    const restored = restoreTimeSnapshot(createTimeSnapshot(running));
    expect(restored).toEqual({ ...running, runState: 'paused' });
  });

  it('rejects incompatible snapshots', () => {
    const snapshot = { ...createTimeSnapshot(createClock()), schemaVersion: 2 };
    expect(() => restoreTimeSnapshot(snapshot)).toThrow(/schema/i);
  });
});
