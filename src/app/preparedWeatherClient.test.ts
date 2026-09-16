import { afterEach, describe, expect, it, vi } from 'vitest';
import type { WeatherDataPackage, WeatherReferenceHour } from '../weather/weatherModel';
import { buildPreparedWeather } from '../weather/preparedWeather';
import { decodePreparedWeather, encodePreparedWeather } from '../weather/preparedWeatherCodec';
import { WEATHER_YEAR_CONFIGURATION_VERSION } from '../weather/annualWeather';
import {
  emitPreparedWeatherForTests, handlePreparedWeatherRequest, setPreparedWeatherWorkerStorageForTests,
  type PreparedWeatherWorkerResponse,
} from './preparedWeather.worker';
import {
  resolvePreparedWeather, setPreparedWeatherDesktopForTests, setPreparedWeatherWorkerFactoryForTests,
} from './preparedWeatherClient';

const identity = {
  packageContentHash: 'content', terrainBinding: 'binding', seed: 'seed', year: 2024,
  timezone: 'America/New_York', generatorVersion: 2, configurationVersion: 2,
  cacheFormatVersion: 1 as const,
};

const weatherPackage = {
  manifest: {
    schemaVersion: 1, terrainKey: 'terrain', terrainBinding: 'binding', timezone: 'America/New_York',
    historicalStartYear: 2023, historicalEndYear: 2024, quality: 'limited', sourceSummary: 'fixture',
    sourceVersion: 'fixture', generatorVersion: 2, contentHash: 'content', complete: true,
    createdAt: '2024-01-01T00:00:00.000Z',
  },
  historicalYears: [{ year: 2023, hours: [] }, { year: 2024, hours: [] }],
} as unknown as WeatherDataPackage;

function handlerHour(at: string): WeatherReferenceHour {
  return {
    at, temperatureC: -3, wetBulbC: -3, humidityPct: 80, precipitationMm: 1,
    precipitationType: 'snow', snowfallCm: 1, windSpeedKph: 10, windGustKph: 15,
    windDirectionDeg: 180, cloudCoverPct: 50, visibilityKm: 20, pressureHpa: 1010,
    radiationWm2: 0,
  };
}

const handlerPackage: WeatherDataPackage = {
  manifest: {
    schemaVersion: 1, terrainKey: 'handler-terrain', terrainBinding: 'handler-binding',
    timezone: 'America/New_York', historicalStartYear: 2023, historicalEndYear: 2024,
    quality: 'limited', sourceSummary: 'fixture', sourceVersion: 'fixture', generatorVersion: 2,
    contentHash: 'handler-content', complete: true, createdAt: '2024-01-01T00:00:00.000Z',
  },
  historicalYears: [
    { year: 2023, hours: [handlerHour('2023-09-01T04:00:00.000Z')] },
    { year: 2024, hours: [handlerHour('2024-09-01T04:00:00.000Z')] },
  ],
};

const handlerIdentity = {
  packageContentHash: 'handler-content', terrainBinding: 'handler-binding', seed: 'handler-seed', year: 2024,
  timezone: 'America/New_York', generatorVersion: 2, configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION,
  cacheFormatVersion: 1 as const,
};

afterEach(() => {
  setPreparedWeatherDesktopForTests(null);
  setPreparedWeatherWorkerFactoryForTests(null);
  setPreparedWeatherWorkerStorageForTests(null);
});

class FakeWorker {
  onmessage: ((event: MessageEvent<PreparedWeatherWorkerResponse>) => void) | null = null;
  onerror: ((event: ErrorEvent) => void) | null = null;
  terminate = vi.fn();
  postMessage = vi.fn();
}

function requestOf(worker: FakeWorker): { id: number; generation: number } {
  return worker.postMessage.mock.calls.at(-1)?.[0] as { id: number; generation: number };
}

function emitRaw(worker: FakeWorker, data: PreparedWeatherWorkerResponse): void {
  worker.onmessage?.({ data } as MessageEvent<PreparedWeatherWorkerResponse>);
}

function emit(worker: FakeWorker, type: PreparedWeatherWorkerResponse['type'], extra: Record<string, unknown> = {},
  request = requestOf(worker)): void {
  emitRaw(worker, {
    type, id: request.id, generation: request.generation, identity,
    session: {
      timezone: identity.timezone,
      plan: { startsAt: '2024-09-01T04:00:00.000Z', endsAt: '2025-09-01T03:00:00.000Z' },
    }, planTotal: 1, resolvedTotal: 1, averageAnnualSnowfallCm: 1, ...extra,
  } as PreparedWeatherWorkerResponse);
}

