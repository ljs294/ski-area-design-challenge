import { describe, expect, it, vi } from 'vitest';
import { createDualClock, projectDualClock } from '../dualClock/clock';
import type { SavedWeatherRun } from '../types/gameSave';
import { weatherRunSnapshot } from './useGameSimulation';
import { dualSimulationSnapshot } from './useResortSimulation';

const weatherRun = {
  terrainKey: 'terrain', terrainBinding: 'binding', packageContentHash: 'weather', seed: 'seed',
  generatorVersion: 2, configurationVersion: 2, localStartAt: '2026-05-01T04:00:00.000Z', cursorHour: 0,
} as SavedWeatherRun;

describe('resort simulation snapshot composition', () => {
  it('does not invoke legacy clock serialization for cumulative winter elapsed time', () => {
    const dual = createDualClock('2026-05-01T04:00:00.000Z', 'America/New_York');
    const elapsed = (Date.parse(dual.winterStart) - Date.parse(dual.at)) / 1000 + 90;
    const clock = projectDualClock({ ...dual, at: new Date(Date.parse(dual.winterStart) + 90_000).toISOString(),
      macroSecond: elapsed as typeof dual.macroSecond, season: 'winter', paused: false });
    const legacy = {
      snapshot: vi.fn(() => { throw new Error('Invalid elapsed simulation second'); }),
      snapshotWeatherRun: vi.fn((at: string) => weatherRunSnapshot(weatherRun, at)),
    };
    const snapshot = dualSimulationSnapshot(clock, legacy.snapshotWeatherRun);

    expect(legacy.snapshot).not.toHaveBeenCalled();
    expect(legacy.snapshotWeatherRun).toHaveBeenCalledWith(clock.calendarDate);
    expect(snapshot.time.clock).toBe(clock);
    expect(snapshot.time.clock.elapsedSimSecond).toBeGreaterThan(24 * 43_200);
    expect(snapshot.weatherRun).toMatchObject({ packageContentHash: 'weather', seed: 'seed',
      cursorHour: Math.floor((Date.parse(clock.calendarDate) - Date.parse(weatherRun.localStartAt)) / 3_600_000) });
  });

  it('supports later seasons and omits unavailable weather', () => {
    const dual = createDualClock('2027-11-01T13:00:00.000Z', 'America/New_York');
    const clock = projectDualClock({ ...dual, macroSecond: 40_000_000 as typeof dual.macroSecond, season: 'winter' });
    expect(dualSimulationSnapshot(clock, () => undefined)).toEqual({
      time: { schemaVersion: 3, configVersion: 1, clock },
    });
  });
});
