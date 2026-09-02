import { describe, expect, it } from 'vitest';
import type { SnowGrid } from './types/snow';
import type { TerrainRecord } from './types/terrain';
import type { ResolvedWeatherHour } from './weather/weatherModel';
import { stepNaturalSnow } from './snowSimulation';

const terrain = {
  schemaVersion: 6, key: 'terrain', mountainName: 'Test', latitude: 45, longitude: -120,
  areaSizeMeters: 100, bounds: { west: -120, south: 45, east: -119.999, north: 45.001 },
  sampleGridSize: 2, sampleHeights: [1000, 1000, 1100, 1100], climate: { monthly: [] },
  sourceType: 'live', createdAt: '', updatedAt: '',
} satisfies TerrainRecord;

function grid(depth = 0): SnowGrid {
  return { bounds: { ...terrain.bounds }, width: 2, height: 2,
    depthM: new Float32Array(4).fill(depth), surface: new Uint8Array(4).fill(depth ? 1 : 0) };
}

function hour(at: string, temperatureC: number, precipitationMm: number): ResolvedWeatherHour {
  return {
    at, temperatureC, wetBulbC: temperatureC - 1, humidityPct: 90, precipitationMm,
    precipitationType: temperatureC < 0 && precipitationMm ? 'snow' : precipitationMm ? 'rain' : 'none',
    snowfallCm: temperatureC < 0 ? precipitationMm : 0, windSpeedKph: 10, windGustKph: 15,
    windDirectionDeg: 0, cloudCoverPct: 50, visibilityKm: 10, pressureHpa: 900, radiationWm2: 0,
    windUms: 0, windVms: 0, snowWaterEquivalentMm: precipitationMm, globalRadiationWm2: 0,
    directRadiationWm2: 0, diffuseRadiationWm2: 0, cloudTransmissionPct: 60,
    solarElevationDeg: 0, solarAzimuthDeg: 0, provenance: { fieldFlags: 0, fields: {} },
  };
}

describe('natural snow simulation', () => {
  it('accumulates solid precipitation and melts under warm rain', () => {
    const snowy = stepNaturalSnow(grid(), terrain, [hour('2026-01-01T00:00:00.000Z', -5, 10)]).grid;
    expect(Math.max(...snowy.depthM)).toBeGreaterThan(0.05);
    const melted = stepNaturalSnow(snowy, terrain, [hour('2026-01-01T01:00:00.000Z', 8, 20)]).grid;
    expect(melted.depthM[0]).toBeLessThan(snowy.depthM[0]);
  });

  it('makes a batch exactly equivalent to sequential hourly stepping', () => {
    const hours = [hour('2026-01-01T00:00:00.000Z', -5, 5), hour('2026-01-01T01:00:00.000Z', 2, 0)];
    const batch = stepNaturalSnow(grid(), terrain, hours).grid;
    const first = stepNaturalSnow(grid(), terrain, [hours[0]]).grid;
    const sequential = stepNaturalSnow(first, terrain, [hours[1]]).grid;
    expect([...batch.depthM]).toEqual([...sequential.depthM]);
    expect([...batch.surface]).toEqual([...sequential.surface]);
  });
});
