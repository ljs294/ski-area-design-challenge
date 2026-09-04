import { describe, expect, it } from 'vitest';
import { advanceSummerToSeptember, createClock } from '../../time-engine/src/timeEngine';
import { isDeveloperConsoleEnabled, parseDeveloperConsoleCommand, skipClockWithoutSimulation } from './developerConsoleCommands';

describe('developer console commands', () => {
  it('parses skip durations and utility commands', () => {
    expect(isDeveloperConsoleEnabled('?dev-console')).toBe(true);
    expect(isDeveloperConsoleEnabled('', { development: false, desktop: true })).toBe(true);
    expect(isDeveloperConsoleEnabled('', { development: false, desktop: false })).toBe(false);
    expect(parseDeveloperConsoleCommand('skip 30m')).toEqual({ kind: 'skip', minutes: 30 });
    expect(parseDeveloperConsoleCommand('skip 1.5 hours')).toEqual({ kind: 'skip', minutes: 90 });
    expect(parseDeveloperConsoleCommand('skip day')).toEqual({ kind: 'skip', minutes: 720 });
    expect(parseDeveloperConsoleCommand('skip 1w')).toEqual({ kind: 'skip', minutes: 720 });
    expect(parseDeveloperConsoleCommand('skip 45')).toEqual({ kind: 'skip', minutes: 45 });
    expect(parseDeveloperConsoleCommand('skip ahead 2h')).toEqual({ kind: 'skip', minutes: 120 });
    expect(parseDeveloperConsoleCommand('advance 15m')).toEqual({ kind: 'skip', minutes: 15 });
    expect(parseDeveloperConsoleCommand('time')).toEqual({ kind: 'time' });
    expect(() => parseDeveloperConsoleCommand('skip backwards')).toThrow(/Invalid duration/);
  });

  it('teleports only the clock to a paused future timestamp', () => {
    const before = advanceSummerToSeptember(createClock()).clock;
    const result = skipClockWithoutSimulation({ ...before, runState: 'running' }, 180);
    expect(result.after.absoluteGameMinute).toBe(before.absoluteGameMinute + 180);
    expect(result.after.minuteOfDay).toBe(before.minuteOfDay + 180);
    expect(result.after.runState).toBe('paused');
    expect(result.skippedMinutes).toBe(180);
    expect(before.absoluteGameMinute).not.toBe(result.after.absoluteGameMinute);
  });

  it('rejects skips while the resort is in summer planning', () => {
    expect(() => skipClockWithoutSimulation(createClock(), 60)).toThrow(/winter season/);
  });
});
