import { describe, expect, it } from 'vitest';
import { renderResortTilePixels, type RasterTerrainRecord } from './resortTileEngine';

const record: RasterTerrainRecord = {
  key: 'test',
  bounds: { west: -180, south: -85, east: 180, north: 85 },
  sampleGridSize: 2,
  sampleHeights: new Float32Array([100, 100, 100, 100]),
  coverGrid: {
    bounds: { west: -180, south: -85, east: 180, north: 85 },
    width: 2,
    height: 2,
    data: new Uint8Array([1, 1, 1, 1]),
  },
};

describe('renderResortTilePixels', () => {
  it('encodes deterministic Terrarium pixels in a worker-safe buffer', () => {
    const pixels = renderResortTilePixels(record, 'dem', 0, 0, 0);
    const index = (128 * 256 + 128) * 4;
    expect([...pixels.slice(index, index + 4)]).toEqual([128, 100, 0, 255]);
  });

  it('renders the same cover palette used by the protocol fallback', () => {
    const pixels = renderResortTilePixels(record, 'cover', 0, 0, 0);
    const index = (128 * 256 + 128) * 4;
    expect([...pixels.slice(index, index + 4)]).toEqual([82, 105, 82, 205]);
  });

  it('pads a coarse DEM tile that intersects a small source while preserving outside-source tiles', () => {
    const small: RasterTerrainRecord = {
      key: 'small', bounds: { west: -121.5, south: 46.9, east: -121.49, north: 46.91 },
      sampleGridSize: 2, sampleHeights: new Float32Array([1000, 1010, 1020, 1030]),
    };
    const intersecting = renderResortTilePixels(small, 'dem', 5, 5, 11);
    expect(intersecting[3]).toBe(255);
    expect(intersecting.filter((_, index) => index % 4 === 3 && intersecting[index] === 0)).toHaveLength(0);
    expect(intersecting[0] * 256 + intersecting[1] + intersecting[2] / 256 - 32768).toBeGreaterThan(900);

    const outside = renderResortTilePixels(small, 'dem', 5, 4, 11);
    expect(outside.filter((_, index) => index % 4 === 3 && outside[index] === 255)).toHaveLength(0);
  });

  it('pads a source intersecting only the outer half-pixel strip of a tile', () => {
    const edge: RasterTerrainRecord = { key: 'edge',
      bounds: { west: 0, south: -0.1, east: 0.1, north: 0 }, sampleGridSize: 2,
      sampleHeights: new Float32Array([250, 250, 250, 250]) };
    const pixels = renderResortTilePixels(edge, 'dem', 1, 1, 1);
    expect(pixels[3]).toBe(255);
    expect(pixels[0] * 256 + pixels[1] + pixels[2] / 256 - 32768).toBe(250);
  });
});
