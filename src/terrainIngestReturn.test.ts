import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { TerrainCoverGrid, TerrainRecord } from './types';
import type { LatLonBounds } from './types/geo';

const BOUNDS: LatLonBounds = { west: -121.5, south: 46.9, east: -121.49, north: 46.91 };
const ORIGINAL_COVER = {
  bounds: BOUNDS,
  width: 2,
  height: 2,
  cellSizeM: 10,
  data: [10, 20, 30, 40],
  complete: true,
  nodataCount: 0,
  source: 'esa-worldcover-2021-v200' as const,
  vintage: '2021' as const,
};
const REFINED_COVER: TerrainCoverGrid = {
  bounds: BOUNDS,
  width: 2,
  height: 2,
  cellSizeM: 2,
  data: [1, 2, 3, 4],
  complete: true,
  nodataCount: 0,
  source: 'usgs-four-class-v1',
  vintage: '2021',
  treelineM: { north: 1800, east: 1800, south: 1800, west: 1800, site: 1800 },
  provenance: {
    processingVersion: 'four-class-v1',
    confidence: 'reduced',
    method: 'worldcover-fallback',
    attribution: ['ESA WorldCover'],
    worldCover: { vintage: '2021', license: 'cc-by-4.0' },
  },
};

const mocks = vi.hoisted(() => ({
  saved: null as TerrainRecord | null,
  saveTerrain: vi.fn(),
  loadTerrain: vi.fn(),
}));

vi.mock('./elevation', async (importOriginal) => ({
  ...await importOriginal<typeof import('./elevation')>(),
  fetchElevationGrid: vi.fn(async () => ({
    heights: [1000, 1010, 1020, 1030], bounds: BOUNDS, width: 2, height: 2,
  })),
  fetchElevationBuffer: vi.fn(async () => null),
}));
vi.mock('./fourClassCover', async (importOriginal) => ({
  ...await importOriginal<typeof import('./fourClassCover')>(),
  deriveFourClassCover: vi.fn(() => REFINED_COVER),
}));
vi.mock('./coverDisplay', async (importOriginal) => ({
  ...await importOriginal<typeof import('./coverDisplay')>(),
  deriveCoverDisplayGeometry: vi.fn(() => ({
    geometry: [1, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0],
    stats: { polygonCount: 1, ringCount: 1, vertexCount: 4, smoothingM: 6, simplifyM: 2, minFeatureM2: 16 },
  })),
}));
vi.mock('./coverAnalysis', async (importOriginal) => ({
  ...await importOriginal<typeof import('./coverAnalysis')>(),
  deriveCoverBoundarySegments: vi.fn(() => [0, 0, 1, 0, 1]),
}));
vi.mock('./marchingSquares', async (importOriginal) => ({
  ...await importOriginal<typeof import('./marchingSquares')>(),
  traceContours: vi.fn(() => [{ x1: 0, y1: 0, x2: 1, y2: 1, level: 1000 }]),
}));
vi.mock('./usgsTerrainCover', async (importOriginal) => ({
  ...await importOriginal<typeof import('./usgsTerrainCover')>(),
  fetchNaipAcquisition: vi.fn(async () => undefined),
}));
vi.mock('./vectorFeatures', async (importOriginal) => ({
  ...await importOriginal<typeof import('./vectorFeatures')>(),
  fetchVectorFeatures: vi.fn(async () => undefined),
}));
vi.mock('./terrainStorageClient', () => ({
  saveTerrain: mocks.saveTerrain,
  loadTerrain: mocks.loadTerrain,
  deleteTerrain: vi.fn(async () => ({ ok: true })),
}));

import { prepareResortPackage } from './terrainIngest';
import { validateTerrainPackage } from './terrainPackage';

describe('prepareResortPackage persisted return contract', () => {
  beforeEach(() => {
    mocks.saved = null;
    mocks.saveTerrain.mockReset().mockImplementation(async (record: TerrainRecord) => {
      mocks.saved = record;
      return { ok: true, key: record.key };
    });
    mocks.loadTerrain.mockReset().mockImplementation(async () => mocks.saved);
  });

  it('returns the verified persisted TerrainRecord without legacy hydration fields', async () => {
    const record = await prepareResortPackage(
      { bounds: [[BOUNDS.west, BOUNDS.south], [BOUNDS.east, BOUNDS.north]], widthKm: 1, heightKm: 1 },
      'Persisted Peak',
      { sampleSiteCoverGrid: vi.fn(async () => ORIGINAL_COVER) }
    );

    expect(record).toBe(mocks.saved);
    expect(validateTerrainPackage(record)).toEqual({ ok: true, errors: [] });
    expect(record.schemaVersion).toBe(6);
    expect(record).not.toHaveProperty('displayGridSize');
    expect(record).not.toHaveProperty('displayHeights');
    expect(record).not.toHaveProperty('hydratedFeatures');
    expect(record).not.toHaveProperty('widthMeters');
    expect(record).not.toHaveProperty('heightMeters');
  });
});
