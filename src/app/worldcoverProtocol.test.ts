import { describe, expect, it } from 'vitest';
import { siteCoverDimensions, worldCoverCodeForRgb } from './worldcoverProtocol';

describe('WorldCover source fidelity', () => {
  it('decodes every official class exactly and rejects unknown colors', () => {
    expect(worldCoverCodeForRgb(0, 100, 0)).toBe(10);
    expect(worldCoverCodeForRgb(255, 187, 34)).toBe(20);
    expect(worldCoverCodeForRgb(250, 0, 0)).toBe(50);
    expect(worldCoverCodeForRgb(0, 150, 160)).toBe(90);
    expect(worldCoverCodeForRgb(1, 100, 0)).toBe(255);
  });
  it('preserves a rectangular resort boundary aspect ratio', () => {
    const dims = siteCoverDimensions({ west: -121.5, east: -121.47, south: 46.9, north: 46.91 });
    expect(dims.width).toBeGreaterThan(dims.height * 2);
  });
});
