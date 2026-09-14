import { describe, expect, it } from 'vitest';
import { advanceDestination, createDualClock, microRate, projectDualClock, DUAL_MICRO_RUNTIME_MULTIPLIER } from './clock';
import { DEFAULT_DUAL_CONFIG, DUAL_SPEEDS } from './model';

describe('dual clocks', () => {
  it('keeps the calendar profile unchanged while accelerating every interactive micro rate by 3x', () => {
    expect(8 * 3600 / DEFAULT_DUAL_CONFIG.macroSecondsPerSecond).toBe(720);
    const clock = createDualClock('2026-11-02T08:00:00Z', 'UTC');
    const expected = [3, 5.25, 8.25, 24, 48, 192];
    for (const speed of DUAL_SPEEDS) {
      clock.speed = speed;
      const index = DUAL_SPEEDS.indexOf(speed);
      expect(microRate(clock, false, DEFAULT_DUAL_CONFIG) * 40 * speed).toBe(expected[index]);
      expect(expected[index]).toBe(DEFAULT_DUAL_CONFIG.microRates[speed] * DUAL_MICRO_RUNTIME_MULTIPLIER);
    }
    expect(DEFAULT_DUAL_CONFIG.microRates).toEqual({ 1: 1, 2: 1.75, 4: 2.75, 8: 8, 16: 16, 64: 64 });
    expect(projectDualClock(clock).calendarDate).toBe(clock.at);
  });
  it('uses the same accelerated base ratio for explicit headless calendar advances', () => {
    const clock = createDualClock('2026-11-02T08:00:00Z', 'UTC');
    for (const speed of DUAL_SPEEDS) {
      clock.speed = speed;
      expect(microRate(clock, true, DEFAULT_DUAL_CONFIG) * DEFAULT_DUAL_CONFIG.macroSecondsPerSecond)
        .toBe(DUAL_MICRO_RUNTIME_MULTIPLIER);
    }
  });
  it('preserves civil time across DST and clamps month ends', () => {
    const clock = createDualClock('2027-01-31T20:00:00Z', 'America/Los_Angeles');
    expect(advanceDestination(clock, 'month')).toBe('2027-02-28T20:00:00.000Z');
    clock.at = '2027-03-13T20:00:00Z';
    expect(advanceDestination(clock, 'day')).toBe('2027-03-14T19:00:00.000Z');
    expect(advanceDestination(clock, 'opening')).toBe('2027-03-14T15:00:00.000Z');
  });
  it('uses configured winter boundaries rather than representative weeks', () => {
    const clock = createDualClock('2026-11-02T08:00:00Z', 'UTC');
    expect(clock.season).toBe('winter');
    expect((Date.parse(clock.winterEnd) - Date.parse(clock.winterStart)) / 86400000).toBe(168);
    clock.at = new Date(Date.parse(clock.winterEnd) - 3600000).toISOString();
    expect(advanceDestination(clock, 'day')).toBe(clock.winterEnd);
    expect(advanceDestination(clock, 'month')).toBe(clock.winterEnd);
  });
});
