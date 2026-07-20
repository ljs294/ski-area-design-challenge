import { describe, expect, it } from 'vitest';
import { deriveFourClassCover, TERRAIN_COVER_CODES } from './fourClassCover';
import type { SiteCoverGrid } from './types';
import type { LidarCanopyGrid } from './usgsTerrainCover';
import type { NaipAcquisition } from './usgsTerrainCover';

const bounds = { west: -116.62, south: 48.36, east: -116.61866, north: 48.3609 };

function originalGrid(): SiteCoverGrid {
  const data = new Array<number>(100).fill(30);
  data[8 * 10 + 1] = 80;
  return {
    bounds, width: 10, height: 10, cellSizeM: 10, data,
    complete: true, nodataCount: 0, source: 'esa-worldcover-2021-v200', vintage: '2021',
  };
}

function lidarGrid(): LidarCanopyGrid {
  const maxHeightM = new Float32Array(100);
  for (let row = 3; row < 10; row++) for (let col = 0; col < 10; col++) {
    if (col !== 5) maxHeightM[row * 10 + col] = 12;
  }
  return {
    bounds, width: 10, height: 10, cellSizeM: 10, maxHeightM,
    projectId: 'ID_NorthernID_1_2019', acquisitionYear: 2019, downloadedBytes: 2048,
  };
}

function spectralWaterNaip(): NaipAcquisition {
  return {
    bounds, width: 10, height: 10,
    red: new Uint8Array(100).fill(100), green: new Uint8Array(100).fill(200),
    blue: new Uint8Array(100).fill(80), nir: new Uint8Array(100).fill(20),
    sceneIds: [42], sceneNames: ['public-domain-naip'], acquisitionYear: 2020,
    agency: 'USDA', resolutionM: 1,
  };
}

describe('Schweitzer-style four-class terrain cover', () => {
  it('uses water, observed canopy, and local elevation in deterministic precedence order', () => {
    const heights = Array.from({ length: 100 }, (_, index) => 2200 - Math.floor(index / 10) * 20);
    const cover = deriveFourClassCover({
      bounds, original: originalGrid(), elevation: { heights, width: 10, height: 10 },
      lidar: lidarGrid(), targetCellM: 10,
    });
    const codes = new Set(cover.data);
    expect(codes).toContain(TERRAIN_COVER_CODES.forest);
    expect(codes).toContain(TERRAIN_COVER_CODES.alpine);
    expect(codes).toContain(TERRAIN_COVER_CODES.grassland);
    expect(codes).toContain(TERRAIN_COVER_CODES.water);
    expect(cover.data[8 * cover.width + 1]).toBe(TERRAIN_COVER_CODES.water);
    expect(cover.data[6 * cover.width + 2]).toBe(TERRAIN_COVER_CODES.forest);
    expect(cover.data[6 * cover.width + 5]).not.toBe(TERRAIN_COVER_CODES.forest);
    expect(cover.provenance.method).toBe('lidar-worldcover');
    expect(cover.provenance.lidar?.license).toBe('us-government-public-domain');
  });

  it('falls back to WorldCover forest seeds without inventing restricted sources', () => {
    const original = originalGrid();
    original.data[55] = 10;
    const heights = new Array<number>(100).fill(1500);
    const cover = deriveFourClassCover({
      bounds, original, elevation: { heights, width: 10, height: 10 }, targetCellM: 10,
    });
    expect(cover.data[55]).toBe(TERRAIN_COVER_CODES.forest);
    expect(cover.provenance.confidence).toBe('reduced');
    expect(cover.provenance.worldCover.license).toBe('cc-by-4.0');
  });

  it('uses NAIP to refine hydrography without inventing isolated water', () => {
    const cover = deriveFourClassCover({
      bounds, original: originalGrid(), naip: spectralWaterNaip(),
      elevation: { heights: new Array<number>(100).fill(1500), width: 10, height: 10 },
      targetCellM: 10,
    });
    expect(cover.data[8 * cover.width + 1]).toBe(TERRAIN_COVER_CODES.water);
    expect(cover.data[0]).not.toBe(TERRAIN_COVER_CODES.water);
  });
});
