import { describe, expect, it } from 'vitest';
import {
  DEFAULT_TIME_CONFIG,
  MINUTES_PER_WEEK,
  SECONDS_PER_WEEK,
  SIMULATION_SPEED_RATES,
  advanceClock,
  advanceClockSeconds,
  advanceSummerPeriod,
  advanceToBoundary,
  confirmSeasonTransition,
  createClock,
  createTimeSnapshot,
  normalizeSimulationSpeed,
  restoreTimeSnapshot,
  scaledSimulationMinutes,
  scaledSimulationSeconds,
  simulationRate,
} from '../src/timeEngine';

function startWinter(config = DEFAULT_TIME_CONFIG) {
  let clock = createClock(config);
  for (let i = 0; i < config.summerPeriods; i += 1) clock = advanceSummerPeriod(clock, config).clock;
  return confirmSeasonTransition(clock, config).clock;
}

describe('time engine v3', () => {
  it('starts paused in deliberate summer planning', () => {
    const clock = createClock();
    expect(clock.schemaVersion).toBe(3);
    expect(clock.season).toBe('summer');
    expect(clock.summerPeriod).toBe(1);
    expect(clock.winterWeek).toBeNull();
    expect(clock.elapsedSimSecond).toBe(0);
    expect(clock.runState).toBe('paused');
    expect(clock.speed).toBe('normal');
  });

  it('begins winter at the first composite operating day', () => {
    const winter = startWinter();
    expect(winter.season).toBe('winter');
    expect(winter.winterWeek).toBe(1);
    expect(winter.weekSecond).toBe(0);
    expect(winter.elapsedSimSecond).toBe(0);
    expect(winter.minuteOfDay).toBe(480);
    expect(winter.calendarDate).toBe('2026-11-02T08:00:00.000Z');
    expect(winter.runState).toBe('paused');
  });

  it('uses exactly 43,200 seconds per displayed week', () => {
    const start = startWinter();
    const result = advanceClockSeconds(start, SECONDS_PER_WEEK);
    expect(result.simulatedSecondsAdvanced).toBe(SECONDS_PER_WEEK);
    expect(result.simulatedMinutesAdvanced).toBe(720);
    expect(result.clock.winterWeek).toBe(2);
    expect(result.clock.weekSecond).toBe(0);
    expect(result.clock.elapsedSimSecond).toBe(SECONDS_PER_WEEK);
    expect(result.clock.minuteOfDay).toBe(480);
    expect(result.clock.weekday).toBe(start.weekday);
  });

  it('derives the operating local time continuously without rounding', () => {
    const result = advanceClockSeconds(startWinter(), 3_600.25);
    expect(result.clock.elapsedSimSecond).toBe(3_600.25);
    expect(result.clock.weekSecond).toBe(3_600.25);
    expect(result.clock.calendarDate).toBe('2026-11-02T09:00:00.250Z');
    expect(result.clock.minuteOfDay).toBe(540);
  });

  it('is deterministic across worker slicing and legacy minute calls', () => {
    const start = startWinter();
    const batched = advanceClockSeconds(start, 12_345.5).clock;
    let sliced = start;
    for (let left = 12_345.5; left > 0; left -= 37.25) {
      sliced = advanceClockSeconds(sliced, Math.min(37.25, left)).clock;
    }
    expect(sliced).toEqual(batched);
    expect(advanceClock(start, 60).clock).toEqual(advanceClockSeconds(start, 3_600).clock);
  });

  it('exposes the approved named speed tiers and exact rates', () => {
    expect(SIMULATION_SPEED_RATES).toEqual({ slow: 30, normal: 60, fast: 240, ultrafast: 960 });
    expect(['slow', 'normal', 'fast', 'ultrafast'].map(simulationRate)).toEqual([30, 60, 240, 960]);
    expect(normalizeSimulationSpeed(1)).toBe('normal');
    expect(normalizeSimulationSpeed(2)).toBe('fast');
    expect(normalizeSimulationSpeed(4)).toBe('ultrafast');
    expect(normalizeSimulationSpeed(8)).toBe('ultrafast');
    expect(scaledSimulationSeconds(1_000, 'slow')).toBe(30);
    expect(scaledSimulationMinutes(1_000, 'normal')).toBe(1);
    expect(scaledSimulationSeconds(24 * 60 * 1_000, 'slow')).toBe(SECONDS_PER_WEEK);
    expect(scaledSimulationSeconds(12 * 60 * 1_000, 'normal')).toBe(SECONDS_PER_WEEK);
    expect(scaledSimulationSeconds(3 * 60 * 1_000, 'fast')).toBe(SECONDS_PER_WEEK);
    expect(scaledSimulationSeconds(45 * 1_000, 'ultrafast')).toBe(SECONDS_PER_WEEK);
  });

  it('keeps summer period advancement deliberate and non-continuous', () => {
    const summer = createClock();
    const result = advanceClockSeconds(summer, 10_000);
    expect(result.simulatedSecondsAdvanced).toBe(0);
    expect(result.clock).toEqual(summer);
    expect(advanceSummerPeriod(summer).clock.summerPeriod).toBe(2);
  });

  it('writes v3 snapshots paused and migrates numeric v2 speed values', () => {
    const running = { ...advanceClockSeconds(startWinter(), 999.5).clock,
      runState: 'running' as const, speed: 'ultrafast' as const };
    const snapshot = createTimeSnapshot(running);
    expect(snapshot.schemaVersion).toBe(3);
    expect(snapshot.clock.runState).toBe('paused');
    expect(restoreTimeSnapshot(snapshot)).toEqual({ ...running, runState: 'paused' });

    const legacy = {
      schemaVersion: 2, configVersion: 2,
      clock: {
        schemaVersion: 2, timezone: 'UTC', resortYear: 1, completedWinterSeasons: 0,
        season: 'winter', seasonStartedAt: '2026-11-02T05:00:00.000Z',
        summerPeriod: null, winterWeek: 2, absoluteGameMinute: 10_000,
        calendarDate: '2026-11-09T12:00:00.000Z', minuteOfDay: 720, weekday: 1,
        dailyPhase: 'operating', speed: 2, runState: 'running', transitionPending: null,
      },
    };
    const restored = restoreTimeSnapshot(legacy);
    expect(restored.schemaVersion).toBe(3);
    expect(restored.speed).toBe('fast');
    expect(restored.winterWeek).toBe(2);
    expect(restored.weekSecond).toBe(14_400);
    expect(restored.runState).toBe('paused');

    // v2 stored the player-facing tier as a numeric multiplier.  Keep the
    // mapping explicit at the persistence boundary, including the two old
    // fast encodings that now collapse into Ultrafast.
    for (const [legacySpeed, expectedSpeed] of [[1, 'normal'], [2, 'fast'], [4, 'ultrafast'], [8, 'ultrafast']] as const) {
      const migrated = restoreTimeSnapshot({ ...legacy,
        clock: { ...legacy.clock, speed: legacySpeed },
      });
      expect(migrated.speed).toBe(expectedSpeed);
      expect(migrated.runState).toBe('paused');
    }
  });

  it('rejects unknown snapshot schemas and retains compatibility constants', () => {
    expect(MINUTES_PER_WEEK).toBe(10_080);
    expect(() => restoreTimeSnapshot({ schemaVersion: 4, configVersion: 4, clock: {} })).toThrow(/schema/i);
  });

  it('skips a composite week without changing the witnessed time of day', () => {
    const start = advanceClockSeconds(startWinter(), 2_345.75).clock;
    const skipped = advanceToBoundary(start, 'next-week').clock;
    expect(skipped.weekday).toBe(start.weekday);
    expect(skipped.weekSecond).toBe(0);
    expect(skipped.winterWeek).toBe(2);
  });

  it('completes the 24-week winter at summer and pauses', () => {
    const result = advanceToBoundary(startWinter(), 'next-summer');
    expect(result.simulatedSecondsAdvanced).toBe(24 * SECONDS_PER_WEEK);
    expect(result.clock.season).toBe('summer');
    expect(result.clock.winterWeek).toBeNull();
    expect(result.clock.runState).toBe('paused');
    expect(result.events.filter((event) => event.type === 'weekEnded')).toHaveLength(24);
  });

  it('normalizes a persisted exact winter boundary instead of restoring week 25', () => {
    const boundary = { ...startWinter(), elapsedSimSecond: 24 * SECONDS_PER_WEEK,
      winterWeek: 24, runState: 'paused' as const };
    const restored = restoreTimeSnapshot({ schemaVersion: 3, configVersion: 3, clock: boundary });
    expect(restored.season).toBe('summer');
    expect(restored.winterWeek).toBeNull();
    expect(restored.completedWinterSeasons).toBe(1);
  });
});
