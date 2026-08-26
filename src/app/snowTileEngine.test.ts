import { describe, expect, it } from 'vitest';
import { renderSnowTilePixels } from './snowTileEngine';

describe('renderSnowTilePixels', () => {
  it('renders a deterministic depth tile from a constant world grid', () => {
    const pixels = renderSnowTilePixels({
      bounds: { west: -180, east: 180, south: -85, north: 85 },
      width: 2, height: 2,
      depthM: new Float32Array([0.2, 0.2, 0.2, 0.2]),
      surface: new Uint8Array([1, 1, 1, 1]),
    }, 0, 0, 0, 'depth');
    expect(Array.from(pixels.slice((128 * 256 + 128) * 4, (128 * 256 + 128) * 4 + 4)))
      .toEqual([162, 214, 244, 205]);
  });
});
