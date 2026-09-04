import { describe, expect, it } from 'vitest';
import type { SimulationClock } from '../types/simulation';
import { guestCheckpointMatchesOperatingWindow, guestOperatingWindow,
  guestSimulationWindowAfterDiscontinuity } from './guestRuntimeSchedule';

const winterClock = { absoluteGameMinute: 177_120, minuteOfDay: 0 } as SimulationClock;

describe('guest runtime operating schedule', () => {
  it('anchors the daily guest horizon to local resort operating hours', () => {
    const window = guestOperatingWindow(winterClock);
    expect(window).toEqual({ startTick: 10_656_000, endTick: 10_699_200 });
    expect(guestOperatingWindow({ ...winterClock, absoluteGameMinute: 177_900,
      minuteOfDay: 780 })).toEqual(window);
  });

  it('rejects a checkpoint from another operating day even when topology matches', () => {
    const current = { startTick: 10_656_000, endTick: 10_699_200 };
    const stale = { topologyRevision: 7,
      demandPlan: { startTick: current.startTick - 86_400, endTick: current.endTick - 86_400 } };
    expect(guestCheckpointMatchesOperatingWindow(stale, current, 7)).toBe(false);
    expect(guestCheckpointMatchesOperatingWindow({ ...stale,
      demandPlan: current }, current, 7)).toBe(true);
  });

  it('starts a fresh roster after a clock teleport without replaying skipped guest events', () => {
    const operating = guestOperatingWindow(winterClock);
    const localMidnightAbsoluteMinute = winterClock.absoluteGameMinute;
    expect(guestSimulationWindowAfterDiscontinuity(operating, localMidnightAbsoluteMinute, {
      absoluteGameMinute: localMidnightAbsoluteMinute + 10 * 60 + 3,
      localMidnightAbsoluteMinute }, 60)).toEqual({
      startTick: operating.startTick + 2 * 60 * 60 + 3 * 60, endTick: operating.endTick });
    expect(guestSimulationWindowAfterDiscontinuity(operating, localMidnightAbsoluteMinute, {
      absoluteGameMinute: localMidnightAbsoluteMinute - 1,
      localMidnightAbsoluteMinute: localMidnightAbsoluteMinute - 1_440,
    }, 600)).toEqual(operating);
  });
});
