import { describe, expect, it, vi } from 'vitest';
import { refreshSnowSource } from './snowProtocol';

describe('refreshSnowSource', () => {
  it('updates the raster tiles and resampling without rebuilding the style source', () => {
    const setTiles = vi.fn(), setPaintProperty = vi.fn();
    const map = { getSource: vi.fn(() => ({ setTiles })), getLayer: vi.fn(() => ({})), setPaintProperty };

    refreshSnowSource(map as never, 'conditions');

    expect(setTiles).toHaveBeenCalledWith([expect.stringContaining('mode=conditions')]);
    expect(setPaintProperty).toHaveBeenCalledWith('snow', 'raster-resampling', 'nearest');
  });
});
