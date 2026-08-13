import { describe, expect, it } from 'vitest';
import { SNOW_CONDITION_LEGEND, SNOW_DEPTH_BANDS, SNOW_DEPTH_LEGEND,
  SNOW_SURFACE_STYLES, snowRgba } from './snowStyle';

describe('snow map styling', () => {
  it('derives both legends from the same palettes used by the raster', () => {
    expect(SNOW_DEPTH_LEGEND).toHaveLength(5);
    expect(SNOW_CONDITION_LEGEND).toHaveLength(11);
    expect(snowRgba(0.1, 1, 'depth').slice(0, 3)).toEqual([...SNOW_DEPTH_BANDS[0].rgb]);
    expect(snowRgba(1, 11, 'conditions').slice(0, 3)).toEqual([...SNOW_SURFACE_STYLES[10].rgb]);
  });

  it('keeps bare ground transparent in both modes', () => {
    expect(snowRgba(0, 0, 'depth')).toEqual([0, 0, 0, 0]);
    expect(snowRgba(0, 0, 'conditions')).toEqual([0, 0, 0, 0]);
  });
});
