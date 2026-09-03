import { createHash } from 'node:crypto';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  downloadWeatherPackageFromJob,
  type WeatherBuildJob,
} from './weatherServiceClient';
import type { WeatherChunkDescriptor, WeatherDataPackage } from './weather/weatherModel';

const bytes = new TextEncoder().encode('immutable-weather-chunk');
const checksumSha256 = createHash('sha256').update(bytes).digest('hex');

function descriptors(): WeatherChunkDescriptor[] {
  return Array.from({ length: 25 }, (_, index) => {
    const year = 2001 + index;
    return {
      id: String(year), year,
      startsAt: `${year}-01-01T08:00:00.000Z`,
      endsAt: `${year + 1}-01-01T07:00:00.000Z`,
      encoding: 'gzip', format: 'weather-hour-v2', checksumSha256,
      byteLength: bytes.byteLength, uncompressedByteLength: 16 + 96 * 8760, recordCount: 8760,
      fieldProvenance: {},
    };
  });
}

function completeJob(manifest: WeatherDataPackage['manifest']): WeatherBuildJob {
  const id = 'ready-job';
  return {
    id, status: 'succeeded',
    request: {
      schemaVersion: 1, terrainKey: 'map-a', terrainBinding: 'binding-a', latitude: 46.9, longitude: -121.4,
      bounds: { west: -121.5, south: 46.8, east: -121.3, north: 47 }, areaSizeMeters: 2000,
      timezone: 'auto', historicalStartYear: 2001, historicalEndYear: 2025, sourcePolicyVersion: 'daymet-v4r1-power-hourly-v2',
    },
    progress: { stage: 'complete', completed: 1, total: 1, message: 'Ready', updatedAt: '2026-08-25T00:00:00.000Z' },
    createdAt: '2026-08-25T00:00:00.000Z', updatedAt: '2026-08-25T00:00:00.000Z',
    result: {
      contentHash: manifest.contentHash,
      manifestUrl: `/v1/weather-package-jobs/${id}/manifest`,
      chunkUrls: descriptors().map((descriptor) => `/v1/weather-package-jobs/${id}/chunks/${descriptor.id}`),
    },
  };
}

function manifest(): WeatherDataPackage['manifest'] {
  const years = descriptors().map((descriptor) => descriptor.year);
  return {
    schemaVersion: 2, terrainKey: 'map-a', terrainBinding: 'binding-a', timezone: 'America/Los_Angeles',
    historicalStartYear: 2001, historicalEndYear: 2025, quality: 'limited', sourceSummary: 'test fixture',
    sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'a'.repeat(64), complete: true,
    immutable: true, sourcePolicyVersion: 'test-v1', createdAt: '2026-08-25T00:00:00.000Z', chunks: descriptors(),
    timezoneResolution: 'coordinate-resolved',
    coverage: { localCalendar: true, historicalStartYear: 2001, historicalEndYear: 2025, merraBoundaryEndYear: 2026 },
    sourceDetails: years.map((year) => ({
      year,
      daymet: { provider: 'fixture-daymet', version: 'fixture-v1', grid: { id: 'daymet-cell', resolutionMeters: 1000 } },
      merra2: { provider: 'fixture-merra2', version: 'fixture-v1', grid: { id: 'merra-cell', resolutionDegrees: 0.5 }, localBoundaryYear: year + 1 },
      ghcnh: { provider: 'fixture-ghcnh', version: 'fixture-v1', stations: [], applied: false, quality: 'limited' as const },
      sourceHashes: { daymet: 'b'.repeat(64), merra2: 'c'.repeat(64), ghcnh: 'd'.repeat(64) },
      flags: { precipitationTiming: false, radiationTiming: false, daymetCalendarAdjusted: false },
    })),
  };
}

afterEach(() => vi.unstubAllGlobals());

describe('weather package build client', () => {
  it('installs only the immutable manifest and binary chunks from a completed job', async () => {
    const expectedManifest = manifest();
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/manifest')) return Response.json(expectedManifest);
      return new Response(bytes, { status: 200, headers: { 'content-type': 'application/octet-stream' } });
    });
    vi.stubGlobal('fetch', fetchMock);

    const weatherPackage = await downloadWeatherPackageFromJob(completeJob(expectedManifest));

    expect(weatherPackage.manifest).toStrictEqual(expectedManifest);
    expect(weatherPackage.historicalYears).toEqual([]);
    expect(weatherPackage.chunks).toHaveLength(25);
    expect(weatherPackage.chunks?.[0]?.dataBase64).toBe(Buffer.from(bytes).toString('base64'));
    expect(fetchMock).toHaveBeenCalledTimes(26);
  });

  it('rejects a short binary chunk before it can be handed to offline storage', async () => {
    const expectedManifest = manifest();
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input);
      if (url.endsWith('/manifest')) return Response.json(expectedManifest);
      return new Response(bytes.subarray(0, -1), { status: 200 });
    });
    vi.stubGlobal('fetch', fetchMock);

    await expect(downloadWeatherPackageFromJob(completeJob(expectedManifest)))
      .rejects.toMatchObject({ code: 'CHUNK_LENGTH_MISMATCH', retryable: true });
  });
});
