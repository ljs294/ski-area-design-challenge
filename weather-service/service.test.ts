import { createServer } from 'node:http';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { PackageArtifactStore, SourceCache } from './lib/cache.mjs';
import { validateWeatherPackageRequest } from './lib/contract.mjs';
import { decodeWeatherChunk, encodeWeatherHours } from './lib/codec.mjs';
import { normalizeWeatherYear } from './lib/builder.mjs';
import { createProviderSet, normalizePowerHourly } from './lib/providers.mjs';
import { createWeatherService } from './server.mjs';

const request = {
  schemaVersion: 1, terrainKey: 'mountain-test', terrainBinding: 'terrain-bind-1234',
  latitude: 39.1911, longitude: -106.8175, timezone: 'auto',
  historicalStartYear: 1991, historicalEndYear: 2020, sourcePolicyVersion: 'daymet-v4r1-merra2-v1',
};

function sampleHour() {
  return {
    at: '1991-01-01T00:00:00.000Z', temperatureC: -6.5, wetBulbC: -7.2, humidityPct: 81,
    precipitationMm: 1.1, precipitationType: 'snow', snowfallCm: 1.2, windSpeedKph: 14,
    windGustKph: 22, windDirectionDeg: 280, cloudCoverPct: 88, visibilityKm: 4,
    pressureHpa: 998, radiationWm2: 12, windUms: 1.5, windVms: -3.2, globalRadiationWm2: 12,
    directRadiationWm2: 2, diffuseRadiationWm2: 10, cloudTransmissionPct: 18,
    snowWaterEquivalentMm: 20, solarElevationDeg: -8, solarAzimuthDeg: 98, provenance: { fieldFlags: 7 },
  };
}

const localDateFormatters = new Map<string, Intl.DateTimeFormat>();

function localDate(at: string, timezone: string): string {
  const formatter = localDateFormatters.get(timezone) ?? new Intl.DateTimeFormat('en-US', {
    timeZone: timezone, year: 'numeric', month: '2-digit', day: '2-digit',
  });
  localDateFormatters.set(timezone, formatter);
  const values = Object.fromEntries(formatter.formatToParts(new Date(at)).filter((part) => part.type !== 'literal')
    .map((part) => [part.type, part.value]));
  return `${values.year}-${values.month}-${values.day}`;
}

