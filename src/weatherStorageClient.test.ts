import { createHash } from 'crypto';
import { describe, expect, it } from 'vitest';
import {
  isWeatherChunkDescriptor,
  isWeatherDataPackage,
  isWeatherPackageManifest,
  type WeatherDataPackage,
  type WeatherReferenceHour,
} from './weather/weatherModel';
import {
  createWeatherPackageStorageInstall,
  decodeWeatherPackageStorageInstall,
  validateWeatherPackageStorageInstall,
} from './weatherStorageClient';

function hour(at: string): WeatherReferenceHour {
  return {
    at,
    temperatureC: -5,
    wetBulbC: -6,
    humidityPct: 85,
    precipitationMm: 1,
    precipitationType: 'snow',
    snowfallCm: 1,
    windSpeedKph: 12,
    windGustKph: 18,
    windDirectionDeg: 220,
    cloudCoverPct: 65,
    visibilityKm: 10,
    pressureHpa: 1014,
    radiationWm2: 35,
  };
}

function legacyPackage(): WeatherDataPackage {
  return {
    manifest: {
      schemaVersion: 1,
      terrainKey: 'terrain-a',
      terrainBinding: 'binding-a',
      timezone: 'America/Denver',
      historicalStartYear: 1991,
      historicalEndYear: 2020,
      quality: 'estimated',
      sourceSummary: 'fixture',
      sourceVersion: 'fixture-v1',
      generatorVersion: 1,
      contentHash: 'logical-weather-content-hash',
      complete: true,
      createdAt: '2026-08-25T00:00:00.000Z',
    },
    historicalYears: [
      { year: 1991, hours: [hour('1991-01-01T00:00:00.000Z')] },
      { year: 1992, hours: [hour('1992-01-01T00:00:00.000Z')] },
    ],
  };
}

describe('content-addressed weather storage artifacts', () => {
  it('round-trips legacy packages through checksummed yearly chunks', async () => {
    const source = legacyPackage();
    const install = await createWeatherPackageStorageInstall(source);

    expect(install.manifest.storageSchemaVersion).toBe(2);
    expect(install.manifest.chunks.map((chunk) => chunk.key)).toEqual([
      'package-shell', 'historical-year-1991', 'historical-year-1992',
    ]);
    expect(install.manifest.chunks.every((chunk) => chunk.encoding === 'gzip-json')).toBe(true);
    await expect(validateWeatherPackageStorageInstall(install)).resolves.toBeUndefined();
    await expect(decodeWeatherPackageStorageInstall(install)).resolves.toEqual(source);
  });

  it('rejects a corrupt chunk before it can become active', async () => {
    const install = await createWeatherPackageStorageInstall(legacyPackage());
    const chunks = install.chunks.map((chunk, index) => ({
      ...chunk,
      data: index === 0 ? new Uint8Array(chunk.data.map((byte, offset) => offset === 0 ? byte ^ 0xff : byte)) : chunk.data,
    }));

    await expect(validateWeatherPackageStorageInstall({ ...install, chunks }))
      .rejects.toThrow('checksum validation');
  });

  it('persists provider chunks byte-for-byte instead of wrapping them in JSON', async () => {
    const bytes = new TextEncoder().encode('native-weather-hour-chunk');
    const checksumSha256 = createHash('sha256').update(bytes).digest('hex');
    const descriptors = Array.from({ length: 30 }, (_, index) => {
      const year = 1991 + index;
      return {
        id: String(year),
        year,
        startsAt: `${year}-01-01T00:00:00.000Z`,
        endsAt: `${year}-12-31T23:00:00.000Z`,
        encoding: 'gzip' as const,
        format: 'weather-hour-v2' as const,
        checksumSha256,
        byteLength: bytes.byteLength,
        uncompressedByteLength: 16 + 96 * 8760,
        recordCount: 8760,
        fieldProvenance: {
          airTemperatureC: {
            provider: 'daymet' as const, quality: 'verified' as const, sourceVersion: 'fixture-v4r1', correction: 'daymet-constrained' as const,
          },
        },
      };
    });
    const source = {
      ...legacyPackage(),
      manifest: {
        ...legacyPackage().manifest,
        schemaVersion: 2,
        generatorVersion: 2,
        contentHash: 'a'.repeat(64),
        sourcePolicyVersion: 'fixture-v2',
        immutable: true,
        chunks: descriptors,
      },
      historicalYears: [],
      chunks: descriptors.map((descriptor) => ({
        descriptor,
        dataBase64: Buffer.from(bytes).toString('base64'),
      })),
    } as unknown as WeatherDataPackage;

    expect(isWeatherChunkDescriptor(descriptors[0])).toBe(true);
    expect(isWeatherPackageManifest(source.manifest)).toBe(true);
    expect(isWeatherDataPackage(source)).toBe(true);
    const install = await createWeatherPackageStorageInstall(source);
    expect(install.manifest.payloadFormat).toBe('weather-package-chunks-v1');
    expect(install.manifest.chunks[0]).toMatchObject({
      key: '1991', encoding: 'binary', checksum: checksumSha256, byteLength: bytes.byteLength,
    });
    expect(install.chunks[0]?.data).toEqual(bytes);

    const decoded = await decodeWeatherPackageStorageInstall(install) as unknown as {
      chunks: Array<{ dataBase64: string }>;
    };
    expect(Buffer.from(decoded.chunks[0]?.dataBase64 ?? '', 'base64')).toEqual(Buffer.from(bytes));
  });
});
