import { describe, expect, it } from 'vitest';
import { addWeatherLocalTime, weatherLocalParts } from './localTime';
import { advanceWeatherPlayback, createWeatherPlayback, skipWeatherPlayback } from './playback';
import {
  detectWeatherEvents,
  type SyntheticWeatherPlan,
  type WeatherDataPackage,
  type WeatherReferenceHour,
} from './weatherModel';
import {
  createWeatherSession,
  forecastForSession,
  historicalAtSession,
  resolveWeatherHour,
  seekWeatherSession,
  weatherAtSession,
} from './weatherSession';

function hour(at: string, overrides: Partial<WeatherReferenceHour> = {}): WeatherReferenceHour {
  return {
    at, temperatureC: -4, wetBulbC: -5, humidityPct: 82, precipitationMm: 0,
    precipitationType: 'none', snowfallCm: 0, windSpeedKph: 18, windGustKph: 30,
    windDirectionDeg: 230, cloudCoverPct: 30, visibilityKm: 20, pressureHpa: 1010, radiationWm2: 250,
    ...overrides,
  };
}

function legacyPackage(timezone = 'America/New_York'): WeatherDataPackage {
  const hours = Array.from({ length: 72 }, (_, index) => hour(new Date(Date.UTC(1992, 0, 1, index)).toISOString()));
  return {
    manifest: {
      schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone,
      historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture',
      sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'fixture', complete: true,
      createdAt: '2026-01-01T00:00:00.000Z',
    },
    historicalYears: [{ year: 1992, hours }],
  };
}

function planForPlayback(startsAt: string, endsAt: string): SyntheticWeatherPlan {
  return {
    seed: 'test', startsAt, endsAt, timezone: 'America/New_York', packageContentHash: 'fixture', generatorVersion: 2,
    hours: [hour(startsAt), hour(endsAt)], events: [],
  };
}