describe('weather service', () => {
  it('round-trips v2 binary chunks through the service codec', () => {
    const chunk = encodeWeatherHours([sampleHour()], 1991, { airTemperatureC: { provider: 'legacy', quality: 'limited', sourceVersion: 'fixture-v1', correction: 'none' } });
    expect(chunk.descriptor.format).toBe('weather-hour-v2');
    expect(chunk.descriptor.uncompressedByteLength).toBe(112);
    const [decoded] = decodeWeatherChunk(chunk.data, chunk.descriptor);
    expect(decoded.at).toBe('1991-01-01T00:00:00.000Z');
    expect(decoded.precipitationType).toBe('snow');
    expect(decoded.provenance.fieldFlags).toBe(7);
    expect(decoded.globalRadiationWm2).toBe(12);
  });

  it('writes a parseable ready manifest after optional source metadata is omitted', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-artifact-store-'));
    try {
      const chunk = encodeWeatherHours([sampleHour()], 1991, {});
      const contentHash = 'b'.repeat(64);
      const manifest = {
        schemaVersion: 2, terrainKey: request.terrainKey, terrainBinding: request.terrainBinding,
        timezone: 'America/Denver', historicalStartYear: 1991, historicalEndYear: 2020,
        quality: 'limited', sourceSummary: 'fixture', sourceVersion: 'fixture-v1', generatorVersion: 2,
        contentHash, complete: true, immutable: true, createdAt: '2026-01-01T00:00:00.000Z',
        sourcePolicyVersion: request.sourcePolicyVersion, chunks: [chunk.descriptor],
        sources: [{ provider: 'legacy', version: 'fixture-v1', quality: 'limited', citation: undefined }],
      };
      const store = new PackageArtifactStore(root);
      await store.install('c'.repeat(64), { manifest, chunks: [chunk] });
      const loaded = await store.readManifest(contentHash);
      expect(loaded).toMatchObject({ contentHash, immutable: true });
      expect(loaded.sources[0]).not.toHaveProperty('citation');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('resolves auto timezone and rejects non-US locations', () => {
    const normalized = validateWeatherPackageRequest(request);
    expect(normalized.timezone).toBe('America/Denver');
    expect(normalized.timezoneResolution).toBe('coordinate-resolved');
    expect(() => validateWeatherPackageRequest({ ...request, latitude: 51.5, longitude: -0.1 })).toThrow(/50 United States/);
  });

  it('uses a normalized fixture source cache without invoking fetch', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-source-cache-'));
    try {
      const sourceCache = new SourceCache(root);
      const providers = createProviderSet({ mode: 'fixture', sourceCache, fetchImpl: async () => { throw new Error('network must not run'); } });
      const context = { throwIfAborted() {}, signal: undefined };
      const first = await providers.daymet.getDaily(validateWeatherPackageRequest(request), 1991, context);
      const second = await providers.daymet.getDaily(validateWeatherPackageRequest(request), 1991, context);
      expect(first.cacheHit).toBe(false);
      expect(second.cacheHit).toBe(true);
      expect(second.days).toHaveLength(365);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('conserves Daymet daily temperature, precipitation, and solar constraints', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-normalization-'));
    try {
      const sourceCache = new SourceCache(root);
      const providers = createProviderSet({ mode: 'fixture', sourceCache, fetchImpl: async () => { throw new Error('network must not run'); } });
      const normalizedRequest = validateWeatherPackageRequest(request);
      const context = { throwIfAborted() {}, signal: undefined };
      const [daily, hourly, nextHourly] = await Promise.all([
        providers.daymet.getDaily(normalizedRequest, 1991, context),
        providers.merra2.getHourly(normalizedRequest, 1991, context),
        providers.merra2.getHourly(normalizedRequest, 1992, context),
      ]);
      const normalized = normalizeWeatherYear({ request: normalizedRequest, year: 1991, daily: daily.days,
        hourly: [...hourly.hours, ...nextHourly.hours], providerSet: providers });
      // Daymet daily constraints are local dates. The final local evening in
      // Colorado lives in the following UTC year, and must remain in 1991's
      // archive rather than being dropped or constrained as UTC Jan 1.
      expect(normalized.hours).toHaveLength(8760);
      expect(localDate(normalized.hours[0].at, normalizedRequest.timezone)).toBe('1991-01-01');
      expect(localDate(normalized.hours.at(-1)!.at, normalizedRequest.timezone)).toBe('1991-12-31');
      const hoursByLocalDate = new Map<string, typeof normalized.hours>();
      for (const hour of normalized.hours) {
        const date = localDate(hour.at, normalizedRequest.timezone);
        hoursByLocalDate.set(date, [...(hoursByLocalDate.get(date) ?? []), hour]);
      }
      for (const anchor of daily.days) {
        const hours = hoursByLocalDate.get(anchor.date) ?? [];
        expect(hours.length).toBeGreaterThanOrEqual(23);
        expect(hours.length).toBeLessThanOrEqual(25);
        expect(Math.min(...hours.map((hour: { temperatureC: number }) => hour.temperatureC))).toBeCloseTo(anchor.tminC, 6);
        expect(Math.max(...hours.map((hour: { temperatureC: number }) => hour.temperatureC))).toBeCloseTo(anchor.tmaxC, 6);
        expect(hours.reduce((sum: number, hour: { precipitationMm: number }) => sum + hour.precipitationMm, 0)).toBeCloseTo(anchor.precipitationMm, 6);
        expect(hours.reduce((sum: number, hour: { radiationWm2: number }) => sum + hour.radiationWm2, 0) * 3600).toBeCloseTo(anchor.shortwaveWm2 * anchor.daylightSeconds, 5);
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('uses the public NASA POWER MERRA-2 route without credentials or private services', async () => {
    const root = await mkdtemp(join(tmpdir(), 'weather-power-public-'));
    try {
      const keys = Array.from({ length: 8760 }, (_, index) => new Date(Date.UTC(1991, 0, 1, index))
        .toISOString().replace(/[-:T]/g, '').slice(0, 10));
      const values = (value: number) => Object.fromEntries(keys.map((key) => [key, value]));
      let requestedUrl = ''; let requestedOptions: RequestInit | undefined;
      const providers = createProviderSet({ mode: 'live', sourceCache: new SourceCache(root),
        fetchImpl: async (input: URL | string, options?: RequestInit) => {
          requestedUrl = String(input); requestedOptions = options;
          return Response.json({ header: { fill_value: -999 }, geometry: { coordinates: [-106.875, 39] }, properties: { parameter: {
            T2M: values(-2), RH2M: values(75), PS: values(90), U10M: values(2), V10M: values(-1),
            PRECTOTCORR: values(.2), CLOUD_AMT: values(55), ALLSKY_SFC_SW_DWN: values(.05),
          } } });
        } });
      const hourly = await providers.merra2.getHourly(validateWeatherPackageRequest(request), 1991,
        { throwIfAborted() {}, signal: undefined });
      expect(hourly.hours).toHaveLength(8760);
      expect(hourly.hours[0]).toMatchObject({ temperatureC: -2, relativeHumidityPct: 75, pressureHpa: 900,
        precipitationMm: .2, cloudCoverPct: 55, shortwaveWm2: 50 });
      expect(requestedUrl).toContain('https://power.larc.nasa.gov/api/temporal/hourly/point');
      expect(requestedUrl).toContain('time-standard=UTC');
      expect(requestedUrl).toContain('CLOUD_AMT');
      expect(requestedUrl).toContain('PRECTOTCORR');
      expect(requestedOptions?.headers).toBeUndefined();
    } finally { await rm(root, { recursive: true, force: true }); }
  });

  it('rejects incomplete NASA POWER hours instead of inventing source values', () => {
    const parameter = Object.fromEntries(['T2M', 'RH2M', 'PS', 'U10M', 'V10M', 'PRECTOTCORR', 'CLOUD_AMT']
      .map((name) => [name, { '1991010100': 1 }]));
    expect(normalizePowerHourly({ properties: { parameter } }, 1991).hours).toEqual([]);
  });

  it('reports job progress, serves validated chunks, and keeps legacy package loading compatible', async () => {
    const encoded = encodeWeatherHours([sampleHour()], 1991, {});
    const manifest = {
      schemaVersion: 2, terrainKey: request.terrainKey, terrainBinding: request.terrainBinding,
      timezone: 'America/Denver', historicalStartYear: 1991, historicalEndYear: 2020,
      quality: 'limited', sourceSummary: 'fixture', sourceVersion: 'fixture-v1', generatorVersion: 2,
      contentHash: 'a'.repeat(64), complete: true, immutable: true, createdAt: '2026-01-01T00:00:00.000Z',
      sourcePolicyVersion: request.sourcePolicyVersion, midpoint: { latitude: request.latitude, longitude: request.longitude }, chunks: [encoded.descriptor],
    };
    const artifactStore = {
      async readManifest(hash: string) { return hash === manifest.contentHash ? manifest : null; },
      async readChunk(hash: string, id: string) { return hash === manifest.contentHash && id === encoded.descriptor.id ? { descriptor: encoded.descriptor, data: encoded.data } : null; },
      async packageWithChunks(hash: string) {
        return hash === manifest.contentHash ? { manifest, chunks: [{ descriptor: encoded.descriptor, dataBase64: encoded.data.toString('base64') }], historicalYears: [] } : null;
      },
    };
    const builder = {
      async build(_request: unknown, { onProgress }: { onProgress: (progress: unknown) => void }) {
        onProgress({ stage: 'packing', completed: 1, total: 1, message: 'Packing.' });
        return { manifest, chunks: [{ descriptor: encoded.descriptor, dataBase64: encoded.data.toString('base64') }], historicalYears: [], contentHash: manifest.contentHash, cacheHit: false };
      },
    };
    const providerSet = { mode: 'fixture', daymet: { version: 'fixture-v1' }, merra2: { version: 'fixture-v1' } };
    const service = createWeatherService({ artifactStore, builder, providerSet, idFactory: () => 'job-1' });
    const server = createServer(service.handler);
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('Test weather server did not bind a TCP port.');
    const base = `http://127.0.0.1:${address.port}`;
    try {
      const create = await fetch(`${base}/v1/weather-package-jobs`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(request) });
      expect(create.status).toBe(202);
      expect((await create.json()).job.status).toBe('queued');
      let job: { job: { status: string; result?: { chunkUrls: string[] } } } | undefined;
      for (let attempt = 0; attempt < 20; attempt += 1) {
        job = await (await fetch(`${base}/v1/weather-package-jobs/job-1`)).json();
        if (job.job.status === 'succeeded') break;
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
      expect(job?.job.status).toBe('succeeded');
      expect(job?.job.result?.chunkUrls[0]).toBe('/v1/weather-package-jobs/job-1/chunks/1991');
      const manifestResponse = await fetch(`${base}/v1/weather-package-jobs/job-1/manifest`);
      expect(await manifestResponse.json()).toEqual(manifest);
      const chunkResponse = await fetch(`${base}/v1/weather-package-jobs/job-1/chunks/1991`);
      expect(chunkResponse.headers.get('x-weather-chunk-checksum-sha256')).toBe(encoded.descriptor.checksumSha256);
      expect(Buffer.from(await chunkResponse.arrayBuffer())).toEqual(encoded.data);
      const legacy = await fetch(`${base}/v1/weather-packages`, { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(request) });
      expect(legacy.status, await legacy.clone().text()).toBe(200);
      const decoded = await legacy.json() as { historicalYears: Array<{ hours: Array<{ temperatureC: number }> }> };
      expect(decoded.historicalYears[0].hours[0].temperatureC).toBe(-6.5);
    } finally {
      await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    }
  });
});
