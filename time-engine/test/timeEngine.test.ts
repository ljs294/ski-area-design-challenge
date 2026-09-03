import { describe, expect, it } from 'vitest';
import {
  DEFAULT_TIME_CONFIG,
  MINUTES_PER_WEEK,
  advanceClock,
  advanceSummerPeriod,
  advanceSummerToSeptember,
  advanceToBoundary,
  confirmSeasonTransition,
  createClock,
  createTimeSnapshot,
  restoreTimeSnapshot,
  scaledSimulationMinutes,
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
    const snapshot = { ...createTimeSnapshot(createClock()), schemaVersion: 3 };
    expect(() => restoreTimeSnapshot(snapshot)).toThrow(/schema/i);
  });

  it('maps real elapsed time through the configured scale and speed multiplier', () => {
    expect(scaledSimulationMinutes(DEFAULT_TIME_CONFIG.realSecondsPerWinterWeek * 1_000, 1)).toBe(MINUTES_PER_WEEK);
    expect(scaledSimulationMinutes(1_000, 8)).toBeCloseTo(
      MINUTES_PER_WEEK / DEFAULT_TIME_CONFIG.realSecondsPerWinterWeek * 8,
    );
  });

  it('completes planning in one exact skip to resort-local September 1', () => {
    const config = { ...DEFAULT_TIME_CONFIG, timezone: 'America/Los_Angeles',
      initialSummerStart: '2026-05-01T07:00:00.000Z' };
    const result = advanceSummerToSeptember(createClock(config), config);
    expect(result.clock.calendarDate).toBe('2026-09-01T07:00:00.000Z');
    expect(result.clock.season).toBe('winter');
    expect(result.clock.winterWeek).toBe(1);
    expect(result.clock.runState).toBe('paused');
    expect(result.events.map((event) => event.type)).toEqual([
      'summerPeriodEnded', 'seasonEnded', 'seasonStarted', 'weekStarted',
    ]);
  });

  it('derives winter start and daily phases in resort local time', () => {
    const config = { ...DEFAULT_TIME_CONFIG, timezone: 'America/Los_Angeles',
      initialSummerStart: '2026-05-01T07:00:00.000Z' };
    let clock = createClock(config);
    for (let index = 0; index < config.summerPeriods; index += 1) {
      clock = advanceSummerPeriod(clock, config).clock;
    }
    const winter = confirmSeasonTransition(clock, config).clock;
    expect(winter.calendarDate).toBe('2026-11-02T13:00:00.000Z');
    expect(winter.minuteOfDay).toBe(300);
    expect(winter.weekday).toBe(1);
    expect(winter.timezone).toBe('America/Los_Angeles');
  });

  it('advances to the next local day across spring-forward', () => {
    const config = { ...DEFAULT_TIME_CONFIG, timezone: 'America/Los_Angeles' };
    const start = { ...createClock(config), season: 'winter' as const, winterWeek: 1,
      summerPeriod: null, seasonStartedAt: '2027-03-08T13:00:00.000Z',
      calendarDate: '2027-03-14T08:00:00.000Z' };
    const result = advanceToBoundary(start, 'next-day', config);
    expect(result.simulatedMinutesAdvanced).toBe(1_380);
    expect(result.clock.calendarDate).toBe('2027-03-15T07:00:00.000Z');
  });
});
