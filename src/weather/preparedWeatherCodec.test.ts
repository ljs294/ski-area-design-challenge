import { describe, expect, it } from 'vitest';
import type { WeatherDataPackage, WeatherReferenceHour } from './weatherModel';
import { buildPreparedWeather } from './preparedWeather';
import { WEATHER_YEAR_CONFIGURATION_VERSION } from './annualWeather';
import { preparedWeatherPayloadJson } from './preparedWeatherModel';
import { decodePreparedWeather, encodePreparedWeather } from './preparedWeatherCodec';

const weatherPackage: WeatherDataPackage = {
  manifest: {
    schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'America/New_York',
    historicalStartYear: 2023, historicalEndYear: 2024, quality: 'limited', sourceSummary: 'fixture',
    sourceVersion: 'fixture', generatorVersion: 2, contentHash: 'fixture', complete: true,
    createdAt: '2024-01-01T00:00:00.000Z',
  },
  historicalYears: [
    { year: 2023, hours: [historicalHour('2023-09-01T04:00:00.000Z')] },
    { year: 2024, hours: [historicalHour('2024-09-01T04:00:00.000Z')] },
  ],
};

const identity = {
  packageContentHash: 'fixture', terrainBinding: 'binding', seed: 'codec-seed', year: 2024,
  timezone: 'America/New_York', generatorVersion: 2, configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION,
  cacheFormatVersion: 1 as const,
};

function historicalHour(at: string): WeatherReferenceHour {
  return {
    at, temperatureC: -3, wetBulbC: -3, humidityPct: 80, precipitationMm: 1,
    precipitationType: 'snow', snowfallCm: 1, windSpeedKph: 10, windGustKph: 15,
    windDirectionDeg: 180, cloudCoverPct: 50, visibilityKm: 20, pressureHpa: 1010, radiationWm2: 0,
    provenance: {
      fieldFlags: 7,
      fields: {
        airTemperatureC: { provider: 'daymet', quality: 'verified', sourceVersion: 'fixture', correction: 'none' },
      },
    },
  };
}

describe('prepared weather codec', () => {
  it('round-trips a generated Sep-to-Sep year with provenance and compresses it', async () => {
    const prepared = await buildPreparedWeather(weatherPackage, identity);
    const bytes = await encodePreparedWeather(prepared);
    const restored = await decodePreparedWeather(bytes, identity);
    expect(restored).toEqual(prepared);
    expect(prepared.session.plan.hours.every((hour) => hour.provenance)).toBe(true);
    expect(prepared.resolvedHours.every((hour) => hour.provenance)).toBe(true);
    expect(bytes.byteLength).toBeLessThan(Buffer.byteLength(preparedWeatherPayloadJson(prepared), 'utf8') * 0.5);
  });

  it('safely misses corrupt bytes and an identity mismatch', async () => {
    const prepared = await buildPreparedWeather(weatherPackage, identity);
    const bytes = await encodePreparedWeather(prepared);
    const corrupt = bytes.slice();
    corrupt[corrupt.length - 1] ^= 1;
    expect(await decodePreparedWeather(corrupt, identity)).toBeNull();
    expect(await decodePreparedWeather(bytes, { ...identity, seed: 'wrong' })).toBeNull();
  });
});
