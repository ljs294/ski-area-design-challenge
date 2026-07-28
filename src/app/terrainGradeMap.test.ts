import { describe, expect, it, vi } from 'vitest';
import type { TerrainRecord } from '../types';
import { refreshTerrainGradeSources, setTerrainContourData } from './terrainGradeMap';

function record(): TerrainRecord {
  return {
    key: 'map-grade',
    bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
    contourSegments: [0, 0, 1, 1, 1000],
    packageManifest: { elevationChecksum: 'fnv1a32-newgrade' },
  } as TerrainRecord;
}

describe('terrain grade map refresh', () => {
  it('revisions every derived source, replaces contours, and repaints', () => {
    const setData = vi.fn();
    const sources = new Map<string, { setTiles?: ReturnType<typeof vi.fn>; setData?: ReturnType<typeof vi.fn> }>([
      ['contours', { setData }],
      ['dem', { setTiles: vi.fn() }],
      ['terrain-dem', { setTiles: vi.fn() }],
      ['slope', { setTiles: vi.fn() }],
      ['aspect', { setTiles: vi.fn() }],
    ]);
    const map = {
      getSource: (id: string) => sources.get(id),
      triggerRepaint: vi.fn(),
    };
    refreshTerrainGradeSources(map as never, record(), true);

    for (const id of ['dem', 'terrain-dem', 'slope', 'aspect']) {
      const call = sources.get(id)!.setTiles!.mock.calls[0][0][0] as string;
      expect(call).toContain('rev=fnv1a32-newgrade');
    }
    expect(setData).toHaveBeenCalledOnce();
    expect(map.triggerRepaint).toHaveBeenCalledOnce();
  });

  it('restores contour data independently for uncheck and cancel paths', () => {
    const setData = vi.fn();
    const map = { getSource: () => ({ setData }) };
    setTerrainContourData(map as never, record(), false);
    expect(setData).toHaveBeenCalledOnce();
    const data = setData.mock.calls[0][0] as GeoJSON.FeatureCollection;
    expect(data.features).toHaveLength(1);
  });
});
