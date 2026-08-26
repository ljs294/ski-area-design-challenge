import { describe, expect, it, vi } from 'vitest';
import { applyTileLod } from './terrainLod';

describe('applyTileLod', () => {
  it('uses MapLibre public LOD parameters for the selected profile', () => {
    const map = {
      isStyleLoaded: () => true,
      setSourceTileLodParams: vi.fn(),
      triggerRepaint: vi.fn(),
    };
    applyTileLod(map as never, 'performance');
    expect(map.setSourceTileLodParams).toHaveBeenCalledWith(12, 1.5);
    applyTileLod(map as never, 'ultra');
    expect(map.setSourceTileLodParams).toHaveBeenLastCalledWith(2, 6);
  });

  it('waits until the style is loaded', () => {
    const map = {
      isStyleLoaded: () => false,
      setSourceTileLodParams: vi.fn(),
      triggerRepaint: vi.fn(),
    };
    applyTileLod(map as never, 'standard');
    expect(map.setSourceTileLodParams).not.toHaveBeenCalled();
  });
});
