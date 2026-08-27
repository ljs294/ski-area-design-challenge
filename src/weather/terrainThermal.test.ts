import { describe, expect, it } from 'vitest';
import type { TerrainRecord } from '../types/terrain';
import type { WeatherReferenceHour } from './weatherModel';
import { createTerrainThermalModel, terrainWeatherFieldForHour } from './terrainThermal';

const terrain = {
  key: 'thermal-test',
  bounds: { west: -121.5, south: 46.8, east: -121.4, north: 46.9 },
  sampleGridSize: 2,
  // A low cold-air collection at index 0 and higher terrain at index 3.
  sampleHeights: [1000, 1500, 1500, 2000],
} as TerrainRecord;

const hour: WeatherReferenceHour = {
  at: '2026-01-15T18:00:00.000Z', temperatureC: 2, wetBulbC: 1, humidityPct: 90,
  precipitationMm: 1, precipitationType: 'mixed', snowfallCm: 0.5, windSpeedKph: 8, windGustKph: 12,
  windDirectionDeg: 220, cloudCoverPct: 20, visibilityKm: 10, pressureHpa: 900, radiationWm2: 100,
};

describe('terrain weather fields', () => {
  it('resolves elevation, inversion, wet-bulb, phase, and snow-ratio fields without new provider data', () => {
    const model = createTerrainThermalModel(terrain);
    const field = terrainWeatherFieldForHour(model, hour);

    expect(field.temperatureC).toHaveLength(4);
    expect(field.wetBulbC).toHaveLength(4);
    expect(field.precipitationPhase).toHaveLength(4);
    expect(field.snowRatio).toHaveLength(4);
    // Low terrain is warmer from lapse rate but has an inversion penalty;
    // high terrain is still cold enough to resolve as snow.
    expect(field.temperatureC[0]).toBeGreaterThan(field.temperatureC[3]);
    expect(field.precipitationPhase[0]).toBe(1); // rain
    expect(field.precipitationPhase[3]).toBe(3); // snow
    expect(field.snowRatio[0]).toBe(0);
    expect(field.snowRatio[3]).toBeGreaterThanOrEqual(8);
  });
});
