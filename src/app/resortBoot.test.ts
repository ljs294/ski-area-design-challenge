import { describe, expect, it } from 'vitest';
import { canRevealLoad, isLoadReadinessReady, nextLoadSceneDrawCount, skipsInitialWeatherCheckpoint, weatherMutationBlocked } from './resortBoot';

const current = { generation: 4, mapReady: true, simulationRestored: true, weatherReady: false } as const;

describe('resort load readiness', () => {
  it('requires the current generation and two actual scene draws', () => {
    expect(isLoadReadinessReady(current, 4, 1)).toBe(false);
    expect(isLoadReadinessReady(current, 4, 2)).toBe(true);
    expect(isLoadReadinessReady(current, 5, 2)).toBe(false);
  });

  it('resets consecutive draws when any prerequisite becomes false', () => {
    expect(nextLoadSceneDrawCount(1, current, 4)).toBe(2);
    expect(nextLoadSceneDrawCount(2, { ...current, mapReady: false }, 4)).toBe(0);
    expect(nextLoadSceneDrawCount(2, { ...current, simulationRestored: false }, 4)).toBe(0);
  });

  it('keeps automatic reveal gated while allowing the explicit stalled-load action', () => {
    expect(isLoadReadinessReady({ ...current, mapReady: false }, 4, 99)).toBe(false);
    expect(isLoadReadinessReady({ ...current, simulationRestored: false }, 4, 99)).toBe(false);
    expect(canRevealLoad(false, { ...current, mapReady: false }, 4, 99)).toBe(false);
    expect(canRevealLoad(true, { ...current, mapReady: false }, 4, 0)).toBe(true);
  });

  it('ignores background warm completion and allows paused reveal before weather', () => {
    expect(isLoadReadinessReady(current, 4, 2)).toBe(true);
    expect(weatherMutationBlocked(true, current.weatherReady)).toBe(true);
    expect(weatherMutationBlocked(false, false)).toBe(false);
  });

  it('skips the initial checkpoint only while initial weather is still loading', () => {
    expect(skipsInitialWeatherCheckpoint(true, false, true)).toBe(true);
    expect(skipsInitialWeatherCheckpoint(true, false, false)).toBe(false);
    expect(skipsInitialWeatherCheckpoint(true, true, false)).toBe(false);
  });
});
