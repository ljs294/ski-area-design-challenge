import { describe, expect, it } from 'vitest';
import { decodeWeatherChunk, encodeWeatherChunk } from './weatherChunks';
import { isWeatherDataPackage, isWeatherPackageManifest, type WeatherDataPackage, type WeatherReferenceHour } from './weatherModel';
import { createWeatherSession, loadWeatherSession } from './weatherSession';

function hour(at: string): WeatherReferenceHour {
  return {
    at, temperatureC: -3.25, wetBulbC: -4.1, humidityPct: 85, precipitationMm: 1.4,
    precipitationType: 'snow', snowfallCm: 1.8, windSpeedKph: 22, windGustKph: 38,
    windDirectionDeg: 245, cloudCoverPct: 70, visibilityKm: 7, pressureHpa: 1008, radiationWm2: 110,
    windUms: 4.2, windVms: 1.1, globalRadiationWm2: 110, directRadiationWm2: 40,
    diffuseRadiationWm2: 70, cloudTransmissionPct: 45, snowWaterEquivalentMm: 25,
    solarElevationDeg: 12, solarAzimuthDeg: 165, provenance: { fieldFlags: 13 },
  };
}

describe('immutable weather-hour-v2 chunks', () => {
  it('round-trips typed hourly fields and verifies a chunk-only package before a session starts', async () => {
    const hours = Array.from({ length: 24 }, (_, index) => hour(new Date(Date.UTC(1991, 0, 1, index)).toISOString()));
    const chunk = await encodeWeatherChunk({
      id: '1991', year: 1991, encoding: 'identity',
      fieldProvenance: {
        airTemperatureC: { provider: 'daymet', quality: 'verified', sourceVersion: 'v4r1', correction: 'daymet-constrained' },
        globalHorizontalIrradianceWm2: { provider: 'merra-2', quality: 'estimated', sourceVersion: '5.12.4', correction: 'daymet-constrained' },
      },
    }, hours);
    const decoded = await decodeWeatherChunk(chunk);
    expect(decoded.hours).toHaveLength(24);
    expect(decoded.hours[0]).toMatchObject({ precipitationType: 'snow', provenance: { fieldFlags: 13 } });
    expect(decoded.hours[0].directRadiationWm2).toBeCloseTo(40, 4);

    const weatherPackage: WeatherDataPackage = {
      manifest: {
        schemaVersion: 2, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'UTC',
        historicalStartYear: 1991, historicalEndYear: 1991, quality: 'estimated', sourceSummary: 'fixture',
        sourceVersion: 'fixture-v2', generatorVersion: 2, contentHash: 'a'.repeat(64), complete: true,
        createdAt: '2026-01-01T00:00:00.000Z', sourcePolicyVersion: 'fixture-v1', immutable: true,
        midpoint: { latitude: 44.1, longitude: -71.2 }, chunks: [chunk.descriptor],
      },
      chunks: [chunk],
      historicalYears: [],
    };
    expect(isWeatherDataPackage(weatherPackage)).toBe(true);
    const session = await loadWeatherSession(weatherPackage, { seed: 'seed', startsAt: '2026-01-01T00:00:00.000Z', days: 1 });
    expect(session.historicalYears).toHaveLength(1);
    expect(session.plan.hours).toHaveLength(24);
  });

  it('rejects checksum corruption before decoding provider bytes', async () => {
    const chunk = await encodeWeatherChunk({ id: '1991', year: 1991, encoding: 'identity', fieldProvenance: {} }, [hour('1991-01-01T00:00:00.000Z')]);
    const tampered = { ...chunk, dataBase64: `${chunk.dataBase64.slice(0, -2)}AA` };
    await expect(decodeWeatherChunk(tampered)).rejects.toThrow(/checksum|length/i);
  });

  it('always checksum-decodes v2 chunks instead of trusting a populated decoded cache', async () => {
    const sourceHours = Array.from({ length: 24 }, (_, index) => hour(new Date(Date.UTC(1991, 0, 1, index)).toISOString()));
    const chunk = await encodeWeatherChunk({ id: '1991', year: 1991, encoding: 'identity', fieldProvenance: {} }, sourceHours);
    const weatherPackage: WeatherDataPackage = {
      manifest: {
        schemaVersion: 2, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'UTC',
        historicalStartYear: 1991, historicalEndYear: 1991, quality: 'estimated', sourceSummary: 'fixture',
        sourceVersion: 'fixture-v2', generatorVersion: 2, contentHash: 'b'.repeat(64), complete: true,
        createdAt: '2026-01-01T00:00:00.000Z', sourcePolicyVersion: 'fixture-v1', immutable: true,
        chunks: [chunk.descriptor],
      },
      chunks: [chunk],
      historicalYears: [{
        year: 1991,
        hours: [{ ...hour('1991-01-01T00:00:00.000Z'), temperatureC: 99 }],
      }],
    };
    expect(isWeatherDataPackage(weatherPackage)).toBe(true);
    expect(() => createWeatherSession(weatherPackage, { seed: 'seed', startsAt: '2026-01-01T00:00:00.000Z', days: 1 }))
      .toThrow(/checksum-decoded/i);
    const session = await loadWeatherSession(weatherPackage, { seed: 'seed', startsAt: '2026-01-01T00:00:00.000Z', days: 1 });
    expect(session.historicalYears[0]?.hours[0]?.temperatureC).toBeCloseTo(-3.25, 4);
  });

  it('requires unique annual chunks and exact declared v2 archive coverage', async () => {
    const chunk = await encodeWeatherChunk({ id: '1991', year: 1991, encoding: 'identity', fieldProvenance: {} }, [hour('1991-01-01T00:00:00.000Z')]);
    const incompleteManifest = {
      schemaVersion: 2 as const, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'UTC',
      historicalStartYear: 1991, historicalEndYear: 1992, quality: 'estimated' as const, sourceSummary: 'fixture',
      sourceVersion: 'fixture-v2', generatorVersion: 2, contentHash: 'c'.repeat(64), complete: true,
      createdAt: '2026-01-01T00:00:00.000Z', sourcePolicyVersion: 'fixture-v1', immutable: true,
      chunks: [chunk.descriptor],
    };
    expect(isWeatherPackageManifest(incompleteManifest)).toBe(false);
    const duplicateYear = { ...chunk.descriptor, id: '1992', year: 1991 };
    expect(isWeatherPackageManifest({ ...incompleteManifest, chunks: [chunk.descriptor, duplicateYear] })).toBe(false);

    const secondYear = { ...chunk.descriptor, id: '1992', year: 1992 };
    const validManifest = { ...incompleteManifest, chunks: [chunk.descriptor, secondYear] };
    expect(isWeatherPackageManifest(validManifest)).toBe(true);
    const duplicatePayload: WeatherDataPackage = {
      manifest: validManifest,
      chunks: [chunk, chunk],
      historicalYears: [],
    };
    expect(isWeatherDataPackage(duplicatePayload)).toBe(false);
  });

  it('uses gzip by default for durable yearly package bytes', async () => {
    const hours = Array.from({ length: 24 }, (_, index) => hour(new Date(Date.UTC(1992, 0, 1, index)).toISOString()));
    const chunk = await encodeWeatherChunk({ id: '1992', year: 1992, fieldProvenance: {} }, hours);
    expect(chunk.descriptor.encoding).toBe('gzip');
    expect(chunk.descriptor.byteLength).toBeLessThan(chunk.descriptor.uncompressedByteLength);
    expect((await decodeWeatherChunk(chunk)).hours[0].at).toBe('1992-01-01T00:00:00.000Z');
  });
});