function emitHeader(worker: FakeWorker, total = 1, extra: Record<string, unknown> = {}): void {
  emit(worker, 'header', { planTotal: total, resolvedTotal: total, ...extra });
}

function emitEvents(worker: FakeWorker, events: unknown[] = []): void {
  emit(worker, 'events', { events });
}

function emitHour(worker: FakeWorker, type: 'plan-hours' | 'resolved-hours', index = 0, total = 1,
  hours: unknown[] = [{ at: '2024-09-01T04:00:00.000Z' }]): void {
  emit(worker, type, { index, total, hours });
}

async function pendingRequest(worker: FakeWorker, signal?: AbortSignal) {
  setPreparedWeatherWorkerFactoryForTests(() => worker);
  return resolvePreparedWeather(weatherPackage, identity, signal);
}

describe('prepared weather worker client', () => {
  it.each([
    ['content hash', { contentHash: 'wrong' }],
    ['terrain binding', { terrainBinding: 'wrong' }],
    ['timezone', { timezone: 'UTC' }],
    ['generator version', { generatorVersion: 99 }],
  ])('rejects manifest %s mismatches before starting a worker', async (_name, manifestPatch) => {
    const factory = vi.fn(() => new FakeWorker());
    setPreparedWeatherWorkerFactoryForTests(factory);
    const mismatched = { ...weatherPackage, manifest: { ...weatherPackage.manifest, ...manifestPatch } };
    await expect(resolvePreparedWeather(mismatched, identity)).rejects.toThrow();
    expect(factory).not.toHaveBeenCalled();
  });

  it('rejects an invalid configuration identity before starting a worker', async () => {
    const factory = vi.fn(() => new FakeWorker());
    setPreparedWeatherWorkerFactoryForTests(factory);
    await expect(resolvePreparedWeather(weatherPackage, { ...identity, configurationVersion: 1 })).rejects.toThrow();
    expect(factory).not.toHaveBeenCalled();
  });

  it.each([
    ['packageContentHash', 'other'], ['terrainBinding', 'other'], ['seed', 'other'], ['year', 2025],
    ['timezone', 'UTC'], ['generatorVersion', 3], ['configurationVersion', 3], ['cacheFormatVersion', 2],
  ])('rejects a response identity mismatch for %s', async (field, value) => {
    const worker = new FakeWorker();
    const pending = pendingRequest(worker);
    const responseIdentity = { ...identity, [field]: value } as typeof identity;
    emit(worker, 'header', { identity: responseIdentity });
    await expect(pending).rejects.toThrow('header');
    expect(worker.terminate).toHaveBeenCalledTimes(1);
  });

  it('deduplicates exact requests, lets one consumer abort, and reports AbortError', async () => {
    const worker = new FakeWorker();
    const firstAbort = new AbortController();
    const first = pendingRequest(worker, firstAbort.signal);
    const second = resolvePreparedWeather(weatherPackage, identity);
    expect(worker.postMessage).toHaveBeenCalledTimes(1);
    firstAbort.abort();
    await expect(first).rejects.toMatchObject({ name: 'AbortError' });
    emitHeader(worker);
    emitEvents(worker);
    emitHour(worker, 'plan-hours'); emitHour(worker, 'resolved-hours'); emit(worker, 'complete');
    await expect(second).resolves.toMatchObject({ identity });
    expect(worker.terminate).toHaveBeenCalledTimes(1);
  });

  it('terminates on final abort, clears the request, and rejects already-aborted consumers', async () => {
    const worker = new FakeWorker();
    const controller = new AbortController();
    const pending = pendingRequest(worker, controller.signal);
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    expect(worker.terminate).toHaveBeenCalledTimes(1);

    const alreadyAborted = new AbortController(); alreadyAborted.abort();
    const next = resolvePreparedWeather(weatherPackage, identity, alreadyAborted.signal);
    await expect(next).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('keeps different weather years in separate requests', async () => {
    const workers: FakeWorker[] = [];
    setPreparedWeatherWorkerFactoryForTests(() => { const worker = new FakeWorker(); workers.push(worker); return worker; });
    const firstAbort = new AbortController(); const secondAbort = new AbortController();
    const first = resolvePreparedWeather(weatherPackage, identity, firstAbort.signal);
    const second = resolvePreparedWeather(weatherPackage, { ...identity, year: 2025 }, secondAbort.signal);
    expect(workers).toHaveLength(2);
    firstAbort.abort(); secondAbort.abort();
    await expect(first).rejects.toMatchObject({ name: 'AbortError' });
    await expect(second).rejects.toMatchObject({ name: 'AbortError' });
  });

  it.each([
    ['chunks before header', (worker: FakeWorker) => emitHour(worker, 'plan-hours')],
    ['duplicate header', (worker: FakeWorker) => { emitHeader(worker); emitHeader(worker); }],
    ['missing events', (worker: FakeWorker) => { emitHeader(worker); emitHour(worker, 'plan-hours'); }],
    ['duplicate events', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitEvents(worker); }],
    ['duplicate chunk', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitHour(worker, 'plan-hours'); emitHour(worker, 'plan-hours'); }],
    ['empty chunk', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitHour(worker, 'plan-hours', 0, 1, []); }],
    ['oversize chunk', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitHour(worker, 'plan-hours', 0, 257, Array.from({ length: 257 }, () => ({}))); }],
    ['wrong non-final size', (worker: FakeWorker) => { emitHeader(worker, 512); emitEvents(worker); emitHour(worker, 'plan-hours', 0, 512, Array.from({ length: 1 }, () => ({}))); }],
    ['wrong total', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitHour(worker, 'plan-hours', 0, 2); }],
    ['out of order chunk', (worker: FakeWorker) => { emitHeader(worker, 512); emitEvents(worker); emitHour(worker, 'plan-hours', 1, 512, Array.from({ length: 256 }, () => ({}))); }],
    ['resolved before plan', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emitHour(worker, 'resolved-hours'); }],
    ['completion before exact counts', (worker: FakeWorker) => { emitHeader(worker); emitEvents(worker); emit(worker, 'complete'); }],
  ])('rejects %s', async (_name, scenario) => {
    const worker = new FakeWorker();
    const pending = pendingRequest(worker);
    scenario(worker);
    await expect(pending).rejects.toThrow();
    expect(worker.terminate).toHaveBeenCalledTimes(1);
  });

  it('rejects an active response with the wrong request generation or id', async () => {
    for (const key of ['generation', 'id'] as const) {
      const worker = new FakeWorker();
      const pending = pendingRequest(worker);
      const request = requestOf(worker);
      emitRaw(worker, {
        type: 'header', id: key === 'id' ? request.id + 1 : request.id,
        generation: key === 'generation' ? request.generation + 1 : request.generation,
      } as PreparedWeatherWorkerResponse);
      await expect(pending).rejects.toThrow('Stale');
      expect(worker.terminate).toHaveBeenCalledTimes(1);
    }
  });

  it('accepts contiguous chunks only after events and exact completion counts', async () => {
    const worker = new FakeWorker();
    const pending = pendingRequest(worker);
    emitHeader(worker, 512); emitEvents(worker);
    emitHour(worker, 'plan-hours', 0, 512, Array.from({ length: 256 }, () => ({})));
    emitHour(worker, 'plan-hours', 256, 512, Array.from({ length: 256 }, () => ({})));
    emitHour(worker, 'resolved-hours', 0, 512, Array.from({ length: 256 }, () => ({})));
    emitHour(worker, 'resolved-hours', 256, 512, Array.from({ length: 256 }, () => ({})));
    emit(worker, 'complete');
    await expect(pending).resolves.toMatchObject({ identity });
  });

  it('emits bounded hour batches', () => {
    const postMessage = vi.fn();
    const scope = { postMessage };
    const hours = Array.from({ length: 513 }, (_, index) => ({ at: String(index) }));
    emitPreparedWeatherForTests(scope as never, {
      id: 1, generation: 1, weatherPackage,
      identity, storageMode: 'desktop-relay',
    }, {
      identity,
      session: { timezone: identity.timezone, plan: { startsAt: 'x', endsAt: 'y', hours, events: [] } },
      resolvedHours: hours,
      averageAnnualSnowfallCm: null,
    } as never);
    const chunks = postMessage.mock.calls
      .map(([message]) => message as { type?: string; hours?: unknown[] })
      .filter((message) => message.type === 'plan-hours' || message.type === 'resolved-hours');
    expect(chunks.length).toBe(6);
    expect(chunks.every((chunk) => (chunk.hours?.length ?? 0) <= 256)).toBe(true);
  });

  it('starts a desktop relay request after a desktop read failure', async () => {
    const read = vi.fn().mockRejectedValue(new Error('desktop unavailable'));
    setPreparedWeatherDesktopForTests({ read, write: vi.fn().mockResolvedValue(false) });
    const worker = new FakeWorker();
    const controller = new AbortController();
    const pending = pendingRequest(worker, controller.signal);
    await vi.waitFor(() => expect(worker.postMessage).toHaveBeenCalledTimes(1));
    const request = requestOf(worker) as unknown as { storageMode: string; cacheBytes?: Uint8Array };
    expect(request.storageMode).toBe('desktop-relay');
    expect(request.cacheBytes).toBeUndefined();
    controller.abort();
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });

  it.each([
    ['false', () => Promise.resolve(false)],
    ['rejection', () => Promise.reject(new Error('write failed'))],
  ])('treats desktop write %s as nonfatal', async (_name, writeResult) => {
    const worker = new FakeWorker();
    setPreparedWeatherDesktopForTests({ read: vi.fn().mockResolvedValue(null), write: vi.fn().mockImplementation(writeResult) });
    const pending = pendingRequest(worker);
    await vi.waitFor(() => expect(worker.postMessage).toHaveBeenCalledTimes(1));
    emitHeader(worker); emitEvents(worker);
    emitHour(worker, 'plan-hours'); emitHour(worker, 'resolved-hours');
    emit(worker, 'complete', { cacheBytes: new Uint8Array([1, 2, 3]) });
    await expect(pending).resolves.toMatchObject({ identity });
  });

  it('regenerates corrupt desktop bytes in the real worker handler and transfers one cache', async () => {
    const prepared = await buildPreparedWeather(handlerPackage, handlerIdentity);
    const corrupt = await encodePreparedWeather(prepared);
    corrupt[corrupt.length - 1] ^= 1;
    const postMessage = vi.fn();
    await handlePreparedWeatherRequest({ postMessage } as never, {
      id: 7, generation: 3, weatherPackage: handlerPackage, identity: handlerIdentity,
      cacheBytes: corrupt, storageMode: 'desktop-relay',
    });
    const messages = postMessage.mock.calls.map(([message]) => message as {
      type: string; hours?: unknown[]; cacheBytes?: Uint8Array;
    });
    expect(messages[0]?.type).toBe('header');
    expect(messages.filter(({ type }) => type === 'events')).toHaveLength(1);
    const chunks = messages.filter(({ type }) => type === 'plan-hours' || type === 'resolved-hours');
    expect(chunks.length).toBeGreaterThan(2);
    expect(chunks.every(({ hours }) => !!hours && hours.length > 0 && hours.length <= 256)).toBe(true);
    const completeIndex = messages.findIndex(({ type }) => type === 'complete');
    expect(completeIndex).toBeGreaterThan(0);
    expect(messages[completeIndex]?.cacheBytes).toBeInstanceOf(Uint8Array);
    const transfer = postMessage.mock.calls[completeIndex]?.[1] as Transferable[] | undefined;
    expect(transfer?.length).toBe(1);
  });

  it('browser cache misses encode once and persist decodable opaque bytes', async () => {
    const expected = await buildPreparedWeather(handlerPackage, handlerIdentity);
    const reads = vi.fn().mockResolvedValue(null);
    const writes: Uint8Array[] = [];
    setPreparedWeatherWorkerStorageForTests({
      read: reads,
      write: vi.fn(async (_identity, bytes) => { writes.push(bytes.slice()); return true; }),
    });
    await handlePreparedWeatherRequest({ postMessage: vi.fn() } as never, {
      id: 8, generation: 4, weatherPackage: handlerPackage, identity: handlerIdentity,
      storageMode: 'browser-indexeddb',
    });
    expect(reads).toHaveBeenCalledTimes(1);
    expect(writes).toHaveLength(1);
    expect(await decodePreparedWeather(writes[0], handlerIdentity)).toEqual(expected);
  });
});

