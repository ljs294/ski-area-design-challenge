import { describe, expect, it } from 'vitest';
import type { SimulationClock } from '../types/simulation';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import { GUEST_RENDER_STATUS_FLAGS, type GuestRenderFrame } from '../guestSimulation/guestRenderFrame';
import { guestRenderPointsFromCompactFrame } from './useGuestSimulationRuntime';
import { guestCheckpointMatchesOperatingWindow, guestOperatingWindow,
  guestOperatingWindowForWeek, guestSimulationWindowAfterClockDiscontinuity,
  guestSimulationWindowAfterDiscontinuity } from './guestRuntimeSchedule';

const winterClock = { absoluteGameMinute: 177_120, minuteOfDay: 0 } as SimulationClock;

describe('guest runtime operating schedule', () => {
  it('anchors a winter roster to elapsed seconds and a single 43,200-second week', () => {
    const clock = { elapsedSimSecond: 43_200 + 123.5, winterWeek: 2, season: 'winter' } as SimulationClock;
    expect(guestOperatingWindow(clock)).toEqual({ startTick: 43_200, endTick: 86_400 });
    expect(guestOperatingWindowForWeek(23)).toEqual({ startTick: 993_600, endTick: 1_036_800 });
  });

  it('does not derive a composite week window from ISO-date subtraction', () => {
    const clock = { elapsedSimSecond: 12.25, winterWeek: 1, calendarDate: '2038-03-14T07:00:00.000Z',
      season: 'winter' } as SimulationClock;
    expect(guestOperatingWindow(clock)).toEqual({ startTick: 0, endTick: 43_200 });
  });

  it('rebuilds a developer-skipped roster at the next demand bucket', () => {
    const operating = guestOperatingWindowForWeek(3);
    expect(guestSimulationWindowAfterClockDiscontinuity(operating, operating.startTick + 61,
      { revision: 4 }, 60)).toEqual({ startTick: operating.startTick + 120, endTick: operating.endTick });
    expect(guestSimulationWindowAfterClockDiscontinuity(operating, operating.endTick + 1,
      { revision: 5 }, 60)).toEqual({ startTick: operating.endTick, endTick: operating.endTick });
    expect(guestSimulationWindowAfterClockDiscontinuity(operating, operating.startTick,
      null, 60)).toEqual(operating);
  });

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

  it('projects compact frames by worker edge id and omits scheduled/departed guests', () => {
    const portal = { id: 'portal', lngLat: [0, 0] as [number, number] } as never;
    const skiEdge = { id: 'actual-edge', path: [[0, 0], [10, 0]] } as never;
    const network = { edges: [skiEdge], edgeById: new Map([['actual-edge', skiEdge]]) } as never;
    const snapshot = { guests: [{ id: 'guest-000001', ordinal: 0 }], network: {
      edges: [{ id: 'actual-edge' }],
    } } as unknown as GuestSimulationEngineSnapshot;
    const frame: GuestRenderFrame = { ids: new Uint32Array([1, 2]), guestIds: new Uint32Array([1, 2]),
      edgeIndices: new Int32Array([0, -1]), progress: new Float32Array([0.5, 0]),
      statusFlags: new Uint32Array([GUEST_RENDER_STATUS_FLAGS.skiing, GUEST_RENDER_STATUS_FLAGS.scheduled]),
      bytesPerGuest: 16, byteLength: 32 };
    expect(guestRenderPointsFromCompactFrame(frame, snapshot, network, portal)).toEqual([
      { id: 'guest-000001', lng: 5, lat: 0, status: 'skiing' },
    ]);
  });
});
