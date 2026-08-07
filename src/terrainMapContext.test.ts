import { describe, expect, it, vi } from 'vitest';
import { contourMetadataOf, coverGeometryMetadataOf, coverMetadataOf,
  manifestOf } from './terrainPackage';
import { repairTerrainMapContext } from './terrainMapContext';
import type { SiteCoverGrid, TerrainRecord } from './types';

function terrain(): TerrainRecord {
  const coverGrid: SiteCoverGrid = {
    bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
    width: 2, height: 2, cellSizeM: 10, data: [10, 10, 20, 30], complete: true,
    nodataCount: 0, source: 'esa-worldcover-2021-v200', vintage: '2021',
  };
  const contours = [0, 0, 1, 1, 1500];
  const boundaries = [0, 0, 1, 0, 10];
  let record: TerrainRecord = {
    schemaVersion: 4, key: 'context-test', mountainName: 'Test', latitude: 46.905,
    longitude: -121.495, areaSizeMeters: 2000, bounds: coverGrid.bounds,
    sampleGridSize: 2, sampleHeights: [1000, 1010, 1020, 1030],
    coverGrid, coverMetadata: coverMetadataOf(coverGrid), coverBoundarySegments: boundaries,
    coverGeometryMetadata: coverGeometryMetadataOf(boundaries), contourSegments: contours,
    contourMetadata: contourMetadataOf(contours, 2, 6.096), climate: { monthly: [] },
    sourceType: 'live', createdAt: '2026-01-01T00:00:00.000Z',
    updatedAt: '2026-01-01T00:00:00.000Z',
  };
  record = { ...record, packageManifest: manifestOf(record) };
  return record;
}

describe('terrain map-context repair', () => {
  it('persists before returning and changes only context plus the timestamp', async () => {
    const original = terrain();
    const vectors = {
      roads: [{ id: 'way/1', roadClass: 'minor' as const,
        points: [[-121.5, 46.9], [-121.49, 46.91]] as [number, number][] }],
      waterLines: [], waterPolygons: [], landCover: [], peaks: [],
    };
    const save = vi.fn(async () => ({ ok: true }));

    const result = await repairTerrainMapContext(original, {
      fetch: vi.fn(async () => vectors), save,
      now: () => '2026-02-01T00:00:00.000Z',
    });

    expect(result.ok).toBe(true);
    if (!result.ok) return;
    expect(save).toHaveBeenCalledWith({ key: original.key, vectorFeatures: vectors,
      updatedAt: '2026-02-01T00:00:00.000Z' });
    expect(result).toEqual({ ok: true, vectorFeatures: vectors,
      updatedAt: '2026-02-01T00:00:00.000Z' });
    expect(original.vectorFeatures).toBeUndefined();
  });

  it('does not publish a record when persistence fails', async () => {
    const result = await repairTerrainMapContext(terrain(), {
      fetch: vi.fn(async () => ({ roads: [], waterLines: [], waterPolygons: [],
        landCover: [], peaks: [] })),
      save: vi.fn(async () => ({ ok: false, error: 'disk full' })),
      now: () => '2026-02-01T00:00:00.000Z',
    });
    expect(result).toEqual({ ok: false, error: 'disk full' });
  });

  it('does not begin persistence when cancellation wins before the commit', async () => {
    const controller = new AbortController();
    const save = vi.fn(async () => ({ ok: true }));
    const fetch = vi.fn(async () => {
      controller.abort();
      return { roads: [], waterLines: [], waterPolygons: [], landCover: [], peaks: [] };
    });
    const result = await repairTerrainMapContext(terrain(), {
      fetch,
      save,
      now: () => '2026-02-01T00:00:00.000Z',
    }, controller.signal);

    expect(result).toMatchObject({ ok: false });
    expect(fetch).toHaveBeenCalledWith(terrain().bounds, controller.signal);
    expect(save).not.toHaveBeenCalled();
  });
});
