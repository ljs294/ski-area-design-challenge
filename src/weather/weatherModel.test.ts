import { describe, expect, it } from 'vitest';
import { createTerrainThermalModel, sampleTemperatureField, temperatureFieldForHour } from './terrainThermal';
import { advanceWeatherPlayback, createWeatherPlayback, historicalAt, skipWeatherPlayback } from './playback';
import { generateSyntheticWeather, type WeatherDataPackage, type WeatherReferenceHour } from './weatherModel';
import { weatherTerrainBinding } from './terrainBinding';

function hour(at: string, temperatureC = -4, precipitationMm = 0): WeatherReferenceHour {
  return { at, temperatureC, wetBulbC: temperatureC - 1, humidityPct: 75, precipitationMm,
    precipitationType: precipitationMm ? 'snow' : 'none', snowfallCm: precipitationMm / 10,
    windSpeedKph: 15, windGustKph: 25, windDirectionDeg: 210, cloudCoverPct: 50,
    visibilityKm: 15, pressureHpa: 1015, radiationWm2: 50 };
}

function weatherPackage(): WeatherDataPackage {
  const hours = Array.from({ length: 48 }, (_, index) => hour(new Date(Date.UTC(1991, 0, 1, index)).toISOString(), -5 + index / 24, index < 5 ? 1 : 0));
  return { manifest: { schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'UTC',
    historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture', sourceVersion: '1',
    generatorVersion: 1, contentHash: 'fixture', complete: true, createdAt: '2026-01-01T00:00:00.000Z' },
  historicalYears: [{ year: 1991, hours }] };
}

describe('offline weather package simulation', () => {
  it('generates deterministic historical analog weather without network input', () => {
    const pkg = weatherPackage();
    expect(generateSyntheticWeather(pkg, '2026-01-01T00:00:00.000Z', 'seed', 2))
      .toEqual(generateSyntheticWeather(pkg, '2026-01-01T00:00:00.000Z', 'seed', 2));
  });

  it('keeps historical comparison and calendar skips deterministic', () => {
    const plan = generateSyntheticWeather(weatherPackage(), '2026-01-31T10:00:00.000Z', 'seed', 40);
    const start = createWeatherPlayback(plan, 1991);
    const month = skipWeatherPlayback(plan, start, 'month');
    expect(new Date(month.cursor).getUTCMonth()).toBe(1);
    expect(new Date(month.cursor).getUTCDate()).toBe(28);
    expect(historicalAt(weatherPackage(), 1991, '2026-01-01T00:00:00.000Z')?.temperatureC).toBeTypeOf('number');
    expect(advanceWeatherPlayback(plan, { ...start, running: true, speed: 64 }, 1000).cursor)
      .not.toBe(start.cursor);
  });
});

describe('terrain thermal field', () => {
  it('applies lapse rate and samples a bounded terrain field', () => {
    const record = { bounds: { west: 0, south: 0, east: 1, north: 1 }, sampleGridSize: 2,
      sampleHeights: [1000, 1200, 800, 1000] } as never;
    const model = createTerrainThermalModel(record);
    const field = temperatureFieldForHour(model, hour('2026-01-01T00:00:00.000Z', 0));
    expect(sampleTemperatureField(field, 1, 1)).toBeLessThan(0);
    expect(sampleTemperatureField(field, 2, 2)).toBeNull();
  });
});

describe('terrain binding', () => {
  it('survives elevation edits but rejects a different map location', () => {
    const base = { key: 'map', latitude: 44.1, longitude: -71.2, areaSizeMeters: 3000,
      bounds: { west: -71.3, south: 44, east: -71.1, north: 44.2 } } as const;
    expect(weatherTerrainBinding(base)).toBe(weatherTerrainBinding({ ...base }));
    expect(weatherTerrainBinding(base)).not.toBe(weatherTerrainBinding({ ...base, latitude: 44.2 }));
  });
});
