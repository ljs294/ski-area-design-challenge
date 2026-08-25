import { describe, expect, it } from 'vitest';
import { displayImageryDimensions } from './displayImagery';

describe('displayImageryDimensions', () => {
  it('bounds the longest side while preserving aspect ratio', () => {
    expect(displayImageryDimensions(4000, 3000, 2048)).toEqual({ width: 2048, height: 1536 });
    expect(displayImageryDimensions(2000, 3000, 2048)).toEqual({ width: 1365, height: 2048 });
  });

  it('does not upscale imagery', () => {
    expect(displayImageryDimensions(1200, 900, 2048)).toEqual({ width: 1200, height: 900 });
  });
});