describe('WeatherSession', () => {
  it('is deterministic and recomputes forecasts after backward seeking', () => {
    const weatherPackage = legacyPackage('UTC');
    const sessionA = createWeatherSession(weatherPackage, { seed: 'mountain', startsAt: '2026-01-01T00:00:00.000Z', days: 2 });
    const sessionB = createWeatherSession(weatherPackage, { seed: 'mountain', startsAt: '2026-01-01T00:00:00.000Z', days: 2 });
    expect(sessionA.plan).toEqual(sessionB.plan);
    const early = seekWeatherSession(sessionA, '2026-01-01T06:00:00.000Z');
    const late = seekWeatherSession(sessionA, '2026-01-02T06:00:00.000Z');
    expect(late.forecast.hours.length).toBeGreaterThan(0);
    expect(seekWeatherSession(sessionA, early.cursor)).toEqual(early);
    expect(forecastForSession(sessionA, early.cursor, 12).hours[0]).toEqual(weatherAtSession(sessionA, early.cursor));
  });

  it('resolves wet-bulb, wind vectors, phase, and radiation for snow-cover consumers', () => {
    const resolved = resolveWeatherHour(hour('2026-06-21T17:00:00.000Z', {
      temperatureC: -0.5, wetBulbC: Number.NaN, humidityPct: 92, precipitationMm: 1.2,
      precipitationType: 'rain', cloudCoverPct: 0, radiationWm2: 800,
    }), { latitude: 44.1, longitude: -71.2 });
    expect(resolved.wetBulbC).toBeLessThan(0);
    expect(resolved.precipitationType).toBe('snow');
    expect(resolved.windUms).toBeTypeOf('number');
    expect(resolved.globalRadiationWm2).toBe(800);
    expect(resolved.directRadiationWm2).toBeGreaterThan(0);
    expect(resolved.diffuseRadiationWm2).toBeGreaterThan(0);
    expect(resolved.cloudTransmissionPct).toBeGreaterThan(95);
    expect(resolved.solarElevationDeg).toBeGreaterThan(0);
  });

  it('classifies storm style independently of temperature and precipitation phase', () => {
    const storm = Array.from({ length: 6 }, (_, index) => hour(`2026-01-01T0${index}:00:00.000Z`, {
      precipitationMm: 2, precipitationType: 'snow', windSpeedKph: 30, windGustKph: 65, cloudCoverPct: 100,
    }));
    const warmStorm = storm.map((entry) => ({ ...entry, temperatureC: 12, wetBulbC: 10, precipitationType: 'rain' as const }));
    const coldStyle = detectWeatherEvents(storm).find((event) => event.type === 'storm')?.stormStyle;
    const warmStyle = detectWeatherEvents(warmStorm).find((event) => event.type === 'storm')?.stormStyle;
    expect(coldStyle).toBe('convective');
    expect(warmStyle).toBe(coldStyle);
  });

  it('retains both repeated fall-back local hours for historical lookup and analog selection', () => {
    const startsAt = '2020-11-01T04:00:00.000Z'; // 00:00 EDT, before the repeated 01:00
    const fallbackHours = Array.from({ length: 25 }, (_, index) => hour(new Date(new Date(startsAt).getTime() + index * 3_600_000).toISOString(), {
      temperatureC: index,
      wetBulbC: index - 1,
    }));
    const weatherPackage: WeatherDataPackage = {
      manifest: {
        schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'America/New_York',
        historicalStartYear: 2020, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture',
        sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'fixture', complete: true,
        createdAt: '2026-01-01T00:00:00.000Z',
      },
      historicalYears: [{ year: 2020, hours: fallbackHours }],
    };
    const session = createWeatherSession(weatherPackage, { seed: 'fallback', startsAt, days: 1 });
    const firstOne = historicalAtSession(session, 2020, '2020-11-01T05:00:00.000Z'); // 01:00 EDT
    const secondOne = historicalAtSession(session, 2020, '2020-11-01T06:00:00.000Z'); // 01:00 EST
    expect(firstOne?.temperatureC).toBe(1);
    expect(secondOne?.temperatureC).toBe(2);
    const simulatedFirst = session.plan.hours.find((entry) => entry.at === '2020-11-01T05:00:00.000Z')!;
    const simulatedSecond = session.plan.hours.find((entry) => entry.at === '2020-11-01T06:00:00.000Z')!;
    expect(simulatedSecond.temperatureC - simulatedFirst.temperatureC).toBeCloseTo(1, 8);
  });

  it('compares the same local clock across historical U.S. DST rule changes', () => {
    const weatherPackage: WeatherDataPackage = {
      manifest: {
        schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'America/New_York',
        historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture',
        sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'fixture', complete: true,
        createdAt: '2026-01-01T00:00:00.000Z',
      },
      // March 15 was standard time in 1991, but daylight time in 2026.
      historicalYears: [{ year: 1991, hours: [hour('1991-03-15T17:00:00.000Z', { temperatureC: 7 })] }],
    };
    const session = createWeatherSession(weatherPackage, {
      seed: 'dst-rules', startsAt: '2026-03-15T16:00:00.000Z', days: 1,
    });
    expect(historicalAtSession(session, 1991, '2026-03-15T16:00:00.000Z')?.temperatureC).toBe(7);
  });
});

describe('local weather playback', () => {
  it('uses compatible DST behavior and preserves wall clock time for day skips', () => {
    const beforeGap = '2024-03-09T07:30:00.000Z'; // 02:30 EST
    const afterGap = addWeatherLocalTime(beforeGap, 'America/New_York', { days: 1 });
    expect(weatherLocalParts(afterGap, 'America/New_York')).toMatchObject({ year: 2024, month: 3, day: 10, hour: 3, minute: 30 });
    const plan = planForPlayback(beforeGap, '2024-03-12T07:30:00.000Z');
    const state = skipWeatherPlayback(plan, createWeatherPlayback(plan, 1992), 'day');
    expect(state.cursor).toBe(afterGap);
  });

  it('clamps month end and accumulates playback fractions without pausing early', () => {
    const startsAt = '2024-01-31T15:00:00.000Z'; // 10:00 EST
    const plan = planForPlayback(startsAt, '2024-03-03T15:00:00.000Z');
    const start = createWeatherPlayback(plan, 1992);
    const month = skipWeatherPlayback(plan, start, 'month');
    expect(weatherLocalParts(month.cursor, 'America/New_York')).toMatchObject({ year: 2024, month: 2, day: 29, hour: 10 });
    const once = advanceWeatherPlayback(plan, { ...start, running: true, speed: 2 }, 500);
    const twice = advanceWeatherPlayback(plan, once, 500);
    expect(new Date(twice.cursor).getTime() - new Date(start.cursor).getTime()).toBe(120_000);
  });
});
