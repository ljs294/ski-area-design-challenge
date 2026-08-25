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
});
