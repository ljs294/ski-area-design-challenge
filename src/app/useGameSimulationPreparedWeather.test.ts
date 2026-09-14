import { describe, expect, it, vi } from 'vitest';
import type { PreparedAnnualWeather, PreparedWeatherIdentity } from '../weather/preparedWeatherModel';
import type { ResolvedWeatherHour, WeatherDataPackage } from '../weather/weatherModel';
import {
  activePreparedForIdentity, commitActivePreparedWeather, preparedHoursWindow, resolveActivePreparedWeather,
} from './useGameSimulation';

const packageFixture = {} as WeatherDataPackage;
const identity = (year: number): PreparedWeatherIdentity => ({
  packageContentHash: 'content', terrainBinding: 'binding', seed: 'seed', year,
  timezone: 'America/New_York', generatorVersion: 2, configurationVersion: 2,
  cacheFormatVersion: 1,
});
const prepared = (weatherIdentity: PreparedWeatherIdentity): PreparedAnnualWeather => ({
  identity: weatherIdentity,
  session: { timezone: weatherIdentity.timezone, plan: { hours: [], events: [] } } as never,
  resolvedHours: [], averageAnnualSnowfallCm: null,
});

describe('active prepared weather reuse', () => {
  it('reuses an active year without invoking the resolver', async () => {
    const active = prepared(identity(2024));
    const resolver = vi.fn();
    const result = await resolveActivePreparedWeather(active, packageFixture, identity(2024), undefined, undefined, resolver);
    expect(result).toBe(active);
    expect(resolver).not.toHaveBeenCalled();
    expect(activePreparedForIdentity(active, identity(2024))).toBe(active);
  });

  it('resolves and commits one replacement for a year transition', async () => {
    const holder = { current: prepared(identity(2024)) };
    const next = prepared(identity(2025));
    const resolver = vi.fn().mockResolvedValue(next);
    const loaded = await resolveActivePreparedWeather(holder.current, packageFixture, identity(2025), undefined, undefined, resolver);
    expect(resolver).toHaveBeenCalledTimes(1);
    expect(commitActivePreparedWeather(holder, loaded, identity(2025))).toBe(true);
    expect(holder.current).toBe(next);
    expect(activePreparedForIdentity(holder.current, identity(2024))).toBeNull();
  });

  it('rejects stale or aborted preparation before changing the active holder', async () => {
    const previous = prepared(identity(2024));
    const holder = { current: previous };
    const next = prepared(identity(2025));
    const resolver = vi.fn().mockResolvedValue(next);
    await expect(resolveActivePreparedWeather(previous, packageFixture, identity(2025), undefined, () => false, resolver))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(commitActivePreparedWeather(holder, next, identity(2025), () => false)).toBe(false);
    expect(holder.current).toBe(previous);

    const controller = new AbortController();
    controller.abort();
    await expect(resolveActivePreparedWeather(previous, packageFixture, identity(2025), controller.signal, undefined, resolver))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(holder.current).toBe(previous);
  });

  it('keeps exactly one active payload as replacements commit', () => {
    const holder = { current: prepared(identity(2024)) };
    const first = prepared(identity(2025));
    const second = prepared(identity(2026));
    expect(commitActivePreparedWeather(holder, first, first.identity)).toBe(true);
    expect(commitActivePreparedWeather(holder, second, second.identity)).toBe(true);
    expect(holder.current).toBe(second);
  });

  it('selects only the inclusive UTC hour window across a year transition', () => {
    const hours = [
      { at: '2024-12-31T23:00:00.000Z' },
      { at: '2025-01-01T00:00:00.000Z' },
      { at: '2025-01-01T01:00:00.000Z' },
      { at: '2025-01-01T02:00:00.000Z' },
    ] as ResolvedWeatherHour[];
    const window = preparedHoursWindow(hours, '2024-12-31T23:30:00.000Z', '2025-01-01T00:30:00.000Z');
    expect(window.map((hour) => hour.at)).toEqual(hours.slice(0, 3).map((hour) => hour.at));
    expect(window).toHaveLength(3);
  });
});
