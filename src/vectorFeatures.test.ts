import { afterEach, describe, expect, it, vi } from 'vitest';
import { fetchVectorFeatures } from './vectorFeatures';

describe('waterway ingestion', () => {
  afterEach(() => vi.unstubAllGlobals());

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
});
