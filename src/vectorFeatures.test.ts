import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchVectorFeatures, MapContextProviderError, OVERPASS_ENDPOINTS } from './vectorFeatures';

describe('waterway ingestion', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('preserves stable identity, name, class, and parsed OSM width', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ elements: [{
        type: 'way', id: 17, tags: { waterway: 'stream', name: 'Cold Creek', width: '12 ft' },
        geometry: [{ lon: -121, lat: 46 }, { lon: -120.999, lat: 45.999 }],
      }] }),
    }));
    const result = await fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 });
    expect(result.waterLines[0]).toMatchObject({
      id: 'way/17', name: 'Cold Creek', waterClass: 'stream',
    });
    expect(result.waterLines[0].sourceWidthM).toBeCloseTo(3.6576, 4);
  });

  it('continues to model canals as rivers', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ elements: [{
        type: 'way', id: 18, tags: { waterway: 'canal' },
        geometry: [{ lon: -121, lat: 46 }, { lon: -120.999, lat: 45.999 }],
      }] }),
    }));
    const result = await fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 });
    expect(result.waterLines[0]).toMatchObject({ id: 'way/18', waterClass: 'river' });
  });

  it('normalizes paved-road width, lanes, direction, and highway metadata', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ elements: [{
        type: 'way', id: 19,
        tags: { highway: 'primary', name: 'Pass Road', surface: 'asphalt',
          width: '36 ft', lanes: '3', 'lanes:forward': '2', 'lanes:backward': '1', oneway: 'no' },
        geometry: [{ lon: -121, lat: 46 }, { lon: -120.999, lat: 45.999 }],
      }] }),
    }));
    const result = await fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 });
    expect(result.roads[0]).toMatchObject({
      id: 'way/19', name: 'Pass Road', roadClass: 'major', highway: 'primary',
      surfaceClass: 'paved', lanes: 3, lanesForward: 2, lanesBackward: 1, oneWay: false,
    });
    expect(result.roads[0].sourceWidthM).toBeCloseTo(10.9728, 4);
  });

  it('falls through the providers in primary, VK Maps, Private.coffee order', async () => {
    const request = vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 503, statusText: 'Unavailable' })
      .mockRejectedValueOnce(new TypeError('connection reset'))
      .mockResolvedValueOnce({ ok: true, json: async () => ({ elements: [] }) });
    vi.stubGlobal('fetch', request);

    await expect(fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 }))
      .resolves.toMatchObject({ roads: [], waterLines: [], waterPolygons: [] });
    expect(request.mock.calls.map(([endpoint]) => endpoint)).toEqual([...OVERPASS_ENDPOINTS]);
    expect(OVERPASS_ENDPOINTS).toEqual([
      'https://overpass-api.de/api/interpreter',
      'https://maps.mail.ru/osm/tools/overpass/api/interpreter',
      'https://overpass.private.coffee/api/interpreter',
    ]);
  });

  it('lets the browser set identity headers and supplies an origin referrer policy', async () => {
    const request = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ elements: [] }),
    });
    vi.stubGlobal('fetch', request);

    await fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 });

    const init = request.mock.calls[0]?.[1] as RequestInit;
    expect(init.headers).toEqual({ 'Content-Type': 'text/plain' });
    expect(init.referrerPolicy).toBe('origin-when-cross-origin');
  });

  it('reports every endpoint HTTP or invalid-JSON failure without losing diagnostics', async () => {
    vi.stubGlobal('fetch', vi.fn()
      .mockResolvedValueOnce({ ok: false, status: 429, statusText: 'Too Many Requests' })
      .mockResolvedValueOnce({ ok: true, json: async () => {
        throw new SyntaxError('Unexpected token');
      } })
      .mockResolvedValueOnce({ ok: false, status: 502, statusText: 'Bad Gateway' }));

    const error = await fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 })
      .catch((failure: unknown) => failure);
    expect(error).toBeInstanceOf(MapContextProviderError);
    expect((error as MapContextProviderError).failures).toMatchObject([
      { endpoint: OVERPASS_ENDPOINTS[0], kind: 'http', status: 429 },
      { endpoint: OVERPASS_ENDPOINTS[1], kind: 'invalid-response' },
      { endpoint: OVERPASS_ENDPOINTS[2], kind: 'http', status: 502 },
    ]);
  });

  it('falls back after a transport failure', async () => {
    const request = vi.fn()
      .mockRejectedValueOnce(new TypeError('connection reset'))
      .mockResolvedValueOnce({ ok: true, json: async () => ({ elements: [] }) });
    vi.stubGlobal('fetch', request);
    await expect(fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 }))
      .resolves.toMatchObject({ roads: [] });
    expect(request).toHaveBeenCalledTimes(2);
  });

  it('records a timeout for each exhausted endpoint', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('fetch', vi.fn((_endpoint: string, init?: RequestInit) =>
      new Promise((_resolve, reject) => init?.signal?.addEventListener('abort', () =>
        reject(new DOMException('aborted', 'AbortError'))))));

    const promise = fetchVectorFeatures({ west: -121, south: 45, east: -120, north: 46 });
    const rejected = expect(promise).rejects.toMatchObject({
      failures: [
        { endpoint: OVERPASS_ENDPOINTS[0], kind: 'timeout' },
        { endpoint: OVERPASS_ENDPOINTS[1], kind: 'timeout' },
        { endpoint: OVERPASS_ENDPOINTS[2], kind: 'timeout' },
      ],
    });
    await vi.advanceTimersByTimeAsync(180_000);
    await rejected;
  });

  it('cancels immediately without trying another provider', async () => {
    const controller = new AbortController();
    const request = vi.fn((_endpoint: string, init?: RequestInit) =>
      new Promise((_resolve, reject) => init?.signal?.addEventListener('abort', () =>
        reject(new DOMException('aborted', 'AbortError')))));
    vi.stubGlobal('fetch', request);

    const promise = fetchVectorFeatures(
      { west: -121, south: 45, east: -120, north: 46 },
      controller.signal,
    );
    controller.abort();

    await expect(promise).rejects.toMatchObject({ name: 'AbortError' });
    expect(request).toHaveBeenCalledOnce();
  });
});
