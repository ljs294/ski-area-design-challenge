import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { SiteCoverGrid } from './types';
import type { LatLonBounds } from './types/geo';

const elevationMocks = vi.hoisted(() => ({
  fetchElevationGrid: vi.fn(),
  fetchElevationBuffer: vi.fn(),
}));

vi.mock('./elevation', async (importOriginal) => ({
  ...await importOriginal<typeof import('./elevation')>(),
  fetchElevationGrid: elevationMocks.fetchElevationGrid,
  fetchElevationBuffer: elevationMocks.fetchElevationBuffer,
}));

import { prepareResortPackage, type ResortPreparationServices } from './terrainIngest';

const REQUESTED_BOUNDS: LatLonBounds = {
  west: -121.51,
  south: 46.89,
  east: -121.48,
  north: 46.92,
};
const ELEVATION_BOUNDS: LatLonBounds = {
  west: -121.515,
  south: 46.885,
  east: -121.475,
  north: 46.925,
};
const SITE = {
  bounds: [
    [REQUESTED_BOUNDS.west, REQUESTED_BOUNDS.south],
    [REQUESTED_BOUNDS.east, REQUESTED_BOUNDS.north],
  ] as [[number, number], [number, number]],
  widthKm: 2,
  heightKm: 3,
};

function coverGrid(bounds: LatLonBounds, complete: boolean, nodataCount: number): SiteCoverGrid {
  return {
    bounds,
    width: 2,
    height: 2,
    cellSizeM: 10,
    data: complete ? [10, 20, 30, 40] : [10, 20, 30, 255],
    complete,
    nodataCount,
    source: 'esa-worldcover-2021-v200',
    vintage: '2021',
  };
}

function services(sampleSiteCoverGrid: ResortPreparationServices['sampleSiteCoverGrid']): ResortPreparationServices {
  return { sampleSiteCoverGrid };
}

describe('prepareResortPackage service boundary', () => {
  beforeEach(() => {
    elevationMocks.fetchElevationGrid.mockReset().mockResolvedValue({
      heights: [1000, 1010, 1020, 1030],
      bounds: ELEVATION_BOUNDS,
      width: 2,
      height: 2,
    });
    elevationMocks.fetchElevationBuffer.mockReset().mockResolvedValue(null);
  });

  it('forwards the elevation service true bounds to the cover service', async () => {
    const sampleSiteCoverGrid = vi.fn(async (bounds: LatLonBounds) => coverGrid(bounds, false, 1));

    await expect(prepareResortPackage(SITE, 'Test Peak', services(sampleSiteCoverGrid)))
      .rejects.toThrow('Ground-cover package is incomplete (1 missing cells).');

    expect(elevationMocks.fetchElevationGrid).toHaveBeenCalledWith(
      REQUESTED_BOUNDS,
      3000,
      expect.any(Function),
      undefined
    );
    expect(sampleSiteCoverGrid).toHaveBeenCalledWith(ELEVATION_BOUNDS, 10, undefined);
  });

  it('rejects incomplete cover before continuing resort preparation', async () => {
    const sampleSiteCoverGrid = vi.fn(async (bounds: LatLonBounds) => coverGrid(bounds, false, 17));

    await expect(prepareResortPackage(SITE, 'Test Peak', services(sampleSiteCoverGrid)))
      .rejects.toThrow('Ground-cover package is incomplete (17 missing cells).');
  });

  it('propagates cancellation after the injected service resolves', async () => {
    const controller = new AbortController();
    const sampleSiteCoverGrid = vi.fn(async (bounds: LatLonBounds) => {
      controller.abort();
      return coverGrid(bounds, true, 0);
    });

    await expect(prepareResortPackage(
      SITE,
      'Test Peak',
      services(sampleSiteCoverGrid),
      { signal: controller.signal }
    )).rejects.toMatchObject({ name: 'AbortError' });
    expect(sampleSiteCoverGrid).toHaveBeenCalledWith(ELEVATION_BOUNDS, 10, controller.signal);
  });

  it('propagates an injected cover-service failure unchanged', async () => {
    const failure = new Error('WorldCover service unavailable');
    const sampleSiteCoverGrid = vi.fn(async () => { throw failure; });

    await expect(prepareResortPackage(SITE, 'Test Peak', services(sampleSiteCoverGrid)))
      .rejects.toBe(failure);
  });
});
