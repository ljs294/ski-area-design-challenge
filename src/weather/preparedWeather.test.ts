import { describe, expect, it, vi } from 'vitest';
import type { WeatherDataPackage, WeatherReferenceHour } from './weatherModel';

const storageMocks = vi.hoisted(() => ({
  desktopRead: vi.fn(),
  desktopWrite: vi.fn(),
}));

vi.mock('../desktopBridge', () => ({
  desktop: {
    preparedWeather: { read: storageMocks.desktopRead, write: storageMocks.desktopWrite },
  },
}));

import {
  annualWeatherSeed,
  WEATHER_YEAR_CONFIGURATION_VERSION,
  weatherYearDayCount,
  weatherYearStart,
} from './annualWeather';
import { buildPreparedWeather } from './preparedWeather';
import { isPreparedAnnualWeather } from './preparedWeatherModel';
import { encodePreparedWeather } from './preparedWeatherCodec';
import { resolveWeatherHour } from './weatherSession';
import { readPreparedWeather, writePreparedWeather } from '../preparedWeatherStorageClient';

const weatherPackage: WeatherDataPackage = {
  manifest: {
    schemaVersion: 1,
    terrainKey: 'terrain',
    terrainBinding: 'binding',
    timezone: 'America/New_York',
    historicalStartYear: 2023,
    historicalEndYear: 2024,
    quality: 'limited',
    sourceSummary: 'fixture',
    sourceVersion: 'fixture',
    generatorVersion: 2,
    contentHash: 'fixture',
    complete: true,
    createdAt: '2024-01-01T00:00:00.000Z',
  },
  historicalYears: [
    { year: 2023, hours: [historicalHour('2023-09-01T04:00:00.000Z', 2.5)] },
    { year: 2024, hours: [historicalHour('2024-09-01T04:00:00.000Z', 4)] },
  ],
};

const identity = {
  packageContentHash: weatherPackage.manifest.contentHash,
  terrainBinding: weatherPackage.manifest.terrainBinding,
  seed: 'seed-1',
  year: 2024,
  timezone: weatherPackage.manifest.timezone,
  generatorVersion: weatherPackage.manifest.generatorVersion,
  configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION,
  cacheFormatVersion: 1 as const,
};

function historicalHour(at: string, snowfallCm: number): WeatherReferenceHour {
  return {
    at,
    temperatureC: -3,
    wetBulbC: -3,
    humidityPct: 80,
    precipitationMm: 1,
    precipitationType: 'snow',
    snowfallCm,
    windSpeedKph: 10,
    windGustKph: 15,
    windDirectionDeg: 180,
    cloudCoverPct: 50,
    visibilityKm: 20,
    pressureHpa: 1010,
    radiationWm2: 0,
  };
}

function localClock(at: string): string {
  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone: identity.timezone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    hourCycle: 'h23',
  }).formatToParts(new Date(at));
  const values = Object.fromEntries(parts.filter(({ type }) => type !== 'literal').map(({ type, value }) => [type, value]));
  return `${values.year}-${values.month}-${values.day}T${values.hour}`;
}

describe('buildPreparedWeather', () => {
  it('builds and serializes a real Sep-to-Sep annual weather cache across both DST transitions', async () => {
    storageMocks.desktopWrite.mockResolvedValue(true);

    const prepared = await buildPreparedWeather(weatherPackage, identity);
    const plan = prepared.session.plan;
    const startsAt = weatherYearStart(identity.year, identity.timezone);
    const nextStartsAt = weatherYearStart(identity.year + 1, identity.timezone);
    const expectedLength = (Date.parse(nextStartsAt) - Date.parse(startsAt)) / 3_600_000;

    expect(plan.seed).toBe(annualWeatherSeed(identity.seed, identity.year));
    expect(plan.startsAt).toBe(startsAt);
    expect(plan.endsAt).toBe(new Date(Date.parse(nextStartsAt) - 3_600_000).toISOString());
    expect(plan.hours).toHaveLength(expectedLength);
    expect(plan.hours).toHaveLength(weatherYearDayCount(identity.year) * 24);
    expect(plan.hours[0].at).toBe(startsAt);
    expect(plan.hours.at(-1)?.at).toBe(plan.endsAt);

    const localClocks = plan.hours.map(({ at }) => localClock(at));
    expect(localClocks.filter((clock) => clock === '2024-11-03T01')).toHaveLength(2);
    expect(localClocks.filter((clock) => clock === '2025-03-09T02')).toHaveLength(0);
    expect(localClocks.filter((clock) => clock === '2025-03-09T01')).toHaveLength(1);
    expect(localClocks.filter((clock) => clock === '2025-03-09T03')).toHaveLength(1);

    expect(prepared.resolvedHours).toEqual(plan.hours.map((hour) => resolveWeatherHour(hour)));
    expect(prepared.resolvedHours.every(({ provenance }) => provenance)).toBe(true);
    expect(prepared.averageAnnualSnowfallCm).toBe(2.5);
    expect(prepared.session).not.toHaveProperty('historicalYears');
    expect(prepared.session).not.toHaveProperty('weatherPackage');
    expect(isPreparedAnnualWeather(prepared, identity)).toBe(true);

    expect(await writePreparedWeather(prepared)).toBe(true);
    storageMocks.desktopRead.mockResolvedValue(await encodePreparedWeather(prepared));
    const restored = await readPreparedWeather(identity);
    expect(restored).toEqual(prepared);
    expect(restored).not.toBe(prepared);
  });
});
